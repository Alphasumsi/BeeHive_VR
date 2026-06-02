using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using BeeHiveVR.Models;

namespace BeeHiveVR.Services;

/// <summary>
/// Startet/stoppt browser-host.exe-Child-Prozesse passend zur aktiven Layout-Konfig.
///
/// Pro Browser-Source läuft ein eigener browser-host.exe-Prozess mit:
///   --url=&lt;source.Target&gt;  --title=&lt;source.Id&gt;  --cloak
///
/// Der Layer matcht das Browser-Fenster über source.Id (statt der URL),
/// damit es deterministisch und eindeutig zuordenbar ist. Beim WPF-Exit
/// werden alle gespawnten Children sauber beendet.
/// </summary>
public sealed class BrowserHostManager
{
    private static BrowserHostManager? _instance;
    public static BrowserHostManager Instance => _instance ??= new BrowserHostManager();

    /// <summary>Aktive Children, Key = SourceModel.Id.</summary>
    private readonly Dictionary<string, Tracked> _children = new();

    private sealed class Tracked
    {
        public Process Process = null!;
        public string Url = "";
        public int Width;
        public int Height;
    }

    private BrowserHostManager() { }

    /// <summary>
    /// Synchronisiert die laufenden Browser-Host-Prozesse mit der gewünschten Liste.
    /// - Source mit Id X, läuft schon, gleiche URL → nichts tun
    /// - Source mit Id X, läuft schon, andere URL → killen + neu starten
    /// - Source mit Id X, läuft nicht → starten
    /// - Laufender Prozess ohne Source → killen
    /// </summary>
    public void Apply(IEnumerable<SourceModel> browserSources)
    {
        var desired = browserSources.ToDictionary(s => s.Id, s => s);

        // Removal: was nicht mehr in desired
        foreach (var id in _children.Keys.ToList())
        {
            if (!desired.ContainsKey(id))
            {
                Kill(id);
            }
        }

        // Add/Restart
        foreach (var (id, src) in desired)
        {
            if (_children.TryGetValue(id, out var existing))
            {
                if (existing.Url == src.Target &&
                    existing.Width == src.PixelWidth &&
                    existing.Height == src.PixelHeight &&
                    !existing.Process.HasExited)
                {
                    continue; // unverändert
                }
                Kill(id);
            }
            Spawn(src);
        }
    }

    public void StopAll()
    {
        foreach (var id in _children.Keys.ToList())
        {
            Kill(id);
        }
    }

    /// <summary>Aktuell laufende Child-Ids (Snapshot).</summary>
    public IReadOnlyList<string> TrackedIds => _children.Keys.ToList();

    /// <summary>
    /// Erzwingt einen Respawn der angegebenen, aktuell laufenden Children
    /// (Kill + Neustart aus den getrackten Url/Size). Use-Case: dashboard.json
    /// geändert → browser-host neu laden, ohne die Source-Konfig anzufassen.
    /// Nicht getrackte Ids werden ignoriert.
    /// </summary>
    public void Restart(IEnumerable<string> ids)
    {
        foreach (var id in ids.ToList())
        {
            if (!_children.TryGetValue(id, out var t)) continue;
            var snap = new SourceModel
            {
                Id = id,
                Name = id,
                Type = SourceType.Browser,
                Target = t.Url,
                PixelWidth = t.Width,
                PixelHeight = t.Height,
            };
            Kill(id);
            Spawn(snap);
        }
    }

    // ---------------------------------------------------------------- internals

    private void Spawn(SourceModel src)
    {
        var exe = ResolveBrowserHostPath();
        if (exe == null)
        {
            Logger.Warn($"BrowserHostManager: cannot spawn for \"{src.Name}\" — browser-host.exe not found. " +
                        "Set Settings.BrowserHostExecutable.");
            return;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(exe) ?? "",
            };
            psi.ArgumentList.Add($"--url={src.Target}");
            psi.ArgumentList.Add($"--title={src.Id}");
            if (src.PixelWidth > 0) psi.ArgumentList.Add($"--width={src.PixelWidth}");
            if (src.PixelHeight > 0) psi.ArgumentList.Add($"--height={src.PixelHeight}");
            psi.ArgumentList.Add("--cloak");

            var proc = Process.Start(psi);
            if (proc == null)
            {
                Logger.Warn($"BrowserHostManager: Process.Start returned null for \"{src.Name}\"");
                return;
            }
            _children[src.Id] = new Tracked
            {
                Process = proc,
                Url = src.Target,
                Width = src.PixelWidth,
                Height = src.PixelHeight,
            };
            Logger.Info($"BrowserHostManager: spawned browser-host pid={proc.Id} id={src.Id} " +
                        $"url=\"{src.Target}\" size={src.PixelWidth}x{src.PixelHeight}");
        }
        catch (Exception ex)
        {
            Logger.Error($"BrowserHostManager: failed to spawn \"{src.Name}\": {ex.Message}");
        }
    }

    private void Kill(string id)
    {
        if (!_children.TryGetValue(id, out var t)) return;
        _children.Remove(id);
        try
        {
            if (!t.Process.HasExited)
            {
                t.Process.Kill(entireProcessTree: true);
                t.Process.WaitForExit(2000);
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"BrowserHostManager: kill id={id} failed: {ex.Message}");
        }
        finally
        {
            try { t.Process.Dispose(); } catch { }
        }
        Logger.Info($"BrowserHostManager: killed browser-host id={id}");
    }

    public static string? ResolveBrowserHostPath()
    {
        var fromSettings = SettingsStore.Current.BrowserHostExecutable;
        if (!string.IsNullOrWhiteSpace(fromSettings) && File.Exists(fromSettings))
        {
            return fromSettings;
        }

        // Auto-Detect: bekannte Dev-Build-Pfade
        string[] candidates =
        {
            @"D:\VBdev\iracing-vr-overlay\engine\bin\x64\Release\browser-host.exe",
            Path.Combine(AppContext.BaseDirectory, "browser-host.exe"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
                         "engine", "bin", "x64", "Release", "browser-host.exe"),
        };
        foreach (var c in candidates)
        {
            var full = Path.GetFullPath(c);
            if (File.Exists(full)) return full;
        }
        return null;
    }
}
