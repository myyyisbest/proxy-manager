using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using ProxyManager.Models;
using ProxyManager.Services;

namespace ProxyManager.Views.Pages;

public partial class RulesPage : Page
{
    readonly ViewModels.RulesViewModel _vm = new();
    CancellationTokenSource? _iconLoad;

    public RulesPage()
    {
        InitializeComponent();
        DataContext = _vm;
        Loaded += OnLoaded;
        Unloaded += (_, _) => _iconLoad?.Cancel();
    }

    async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _iconLoad?.Cancel();
        _iconLoad?.Dispose();
        _iconLoad = new CancellationTokenSource();
        var token = _iconLoad.Token;
        var rules = _vm.Rules.Where(r => r.Icon == null && !string.IsNullOrEmpty(r.ExePath))
            .Select(r => (Rule: r, Path: r.ExePath!)).ToArray();
        try
        {
            await Task.Run(async () =>
            {
                foreach (var (rule, path) in rules)
                {
                    token.ThrowIfCancellationRequested();
                    var icon = IconLoader.GetIcon(path);
                    if (icon != null)
                        await Dispatcher.InvokeAsync(() => rule.Icon = icon,
                            System.Windows.Threading.DispatcherPriority.Background, token);
                }
            }, token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { AppServices.Log.Warn("读取规则图标失败：" + ex.Message); }
    }

    void OnAddFromRunning(object sender, RoutedEventArgs e)
    {
        var picker = new ProcessPickerWindow { Owner = Window.GetWindow(this) };
        if (picker.ShowDialog() != true) return;
        var (added, skipped) = _vm.AddRules(picker.SelectedRules);
        AppServices.Log.Info($"添加完成：新增 {added} 个，{skipped} 个已存在被跳过");
    }

    async void OnBrowseFile(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = "选择程序文件", Filter = "程序 (*.exe)|*.exe" };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        var rule = await Task.Run(() =>
        {
            var value = ProcessService.RuleFromExePath(dlg.FileName);
            if (value != null) value.Icon = IconLoader.GetIcon(dlg.FileName);
            return value;
        });
        if (rule == null) return;
        var (added, _) = _vm.AddRules(new[] { rule });
        if (added == 0)
            MessageBox.Show($"“{rule.DisplayName}”的此路径已经在规则列表中。", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
    }

    void OnRemoveRule(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is AppRule rule) _vm.RemoveRule(rule);
    }
}
