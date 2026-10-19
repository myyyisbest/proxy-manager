using System.Windows;
using Wpf.Ui.Abstractions;

namespace ProxyManager.Services;

/// <summary>WPF-UI 导航页服务：按需创建页面并缓存。</summary>
public sealed class PageService : INavigationViewPageProvider
{
    readonly Dictionary<Type, FrameworkElement> _pages = new();

    public object? GetPage(Type pageType)
    {
        lock (_pages)
        {
            if (_pages.TryGetValue(pageType, out var cached)) return cached;
            var page = (FrameworkElement)Activator.CreateInstance(pageType)!;
            _pages[pageType] = page;
            return page;
        }
    }
}
