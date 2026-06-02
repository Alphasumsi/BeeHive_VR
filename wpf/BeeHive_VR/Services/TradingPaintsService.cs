using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ICSharpCode.SharpZipLib.BZip2;
using IRSDKSharper;

namespace BeeHiveVR.Services;

/// <summary>
/// Trading-Paints-Downloader. Portiert aus Alphasumsi/MarvinsAIRARefactored,
/// integriert in den IRacingService.
///
/// Lifecycle:
///   App.OnStartup  -> Start()   (Background-Loop + Hooks an IRacingService)
///   App.OnExit     -> Stop()
///
/// Verhalten:
///   - Background-Loop wartet auf AutoResetEvent. Bei jedem OnSessionInfo
///     (neue/geänderte Drivers) wird das Event gesetzt.
///   - Vor jeder Verarbeitung wird das aktuelle Settings.TradingPaintsEnabled
///     geprüft — Toggle wirkt sofort, kein Neustart nötig.
///   - Disconnect -> seenUserIds leeren (Reset).
/// </summary>
public sealed class TradingPaintsService
{
    private static TradingPaintsService? _instance;
    public static TradingPaintsService Instance => _instance ??= new TradingPaintsService();

    private static readonly HttpClient _http = CreateHttpClient();

    private readonly HashSet<int> _seenUserIds = new();
    private readonly object _lock = new();

    private CancellationTokenSource? _cts;
    private AutoResetEvent? _trigger;
    private Task? _loopTask;
    private bool _running;

