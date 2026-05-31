using System;
using System.IO;
using System.Linq;

namespace HoneyOverlays.Services;

/// <summary>
/// Zentralisiert alle Pfade für die Config-Speicherung.
/// Default-Pfad: %LOCALAPPDATA%\&lt;Logger.AppDataFolderName&gt;\configs\.
/// <see cref="OverridePath"/> kann bei Bedarf gesetzt werden (z.B. Dev-Tools).
/// </summary>
public static class ConfigPaths
{
    /// <summary>Wenn gesetzt, überschreibt das den Default-Pfad. null = Default.</summary>
    public static string? OverridePath { get; set; }

    /// <summary>Voller Pfad zum configs-Ordner.</summary>
    public static string ConfigsFolder
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(OverridePath))
                return OverridePath!;

            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(appData, Logger.AppDataFolderName, "configs");
        }
    }

    /// <summary>Pfad zur default.json (immer das Fallback-Layout).</summary>
    public static string DefaultConfigFile =>
        Path.Combine(ConfigsFolder, "default.json");

    /// <summary>Stellt sicher dass der configs-Ordner existiert.</summary>
    public static void EnsureConfigsFolder()
    {
        try
        {
            Directory.CreateDirectory(ConfigsFolder);
        }
        catch (Exception ex)
        {
            Logger.Error($"Could not create configs folder: {ConfigsFolder}", ex);
            throw;
        }
    }

    /// <summary>
    /// Wandelt einen Auto-Namen in einen Windows-tauglichen Dateinamen.
    /// Verbotene Zeichen (\, /, :, *, ?, ", &lt;, &gt;, |) werden durch _ ersetzt.
    /// Mehrfache Spaces / Underscores werden zusammengefasst.
    /// </summary>
    public static string SanitizeFileName(string carName)
    {
        if (string.IsNullOrWhiteSpace(carName)) return "unnamed";

        var invalid = Path.GetInvalidFileNameChars();
        var chars = carName.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var clean = new string(chars).Trim();

        // Mehrfache Underscores reduzieren ("AMG / GT3" → "AMG _ GT3" → "AMG _ GT3" ist ok,
        // aber "////" → "____" wäre hässlich → auf einen Underscore reduzieren)
        while (clean.Contains("__"))
            clean = clean.Replace("__", "_");

        return string.IsNullOrWhiteSpace(clean) ? "unnamed" : clean;
    }

    /// <summary>Voller Pfad zur JSON-Datei für ein bestimmtes Auto.</summary>
    public static string FilePathFor(string carName) =>
        Path.Combine(ConfigsFolder, SanitizeFileName(carName) + ".json");
}