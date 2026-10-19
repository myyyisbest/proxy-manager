using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ProxyManager.Models;

namespace ProxyManager.Services;

/// <summary>进程信息。Icon 可后台补充（带变更通知，列表增量刷新）。</summary>
public class ProcInfo : INotifyPropertyChanged
{
    public string ProcessName { get; init; } = "";
    public string ExePath { get; init; } = "";
    public string DisplayName { get; init; } = "";

    ImageSource? _icon;
    public ImageSource? Icon
    {
        get => _icon;
        set { _icon = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon))); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }
}

/// <summary>枚举运行中的进程并提取程序图标。</summary>
public static class ProcessService
{
    /// <summary>快速枚举：只取名称/路径/描述，不含图标（图标提取较慢，由调用方后台分批补）。</summary>
    public static List<ProcInfo> GetRunningProcesses(CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, ProcInfo>(StringComparer.OrdinalIgnoreCase);

        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                // 即使取消也继续释放 Process.GetProcesses 返回的所有句柄。
                if (cancellationToken.IsCancellationRequested) continue;
                try
                {
                    var processName = p.ProcessName;
                    string? path = null;
                    try { path = p.MainModule?.FileName; } catch { /* 系统/UWP 进程无法访问 */ }
                    var name = processName + ".exe";
                    if (string.IsNullOrEmpty(path))
                    {
                        if (!result.ContainsKey(name))
                            result[name] = new ProcInfo { ProcessName = name, DisplayName = processName };
                    }
                    else if (!result.ContainsKey(path))
                    {
                        var display = TryGetDescription(path) ?? processName;
                        result[path] = new ProcInfo { ProcessName = Path.GetFileName(path), ExePath = path, DisplayName = display };
                    }
                }
                catch (InvalidOperationException) { /* 枚举期间进程已退出 */ }
                catch (System.ComponentModel.Win32Exception) { /* 受保护进程 */ }
            }
        }

        cancellationToken.ThrowIfCancellationRequested();

        return result.Values
            .OrderBy(x => x.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>为单个进程提取图标（已提取过则跳过）。</summary>
    public static void LoadIcon(ProcInfo info)
    {
        if (info.Icon != null || string.IsNullOrEmpty(info.ExePath)) return;
        info.Icon = IconLoader.GetIcon(info.ExePath);
    }

    public static string? TryGetDescription(string exePath)
    {
        try
        {
            var desc = FileVersionInfo.GetVersionInfo(exePath).FileDescription;
            return string.IsNullOrWhiteSpace(desc) ? null : desc.Trim();
        }
        catch { return null; }
    }

    public static AppRule? RuleFromExePath(string exePath)
    {
        if (!File.Exists(exePath)) return null;
        var name = Path.GetFileName(exePath);
        return new AppRule
        {
            ProcessName = name,
            DisplayName = TryGetDescription(exePath) ?? Path.GetFileNameWithoutExtension(exePath),
            ExePath = exePath
        };
    }
}

/// <summary>通过 SHGetFileInfo 提取 exe 图标并转换为 WPF ImageSource。</summary>
public static class IconLoader
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    const uint SHGFI_ICON = 0x100;
    const uint SHGFI_LARGEICON = 0x0;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr hIcon);

    public static ImageSource? GetIcon(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var shfi = new SHFILEINFO();
        try
        {
            SHGetFileInfo(path, 0, ref shfi, (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_ICON | SHGFI_LARGEICON);
            if (shfi.hIcon == IntPtr.Zero) return null;

            var source = Imaging.CreateBitmapSourceFromHIcon(shfi.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch
        {
            return null;
        }
        finally
        {
            // 转换/Freeze 失败时仍必须释放 shell 返回的 native 图标。
            if (shfi.hIcon != IntPtr.Zero) DestroyIcon(shfi.hIcon);
        }
    }
}
