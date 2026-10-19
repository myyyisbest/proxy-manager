using CommunityToolkit.Mvvm.ComponentModel;
using System.Text.Json.Serialization;

namespace ProxyManager.Models;

public enum ProxyType
{
    Socks5,
    Http
}

/// <summary>上游代理服务器（通常是本机或局域网内的 Clash）。</summary>
public class ProxyServer
{
    public ProxyType Type { get; set; } = ProxyType.Socks5;
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 7890;
    [JsonIgnore] public string? Username { get; set; }
    [JsonIgnore] public string? Password { get; set; }
    /// <summary>仅当前 Windows 用户可解密的 DPAPI 凭据。</summary>
    public string? ProtectedCredentials { get; set; }

    public override string ToString() => $"{Host}:{Port}";
}

/// <summary>一条应用分流规则：该进程的流量走代理。</summary>
public partial class AppRule : ObservableObject
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>进程名（exe 文件名，含扩展名）。</summary>
    public string ProcessName { get; set; } = "";

    /// <summary>显示名（产品名或进程名）。</summary>
    public string DisplayName { get; set; } = "";

    /// <summary>优先按此完整路径匹配；没有路径时才按进程名匹配。</summary>
    public string? ExePath { get; set; }

    [ObservableProperty]
    private bool _enabled = true;

    /// <summary>列表显示用图标（不持久化）。</summary>
    [ObservableProperty]
    [property: JsonIgnore]
    private System.Windows.Media.ImageSource? _icon;
}

/// <summary>应用配置；便携版存 Data，安装版存 LocalAppData。</summary>
public class AppConfig
{
    /// <summary>3 = 1.4.3：关闭窗口默认驻留托盘（迁移覆盖 v2 时代显式保存的 false）。</summary>
    public int SchemaVersion { get; set; } = 3;
    /// <summary>可选 fake-IP：处理本地 DNS 污染；兼容模式保留系统 DNS。</summary>
    public bool UseFakeIpDns { get; set; }
    public ProxyServer Proxy { get; set; } = new();

    public System.Collections.ObjectModel.ObservableCollection<AppRule> Rules { get; set; } = new();

    /// <summary>启动软件时自动开启分流。</summary>
    public bool StartEngineOnLaunch { get; set; } = false;

    /// <summary>开机自启动。</summary>
    public bool AutoStartWithWindows { get; set; } = false;

    /// <summary>关闭窗口时最小化到托盘。</summary>
    public bool MinimizeToTray { get; set; } = true;

    /// <summary>界面主题：Dark / Light / System。</summary>
    public string Theme { get; set; } = "Dark";
}
