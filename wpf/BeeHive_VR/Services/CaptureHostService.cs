using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace BeeHiveVR.Services;

/// <summary>
/// Startet den capture-host (<c>capture-host.exe</c>) beim iRacing-Connect —
/// D1 (8.7.2026): der Helper macht WGC-Capture + Chroma-Compose in einem eigenen
/// Prozess (eigene GPU-Allokationen), der OpenXR-Layer kopiert nur noch fertige
/// Shared-Texturen. Lebenszyklus analog <see cref="ElectronAtlasService"/>:
/// Start bei Connect (idempotent), Stop in OnExit. Zusätzlich bekommt der Helper
/// unsere PID als <c>--parent-pid</c> — stirbt die WPF, beendet er sich selbst
/// (Gürtel + Hosenträger neben seinem Atlas-tot-Self-Exit).
///
/// Pfad-Auflösung:
///   1. Sibling neben der WPF-Exe (Installer-Layout)
///   2. Walk-up zu <c>engine\bin\x64\Release\capture-host.exe</c> (Dev-Layout)
/// </summary>
public sealed class CaptureHostService
{
    private static CaptureHostService? _instance;
    public static CaptureHostService Instance => _instance ??= new CaptureHostService();

    private CaptureHostService() { }

    private Process? _process;
    private readonly object _gate = new();

    /// <summary>Startet capture-host, falls noch nicht laufend. Idempotent.</summary>
    public bool Start()
    {
        lock (_gate)
        {
            if (_process != null && !_process.HasExited) return true;

            var exePath = ResolveExePath();
            if (exePath == null)
            {
                Logger.Warn("CaptureHostService.Start: capture-host.exe not found");
                return false;
            }

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = $"--parent-pid={Environment.ProcessId}",
                    UseShellExecute = false,
                    // Console-Subsystem-Exe — ohne CreateNoWindow gäbe es ein
                    // sichtbares Konsolenfenster bei jedem iRacing-Connect.
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(exePath) ?? "",
                };
                _process = Process.Start(psi);
                if (_process == null)
                {
                    Logger.Warn("CaptureHostService.Start: Process.Start returned null");
                    return false;
                }
                Logger.Info($"CaptureHostService: started, pid={_process.Id}, exe={exePath}");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Warn($"CaptureHostService.Start: Process.Start failed: {ex.Message}");
                _process = null;
                return false;
            }
        }
    }

    /// <summary>Beendet den capture-host. Idempotent.</summary>
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
            catch (Exception ex) { Logger.Warn($"CaptureHostService.Stop: {ex.Message}"); }
            _process = null;
        }
    }

    private static string? ResolveExePath()
    {
        var asm = Assembly.GetEntryAssembly()?.Location;
        var asmDir = string.IsNullOrEmpty(asm) ? null : Path.GetDirectoryName(asm);
        if (asmDir == null) return null;

        // 1. Sibling neben der WPF-Exe (Installer-Layout)
        var sibling = Path.Combine(asmDir, "capture-host.exe");
        if (File.Exists(sibling)) return sibling;

        // 2. Walk-up zum Repo-Root, dann ins Engine-Build-Output
        var dir = asmDir;
        for (int i = 0; i < 8 && dir != null; i++)
        {
            var cand = Path.Combine(dir, "engine", "bin", "x64", "Release", "capture-host.exe");
            if (File.Exists(cand)) return cand;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }
}
