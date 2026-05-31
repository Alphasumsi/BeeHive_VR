using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using HoneyOverlays.Models;

namespace HoneyOverlays.Services;

/// <summary>
/// Lädt und speichert Layout-Configs (eine JSON pro Auto + default.json).
/// Pfade kommen aus <see cref="ConfigPaths"/>.
/// </summary>
public static class ConfigStore
{
    /// <summary>Dateinamen im configs-Ordner, die NICHT als Car-Layout zählen.</summary>
    private static readonly HashSet<string> ReservedFileNames = new(System.StringComparer.OrdinalIgnoreCase)
    {
        "spotter.json", // gehört SpotterStore
    };

    private static bool IsReservedFile(string path)
        => ReservedFileNames.Contains(Path.GetFileName(path));

    /// <summary>Aktuelle Schema-Version. Bei Format-Brüchen erhöhen.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Zentrale JSON-Optionen — pretty-printed, UTF-8, lesbar.</summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = null, // wir nutzen explizite [JsonPropertyName]-Attribute
        // Unicode-Escape vermeiden — ° und Co. bleiben lesbar
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        // Defensive Defaults beim Lesen
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    // ====================================================================
    // LOAD
    // ====================================================================

    /// <summary>
    /// Liest alle *.json aus dem configs-Ordner. Kaputte Dateien werden
    /// übersprungen + im Log vermerkt. Gibt eine Liste in beliebiger
    /// Reihenfolge zurück (Sortierung macht der Caller).
    /// </summary>
    public static List<CarLayoutModel> LoadAll()
    {
        var result = new List<CarLayoutModel>();

        try
        {
            ConfigPaths.EnsureConfigsFolder();
        }
        catch
        {
            // EnsureConfigsFolder hat schon geloggt — wir geben leere Liste zurück
            return result;
        }

        // Reserved-Filenames: liegen im selben Ordner, gehören aber anderen Stores
        // (z.B. spotter.json → SpotterStore, eigenes Schema). Hier ausfiltern, sonst
        // wirft die ConfigFileModel-Deserialisierung jedes Mal einen falschen Warn.
        var files = Directory.GetFiles(ConfigPaths.ConfigsFolder, "*.json")
            .Where(p => !IsReservedFile(p))
            .ToArray();
        Logger.Info($"Scanning configs folder: found {files.Length} JSON file(s)");

        foreach (var path in files)
        {
            var layout = LoadFile(path);
            if (layout != null) result.Add(layout);
        }

        Logger.Info($"Loaded {result.Count} layout(s) successfully");
        return result;
    }

