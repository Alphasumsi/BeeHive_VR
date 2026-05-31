namespace HoneyOverlays.Services;

/// <summary>
/// Edition-spezifische Identität, gesteuert über die Build-Konstante LITE
/// (Konfiguration "Release-Lite"). Bündelt alle Werte, die zwischen der
/// Vollversion und einer späteren schlanken "Lite"-Edition abweichen sollen,
/// damit beide Editionen vollständig getrennten Zustand nutzen (eigener
/// AppData-Ordner, Mutex, Autostart-Key, Name).
///
/// Aktuell existiert noch keine "Release-Lite"-Konfiguration → LITE ist
/// undefiniert → der #else-Zweig greift → identisches Verhalten wie zuvor
/// ("HoneyOverlays"). Diese Klasse ist die zentrale Anlaufstelle, damit die
/// Identitäts-Strings nicht mehr quer im Code verstreut sind.
///
/// Hinweis: Der Engine-IPC-Pipe-Name (siehe EngineLink) bleibt bewusst
/// editionsübergreifend gleich — er ist ein Build-Vertrag mit der C++-Engine,
/// die in iRacing injiziert wird. Da es nur eine iRacing-Instanz gibt, gibt es
/// hier keinen Konflikt, deshalb gehört er NICHT hierher.
/// </summary>
public static class AppEdition
{
#if LITE
    public const bool IsLite = true;
    public const string ProductName = "Honey VR-Dashies";
    public const string DataFolderName = "HoneyVRDashies";
    public const string StartupKey = "HoneyVRDashies";
    public const string InstanceMutexName = "HoneyVRDashies_SingleInstance_8F3A1C20";
    public const string ActivateEventName = "HoneyVRDashies_Activate_8F3A1C20";
    public const string IconPackUri = "pack://application:,,,/Assets/bee_icon_256_Lite.ico";
#else
    public const bool IsLite = false;
    public const string ProductName = "BeeHive VR";
    public const string DataFolderName = "BeeHive_VR";
    public const string StartupKey = "BeeHive_VR";
    public const string InstanceMutexName = "BeeHive_VR_SingleInstance_8F3A1C20";
    public const string ActivateEventName = "BeeHive_VR_Activate_8F3A1C20";
    public const string IconPackUri = "pack://application:,,,/Assets/bee_icon_256.ico";
#endif
}
