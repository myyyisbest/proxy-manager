namespace ProxyManager.Infrastructure;

/// <summary>字节/速度人性化格式化。</summary>
public static class Format
{
    public static string Bytes(double value)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        var i = 0;
        while (value >= 1024 && i < units.Length - 1) { value /= 1024; i++; }
        return $"{value:0.#} {units[i]}";
    }

    public static string Speed(double bytesPerSecond) => Bytes(bytesPerSecond) + "/s";
}
