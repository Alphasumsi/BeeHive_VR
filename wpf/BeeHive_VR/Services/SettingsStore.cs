using System;
using System.IO;
using System.Text.Json;
using BeeHiveVR.Models;

namespace BeeHiveVR.Services;

/// <summary>
/// Verwaltet die App-Einstellungen. Persistiert nach
/// <c>%LOCALAPPDATA%\BeeHiveVR\settings\settings.json</c>.
/// Altpfad <c>…\BeeHiveVR\settings.json</c> wird beim ersten Load nach
/// <c>settings\settings.json</c> migriert.
/// </summary>
public static class SettingsStore
{
    /// <summary>Aktuelle Einstellungen — Default beim Start, wird von Load() überschrieben.</summary>
    public static SettingsModel Current { get; set; } = new();

    private static string AppDataRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Logger.AppDataFolderName);

    /// <summary>Pfad zur settings.json (im settings-Subfolder).</summary>
    public static string SettingsFile => Path.Combine(AppDataRoot, "settings", "settings.json");

    /// <summary>Alt-Pfad (Migration). Vor 0.8.6 lag die Datei direkt im Root.</summary>
    private static string LegacySettingsFile => Path.Combine(AppDataRoot, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>Lädt die Settings aus der JSON-Datei. Bei Fehler/Missing bleiben Default-Werte.</summary>
    public static void Load()
    {
        // Migration: alte settings.json einmalig in den neuen Subfolder umziehen.
        try
        {
            if (!File.Exists(SettingsFile) && File.Exists(LegacySettingsFile))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
                File.Move(LegacySettingsFile, SettingsFile);
                Logger.Info($"SettingsStore: migrated {LegacySettingsFile} → {SettingsFile}");
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"SettingsStore: legacy migration failed: {ex.Message}");
        }

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
