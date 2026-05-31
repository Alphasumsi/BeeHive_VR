using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using HoneyOverlays.Models;
using HoneyOverlays.Services;
using HoneyOverlays.ViewModels;

namespace HoneyOverlays.Views
{
    /// <summary>
    /// irDashies-Tab: Widget aus Dropdown → Adapter-URL → Browser-Source ins
    /// aktuell bearbeitete Set (Pfad wie LayoutPage „Add overlay"), plus
    /// „Alle neu laden" (browser-host-Respawn für dashboard.json-Änderungen).
    /// </summary>
    public partial class HtmlOverlaysPage : UserControl
    {
        // Teilmenge von WIDGET_MAP (src/frontend/WidgetIndex.tsx), alphabetisch.
        // garagecover + twitchchat raus = reine OBS/Streaming-Tools, nicht VR.
        private static readonly string[] WidgetIds =
        {
            "blindspotmonitor", "fastercarsfrombehind", "flag", "flatmap",
            "fuel", "infobar", "input", "laptimelog", "map", "pitlanehelper",
            "rejoin", "relative", "sectordelta", "slowcarahead", "standings",
            "tachometer", "telemetryinspector", "weather",
        };

        private const string UrlMarker = "honeyvr.html";
        // Alte URL-Form (aus pre-rename Configs) — wird beim Layout-Load migriert
        // und im UrlMarker-Filter (Reload-All) zusätzlich erkannt.
        private const string UrlMarkerLegacy = "index-honey-widget.html";

        public HtmlOverlaysPage()
        {
            InitializeComponent();
            AdapterBox.Text = AdapterBase();
            WidgetBox.ItemsSource = WidgetIds;
            WidgetBox.SelectedIndex = 0; // → SelectionChanged füllt Name/URL
        }

        private MainViewModel? VM => DataContext as MainViewModel;

        private static string AdapterBase()
            => $"http://localhost:{IrdashiesAdapterService.Port}";

        private static string BuildUrl(string widgetId)
        {
            var b = AdapterBase();
            return $"{b}/{UrlMarker}?wsUrl={b}&profile=default&widget={widgetId}";
        }

        /// <summary>Kurze, eindeutige ID — identisch zu LayoutPage.NewId().</summary>
        private static string NewId()
            => $"src_{Guid.NewGuid():N}".Substring(0, 12);

        private void WidgetBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (WidgetBox.SelectedItem is not string id) return;
            if (string.IsNullOrWhiteSpace(NameBox.Text) ||
                Array.IndexOf(WidgetIds, NameBox.Text.Trim()) >= 0)
            {
                NameBox.Text = id;
            }
            UrlPreview.Text = BuildUrl(id);
        }

        private void Add_Click(object sender, RoutedEventArgs e)
        {
            if (WidgetBox.SelectedItem is not string id)
            {
                StatusText.Text = "Please select a widget.";
                return;
            }

            var target = VM?.EditSources;
            if (target == null)
            {
                StatusText.Text = "No editable layout/session active — " +
                                  "pick a session (or Spotter) in the Layout tab first.";
                return;
            }

            var name = NameBox.Text?.Trim();
            if (string.IsNullOrWhiteSpace(name)) name = id;
            var url = BuildUrl(id);

            // Defaults wie CreateBrowserOverlay (Scale-Basiswert 25 = roh 0.25).
            target.Add(new SourceViewModel
            {
                Id = NewId(),
                Name = name,
                Type = SourceType.Browser,
                Target = url,
                Visible = true,
                X = 0.0f,
                Y = 0.0f,
                Z = -0.8f,
                Yaw = 0.0f,
                Pitch = 0.0f,
                Scale = 0.25f,
                Opacity = 1.0f,
            });

            StatusText.Text = $"\"{name}\" added -> {url}";
        }

        private void CopyUrl_Click(object sender, RoutedEventArgs e)
        {
            var url = UrlPreview.Text?.Trim();
            if (string.IsNullOrEmpty(url))
            {
                StatusText.Text = "Nothing to copy.";
                return;
            }
            try
            {
                Clipboard.SetText(url);
                StatusText.Text = "URL copied to clipboard.";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Copy failed: {ex.Message}";
            }
        }

        private void ReloadAll_Click(object sender, RoutedEventArgs e)
        {
            var es = VM?.EditSources;
            var ids = es == null
                ? new List<string>()
                : es.Where(s => s.Target?.IndexOf(UrlMarker,
                            StringComparison.OrdinalIgnoreCase) >= 0
                        || s.Target?.IndexOf(UrlMarkerLegacy,
                            StringComparison.OrdinalIgnoreCase) >= 0)
                    .Select(s => s.Id)
                    .ToList();

            if (ids.Count == 0)
            {
                StatusText.Text = "No irDashies overlays in the current set.";
                return;
            }

            var tracked = BrowserHostManager.Instance.TrackedIds;
            int running = ids.Count(id => tracked.Contains(id));
            BrowserHostManager.Instance.Restart(ids);
            StatusText.Text = running > 0
                ? $"Reloaded {running} running overlay(s) (browser-host respawned)."
                : "No browser-host processes running for this set " +
                  "(only the ACTIVE layout is actually running).";
        }
    }
}
