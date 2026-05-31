using System;
using System.IO;
using System.Text.Json;
using HoneyOverlays.Models;

namespace HoneyOverlays.Services;

/// <summary>
/// Verwaltet die App-Einstellungen. Persistiert nach
/// <c>%LOCALAPPDATA%\HoneyOverlays\settings.json</c>.
/// </summary>
public static class SettingsStore
{
    /// <summary>Aktuelle Einstellungen — Default beim Start, wird von Load() überschrieben.</summary>
    public static SettingsModel Current { get; set; } = new();

    /// <summary>Pfad zur settings.json (parallel zu configs-Ordner).</summary>
    public static string SettingsFile
    {
        get
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(appData, Logger.AppDataFolderName, "settings.json");
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>Lädt die Settings aus der JSON-Datei. Bei Fehler/Missing bleiben Default-Werte.</summary>
    public static void Load()
    {
        var path = SettingsFile;
        if (!File.Exists(path))
        {
            Logger.Info($"SettingsStore: no settings.json found at {path} — using defaults");
            return;
        }

        try
        {
            var json = File.ReadAllText(path);
            var loaded = JsonSerializer.Deserialize<SettingsModel>(json, JsonOptions);
            if (loaded != null)
            {
                Current = loaded;
                Logger.Info($"SettingsStore: loaded {Path.GetFileName(path)}");
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"SettingsStore: failed to load settings, using defaults: {ex.Message}");
        }
    }

    /// <summary>Schreibt die aktuellen Settings nach JSON. Atomic: tmp+rename.</summary>
    public static bool Save()
    {
        var path = SettingsFile;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var json = JsonSerializer.Serialize(Current, JsonOptions);

            var tmp = path + ".tmp";
            File.WriteAllText(tmp, json, System.Text.Encoding.UTF8);
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);

            // Auto-Save feuert pro Property-Änderung — kein Erfolgs-Log, sonst spam.
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error($"SettingsStore: failed to save settings", ex);
            return false;
        }
    }
}
