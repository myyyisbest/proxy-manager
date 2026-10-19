using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace ProxyManager.Services;

public record ProxiedConnection(string Process, string Host, string Network, long UpBytes, long DownBytes);

/// <summary>带认证的本地 API。速度为 TUN 总流量（包含直连），列表只包含 proxy 出口。</summary>
public sealed class StatusService : IDisposable
{
    readonly HttpClient _http;
    readonly object _stateGate = new();
    int _polling;
    int _generation;
    long _lastDown, _lastUp, _lastTimestamp;
    string _endpoint = "";
    public double DownSpeed { get; private set; }
    public double UpSpeed { get; private set; }
    public double TotalDownSpeed => DownSpeed;
    public double TotalUpSpeed => UpSpeed;
    public bool ApiAlive { get; private set; }
    public List<ProxiedConnection> Proxied { get; private set; } = new();

    public StatusService(HttpClient? httpClient = null) => _http = httpClient ?? new HttpClient(new SocketsHttpHandler
    { UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(2) }) { Timeout = TimeSpan.FromSeconds(3) };

    public async Task PollAsync(int apiPort, string apiSecret = "", CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _polling, 1, 0) != 0) return;
        int generation;
        lock (_stateGate)
        {
            var endpoint = apiPort + ":" + apiSecret;
            if (_endpoint != endpoint) { ResetLocked(); _endpoint = endpoint; }
            generation = _generation;
        }
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{apiPort}/connections");
            if (!string.IsNullOrEmpty(apiSecret)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiSecret);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > 8 * 1024 * 1024) throw new HttpRequestException("状态响应过大");
            using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, new JsonDocumentOptions { MaxDepth = 32 }, timeout.Token).ConfigureAwait(false);
            var root = doc.RootElement;
            long down = GetNumber(root, "downloadTotal"), up = GetNumber(root, "uploadTotal");
            var proxied = new List<ProxiedConnection>();
            if (root.TryGetProperty("connections", out var conns) && conns.ValueKind == JsonValueKind.Array)
            {
                foreach (var c in conns.EnumerateArray().Take(10000))
                {
                    if (!c.TryGetProperty("chains", out var chains) || chains.ValueKind != JsonValueKind.Array
                        || !chains.EnumerateArray().Any(link => link.ValueKind == JsonValueKind.String && link.GetString() == "proxy")) continue;
                    var m = c.TryGetProperty("metadata", out var meta) ? meta : default;
                    var proc = GetString(m, "process");
                    if (string.IsNullOrEmpty(proc))
                    {
                        var path = GetString(m, "processPath").Replace('\\', '/');
                        proc = path[(path.LastIndexOf('/') + 1)..];
                    }
                    var host = GetString(m, "host");
                    if (string.IsNullOrEmpty(host)) host = GetString(m, "destinationIP");
                    if (IPAddress.TryParse(host, out var address) && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
                        host = "[" + host.Trim('[', ']') + "]";
                    var port = GetString(m, "destinationPort");
                    if (port.Length > 0) host += ":" + port;
                    proxied.Add(new(proc, host, GetString(m, "network"), GetNumber(c, "upload"), GetNumber(c, "download")));
                }
            }
            proxied = proxied.OrderByDescending(c => (double)c.UpBytes + c.DownBytes).Take(500).ToList();
            lock (_stateGate)
            {
                if (generation != _generation) return;
                var now = Stopwatch.GetTimestamp();
                if (_lastTimestamp != 0 && down >= _lastDown && up >= _lastUp)
                {
                    var seconds = Math.Max(0.05, Stopwatch.GetElapsedTime(_lastTimestamp, now).TotalSeconds);
                    DownSpeed = (down - _lastDown) / seconds;
                    UpSpeed = (up - _lastUp) / seconds;
                }
                else DownSpeed = UpSpeed = 0;
                _lastDown = down; _lastUp = up; _lastTimestamp = now;
                Proxied = proxied; ApiAlive = true;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or IOException or InvalidOperationException)
        {
            lock (_stateGate) { if (generation == _generation) ResetLocked(); }
        }
        finally { Volatile.Write(ref _polling, 0); }
    }

    public void Reset() { lock (_stateGate) ResetLocked(); }
    void ResetLocked()
    {
        _generation++;
        _lastTimestamp = _lastDown = _lastUp = 0;
        DownSpeed = UpSpeed = 0; Proxied = new(); ApiAlive = false;
    }

    static string GetString(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return "";
        return v.ValueKind switch { JsonValueKind.String => v.GetString() ?? "", JsonValueKind.Number => v.GetRawText(), _ => "" };
    }
    static long GetNumber(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n)) return Math.Max(0, n);
        return v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out n) ? Math.Max(0, n) : 0;
    }
    public void Dispose() { Reset(); _http.Dispose(); }
}

