using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using Microsoft.Win32;
using ProxyManager.Infrastructure;

namespace ProxyManager.Services;

/// <summary>当前用户登录时以最高可用权限运行，兼容 requireAdministrator 清单。</summary>
public static class AutoStartService
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public static string TaskName => "ProxyManager-" + WindowsIdentity.GetCurrent().User!.Value;
    static string ExePath => Path.GetFullPath(Environment.ProcessPath ?? throw new InvalidOperationException("无法确定程序路径"));

    public static bool IsEnabled()
    {
        if (AppPaths.IsIsolated) return false;
        object? service = null, folder = null, task = null, definition = null, actions = null, action = null, principal = null;
        try
        {
            service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!);
            ((dynamic)service!).Connect();
            folder = ((dynamic)service).GetFolder(@"\");
            task = ((dynamic)folder).GetTask(TaskName);
            if (!(bool)((dynamic)task).Enabled) return false;
            definition = ((dynamic)task).Definition;
            principal = ((dynamic)definition).Principal;
            if ((int)((dynamic)principal).RunLevel != 1 || (int)((dynamic)principal).LogonType != 3) return false;
            actions = ((dynamic)definition).Actions;
            if ((int)((dynamic)actions).Count != 1) return false;
            action = ((dynamic)actions).Item(1);
            return string.Equals((string)((dynamic)action).Path, ExePath, StringComparison.OrdinalIgnoreCase)
                && string.Equals((string)((dynamic)action).Arguments, "--autostart", StringComparison.Ordinal);
        }
        catch { return false; }
        finally { Release(action); Release(actions); Release(principal); Release(definition); Release(task); Release(folder); Release(service); }
    }

    public static void SetEnabled(bool enabled)
    {
        if (AppPaths.IsIsolated) throw new InvalidOperationException("隔离测试模式不允许修改 Windows 自启动。");
        object? service = null, folder = null, registered = null;
        try
        {
            service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!);
            ((dynamic)service!).Connect();
            folder = ((dynamic)service).GetFolder(@"\");
            if (enabled)
            {
                var user = WindowsIdentity.GetCurrent().User!.Value;
                var xml = BuildTaskXml(ExePath, user);
                registered = ((dynamic)folder).RegisterTask(TaskName, xml, 6, user, null, 3, null);
                if (!IsEnabled()) throw new IOException("自启动任务创建后校验失败。");
            }
            else
            {
                try
                {
                    object? task = null, definition = null, actions = null, action = null;
                    try
                    {
                        task = ((dynamic)folder).GetTask(TaskName);
                        definition = ((dynamic)task).Definition;
                        actions = ((dynamic)definition).Actions;
                        if ((int)((dynamic)actions).Count == 1)
                        {
                            action = ((dynamic)actions).Item(1);
                            if (string.Equals((string)((dynamic)action).Path, ExePath, StringComparison.OrdinalIgnoreCase))
                                ((dynamic)folder).DeleteTask(TaskName, 0);
                        }
                    }
                    finally { Release(action); Release(actions); Release(definition); Release(task); }
                }
                catch (COMException ex) when ((uint)ex.HResult == 0x80070002) { }
                if (IsEnabled()) throw new IOException("自启动任务删除后校验失败。");
            }
            // 只移除本程序路径的旧版 Run 项，不触碰其他路径的注册值。
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key?.GetValue("ProxyManager") is string legacy
                && string.Equals(legacy, $"\"{ExePath}\"", StringComparison.OrdinalIgnoreCase))
                key.DeleteValue("ProxyManager", false);
        }
        finally { Release(registered); Release(folder); Release(service); }
    }

    public static string BuildTaskXml(string exePath, string userSid)
    {
        var exe = SecurityElement.Escape(Path.GetFullPath(exePath));
        var dir = SecurityElement.Escape(Path.GetDirectoryName(Path.GetFullPath(exePath)));
        var sid = SecurityElement.Escape(userSid);
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Description>应用代理管理器：当前用户登录后启动。</Description></RegistrationInfo>
              <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{sid}</UserId><Delay>PT10S</Delay></LogonTrigger></Triggers>
              <Principals><Principal id="Author"><UserId>{sid}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
              <Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries><StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><AllowHardTerminate>true</AllowHardTerminate><StartWhenAvailable>true</StartWhenAvailable><RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable><AllowStartOnDemand>true</AllowStartOnDemand><Enabled>true</Enabled><Hidden>false</Hidden><ExecutionTimeLimit>PT0S</ExecutionTimeLimit></Settings>
              <Actions Context="Author"><Exec><Command>{exe}</Command><Arguments>--autostart</Arguments><WorkingDirectory>{dir}</WorkingDirectory></Exec></Actions>
            </Task>
            """;
    }

    static void Release(object? value) { if (value != null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value); }
}

