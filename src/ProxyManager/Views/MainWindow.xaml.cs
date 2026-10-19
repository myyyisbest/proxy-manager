using System.ComponentModel;
using System.Windows;
using System.Windows.Media.Imaging;
using ProxyManager.Services;
using ProxyManager.Views.Pages;

namespace ProxyManager.Views;

public partial class MainWindow : Wpf.Ui.Controls.FluentWindow
{
    TrayService? _tray;
    bool _reallyExit;
    bool _closing;
    bool _switching;

    public MainWindow()
    {
        InitializeComponent();
        try { Icon = new BitmapImage(new Uri("pack://application:,,,/ProxyManager;component/Assets/app.ico")); } catch { }
        RootNavigation.SetPageProviderService(AppServices.PageService);
        Loaded += (_, _) =>
        {
            RootNavigation.Navigate(typeof(DashboardPage));
            StartEngineIfConfigured();
        };
        AppServices.Engine.RunningChanged += OnEngineRunningChanged;
        try
        {
            _tray = new TrayService(ShowFromTray, ToggleFromTray, RequestExit);
            UpdateToggleText();
        }
        catch (Exception ex)
        {
            AppServices.Log.Warn("托盘初始化失败，关闭窗口时将直接退出：" + ex.Message);
        }
    }

    async void StartEngineIfConfigured()
    {
        if (App.SuppressAutoStart || !AppServices.Config.Current.StartEngineOnLaunch || AppServices.Engine.IsRunning) return;
        var snapshot = AppServices.Config.Snapshot();
        try { await Task.Run(() => AppServices.Engine.Start(snapshot)); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { AppServices.Log.Error("自动启动分流失败：" + ex.Message); }
    }

    async void ToggleFromTray()
    {
        if (_switching || _closing) return;
        _switching = true;
        UpdateToggleText();
        var snapshot = AppServices.Config.Snapshot();
        try
        {
            await Task.Run(() =>
            {
                if (AppServices.Engine.IsRunning) AppServices.Engine.Stop();
                else AppServices.Engine.Start(snapshot);
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AppServices.Log.Error("切换分流失败：" + ex.Message);
            ShowFromTray();
        }
        finally
        {
            _switching = false;
            UpdateToggleText();
        }
    }

    void OnEngineRunningChanged()
    {
        if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(UpdateToggleText);
    }

    void UpdateToggleText()
    {
        var state = AppServices.Engine.State;
        _tray?.Update(AppServices.Engine.IsRunning,
            _closing || _switching || state is EngineState.Starting or EngineState.Stopping);
    }

    void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    public void RequestExit()
    {
        _reallyExit = true;
        Close();
    }

    async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closing) { e.Cancel = true; return; }
        if (!_reallyExit && AppServices.Config.Current.MinimizeToTray && _tray?.IsAvailable == true)
        {
            e.Cancel = true;
            Hide();
            _tray.NotifyHiddenToTray();
            return;
        }
        // 停引擎和卸载网卡在后台完成，退出期间窗口继续响应消息。
        if (AppServices.Engine.State is not EngineState.Stopped)
        {
            e.Cancel = true;
            _closing = true;
            RootNavigation.IsEnabled = false;
            Title = "应用代理管理器 · 正在退出…";
            UpdateToggleText();
            try { await Task.Run(() => AppServices.Engine.Stop()); }
            catch (Exception ex) { AppServices.Log.Error("退出时停止分流失败：" + ex.Message); }
            finally
            {
                _closing = false;
                _reallyExit = true;
                // 引擎失败也允许关窗，由 App 退出清理回收自己持有的资源。
                Closing -= OnClosing;
                Close();
            }
        }
    }

    void OnClosed(object? sender, EventArgs e)
    {
        AppServices.Engine.RunningChanged -= OnEngineRunningChanged;
        _tray?.Dispose();
    }
}
