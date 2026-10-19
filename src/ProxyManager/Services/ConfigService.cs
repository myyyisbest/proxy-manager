using System.Collections.ObjectModel;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ProxyManager.Infrastructure;
using ProxyManager.Models;

namespace ProxyManager.Services;

/// <summary>UI 编辑 Current；后台只读取最后一次成功提交的 Snapshot。</summary>
public sealed class ConfigService
{
    static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    readonly object _gate = new();
    readonly string _dataDir;
    readonly bool _useAppPaths;
    AppConfig _committed = new();
    public AppConfig Current { get; private set; } = new();
    public string? LoadWarning { get; private set; }
    public string ConfigFile => Path.Combine(_dataDir, "config.json");
    public event Action? ConfigUpdated;
    public event Action? EngineRestartNeeded;

    public ConfigService(string? dataDirectory = null)
    {
        _useAppPaths = dataDirectory == null;
        _dataDir = dataDirectory ?? AppPaths.DataDir;
    }

    public void Load()
    {
        if (_useAppPaths) { AppPaths.EnsureWritableDataDirectory(); AppPaths.MigrateLegacyConfig(); }
        else Directory.CreateDirectory(_dataDir);
        LoadWarning = null;
        if (!File.Exists(ConfigFile)) { CommitLoaded(new()); return; }
        try
        {
            var cfg = Read(ConfigFile, out var needsRewrite);
            CommitLoaded(cfg);
            if (needsRewrite) Save(restartEngine: false);
        }
        catch (Exception ex) when (ex is JsonException or IOException or ArgumentException or FormatException or System.Security.Cryptography.CryptographicException)
        {
            var preserved = Path.Combine(_dataDir, $"config-damaged-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.json");
            try { File.Copy(ConfigFile, preserved); } catch { }
            try
            {
                var backup = Read(ConfigFile + ".bak", out _);
                CommitLoaded(backup);
                LoadWarning = "配置损坏，已读取最近备份；原配置已保留，请检查设置和规则。";
            }
            catch
            {
                CommitLoaded(new());
                LoadWarning = "无法读取配置，已使用默认设置；原配置已保留，请重新检查代理和规则。";
            }
            TryLogWarning(LoadWarning + " " + ex.Message);
        }
    }

    void CommitLoaded(AppConfig cfg)
    {
        Current = cfg;
        lock (_gate) _committed = Clone(cfg);
    }

    AppConfig Read(string path, out bool needsRewrite)
    {
        var json = File.ReadAllText(path);
        using var document = JsonDocument.Parse(json);
        var cfg = JsonSerializer.Deserialize<AppConfig>(json, JsonOpts) ?? throw new JsonException("配置根对象为空");
        needsRewrite = false;
        if (!document.RootElement.TryGetProperty("SchemaVersion", out var schema))
        {
            // 1.3 使用 fake-IP。迁移时保留原行为，新建配置默认使用系统 DNS。
            cfg.UseFakeIpDns = true;
            needsRewrite = true;
        }
        else if (schema.ValueKind == JsonValueKind.Number && schema.GetInt32() < 3)
        {
            // v2 及以前沿袭了旧版"关闭窗口退出"的显式设置；按托盘应用惯例迁移为驻留托盘。
            cfg.MinimizeToTray = true;
            needsRewrite = true;
        }
        if (cfg.Proxy == null || cfg.Rules == null) throw new JsonException("配置缺少代理或规则");
        if (!string.IsNullOrEmpty(cfg.Proxy.ProtectedCredentials))
        {
            try
            {
                var credentials = JsonSerializer.Deserialize<Credentials>(Dpapi.Unprotect(cfg.Proxy.ProtectedCredentials));
                cfg.Proxy.Username = credentials?.Username;
                cfg.Proxy.Password = credentials?.Password;
            }
            catch (Exception ex) when (ex is FormatException or JsonException or System.Security.Cryptography.CryptographicException)
            {
                cfg.Proxy.ProtectedCredentials = null;
                LoadWarning = "保存的代理凭据无法由当前 Windows 用户解密，请重新输入用户名和密码。";
                TryLogWarning(LoadWarning);
            }
        }
        else if (document.RootElement.TryGetProperty("Proxy", out var proxy))
        {
            if (proxy.TryGetProperty("Username", out var user) && user.ValueKind == JsonValueKind.String) cfg.Proxy.Username = user.GetString();
            if (proxy.TryGetProperty("Password", out var pass) && pass.ValueKind == JsonValueKind.String) cfg.Proxy.Password = pass.GetString();
            needsRewrite |= proxy.TryGetProperty("Username", out _) || proxy.TryGetProperty("Password", out _);
        }
        Validate(cfg);
        return cfg;
    }

