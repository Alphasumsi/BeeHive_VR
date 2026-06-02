namespace BeeHiveVR.Services;

/// <summary>
/// Platzhalter — der alte browser-host-basierte Preview-Pfad ist tot, eine
/// neue (Electron- oder WebView2-)Preview-Lösung kommt mit der DashiesPage-
/// Renovierung. Damit DashiesPage weiter kompiliert, liefern die Methoden
/// hier konservative No-op-Werte zurück: BuildUrl erzeugt die Adapter-URL
/// (das ist editions-unabhängig), Show/GetContentSize/Close tun nichts.
/// </summary>
public sealed class IrdashiesPreviewService
{
    private static IrdashiesPreviewService? _instance;
    public static IrdashiesPreviewService Instance => _instance ??= new IrdashiesPreviewService();

    private IrdashiesPreviewService() { }

    /// <summary>Baut die Per-Widget-URL gegen den lokalen Adapter.</summary>
    public static string BuildUrl(string widgetId)
    {
        int port = IrdashiesAdapterService.Port;
        return $"http://localhost:{port}/honeyvr.html?widget={widgetId}";
    }

    public bool Show(string widgetId, int width = 420, int height = 240) => false;

    public (int Width, int Height)? GetContentSize() => null;

    public void Close() { }
}
