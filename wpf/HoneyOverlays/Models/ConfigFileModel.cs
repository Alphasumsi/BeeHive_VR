using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HoneyOverlays.Models;

/// <summary>
/// Top-Level-Wrapper für eine einzelne Layout-JSON-Datei.
/// Enthält schemaVersion + das eigentliche Layout. So können wir später
/// Format-Migrationen einbauen ohne alte Configs zu brechen.
/// </summary>
public class ConfigFileModel
{
    /// <summary>Aktuelle Schema-Version. Bei Format-Brüchen erhöhen.</summary>
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = 1;

    [JsonPropertyName("layout")]
    public CarLayoutModel Layout { get; set; } = new();

    /// <summary>
    /// Sammelt alle unbekannten Felder auf Top-Level — werden beim Save
    /// wieder rausgeschrieben (Round-Trip-Stabilität für Felder die nur
    /// die C++ Engine kennt).
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}