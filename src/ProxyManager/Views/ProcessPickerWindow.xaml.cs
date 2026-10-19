using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.Win32;
using ProxyManager.Models;
using ProxyManager.Services;

namespace ProxyManager.Views;

/// <summary>从运行中的进程或文件挑选应用。选择存储在数据项上，不受搜索和虚拟化回收影响。</summary>
public partial class ProcessPickerWindow : Wpf.Ui.Controls.FluentWindow
{
    readonly ObservableCollection<ProcInfo> _all = new();
    readonly CancellationTokenSource _lifetime = new();
    bool _loaded;
    bool _closed;

    public List<AppRule> SelectedRules { get; } = new();

    public ProcessPickerWindow()
    {
        InitializeComponent();
        ProcList.ItemsSource = _all;
        CollectionViewSource.GetDefaultView(_all).Filter = FilterProc;
        Closed += (_, _) =>
        {
            _closed = true;
            _lifetime.Cancel();
        };
    }

    bool FilterProc(object item)
    {
        var keyword = SearchBox.Text.Trim();
        if (keyword.Length == 0 || item is not ProcInfo p) return true;
        return p.DisplayName.Contains(keyword, StringComparison.OrdinalIgnoreCase)
               || p.ProcessName.Contains(keyword, StringComparison.OrdinalIgnoreCase)
               || p.ExePath.Contains(keyword, StringComparison.OrdinalIgnoreCase);
    }

    async void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;
        var token = _lifetime.Token;
        try
        {
            var procs = await Task.Run(() => ProcessService.GetRunningProcesses(token), token);
            if (_closed) return;
            using (CollectionViewSource.GetDefaultView(_all).DeferRefresh())
                foreach (var p in procs) _all.Add(p);
            LoadingPanel.Visibility = Visibility.Collapsed;
            ConfirmButton.IsEnabled = true;
            SearchBox.Focus();

            // 后台只读取普通数据，冻结后的图片在 UI 线程赋值；不访问 DialogResult 等 WPF 属性。
            await Task.Run(async () =>
            {
                foreach (var p in procs)
                {
                    token.ThrowIfCancellationRequested();
                    var icon = IconLoader.GetIcon(p.ExePath);
                    if (icon != null)
                        await Dispatcher.InvokeAsync(() => p.Icon = icon,
                            System.Windows.Threading.DispatcherPriority.Background, token);
                }
            }, token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AppServices.Log.Warn("读取应用列表失败：" + ex.Message);
            if (!_closed)
            {
                LoadingPanel.Visibility = Visibility.Collapsed;
                ConfirmButton.IsEnabled = true;
                MessageBox.Show(this, "读取部分进程失败，可通过“浏览选择文件”添加应用。\n" + ex.Message,
                    "读取应用", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }

    void OnSearchChanged(object sender, TextChangedEventArgs e) =>
        CollectionViewSource.GetDefaultView(_all).Refresh();

    void OnConfirm(object sender, RoutedEventArgs e)
    {
        foreach (var p in _all.Where(p => p.IsSelected))
            SelectedRules.Add(new AppRule
            {
                ProcessName = p.ProcessName,
                DisplayName = p.DisplayName,
                ExePath = p.ExePath.Length > 0 ? p.ExePath : null,
                Icon = p.Icon,
            });
        DialogResult = true;
    }

    void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    async void OnBrowseFile(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = "选择程序文件", Filter = "程序 (*.exe)|*.exe" };
        if (dlg.ShowDialog(this) != true) return;
        var rule = await Task.Run(() =>
        {
            var value = ProcessService.RuleFromExePath(dlg.FileName);
            if (value != null) value.Icon = IconLoader.GetIcon(dlg.FileName);
            return value;
        });
        if (_closed || rule == null) return;
        SelectedRules.Add(rule);
        DialogResult = true;
    }
}
