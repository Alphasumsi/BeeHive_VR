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