    /// <summary>原子替换。失败不发布新快照、不通知引擎重启。</summary>
    public void Save(bool restartEngine = true)
    {
        var snapshot = Clone(Current);
        Validate(snapshot);
        if (_useAppPaths) AppPaths.EnsureWritableDataDirectory();
        else Directory.CreateDirectory(_dataDir);
        var persisted = PrepareForDisk(snapshot);
        var json = JsonSerializer.Serialize(persisted, JsonOpts);
        var temp = Path.Combine(_dataDir, $"config-{Guid.NewGuid():N}.tmp");
        lock (_gate)
        {
            try
            {
                WriteDurable(temp, json);
                // 备份也加密，迁移时不会把旧明文保留在 .bak 中。
                if (File.Exists(ConfigFile))
                {
                    var backupTemp = temp + ".bak";
                    try
                    {
                        WriteDurable(backupTemp, JsonSerializer.Serialize(PrepareForDisk(_committed), JsonOpts));
                        File.Move(backupTemp, ConfigFile + ".bak", true);
                    }
                    finally { if (File.Exists(backupTemp)) File.Delete(backupTemp); }
                    File.Move(temp, ConfigFile, true);
                }
                else File.Move(temp, ConfigFile);
                _committed = snapshot;
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        ConfigUpdated?.Invoke();
        if (restartEngine) EngineRestartNeeded?.Invoke();
    }

    static void WriteDurable(string path, string text)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(text); writer.Flush(); stream.Flush(flushToDisk: true);
    }

    static AppConfig PrepareForDisk(AppConfig snapshot)
    {
        var cfg = Clone(snapshot);
        cfg.Proxy.ProtectedCredentials = string.IsNullOrEmpty(cfg.Proxy.Username) && string.IsNullOrEmpty(cfg.Proxy.Password)
            ? null : Dpapi.Protect(JsonSerializer.Serialize(new Credentials(cfg.Proxy.Username, cfg.Proxy.Password)));
        return cfg;
    }

    public AppConfig Snapshot() { lock (_gate) return Clone(_committed); }

    public static AppConfig Clone(AppConfig cfg) => new()
    {
        SchemaVersion = 3, UseFakeIpDns = cfg.UseFakeIpDns,
        Proxy = new ProxyServer { Type = cfg.Proxy.Type, Host = cfg.Proxy.Host, Port = cfg.Proxy.Port, Username = cfg.Proxy.Username, Password = cfg.Proxy.Password },
        Rules = new ObservableCollection<AppRule>(cfg.Rules.Select(r => new AppRule { Id = r.Id, ProcessName = r.ProcessName, DisplayName = r.DisplayName, ExePath = r.ExePath, Enabled = r.Enabled })),
        StartEngineOnLaunch = cfg.StartEngineOnLaunch, AutoStartWithWindows = cfg.AutoStartWithWindows,
        MinimizeToTray = cfg.MinimizeToTray, Theme = cfg.Theme,
    };

    public static void Validate(AppConfig cfg)
    {
        if (cfg.Proxy == null || cfg.Rules == null) throw new ArgumentException("配置缺少代理或规则。");
        var p = cfg.Proxy;
        if (!Enum.IsDefined(p.Type)) throw new ArgumentException("不支持的代理协议。");
        p.Host = (p.Host ?? "").Trim().Trim('[', ']');
        if (string.IsNullOrEmpty(p.Host) || (!IPAddress.TryParse(p.Host, out _) && Uri.CheckHostName(p.Host) != UriHostNameType.Dns))
            throw new ArgumentException("请输入有效的代理主机名或 IP 地址，不要包含协议、路径或端口。");
        if (p.Port is < 1 or > 65535) throw new ArgumentException("代理端口必须在 1–65535 之间。");
        if (p.Type == ProxyType.Socks5 && (Encoding.UTF8.GetByteCount(p.Username ?? "") > 255 || Encoding.UTF8.GetByteCount(p.Password ?? "") > 255))
            throw new ArgumentException("SOCKS5 用户名和密码分别最多为 255 字节。");
        if (!string.IsNullOrEmpty(p.Password) && string.IsNullOrEmpty(p.Username)) throw new ArgumentException("填写密码时必须同时填写用户名。");
        if ((p.Username ?? "").Any(char.IsControl) || (p.Password ?? "").Any(char.IsControl)) throw new ArgumentException("凭据不能包含控制字符。");
        if (p.Type == ProxyType.Http && (p.Username ?? "").Contains(':')) throw new ArgumentException("HTTP 代理用户名不能包含冒号。");
        if (cfg.Rules.Count > 10000) throw new ArgumentException("规则数量过多。");
        foreach (var rule in cfg.Rules)
        {
            if (rule == null || string.IsNullOrWhiteSpace(rule.ProcessName) || rule.ProcessName.IndexOfAny(new[] { '/', '\\', '\r', '\n' }) >= 0)
                throw new ArgumentException("规则必须包含有效的进程文件名。");
            if (!string.IsNullOrWhiteSpace(rule.ExePath) && (!Path.IsPathFullyQualified(rule.ExePath) || rule.ExePath.Any(char.IsControl)))
                throw new ArgumentException("应用规则路径必须是完整路径。");
        }
        if (cfg.Theme is not ("Dark" or "Light" or "System")) cfg.Theme = "Dark";
    }

    static void TryLogWarning(string message) { try { AppServices.Log?.Warn(message); } catch { } }
    sealed record Credentials(string? Username, string? Password);

    static class Dpapi
    {
        [StructLayout(LayoutKind.Sequential)] struct Blob { public int Length; public IntPtr Data; }
        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool CryptProtectData(ref Blob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
        [DllImport("crypt32.dll", SetLastError = true)] static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
        [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr memory);
        public static string Protect(string value) => Convert.ToBase64String(Transform(Encoding.UTF8.GetBytes(value), true));
        public static string Unprotect(string value) => Encoding.UTF8.GetString(Transform(Convert.FromBase64String(value), false));
        static byte[] Transform(byte[] bytes, bool protect)
        {
            var input = new Blob { Length = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
            Blob output = default;
            try
            {
                Marshal.Copy(bytes, 0, input.Data, bytes.Length);
                var ok = protect ? CryptProtectData(ref input, "ProxyManager credentials", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                    : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
                if (!ok) throw new System.Security.Cryptography.CryptographicException(Marshal.GetLastWin32Error());
                var result = new byte[output.Length]; Marshal.Copy(output.Data, result, 0, output.Length); return result;
            }
            finally { Marshal.FreeHGlobal(input.Data); if (output.Data != IntPtr.Zero) LocalFree(output.Data); Array.Clear(bytes); }
        }
    }
}

