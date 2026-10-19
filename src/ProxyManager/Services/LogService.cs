using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using ProxyManager.Infrastructure;

namespace ProxyManager.Services;

public record LogEntry(DateTime Time, string Level, string Message);

/// <summary>有限内存队列、后台落盘、大小/天数轮转；输出前移除常见凭据格式。</summary>
public sealed class LogService : IDisposable
{
    const int MaxEntries = 2000;
    const long MaxFileBytes = 4 * 1024 * 1024;
    const int MaxFiles = 7;
    readonly ConcurrentQueue<LogEntry> _entries = new();
    readonly Channel<LogEntry> _pending = Channel.CreateBounded<LogEntry>(new BoundedChannelOptions(1024)
        { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = false });
    readonly Task _writer;
    string? _logFile;
    DateOnly _logDay;
    long _fileBytes;
    int _disposed;
    public event Action<LogEntry>? EntryAdded;
    public LogService() => _writer = Task.Run(WriteLoopAsync);
    public void Info(string message) => Add("INFO", message);
    public void Warn(string message) => Add("警告", message);
    public void Error(string message) => Add("错误", message);
    public void Engine(string message) => Add("引擎", message);

    void Add(string level, string message)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        var raw = message ?? "";
        if (raw.Length > 8192) raw = raw[..8192] + "…";
        string sanitized;
        try { sanitized = Redact(raw); }
        catch (RegexMatchTimeoutException) { sanitized = "日志内容无法安全脱敏，已隐藏。"; }
        if (sanitized.Length > 8192) sanitized = sanitized[..8192] + "…";
        var entry = new LogEntry(DateTime.Now, level, sanitized);
        _entries.Enqueue(entry);
        while (_entries.Count > MaxEntries && _entries.TryDequeue(out _)) { }
        if (EntryAdded != null)
            foreach (Action<LogEntry> subscriber in EntryAdded.GetInvocationList()) { try { subscriber(entry); } catch { } }
        _pending.Writer.TryWrite(entry);
    }

    async Task WriteLoopAsync()
    {
        while (await _pending.Reader.WaitToReadAsync().ConfigureAwait(false))
        {
            var batch = new StringBuilder();
            while (_pending.Reader.TryRead(out var entry))
            {
                batch.Append(entry.Time.ToString("yyyy-MM-dd HH:mm:ss")).Append(" [").Append(entry.Level).Append("] ")
                    .Append(entry.Message.Replace("\r", "")).Append('\n');
                if (batch.Length >= 32768) break;
            }
            if (batch.Length == 0) continue;
            try
            {
                var dir = Path.Combine(AppPaths.DataDir, "logs");
                Directory.CreateDirectory(dir);
                var bytes = Encoding.UTF8.GetByteCount(batch.ToString());
                var today = DateOnly.FromDateTime(DateTime.Now);
                if (_logFile == null || _logDay != today || _fileBytes + bytes > MaxFileBytes)
                {
                    _logFile = Path.Combine(dir, $"proxy-manager-{DateTime.Now:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}.log");
                    _logDay = today; _fileBytes = 0;
                    foreach (var old in new DirectoryInfo(dir).EnumerateFiles("proxy-manager-*.log")
                        .OrderByDescending(f => f.LastWriteTimeUtc).Skip(MaxFiles - 1))
                        try { old.Delete(); } catch { }
                }
                await File.AppendAllTextAsync(_logFile, batch.ToString(), new UTF8Encoding(false)).ConfigureAwait(false);
                _fileBytes += bytes;
            }
            catch { /* 日志失败不改变网络或 UI。 */ }
        }
    }

    public static string Redact(string value)
    {
        value = Regex.Replace(value, "(?i)(\"(?:password|passwd|secret|username|protectedcredentials)\"\\s*:\\s*)\"(?:\\\\.|[^\"\\\\])*\"", "$1\"[redacted]\"", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
        value = Regex.Replace(value, "(?i)((?:proxy-)?authorization\\s*[:=]\\s*)[^\\r\\n]+", "$1[redacted]", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
        value = Regex.Replace(value, "(?i)((?:password|passwd|secret|token)\\s*[:=]\\s*)[^\\s,;\\r\\n]+", "$1[redacted]", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
        return Regex.Replace(value, "(?i)([a-z][a-z0-9+.-]*://)[^\\s/@]+:[^\\s/@]+@", "$1[redacted]@", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
    }

    public IReadOnlyList<LogEntry> Snapshot() => _entries.ToArray();
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _pending.Writer.TryComplete();
        try { _writer.Wait(TimeSpan.FromSeconds(2)); } catch { }
    }
}

