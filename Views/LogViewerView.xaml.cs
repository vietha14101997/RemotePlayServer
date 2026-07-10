using System;
using System.Collections.Specialized;
using System.Windows.Controls;
using System.Windows.Threading;
using RemotePlayServer.ViewModels;

namespace RemotePlayServer.Views;

public partial class LogViewerView : UserControl
{
    private bool _scrollPending;

    public LogViewerView()
    {
        InitializeComponent();
        ((INotifyCollectionChanged)LogList.Items).CollectionChanged += OnLogItemsChanged;
    }

    /// <summary>
    /// Keep the newest entry visible while auto-scroll is enabled.
    /// Debounced via a single pending Background-priority dispatch so bursts of
    /// added lines (or a full filter reseed) trigger one layout pass, not one per item.
    /// </summary>
    private void OnLogItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add) return;
        if (_scrollPending) return;
        if (DataContext is not LogViewerViewModel { AutoScroll: true }) return;

        _scrollPending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _scrollPending = false;
            if (DataContext is LogViewerViewModel { AutoScroll: true } && LogList.Items.Count > 0)
                LogList.ScrollIntoView(LogList.Items[LogList.Items.Count - 1]);
        }));
    }
}
