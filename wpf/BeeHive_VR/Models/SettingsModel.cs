using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace BeeHiveVR.Models;

/// <summary>
/// App-weite Einstellungen, persistiert nach
/// %LOCALAPPDATA%\BeeHiveVR\settings.json.
/// </summary>
public class SettingsModel
{
    // --- General ----------------------------------------------------------
    /// <summary>
    /// true = Layout sofort anlegen sobald die Engine ein neues Auto meldet.
    /// false = Layout erst beim ersten Edit anlegen.
    /// </summary>
    [JsonPropertyName("autoCreateLayoutOnNewCar")]
    public bool AutoCreateLayoutOnNewCar { get; set; } = true;

    [JsonPropertyName("startWithWindows")]
    public bool StartWithWindows { get; set; } = false;

    [JsonPropertyName("startMinimized")]
    public bool StartMinimized { get; set; } = false;

    [JsonPropertyName("startInTray")]
    public bool StartInTray { get; set; } = false;

    /// <summary>Welche Page beim Start aktiv ist: "Menu", "Layout", etc.</summary>
    [JsonPropertyName("startPage")]
    public string StartPage { get; set; } = "Layout";

    // --- Engine -----------------------------------------------------------
    /// <summary>
    /// Pfad zur browser-host.exe (WebView2-Wrapper für Browser-Sources).
    /// Leer = Auto-Detect (versucht den dev-Build-Pfad neben dem Layer).
    /// </summary>
    [JsonPropertyName("browserHostExecutable")]
    public string BrowserHostExecutable { get; set; } = "";

    // --- UI ---------------------------------------------------------------
    // Icon-Nav Sichtbarkeit (Appearance-Toggles). TradingPaints existiert schon,
    // die drei anderen sind vorerst Platzhalter (Default aus).
    [JsonPropertyName("showTradingPaints")]
    public bool ShowTradingPaints { get; set; } = true;

    [JsonPropertyName("showHtmlOverlays")]
    public bool ShowHtmlOverlays { get; set; } = false;

    [JsonPropertyName("showAutostart")]
    public bool ShowAutostart { get; set; } = false;

    [JsonPropertyName("showButtonbox")]
    public bool ShowButtonbox { get; set; } = false;

    /// <summary>Dashies-Tab (In-App-Config). Experimentell → nur im Dev-Mode sichtbar.</summary>
    [JsonPropertyName("showDashies")]
    public bool ShowDashies { get; set; } = false;

    // --- Window-State (Remember Window Position and Scale) -----------------
    /// <summary>Master-Toggle: Fenster-Geometrie + UI-Scale über Sessions merken.</summary>
    [JsonPropertyName("rememberWindowPositionAndScale")]
    public bool RememberWindowPositionAndScale { get; set; } = false;

    // 0 = noch nicht gespeichert (Restore wird übersprungen).
    [JsonPropertyName("windowLeft")]   public double WindowLeft { get; set; }
    [JsonPropertyName("windowTop")]    public double WindowTop { get; set; }
    [JsonPropertyName("windowWidth")]  public double WindowWidth { get; set; }
    [JsonPropertyName("windowHeight")] public double WindowHeight { get; set; }
    [JsonPropertyName("windowMaximized")] public bool WindowMaximized { get; set; }

    [JsonPropertyName("uiScale")]
    public double UiScale { get; set; } = 1.0;

    // --- Keybinds ---------------------------------------------------------
    /// <summary>Aktion-Name (enum) → serialisierter InputChord. Leer = nicht belegt.</summary>
    [JsonPropertyName("keybinds")]
    public Dictionary<string, string> Keybinds { get; set; } = new();

    // --- Trading Paints ---------------------------------------------------
    /// <summary>Master-Toggle für den Trading-Paints-Downloader.</summary>
    [JsonPropertyName("tradingPaintsEnabled")]
    public bool TradingPaintsEnabled { get; set; } = false;

    /// <summary>Override-Pfad. Leer = Default (%USERPROFILE%\Documents\iRacing\paint).</summary>
    [JsonPropertyName("tradingPaintsFolder")]
    public string TradingPaintsFolder { get; set; } = "";

    /// <summary>Max Download-Geschwindigkeit in KB/s. 0 = unbegrenzt.</summary>
    [JsonPropertyName("tradingPaintsMaxDownloadKbps")]
    public int TradingPaintsMaxDownloadKbps { get; set; } = 1024;

    /// <summary>Dateien älter als N Tage werden beim Cleanup gelöscht.</summary>
    [JsonPropertyName("tradingPaintsAutoCleanupDays")]
    public int TradingPaintsAutoCleanupDays { get; set; } = 30;

    /// <summary>Bei App-Start einmal Cleanup laufen lassen.</summary>
    [JsonPropertyName("tradingPaintsCleanupOnStartup")]
    public bool TradingPaintsCleanupOnStartup { get; set; } = false;

    // --- Dev / Preview ----------------------------------------------------
    /// <summary>Dev-Toggle: alte Layout-Bar (Legacy) statt der VR-Layouts-Sidebar benutzen.</summary>
    [JsonPropertyName("useLegacyLayoutBar")]
    public bool UseLegacyLayoutBar { get; set; } = false;
}