    /// <summary>Default-Ordner: %USERPROFILE%\Documents\iRacing\paint.</summary>
    public static string DefaultFolder =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "iRacing", "paint");

    /// <summary>Effektiver Paint-Ordner (Setting-Override oder Default).</summary>
    public static string ResolveFolder()
    {
        var s = SettingsStore.Current.TradingPaintsFolder;
        return string.IsNullOrWhiteSpace(s) ? DefaultFolder : s;
    }

    // ---- Lifecycle ------------------------------------------------------

    public void Start()
    {
        if (_running) return;
        _running = true;
        _cts = new CancellationTokenSource();
        _trigger = new AutoResetEvent(false);

        IRacingService.Instance.SessionInfoUpdated += OnSessionInfoUpdated;
        IRacingService.Instance.ConnectionChanged += OnConnectionChanged;

        _loopTask = Task.Run(() => LoopAsync(_cts.Token));

        Logger.Info("TradingPaints: service started");
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;

        IRacingService.Instance.SessionInfoUpdated -= OnSessionInfoUpdated;
        IRacingService.Instance.ConnectionChanged -= OnConnectionChanged;

        try
        {
            _cts?.Cancel();
            _trigger?.Set();
            _loopTask?.Wait(1500);
        }
        catch { /* ignore */ }
        finally
        {
            _trigger?.Dispose();
            _cts?.Dispose();
            _trigger = null;
            _cts = null;
            _loopTask = null;
        }

        Logger.Info("TradingPaints: service stopped");
    }

    /// <summary>Triggert die Driver-Verarbeitung manuell (z.B. nach Toggle on).</summary>
    public void Kick()
    {
        if (IRacingService.Instance.IsConnected) _trigger?.Set();
    }

    // ---- Event-Hooks ----------------------------------------------------

    private void OnSessionInfoUpdated(object? sender, EventArgs e) => _trigger?.Set();

    private void OnConnectionChanged(object? sender, bool connected)
    {
        if (!connected)
        {
            lock (_lock) _seenUserIds.Clear();
        }
        else
        {
            _trigger?.Set();
        }
    }

    // ---- Main loop ------------------------------------------------------

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { _trigger!.WaitOne(); } catch { break; }
            if (ct.IsCancellationRequested) break;
            if (!SettingsStore.Current.TradingPaintsEnabled) continue;
            if (!IRacingService.Instance.IsConnected) continue;

            try
            {
                var sdk = IRacingService.Instance.Sdk;
                var sessionInfo = sdk?.Data?.SessionInfo;
                if (sessionInfo?.DriverInfo?.Drivers == null) continue;

                var newDrivers = new List<IRacingSdkSessionInfo.DriverInfoModel.DriverModel>();
                lock (_lock)
                {
                    foreach (var d in sessionInfo.DriverInfo.Drivers)
                    {
                        if (_seenUserIds.Add(d.UserID))
                            newDrivers.Add(d);
                    }
                }
                if (newDrivers.Count == 0) continue;

                Logger.Info($"TradingPaints: processing {newDrivers.Count} new driver(s)");
                await ProcessDriversAsync(newDrivers, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Logger.Warn($"TradingPaints: loop iteration failed: {ex.Message}");
            }
        }
    }

    // ---- Fetch + download ----------------------------------------------

    private async Task ProcessDriversAsync(
        List<IRacingSdkSessionInfo.DriverInfoModel.DriverModel> drivers,
        CancellationToken ct)
    {
        var sdk = IRacingService.Instance.Sdk;
        var sessionInfo = sdk?.Data?.SessionInfo;
        if (sessionInfo == null) return;

        var folder = ResolveFolder();
        try { Directory.CreateDirectory(folder); }
        catch (Exception ex)
        {
            Logger.Error($"TradingPaints: could not create paint folder '{folder}'", ex);
            return;
        }

        var ss = (sessionInfo.WeekendInfo?.TrackType == "super speedway") ? "ss" : string.Empty;

        var sb = new StringBuilder();
        sb.Append("list=");
        foreach (var d in drivers)
        {
            sb.Append($"{Math.Abs(d.UserID)}={d.CarPath}={d.TeamID}={d.CarNumber}={ss},");
        }
        sb.Append($"&series={sessionInfo.WeekendInfo?.SeriesID ?? 0}");
        sb.Append($"&league={sessionInfo.WeekendInfo?.LeagueID ?? 0}");
        sb.Append($"&night={sessionInfo.WeekendInfo?.WeekendOptions?.TimeOfDay ?? string.Empty}");
        sb.Append($"&team={sessionInfo.WeekendInfo?.TeamRacing ?? 0}");
        sb.Append($"&numbers=False");
        sb.Append($"&user={sessionInfo.DriverInfo?.DriverUserID ?? 0}");

        var query = sb.ToString();

        IReadOnlyList<TradingPaintsXml.Asset> assets;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://fetch.tradingpaints.gg/fetch.php")
            {
                Content = new StringContent(query, Encoding.UTF8, "application/x-www-form-urlencoded")
            };
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            await using var xmlStream = await resp.Content.ReadAsStreamAsync(ct);
            assets = TradingPaintsXml.ParseAssets(xmlStream);
        }
        catch (Exception ex)
        {
            Logger.Warn($"TradingPaints: fetch failed: {ex.Message}");
            return;
        }

        if (assets.Count == 0) return;

        var reloadUserIds = new HashSet<long>();

        foreach (var asset in assets)
        {
            if (ct.IsCancellationRequested) return;

            var carFolder = Path.Combine(folder, asset.Directory);
            try { Directory.CreateDirectory(carFolder); }
            catch (Exception ex)
            {
                Logger.Warn($"TradingPaints: could not create '{carFolder}': {ex.Message}");
                continue;
            }

            reloadUserIds.Add(asset.UserID);

            var prefix = asset.Type switch
            {
                TradingPaintsXml.AssetType.Car => "car",
                TradingPaintsXml.AssetType.CarNum => "car_num",
                TradingPaintsXml.AssetType.CarSpec => "car_spec",
                TradingPaintsXml.AssetType.CarDecal => "car_decal",
                TradingPaintsXml.AssetType.Suit => "suit",
                TradingPaintsXml.AssetType.Helmet => "helmet",
                _ => "file"
            };
            var ownerSuffix = asset.TeamId == 0 ? $"_{asset.UserID}" : $"_team_{asset.TeamId}";
            var (isBz2, ext) = GetFileExtension(asset.FileURL);
            if (!string.IsNullOrWhiteSpace(asset.Ext)) ext = $"_{asset.Ext}{ext}";

            var finalName = $"{prefix}{ownerSuffix}{ext}";
            var finalPath = Path.Combine(folder, asset.Directory, finalName);
            var tmpPath = finalPath + ".part";

            try
            {
                Logger.Info($"TradingPaints: downloading {asset.FileURL}");
                await DownloadAsync(asset.FileURL, tmpPath, ct);

                if (isBz2)
                {
                    if (!TryDecompressBZip2(tmpPath, finalPath))
                    {
                        try { File.Delete(tmpPath); } catch { }
                        continue;
                    }
                    try { File.Delete(tmpPath); } catch { }
                }
                else
                {
                    if (File.Exists(finalPath)) File.Delete(finalPath);
                    File.Move(tmpPath, finalPath);
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                Logger.Warn($"TradingPaints: download failed for {asset.FileURL}: {ex.Message}");
                try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
            }
        }

        // ReloadTextures pro betroffenem User
        if (reloadUserIds.Count == 0) return;
        try
        {
            var driversNow = sdk?.Data?.SessionInfo?.DriverInfo?.Drivers;
            if (driversNow == null) return;
            foreach (var uid in reloadUserIds)
            {
                foreach (var d in driversNow)
                {
                    if (d.UserID == uid)
                    {
                        sdk!.ReloadTextures(IRacingSdkEnum.ReloadTexturesMode.CarIdx, d.CarIdx);
                        Logger.Info($"TradingPaints: reload textures for {d.UserName} (carIdx={d.CarIdx})");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"TradingPaints: ReloadTextures failed: {ex.Message}");
        }
    }

    // ---- Cleanup --------------------------------------------------------

    public sealed class CleanupResult
    {
        public int FilesDeleted { get; set; }
        public long BytesDeleted { get; set; }
        public int FoldersDeleted { get; set; }
        public int Errors { get; set; }
    }

    /// <summary>
    /// Löscht Dateien im Paint-Ordner deren LastWriteTime älter als <paramref name="olderThanDays"/> ist.
    /// Leere Unterordner werden danach mit aufgeräumt. Liefert eine Statistik.
    /// </summary>
    public CleanupResult Cleanup(int olderThanDays)
    {
        var result = new CleanupResult();
        var root = ResolveFolder();
        if (!Directory.Exists(root))
        {
            Logger.Info($"TradingPaints: cleanup skipped — folder does not exist ({root})");
            return result;
        }
        if (olderThanDays <= 0)
        {
            Logger.Warn($"TradingPaints: cleanup skipped — invalid age '{olderThanDays}' days");
            return result;
        }

        var cutoff = DateTime.Now.AddDays(-olderThanDays);

        foreach (var file in EnumerateFilesSafe(root))
        {
            try
            {
                var fi = new FileInfo(file);
                if (!fi.Exists) continue;
                if (fi.LastWriteTime >= cutoff) continue;
                var size = fi.Length;
                fi.Delete();
                result.FilesDeleted++;
                result.BytesDeleted += size;
            }
            catch (Exception ex)
            {
                result.Errors++;
                Logger.Warn($"TradingPaints: delete failed for '{file}': {ex.Message}");
            }
        }

        // leere Unterordner abräumen (root selbst bleibt)
        foreach (var dir in EnumerateDirsSafeBottomUp(root))
        {
            try
            {
                if (string.Equals(Path.GetFullPath(dir), Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase))
                    continue;
                if (Directory.GetFileSystemEntries(dir).Length == 0)
                {
                    Directory.Delete(dir);
                    result.FoldersDeleted++;
                }
            }
            catch (Exception ex)
            {
                result.Errors++;
                Logger.Warn($"TradingPaints: rmdir failed for '{dir}': {ex.Message}");
            }
        }

        Logger.Info(
            $"TradingPaints: cleanup done (root='{root}', cutoff={cutoff:yyyy-MM-dd}) — " +
            $"{result.FilesDeleted} files / {result.BytesDeleted / 1024} KB / " +
            $"{result.FoldersDeleted} folders / {result.Errors} errors");
        return result;
    }

    private static IEnumerable<string> EnumerateFilesSafe(string root)
    {
        try { return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories); }
        catch { return Array.Empty<string>(); }
    }

    private static IEnumerable<string> EnumerateDirsSafeBottomUp(string root)
    {
        List<string> list;
        try { list = new List<string>(Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)); }
        catch { return Array.Empty<string>(); }
        list.Sort((a, b) => b.Length.CompareTo(a.Length));
        return list;
    }

    // ---- HTTP helpers ---------------------------------------------------

    private static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            MaxAutomaticRedirections = 10,
            UseCookies = true
        };
        var http = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("BeeHiveVR/0.7 (+https://github.com/)");
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
        http.DefaultRequestHeaders.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
        http.DefaultRequestHeaders.AcceptEncoding.Add(new StringWithQualityHeaderValue("deflate"));
        return http;
    }

    private static (bool isBz2, string finalExt) GetFileExtension(string fileUrl)
    {
        var path = new Uri(fileUrl, UriKind.Absolute).AbsolutePath.ToLowerInvariant();
        if (path.EndsWith(".tga.bz2", StringComparison.Ordinal)) return (true, ".tga");
        if (path.EndsWith(".tga", StringComparison.Ordinal)) return (false, ".tga");
        if (path.EndsWith(".mip", StringComparison.Ordinal)) return (false, ".mip");
        var lastDot = path.LastIndexOf('.');
        return (false, lastDot >= 0 ? path[lastDot..] : ".tga");
    }

    private static async Task DownloadAsync(string url, string destPath, CancellationToken ct)
    {
        var delayMs = 400;
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

            if ((int)resp.StatusCode == 429)
            {
                var retryAfter = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromMilliseconds(delayMs);
                await Task.Delay(retryAfter, ct);
                delayMs = Math.Min(delayMs * 2, 8000);
                continue;
            }
            if (!resp.IsSuccessStatusCode)
            {
                if ((int)resp.StatusCode >= 500 && attempt < 5)
                {
                    await Task.Delay(delayMs, ct);
                    delayMs = Math.Min(delayMs * 2, 8000);
                    continue;
                }
                resp.EnsureSuccessStatusCode();
            }

            var maxKbps = SettingsStore.Current.TradingPaintsMaxDownloadKbps;
            await using var content = await resp.Content.ReadAsStreamAsync(ct);
            await using var fs = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true);
            if (maxKbps <= 0)
                await content.CopyToAsync(fs, ct);
            else
                await ThrottledCopyAsync(content, fs, maxKbps * 1024, ct);
            return;
        }
        throw new HttpRequestException($"Failed to download after retries: {url}");
    }

    private static async Task ThrottledCopyAsync(Stream src, Stream dst, int bytesPerSecond, CancellationToken ct)
    {
        var buf = new byte[16 * 1024];
        long total = 0;
        var t0 = Environment.TickCount64;
        while (true)
        {
            var n = await src.ReadAsync(buf.AsMemory(), ct);
            if (n == 0) break;
            await dst.WriteAsync(buf.AsMemory(0, n), ct);
            total += n;
            var expectedMs = total * 1000L / bytesPerSecond;
            var actualMs = Environment.TickCount64 - t0;
            if (actualMs < expectedMs)
                await Task.Delay((int)(expectedMs - actualMs), ct);
        }
    }

    private static bool TryDecompressBZip2(string bz2Path, string tgaOutPath)
    {
        var tmp = tgaOutPath + ".part2";
        try
        {
            using (var input = File.OpenRead(bz2Path))
            using (var output = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                BZip2.Decompress(input, output, true);
            }
            if (File.Exists(tgaOutPath)) File.Delete(tgaOutPath);
            File.Move(tmp, tgaOutPath);
            return true;
        }
        catch
        {
            try { File.Delete(tmp); } catch { }
            return false;
        }
    }
}
