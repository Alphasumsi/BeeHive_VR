using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;

namespace BeeHiveVR.Services;

public enum LogLevel
{
    Info,
    Warn,
    Error
}

/// <summary>Ein einzelner Log-Eintrag (für In-Memory-Buffer + spätere GUI-Page).</summary>
public class LogEntry
{
    public DateTime Timestamp { get; set; }
    public LogLevel Level { get; set; }
    public string Message { get; set; } = "";
    public string? Exception { get; set; }

    public override string ToString() =>
        $"{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level,-5}] {Message}" +
        (Exception != null ? Environment.NewLine + Exception : "");
}

/// <summary>
/// Zentraler Logger: schreibt parallel in Datei (%LOCALAPPDATA%\&lt;App&gt;\logs\app.log)
/// und in einen In-Memory-Buffer (ObservableCollection für spätere Log-Page in der GUI).
///
/// Thread-safe via lock — kann von beliebigen Threads aufgerufen werden.
/// </summary>
public static class Logger
{
    /// <summary>App-Ordnername unter %LOCALAPPDATA% — editionsabhängig zentral
    /// in <see cref="AppEdition"/> definiert (Lite nutzt einen eigenen Ordner).</summary>
    public const string AppDataFolderName = AppEdition.DataFolderName;

    /// <summary>Maximale Log-Datei-Größe bevor rotiert wird (5 MB).</summary>
    private const long MaxLogSizeBytes = 5 * 1024 * 1024;

    /// <summary>Maximale Anzahl Einträge im In-Memory-Buffer.</summary>
    private const int MaxInMemoryEntries = 500;

    private static readonly object _sync = new();
    private static readonly string _logFilePath;
    private static bool _initialized;

    /// <summary>Live-Buffer für eine spätere Log-Page in der GUI.</summary>
    public static ObservableCollection<LogEntry> Entries { get; } = new();

    static Logger()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var logsDir = Path.Combine(appData, AppDataFolderName, "logs");
        _logFilePath = Path.Combine(logsDir, "app.log");
    }

    /// <summary>Einmalig beim App-Start aufrufen — legt Ordner an, rotiert große Logs.</summary>
    public static void Initialize()
    {
        lock (_sync)
        {
            if (_initialized) return;

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_logFilePath)!);

                // Rotation: bei zu großer Datei einmal nach .old verschieben
                if (File.Exists(_logFilePath))
                {
                    var info = new FileInfo(_logFilePath);
                    if (info.Length > MaxLogSizeBytes)
                    {
                        var oldPath = _logFilePath + ".old";
                        if (File.Exists(oldPath)) File.Delete(oldPath);
                        File.Move(_logFilePath, oldPath);
                    }
                }

                _initialized = true;
            }
            catch
            {
                // Wenn das Log-Setup fehlschlägt (Berechtigungen, Disk voll),
                // bleibt nur der In-Memory-Buffer aktiv.
            }
        }

        Info($"=== {AppInfo.AppName} {AppInfo.VersionDisplay} started — log file: {_logFilePath} ===");
    }

    public static void Info(string message) => Write(LogLevel.Info, message, null);
    public static void Warn(string message) => Write(LogLevel.Warn, message, null);
    public static void Error(string message, Exception? ex = null) => Write(LogLevel.Error, message, ex);

    private static void Write(LogLevel level, string message, Exception? ex)
    {
        var entry = new LogEntry
        {
            Timestamp = DateTime.Now,
            Level = level,
            Message = message,
            Exception = ex?.ToString()
        };

        // Datei schreiben (best-effort, niemals werfen)
        lock (_sync)
        {
            if (_initialized)
            {
                try
                {
                    File.AppendAllText(_logFilePath, entry + Environment.NewLine, Encoding.UTF8);
                }
                catch
                {
                    // Schlucken — ein kaputtes Logfile darf die App nicht killen
                }
            }
        }

        // In-Memory-Buffer aktualisieren — UI-Thread safe via Dispatcher
        var app = Application.Current;
        if (app != null)
        {
            app.Dispatcher.BeginInvoke(new Action(() =>
            {
                Entries.Add(entry);
                while (Entries.Count > MaxInMemoryEntries) Entries.RemoveAt(0);
            }));
        }
        else
        {
            // Fallback (z.B. in Unit-Tests ohne Dispatcher)
            Entries.Add(entry);
            while (Entries.Count > MaxInMemoryEntries) Entries.RemoveAt(0);
        }
    }

    /// <summary>Pfad zur Log-Datei (für "Open log file"-Buttons in Settings später).</summary>
    public static string LogFilePath => _logFilePath;
}