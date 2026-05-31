using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HoneyOverlays.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SourceType
{
    Browser,    // SimHub HTTP-Browsersource (z.B. http://localhost:8888/fuel)
    Window      // Fenster-Capture (z.B. SimHub-Dashboard-Fenster)
}

/// <summary>
/// Eine Quelle innerhalb einer Session (z.B. Fuel-Browsersource oder
/// ein gecapturetes SimHub-Wheel-Fenster). Jede Quelle hat ihre eigene
/// VR-Platzierung.
/// </summary>
public class SourceModel
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("type")]
    public SourceType Type { get; set; } = SourceType.Browser;

    // Browser → URL, z.B. "http://localhost:8888/fuel"
    // Window  → Fenstertitel oder Prozessname
    [JsonPropertyName("target")]
    public string Target { get; set; } = "";

    [JsonPropertyName("visible")]
    public bool Visible { get; set; } = true;

    // VR-Platzierung — Meter / Grad / Faktor / 0..1
    [JsonPropertyName("x")] public float X { get; set; } = 0.0f;
    [JsonPropertyName("y")] public float Y { get; set; } = 0.0f;
    [JsonPropertyName("z")] public float Z { get; set; } = -0.8f;
    [JsonPropertyName("yaw")] public float Yaw { get; set; } = 0.0f;
    [JsonPropertyName("pitch")] public float Pitch { get; set; } = 0.0f;
    [JsonPropertyName("scale")] public float Scale { get; set; } = 0.10f;
    [JsonPropertyName("opacity")] public float Opacity { get; set; } = 1.0f;

    /// <summary>
    /// Browser-Source: Pixelbreite/-höhe des browser-host-Fensters. 0 = Auto (lässt
    /// browser-host mit Default-Größe + JS-Auto-Fit rennen). Für SimHub-Overlays mit
    /// fluidem Body explizit setzen, damit das Fenster passgenau startet.
    /// Bei Window-Sources ignoriert (Fenstergröße bestimmt die App).
    /// </summary>
    [JsonPropertyName("pixelWidth")] public int PixelWidth { get; set; } = 0;
    [JsonPropertyName("pixelHeight")] public int PixelHeight { get; set; } = 0;

    /// <summary>Round-Trip-Speicher für unbekannte Felder.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}