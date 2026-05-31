using System.Reflection;

namespace HoneyOverlays.Services;

/// <summary>
/// Liefert Versions-/Build-Info aus dem Assembly. Single Source of Truth
/// ist die &lt;Version&gt;-Property in der csproj.
/// </summary>
public static class AppInfo
{
    /// <summary>Versions-String, z.B. "0.4.0" — ohne führendes "v".</summary>
    public static string Version
    {
        get
        {
            var asm = Assembly.GetExecutingAssembly();
            var v = asm.GetName().Version;
            if (v == null) return "0.0.0";
            // Immer 3-stellig (Major.Minor.Build) — nur Revision weglassen wenn 0
            if (v.Revision == 0)
                return $"{v.Major}.{v.Minor}.{v.Build}";
            return v.ToString();
        }
    }

    /// <summary>Versions-String mit "v" Prefix, z.B. "v0.4.0" — für Anzeige.</summary>
    public static string VersionDisplay => $"v{Version}";

    /// <summary>App-Name aus dem Assembly.</summary>
    public static string AppName =>
        Assembly.GetExecutingAssembly().GetName().Name ?? "HoneyOverlays";

    /// <summary>Build-Datum aus dem letzten Schreibzugriff der EXE-Datei.</summary>
    public static string BuildDate
    {
        get
        {
            try
            {
                var path = Assembly.GetExecutingAssembly().Location;
                if (string.IsNullOrEmpty(path)) return "unknown";
                var dt = System.IO.File.GetLastWriteTime(path);
                return dt.ToString("yyyy-MM-dd HH:mm");
            }
            catch
            {
                return "unknown";
            }
        }
    }

    /// <summary>Working directory der Exe — nützlich für Dev-Diagnose.</summary>
    public static string WorkingDirectory => System.AppContext.BaseDirectory;
}