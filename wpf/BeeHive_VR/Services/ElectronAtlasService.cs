using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace BeeHiveVR.Services;

/// <summary>
/// Startet die Electron-Atlas-Exe (<c>BeeHive_VR_Atlas.exe</c>) beim
/// iRacing-Connect automatisch — bequem als „kein extra Window öffnen, App
/// fährt sich selbst hoch". Beim WPF-OnExit wird der Prozess wieder beendet,
/// damit der nächste WPF-Start nicht auf einen Zombie trifft.
///
/// Pfad-Auflösung (analog zu <see cref="DashiesPreviewService"/>):
///   1. Sibling neben der WPF-Exe (Installer-Layout)
///   2. Walk-up zu <c>app\out\BeeHive_VR_Atlas-win32-x64\BeeHive_VR_Atlas.exe</c>
///      (Forge-Default-Output)
///
/// Single-Process-Garantie hält das Electron-eigene Single-Instance-Lock
/// (<c>app.requestSingleInstanceLock</c> + Named Mutex). <see cref="Start"/>
/// ist trotzdem idempotent — wenn unser eigener Child-Prozess noch lebt,
/// no-op.
/// </summary>
public sealed class ElectronAtlasService
{
    private static ElectronAtlasService? _instance;
    public static ElectronAtlasService Instance => _instance ??= new ElectronAtlasService();

    private ElectronAtlasService() { }

    private Process? _process;
    private readonly object _gate = new();

    /// <summary>
    /// Startet die Atlas-Exe, falls noch nicht laufend. Idempotent + tolerant
    /// gegenüber fehlendem Pfad (loggt Warnung, returnt false statt zu werfen).
    /// </summary>
    public bool Start()
    {
        lock (_gate)
        {
            if (_process != null && !_process.HasExited) return true;

            var exePath = ResolveExePath();
            if (exePath == null)
            {
                Logger.Warn("ElectronAtlasService.Start: BeeHive_VR_Atlas.exe not found");
                return false;
            }

            // 18.7.2026: VOR jedem Start sweepen, nicht nur beim WPF-Start. Grund
            // (Trace-Beweis): stirbt der Atlas-HAUPTprozess, überleben seine Electron-
            // Kindprozesse ungetrackt weiter — Stop()/Kill(tree) erreicht sie dann nicht
            // mehr. Bei jedem Neustart sammelten sich so Zombies an, die weiter GPU-
            // Arbeit machten (eigene Window-Capture + 3-s-getSources-Bursts) → Ruckeln
            // bis hin zum adapter-weiten Freeze; eine Zombie-Instanz publizierte sogar
            // per Heartbeat ihre ALTE FrameSlot und überschrieb die des neuen Atlas.
            // Wir kommen hier nur hin, wenn unser getrackter Prozess tot ist → alles was
            // jetzt noch läuft, ist per Definition eine Waise.
            SweepOrphans();

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = exePath,
                    // --parent-pid: stirbt die WPF unsauber (ohne OnExit → Stop() lief
                    // nie), beendet sich der Atlas selbst (Gürtel+Hosenträger zum
                    // Startup-Orphan-Sweep). Muster wie CaptureHostService.
                    Arguments = $"--parent-pid={Environment.ProcessId}",
                    UseShellExecute = false,
                    CreateNoWindow = false,
                    WorkingDirectory = Path.GetDirectoryName(exePath) ?? "",
                };
                _process = Process.Start(psi);
                if (_process == null)
                {
                    Logger.Warn("ElectronAtlasService.Start: Process.Start returned null");
                    return false;
                }
                Logger.Info($"ElectronAtlasService: started, pid={_process.Id}, exe={exePath}");
                ChildProcessJob.Assign(_process); // stirbt mit der WPF (auch bei hartem Kill)
                return true;
            }
            catch (Exception ex)
            {
                Logger.Warn($"ElectronAtlasService.Start: Process.Start failed: {ex.Message}");
                _process = null;
                return false;
            }
        }
    }

    /// <summary>
    /// Killt verwaiste <c>BeeHive_VR_Atlas</c>-Prozesse aus einem unsauber
    /// beendeten Vorlauf (WPF-Crash / Task-Manager / Shutdown → OnExit lief nicht
    /// → <see cref="Stop"/> nie). NUR beim First-Instance-Start aufrufen (der
    /// Single-Instance-Mutex garantiert dann, dass jeder vorgefundene Atlas stale
    /// ist — unser eigener startet erst beim iRacing-Connect). Ohne diesen Sweep
    /// bleiben Waisen „sticky": der nächste Start spawnt einen Atlas, der am
    /// Electron-Single-Instance-Lock sofort wieder aussteigt, während die Waise
    /// untracked weiterläuft. <see cref="Process.GetProcessesByName(string)"/>
    /// erfasst alle Electron-Kindprozesse (gleicher Name); Kill mit
    /// <c>entireProcessTree</c> räumt Reste weg.
    /// </summary>
    public void SweepOrphans()
    {
        Process[] found;
        try { found = Process.GetProcessesByName("BeeHive_VR_Atlas"); }
        catch (Exception ex)
        {
            Logger.Warn($"ElectronAtlasService.SweepOrphans: enum failed: {ex.Message}");
            return;
        }

        foreach (var p in found)
        {
            try
            {
                p.Kill(entireProcessTree: true);
                Logger.Info($"ElectronAtlasService.SweepOrphans: killed stale atlas pid={p.Id}");
            }
            catch (Exception ex)
            {
                Logger.Warn($"ElectronAtlasService.SweepOrphans: kill pid={p.Id} failed: {ex.Message}");
            }
            finally { p.Dispose(); }
        }
    }

    /// <summary>Beendet den Atlas-Prozess. Idempotent.</summary>
    public void Stop()
    {
        lock (_gate)
        {
            if (_process == null) return;
            try
            {
                if (!_process.HasExited)
                    _process.Kill(entireProcessTree: true);
                _process.Dispose();
            }
            catch (Exception ex) { Logger.Warn($"ElectronAtlasService.Stop: {ex.Message}"); }
            _process = null;
        }
    }

    private static string? ResolveExePath()
    {
        var asm = Assembly.GetEntryAssembly()?.Location;
        var asmDir = string.IsNullOrEmpty(asm) ? null : Path.GetDirectoryName(asm);
        if (asmDir == null) return null;

        // 1. Sibling neben der WPF-Exe (flaches Layout)
        var sibling = Path.Combine(asmDir, "BeeHive_VR_Atlas.exe");
        if (File.Exists(sibling)) return sibling;

        // 2. Install-Unterordner atlas\ (Setup-Layout %LOCALAPPDATA%\Programs\BeeHive_VR)
        var installed = Path.Combine(asmDir, "atlas", "BeeHive_VR_Atlas.exe");
        if (File.Exists(installed)) return installed;

        // 3. Walk-up zum Repo-Root, dann ins Forge-Default-Output
        var dir = asmDir;
        for (int i = 0; i < 8 && dir != null; i++)
        {
            var cand = Path.Combine(dir, "app", "out",
                "BeeHive_VR_Atlas-win32-x64", "BeeHive_VR_Atlas.exe");
            if (File.Exists(cand)) return cand;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }
}
