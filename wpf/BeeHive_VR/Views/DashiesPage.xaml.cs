using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BeeHiveVR.Models;
using BeeHiveVR.Services;
using BeeHiveVR.ViewModels;

namespace BeeHiveVR.Views;

/// <summary>
/// Dashies-Tab: Sidebar mit kuratierter Overlay-Liste, rechts ein pro Overlay
/// adaptiertes Settings-Panel (Toggle-Switches, einklappbare Sub-Sektionen,
/// drag-verschiebbare Reihenfolge-Listen). BeeHive-VR-Sachen (Opacity/Visibility/
/// Position) bewusst NICHT enthalten — die macht BeeHive VR.
///
/// Bisher umgesetzt: Input (Display/Options) und Relative (Display/Options/
/// Header/Footer/Styling). Die Verdrahtung ist widget-neutral: jedes Control
/// trägt seinen JSON-Pfad als Tag, generische Handler patchen die Config des
/// gerade gewählten Overlays (_activeWidget) + Live-Broadcast.
/// </summary>
public partial class DashiesPage : UserControl
{
    /// <summary>Ein Eintrag einer Reihenfolge-Liste (Name + ird-Id + ein/aus + Drag-Zustand).</summary>
    public sealed class DisplayItem : INotifyPropertyChanged
    {
        public required string Name { get; set; }
        public required string Id { get; set; }

        private bool _enabled = true;
        public bool Enabled
        {
            get => _enabled;
            set { if (_enabled != value) { _enabled = value; OnChanged(); } }
        }

