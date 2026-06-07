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

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = exePath,
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

        // 1. Sibling neben der WPF-Exe (Installer-Layout)
        var sibling = Path.Combine(asmDir, "BeeHive_VR_Atlas.exe");
        if (File.Exists(sibling)) return sibling;

        // 2. Walk-up zum Repo-Root, dann ins Forge-Default-Output
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
