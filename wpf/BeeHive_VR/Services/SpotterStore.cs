using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using BeeHiveVR.Models;

namespace BeeHiveVR.Services;

/// <summary>
/// Persistenz des globalen Spotter-Overlay-Sets (car-unabhängig, eine Datei
/// <c>spotter.json</c> im configs-Ordner). Bewusst KEIN Sessions-Konzept —
/// Spotter ist ein einzelnes Set, das greift wenn man nicht selbst fährt.
/// </summary>
public static class SpotterStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = null,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static string FilePath => Path.Combine(ConfigPaths.ConfigsFolder, "spotter.json");

    public static List<SourceModel> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new();
            var json = File.ReadAllText(FilePath);
            var list = JsonSerializer.Deserialize<List<SourceModel>>(json, JsonOptions) ?? new();
            // URL-Rename-Migration: alte Targets aus dem Honey-Tree auf
            // „dashie.html" umbiegen. Historie:
            //   29.5.2026: index-honey-widget.html → honeyvr.html
            //    2.6.2026: honeyvr.html             → dashie.html
            // Beim nächsten Save landet's persistent drin.
            foreach (var src in list)
            {
                if (src?.Target == null) continue;
                if (src.Target.IndexOf("index-honey-widget.html",
                        StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    src.Target = src.Target.Replace("index-honey-widget.html",
                        "dashie.html", StringComparison.OrdinalIgnoreCase);
                }
                if (src.Target.IndexOf("honeyvr.html",
                        StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    src.Target = src.Target.Replace("honeyvr.html",
                        "dashie.html", StringComparison.OrdinalIgnoreCase);
                }
            }
            return list;
        }
        catch (Exception ex)
        {
            Logger.Error("SpotterStore.Load failed", ex);
            return new();
        }
    }

    public static void Save(List<SourceModel> sources)
    {
        try
        {
            ConfigPaths.EnsureConfigsFolder();
            var json = JsonSerializer.Serialize(sources, JsonOptions);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, json, Encoding.UTF8);
            if (File.Exists(FilePath)) File.Delete(FilePath);
            File.Move(tmp, FilePath);
            Logger.Info($"Saved spotter overlays ({sources.Count})");
        }
        catch (Exception ex)
        {
            Logger.Error("SpotterStore.Save failed", ex);
        }
    }
}
