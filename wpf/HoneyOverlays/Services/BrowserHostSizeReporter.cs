using System;
using System.IO;
using System.Text.RegularExpressions;

namespace HoneyOverlays.Services;

/// <summary>
/// Watcht %TEMP% auf "vroverlay-host-&lt;source-id&gt;.size"-Dateien, die browser-host
/// nach NavigationCompleted + JS-Messung der Content-Size schreibt. Dateiinhalt: "WxH".
/// Bei Änderung wird das Event mit (sourceId, w, h) ausgelöst — Consumer (MainViewModel)
/// setzt PixelWidth/Height am passenden Source.
///
/// So entfällt für den User die manuelle Pixel-Eingabe: browser-host startet mit
/// Default-Größe, misst Content, meldet zurück, WPF setzt die exakten Werte.
/// </summary>
public sealed class BrowserHostSizeReporter
{
    private static BrowserHostSizeReporter? _instance;
    public static BrowserHostSizeReporter Instance => _instance ??= new BrowserHostSizeReporter();

    private const string FilePrefix = "vroverlay-host-";
    private const string FileSuffix = ".size";

    private FileSystemWatcher? _watcher;
    private static readonly Regex SizeRegex = new(@"^\s*(\d+)\s*x\s*(\d+)\s*$", RegexOptions.Compiled);

    /// <summary>Fired vom Watcher-Thread.</summary>
    public event EventHandler<(string SourceId, int Width, int Height)>? SizeReported;

    private BrowserHostSizeReporter() { }

    public void Start()
    {
        if (_watcher != null) return;
        var temp = Path.GetTempPath();
        try
        {
            _watcher = new FileSystemWatcher(temp, FilePrefix + "*" + FileSuffix)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            _watcher.Created += OnChanged;
            _watcher.Changed += OnChanged;
            Logger.Info($"BrowserHostSizeReporter: watching {temp}{FilePrefix}*{FileSuffix}");
        }
        catch (Exception ex)
        {
            Logger.Warn($"BrowserHostSizeReporter: cannot start watcher: {ex.Message}");
        }
    }

    public void Stop()
    {
        if (_watcher == null) return;
        _watcher.EnableRaisingEvents = false;
        _watcher.Dispose();
        _watcher = null;
    }

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        // Source-Id aus Filename extrahieren
        var name = Path.GetFileNameWithoutExtension(e.Name ?? "");
        if (!name.StartsWith(FilePrefix, StringComparison.Ordinal)) return;
        var sourceId = name.Substring(FilePrefix.Length);
        if (string.IsNullOrWhiteSpace(sourceId)) return;

        // Datei lesen (mit kurzem Retry — kann gerade noch vom Schreiber gelockt sein)
        string? content = null;
        for (int i = 0; i < 5; i++)
        {
            try
            {
                content = File.ReadAllText(e.FullPath);
                break;
            }
            catch (IOException)
            {
                System.Threading.Thread.Sleep(20);
            }
            catch (Exception ex)
            {
                Logger.Warn($"BrowserHostSizeReporter: read failed for {e.Name}: {ex.Message}");
                return;
            }
        }
        if (content == null) return;

        var m = SizeRegex.Match(content);
        if (!m.Success) return;
        if (!int.TryParse(m.Groups[1].Value, out var w)) return;
        if (!int.TryParse(m.Groups[2].Value, out var h)) return;
        if (w <= 0 || h <= 0) return;

        Logger.Info($"BrowserHostSizeReporter: size for {sourceId} = {w}x{h}");
        SizeReported?.Invoke(this, (sourceId, w, h));
    }
}
