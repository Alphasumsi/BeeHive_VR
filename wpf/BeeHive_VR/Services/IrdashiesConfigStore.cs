using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BeeHiveVR.Services;

/// <summary>
/// BeeHive-VR-eigene Dashies-Config (Lesen UND Schreiben), damit irdashies nicht mehr
/// installiert sein muss. Quelle: %LOCALAPPDATA%\BeeHive_VR\settings\dashies-config.json
/// (Format wie irdashies: { currentProfile, dashboards:{ default:{widgets,generalSettings} } }).
///
/// Beim ersten Mal Migration in dieser Reihenfolge:
///   1) Alt-Pfad %LOCALAPPDATA%\BeeHive_VR\irdashies-config.json (vor 0.8.6)
///   2) %APPDATA%\irdashies\config.json (Original-irdashies-Installation)
///   3) Eingebettete dashies-dist\dashboard.json → Minimal-Fallback.
///
/// Genutzt von: IrdashiesAdapterService (Lesen, BuildDashboard) und dem Dashies-Tab
/// (Schreiben einzelner Widget-Configs).
/// </summary>
public sealed class IrdashiesConfigStore
{
    private static IrdashiesConfigStore? _instance;
    public static IrdashiesConfigStore Instance => _instance ??= new IrdashiesConfigStore();

    private readonly object _gate = new();
    private JsonObject? _root;

