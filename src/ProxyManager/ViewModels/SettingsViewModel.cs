using System.Diagnostics;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProxyManager.Infrastructure;
using ProxyManager.Models;
using ProxyManager.Services;

namespace ProxyManager.ViewModels;

/// <summary>代理字段为草稿，点击应用后才持久化；连通测试不改变系统或分流状态。</summary>
public partial class SettingsViewModel : ObservableObject
{
    AppConfig Cfg => AppServices.Config.Current;
    bool _syncing;
    CancellationTokenSource? _testCancellation;
    [ObservableProperty] private int _proxyTypeIndex;
    [ObservableProperty] private string _host = "";
    [ObservableProperty] private string _portText = "";
    [ObservableProperty] private string _username = "";
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private string _testResult = "";
    [ObservableProperty] private bool _testOk = true;
    [ObservableProperty] private string _applyResult = "";
    [ObservableProperty] private bool _hasProxyChanges;
    [ObservableProperty] private bool _useFakeIpDns;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _autoStart;
    [ObservableProperty] private bool _autoEngine;
    [ObservableProperty] private bool _minimizeTray;
    [ObservableProperty] private int _themeIndex;
    [ObservableProperty] private string _enginePathText = "";
    [ObservableProperty] private string _engineVersion = "读取中…";

    public SettingsViewModel()
    {
        var p = Cfg.Proxy;
        _proxyTypeIndex = p.Type == ProxyType.Http ? 1 : 0;
        _host = p.Host; _portText = p.Port.ToString(); _username = p.Username ?? ""; _password = p.Password ?? "";
        _useFakeIpDns = Cfg.UseFakeIpDns;
        _autoStart = AutoStartService.IsEnabled();
        _autoEngine = Cfg.StartEngineOnLaunch; _minimizeTray = Cfg.MinimizeToTray;
        _themeIndex = Cfg.Theme switch { "Light" => 1, "System" => 2, _ => 0 };
        _enginePathText = AppServices.Engine.EnginePath;
        _ = ReadVersionAsync();
    }

