using System;
using Microsoft.Win32;

namespace HoneyOverlays.Services;

/// <summary>
/// Verwaltet den Autostart-Eintrag in der Windows-Registry (HKCU Run).
/// Kein Admin nötig — pro User-Profil.
/// </summary>
public static class StartupHelper
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = AppEdition.StartupKey;

    /// <summary>Aktuelle Autostart-Aktivierung lesen.</summary>
    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string s && !string.IsNullOrWhiteSpace(s);
        }
        catch (Exception ex)
        {
            Logger.Warn($"StartupHelper.IsEnabled failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>Autostart aktivieren — schreibt Pfad zur aktuellen EXE in HKCU\…\Run.</summary>
    public static bool Enable()
    {
        try
        {
            var exe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(exe))
            {
                Logger.Warn("StartupHelper.Enable: cannot resolve MainModule.FileName");
                return false;
            }

            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            // Pfad in Quotes — falls Leerzeichen drin sind
            key.SetValue(ValueName, $"\"{exe}\"");
            Logger.Info($"StartupHelper: enabled, exe={exe}");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error("StartupHelper.Enable failed", ex);
            return false;
        }
    }

    /// <summary>Autostart deaktivieren — entfernt den Eintrag.</summary>
    public static bool Disable()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key?.GetValue(ValueName) != null)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                Logger.Info("StartupHelper: disabled");
            }
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error("StartupHelper.Disable failed", ex);
            return false;
        }
    }
}
