using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ProxyManager;
using ProxyManager.Infrastructure;
using ProxyManager.Models;
using ProxyManager.Services;
using ProxyManager.ViewModels;
using ProxyManager.Views;
using ProxyManager.Views.Pages;

internal static class Program
{
    static readonly List<object> Results = new();
    static string Root = "";
    static string FixtureExe => Path.Combine(AppContext.BaseDirectory, "ProxyManager.Tests.exe");

    [STAThread]
    static int Main(string[] args)
    {
        if (args.FirstOrDefault() is "run" or "check" or "version" or "hang" or "flood" or "fixture-host" or "network-probe")
        {
            try { return Fixture(args).GetAwaiter().GetResult(); }
            catch (Exception ex) { Console.Error.WriteLine(ex); File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "fixture-errors.log"), ex + "\n"); return 9; }
        }
        Root = Path.GetFullPath(args.Length >= 2 && args[0] == "--ui" ? args[1] :
            Path.Combine(AppContext.BaseDirectory, "test-data", DateTime.UtcNow.ToString("yyyyMMddHHmmssfff")));
        Directory.CreateDirectory(Root);
        AppPaths.ConfigureDataDirectory(Root);
        if (args.FirstOrDefault() == "--ui") return UiSession();
        try
        {
            AppServices.Init();
            if (args.Contains("--ui-checks")) { Check("UI acceptance", TestUi); return 0; }
            if (args.Contains("--proxy-checks")) { Check("proxy probe: SOCKS5 and HTTP auth, trusted TLS and HTTPS", TestProbeSuccess); return 0; }
            if (args.Contains("--network-check") || args.Contains("--network-check-ipv4"))
            {
                var includeIpv6 = !args.Contains("--network-check-ipv4");
                Check(includeIpv6 ? "real TUN: IPv4/IPv6 selected/direct processes, enhanced DNS and cleanup"
                    : "real TUN: IPv4 selected/direct processes, enhanced DNS and cleanup (IPv6 not tested)", () => TestNetwork(includeIpv6));
                Check("proxy probe: SOCKS5 and HTTP auth, trusted TLS and HTTPS", TestProbeSuccess);
                File.WriteAllText(Path.Combine(Root, "results.json"), JsonSerializer.Serialize(Results, new JsonSerializerOptions { WriteIndented = true }));
                return 0;
            }
            Check("configuration: encryption, reload, stable snapshot, backup recovery", TestConfig);
            Check("configuration: validation rejects invalid host and port", TestValidation);
            Check("routing: exact paths and both DNS modes pass real core validation", TestBuilder);
            Check("engine command: bounded timeout and concurrent stdout/stderr", TestExecution);
            Check("engine: start, auth, reject invalid update, rollback, restart, stop", TestEngine);
            Check("engine: a save arriving during a swap is processed after the swap", TestEnginePendingSave);
            Check("engine: abrupt host termination kills only its owned child", TestJobCrash);
            Check("status: bearer auth, numeric port, IPv6, reset", TestStatus);
            Check("proxy probe: protocol mismatch and bad authentication", TestProbeFailures);
            Check("proxy probe: cancellation interrupts a silent upstream", TestProbeCancellation);
            Check("UI: layout uses top space, wheel scrolls, virtualized list", TestUi);
            File.WriteAllText(Path.Combine(Root, "results.json"), JsonSerializer.Serialize(Results, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("ALL CHECKS PASSED " + Path.Combine(Root, "results.json"));
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            foreach (var entry in AppServices.Log?.Snapshot().TakeLast(20) ?? Array.Empty<LogEntry>()) Console.Error.WriteLine(entry.Message);
            File.WriteAllText(Path.Combine(Root, "failure.txt"), ex.ToString());
            return 1;
        }
        finally
        {
            try { AppServices.Engine?.Stop(); } catch { }
            AppServices.Status?.Dispose(); AppServices.Log?.Dispose();
        }
    }

    static void Check(string name, Action test)
    {
        var timer = Stopwatch.StartNew(); test();
        Results.Add(new { name, passed = true, elapsedMs = timer.ElapsedMilliseconds });
        Console.WriteLine("PASS " + name);
    }
    static void Expect(bool condition, string description) { if (!condition) throw new Exception(description); }
    static void Until(Func<bool> condition, int timeout = 10000)
    {
        var timer = Stopwatch.StartNew();
        while (!condition()) { if (timer.ElapsedMilliseconds > timeout) throw new TimeoutException("Condition not reached"); Thread.Sleep(20); }
    }
    static AppConfig Config(string host = "127.0.0.1") => new() { Proxy = new() { Host = host, Port = 7890 } };

    static void TestConfig()
    {
        var dir = Path.Combine(Root, "config"); var cfg = new ConfigService(dir); cfg.Load();
        cfg.Current.Proxy.Username = "test-user"; cfg.Current.Proxy.Password = " secret with spaces ";
        cfg.Save(false);
        var json = File.ReadAllText(cfg.ConfigFile);
        Expect(!json.Contains("test-user") && !json.Contains("secret with spaces"), "Plain credentials on disk");
        var reload = new ConfigService(dir); reload.Load();
        Expect(reload.Current.Proxy.Password == " secret with spaces ", "Credentials changed on reload");
        reload.Current.Proxy.Port = 9999;
        Expect(reload.Snapshot().Proxy.Port == 7890, "Uncommitted edits leaked into runtime snapshot");
        reload.Save(false); File.WriteAllText(reload.ConfigFile, "{ damaged");
        var recovered = new ConfigService(dir); recovered.Load();
        Expect(recovered.Current.Proxy.Port == 7890 && recovered.LoadWarning != null, "Backup recovery failed");
        Expect(Directory.GetFiles(dir, "config-damaged-*.json").Length == 1, "Damaged configuration not preserved");
        // v2 配置（1.4.2 及以前）显式保存过"关闭窗口退出"；迁移到 v3 时改为驻留托盘。
        const string v2 = """{"SchemaVersion":2,"UseFakeIpDns":true,"Proxy":{"Type":0,"Host":"127.0.0.1","Port":7890},"Rules":[],"StartEngineOnLaunch":false,"AutoStartWithWindows":false,"MinimizeToTray":false,"Theme":"Dark"}""";
        File.WriteAllText(reload.ConfigFile, v2);
        var upgraded = new ConfigService(dir); upgraded.Load();
        Expect(upgraded.Current.MinimizeToTray && File.ReadAllText(upgraded.ConfigFile).Contains("\"SchemaVersion\": 3"),
            "v2 configuration did not migrate to tray-dwelling default");
    }
    static void TestValidation()
    {
        foreach (var cfg in new[] { Config("http://bad/path"), new AppConfig { Proxy = new() { Port = 65536 } } })
        {
            try { ConfigService.Validate(cfg); throw new Exception("Invalid configuration accepted"); }
            catch (ArgumentException) { }
        }
    }
    static void TestExecution()
    {
        var timer = Stopwatch.StartNew();
        var result = EngineService.RunToCompletion(FixtureExe, "hang", 150);
        Expect(result.ExitCode == -1 && timer.ElapsedMilliseconds < 2500, "Timeout failed");
        result = EngineService.RunToCompletion(FixtureExe, "flood", 5000);
        Expect(result.ExitCode == 0 && result.Output.Contains("stdout-end") && result.Output.Contains("stderr-end"), "Redirected streams blocked or missing");
    }
    static void TestBuilder()
    {
        foreach (var enhanced in new[] { false, true })
        {
            var cfg = Config(); cfg.UseFakeIpDns = enhanced;
            cfg.Proxy.Username = " fixture-user "; cfg.Proxy.Password = "   ";
            cfg.Rules.Add(new AppRule { ProcessName = "sample.exe", ExePath = "C:\\One\\sample.exe" });
            cfg.Rules.Add(new AppRule { ProcessName = "sample.exe", ExePath = "C:\\Two\\sample.exe" });
            cfg.Rules.Add(new AppRule { ProcessName = "disabled.exe", Enabled = false });
            var json = EngineConfigBuilder.Build(cfg, 19097, "unit-test-secret");
            using var doc = JsonDocument.Parse(json);
            Expect(doc.RootElement.GetProperty("log").GetProperty("level").GetString() == "warning", "Diagnostic logging leaked into production configuration");
            var tun = doc.RootElement.GetProperty("inbounds")[0];
            var proxy = doc.RootElement.GetProperty("outbounds")[0];
            Expect(proxy.GetProperty("username").GetString() == cfg.Proxy.Username && proxy.GetProperty("password").GetString() == cfg.Proxy.Password,
                "Core credentials lost significant whitespace");
            Expect(tun.GetProperty("dns_mode").GetString() == (enhanced ? "hijack" : "disabled"), "DNS mode not explicit");
            var resolutions = doc.RootElement.GetProperty("outbounds").EnumerateArray()
                .Where(r => r.TryGetProperty("domain_resolver", out _)).ToArray();
            Expect(resolutions.Length == (enhanced ? 1 : 0), "Enhanced direct resolver is missing");
            if (enhanced)
                Expect(resolutions[0].GetProperty("domain_resolver").GetProperty("strategy").GetString() == "prefer_ipv4"
                    && resolutions[0].GetProperty("domain_resolver").GetProperty("timeout").GetString() == "3s", "Direct resolution lost dual-stack support or its query timeout");
            Expect(!json.Contains("disabled.exe") && json.Contains("C:\\\\One\\\\sample.exe") && json.Contains("C:\\\\Two\\\\sample.exe"), "Path rules are inaccurate");
            var path = Path.Combine(Root, "builder-" + enhanced + ".json"); File.WriteAllText(path, json);
            var result = EngineService.RunToCompletion(AppPaths.ResolveEnginePath(), $"check -c \"{path}\" --disable-color", 10000);
            Expect(result.ExitCode == 0, "Core rejected configuration: " + result.Output);
        }
    }
    static void TestEngine()
    {
        var cfg = Config(); var dir = Path.Combine(Root, "engine");
        using var engine = new EngineService(FixtureExe, dir, () => ConfigService.Clone(cfg));
        engine.Start(cfg); Expect(engine.IsRunning && engine.ApiSecret.Length >= 32, "Start/readiness failed");
        var firstPort = engine.ApiPort;
        cfg.Proxy.Host = "check-fail.invalid"; engine.RestartIfRunning(0);
        Until(() => engine.LastError.Contains("新配置未生效"));
        Expect(engine.IsRunning && engine.ApiPort == firstPort, "Invalid candidate stopped existing engine");
        cfg.Proxy.Host = "run-fail.invalid"; engine.RestartIfRunning(0);
        Until(() => engine.IsRunning && engine.LastError.Contains("已恢复"));
        Expect(engine.ApiPort == firstPort, "Rollback did not restore previous runtime config");
        for (int i = 0; i < 4; i++)
        {
            cfg.Proxy.Host = "127.0.0.1"; var previousPort = engine.ApiPort; engine.RestartIfRunning(0);
            Until(() => engine.IsRunning && engine.ApiPort != previousPort);
            Thread.Sleep(100); Expect(engine.IsRunning, "An old exit callback killed the current generation");
        }
        engine.Stop(); Expect(engine.State == EngineState.Stopped, "Stop failed");
        Expect(Directory.GetFiles(Path.Combine(dir, "runtime"), "*.json").Length == 0, "Runtime secrets remained after stop");
        engine.Start(cfg); engine.RestartIfRunning(200); engine.Stop(); Thread.Sleep(350);
        Expect(engine.State == EngineState.Stopped, "Debounced restart revived a stopped engine");
    }
    static void TestEnginePendingSave()
    {
        var cfg = Config(); var dir = Path.Combine(Root, "engine-pending");
        using var engine = new EngineService(FixtureExe, dir, () => ConfigService.Clone(cfg));
        engine.Start(cfg);
        Environment.SetEnvironmentVariable("FIXTURE_API_DELAY_MS", "1200");
        try
        {
            cfg.Proxy.Host = "swap-one.invalid"; engine.RestartIfRunning(0);
            Thread.Sleep(200);
            Expect(engine.State is EngineState.Stopping or EngineState.Starting, "Swap window was not observed");
            // 该保存到达时上一次切换尚未完成；它必须被置位补跑，而不是被静默丢弃。
            // 让补跑的候选配置故意校验失败：只要"新配置未生效"出现，就证明补跑确实执行了。
            cfg.Proxy.Host = "check-fail.invalid"; engine.RestartIfRunning(0);
            Until(() => engine.LastError.Contains("新配置未生效"), 15000);
            Expect(engine.IsRunning, "Pending candidate failure stopped the running engine");
            Thread.Sleep(200);
            Expect(engine.IsRunning && engine.State == EngineState.Running, "Engine unstable after pending swap");
        }
        finally { Environment.SetEnvironmentVariable("FIXTURE_API_DELAY_MS", null); }
        engine.Stop(); Expect(engine.State == EngineState.Stopped, "Stop after pending swap failed");
    }
    static void TestStatus()
    {
        using var status = new StatusService(new HttpClient(new Samples()));
        status.PollAsync(19097, "fixture-secret").GetAwaiter().GetResult();
        Expect(status.ApiAlive && status.Proxied.Single().Host == "[2001:db8::1]:443", "Numeric port/IPv6 parsing failed");
        Expect(status.Proxied.Single().Process == "sample.exe", "processPath fallback failed");
        status.Reset(); Expect(!status.ApiAlive && status.DownSpeed == 0 && status.Proxied.Count == 0, "Reset retained stale data");
    }
    static void TestJobCrash()
    {
        var dir = Path.Combine(Root, "job-crash"); Directory.CreateDirectory(dir);
        var config = Path.Combine(dir, "fixture.json"); var state = Path.Combine(dir, "child.txt");
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        File.WriteAllText(config, EngineConfigBuilder.Build(Config(), port, "job-crash-test"));
        using var host = new Process { StartInfo = new ProcessStartInfo(FixtureExe) { UseShellExecute = false, CreateNoWindow = true } };
        using var unrelated = Process.Start(new ProcessStartInfo(FixtureExe, "hang") { UseShellExecute = false, CreateNoWindow = true })!;
        host.StartInfo.ArgumentList.Add("fixture-host"); host.StartInfo.ArgumentList.Add(state); host.StartInfo.ArgumentList.Add(config);
        host.Start(); Process? child = null;
        try
        {
            Until(() => File.Exists(state) || host.HasExited);
            Expect(!host.HasExited, "Fixture host exited before the test");
            child = Process.GetProcessById(int.Parse(File.ReadAllText(state)));
            Expect(!child.HasExited, "Fixture child was not running");
            host.Kill(); host.WaitForExit(3000);
            Expect(child.WaitForExit(5000), "Child survived abrupt termination of the Job owner");
            Expect(!unrelated.HasExited, "Stopping an owned child affected an unrelated same-image process");
        }
        finally
        {
            if (!host.HasExited) host.Kill();
            if (child != null) { if (!child.HasExited) child.Kill(); child.Dispose(); }
            if (!unrelated.HasExited) unrelated.Kill();
        }
    }
    static string DnsSnapshot() => string.Join("|", NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.Name != "ProxyManager").OrderBy(n => n.Id)
        .Select(n => n.Id + ":" + string.Join(",", n.GetIPProperties().DnsAddresses)));
    [DllImport("dnsapi.dll", SetLastError = true)]
    static extern bool DnsFlushResolverCache();
    static void TestNetwork(bool includeIpv6)
    {
        Expect(!NetworkInterface.GetAllNetworkInterfaces().Any(n => n.Name == "ProxyManager"), "An existing product TUN is active; do not touch it");
        var beforeDns = DnsSnapshot(); var probeExe = Path.Combine(AppContext.BaseDirectory, "SelectedProbe.exe");
        File.Copy(FixtureExe, probeExe, true);
        const string directUrl = "http://www.msftconnecttest.com/connecttest.txt";
        const string directV6Url = "http://ipv6.msftconnecttest.com/connecttest.txt";
        var directBody = ProbeBody(RunProbe(FixtureExe, directUrl, "baseline-direct"));
        var directV6Body = includeIpv6 ? ProbeBody(RunProbe(FixtureExe, directV6Url, "baseline-direct-v6")) : null;
        Expect(directBody.Contains("Microsoft Connect Test", StringComparison.Ordinal), "Direct baseline was intercepted or unavailable");
        Expect(!includeIpv6 || Dns.GetHostAddresses("ipv6.msftconnecttest.com").All(a => a.AddressFamily == AddressFamily.InterNetworkV6)
            && directV6Body == directBody, "IPv6 direct baseline was unavailable");
        using var proxy = new MockSocks();
        var cfg = Config(); cfg.Proxy.Port = proxy.Port;
        cfg.Rules.Add(new AppRule { ProcessName = "SelectedProbe.exe", DisplayName = "Acceptance probe", ExePath = probeExe });
        using var engine = new EngineService(AppPaths.ResolveEnginePath(), Path.Combine(Root, "real-engine"), () => ConfigService.Clone(cfg));
        var stopMilliseconds = new List<long>();
        try
        {
            engine.Start(cfg);
            var selected = RunProbe(probeExe, "http://1.1.1.1:8089/proxy-manager-acceptance", "selected");
            Expect(ProbeBody(selected) == "proxied-by-fixture", "Selected executable did not use the configured upstream");
            var count = proxy.Targets.Count;
            Expect(DnsFlushResolverCache(), "Could not clear DNS cache before direct-path acceptance");
            var unselected = RunProbe(FixtureExe, directUrl, "unselected");
            Expect(ProbeBody(unselected) == directBody && proxy.Targets.Count == count, "Unselected executable did not retain its working direct path");
            Expect(!includeIpv6 || ProbeBody(RunProbe(FixtureExe, directV6Url, "unselected-v6")) == directV6Body && proxy.Targets.Count == count,
                "Unselected IPv6 traffic did not retain its working direct path");
            var stopTimer = Stopwatch.StartNew(); engine.Stop(); stopMilliseconds.Add(stopTimer.ElapsedMilliseconds);
            cfg.UseFakeIpDns = true; engine.Start(cfg);
            using var traceStop = new CancellationTokenSource();
            var tracePath = Path.Combine(Root, "enhanced-trace.jsonl");
            var trace = CaptureEngineLogs(engine, tracePath, traceStop.Token);
            try
            {
            var domain = "pm-" + Guid.NewGuid().ToString("N") + ".invalid";
            var enhanced = RunProbe(probeExe, "http://" + domain + ":8089/proxy-manager-acceptance", "enhanced-dns");
            Expect(ProbeBody(enhanced) == "proxied-by-fixture" && proxy.Targets.Any(t => t.StartsWith(domain + ":", StringComparison.Ordinal)), "Enhanced DNS did not hand the original domain to the proxy");
            count = proxy.Targets.Count;
            Expect(DnsFlushResolverCache(), "Could not clear DNS cache before enhanced direct-path acceptance");
            var enhancedDirect = RunProbe(FixtureExe, directUrl, "enhanced-direct", forceIpv6: true);
            Expect(ProbeBody(enhancedDirect) == directBody && proxy.Targets.Count == count, "Enhanced DNS broke unselected applications' direct path");
            Expect(!includeIpv6 || ProbeBody(RunProbe(FixtureExe, directV6Url, "enhanced-direct-v6")) == directV6Body && proxy.Targets.Count == count,
                "Enhanced DNS broke unselected IPv6 traffic");
            }
            finally { traceStop.Cancel(); trace.GetAwaiter().GetResult(); }
            var traceMessages = File.ReadAllLines(tracePath).Select(line => {
                using var message = JsonDocument.Parse(line);
                return message.RootElement.GetProperty("payload").GetString() ?? "";
            }).ToArray();
            Expect(traceMessages.Any(m => m.Contains("outbound/direct[direct]") && m.Contains("www.msftconnecttest.com:80")), "Fake IPv6 probe did not use the real-domain direct dialer");
            Expect(!includeIpv6 || traceMessages.Any(m => m.Contains("outbound/direct[direct]") && m.Contains("ipv6.msftconnecttest.com:80")), "IPv6 probe did not use the direct dialer");
        }
        finally { var stopTimer = Stopwatch.StartNew(); engine.Stop(); stopMilliseconds.Add(stopTimer.ElapsedMilliseconds); }
        Until(() => !NetworkInterface.GetAllNetworkInterfaces().Any(n => n.Name == "ProxyManager"), 5000);
        Expect(DnsSnapshot() == beforeDns, "Existing network interfaces' DNS settings changed");
        Expect(ProbeBody(RunProbe(FixtureExe, directUrl, "restored-direct")) == directBody, "Direct connectivity did not recover after stopping");
        File.WriteAllText(Path.Combine(Root, "network-evidence.json"), JsonSerializer.Serialize(new {
            scope = includeIpv6 ? "IPv4/IPv6" : "IPv4 only; IPv6 not tested",
            targets = proxy.Targets.ToArray(), normalDirectWorks = true, enhancedDirectWorks = true, ipv6DirectWorks = includeIpv6 ? (bool?)true : null,
            restoredDirectWorks = true, tunRemoved = true, originalDnsPreserved = true, stopMilliseconds }));
    }
    static string ProbeBody(string json)
    {
        using var document = JsonDocument.Parse(json);
        Expect(document.RootElement.GetProperty("success").GetBoolean(), "Probe failed: " + json);
        return document.RootElement.GetProperty("body").GetString() ?? "";
    }
    static async Task CaptureEngineLogs(EngineService engine, string path, CancellationToken token)
    {
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(2) }) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", engine.ApiSecret);
        try
        {
            using var response = await client.GetAsync($"http://127.0.0.1:{engine.ApiPort}/logs?level=debug", HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(token));
            await using var writer = new StreamWriter(path, false, Encoding.UTF8);
            for (var count = 0; count < 2000; count++)
            {
                var line = await reader.ReadLineAsync(token);
                if (line == null) break;
                await writer.WriteLineAsync(line);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or HttpRequestException) { }
    }
    static string RunProbe(string exe, string url, string name, bool forceIpv6 = false)
    {
        var result = Path.Combine(Root, name + ".json");
        var run = EngineService.RunToCompletion(exe, $"network-probe \"{url}\" \"{result}\"{(forceIpv6 ? " force-fake-ipv6" : "")}", 10000);
        Expect(run.ExitCode == 0, "Probe process failed: " + run.Output);
        Expect(File.Exists(result), "Probe produced no result: " + run.Output);
        return File.ReadAllText(result);
    }
    static void TestProbeFailures()
    {
        foreach (var http in new[] { true, false })
        {
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var server = Task.Run(async () =>
            {
                using var connection = await listener.AcceptTcpClientAsync(); using var stream = connection.GetStream();
                var data = new byte[4096]; await stream.ReadAsync(data);
                await stream.WriteAsync(http ? Encoding.ASCII.GetBytes("HTTP/1.1 407 Proxy Authentication Required\r\nContent-Length: 0\r\n\r\n") : new byte[] { 5, 255 });
            });
            try
            {
                SettingsViewModel.ProbeProxyAsync(new ProxyServer { Host = "127.0.0.1", Port = port, Type = http ? ProxyType.Http : ProxyType.Socks5 })
                    .GetAwaiter().GetResult();
                throw new Exception("Rejected proxy handshake reported success");
            }
            catch (IOException) { }
            finally { listener.Stop(); server.GetAwaiter().GetResult(); }
        }
    }
    static void TestProbeCancellation()
    {
        foreach (var type in new[] { ProxyType.Socks5, ProxyType.Http })
        {
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
            var server = Task.Run(async () => {
                try
                {
                    using var client = await listener.AcceptTcpClientAsync(cancel.Token);
                    await Task.Delay(Timeout.Infinite, cancel.Token);
                }
                catch (OperationCanceledException) { }
            });
            var timer = Stopwatch.StartNew();
            try
            {
                SettingsViewModel.ProbeProxyAsync(new ProxyServer { Host = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port, Type = type }, cancel.Token)
                    .GetAwaiter().GetResult();
                throw new Exception("Silent upstream did not cancel");
            }
            catch (OperationCanceledException) { Expect(timer.ElapsedMilliseconds < 2000, "Proxy cancellation stalled"); }
            finally { cancel.Cancel(); listener.Stop(); server.GetAwaiter().GetResult(); }
        }
    }
    static void TestProbeSuccess()
    {
        // Relay actual TLS unchanged: production certificate validation stays enabled.
        foreach (var type in new[] { ProxyType.Socks5, ProxyType.Http })
        {
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var proxy = new ProxyServer { Host = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port,
                Type = type, Username = " fixture-user ", Password = "   " };
            var server = Task.Run(async () =>
            {
                using var incoming = await listener.AcceptTcpClientAsync(cancel.Token);
                using var stream = incoming.GetStream();
                if (type == ProxyType.Socks5)
                {
                    var hello = new byte[3]; await stream.ReadExactlyAsync(hello, cancel.Token);
                    Expect(hello.SequenceEqual(new byte[] { 5, 1, 2 }), "Probe did not request SOCKS5 authentication");
                    await stream.WriteAsync(new byte[] { 5, 2 }, cancel.Token);
                    var version = new byte[1]; await stream.ReadExactlyAsync(version, cancel.Token);
                    Expect(version[0] == 1, "Invalid SOCKS5 auth version");
                    async Task<string> ReadString()
                    {
                        var length = new byte[1]; await stream.ReadExactlyAsync(length, cancel.Token);
                        var value = new byte[length[0]]; await stream.ReadExactlyAsync(value, cancel.Token);
                        return Encoding.UTF8.GetString(value);
                    }
                    Expect(await ReadString() == proxy.Username && await ReadString() == proxy.Password, "SOCKS5 auth changed credential whitespace");
                    await stream.WriteAsync(new byte[] { 1, 0 }, cancel.Token);
                    var request = new byte[4]; await stream.ReadExactlyAsync(request, cancel.Token);
                    Expect(request.SequenceEqual(new byte[] { 5, 1, 0, 3 }), "Invalid SOCKS5 CONNECT request");
                    Expect(await ReadString() == "www.microsoft.com", "SOCKS5 CONNECT target changed");
                    var port = new byte[2]; await stream.ReadExactlyAsync(port, cancel.Token);
                    Expect(((port[0] << 8) | port[1]) == 443, "SOCKS5 CONNECT port changed");
                }
                else
                {
                    using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true);
                    Expect(await reader.ReadLineAsync(cancel.Token) == "CONNECT www.microsoft.com:443 HTTP/1.1", "HTTP CONNECT target changed");
                    var headers = new List<string>();
                    for (var i = 0; i < 100; i++)
                    {
                        var line = await reader.ReadLineAsync(cancel.Token) ?? throw new IOException("Proxy client disconnected");
                        if (line.Length == 0) break;
                        headers.Add(line);
                    }
                    var auth = Convert.ToBase64String(Encoding.UTF8.GetBytes(proxy.Username + ":" + proxy.Password));
                    Expect(headers.Contains("Proxy-Authorization: Basic " + auth), "HTTP auth changed credential whitespace");
                }
                using var upstream = new TcpClient();
                await upstream.ConnectAsync("www.microsoft.com", 443, cancel.Token);
                using var remote = upstream.GetStream();
                await stream.WriteAsync(type == ProxyType.Socks5 ? new byte[] { 5, 0, 0, 1, 127, 0, 0, 1, 0, 0 }
                    : Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection Established\r\n\r\n"), cancel.Token);
                var upload = stream.CopyToAsync(remote, cancel.Token);
                var download = remote.CopyToAsync(stream, cancel.Token);
                await Task.WhenAny(upload, download);
                incoming.Close(); upstream.Close();
                try { await Task.WhenAll(upload, download); }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
            });
            try { SettingsViewModel.ProbeProxyAsync(proxy, cancel.Token).GetAwaiter().GetResult(); }
            finally { cancel.Cancel(); listener.Stop(); server.GetAwaiter().GetResult(); }
        }
    }

    static Application InitializeUi()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ThemesDictionary { Theme = Wpf.Ui.Appearance.ApplicationTheme.Dark });
        app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ControlsDictionary());
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/ProxyManager;component/Resources/AppTheme.xaml", UriKind.Relative) });
        AppServices.Init(); AppServices.Config.Current.MinimizeToTray = false;
        if (AppServices.Config.Current.Rules.Count == 0)
            for (var i = 0; i < 120; i++) AppServices.Config.Current.Rules.Add(new AppRule {
                ProcessName = $"sample{i}.exe", DisplayName = $"测试应用 {i + 1}", ExePath = $"C:\\TestApplications\\sample{i}.exe" });
        return app;
    }
    static IEnumerable<DependencyObject> Descendants(DependencyObject node)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            var child = VisualTreeHelper.GetChild(node, i); yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
    static void Pump()
    {
        var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
    static void TestUi()
    {
        var app = InitializeUi();
        var uiErrors = new List<Exception>();
        app.DispatcherUnhandledException += (_, e) => { uiErrors.Add(e.Exception); e.Handled = true; };
        var window = new MainWindow { Title = "ProxyManager 验收", ShowInTaskbar = false, Left = -9000, Top = -9000 };
        window.Show(); Pump(); window.UpdateLayout();
        var nav = Descendants(window).OfType<Wpf.Ui.Controls.NavigationView>().First();
        nav.Navigate(typeof(RulesPage)); Pump(); window.UpdateLayout();
        var rules = Descendants(window).OfType<RulesPage>().Single();
        var list = Descendants(rules).OfType<ListBox>().Single();
        var first = list.ItemContainerGenerator.ContainerFromIndex(0) as FrameworkElement ?? throw new Exception("No visible first rule");
        var firstTop = first.TranslatePoint(new Point(), rules).Y;
        Expect(firstTop < 160, $"Rule list still has unused top gap: {firstTop}");
        var realized = Descendants(list).OfType<ListBoxItem>().Count();
        Console.WriteLine(JsonSerializer.Serialize(new { firstTop, realized, listHeight = list.ActualHeight, rulesHeight = rules.ActualHeight,
            windowHeight = window.ActualHeight, presenters = Descendants(window).OfType<Wpf.Ui.Controls.NavigationViewContentPresenter>().Select(p => p.IsDynamicScrollViewerEnabled).ToArray(),
            scrolls = Descendants(window).OfType<ScrollViewer>().Select(s => new { s.Name, s.ActualHeight, s.ExtentHeight, s.ViewportHeight }).ToArray() }));
        Expect(realized < 40, "Rule list did not virtualize");
        nav.Navigate(typeof(SettingsPage)); Pump(); window.UpdateLayout();
        var settings = Descendants(window).OfType<SettingsPage>().Single();
        var scroll = (ScrollViewer)settings.FindName("SettingsScroll");
        Expect(scroll.ScrollableHeight > 100, $"Settings cannot scroll: extent {scroll.ExtentHeight}, viewport {scroll.ViewportHeight}");
        scroll.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120) { RoutedEvent = Mouse.PreviewMouseWheelEvent, Source = scroll });
        Pump(); window.UpdateLayout(); Expect(scroll.VerticalOffset > 0, "Wheel did not move settings");
        var settingsVm = (SettingsViewModel)settings.DataContext; var oldHost = AppServices.Config.Snapshot().Proxy.Host;
        settingsVm.Host = "uncommitted.invalid";
        Expect(AppServices.Config.Snapshot().Proxy.Host == oldHost, "Typing settings changed live config");
        var screenshots = Path.Combine(Root, "screenshots"); Directory.CreateDirectory(screenshots);
        foreach (var theme in new[] { "Light", "Dark" })
        {
            settingsVm.ThemeIndex = theme == "Light" ? 1 : 0; Pump(); window.UpdateLayout();
            Expect(AppServices.Config.Snapshot().Theme == theme, "Theme selection did not save the active preference");
            foreach (var key in new[] { "ApplicationBackgroundBrush", "TextFillColorPrimaryBrush", "ControlFillColorSecondaryBrush", "SystemFillColorSuccessBrush", "SystemFillColorCriticalBrush" })
                Expect(app.TryFindResource(key) is Brush, "Missing theme brush: " + key);
            var background = ((SolidColorBrush)window.Background).Color;
            Console.WriteLine(JsonSerializer.Serialize(new { theme, background = background.ToString(), resource = app.TryFindResource("ApplicationBackgroundBrush")?.ToString(), cached = Wpf.Ui.Appearance.ApplicationThemeManager.GetAppTheme().ToString(), dictionaries = app.Resources.MergedDictionaries.Select(d => d.Source?.ToString()).ToArray() }));
            Expect(background.A == 255 && (theme == "Light" ? background.R > 200 : background.R < 80), "Window background does not track theme");
            foreach (var page in new[] { typeof(DashboardPage), typeof(RulesPage), typeof(LogPage), typeof(SettingsPage) })
            {
                nav.Navigate(page); Pump(); window.UpdateLayout();
                Expect(((FrameworkElement)Descendants(nav).OfType<Wpf.Ui.Controls.NavigationViewContentPresenter>().Single().Content).Opacity > 0.95, "Page transition left content faded");
                Render(window, Path.Combine(screenshots, theme + "-" + page.Name + ".png"));
            }
        }
        var navigationTimer = Stopwatch.StartNew();
        for (var i = 0; i < 60; i++) { nav.Navigate(i % 2 == 0 ? typeof(RulesPage) : typeof(SettingsPage)); Pump(); }
        Expect(navigationTimer.ElapsedMilliseconds < 10000, "Repeated navigation stalled the UI");
        Results.Add(new { name = "60 page switches", elapsedMs = navigationTimer.ElapsedMilliseconds });
        nav.Navigate(typeof(RulesPage)); Pump();
        var rulesVm = (RulesViewModel)rules.DataContext; var batchTimer = Stopwatch.StartNew();
        var batch = rulesVm.AddRules(Enumerable.Range(0, 5000).Select(i => new AppRule { ProcessName = $"batch{i}.exe", DisplayName = $"批量测试 {i}", ExePath = $"C:\\Batch\\batch{i}.exe" }));
        Pump(); window.UpdateLayout();
        Expect(batch.added == 5000 && batchTimer.ElapsedMilliseconds < 5000, "Large batch rule addition stalled");
        Expect(Descendants(list).OfType<ListBoxItem>().Count() < 40, "Large rule list lost virtualization");
        Results.Add(new { name = "5000 rule batch", elapsedMs = batchTimer.ElapsedMilliseconds });
        var firstId = rulesVm.Rules[0].Id; var firstEnabled = rulesVm.Rules[0].Enabled;
        using (var lockedConfig = new FileStream(AppServices.Config.ConfigFile, FileMode.Open, FileAccess.Read, FileShare.None))
            rulesVm.Rules[0].Enabled = !firstEnabled;
        Expect(rulesVm.Rules.Single(r => r.Id == firstId).Enabled == firstEnabled && rulesVm.SummaryText.Contains("修改未保存"), "Failed save left misleading rule state");
        for (var i = 0; i < 8; i++)
        {
            var picker = new ProcessPickerWindow { ShowInTaskbar = false, Left = -9000, Top = -9000 };
            picker.Show(); Pump(); picker.Close(); Pump();
        }
        Expect(uiErrors.Count == 0, "UI exceptions during navigation/picker cancellation: " + string.Join(";", uiErrors.Select(e => e.Message)));
        window.RequestExit(); Pump();
    }
    static void Render(FrameworkElement element, string path)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }
    static int UiSession()
    {
        var app = InitializeUi();
        var window = new MainWindow { Title = "应用代理管理器 · 验收" };
        app.MainWindow = window;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        timer.Tick += (_, _) => { if (File.Exists(Path.Combine(Root, "ui-stop.flag"))) { timer.Stop(); window.RequestExit(); } };
        timer.Start(); window.Closed += (_, _) => Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background); window.Show();
        Dispatcher.Run(); AppServices.Log.Dispose(); return 0;
    }

    sealed class Samples : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Expect(request.Headers.Authorization?.ToString() == "Bearer fixture-secret", "Missing API authentication");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""
                {"downloadTotal":100,"uploadTotal":30,"connections":[{"chains":["proxy"],"metadata":{"processPath":"C:\\Programs\\sample.exe","destinationIP":"2001:db8::1","destinationPort":443,"network":"tcp"},"download":100,"upload":30}]}
                """) });
        }
    }
    static async Task<int> Fixture(string[] args)
    {
        if (args[0] == "network-probe")
        {
            try
            {
                using var handler = new SocketsHttpHandler { UseProxy = false };
                if (args.Contains("force-fake-ipv6"))
                    handler.ConnectCallback = async (context, token) =>
                    {
                        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, token);
                        var fakeV6 = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetworkV6 && a.GetAddressBytes()[0] == 0xfc)
                            ?? throw new IOException("Enhanced DNS did not return a fake IPv6 address");
                        var socket = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp);
                        try
                        {
                            await socket.ConnectAsync(new IPEndPoint(fakeV6, context.DnsEndPoint.Port), token);
                            return new NetworkStream(socket, ownsSocket: true);
                        }
                        catch { socket.Dispose(); throw; }
                    };
                using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(6) };
                var body = await client.GetStringAsync(args[1]);
                File.WriteAllText(args[2], JsonSerializer.Serialize(new { success = true, body }));
            }
            catch (Exception ex) { File.WriteAllText(args[2], JsonSerializer.Serialize(new { success = false, error = ex.GetType().Name, detail = ex.Message })); }
            return 0;
        }
        if (args[0] == "fixture-host")
        {
            using var child = EngineChildProcess.Start(FixtureExe, args[2], Path.GetDirectoryName(args[2])!, _ => { });
            File.WriteAllText(args[1], child.Process.Id.ToString());
            await Task.Delay(Timeout.Infinite); return 0;
        }
        if (args[0] == "version") { Console.WriteLine("sing-box version fixture"); return 0; }
        if (args[0] == "hang") { await Task.Delay(10000); return 0; }
        if (args[0] == "flood")
        {
            await Task.WhenAll(Task.Run(() => { for (int i = 0; i < 10000; i++) Console.WriteLine("out"); Console.WriteLine("stdout-end"); }),
                Task.Run(() => { for (int i = 0; i < 10000; i++) Console.Error.WriteLine("err"); Console.Error.WriteLine("stderr-end"); })); return 0;
        }
        var path = args[Array.IndexOf(args, "-c") + 1]; using var json = JsonDocument.Parse(File.ReadAllText(path));
        var root = json.RootElement; var host = root.GetProperty("outbounds")[0].GetProperty("server").GetString();
        if (args[0] == "check") return host == "check-fail.invalid" ? 2 : 0;
        if (host == "run-fail.invalid") return 3;
        var api = root.GetProperty("experimental").GetProperty("clash_api");
        var port = int.Parse(api.GetProperty("external_controller").GetString()!.Split(':')[^1]);
        var secret = api.GetProperty("secret").GetString();
        // 测试钩子：延迟 API 就绪，让一次保存能落进切换窗口。
        if (int.TryParse(Environment.GetEnvironmentVariable("FIXTURE_API_DELAY_MS"), out var apiDelay) && apiDelay > 0)
            await Task.Delay(apiDelay);
        var listener = new TcpListener(IPAddress.Loopback, port); listener.Start();
        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); listener.Stop(); };
        try
        {
            while (!stop.IsCancellationRequested)
            {
                using var client = await listener.AcceptTcpClientAsync(stop.Token); using var stream = client.GetStream();
                try
                {
                    using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                    requestTimeout.CancelAfter(TimeSpan.FromSeconds(3));
                    var buffer = new byte[8192]; var count = 0;
                    while (count < buffer.Length)
                    {
                        var received = await stream.ReadAsync(buffer.AsMemory(count), requestTimeout.Token);
                        if (received == 0) break;
                        count += received;
                        if (Encoding.ASCII.GetString(buffer, 0, count).Contains("\r\n\r\n", StringComparison.Ordinal)) break;
                    }
                    if (count == 0) continue;
                    var request = Encoding.ASCII.GetString(buffer, 0, count);
                    var requestPath = request.Split(' ', 3)[1];
                    var authorized = request.Contains("Authorization: Bearer " + secret, StringComparison.OrdinalIgnoreCase);
                    if (requestPath != "/version")
                    {
                        // 真实内核对未知接口返回 404；进程识别就绪门依赖这一降级路径。
                        const string missing = "{\"message\":\"not found\"}";
                        await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 404 Not Found\r\nContent-Length: {missing.Length}\r\nConnection: close\r\n\r\n{missing}"), requestTimeout.Token);
                        continue;
                    }
                    const string body = "{\"version\":\"fixture\"}";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {(authorized ? "200 OK" : "401 Unauthorized")}\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}"), requestTimeout.Token);
                }
                // Readiness clients can cancel or reset a connection at any time. A mock
                // HTTP server must keep accepting, just as the real core's server does.
                catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException) { }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException) { }
        return 0;
    }

    sealed class MockSocks : IDisposable
    {
        readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        readonly CancellationTokenSource _stop = new();
        readonly Task _accept;
        public readonly ConcurrentQueue<string> Targets = new();
        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public MockSocks() { _listener.Start(); _accept = Accept(); }
        async Task Accept()
        {
            try { while (!_stop.IsCancellationRequested) { var client = await _listener.AcceptTcpClientAsync(_stop.Token); _ = Serve(client); } }
            catch (OperationCanceledException) { }
        }
        async Task Serve(TcpClient client)
        {
            using (client)
            {
                try
                {
                    using var stream = client.GetStream(); var token = _stop.Token;
                    var hello = new byte[2]; await stream.ReadExactlyAsync(hello, token);
                    await stream.ReadExactlyAsync(new byte[hello[1]], token); await stream.WriteAsync(new byte[] { 5, 0 }, token);
                    var header = new byte[4]; await stream.ReadExactlyAsync(header, token);
                    string target;
                    if (header[3] == 3) { var count = new byte[1]; await stream.ReadExactlyAsync(count, token); var name = new byte[count[0]]; await stream.ReadExactlyAsync(name, token); target = Encoding.ASCII.GetString(name); }
                    else { var address = new byte[header[3] == 4 ? 16 : 4]; await stream.ReadExactlyAsync(address, token); target = new IPAddress(address).ToString(); }
                    var port = new byte[2]; await stream.ReadExactlyAsync(port, token); Targets.Enqueue(target + ":" + ((port[0] << 8) | port[1]));
                    await stream.WriteAsync(new byte[] { 5, 0, 0, 1, 127, 0, 0, 1, 0, 0 }, token);
                    var request = new byte[4096]; await stream.ReadAsync(request, token);
                    var body = "proxied-by-fixture";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}"), token);
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException) { }
            }
        }
        public void Dispose() { _stop.Cancel(); _listener.Stop(); _accept.GetAwaiter().GetResult(); _stop.Dispose(); }
    }
}
