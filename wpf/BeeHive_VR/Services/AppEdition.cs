namespace BeeHiveVR.Services;

/// <summary>
/// Zentrale Identitäts-Konstanten der App (Name, AppData-Folder, Mutex,
/// Autostart-Key, Icon). Hier gebündelt damit die Strings nicht quer im Code
/// verstreut sind.
///
/// Hinweis: Der Engine-IPC-Pipe-Name (siehe EngineLink) gehört NICHT hierher
/// — er ist ein Build-Vertrag mit der C++-Engine in iRacing.
/// </summary>
public static class AppEdition
{
    public const string ProductName       = "BeeHive VR";
    public const string DataFolderName    = "BeeHive_VR";
    public const string StartupKey        = "BeeHive_VR";
    public const string InstanceMutexName = "BeeHive_VR_SingleInstance_8F3A1C20";
    public const string ActivateEventName = "BeeHive_VR_Activate_8F3A1C20";
    public const string IconPackUri       = "pack://application:,,,/Assets/bee_icon_256.ico";
}
