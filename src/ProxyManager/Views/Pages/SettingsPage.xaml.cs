using System.Windows.Controls;
using System.Windows.Input;

namespace ProxyManager.Views.Pages;

public partial class SettingsPage : Page
{
    public SettingsPage()
    {
        InitializeComponent();
        DataContext = new ViewModels.SettingsViewModel();
    }

    private void OnScrollPreviewMouseWheel(object sender, MouseWheelEventArgs e) => ScrollWheelRouter.Route(sender, e);
}
