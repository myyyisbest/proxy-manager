using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ProxyManager.Services;

namespace ProxyManager.Views.Pages;

public partial class LogPage : Page
{
    readonly ObservableCollection<LogEntry> _entries = new();
    readonly Queue<LogEntry> _pending = new();
    readonly object _lock = new();
    readonly DispatcherTimer _flush;

    public LogPage()
    {
        InitializeComponent();

        LogList.ItemsSource = _entries;

        // 引擎日志每秒可达几十行，逐条刷 UI 会卡顿；攒起来每 500ms 批量上屏
        _flush = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _flush.Tick += (_, _) => FlushPending();
        Loaded += (_, _) =>
        {
            _entries.Clear();
            foreach (var entry in AppServices.Log.Snapshot().TakeLast(1000)) _entries.Add(entry);
            AppServices.Log.EntryAdded += OnEntryAdded;
            _flush.Start();
            ScrollToEnd();
        };
        Unloaded += (_, _) =>
        {
            AppServices.Log.EntryAdded -= OnEntryAdded;
            _flush.Stop();
            lock (_lock) _pending.Clear();
        };
    }

    void OnEntryAdded(LogEntry entry)
    {
        lock (_lock)
        {
            _pending.Enqueue(entry);
            while (_pending.Count > 1000) _pending.Dequeue();
        }
    }

    void FlushPending()
    {
        LogEntry[] batch;
        lock (_lock)
        {
            if (_pending.Count == 0) return;
            batch = _pending.ToArray();
            _pending.Clear();
        }
        foreach (var entry in batch)
            _entries.Add(entry);
        while (_entries.Count > 1000)
            _entries.RemoveAt(0);
        if (AutoScrollCheck.IsChecked == true)
            ScrollToEnd();
    }

    void ScrollToEnd()
    {
        if (LogList.Items.Count > 0)
            LogList.ScrollIntoView(LogList.Items[^1]);
    }

    void OnClear(object sender, RoutedEventArgs e)
    {
        lock (_lock) _pending.Clear();
        _entries.Clear();
    }
}
