using System.IO;

namespace ProxyManager.Infrastructure;

public static class AppPaths
{
    public static bool IsPortable { get; } = File.Exists(Path.Combine(AppContext.BaseDirectory, "portable.marker"));
    static string? _dataDirectoryOverride;
    public static bool IsIsolated => _dataDirectoryOverride != null;
    public static string DataDir => _dataDirectoryOverride ?? (IsPortable
        ? Path.Combine(AppContext.BaseDirectory, "Data")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProxyManager"));
    public static string LegacyDataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ProxyManager");
    public static string ConfigFile => Path.Combine(DataDir, "config.json");
    public static string EngineConfigFile => Path.Combine(DataDir, "engine-config.json");

    /// <summary>仅启动阶段显式隔离运行时调用，不从环境变量接受目录。</summary>
    public static void ConfigureDataDirectory(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("隔离数据目录必须为绝对路径。");
        _dataDirectoryOverride = Path.GetFullPath(path);
    }

    /// <summary>不从可写配置目录加载可执行文件。</summary>
    public static string ResolveEnginePath()
    {
        var besideApp = Path.Combine(AppContext.BaseDirectory, "sing-box.exe");
        if (File.Exists(besideApp)) return besideApp;

        var fallback = Path.Combine(AppContext.BaseDirectory, "tools", "sing-box.exe");
        return File.Exists(fallback) ? fallback : besideApp;
    }

    public static void EnsureWritableDataDirectory()
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            var probe = Path.Combine(DataDir, $".write-test-{Guid.NewGuid():N}");
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException(IsPortable
                ? "便携版 Data 目录不可写，请将整个程序文件夹解压到可写位置后运行。"
                : "应用数据目录不可写，请检查当前用户的目录权限。", ex);
        }
    }

    public static void MigrateLegacyConfig()
    {
        if (IsPortable || IsIsolated || File.Exists(ConfigFile) || !File.Exists(Path.Combine(LegacyDataDir, "config.json"))) return;
        File.Copy(Path.Combine(LegacyDataDir, "config.json"), ConfigFile, overwrite: false);
    }
}
