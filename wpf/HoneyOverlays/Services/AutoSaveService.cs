using System.Windows.Threading;
using HoneyOverlays.ViewModels;

namespace HoneyOverlays.Services;

/// <summary>
/// Verwaltet das automatische Speichern eines CarLayoutViewModels.
/// - SaveImmediate(): sofort speichern (für diskrete Änderungen wie
///   Add/Remove/Toggle/Rename/Reset)
/// - ScheduleSave(): debounced 500ms (für kontinuierliche Slider-Werte)
/// - Flush(): falls ein Save pending ist, sofort ausführen — bei App-Close
/// </summary>
public class AutoSaveService
{
    private const int DebounceMs = 500;

    private readonly DispatcherTimer _timer;
    private CarLayoutViewModel? _pendingLayout;

    public AutoSaveService()
    {
        _timer = new DispatcherTimer
        {
            Interval = System.TimeSpan.FromMilliseconds(DebounceMs)
        };
        _timer.Tick += OnTimerTick;
    }

    /// <summary>Sofort speichern — kein Debounce.</summary>
    public void SaveImmediate(CarLayoutViewModel? layout)
    {
        if (layout == null) return;

        // Falls noch ein debounced Save für DASSELBE Layout läuft → abbrechen,
        // wir speichern jetzt eh.
        if (_pendingLayout == layout)
        {
            _timer.Stop();
            _pendingLayout = null;
        }

        ConfigStore.Save(layout.ToModel());
    }

    /// <summary>Debounced speichern — 500ms nach der letzten Änderung.</summary>
    public void ScheduleSave(CarLayoutViewModel? layout)
    {
        if (layout == null) return;

        // Wenn ein Save für ein ANDERES Layout pending ist, das erst flushen
        // damit es nicht verloren geht
        if (_pendingLayout != null && _pendingLayout != layout)
        {
            _timer.Stop();
            ConfigStore.Save(_pendingLayout.ToModel());
        }

        _pendingLayout = layout;
        _timer.Stop();   // Reset
        _timer.Start();  // Neu starten
    }

    /// <summary>Falls ein Save pending ist → sofort ausführen. Beim App-Close aufrufen.</summary>
    public void Flush()
    {
        if (_pendingLayout != null)
        {
            _timer.Stop();
            ConfigStore.Save(_pendingLayout.ToModel());
            _pendingLayout = null;
        }
    }

    private void OnTimerTick(object? sender, System.EventArgs e)
    {
        _timer.Stop();
        if (_pendingLayout != null)
        {
            ConfigStore.Save(_pendingLayout.ToModel());
            _pendingLayout = null;
        }
    }
}