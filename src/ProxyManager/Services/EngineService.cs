using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using ProxyManager.Infrastructure;
using ProxyManager.Models;

namespace ProxyManager.Services;

public class EngineException : Exception
{
    public EngineException(string message) : base(message) { }
    public EngineException(string message, Exception innerException) : base(message, innerException) { }
}

public enum EngineState { Stopped, Starting, Running, Stopping, Faulted }

/// <summary>Serialized, transactional lifecycle. Candidates are checked before stopping the live engine.</summary>
public sealed class EngineService : IDisposable
{
    readonly object _operation = new();
    readonly object _stateSync = new();
    readonly string _enginePath;
    readonly string _dataDir;
    readonly Func<AppConfig> _snapshotProvider;
    Session? _current;
    CancellationTokenSource? _restartDebounce;
    CancellationTokenSource? _pendingOperation;
    bool _pendingRestart;
    EngineState _state;
    string _lastError = "";
    long _commandVersion;
    bool _disposed;

    sealed record RuntimeConfig(string Path, int ApiPort, string ApiSecret, bool UsesFakeIp);
    sealed record Session(EngineChildProcess Child, RuntimeConfig Config);

    public EngineService() : this(AppPaths.ResolveEnginePath(), AppPaths.DataDir, () => AppServices.Config.Snapshot()) { }

    internal EngineService(string enginePath, string dataDir, Func<AppConfig> snapshotProvider)
    {
        _enginePath = Path.GetFullPath(enginePath);
        _dataDir = Path.GetFullPath(dataDir);
        _snapshotProvider = snapshotProvider;
    }

    public string EnginePath => _enginePath;
    public string ConfigPath { get { lock (_stateSync) return _current?.Config.Path ?? Path.Combine(_dataDir, "engine-config.json"); } }
    public bool EngineFileExists => File.Exists(EnginePath);
    public EngineState State { get { lock (_stateSync) return _state; } }
    public string LastError { get { lock (_stateSync) return _lastError; } }
    public bool IsRunning => State == EngineState.Running;
    public int ApiPort { get { lock (_stateSync) return _current?.Config.ApiPort ?? 0; } }
    public string ApiSecret { get { lock (_stateSync) return _current?.Config.ApiSecret ?? ""; } }

    /// <summary>All state/error changes are reported; UI subscribers must dispatch to their UI thread.</summary>
    public event Action? RunningChanged;

