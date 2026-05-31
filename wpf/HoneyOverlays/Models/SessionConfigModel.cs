using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HoneyOverlays.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SessionType
{
    Practice,
    Qualify,
    Race,
    TestDrive, // iRacing-"Test Drive" / SessionInfo "Offline Testing"
}

/// <summary>
/// Konfiguration für eine Session (Practice / Qualify / Race) eines Autos.
/// Enthält die Liste der Quellen mit ihren jeweiligen VR-Platzierungen.
/// </summary>
public class SessionConfigModel
{
    [JsonPropertyName("session")]
    public SessionType Session { get; set; } = SessionType.Practice;

    [JsonPropertyName("overlays")]
    public List<SourceModel> Sources { get; set; } = new();

    /// <summary>Round-Trip-Speicher für unbekannte Felder.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}