    private static string AppDataRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Logger.AppDataFolderName);

    private static string LocalConfigPath => Path.Combine(AppDataRoot, "settings", "dashies-config.json");

    /// <summary>Alt-Pfad vor 0.8.6 (Migration). Lag direkt im Root + hieß noch irdashies-config.json.</summary>
    private static string LegacyLocalConfigPath => Path.Combine(AppDataRoot, "irdashies-config.json");

    private static string IrdashiesConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "irdashies", "config.json");

    // Eingebettete Baseline aus dem dashies-dist (kein D:\VBdev\irdashies-Pfad
    // zur Laufzeit). Leerer String falls die Asset-Suche nichts findet — der
    // Loader fällt dann auf seine Minimal-Standard-Config zurück.
    private static string FallbackDashboardPath => DashiesAssets.ResolveFallbackDashboardPath();

    private IrdashiesConfigStore() { }

    /// <summary>Aktives Profil (default "default").</summary>
    public string CurrentProfile
    {
        get
        {
            lock (_gate)
            {
                EnsureLoaded();
                return _root?["currentProfile"]?.GetValue<string>() ?? "default";
            }
        }
    }

    /// <summary>Das aktive DashboardLayout als losgelöstes Objekt (für JSON-Serialisierung).</summary>
    public object GetDashboardObject()
    {
        lock (_gate)
        {
            EnsureLoaded();
            var dash = ActiveDashboardNode();
            if (dash != null)
            {
                // Kopie als JsonElement (losgelöst vom Live-Baum).
                using var doc = JsonDocument.Parse(dash.ToJsonString());
                return doc.RootElement.Clone();
            }
            return new { widgets = Array.Empty<object>(), generalSettings = new { } };
        }
    }

    /// <summary>
    /// Patcht die config eines Widgets (z.B. "input") und speichert. Der Callback
    /// bekommt das (ggf. neu angelegte) config-JsonObject zum Mutieren.
    /// </summary>
    public void PatchWidgetConfig(string widgetId, Action<JsonObject> patch)
    {
        lock (_gate)
        {
            EnsureLoaded();
            var dash = ActiveDashboardNode();
            if (dash?["widgets"] is not JsonArray widgets) return;

            foreach (var w in widgets)
            {
                if (w is JsonObject wo &&
                    (wo["id"]?.GetValue<string>() == widgetId ||
                     wo["type"]?.GetValue<string>() == widgetId))
                {
                    if (wo["config"] is not JsonObject cfg)
                    {
                        cfg = new JsonObject();
                        wo["config"] = cfg;
                    }
                    patch(cfg);
                    Save();
                    return;
                }
            }
        }
    }

    /// <summary>Liest die config eines Widgets (Kopie) — null wenn nicht vorhanden.</summary>
    public JsonObject? GetWidgetConfig(string widgetId)
    {
        lock (_gate)
        {
            EnsureLoaded();
            var dash = ActiveDashboardNode();
            if (dash?["widgets"] is not JsonArray widgets) return null;
            foreach (var w in widgets)
            {
                if (w is JsonObject wo &&
                    (wo["id"]?.GetValue<string>() == widgetId ||
                     wo["type"]?.GetValue<string>() == widgetId))
                {
                    return wo["config"] is JsonObject cfg
                        ? JsonNode.Parse(cfg.ToJsonString()) as JsonObject
                        : null;
                }
            }
            return null;
        }
    }

    // --- intern ---

    private JsonObject? ActiveDashboardNode()
    {
        var profile = _root?["currentProfile"]?.GetValue<string>() ?? "default";
        return _root?["dashboards"]?[profile] as JsonObject;
    }

    /// <summary>
    /// Ergänzt Widgets, die in der eingebetteten Baseline (dashies-dist/dashboard.json)
    /// existieren, aber in der geladenen User-Config fehlen — z.B. neu importierte Dashies
    /// wie 'cornername'. So tauchen neue Widgets bei bestehenden Configs im Dashies-Tab /
    /// in der Preview auf, ohne das gespeicherte Layout zu verändern. Speichert nur, wenn
    /// tatsächlich etwas ergänzt wurde.
    /// </summary>
    private void MergeMissingWidgets()
    {
        try
        {
            if (!TryLoadFile(FallbackDashboardPath, out var baseline) || baseline == null) return;
            if (baseline["widgets"] is not JsonArray baseWidgets) return;

            var dash = ActiveDashboardNode();
            if (dash == null) return;
            if (dash["widgets"] is not JsonArray widgets)
            {
                widgets = new JsonArray();
                dash["widgets"] = widgets;
            }

            var have = new HashSet<string>();
            foreach (var w in widgets)
                if (w?["id"]?.GetValue<string>() is string id) have.Add(id);

            bool added = false;
            foreach (var bw in baseWidgets)
            {
                if (bw?["id"]?.GetValue<string>() is not string bid || have.Contains(bid)) continue;
                widgets.Add(JsonNode.Parse(bw.ToJsonString())); // Deep-Copy, losgelöst von der Baseline
                added = true;
                Logger.Info($"IrdashiesConfigStore: Widget '{bid}' aus Baseline ergänzt.");
            }

            if (added) Save();
        }
        catch (Exception ex)
        {
            Logger.Warn($"IrdashiesConfigStore: MergeMissingWidgets fehlgeschlagen: {ex.Message}");
        }
    }

    private void EnsureLoaded()
    {
        if (_root != null) return;

        // 1) Neue BeeHive-VR-eigene Config (settings/dashies-config.json)
        if (TryLoadFile(LocalConfigPath, out var root))
        {
            _root = root;
            MergeMissingWidgets();   // neue Widgets (z.B. cornername) aus Baseline nachziehen
            return;
        }

        // 2) Alt-Pfad migrieren (irdashies-config.json im Root vor 0.8.6).
        //    Datei wird beim ersten Save() automatisch in den neuen Pfad gespeichert.
        if (TryLoadFile(LegacyLocalConfigPath, out var legacy))
        {
            _root = legacy;
            Logger.Info($"IrdashiesConfigStore: migrated {LegacyLocalConfigPath} → {LocalConfigPath}");
            MergeMissingWidgets();
            Save();
            try { File.Delete(LegacyLocalConfigPath); } catch { /* nicht kritisch */ }
            return;
        }

        // 3) Einmal-Migration aus dem Original-irdashies
        if (TryLoadFile(IrdashiesConfigPath, out var migrated))
        {
            _root = migrated;
            Logger.Info($"IrdashiesConfigStore: aus {IrdashiesConfigPath} migriert.");
            MergeMissingWidgets();
            Save();
            return;
        }

        // 4) Fallback: generierte Baseline aus honey-dist
        if (TryLoadFile(FallbackDashboardPath, out var fb) && fb != null)
        {
            // dashboard.json ist ein DashboardLayout (kein Hüllen-Objekt) → einwickeln.
            _root = new JsonObject
            {
                ["currentProfile"] = "default",
                ["dashboards"] = new JsonObject { ["default"] = fb },
            };
            Logger.Info("IrdashiesConfigStore: Fallback dashies-dist/dashboard.json.");
            Save();
            return;
        }

        // 5) Minimal
        _root = new JsonObject
        {
            ["currentProfile"] = "default",
            ["dashboards"] = new JsonObject
            {
                ["default"] = new JsonObject
                {
                    ["widgets"] = new JsonArray(),
                    ["generalSettings"] = new JsonObject(),
                },
            },
        };
        Logger.Warn("IrdashiesConfigStore: keine Quelle gefunden — Minimal-Config.");
    }

    private static bool TryLoadFile(string path, out JsonObject? node)
    {
        node = null;
        try
        {
            if (!File.Exists(path)) return false;
            node = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
            return node != null;
        }
        catch (Exception ex)
        {
            Logger.Warn($"IrdashiesConfigStore: {path} nicht lesbar: {ex.Message}");
            return false;
        }
    }

    private void Save()
    {
        if (_root == null) return;
        try
        {
            var dir = Path.GetDirectoryName(LocalConfigPath)!;
            Directory.CreateDirectory(dir);
            var json = _root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            var tmp = LocalConfigPath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, LocalConfigPath, overwrite: true);
        }
        catch (Exception ex)
        {
            Logger.Warn($"IrdashiesConfigStore: Speichern fehlgeschlagen: {ex.Message}");
        }
    }
}