        private bool _isDragging;
        /// <summary>Live an die Row-Optik gebunden → Vorschau-Hervorhebung beim Ziehen.</summary>
        public bool IsDragging
        {
            get => _isDragging;
            set { if (_isDragging != value) { _isDragging = value; OnChanged(); } }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnChanged([CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    // ---- Default-Element-Sätze pro Liste --------------------------------------

    private static readonly (string Name, string Id)[] InputDisplay =
        { ("Trace", "trace"), ("Bar", "bar"), ("Gear", "gear"), ("ABS", "abs"), ("Steer", "steer") };

    // Reihenfolge wie ird's defaultDashboard (driverTag vor teamName). positionChange
    // bleibt — wie in ird's Settings-UI — bewusst draußen (nicht editierbare Spalte).
    private static readonly (string Name, string Id)[] RelativeDisplay =
    {
        ("Position", "position"), ("Car Number", "carNumber"), ("Country Flags", "countryFlags"),
        ("Driver Name", "driverName"), ("Driver Tag", "driverTag"), ("Team Name", "teamName"),
        ("Pit Status", "pitStatus"), ("Car Manufacturer", "carManufacturer"), ("Driver Badge", "badge"),
        ("iRating Change", "iratingChange"), ("Relative", "delta"), ("Best Time", "fastestTime"),
        ("Last Time", "lastTime"), ("Tire Compound", "compound"), ("Lap Delta", "lapTimeDeltas"),
        ("Driven Laps", "drivenLaps"),
    };

    private static readonly (string Name, string Id)[] SessionBar =
    {
        ("Session Name", "sessionName"), ("Session Time", "sessionTime"), ("Session Laps", "sessionLaps"),
        ("Incident Count", "incidentCount"), ("Brake Bias", "brakeBias"), ("Local Time", "localTime"),
        ("Session Clock Time", "sessionClockTime"), ("Track Wetness", "trackWetness"),
        ("Precipitation", "precipitation"), ("Air Temperature", "airTemperature"),
        ("Track Temperature", "trackTemperature"), ("Wind", "wind"), ("Track Name", "trackName"),
    };

    // Standings-Spalten (ird sortableSettings-Order) + Driven Laps. gap/interval/avgLapTime
    // statt nur delta. positionChange ist hier eine editierbare Spalte.
    private static readonly (string Name, string Id)[] StandingsDisplay =
    {
        ("Position", "position"), ("Car Number", "carNumber"), ("Country Flags", "countryFlags"),
        ("Driver Name", "driverName"), ("Team Name", "teamName"), ("Pit Status", "pitStatus"),
        ("Car Manufacturer", "carManufacturer"), ("Driver Tag", "driverTag"), ("Driver Badge", "badge"),
        ("iRating Change", "iratingChange"), ("Position Change", "positionChange"),
        ("Gap", "gap"), ("Interval", "interval"), ("Best Time", "fastestTime"),
        ("Last Time", "lastTime"), ("Tire Compound", "compound"), ("Lap Time Deltas", "lapTimeDeltas"),
        ("Avg Lap Time", "avgLapTime"), ("Driven Laps", "drivenLaps"),
    };

    private readonly ObservableCollection<DisplayItem> _inputDisplay = new();
    private readonly ObservableCollection<DisplayItem> _relDisplay = new();
    private readonly ObservableCollection<DisplayItem> _relHeader = new();
    private readonly ObservableCollection<DisplayItem> _relFooter = new();
    private readonly ObservableCollection<DisplayItem> _stdDisplay = new();
    private readonly ObservableCollection<DisplayItem> _stdHeader = new();
    private readonly ObservableCollection<DisplayItem> _stdFooter = new();

    // Fuel-Sektionen (= layoutTree-Boxen). „Display order" ist hier das widgets-Array
    // in layoutTree.children[0]; Sektion drin = sichtbar. Grid/Graph bewusst raus.
    private static readonly (string Name, string Id)[] FuelDisplay =
    {
        ("Header", "fuelHeader"), ("Fuel Gauge", "fuelGauge"), ("Pit Scenarios", "fuelScenarios"),
        ("Confidence Messages", "fuelConfidence"), ("Time Until Empty", "fuelTimeEmpty"),
        ("Economy Predict", "fuelEconomyPredict"),
    };
    private readonly ObservableCollection<DisplayItem> _fuelDisplay = new();
    private const string FuelLayoutPrefix = "@fuelLayout";

    // Pitlane-Helper-Sektionen (Bars + Badges + Inputs). Display-Order steuert
    // beides: Reihenfolge im Widget UND Sichtbarkeit per Section-Toggle.
    private static readonly (string Name, string Id)[] PitlaneHelperDisplay =
    {
        ("Speed Summary", "speedSummary"), ("Speed Bar", "speedBar"),
        ("Progress Bar", "progressBar"), ("Pit Exit Inputs", "pitExitInputs"),
        ("At Pitbox Badge", "atPitbox"), ("Limiter Warning", "limiterWarning"),
        ("Early Pitbox Warning", "earlyPitbox"), ("Pitlane Traffic", "pitlaneTraffic"),
    };
    private readonly ObservableCollection<DisplayItem> _plhDisplay = new();

    // ---- Aktiver Zustand (vom gewählten Overlay gesetzt) ----------------------

    private string? _activeWidget;          // "input" / "relative" / null
    private DependencyObject? _activeRoot;   // InputPanel / RelativePanel (für TaggedControls/Load)
    private TextBox? _activeSizeW, _activeSizeH;

    private ObservableCollection<DisplayItem> ActiveMainDisplay => _activeWidget switch
    {
        "relative" => _relDisplay,
        "standings" => _stdDisplay,
        "fuel" => _fuelDisplay,
        "pitlanehelper" => _plhDisplay,
        _ => _inputDisplay,
    };

    // ---- Drag-Zustand ---------------------------------------------------------

    private Point _dragStartPoint;
    private DisplayItem? _dragItem;
    private ItemsControl? _dragList;

    private bool _previewOpen;
    private string? _previewWidgetId;
    private bool _ready;      // true nach Konstruktor → unterdrückt Init-Events
    private bool _loading;    // true während Load → unterdrückt Patch-Events

    public DashiesPage()
    {
        InitializeComponent();

        FillDefaults(_inputDisplay, InputDisplay);
        FillDefaults(_relDisplay, RelativeDisplay);
        FillDefaults(_relHeader, SessionBar);
        FillDefaults(_relFooter, SessionBar);
        FillDefaults(_stdDisplay, StandingsDisplay);
        FillDefaults(_stdHeader, SessionBar);
        FillDefaults(_stdFooter, SessionBar);
        FillDefaults(_fuelDisplay, FuelDisplay);
        FillDefaults(_plhDisplay, PitlaneHelperDisplay);

        DisplayOrderList.ItemsSource = _inputDisplay;
        RelDisplayList.ItemsSource = _relDisplay;
        RelHeaderList.ItemsSource = _relHeader;
        RelFooterList.ItemsSource = _relFooter;
        StdDisplayList.ItemsSource = _stdDisplay;
        StdHeaderList.ItemsSource = _stdHeader;
        StdFooterList.ItemsSource = _stdFooter;
        FuelDisplayList.ItemsSource = _fuelDisplay;
        PlhDisplayList.ItemsSource = _plhDisplay;

        // Default-Tabs erst nach dem Aufbau setzen (benannte Elemente existieren).
        TabDisplay.IsChecked = true;
        RelTabDisplay.IsChecked = true;
        MapTabTrack.IsChecked = true;
        StdTabDisplay.IsChecked = true;
        FuelTabDisplay.IsChecked = true;
        BspTabDisplay.IsChecked = true;
        PlhTabDisplay.IsChecked = true;

        IsVisibleChanged += DashiesPage_IsVisibleChanged;
        _ready = true;
    }

    private static void FillDefaults(ObservableCollection<DisplayItem> coll, (string Name, string Id)[] defs)
    {
        coll.Clear();
        foreach (var (name, id) in defs)
            coll.Add(new DisplayItem { Name = name, Id = id, Enabled = true });
    }

    /// <summary>Sidebar-Anzeigename → irdashies-Widget-Id.</summary>
    private static string? WidgetId(string? name) => name switch
    {
        "Input" => "input",
        "Relative" => "relative",
        "Standings" => "standings",
        "Track Map" => "map",
        "Fuel Calculator" => "fuel",
        "Blind Spot Monitor" => "blindspotmonitor",
        "Pitlane Helper" => "pitlanehelper",
        _ => null,
    };

    private void DashiesPage_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible) return;
        _previewOpen = false;
        DashiesPreviewService.Instance.Close();
        IrdashiesAdapterService.Instance.SetMock(false);
        PreviewButton.Content = "Preview";
    }

    private string? SelectedOverlay =>
        (OverlayList.SelectedItem as ListBoxItem)?.Content as string;

    private void OverlayList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var name = SelectedOverlay;
        PanelTitle.Text = name ?? "Select an overlay";
        StatusText.Text = string.Empty;

        bool isInput = name == "Input";
        bool isRel = name == "Relative";
        bool isMap = name == "Track Map";
        bool isStd = name == "Standings";
        bool isFuel = name == "Fuel Calculator";
        bool isBsp = name == "Blind Spot Monitor";
        bool isPlh = name == "Pitlane Helper";
        InputPanel.Visibility = isInput ? Visibility.Visible : Visibility.Collapsed;
        RelativePanel.Visibility = isRel ? Visibility.Visible : Visibility.Collapsed;
        TrackMapPanel.Visibility = isMap ? Visibility.Visible : Visibility.Collapsed;
        StandingsPanel.Visibility = isStd ? Visibility.Visible : Visibility.Collapsed;
        FuelPanel.Visibility = isFuel ? Visibility.Visible : Visibility.Collapsed;
        BlindSpotPanel.Visibility = isBsp ? Visibility.Visible : Visibility.Collapsed;
        PitlaneHelperPanel.Visibility = isPlh ? Visibility.Visible : Visibility.Collapsed;
        GenericPanel.Visibility = (isInput || isRel || isMap || isStd || isFuel || isBsp || isPlh) ? Visibility.Collapsed : Visibility.Visible;

        if (isInput)
        {
            _activeWidget = "input"; _activeRoot = InputPanel;
            _activeSizeW = SizeWidth; _activeSizeH = SizeHeight;
            LoadConfig("input");
        }
        else if (isRel)
        {
            _activeWidget = "relative"; _activeRoot = RelativePanel;
            _activeSizeW = RelSizeWidth; _activeSizeH = RelSizeHeight;
            LoadConfig("relative");
        }
        else if (isMap)
        {
            _activeWidget = "map"; _activeRoot = TrackMapPanel;
            _activeSizeW = MapSizeWidth; _activeSizeH = MapSizeHeight;
            LoadConfig("map");
        }
        else if (isStd)
        {
            _activeWidget = "standings"; _activeRoot = StandingsPanel;
            _activeSizeW = StdSizeWidth; _activeSizeH = StdSizeHeight;
            LoadConfig("standings");
        }
        else if (isFuel)
        {
            _activeWidget = "fuel"; _activeRoot = FuelPanel;
            _activeSizeW = FuelSizeWidth; _activeSizeH = FuelSizeHeight;
            LoadConfig("fuel");
        }
        else if (isBsp)
        {
            _activeWidget = "blindspotmonitor"; _activeRoot = BlindSpotPanel;
            _activeSizeW = BspSizeWidth; _activeSizeH = BspSizeHeight;
            LoadConfig("blindspotmonitor");
        }
        else if (isPlh)
        {
            _activeWidget = "pitlanehelper"; _activeRoot = PitlaneHelperPanel;
            _activeSizeW = PlhSizeWidth; _activeSizeH = PlhSizeHeight;
            LoadConfig("pitlanehelper");
        }
        else
        {
            _activeWidget = null; _activeRoot = null;
            _activeSizeW = _activeSizeH = null;
        }
    }

