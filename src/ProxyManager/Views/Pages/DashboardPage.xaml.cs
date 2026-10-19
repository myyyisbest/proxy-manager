using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace ProxyManager.Views.Pages;

public partial class DashboardPage : Page
{
    readonly DispatcherTimer _timer;
    readonly ViewModels.DashboardViewModel _vm;
    CancellationTokenSource? _pollLifetime;

    public DashboardPage()
    {
        InitializeComponent();
        _vm = new ViewModels.DashboardViewModel();
        DataContext = _vm;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += OnTick;
        Loaded += (_, _) =>
        {
            _pollLifetime?.Dispose();
            _pollLifetime = new CancellationTokenSource();
            _timer.Start();
        };
        Unloaded += (_, _) =>
        {
            _timer.Stop();
            _pollLifetime?.Cancel();
        };
    }

    async void OnTick(object? sender, EventArgs e)
    {
        var token = _pollLifetime?.Token ?? new CancellationToken(true);
        await _vm.RefreshAsync(token);
    }

    private void OnScrollPreviewMouseWheel(object sender, MouseWheelEventArgs e) => ScrollWheelRouter.Route(sender, e);
}
