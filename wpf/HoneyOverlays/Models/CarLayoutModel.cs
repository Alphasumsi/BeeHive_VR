using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HoneyOverlays.Models;

/// <summary>
/// Komplettes Layout für ein Auto. Wird aus / in JSON
/// (configs/&lt;CarName&gt;.json) geladen und gespeichert.
///
/// "Default" ist auch ein CarLayoutModel mit IsDefault = true.
/// </summary>
public class CarLayoutModel
{
    /// <summary>iRSDK CarScreenName, z.B. "Porsche 911 Cup (992.2)"</summary>
    [JsonPropertyName("carName")]
    public string CarName { get; set; } = "";

    /// <summary>iRSDK CarClassShortName (optional, für Klassen-Fallback)</summary>
    [JsonPropertyName("carClass")]
    public string CarClass { get; set; } = "";

    /// <summary>Markiert das Default-Layout das als Vorlage dient.</summary>
    [JsonPropertyName("isDefault")]
    public bool IsDefault { get; set; } = false;

    /// <summary>Vom User als Favorit markiert (wird in der Liste oben einsortiert).</summary>
    [JsonPropertyName("favorite")]
    public bool IsFavorite { get; set; } = false;

    /// <summary>Eine Konfiguration pro Session-Typ (Practice / Qualify / Race).</summary>
    [JsonPropertyName("sessions")]
    public List<SessionConfigModel> Sessions { get; set; } = new();

    /// <summary>Round-Trip-Speicher für unbekannte Felder.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}