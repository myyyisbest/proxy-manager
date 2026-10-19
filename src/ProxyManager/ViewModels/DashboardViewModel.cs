using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using ProxyManager.Infrastructure;
using ProxyManager.Services;

namespace ProxyManager.ViewModels;

/// <summary>仪表盘区分内核运行、接口健康和代理配置测试，轮询不重入且可取消。</summary>
public partial class DashboardViewModel : ObservableObject
{
    static readonly Brush GreenBrush = MakeBrush(34, 197, 94);
    static readonly Brush GrayBrush = MakeBrush(148, 163, 184);
    static readonly Brush YellowBrush = MakeBrush(234, 179, 8);
    static readonly Brush RedBrush = MakeBrush(248, 113, 113);
    readonly System.Windows.Threading.Dispatcher _dispatcher = System.Windows.Application.Current.Dispatcher;

    [ObservableProperty] private bool _engineOn;
    [ObservableProperty] private bool _canToggle = true;
    [ObservableProperty] private string _statusText = "已停止";
    [ObservableProperty] private Brush _statusBrush = GrayBrush;
    [ObservableProperty] private string _subStatusText = "打开开关后，所选应用的公网连接将通过代理转发";
    [ObservableProperty] private string _apiHealthText = "";
    [ObservableProperty] private string _downSpeedText = "0 B/s";
    [ObservableProperty] private string _upSpeedText = "0 B/s";
    [ObservableProperty] private string _proxiedCountText = "0";
    [ObservableProperty] private ObservableCollection<ProxiedConnection> _connections = new();
    [ObservableProperty] private string _errorMessage = "";
    [ObservableProperty] private bool _errorOpen;

    bool _syncing;
    bool _switching;
    int _polling;

    public DashboardViewModel()
    {
        AppServices.Engine.RunningChanged += () => Dispatch(SyncFromEngine);
        AppServices.Config.ConfigUpdated += () => Dispatch(UpdateSubStatus);
        SyncFromEngine();
    }

    static Brush MakeBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    void Dispatch(Action action)
    {
        if (!_dispatcher.HasShutdownStarted) _dispatcher.BeginInvoke(action);
    }

    void UpdateSubStatus()
    {
        SubStatusText = AppServices.Engine.IsRunning
            ? $"已启用 {AppServices.Config.Current.Rules.Count(r => r.Enabled)} 条应用规则 · 其他应用与内网直连"
            : "开启分流后，所选应用的公网连接走代理，其他应用与内网直连";
    }

    void SyncFromEngine()
    {
        _syncing = true;
        try
        {
            var state = AppServices.Engine.State;
            EngineOn = state is EngineState.Running or EngineState.Starting;
            CanToggle = !_switching && state is not (EngineState.Starting or EngineState.Stopping);
            (StatusText, StatusBrush) = state switch
            {
                EngineState.Starting => ("启动中…", YellowBrush),
                EngineState.Running => ("分流运行中", GreenBrush),
                EngineState.Stopping => ("停止中…", YellowBrush),
                EngineState.Faulted => ("分流异常", RedBrush),
                _ => ("已停止", GrayBrush),
            };
            if (state != EngineState.Running)
            {
                DownSpeedText = UpSpeedText = "0 B/s";
                ProxiedCountText = "0";
                if (Connections.Count > 0) Connections = new();
                ApiHealthText = "";
                if (state is EngineState.Stopped or EngineState.Faulted) AppServices.Status.Reset();
            }
            if (!string.IsNullOrWhiteSpace(AppServices.Engine.LastError))
            {
                ErrorMessage = AppServices.Engine.LastError;
                ErrorOpen = true;
            }
            UpdateSubStatus();
        }
        finally { _syncing = false; }
    }

    partial void OnEngineOnChanged(bool value)
    {
        if (_syncing) return;
        ChangeEngineAsync(value);
    }

    async void ChangeEngineAsync(bool start)
    {
        if (_switching) { SyncFromEngine(); return; }
        _switching = true;
        ErrorOpen = false;
        CanToggle = false;
        var config = AppServices.Config.Snapshot();
        try
        {
            await Task.Run(() =>
            {
                if (start) AppServices.Engine.Start(config);
                else AppServices.Engine.Stop();
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AppServices.Log.Error("切换分流失败：" + ex.Message);
            ErrorMessage = ex.Message;
            ErrorOpen = true;
        }
        finally
        {
            _switching = false;
            SyncFromEngine();
        }
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (!AppServices.Engine.IsRunning || Interlocked.Exchange(ref _polling, 1) != 0) return;
        var secret = AppServices.Engine.ApiSecret;
        try
        {
            await AppServices.Status.PollAsync(AppServices.Engine.ApiPort, secret, cancellationToken);
            if (cancellationToken.IsCancellationRequested || !AppServices.Engine.IsRunning ||
                secret != AppServices.Engine.ApiSecret) return;
            DownSpeedText = Format.Speed(AppServices.Status.DownSpeed);
            UpSpeedText = Format.Speed(AppServices.Status.UpSpeed);
            ProxiedCountText = AppServices.Status.Proxied.Count.ToString();
            ApiHealthText = AppServices.Status.ApiAlive
                ? "状态接口正常 · 上游出口可用性请在设置中测试"
                : "内核仍在运行，状态接口暂不可用；统计已暂停";
            StatusBrush = AppServices.Status.ApiAlive ? GreenBrush : YellowBrush;
            var visible = AppServices.Status.Proxied.Take(50).ToArray();
            if (!Connections.SequenceEqual(visible)) Connections = new(visible);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ApiHealthText = "读取运行状态失败；请查看日志";
            AppServices.Log.Warn("仪表盘刷新失败：" + ex.Message);
        }
        finally { Volatile.Write(ref _polling, 0); }
    }
}
