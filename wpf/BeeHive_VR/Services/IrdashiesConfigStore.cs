using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BeeHiveVR.Services;

/// <summary>
/// BeeHive-VR-eigene irdashies-Config (Lesen UND Schreiben), damit irdashies nicht mehr
/// installiert sein muss. Quelle: %LOCALAPPDATA%\BeeHiveVR\irdashies-config.json
/// (Format wie irdashies: { currentProfile, dashboards:{ default:{widgets,generalSettings} } }).
///
/// Beim ersten Mal Migration aus %APPDATA%\irdashies\config.json (falls vorhanden),
/// sonst Fallback aus der eingebetteten dashies-dist\dashboard.json → Minimal.
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

    private static string LocalConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Logger.AppDataFolderName, "irdashies-config.json");

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

    private void EnsureLoaded()
    {
        if (_root != null) return;

        // 1) BeeHive-VR-eigene Config
        if (TryLoadFile(LocalConfigPath, out var root))
        {
            _root = root;
            return;
        }

        // 2) Einmal-Migration aus dem Original-irdashies
        if (TryLoadFile(IrdashiesConfigPath, out var migrated))
        {
            _root = migrated;
            Logger.Info($"IrdashiesConfigStore: aus {IrdashiesConfigPath} migriert.");
            Save();
            return;
        }

        // 3) Fallback: generierte Baseline aus honey-dist
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

        // 4) Minimal
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