    // ---- Laden: Config → getaggte Controls + Reihenfolge-Listen ---------------

    private void LoadConfig(string widget)
    {
        var cfg = IrdashiesConfigStore.Instance.GetWidgetConfig(widget);
        if (cfg == null || _activeRoot == null) return;

        _loading = true;
        try
        {
            // Format (irdashies-Template-Bridge-Feld config.honeySize) → Felder.
            if (_activeSizeW != null && _activeSizeH != null)
            {
                _activeSizeW.Text = ((int)(AsDouble(GetByPath(cfg, "honeySize.width")) ?? 420)).ToString();
                _activeSizeH.Text = ((int)(AsDouble(GetByPath(cfg, "honeySize.height")) ?? 240)).ToString();
            }

            // Alle getaggten Controls im aktiven Panel (über alle Tabs) füllen.
            foreach (var fe in TaggedControls(_activeRoot))
            {
                var node = GetByPath(cfg, (string)fe.Tag);
                switch (fe)
                {
                    case ComboBox combo:
                        var key = AsString(node) ?? AsDouble(node)?.ToString(CultureInfo.InvariantCulture);
                        if (key != null)
                            foreach (var it in combo.Items)
                                if (it is ComboBoxItem ci && (ci.Tag as string) == key)
                                { combo.SelectedItem = ci; break; }
                        break;
                    case CheckBox cb:
                        var b = AsBool(node);
                        if (b.HasValue) cb.IsChecked = b.Value;
                        break;
                    case Slider sl:
                        var d = AsDouble(node);
                        if (d.HasValue) sl.Value = d.Value;
                        break;
                }
            }

            // Reihenfolge-Listen aus der Config (main + ggf. header/footer).
            if (widget == "input")
                LoadList(cfg, _inputDisplay, InputDisplay, "");
            else if (widget == "relative")
            {
                LoadList(cfg, _relDisplay, RelativeDisplay, "");
                LoadList(cfg, _relHeader, SessionBar, "headerBar");
                LoadList(cfg, _relFooter, SessionBar, "footerBar");
            }
            else if (widget == "standings")
            {
                LoadList(cfg, _stdDisplay, StandingsDisplay, "");
                LoadList(cfg, _stdHeader, SessionBar, "headerBar");
                LoadList(cfg, _stdFooter, SessionBar, "footerBar");
            }
            else if (widget == "fuel")
            {
                LoadList(cfg, _fuelDisplay, FuelDisplay, FuelLayoutPrefix);
            }
            else if (widget == "pitlanehelper")
            {
                LoadList(cfg, _plhDisplay, PitlaneHelperDisplay, "");
            }
        }
        finally { _loading = false; }
    }