    /// <summary>
    /// Lädt eine einzelne JSON-Datei. Gibt null zurück wenn die Datei kaputt
    /// oder unlesbar ist (Fehler wird geloggt).
    /// </summary>
    public static CarLayoutModel? LoadFile(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            var wrapper = JsonSerializer.Deserialize<ConfigFileModel>(json, JsonOptions);

            if (wrapper == null || wrapper.Layout == null)
            {
                Logger.Warn($"Empty or invalid config file: {Path.GetFileName(path)}");
                return null;
            }

            // Schema-Version prüfen — für jetzt nur loggen, später Migration
            if (wrapper.SchemaVersion != CurrentSchemaVersion)
            {
                Logger.Warn($"Config '{Path.GetFileName(path)}' has schema version " +
                            $"{wrapper.SchemaVersion}, expected {CurrentSchemaVersion}. " +
                            $"Loading anyway.");
            }

            // URL-Rename-Migration: alte „index-honey-widget.html"-Targets auf
            // „honeyvr.html" umbiegen (29.5.2026). Beim nächsten Save landet's
            // korrekt im File.
            MigrateHoneyWidgetUrls(wrapper.Layout);

            return wrapper.Layout;
        }
        catch (JsonException ex)
        {
            Logger.Warn($"Skipping broken JSON: {Path.GetFileName(path)} — {ex.Message}");
            return null;
        }
        catch (System.Exception ex)
        {
            Logger.Error($"Failed to read config file: {Path.GetFileName(path)}", ex);
            return null;
        }
    }

    /// <summary>
    /// Migriert Source-Targets von der alten URL-Form (<c>index-honey-widget.html</c>)
    /// auf die gekürzte (<c>honeyvr.html</c>). Wird beim Load aufgerufen; der nächste
    /// Save schreibt's persistent zurück.
    /// </summary>
    private static void MigrateHoneyWidgetUrls(CarLayoutModel layout)
    {
        if (layout?.Sessions == null) return;
        foreach (var session in layout.Sessions)
        {
            if (session?.Sources == null) continue;
            foreach (var src in session.Sources)
            {
                if (src?.Target == null) continue;
                if (src.Target.IndexOf("index-honey-widget.html",
                        System.StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    src.Target = src.Target.Replace("index-honey-widget.html",
                        "honeyvr.html",
                        System.StringComparison.OrdinalIgnoreCase);
                }
            }
        }
    }

    // ====================================================================
    // SAVE
    // ====================================================================

    /// <summary>
    /// Speichert ein Layout als JSON. Dateiname kommt aus carName (sanitized).
    /// Default-Layout wird immer als "default.json" gespeichert.
    /// </summary>
    public static bool Save(CarLayoutModel layout)
    {
        if (layout == null) return false;

        try
        {
            ConfigPaths.EnsureConfigsFolder();

            var path = layout.IsDefault
                ? ConfigPaths.DefaultConfigFile
                : ConfigPaths.FilePathFor(layout.CarName);

            var wrapper = new ConfigFileModel
            {
                SchemaVersion = CurrentSchemaVersion,
                Layout = layout
            };

            var json = JsonSerializer.Serialize(wrapper, JsonOptions);

            // Atomic-ish write: erst in .tmp, dann umbenennen — verhindert
            // dass bei einem Crash mitten im Schreiben die Datei kaputt zurückbleibt.
            var tmpPath = path + ".tmp";
            File.WriteAllText(tmpPath, json, System.Text.Encoding.UTF8);
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmpPath, path);

            Logger.Info($"Saved layout: {Path.GetFileName(path)}");
            return true;
        }
        catch (System.Exception ex)
        {
            Logger.Error($"Failed to save layout '{layout.CarName}'", ex);
            return false;
        }
    }

    // ====================================================================
    // DELETE
    // ====================================================================

    /// <summary>
    /// Löscht die JSON-Datei eines Layouts. Default kann nicht gelöscht werden.
    /// </summary>
    public static bool Delete(CarLayoutModel layout)
    {
        if (layout == null || layout.IsDefault) return false;

        try
        {
            var path = ConfigPaths.FilePathFor(layout.CarName);
            if (File.Exists(path))
            {
                File.Delete(path);
                Logger.Info($"Deleted layout file: {Path.GetFileName(path)}");
                return true;
            }
            else
            {
                Logger.Warn($"Delete: file did not exist: {Path.GetFileName(path)}");
                return false;
            }
        }
        catch (System.Exception ex)
        {
            Logger.Error($"Failed to delete layout '{layout.CarName}'", ex);
            return false;
        }
    }

    // ====================================================================
    // BOOTSTRAP — leeres Default anlegen falls es noch keins gibt
    // ====================================================================

    /// <summary>
    /// Stellt sicher dass eine default.json existiert. Falls nicht, wird ein
    /// leeres Default mit drei leeren Sessions angelegt + auf Disk geschrieben.
    /// Aufgerufen beim ersten Start auf einem leeren System.
    /// </summary>
    public static CarLayoutModel EnsureDefaultExists()
    {
        var path = ConfigPaths.DefaultConfigFile;
        if (File.Exists(path))
        {
            var existing = LoadFile(path);
            if (existing != null) return existing;
            // Datei vorhanden aber kaputt → wir überschreiben mit leerem Default
            Logger.Warn($"default.json was unreadable — recreating it");
        }

        var fresh = new CarLayoutModel
        {
            CarName = "Template",
            CarClass = "",
            IsDefault = true,
            IsFavorite = false,
            Sessions = new()
            {
                new SessionConfigModel { Session = SessionType.Practice },
                new SessionConfigModel { Session = SessionType.Qualify },
                new SessionConfigModel { Session = SessionType.Race },
                new SessionConfigModel { Session = SessionType.TestDrive },
            }
        };

        Save(fresh);
        return fresh;
    }
}