    async Task ReadVersionAsync()
    {
        try
        {
            var version = await Task.Run(() => AppServices.Engine.GetEngineVersion());
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.HasShutdownStarted) await dispatcher.InvokeAsync(() => EngineVersion = version);
        }
        catch { EngineVersion = "读取失败"; }
    }

    void DraftChanged()
    {
        HasProxyChanges = ProxyTypeIndex != (Cfg.Proxy.Type == ProxyType.Http ? 1 : 0)
            || !string.Equals(Host.Trim(), Cfg.Proxy.Host, StringComparison.Ordinal)
            || PortText.Trim() != Cfg.Proxy.Port.ToString()
            || Username != (Cfg.Proxy.Username ?? "") || Password != (Cfg.Proxy.Password ?? "") || UseFakeIpDns != Cfg.UseFakeIpDns;
        ApplyResult = HasProxyChanges ? "代理设置尚未应用" : "";
    }
    partial void OnProxyTypeIndexChanged(int value) => DraftChanged();
    partial void OnHostChanged(string value) => DraftChanged();
    partial void OnPortTextChanged(string value) => DraftChanged();
    partial void OnUsernameChanged(string value) => DraftChanged();
    partial void OnPasswordChanged(string value) => DraftChanged();
    partial void OnUseFakeIpDnsChanged(bool value) => DraftChanged();

    ProxyServer ReadDraft()
    {
        if (ProxyTypeIndex is not (0 or 1)) throw new ArgumentException("请选择 SOCKS5 或 HTTP 协议。");
        if (!int.TryParse(PortText.Trim(), out var port)) throw new ArgumentException("端口必须为 1–65535 的整数。");
        var proxy = new ProxyServer { Type = ProxyTypeIndex == 1 ? ProxyType.Http : ProxyType.Socks5,
            Host = Host.Trim(), Port = port, Username = string.IsNullOrEmpty(Username) ? null : Username,
            Password = string.IsNullOrEmpty(Password) ? null : Password };
        ConfigService.Validate(new AppConfig { Proxy = proxy });
        return proxy;
    }

    [RelayCommand]
    void ApplyProxy()
    {
        try
        {
            var draft = ReadDraft();
            var previous = Cfg.Proxy;
            var previousDns = Cfg.UseFakeIpDns;
            Cfg.Proxy = draft;
            Cfg.UseFakeIpDns = UseFakeIpDns;
            try { AppServices.Config.Save(); }
            catch { Cfg.Proxy = previous; Cfg.UseFakeIpDns = previousDns; throw; }
            Host = draft.Host; PortText = draft.Port.ToString();
            HasProxyChanges = false;
            ApplyResult = "代理设置已保存；运行中的引擎将校验后切换，失败时保留原连接设置。";
        }
        catch (Exception ex)
        {
            ApplyResult = "未应用：" + ex.Message;
            AppServices.Log.Warn("代理设置未应用：" + ex.Message);
        }
    }

    partial void OnAutoStartChanged(bool value)
    {
        if (_syncing) return;
        var previous = AutoStartService.IsEnabled();
        try
        {
            AutoStartService.SetEnabled(value);
            Cfg.AutoStartWithWindows = AutoStartService.IsEnabled();
            if (Cfg.AutoStartWithWindows != value) throw new IOException("Windows 自启动状态校验失败。");
            AppServices.Config.Save(restartEngine: false);
        }
        catch (Exception ex)
        {
            try { if (AutoStartService.IsEnabled() != previous) AutoStartService.SetEnabled(previous); } catch { }
            _syncing = true;
            AutoStart = AutoStartService.IsEnabled();
            Cfg.AutoStartWithWindows = AutoStart;
            _syncing = false;
            ApplyResult = "设置开机自启失败：" + ex.Message;
            AppServices.Log.Error(ApplyResult);
        }
    }

    partial void OnAutoEngineChanged(bool value)
    {
        if (_syncing) return;
        var old = Cfg.StartEngineOnLaunch; Cfg.StartEngineOnLaunch = value;
        try { AppServices.Config.Save(false); }
        catch (Exception ex) { Cfg.StartEngineOnLaunch = old; _syncing = true; AutoEngine = old; _syncing = false; ShowSaveError(ex); }
    }
    partial void OnMinimizeTrayChanged(bool value)
    {
        if (_syncing) return;
        var old = Cfg.MinimizeToTray; Cfg.MinimizeToTray = value;
        try { AppServices.Config.Save(false); }
        catch (Exception ex) { Cfg.MinimizeToTray = old; _syncing = true; MinimizeTray = old; _syncing = false; ShowSaveError(ex); }
    }
    partial void OnThemeIndexChanged(int value)
    {
        if (_syncing) return;
        var old = Cfg.Theme; Cfg.Theme = value switch { 1 => "Light", 2 => "System", _ => "Dark" };
        try { AppServices.Config.Save(false); App.ApplyAppTheme(Cfg.Theme); }
        catch (Exception ex) { Cfg.Theme = old; _syncing = true; ThemeIndex = old switch { "Light" => 1, "System" => 2, _ => 0 }; _syncing = false; ShowSaveError(ex); }
    }
    void ShowSaveError(Exception ex) { ApplyResult = "设置未保存：" + ex.Message; AppServices.Log.Error(ApplyResult); }

    [RelayCommand]
    async Task TestConnectionAsync()
    {
        if (IsBusy) return;
        IsBusy = true; TestOk = true;
        _testCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        try
        {
            var proxy = ReadDraft();
            TestResult = "正在测试协议、认证及 HTTPS 出口…";
            var timer = Stopwatch.StartNew();
            await ProbeProxyAsync(proxy, _testCancellation.Token);
            TestOk = true; TestResult = $"✓ 协议、认证和 HTTPS 出口正常（{timer.ElapsedMilliseconds} ms）";
        }
        catch (OperationCanceledException) { TestOk = false; TestResult = "测试已取消或超过 12 秒，请检查上游代理。"; }
        catch (Exception ex)
        {
            TestOk = false; TestResult = "✗ " + ex.Message;
            AppServices.Log.Warn("代理连接测试失败：" + ex.Message);
        }
        finally { _testCancellation.Dispose(); _testCancellation = null; IsBusy = false; }
    }

    [RelayCommand]
    void CancelTest() => _testCancellation?.Cancel();

    /// <summary>直接连接上游代理并完成协议、认证、TLS 证书与 HTTP 检测；不使用系统代理。</summary>
    public static async Task ProbeProxyAsync(ProxyServer proxy, CancellationToken cancellationToken = default,
        string targetHost = "www.microsoft.com", int targetPort = 443)
    {
        ConfigService.Validate(new AppConfig { Proxy = proxy });
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        var token = timeout.Token;
        using var tcp = new TcpClient();
        try { await tcp.ConnectAsync(proxy.Host, proxy.Port, token); }
        catch (SocketException ex) { throw new IOException("无法连接代理端口：" + ex.SocketErrorCode, ex); }
        using var stream = tcp.GetStream();
        if (proxy.Type == ProxyType.Socks5) await OpenSocksTunnelAsync(stream, proxy, targetHost, targetPort, token);
        else await OpenHttpTunnelAsync(stream, proxy, targetHost, targetPort, token);
        using var tls = new SslStream(stream, leaveInnerStreamOpen: true);
        try
        {
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = targetHost,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 }, token);
        }
        catch (AuthenticationException ex) { throw new IOException("代理协议已连接，但目标 HTTPS 证书或 TLS 握手失败。", ex); }
        var request = Encoding.ASCII.GetBytes($"HEAD / HTTP/1.1\r\nHost: {targetHost}\r\nConnection: close\r\nUser-Agent: ProxyManager-ConnectionTest/1.4\r\n\r\n");
        await tls.WriteAsync(request, token);
        var header = await ReadHeaderAsync(tls, token);
        var status = ParseStatus(header);
        if (status is < 200 or >= 400) throw new IOException($"代理已连接，但 HTTPS 检测站点返回 HTTP {status}。");
    }

    static async Task OpenSocksTunnelAsync(Stream stream, ProxyServer proxy, string target, int port, CancellationToken token)
    {
        var auth = !string.IsNullOrEmpty(proxy.Username);
        await stream.WriteAsync(new byte[] { 5, 1, auth ? (byte)2 : (byte)0 }, token);
        var reply = new byte[2]; await stream.ReadExactlyAsync(reply, token);
        if (reply[0] != 5 || reply[1] != (auth ? 2 : 0)) throw new IOException(auth ? "SOCKS5 服务器未接受用户名/密码认证方式。" : "端口未提供 SOCKS5 无认证服务，请检查协议和凭据。");
        if (auth)
        {
            var user = Encoding.UTF8.GetBytes(proxy.Username!); var pass = Encoding.UTF8.GetBytes(proxy.Password ?? "");
            var bytes = new byte[3 + user.Length + pass.Length];
            bytes[0] = 1; bytes[1] = (byte)user.Length; user.CopyTo(bytes, 2);
            bytes[2 + user.Length] = (byte)pass.Length; pass.CopyTo(bytes, 3 + user.Length);
            try { await stream.WriteAsync(bytes, token); } finally { Array.Clear(bytes); Array.Clear(pass); }
            await stream.ReadExactlyAsync(reply, token);
            if (reply[0] != 1 || reply[1] != 0) throw new IOException("SOCKS5 用户名或密码不正确。");
        }
        var host = Encoding.ASCII.GetBytes(target);
        var request = new byte[7 + host.Length];
        request[0] = 5; request[1] = 1; request[3] = 3; request[4] = (byte)host.Length; host.CopyTo(request, 5);
        request[^2] = (byte)(port >> 8); request[^1] = (byte)port;
        await stream.WriteAsync(request, token);
        var response = new byte[4]; await stream.ReadExactlyAsync(response, token);
        if (response[0] != 5 || response[2] != 0 || response[1] != 0) throw new IOException($"SOCKS5 代理无法建立目标连接（代码 {response[1]}）。");
        int length;
        if (response[3] == 1) length = 4;
        else if (response[3] == 4) length = 16;
        else if (response[3] == 3) { var count = new byte[1]; await stream.ReadExactlyAsync(count, token); length = count[0]; }
        else throw new IOException("SOCKS5 服务器响应无效。");
        await stream.ReadExactlyAsync(new byte[length + 2], token);
    }

    static async Task OpenHttpTunnelAsync(Stream stream, ProxyServer proxy, string target, int port, CancellationToken token)
    {
        var authorization = string.IsNullOrEmpty(proxy.Username) ? "" : "Proxy-Authorization: Basic " +
            Convert.ToBase64String(Encoding.UTF8.GetBytes(proxy.Username + ":" + (proxy.Password ?? ""))) + "\r\n";
        var request = Encoding.ASCII.GetBytes($"CONNECT {target}:{port} HTTP/1.1\r\nHost: {target}:{port}\r\n{authorization}\r\n");
        await stream.WriteAsync(request, token);
        var status = ParseStatus(await ReadHeaderAsync(stream, token));
        if (status == 407) throw new IOException("HTTP 代理认证失败，请检查用户名和密码。");
        if (status != 200) throw new IOException($"HTTP CONNECT 失败（HTTP {status}），请检查代理协议或上游网络。");
    }

    static int ParseStatus(string header)
    {
        var first = header.Split("\r\n", 2)[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (first.Length < 2 || !first[0].StartsWith("HTTP/1.", StringComparison.Ordinal) || !int.TryParse(first[1], out var status))
            throw new IOException("服务器没有返回有效的 HTTP 响应，请检查代理协议。");
        return status;
    }

    static async Task<string> ReadHeaderAsync(Stream stream, CancellationToken token)
    {
        var buffer = new byte[16384]; var one = new byte[1];
        for (var length = 0; length < buffer.Length; length++)
        {
            if (await stream.ReadAsync(one, token) == 0) throw new IOException("连接在完成协议响应前被关闭。");
            buffer[length] = one[0];
            if (length >= 3 && buffer[length - 3] == 13 && buffer[length - 2] == 10 && buffer[length - 1] == 13 && buffer[length] == 10)
                return Encoding.ASCII.GetString(buffer, 0, length + 1);
        }
        throw new IOException("代理响应头超出限制。");
    }

    [RelayCommand]
    static void OpenDataFolder()
    {
        AppPaths.EnsureWritableDataDirectory();
        Process.Start(new ProcessStartInfo("explorer.exe", AppPaths.DataDir) { UseShellExecute = true });
    }
}