    public void Start(AppConfig cfg)
    {
        var snapshot = ConfigService.Clone(cfg);
        long command;
        lock (_stateSync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_state is EngineState.Starting or EngineState.Running) return;
            command = ++_commandVersion;
            CancelDebounceLocked();
        }
        lock (_operation)
        {
            using var operation = BeginOperation(command);
            if (operation == null) return;
            if (IsRunning) return;
            SetState(EngineState.Starting);
            RuntimeConfig? candidate = null;
            try
            {
                candidate = PrepareCandidate(snapshot, operation.Token);
                StartSession(candidate, operation.Token);
                candidate = null;
                SetState(EngineState.Running);
                LogInfo("分流引擎已启动");
            }
            catch (OperationCanceledException) when (operation.IsCancellationRequested)
            {
                CleanupCurrent();
                SetState(EngineState.Stopped);
            }
            catch (Exception ex)
            {
                CleanupCurrent();
                SetState(EngineState.Faulted, ex.Message);
                throw AsEngineException(ex);
            }
            finally
            {
                DeleteConfig(candidate);
                EndOperation(operation);
            }
        }
    }

    public void Stop()
    {
        lock (_stateSync)
        {
            ++_commandVersion;
            CancelDebounceLocked();
            _pendingOperation?.Cancel();
        }
        lock (_operation)
        {
            if (State == EngineState.Stopped && _current == null) return;
            SetState(EngineState.Stopping);
            try
            {
                StopCurrent(deleteConfig: true);
                SetState(EngineState.Stopped);
                LogInfo("分流引擎已停止");
            }
            catch (Exception ex)
            {
                SetState(EngineState.Faulted, ex.Message);
                throw AsEngineException(ex);
            }
        }
    }

    /// <summary>Snapshot the UI-owned configuration before starting background debounce work.</summary>
    public void RestartIfRunning(int delayMs = 1200)
    {
        lock (_stateSync)
        {
            // 已停止时无需重启（下次 Start 自会读取新配置）；切换进行中的保存标记补跑。
            if (_disposed || _state == EngineState.Stopped || _state == EngineState.Faulted) return;
            if (_state is EngineState.Starting or EngineState.Stopping) { _pendingRestart = true; return; }
        }
        AppConfig snapshot;
        try { snapshot = _snapshotProvider(); }
        catch (Exception ex) { SetState(State, "读取候选配置失败：" + ex.Message); return; }
        CancellationTokenSource debounce;
        long command;
        lock (_stateSync)
        {
            if (_disposed) return;
            if (_state != EngineState.Running)
            {
                if (_state is EngineState.Starting or EngineState.Stopping) _pendingRestart = true;
                return;
            }
            CancelDebounceLocked();
            _pendingRestart = false;
            debounce = _restartDebounce = new CancellationTokenSource();
            command = ++_commandVersion;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    await Task.Delay(Math.Clamp(delayMs, 0, 60_000), debounce.Token).ConfigureAwait(false);
                    lock (_operation)
                    {
                        if (debounce.IsCancellationRequested || !IsRunning) return;
                        using var operation = BeginOperation(command);
                        if (operation == null) return;
                        try { ReplaceRunning(snapshot, operation.Token); }
                        finally { EndOperation(operation); }
                    }
                    lock (_stateSync)
                    {
                        // 只在干净完成后补跑；失败路径已给出错误状态，不再自动重试失败配置。
                        if (_disposed || !_pendingRestart || _state != EngineState.Running) return;
                        _pendingRestart = false;
                        snapshot = _snapshotProvider();
                        command = ++_commandVersion;
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { LogError("应用引擎配置失败：" + ex.Message); }
            finally
            {
                lock (_stateSync) if (ReferenceEquals(_restartDebounce, debounce)) _restartDebounce = null;
                debounce.Dispose();
            }
        });
    }

    void ReplaceRunning(AppConfig snapshot, CancellationToken token)
    {
        RuntimeConfig? candidate = null;
        RuntimeConfig? previous = null;
        try
        {
            candidate = PrepareCandidate(snapshot, token);
            token.ThrowIfCancellationRequested();
            lock (_stateSync) previous = _current?.Config;
            if (previous == null) return;
            SetState(EngineState.Stopping);
            StopCurrent(deleteConfig: false);
            if (previous.UsesFakeIp && !candidate.UsesFakeIp) FlushDnsCache();
            token.ThrowIfCancellationRequested();
            SetState(EngineState.Starting);
            StartSession(candidate, token);
            candidate = null;
            DeleteConfig(previous);
            previous = null;
            SetState(EngineState.Running);
            LogInfo("引擎配置已应用");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            CleanupCurrent();
            SetState(EngineState.Stopped);
        }
        catch (Exception ex)
        {
            if (previous == null)
            {
                SetState(State, "新配置未生效，当前分流保持运行：" + ex.Message);
                LogError(LastError);
                return;
            }
            CleanupCurrent();
            try
            {
                token.ThrowIfCancellationRequested();
                SetState(EngineState.Starting);
                StartSession(previous, token);
                previous = null;
                SetState(EngineState.Running, "新配置启动失败，已恢复上次运行配置：" + ex.Message);
                LogError(LastError);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                CleanupCurrent();
                SetState(EngineState.Stopped);
            }
            catch (Exception rollbackError)
            {
                CleanupCurrent();
                SetState(EngineState.Faulted, $"新配置启动失败：{ex.Message}\n恢复上次配置也失败：{rollbackError.Message}");
                LogError(LastError);
            }
        }
        finally { DeleteConfig(candidate); DeleteConfig(previous); }
    }

    RuntimeConfig PrepareCandidate(AppConfig snapshot, CancellationToken token)
    {
        if (!EngineFileExists) throw new EngineException($"找不到内核文件 sing-box.exe\n路径：{EnginePath}");
        token.ThrowIfCancellationRequested();
        var dir = Path.Combine(_dataDir, "runtime");
        CreatePrivateDirectory(dir);
        if (_current == null)
        {
            // A previous host crash can leave private temporary configuration files.
            foreach (var stale in Directory.EnumerateFiles(dir, "engine-*.json"))
                try { File.Delete(stale); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        var candidate = new RuntimeConfig(Path.Combine(dir, $"engine-{Guid.NewGuid():N}.json"),
            GetFreeLoopbackPort(), Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), snapshot.UseFakeIpDns);
        try
        {
            using (var stream = new FileStream(candidate.Path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                writer.Write(EngineConfigBuilder.Build(snapshot, candidate.ApiPort, candidate.ApiSecret));
            var check = RunToCompletion(EnginePath, $"check -c \"{candidate.Path}\" --disable-color", 15000, token);
            token.ThrowIfCancellationRequested();
            if (check.ExitCode != 0) throw new EngineException("配置校验失败：\n" + Clean(check.Output));
            return candidate;
        }
        catch { DeleteConfig(candidate); throw; }
    }

    void StartSession(RuntimeConfig config, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var child = EngineChildProcess.Start(EnginePath, config.Path, _dataDir, LogEngine);
        var session = new Session(child, config);
        lock (_stateSync) _current = session;
        child.Process.Exited += (_, _) => _ = Task.Run(() => OnProcessExited(session));
        child.Process.EnableRaisingEvents = true;
        WaitForApiAsync(session, token).GetAwaiter().GetResult();
        WaitForProcessSearchAsync(session, token).GetAwaiter().GetResult();
        token.ThrowIfCancellationRequested();
        if (child.Process.HasExited) throw new EngineException($"引擎启动后退出（退出码 {child.Process.ExitCode}），详见日志。");
    }

    /// <summary>
    /// sing-box resolves process identities lazily: for roughly the first ten
    /// seconds after start, connections carry no process path and the selected
    /// application rules cannot match, silently falling through to direct.
    /// Hold the start until one lookup has resolved (or degrade with a warning).
    /// </summary>
    async Task WaitForProcessSearchAsync(Session session, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false, ConnectTimeout = TimeSpan.FromMilliseconds(500) })
        { Timeout = TimeSpan.FromSeconds(1) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.Config.ApiSecret);
        var url = $"http://127.0.0.1:{session.Config.ApiPort}/connections";
        var failures = 0;
        while (true)
        {
            TriggerProcessLookup();
            try
            {
                timeout.Token.ThrowIfCancellationRequested();
                if (session.Child.Process.HasExited)
                    throw new EngineException($"引擎启动失败（退出码 {session.Child.Process.ExitCode}），请检查管理员权限和日志。");
                using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false),
                    cancellationToken: timeout.Token).ConfigureAwait(false);
                if (document.RootElement.TryGetProperty("connections", out var connections) && connections.ValueKind == JsonValueKind.Array)
                {
                    foreach (var connection in connections.EnumerateArray())
                    {
                        if (!connection.TryGetProperty("metadata", out var metadata)) continue;
                        if (HasText(metadata, "processPath") || HasText(metadata, "process")) return;
                    }
                }
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested && !token.IsCancellationRequested)
            {
                throw new EngineException("进程识别在 20 秒内未就绪；已停止该次启动，请查看日志重试。");
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException)
            {
                // 404 等错误说明当前内核不提供连接查询（如测试内核）；短暂重试后按已就绪放行。
                if (++failures >= 6 && ex is HttpRequestException { StatusCode: not null }) return;
            }
            try { await Task.Delay(300, timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                throw new EngineException("进程识别在 20 秒内未就绪；已停止该次启动，请查看日志重试。");
            }
        }
    }

    static bool HasText(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(value.GetString());

    static void TriggerProcessLookup()
    {
        // 自身到公网 443 端口的一条短连接：进入 TUN 后会触发 sing-box 的进程查找，
        // 连接随后出现在 /connections 里，用于确认搜索器已热。
        try
        {
            using var socket = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork,
                System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
            var pending = socket.ConnectAsync(IPAddress.Parse("223.5.5.5"), 443);
            pending.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            Thread.Sleep(250);
        }
        catch { /* 触发连接失败不影响判定 */ }
    }

    static async Task WaitForApiAsync(Session session, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        // Native adapter and DNS initialization can exceed 10s with several VPN adapters.
        // This remains cancellable and runs outside the UI thread.
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false, ConnectTimeout = TimeSpan.FromMilliseconds(500) })
        { Timeout = TimeSpan.FromSeconds(1) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.Config.ApiSecret);
        var url = $"http://127.0.0.1:{session.Config.ApiPort}/version";
        try
        {
            while (true)
            {
                timeout.Token.ThrowIfCancellationRequested();
                if (session.Child.Process.HasExited)
                    throw new EngineException($"引擎启动失败（退出码 {session.Child.Process.ExitCode}），请检查管理员权限和日志。");
                try
                {
                    using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                    if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                        throw new EngineException("引擎状态接口认证失败，无法确认当前运行实例。");
                    response.EnsureSuccessStatusCode();
                    using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false),
                        cancellationToken: timeout.Token).ConfigureAwait(false);
                    if (document.RootElement.TryGetProperty("version", out var version) && version.ValueKind == JsonValueKind.String) return;
                }
                catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException) { }
                // A connection cancellation can race HttpClient's endpoint inspection
                // in .NET 8. Treat that socket race like any other failed readiness
                // attempt; our client itself stays alive until this method returns.
                catch (ObjectDisposedException ex) when (ex.ObjectName == "System.Net.Sockets.Socket") { }
                catch (OperationCanceledException) when (!timeout.IsCancellationRequested) { }
                await Task.Delay(100, timeout.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new EngineException("引擎状态接口在 20 秒内未就绪；已停止该次启动，详见日志。");
        }
    }

    void StopCurrent(bool deleteConfig)
    {
        Session? session;
        lock (_stateSync) { session = _current; _current = null; }
        if (session == null) return;
        try { session.Child.Stop(); }
        finally
        {
            session.Child.Dispose();
            if (deleteConfig && session.Config.UsesFakeIp) FlushDnsCache();
            if (deleteConfig) DeleteConfig(session.Config);
        }
    }

    void CleanupCurrent()
    {
        try { StopCurrent(deleteConfig: true); }
        catch (Exception ex) { LogError("清理引擎失败：" + ex.Message); }
    }

    void OnProcessExited(Session exited)
    {
        lock (_operation)
        {
            lock (_stateSync)
            {
                if (!ReferenceEquals(_current, exited)) return;
                _current = null;
            }
            var code = -1;
            try { code = exited.Child.Process.ExitCode; } catch (InvalidOperationException) { }
            exited.Child.Dispose();
            DeleteConfig(exited.Config);
            if (exited.Config.UsesFakeIp) FlushDnsCache();
            SetState(EngineState.Faulted, $"引擎意外退出（退出码 {code}），请查看日志后重试。");
            LogError(LastError);
        }
    }

    CancellationTokenSource? BeginOperation(long command)
    {
        lock (_stateSync)
        {
            if (_disposed || command != _commandVersion) return null;
            return _pendingOperation = new CancellationTokenSource();
        }
    }

    void EndOperation(CancellationTokenSource operation)
    {
        lock (_stateSync) if (ReferenceEquals(_pendingOperation, operation)) _pendingOperation = null;
    }

    void CancelDebounceLocked()
    {
        _restartDebounce?.Cancel();
        _restartDebounce = null;
    }

    void SetState(EngineState state, string error = "")
    {
        lock (_stateSync) { _state = state; _lastError = error; }
        foreach (var subscriber in RunningChanged?.GetInvocationList() ?? Array.Empty<Delegate>())
        {
            try { ((Action)subscriber)(); }
            catch (Exception ex) { LogError("状态通知失败：" + ex.Message); }
        }
    }

    internal static (int ExitCode, string Output) RunToCompletion(string fileName, string arguments, int timeoutMs,
        CancellationToken cancellationToken = default) => RunToCompletionAsync(fileName, arguments, timeoutMs, cancellationToken).GetAwaiter().GetResult();

    static async Task<(int ExitCode, string Output)> RunToCompletionAsync(string fileName, string arguments, int timeoutMs, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Math.Max(1, timeoutMs));
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(fileName, arguments)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            },
        };
        try
        {
            if (!process.Start()) return (-1, "无法启动内核命令");
            var stdout = ReadBoundedAsync(process.StandardOutput, timeout.Token);
            var stderr = ReadBoundedAsync(process.StandardError, timeout.Token);
            await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(timeout.Token)).WaitAsync(timeout.Token).ConfigureAwait(false);
            return (process.ExitCode, stdout.Result + stderr.Result);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            if (cancellationToken.IsCancellationRequested) throw;
            return (-1, "执行超时");
        }
        catch (Exception ex) { TryKill(process); return (-1, ex.Message); }
    }

    static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken token)
    {
        var text = new StringBuilder();
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) != 0)
            if (text.Length < 65536) text.Append(buffer, 0, Math.Min(count, 65536 - text.Length));
        return text.ToString();
    }

    static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    public string GetEngineVersion()
    {
        if (!EngineFileExists) return "未找到";
        var result = RunToCompletion(EnginePath, "version", 5000);
        var line = result.Output.Split('\n').FirstOrDefault(x => x.Contains("version", StringComparison.OrdinalIgnoreCase));
        return line?.Trim() ?? (result.ExitCode == 0 ? "未知" : "读取失败");
    }

    static void CreatePrivateDirectory(string path)
    {
        var sid = WindowsIdentity.GetCurrent().User ?? throw new EngineException("无法读取当前用户身份，不能安全创建内核配置。");
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(sid);
        security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        Directory.CreateDirectory(path).SetAccessControl(security);
    }

    static void DeleteConfig(RuntimeConfig? config)
    {
        if (config == null) return;
        try { File.Delete(config.Path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { LogError("删除临时内核配置失败：" + ex.Message); }
    }

    [System.Runtime.InteropServices.DllImport("dnsapi.dll", SetLastError = true)]
    static extern bool DnsFlushResolverCache();
    static void FlushDnsCache()
    {
        if (!DnsFlushResolverCache()) LogError("已停止增强解析，但系统 DNS 缓存刷新失败；如应用仍无法访问，请重新打开该应用。");
    }

    static EngineException AsEngineException(Exception ex) => ex as EngineException ?? new EngineException(ex.Message, ex);
    static string Clean(string value) => value.Replace("\u001b[31m", "").Replace("\u001b[0m", "").Trim();
    static int GetFreeLoopbackPort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        try { listener.Start(); return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }
    static void LogInfo(string message) => AppServices.Log?.Info(message);
    static void LogError(string message) => AppServices.Log?.Error(message);
    static void LogEngine(string message) => AppServices.Log?.Engine(message);

    public void Dispose()
    {
        lock (_stateSync) { if (_disposed) return; _disposed = true; }
        Stop();
    }
}
