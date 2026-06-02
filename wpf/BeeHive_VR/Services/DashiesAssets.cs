using System.IO;
using System.Reflection;

namespace BeeHiveVR.Services;

/// <summary>
/// Findet den eingebetteten Vite-Build der Dashies zur Laufzeit. Wird vom
/// IrdashiesAdapterService (statische Files) und vom IrdashiesConfigStore
/// (Fallback dashboard.json) genutzt.
///
/// Such-Reihenfolge:
///   1. Neben der WPF-Exe: <c>&lt;asmDir&gt;\WebRoot\dashies-dist\</c>
///      (Standard nach <c>dotnet build</c> + Installer-Layout)
///   2. Walk-up zum Repo-Root: sucht
///      <c>&lt;repo&gt;\wpf\BeeHive_VR\WebRoot\dashies-dist\</c>
///      (Dev-Fallback, falls Asset-Copy mal nicht griff)
///
/// Hard-Codes auf <c>D:\VBdev\irdashies\</c> sind raus — das Repo ist
/// reine Build-Werkstatt, kein Laufzeit-Pfad.
/// </summary>
internal static class DashiesAssets
{
    private const string SubFolder = "dashies-dist";

    /// <summary>Voller Pfad zum dashies-dist-Folder. Leerer String wenn nichts gefunden.</summary>
    public static string ResolveWebRoot()
    {
        var asm = Assembly.GetEntryAssembly()?.Location;
        var asmDir = string.IsNullOrEmpty(asm) ? null : Path.GetDirectoryName(asm);
        if (asmDir == null) return "";

        // 1. Sibling (Build-Output + Installer)
        var sibling = Path.Combine(asmDir, "WebRoot", SubFolder);
        if (Directory.Exists(sibling)) return sibling;

        // 2. Walk-up (Dev: Repo-Root → wpf\BeeHive_VR\WebRoot\dashies-dist)
        var dir = asmDir;
        for (int i = 0; i < 8 && dir != null; i++)
        {
            var cand = Path.Combine(dir, "wpf", "BeeHive_VR", "WebRoot", SubFolder);
            if (Directory.Exists(cand)) return cand;
            dir = Path.GetDirectoryName(dir);
        }
        return "";
    }

    /// <summary>
    /// Voller Pfad zur eingebetteten <c>dashboard.json</c> (Fallback-Baseline
    /// für IrdashiesConfigStore wenn weder eigene Config noch ird-Migration greifen).
    /// Leerer String wenn der Asset-Folder nicht gefunden wurde.
    /// </summary>
    public static string ResolveFallbackDashboardPath()
    {
        var root = ResolveWebRoot();
        return string.IsNullOrEmpty(root) ? "" : Path.Combine(root, "dashboard.json");
    }
}
