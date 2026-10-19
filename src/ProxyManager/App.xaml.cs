using System.Windows;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using ProxyManager.Infrastructure;
using Wpf.Ui.Appearance;

namespace ProxyManager;

public partial class App : Application
{
    static Mutex? _singleInstanceMutex;
    static bool _ownsMutex;
    bool _servicesInitialized;
    EventWaitHandle? _shutdownSignal;
    RegisteredWaitHandle? _shutdownRegistration;
    public static bool SuppressAutoStart { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        var isolated = e.Args.Contains("--isolated", StringComparer.OrdinalIgnoreCase);
        var dataIndex = Array.FindIndex(e.Args, s => s.Equals("--data-dir", StringComparison.OrdinalIgnoreCase));
        if (dataIndex >= 0 && (!isolated || dataIndex + 1 >= e.Args.Length || !Path.IsPathFullyQualified(e.Args[dataIndex + 1])))
        {
            MessageBox.Show("测试数据目录必须与 --isolated 一起使用，并提供完整路径。", "启动参数", MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown(2);
            return;
        }
        if (isolated && dataIndex < 0)
        {
            MessageBox.Show("隔离模式需要 --data-dir <专用测试目录>。", "启动参数", MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown(2);
            return;
        }
        if (isolated)
        {
            var dataPath = Path.GetFullPath(e.Args[dataIndex + 1]);
            var defaultPath = AppPaths.DataDir;
            if (dataPath.TrimEnd(Path.DirectorySeparatorChar).Equals(defaultPath.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) ||
                dataPath.TrimEnd(Path.DirectorySeparatorChar).Equals(AppPaths.LegacyDataDir.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show("隔离测试不能使用正式数据目录。", "启动参数", MessageBoxButton.OK, MessageBoxImage.Warning);
                Shutdown(2);
                return;
            }
            AppPaths.ConfigureDataDirectory(dataPath);
        }
        SuppressAutoStart = isolated || e.Args.Contains("--no-auto-start", StringComparer.OrdinalIgnoreCase);
        var identity = (Environment.ProcessPath ?? AppContext.BaseDirectory).ToUpperInvariant() + "|" +
            Environment.UserName + "|" + Process.GetCurrentProcess().SessionId +
            (isolated ? "|" + AppPaths.DataDir.ToUpperInvariant() : "");
        var instanceKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..24];
        var signalName = "Local\\ProxyManager_Shutdown_" + instanceKey;
        if (e.Args.Contains("--disable-autostart", StringComparer.OrdinalIgnoreCase))
        {
            try { Services.AutoStartService.SetEnabled(false); Shutdown(); }
            catch (Exception) { Shutdown(4); }
            return;
        }
        if (e.Args.Contains("--shutdown", StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                using var signal = EventWaitHandle.OpenExisting(signalName);
                signal.Set();
            }
            catch (WaitHandleCannotBeOpenedException) { /* 此安装位置没有实例 */ }
            catch (Exception ex)
            {
                MessageBox.Show("无法通知当前实例退出：" + ex.Message, "退出应用", MessageBoxButton.OK, MessageBoxImage.Warning);
                Shutdown(3);
                return;
            }
            Shutdown();
            return;
        }

        _singleInstanceMutex = new Mutex(true,
            isolated ? "Local\\ProxyManager_Test_" + instanceKey : "Global\\ProxyManager_SingleInstance", out var isFirst);
        _ownsMutex = isFirst;
        if (!isFirst)
        {
            MessageBox.Show("应用代理管理器已经在运行了（请查看系统托盘）。", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        try
        {
            AppServices.Init();
            _servicesInitialized = true;
            if (!string.IsNullOrEmpty(AppServices.Config.LoadWarning)) SuppressAutoStart = true;
            Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnSystemPreferenceChanged;
        }
        catch (Exception ex)
        {
            MessageBox.Show("初始化应用失败：" + ex.Message, "启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }
        ReconcileAutoStart();

        _shutdownSignal = new EventWaitHandle(false, EventResetMode.AutoReset, signalName);
        _shutdownRegistration = ThreadPool.RegisterWaitForSingleObject(_shutdownSignal, (_, _) =>
        {
            if (!Dispatcher.HasShutdownStarted)
                Dispatcher.BeginInvoke(() => (MainWindow as Views.MainWindow)?.RequestExit());
        }, null, Timeout.Infinite, false);

        // 后台线程的未处理异常会直接终止进程且不经过 Dispatcher 处理器，这里兜底留痕
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            try { AppServices.Log.Error("致命异常: " + args.ExceptionObject); } catch { }
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            try
            {
                AppServices.Log.Warn("未观察任务异常: " + args.Exception);
                args.SetObserved();
            }
            catch { }
        };
        ApplyAppTheme(AppServices.Config.Current.Theme);

        var main = new Views.MainWindow();
        MainWindow = main;
        main.Show();
        if (!string.IsNullOrEmpty(AppServices.Config.LoadWarning))
            MessageBox.Show(main, AppServices.Config.LoadWarning, "配置恢复提示", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    /// <summary>1.4.x 之前用 HKCU Run 键自启动，升级后该项会随旧目录消失；按配置重新登记为计划任务。</summary>
    void ReconcileAutoStart()
    {
        if (SuppressAutoStart || !AppServices.Config.Current.AutoStartWithWindows || Services.AutoStartService.IsEnabled()) return;
        try
        {
            Services.AutoStartService.SetEnabled(true);
            AppServices.Log.Info("已按配置重新登记开机自启动计划任务");
        }
        catch (Exception ex)
        {
            AppServices.Config.Current.AutoStartWithWindows = false;
            try { AppServices.Config.Save(restartEngine: false); } catch { }
            AppServices.Log.Warn("开机自启动登记失败，已同步关闭该设置：" + ex.Message);
        }
    }

    /// <summary>应用统一动态主题，使用不透明背景避免本地与远程桌面混色。</summary>
    public static void ApplyAppTheme(string? theme)
    {
        try
        {
            // An opaque theme background is consistent across local/RDP sessions.
            // Mica can leave a transparent dark surface after a live light-theme switch.
            var backdrop = Wpf.Ui.Controls.WindowBackdropType.None;
            switch (theme)
            {
                case "Light":
                    ApplicationThemeManager.Apply(ApplicationTheme.Light, backdrop, true);
                    break;
                case "System":
                    ApplicationThemeManager.ApplySystemTheme();
                    break;
                default:
                    ApplicationThemeManager.Apply(ApplicationTheme.Dark, backdrop, true);
                    break;
            }
            foreach (Window window in Current.Windows)
            {
                if (window is Wpf.Ui.Controls.FluentWindow fluent) fluent.WindowBackdropType = backdrop;
                window.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, "ApplicationBackgroundBrush");
                window.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "TextFillColorPrimaryBrush");
            }
        }
        catch (Exception ex)
        {
            try { AppServices.Log?.Warn("主题切换失败：" + ex.Message); } catch { }
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnSystemPreferenceChanged;
        _shutdownRegistration?.Unregister(null);
        _shutdownSignal?.Dispose();
        // 重复实例和 --shutdown 客户端从未拥有服务或互斥锁，不得清理另一实例。
        if (_servicesInitialized)
        {
            try { Task.Run(() => AppServices.Engine.Stop()).GetAwaiter().GetResult(); } catch { }
            AppServices.Log.Info("程序退出");
            AppServices.Status.Dispose();
            AppServices.Log.Dispose();
        }
        if (_ownsMutex)
        {
            try { _singleInstanceMutex?.ReleaseMutex(); } catch (ApplicationException) { }
        }
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }

    void OnDispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        // 必须最先标记已处理：处理器内任何后续异常（如关闭过程中弹窗失败）不再导致进程终止
        e.Handled = true;
        try
        {
            AppServices.Log.Error("未处理异常: " + e.Exception);
        }
        catch { }
        try
        {
            MessageBox.Show("发生了一个错误：\n" + e.Exception.Message, "错误",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch { }
    }

    void OnSystemPreferenceChanged(object sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
    {
        if (!_servicesInitialized || Dispatcher.HasShutdownStarted || AppServices.Config.Current.Theme != "System") return;
        Dispatcher.BeginInvoke(() => { if (AppServices.Config.Current.Theme == "System") ApplyAppTheme("System"); });
    }
}
