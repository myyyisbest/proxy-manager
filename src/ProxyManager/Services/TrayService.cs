using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace ProxyManager.Services;

/// <summary>使用 Windows 自带托盘实现，并尽力把图标设为常显。</summary>
public sealed class TrayService : IDisposable
{
    static readonly Dictionary<Guid, Environment.SpecialFolder> KnownFolders = new()
    {
        [new Guid("6D809377-6AF0-444B-8957-A3773F02200E")] = Environment.SpecialFolder.ProgramFiles,
        [new Guid("7C5A40EF-A0FB-4BFC-874A-C0F2E0B9FA8E")] = Environment.SpecialFolder.ProgramFilesX86,
        [new Guid("F1B32785-6FBA-4FCF-9D55-7B8E7F157091")] = Environment.SpecialFolder.LocalApplicationData,
        [new Guid("B4BFCC3A-DB2C-424C-B029-7FE99A87C641")] = Environment.SpecialFolder.Desktop,
    };

    readonly Forms.NotifyIcon _icon;
    readonly Forms.ToolStripMenuItem _toggle;
    readonly System.Drawing.Icon _image;
    readonly Dispatcher _dispatcher;
    bool _hintShown;
    bool _disposed;

    public bool IsAvailable => !_disposed && _icon.Visible;

    public TrayService(Action showWindow, Action toggleEngine, Action exit)
    {
        _dispatcher = Application.Current.Dispatcher;
        var resource = Application.GetResourceStream(new Uri("pack://application:,,,/ProxyManager;component/Assets/app.ico"))
            ?? throw new InvalidOperationException("找不到托盘图标资源");
        using (resource.Stream)
        using (var source = new System.Drawing.Icon(resource.Stream))
            _image = (System.Drawing.Icon)source.Clone();

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("显示主界面", null, (_, _) => Invoke(showWindow));
        _toggle = new Forms.ToolStripMenuItem("启用分流");
        _toggle.Click += (_, _) => Invoke(toggleEngine);
        menu.Items.Add(_toggle);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => Invoke(exit));
        _icon = new Forms.NotifyIcon { Text = "应用代理管理器", Icon = _image, ContextMenuStrip = menu };
        _icon.DoubleClick += (_, _) => Invoke(showWindow);
        try { _icon.Visible = true; }
        catch
        {
            _icon.Dispose();
            menu.Dispose();
            _image.Dispose();
            throw;
        }
        _ = Task.Run(PromoteAsync);
    }

    /// <summary>
    /// Win11 默认把新图标收进托盘折叠区。explorer 会按自己的哈希为图标创建
    /// NotifyIconSettings 条目，在其上写 IsPromoted=1 即常显；条目由 explorer
    /// 异步创建，只能等它出现后修改，不能自己建键。
    /// </summary>
    async Task PromoteAsync()
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                using var root = Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings", writable: true);
                if (root != null)
                {
                    foreach (var name in root.GetSubKeyNames())
                    {
                        using var entry = root.OpenSubKey(name, writable: true);
                        if (entry == null || !MatchesCurrentExe(entry.GetValue("ExecutablePath") as string)) continue;
                        if (entry.GetValue("IsPromoted") is not int promoted || promoted != 1)
                            entry.SetValue("IsPromoted", 1, RegistryValueKind.DWord);
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                AppServices.Log?.Warn("托盘图标常显设置失败：" + ex.Message);
                return;
            }
            await Task.Delay(1500).ConfigureAwait(false);
        }
    }

    static bool MatchesCurrentExe(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored)) return false;
        var candidate = stored.Trim();
        if (candidate.StartsWith('{'))
        {
            // explorer 有时以 KNOWNFOLDERID 相对路径存储，如 {6D809377-…}\ProxyManager\ProxyManager.exe
            var separator = candidate.IndexOf('\\');
            if (separator <= 0 || !Guid.TryParse(candidate[..separator], out var folder)) return false;
            if (!KnownFolders.TryGetValue(folder, out var special)) return false;
            var expanded = Environment.GetFolderPath(special);
            if (string.IsNullOrEmpty(expanded)) return false;
            candidate = Path.Combine(expanded, candidate[(separator + 1)..]);
        }
        try { candidate = Path.GetFullPath(candidate); } catch { return false; }
        return string.Equals(candidate, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase);
    }

    void Invoke(Action action)
    {
        if (_disposed || _dispatcher.HasShutdownStarted) return;
        _dispatcher.BeginInvoke(action);
    }

    public void Update(bool running, bool busy)
    {
        if (_disposed) return;
        _toggle.Text = busy ? "正在切换分流…" : running ? "停止分流" : "启用分流";
        _toggle.Enabled = !busy;
        _icon.Text = running ? "应用代理管理器 · 分流运行中" : "应用代理管理器 · 已停止";
    }

    /// <summary>首次隐藏到托盘时提示退出入口，避免用户以为程序已被关闭。</summary>
    public void NotifyHiddenToTray()
    {
        if (_hintShown || _disposed) return;
        _hintShown = true;
        _icon.ShowBalloonTip(4000, "应用代理管理器", "已最小化到托盘，分流仍在运行；从托盘菜单“退出”才会真正退出。", Forms.ToolTipIcon.Info);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _icon.Visible = false;
        var menu = _icon.ContextMenuStrip;
        _icon.Dispose();
        menu?.Dispose();
        _image.Dispose();
    }
}
