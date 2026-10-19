using ProxyManager.Services;
using ProxyManager.ViewModels;

namespace ProxyManager;

/// <summary>简单的全局服务定位器（无 DI 框架，够用且直观）。</summary>
public static class AppServices
{
    public static LogService Log { get; private set; } = null!;
    public static ConfigService Config { get; private set; } = null!;
    public static EngineService Engine { get; private set; } = null!;
    public static StatusService Status { get; private set; } = null!;
    public static PageService PageService { get; private set; } = null!;

    static bool _initialized;

    public static void Init()
    {
        if (_initialized) return;
        _initialized = true;

        Log = new LogService();
        Config = new ConfigService();
        Engine = new EngineService();
        Status = new StatusService();
        PageService = new PageService();

        Config.Load();

        // 只有影响分流配置的修改（代理地址、应用规则）才让引擎自动重启
        Config.EngineRestartNeeded += () => Engine.RestartIfRunning();

        Log.Info("应用代理管理器启动");
    }
}
