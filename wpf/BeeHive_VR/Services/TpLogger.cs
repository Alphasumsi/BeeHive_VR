using System;
using System.IO;
using System.Text;

namespace BeeHiveVR.Services;

/// <summary>
/// Eigenes Log für den TradingPaints-Downloader
/// (%LOCALAPPDATA%\BeeHive_VR\logs\TP.log). Getrennt von app.log, weil
/// TradingPaints bei großen Grids viele Zeilen produziert und das
/// Haupt-Log sonst zuspammt. Gleiche Rotation wie <see cref="Logger"/>
/// (3 MB, .old), aber ohne den In-Memory-Buffer (kein GUI-Log-Page-Bedarf
/// für diesen Nischen-Bereich).
/// </summary>
public static class TpLogger
{
    private const long MaxLogSizeBytes = 3 * 1024 * 1024;
    private const int RotateCheckInterval = 50;

    private static readonly object _sync = new();
    private static readonly string _logFilePath;
    private static bool _initialized;
    private static int _writesSinceRotateCheck;

    static TpLogger()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var logsDir = Path.Combine(appData, Logger.AppDataFolderName, "logs");
        _logFilePath = Path.Combine(logsDir, "TP.log");
    }

    public static void Info(string message) => Write("INFO ", message, null);
    public static void Warn(string message) => Write("WARN ", message, null);
    public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    private static void Write(string level, string message, Exception? ex)
    {
        lock (_sync)
        {
            try
            {
                if (!_initialized)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_logFilePath)!);
                    RotateIfNeeded();
                    _initialized = true;
                }
                else if (++_writesSinceRotateCheck >= RotateCheckInterval)
                {
                    _writesSinceRotateCheck = 0;
                    RotateIfNeeded();
                }

                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}" +
                           (ex != null ? Environment.NewLine + ex : "") + Environment.NewLine;
                File.AppendAllText(_logFilePath, line, Encoding.UTF8);
            }
            catch
            {
                // Schlucken — ein kaputtes Logfile darf die App nicht killen.
            }
        }
    }

    private static void RotateIfNeeded()
    {
        if (!File.Exists(_logFilePath)) return;
        var info = new FileInfo(_logFilePath);
        if (info.Length <= MaxLogSizeBytes) return;
        var oldPath = _logFilePath + ".old";
        if (File.Exists(oldPath)) File.Delete(oldPath);
        File.Move(_logFilePath, oldPath);
    }
}