    /// <summary>Baut eine Reihenfolge-Liste aus &lt;prefix&gt;displayOrder + &lt;prefix&gt;&lt;id&gt;.enabled.</summary>
    private static void LoadList(JsonObject cfg, ObservableCollection<DisplayItem> coll,
        (string Name, string Id)[] defs, string prefix)
    {
        if (prefix == FuelLayoutPrefix) { LoadFuelLayout(cfg, coll, defs); return; }

        var scope = prefix == "" ? cfg : GetByPath(cfg, prefix) as JsonObject;
        var items = new List<DisplayItem>();

        if (scope?["displayOrder"] is JsonArray order)
        {
            foreach (var n in order)
            {
                var id = AsString(n);
                var def = defs.FirstOrDefault(x => x.Id == id);
                if (def.Id != null)
                    items.Add(new DisplayItem
                    {
                        Name = def.Name, Id = def.Id,
                        Enabled = AsBool(GetByPath(scope, def.Id + ".enabled")) ?? true,
                    });
            }
        }
        // Fehlende Elemente ergänzen (falls displayOrder unvollständig/leer).
        foreach (var (name, id) in defs)
            if (!items.Any(i => i.Id == id))
                items.Add(new DisplayItem
                {
                    Name = name, Id = id,
                    Enabled = scope != null ? (AsBool(GetByPath(scope, id + ".enabled")) ?? true) : true,
                });

        coll.Clear();
        foreach (var it in items) coll.Add(it);
    }

    // ---- Patchen: Control-Änderung → Config + Live-Broadcast -------------------

    private void PatchActive(Action<JsonObject> patch)
    {
        if (!_ready || _loading || _activeWidget == null) return;
        IrdashiesConfigStore.Instance.PatchWidgetConfig(_activeWidget, patch);
        IrdashiesAdapterService.Instance.BroadcastDashboardUpdated();
    }

    private void Toggle_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not string path) return;
        bool v = (sender as CheckBox)?.IsChecked == true;
        PatchActive(cfg => SetByPath(cfg, path, JsonValue.Create(v)));

