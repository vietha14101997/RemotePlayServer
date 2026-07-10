#nullable enable
using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemotePlayServer.Core;

namespace RemotePlayServer.ViewModels;

/// <summary>
/// Live log viewer: tails Logger.OnLogEntry, seeds from the in-memory
/// history buffer so entries logged before the UI existed are visible.
/// Filter: 0 = All, 1 = Warning+, 2 = Error only.
/// </summary>
public partial class LogViewerViewModel : ObservableObject, IDisposable
{
    private const int MaxDisplayedEntries = 2000;
    // Trim in batches: ObservableCollection.RemoveAt(0) is O(n), so removing one
    // line per new log entry at cap would shift 2000 items on every log call.
    private const int TrimBatchSize = 200;

    public ObservableCollection<LogEntry> Entries { get; } = new();

    [ObservableProperty] private int _filterIndex;
    [ObservableProperty] private bool _autoScroll = true;
    [ObservableProperty] private bool _hasEntries;

    public LogViewerViewModel()
    {
        // Entries logged between the snapshot and the subscribe below can be missed
        // (view only — the log file always has them). Acceptable for a viewer.
        SeedFromHistory();
        Logger.OnLogEntry += OnLogEntry;
    }

    private void OnLogEntry(DateTime timestamp, string message, LogLevel level)
    {
        var entry = new LogEntry(timestamp, message, level);
        if (!PassesFilter(entry)) return;

        Dispatch(() =>
        {
            Entries.Add(entry);
            if (Entries.Count > MaxDisplayedEntries)
            {
                for (int i = 0; i < TrimBatchSize && Entries.Count > 0; i++)
                    Entries.RemoveAt(0);
            }
            HasEntries = true;
        });
    }

    private bool PassesFilter(LogEntry entry) => FilterIndex switch
    {
        1 => entry.Level >= LogLevel.Warning,
        2 => entry.Level == LogLevel.Error,
        _ => true
    };

    partial void OnFilterIndexChanged(int value) => Dispatch(SeedFromHistory);

    /// <summary>Rebuild the visible list from Logger's history using the current filter.</summary>
    private void SeedFromHistory()
    {
        Entries.Clear();
        foreach (var entry in Logger.GetHistorySnapshot())
        {
            if (PassesFilter(entry))
                Entries.Add(entry);
        }
        HasEntries = Entries.Count > 0;
    }

    [RelayCommand]
    private void ClearLogs()
    {
        Logger.ClearHistory();
        Entries.Clear();
        HasEntries = false;
    }

    [RelayCommand]
    private void OpenLogsFolder()
    {
        try
        {
            var dir = System.IO.Path.Combine(AppContext.BaseDirectory, "logs");
            System.IO.Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = dir,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Logger.Warn($"[LogViewer] Cannot open logs folder: {ex.Message}");
        }
    }

    private static void Dispatch(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
            dispatcher.BeginInvoke(action); // async post: never block logging threads on the UI thread
        else
            action();
    }

    public void Dispose()
    {
        Logger.OnLogEntry -= OnLogEntry;
    }
}
