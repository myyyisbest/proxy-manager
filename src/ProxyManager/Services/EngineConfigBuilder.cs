using System.Text.Json;
using ProxyManager.Models;

namespace ProxyManager.Services;

/// <summary>TUN 分流：路径优先匹配；系统 DNS、内网和未选应用直连。</summary>
public static class EngineConfigBuilder
{
    static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static string Build(AppConfig cfg, int apiPort, string apiSecret = "")
    {
        ConfigService.Validate(cfg);
        if (apiPort is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(apiPort));
        var proxy = cfg.Proxy;

        var proxyOutbound = new Dictionary<string, object?>
        {
            ["type"] = proxy.Type == ProxyType.Http ? "http" : "socks",
            ["tag"] = "proxy",
            ["server"] = proxy.Host,
            ["server_port"] = proxy.Port,
        };
        if (proxy.Type == ProxyType.Socks5) proxyOutbound["version"] = "5";
        if (!string.IsNullOrEmpty(proxy.Username)) proxyOutbound["username"] = proxy.Username;
        if (!string.IsNullOrEmpty(proxy.Password)) proxyOutbound["password"] = proxy.Password;

        var processNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var processPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in cfg.Rules)
        {
            if (!rule.Enabled || string.IsNullOrWhiteSpace(rule.ProcessName)) continue;
            if (!string.IsNullOrWhiteSpace(rule.ExePath)) processPaths.Add(System.IO.Path.GetFullPath(rule.ExePath));
            else processNames.Add(rule.ProcessName.Trim());
        }

        var routeRules = new List<Dictionary<string, object?>>
        {
            // 保留 Windows/VPN 提供的 DNS。共享 DNS Client 无法可靠按应用归属，不能全局发 fake-ip。
            new() { ["port"] = new[] { 53 }, [cfg.UseFakeIpDns ? "action" : "outbound"] = cfg.UseFakeIpDns ? "hijack-dns" : "direct" },
            new() { ["ip_is_private"] = true, ["outbound"] = "direct" },
            // 防止上游代理自身恰好被选中造成递归。
            new() { ["process_name"] = new[] { "sing-box.exe", "ProxyManager.exe" }, ["outbound"] = "direct" },
        };
        if (processPaths.Count > 0)
            routeRules.Add(new() { ["process_path"] = processPaths.ToArray(), ["outbound"] = "proxy" });
        if (processNames.Count > 0)
            routeRules.Add(new() { ["process_name"] = processNames.ToList(), ["outbound"] = "proxy" });
        var directOutbound = new Dictionary<string, object?> { ["type"] = "direct", ["tag"] = "direct" };
        if (cfg.UseFakeIpDns)
        {
            // A fake IPv6 address can represent an IPv4-only site (and vice versa).
            // Resolve the real domain independently of the virtual packet family.
            // Bound each family lookup so a stalled query cannot indefinitely hold
            // back a successful result from the other family.
            directOutbound["domain_resolver"] = new Dictionary<string, object?>
                { ["server"] = "dns-local", ["strategy"] = "prefer_ipv4", ["timeout"] = "3s" };
        }
        var outbounds = new object[] { proxyOutbound, directOutbound };

        var root = new Dictionary<string, object?>
        {
            // warning 级别：info 的 DNS/连接日志每秒几十行，会把日志页和日志文件刷爆导致界面卡顿
            ["log"] = new Dictionary<string, object?> { ["level"] = "warning", ["timestamp"] = true },
            ["dns"] = new Dictionary<string, object?>
            {
                ["servers"] = new object[]
                {
                    new Dictionary<string, object?> { ["type"] = "local", ["tag"] = "dns-local" },
                },
                ["final"] = "dns-local",
                ["strategy"] = "prefer_ipv4",
            },
            ["inbounds"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["type"] = "tun",
                    ["tag"] = "tun-in",
                    ["interface_name"] = "ProxyManager",
                    ["address"] = new[] { "172.19.0.1/30", "fdfe:dcba:9876::1/126" },
                    ["mtu"] = 1500,
                    ["auto_route"] = true,
                    ["dns_mode"] = cfg.UseFakeIpDns ? "hijack" : "disabled",
                    ["strict_route"] = false,
                    ["stack"] = "mixed",
                    ["endpoint_independent_nat"] = false,
                },
            },
            ["outbounds"] = outbounds,
            ["route"] = new Dictionary<string, object?>
            {
                ["rules"] = routeRules,
                ["final"] = "direct",
                ["auto_detect_interface"] = true,
                ["default_domain_resolver"] = "dns-local",
            },
            ["experimental"] = new Dictionary<string, object?>
            {
                ["clash_api"] = new Dictionary<string, object?> { ["external_controller"] = $"127.0.0.1:{apiPort}", ["secret"] = apiSecret },
            },
        };

        if (cfg.UseFakeIpDns)
        {
            var dns = (Dictionary<string, object?>)root["dns"]!;
            dns["servers"] = new object[]
            {
                new Dictionary<string, object?> { ["type"] = "local", ["tag"] = "dns-local" },
                new Dictionary<string, object?> { ["type"] = "fakeip", ["tag"] = "dns-fakeip",
                    ["inet4_range"] = "198.18.0.0/15", ["inet6_range"] = "fc00::/18" },
            };
            dns["rules"] = new object[]
            {
                new Dictionary<string, object?> { ["domain_suffix"] = new[] { ".local", ".lan", ".home", ".internal", ".arpa" }, ["server"] = "dns-local" },
                new Dictionary<string, object?> { ["query_type"] = new[] { "A", "AAAA" }, ["server"] = "dns-fakeip", ["rewrite_ttl"] = 1 },
            };
            ((Dictionary<string, object?>)root["experimental"]!)["cache_file"] = new Dictionary<string, object?>
                { ["enabled"] = true, ["path"] = "dns-cache.db", ["store_fakeip"] = true };
        }

        return JsonSerializer.Serialize(root, JsonOpts);
    }
}
