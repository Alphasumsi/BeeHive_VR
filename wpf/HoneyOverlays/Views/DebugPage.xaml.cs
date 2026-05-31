using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using HoneyOverlays.Services;
using HoneyOverlays.ViewModels;

namespace HoneyOverlays.Views;

/// <summary>
/// Debug-Console-Page. Zeigt Logger.Entries gefiltert + scrollbar.
/// Auto-Scroll wird im Code-Behind verwaltet (zum neuesten Eintrag scrollen).
/// </summary>
public partial class DebugPage : UserControl
{
    public DebugPage()
    {
        InitializeComponent();

        // DataContext im Code-Behind setzen statt im XAML — damit das
        // UserControl von außen weiterhin den geerbten DataContext sieht
        // (für Visibility-Bindings auf MainViewModel.ActiveSection).
        // Innen drin (Liste, Buttons) wird DataContext aufs DebugViewModel
        // umgeschaltet.
        var vm = new ViewModels.DebugViewModel();
        if (Content is FrameworkElement root)
            root.DataContext = vm;

        // Auto-Scroll: bei jedem neuen Log-Eintrag ans Ende scrollen
        Logger.Entries.CollectionChanged += Entries_CollectionChanged;
    }

    private DebugViewModel? VM =>
        (Content as FrameworkElement)?.DataContext as DebugViewModel;

    private void Entries_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (VM?.AutoScroll != true) return;
        if (e.Action != NotifyCollectionChangedAction.Add) return;

        // Auf UI-Thread scrollen
        Dispatcher.BeginInvoke(new System.Action(() =>
        {
            LogScrollViewer.ScrollToEnd();
        }));
    }

    private void OpenLogFile_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = Logger.LogFilePath;
            if (!File.Exists(path))
            {
                Logger.Warn($"Open log file: file does not exist yet ({path})");
                return;
            }

            // Standard-Editor öffnet die Datei
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }
        catch (System.Exception ex)
        {
            Logger.Error("Failed to open log file in editor", ex);
        }
    }

    // Step 4b test trigger — cycles through three preset poses so each click
    // visibly moves the test atlas in VR. Useful to verify the WPF → Electron
    // → Layer pipe end-to-end without any UI wiring yet.
    private int _testStep;
    private void SendTestAtlasLayout_Click(object sender, RoutedEventArgs e)
    {
        _testStep = (_testStep + 1) % 3;
        // Three preset offsets; only X varies on p1 to make movement obvious.
        float p1X = -0.6f + 0.2f * _testStep;   // -0.6 / -0.4 / -0.2
        var quads = new List<AtlasQuadDto>
        {
            new() { Id = "p1", PosX = p1X, PosY = 0.0f,  PosZ = -1.0f, SizeW = 0.40f, SizeH = 0.30f },
            new() { Id = "p2", PosX = 0.6f, PosY = 0.0f,  PosZ = -1.0f, SizeW = 0.40f, SizeH = 0.30f },
            new() { Id = "p3", PosX = 0.0f, PosY = -0.35f, PosZ = -1.0f, SizeW = 0.80f, SizeH = 0.30f },
        };
        EngineLink.Instance.PushAtlasLayout(quads);
        Logger.Info($"DebugPage: pushed setAtlasLayout (step {_testStep}, p1.X={p1X:F2}, connected={EngineLink.Instance.IsConnected})");
    }

    private void CopyLog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // Aktuell sichtbare Einträge (durch Filter gefiltert) übernehmen
            var view = VM?.EntriesView;
            if (view == null) return;

            var sb = new System.Text.StringBuilder();
            int count = 0;
            foreach (var item in view)
            {
                if (item is LogEntry entry)
                {
                    sb.Append(entry.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff"));
                    sb.Append(" [");
                    sb.Append(entry.Level.ToString().PadRight(5));
                    sb.Append("] ");
                    sb.AppendLine(entry.Message);
                    if (!string.IsNullOrEmpty(entry.Exception))
                        sb.AppendLine(entry.Exception);
                    count++;
                }
            }

            Clipboard.SetText(sb.ToString());
            Logger.Info($"Copied {count} log entries to clipboard");
        }
        catch (System.Exception ex)
        {
            Logger.Error("Failed to copy log to clipboard", ex);
        }
    }
}