        // Section-Enable (z.B. "trace.enabled"/"driverName.enabled") ↔ Main-Display-Item.
        // Nur top-level <id>.enabled syncen (kein "titleBar.progressBar.enabled").
        if (path.EndsWith(".enabled"))
        {
            var id = path[..^".enabled".Length];
            if (!id.Contains('.'))
            {
                var item = ActiveMainDisplay.FirstOrDefault(i => i.Id == id);
                if (item != null) item.Enabled = v;
            }
        }
    }

    private void Slider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not string path) return;
        int v = (int)Math.Round(e.NewValue);
        PatchActive(cfg => SetByPath(cfg, path, JsonValue.Create(v)));
    }

    private void Combo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox combo || combo.Tag is not string path) return;
        var val = (combo.SelectedItem as ComboBoxItem)?.Tag as string;
        if (val == null) return;
        // Numerische Werte (buffer/precision/numLaps) als Zahl schreiben, sonst String.
        JsonNode node = int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out var iv)
            ? JsonValue.Create(iv) : JsonValue.Create(val);
        PatchActive(cfg => SetByPath(cfg, path, node));
    }

    // ---- Reorder-Listen: Toggle + Drag (alle Listen teilen sich die Handler) ---

    /// <summary>(Collection, prefix) einer Liste auflösen. Tag = prefix ("" / "headerBar" / "footerBar").</summary>
    private static (ObservableCollection<DisplayItem>?, string) ResolveList(ItemsControl? list)
        => (list?.ItemsSource as ObservableCollection<DisplayItem>, (list?.Tag as string) ?? "");

    /// <summary>Schreibt Reihenfolge + per-Item-Enabled einer Liste in die Config (+ Broadcast).</summary>
    private void ApplyList(ObservableCollection<DisplayItem> items, string prefix)
    {
        if (prefix == FuelLayoutPrefix) { ApplyFuelLayout(items); return; }

        var snapshot = items.Select(i => (i.Id, i.Enabled)).ToList();
        PatchActive(cfg =>
        {
            JsonObject target = cfg;
            if (prefix != "")
            {
                if (cfg[prefix] is not JsonObject o) { o = new JsonObject(); cfg[prefix] = o; }
                target = o;
            }
            var arr = new JsonArray();
            foreach (var (id, en) in snapshot)
            {
                arr.Add(JsonValue.Create(id));
                SetByPath(target, id + ".enabled", JsonValue.Create(en));
            }
            target["displayOrder"] = arr;
        });
    }

    // ---- Fuel: „Display order" = layoutTree.children[0].widgets (Sektion drin = sichtbar) ----

    /// <summary>Schreibt die aktivierten Fuel-Sektionen als einspaltigen layoutTree.</summary>
    private void ApplyFuelLayout(ObservableCollection<DisplayItem> items)
    {
        var ids = items.Where(i => i.Enabled).Select(i => i.Id).ToList();
        PatchActive(cfg =>
        {
            var widgets = new JsonArray();
            foreach (var id in ids) widgets.Add(JsonValue.Create(id));
            cfg["layoutTree"] = new JsonObject
            {
                ["id"] = "root-fuel-honey",
                ["type"] = "split",
                ["direction"] = "col",
                ["children"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["id"] = "box-1",
                        ["type"] = "box",
                        ["direction"] = "col",
                        ["widgets"] = widgets,
                    },
                },
            };
        });
    }

    /// <summary>Baut die Fuel-Sektionsliste aus layoutTree.children[0].widgets. Enthält der
    /// Tree keine unserer kuratierten Sektionen (ird-Default), werden alle 7 an gezeigt.</summary>
    private static void LoadFuelLayout(JsonObject cfg, ObservableCollection<DisplayItem> coll,
        (string Name, string Id)[] defs)
    {
        var present = new List<string>();
        if (cfg["layoutTree"] is JsonObject lt && lt["children"] is JsonArray ch && ch.Count > 0
            && ch[0] is JsonObject box && box["widgets"] is JsonArray ws)
            foreach (var n in ws) { var s = AsString(n); if (s != null) present.Add(s); }

        var curated = present.Where(id => defs.Any(d => d.Id == id)).ToList();
        var items = new List<DisplayItem>();
        if (curated.Count == 0)
        {
            // Unkonfiguriert → alle Sektionen an, Default-Reihenfolge.
            foreach (var (name, id) in defs)
                items.Add(new DisplayItem { Name = name, Id = id, Enabled = true });
        }
        else
        {
            foreach (var id in curated)
            {
                var def = defs.FirstOrDefault(d => d.Id == id);
                if (def.Id != null)
                    items.Add(new DisplayItem { Name = def.Name, Id = def.Id, Enabled = true });
            }
            foreach (var (name, id) in defs)
                if (!items.Any(i => i.Id == id))
                    items.Add(new DisplayItem { Name = name, Id = id, Enabled = false });
        }

        coll.Clear();
        foreach (var it in items) coll.Add(it);
    }

    private void DisplayToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox cb || cb.DataContext is not DisplayItem di) return;
        var list = FindAncestorItemsControl(cb);
        var (coll, prefix) = ResolveList(list);
        if (coll == null) return;

        di.Enabled = cb.IsChecked == true;
        ApplyList(coll, prefix);

        // Nur Main-Display syncs mit der zugehörigen Options-Section.
        if (prefix == "")
        {
            var opt = OptionsEnableToggle(di.Id);
            if (opt != null) opt.IsChecked = di.Enabled;
        }
    }

    private void DisplayOrderList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStartPoint = e.GetPosition(null);
        _dragList = sender as ItemsControl;
        _dragItem = (e.OriginalSource is FrameworkElement { Tag: "drag" })
            ? FindDisplayItem(e.OriginalSource)
            : null;
    }

    private void DisplayOrderList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragItem == null || _dragList == null) return;

        var pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _dragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _dragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        var item = _dragItem;
        var list = _dragList;
        _dragItem = null;

        item.IsDragging = true;
        try { DragDrop.DoDragDrop(list, item, DragDropEffects.Move); }
        finally { item.IsDragging = false; }
    }

    private void DisplayOrderList_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(DisplayItem)) is not DisplayItem dragged)
        {
            e.Effects = DragDropEffects.None;
            return;
        }
        e.Effects = DragDropEffects.Move;

        var (coll, prefix) = ResolveList(sender as ItemsControl);
        if (coll == null) return;

        var target = FindDisplayItem(e.OriginalSource);
        if (target == null || ReferenceEquals(target, dragged)) return;

        int oldIndex = coll.IndexOf(dragged);
        int newIndex = coll.IndexOf(target);
        if (oldIndex < 0 || newIndex < 0 || oldIndex == newIndex) return;
        coll.Move(oldIndex, newIndex);
        ApplyList(coll, prefix);
    }

    private void DisplayOrderList_Drop(object sender, DragEventArgs e)
    {
        // Reihenfolge wurde bereits live in DragOver übernommen.
    }

    private static DisplayItem? FindDisplayItem(object? originalSource)
    {
        var d = originalSource as DependencyObject;
        while (d != null)
        {
            if (d is FrameworkElement { DataContext: DisplayItem di }) return di;
            d = VisualTreeHelper.GetParent(d);
        }
        return null;
    }

    private static ItemsControl? FindAncestorItemsControl(DependencyObject? d)
    {
        while (d != null)
        {
            if (d is ItemsControl ic) return ic;
            d = VisualTreeHelper.GetParent(d);
        }
        return null;
    }

    /// <summary>Findet den Options-Enable-Toggle einer Main-Section (Tag "&lt;id&gt;.enabled").</summary>
    private CheckBox? OptionsEnableToggle(string id)
    {
        if (_activeRoot == null) return null;
        foreach (var fe in TaggedControls(_activeRoot))
            if (fe is CheckBox cb && (cb.Tag as string) == id + ".enabled")
                return cb;
        return null;
    }

    // ---- Tab-Umschaltung ------------------------------------------------------

    private void InputTab_Checked(object sender, RoutedEventArgs e)
    {
        if (DisplayTab == null || OptionsTab == null) return;
        DisplayTab.Visibility = TabDisplay.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        OptionsTab.Visibility = TabOptions.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RelTab_Checked(object sender, RoutedEventArgs e)
    {
        if (RelDisplayTab == null) return; // während InitializeComponent
        RelDisplayTab.Visibility = RelTabDisplay.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        RelOptionsTab.Visibility = RelTabOptions.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        RelHeaderTab.Visibility = RelTabHeader.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        RelFooterTab.Visibility = RelTabFooter.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        RelStylingTab.Visibility = RelTabStyling.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void MapTab_Checked(object sender, RoutedEventArgs e)
    {
        if (MapTrackTab == null) return; // während InitializeComponent
        MapTrackTab.Visibility = MapTabTrack.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        MapDriversTab.Visibility = MapTabDrivers.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        MapStylingTab.Visibility = MapTabStyling.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        MapFormatTab.Visibility = MapTabFormat.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void StdTab_Checked(object sender, RoutedEventArgs e)
    {
        if (StdDisplayTab == null) return; // während InitializeComponent
        StdDisplayTab.Visibility = StdTabDisplay.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        StdOptionsTab.Visibility = StdTabOptions.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        StdHeaderTab.Visibility = StdTabHeader.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        StdFooterTab.Visibility = StdTabFooter.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        StdStylingTab.Visibility = StdTabStyling.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void FuelTab_Checked(object sender, RoutedEventArgs e)
    {
        if (FuelDisplayTab == null) return; // während InitializeComponent
        FuelDisplayTab.Visibility = FuelTabDisplay.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        FuelOptionsTab.Visibility = FuelTabOptions.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BspTab_Checked(object sender, RoutedEventArgs e)
    {
        if (BspDisplayTab == null) return; // während InitializeComponent
        BspDisplayTab.Visibility = BspTabDisplay.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        BspOptionsTab.Visibility = BspTabOptions.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void PlhTab_Checked(object sender, RoutedEventArgs e)
    {
        if (PlhDisplayTab == null) return; // während InitializeComponent
        PlhDisplayTab.Visibility = PlhTabDisplay.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PlhOptionsTab.Visibility = PlhTabOptions.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---- Reset-Buttons --------------------------------------------------------

    private void ResetDisplayOrder_Click(object sender, RoutedEventArgs e)
        => ResetOrder(_inputDisplay, InputDisplay, "");

    private void RelResetDisplay_Click(object sender, RoutedEventArgs e)
        => ResetOrder(_relDisplay, RelativeDisplay, "");

    private void RelResetHeader_Click(object sender, RoutedEventArgs e)
        => ResetOrder(_relHeader, SessionBar, "headerBar");

    private void RelResetFooter_Click(object sender, RoutedEventArgs e)
        => ResetOrder(_relFooter, SessionBar, "footerBar");

    private void StdResetDisplay_Click(object sender, RoutedEventArgs e)
        => ResetOrder(_stdDisplay, StandingsDisplay, "");

    private void StdResetHeader_Click(object sender, RoutedEventArgs e)
        => ResetOrder(_stdHeader, SessionBar, "headerBar");

    private void StdResetFooter_Click(object sender, RoutedEventArgs e)
        => ResetOrder(_stdFooter, SessionBar, "footerBar");

    private void PlhResetDisplay_Click(object sender, RoutedEventArgs e)
        => ResetOrder(_plhDisplay, PitlaneHelperDisplay, "");

    private void FuelResetDisplay_Click(object sender, RoutedEventArgs e)
        => ResetOrder(_fuelDisplay, FuelDisplay, FuelLayoutPrefix);

    /// <summary>Setzt nur die Reihenfolge auf Default zurück, behält die Enable-Zustände.</summary>
    private void ResetOrder(ObservableCollection<DisplayItem> coll,
        (string Name, string Id)[] defs, string prefix)
    {
        var enabledById = coll.ToDictionary(i => i.Id, i => i.Enabled);
        coll.Clear();
        foreach (var (name, id) in defs)
            coll.Add(new DisplayItem
            {
                Name = name, Id = id,
                Enabled = enabledById.TryGetValue(id, out var en) ? en : true,
            });
        ApplyList(coll, prefix);
    }

    // ---- Format-Felder --------------------------------------------------------

    private (int W, int H) ReadFormat()
    {
        int w = int.TryParse(_activeSizeW?.Text?.Trim(), out var pw) && pw > 0 ? pw : 420;
        int h = int.TryParse(_activeSizeH?.Text?.Trim(), out var ph) && ph > 0 ? ph : 240;
        return (w, h);
    }

    /// <summary>Default-Format des aktiven Overlays.</summary>
    private (int W, int H) DefaultSize() => _activeWidget switch
    {
        "relative" => (402, 300),
        "map" => (407, 227),
        "standings" => (560, 774),
        "fuel" => (300, 220),
        "blindspotmonitor" => (800, 500),
        "pitlanehelper" => (150, 200),
        _ => (420, 240),
    };

    private void UsePreviewSize_Click(object sender, RoutedEventArgs e)
    {
        if (_activeSizeW == null || _activeSizeH == null) return;
        if (DashiesPreviewService.Instance.GetContentSize() is { } sz)
        {
            _activeSizeW.Text = sz.Width.ToString();
            _activeSizeH.Text = sz.Height.ToString();
            CommitFormat();
            StatusText.Text = $"Size from preview: {sz.Width}x{sz.Height}";
        }
        else
        {
            StatusText.Text = "Open the preview first to copy its size.";
        }
    }

    private void ResetFormat_Click(object sender, RoutedEventArgs e)
    {
        if (_activeSizeW == null || _activeSizeH == null) return;
        var (w, h) = DefaultSize();
        _activeSizeW.Text = w.ToString();
        _activeSizeH.Text = h.ToString();
        CommitFormat();
        StatusText.Text = $"Format reset to default ({w}x{h}).";
    }

    private void SizeField_LostFocus(object sender, RoutedEventArgs e) => CommitFormat();

    private void SizeField_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) CommitFormat();
    }

    private void CommitFormat()
    {
        if (!_ready || _loading) return;
        var (w, h) = ReadFormat();
        PatchActive(cfg => SetByPath(cfg, "honeySize",
            new JsonObject { ["width"] = w, ["height"] = h }));
        if (_previewOpen && _previewWidgetId != null)
            DashiesPreviewService.Instance.Show(_previewWidgetId, w, h);
    }

    // ---- Aktionen -------------------------------------------------------------

    private static string NewId() => $"src_{Guid.NewGuid():N}".Substring(0, 12);

    private void AddToLayout_Click(object sender, RoutedEventArgs e)
    {
        var name = SelectedOverlay;
        var id = WidgetId(name);
        if (id == null)
        {
            StatusText.Text = "Select an overlay first.";
            return;
        }

        var vm = DataContext as MainViewModel;
        if (vm == null) return;
        // Spotter aktiv → kein aktives Auto nötig (Source geht ins Spotter-Set).
        if (!vm.EditingSpotter && vm.ActiveLayout == null)
        {
            StatusText.Text = "No active car — set a car as active first " +
                              "(join a session, or use \"Set as active\" in the Layout tab), " +
                              "or activate the Spotter set.";
            return;
        }

        var sv = new SourceViewModel
        {
            Id = NewId(),
            Name = $"{name} Dashie",
            Type = SourceType.Browser,
            Target = DashiesPreviewService.BuildUrl(id),
            Visible = true,
            X = 0.0f, Y = 0.0f, Z = -0.8f,
            Yaw = 0.0f, Pitch = 0.0f,
            Scale = 0.25f, Opacity = 1.0f,
        };

        // Bei offener Vorschau: aktuelle Fenstergröße in die Format-Felder übernehmen.
        var fromPreview = DashiesPreviewService.Instance.GetContentSize();
        bool synced = false;
        if (fromPreview is { } s && _activeSizeW != null && _activeSizeH != null)
        {
            _activeSizeW.Text = s.Width.ToString();
            _activeSizeH.Text = s.Height.ToString();
            synced = true;
        }

        var (fw, fh) = ReadFormat();
        sv.PixelWidth = fw;
        sv.PixelHeight = fh;
        PatchActive(cfg => SetByPath(cfg, "honeySize",
            new JsonObject { ["width"] = fw, ["height"] = fh }));

        vm.AddSourceToActiveLayout(sv);
        var target = vm.EditingSpotter ? "Spotter" : (vm.ActiveLayout?.CarName ?? "active car");
        StatusText.Text = $"Added '{name}' to {target} ({fw}x{fh}{(synced ? " from preview" : "")}).";
    }

    private void Preview_Click(object sender, RoutedEventArgs e)
    {
        // Toggle: offene Vorschau schließen.
        if (_previewOpen)
        {
            _previewOpen = false;
            DashiesPreviewService.Instance.Close();
            IrdashiesAdapterService.Instance.SetMock(false);
            PreviewButton.Content = "Preview";
            StatusText.Text = "Preview closed.";
            return;
        }

        var name = SelectedOverlay;
        var id = WidgetId(name);
        if (id == null)
        {
            StatusText.Text = "Select an overlay first.";
            return;
        }

        IrdashiesAdapterService.Instance.Start(); // idempotent
        _previewWidgetId = id;
        IrdashiesAdapterService.Instance.SetMock(MockToggle.IsChecked == true, id);

        var (w, h) = ReadFormat();
        bool ok = DashiesPreviewService.Instance.Show(id, w, h);
        if (ok)
        {
            _previewOpen = true;
            PreviewButton.Content = "Close Preview";
            StatusText.Text = $"Preview open: {name} ({w}x{h})";
        }
        else
        {
            IrdashiesAdapterService.Instance.SetMock(false);
            StatusText.Text = "Preview failed — browser-host.exe not found (set it in Settings).";
        }
    }

    private void CopyUrl_Click(object sender, RoutedEventArgs e)
    {
        var name = SelectedOverlay;
        var id = WidgetId(name);
        if (id == null)
        {
            StatusText.Text = "Select an overlay first.";
            return;
        }

        // Adapter starten falls noch nicht — sonst gibt die kopierte URL 404
        // wenn der User sie woanders öffnet (OBS-Browser-Source o.ä.).
        IrdashiesAdapterService.Instance.Start(); // idempotent

        var url = DashiesPreviewService.BuildUrl(id);
        try
        {
            Clipboard.SetText(url);
            StatusText.Text = $"Copied: {url}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Copy failed: {ex.Message}";
        }
    }

    private void MockToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_previewOpen)
            IrdashiesAdapterService.Instance.SetMock(MockToggle.IsChecked == true, _previewWidgetId);
    }

    // ---- JSON-Pfad-Helfer + getaggte Controls ---------------------------------

    private static IEnumerable<FrameworkElement> TaggedControls(DependencyObject root)
    {
        foreach (var raw in LogicalTreeHelper.GetChildren(root))
        {
            if (raw is not DependencyObject d) continue;
            if (d is FrameworkElement fe && fe.Tag is string &&
                (fe is CheckBox or Slider or ComboBox))
                yield return fe;
            foreach (var sub in TaggedControls(d)) yield return sub;
        }
    }

    private static void SetByPath(JsonObject root, string path, JsonNode? value)
    {
        var parts = path.Split('.');
        var cur = root;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            if (cur[parts[i]] is JsonObject o) cur = o;
            else { var n = new JsonObject(); cur[parts[i]] = n; cur = n; }
        }
        cur[parts[^1]] = value;
    }

    private static JsonNode? GetByPath(JsonNode? root, string path)
    {
        var cur = root;
        foreach (var p in path.Split('.'))
        {
            if (cur is JsonObject o) cur = o[p];
            else return null;
        }
        return cur;
    }

    private static bool? AsBool(JsonNode? n)
    { try { return n?.GetValue<bool>(); } catch { return null; } }

    private static string? AsString(JsonNode? n)
    { try { return n?.GetValue<string>(); } catch { return null; } }

    private static double? AsDouble(JsonNode? n)
    {
        try { return n?.GetValue<double>(); }
        catch { try { return n?.GetValue<int>(); } catch { return null; } }
    }
}
