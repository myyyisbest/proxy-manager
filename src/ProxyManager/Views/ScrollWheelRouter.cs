using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ProxyManager.Views;

/// <summary>统一页面滚轮行为，关闭的下拉框不会被滚轮误改，展开时保留原生选择和滚动。</summary>
public static class ScrollWheelRouter
{
    public static void Route(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer viewer || e.Delta == 0) return;
        for (var child = e.OriginalSource as DependencyObject; child != null && child != viewer; child = Parent(child))
        {
            if (child is ComboBox { IsDropDownOpen: true }) return;
            // 多行编辑区、连接表等内层滚动区优先滚动，到边界后再交给页面。
            if (child is ScrollViewer inner && CanScroll(inner, e.Delta)) return;
        }
        if (viewer.ScrollableHeight <= 0) return;
        var lines = SystemParameters.WheelScrollLines;
        var delta = lines < 0 ? viewer.ViewportHeight : Math.Max(1, lines) * 16;
        viewer.ScrollToVerticalOffset(viewer.VerticalOffset - e.Delta / 120.0 * delta);
        e.Handled = true;
    }

    static bool CanScroll(ScrollViewer viewer, int delta) => viewer.ScrollableHeight > 0 &&
        (delta > 0 ? viewer.VerticalOffset > 0 : viewer.VerticalOffset < viewer.ScrollableHeight);

    static DependencyObject? Parent(DependencyObject value) => value is Visual or System.Windows.Media.Media3D.Visual3D
        ? VisualTreeHelper.GetParent(value)
        : LogicalTreeHelper.GetParent(value);
}
