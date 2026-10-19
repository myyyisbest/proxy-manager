using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ProxyManager.Infrastructure;

/// <summary>
/// Starts the engine suspended so it cannot change networking before its cleanup Job is bound.
/// Each engine owns a hidden console; Ctrl+C therefore targets that console only.
/// </summary>
internal sealed class EngineChildProcess : IDisposable
{
    const uint CreateSuspended = 0x4, CreateNewConsole = 0x10, ExtendedStartupInfoPresent = 0x80000;
    const uint StartUseShowWindow = 1, StartUseStdHandles = 0x100, HandleFlagInherit = 1;
    const nuint AttributeHandleList = 0x00020002;
    static readonly object ConsoleSignalLock = new();

    public Process Process { get; }
    readonly JobObject _job;
    readonly StreamReader _stdout;
    readonly StreamReader _stderr;
    readonly Task _outputTask;
    int _disposed;

    EngineChildProcess(Process process, JobObject job, StreamReader stdout, StreamReader stderr, Action<string> onOutput)
    {
        Process = process;
        _job = job;
        _stdout = stdout;
        _stderr = stderr;
        _outputTask = Task.WhenAll(ReadLinesAsync(stdout, onOutput), ReadLinesAsync(stderr, onOutput));
    }

    public static EngineChildProcess Start(string fileName, string configPath, string workingDirectory, Action<string> onOutput)
    {
        JobObject? job = new();
        Process? process = null;
        SafeFileHandle? stdoutRead = null, stdoutWrite = null, stderrRead = null, stderrWrite = null, stdin = null;
        IntPtr attributes = IntPtr.Zero, handleList = IntPtr.Zero;
        var pi = new ProcessInformation();
        try
        {
            var sa = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), InheritHandle = 1 };
            if (!CreatePipe(out stdoutRead, out stdoutWrite, ref sa, 0)) ThrowLastError("创建引擎标准输出管道失败");
            if (!CreatePipe(out stderrRead, out stderrWrite, ref sa, 0)) ThrowLastError("创建引擎错误输出管道失败");
            if (!SetHandleInformation(stdoutRead, HandleFlagInherit, 0) ||
                !SetHandleInformation(stderrRead, HandleFlagInherit, 0)) ThrowLastError("保护引擎输出管道失败");
            stdin = CreateFile("NUL", 0x80000000, 3, ref sa, 3, 0, IntPtr.Zero);
            if (stdin.IsInvalid) ThrowLastError("创建引擎输入句柄失败");

            nuint size = 0;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            attributes = Marshal.AllocHGlobal(checked((int)size));
            if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref size)) ThrowLastError("创建引擎句柄列表失败");
            handleList = Marshal.AllocHGlobal(IntPtr.Size * 3);
            Marshal.WriteIntPtr(handleList, 0, stdin.DangerousGetHandle());
            Marshal.WriteIntPtr(handleList, IntPtr.Size, stdoutWrite.DangerousGetHandle());
            Marshal.WriteIntPtr(handleList, IntPtr.Size * 2, stderrWrite.DangerousGetHandle());
            if (!UpdateProcThreadAttribute(attributes, 0, AttributeHandleList, handleList,
                    (nuint)(IntPtr.Size * 3), IntPtr.Zero, IntPtr.Zero)) ThrowLastError("限制引擎继承句柄失败");
            var si = new StartupInfoEx
            {
                StartupInfo = new StartupInfo
                {
                    Cb = Marshal.SizeOf<StartupInfoEx>(),
                    Flags = StartUseShowWindow | StartUseStdHandles,
                    ShowWindow = 0,
                    StdInput = stdin.DangerousGetHandle(),
                    StdOutput = stdoutWrite.DangerousGetHandle(),
                    StdError = stderrWrite.DangerousGetHandle(),
                },
                AttributeList = attributes,
            };
            var command = new StringBuilder($"\"{fileName}\" run -c \"{configPath}\" --disable-color");
            if (!CreateProcess(fileName, command, IntPtr.Zero, IntPtr.Zero, true,
                    CreateSuspended | CreateNewConsole | ExtendedStartupInfoPresent,
                    IntPtr.Zero, workingDirectory, ref si, out pi)) ThrowLastError("创建引擎进程失败");
            process = Process.GetProcessById(checked((int)pi.ProcessId));
            // This must succeed before ResumeThread: a failed Job assignment must never run TUN code.
            job.Add(process);
            if (ResumeThread(pi.Thread) == uint.MaxValue) ThrowLastError("恢复引擎进程失败");
            stdoutWrite.Dispose(); stdoutWrite = null;
            stderrWrite.Dispose(); stderrWrite = null;
            var stdout = new StreamReader(new FileStream(stdoutRead, FileAccess.Read, 4096, false), Encoding.UTF8);
            stdoutRead = null;
            var stderr = new StreamReader(new FileStream(stderrRead, FileAccess.Read, 4096, false), Encoding.UTF8);
            stderrRead = null;
            var result = new EngineChildProcess(process, job, stdout, stderr, onOutput);
            process = null;
            job = null;
            return result;
        }
        catch
        {
            // Also handle the narrow case where GetProcessById itself failed after CreateProcess.
            if (pi.Process != IntPtr.Zero) TerminateProcess(pi.Process, 1);
            try { process?.WaitForExit(3000); } catch (InvalidOperationException) { }
            process?.Dispose();
            job?.Dispose();
            throw;
        }
        finally
        {
            if (pi.Thread != IntPtr.Zero) CloseHandle(pi.Thread);
            if (pi.Process != IntPtr.Zero) CloseHandle(pi.Process);
            if (attributes != IntPtr.Zero) { DeleteProcThreadAttributeList(attributes); Marshal.FreeHGlobal(attributes); }
            if (handleList != IntPtr.Zero) Marshal.FreeHGlobal(handleList);
            stdoutRead?.Dispose(); stdoutWrite?.Dispose(); stderrRead?.Dispose(); stderrWrite?.Dispose(); stdin?.Dispose();
        }
    }

    static async Task ReadLinesAsync(StreamReader reader, Action<string> onOutput)
    {
        try
        {
            // Bound line buffers as well as the application's log queue, even for a faulty engine.
            var buffer = new char[4096];
            var line = new StringBuilder();
            int count;
            while ((count = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            {
                for (var i = 0; i < count; i++)
                {
                    if (buffer[i] == '\n' || line.Length >= 8192)
                    {
                        if (line.Length > 0) onOutput(line.ToString().TrimEnd('\r'));
                        line.Clear();
                    }
                    if (buffer[i] != '\n') line.Append(buffer[i]);
                }
            }
            if (line.Length > 0) onOutput(line.ToString().TrimEnd('\r'));
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }

    public void Stop()
    {
        if (HasExited()) return;
        TrySendInterrupt();
        if (Process.WaitForExit(2500)) return;
        // The Job contains this engine and its children only. Closing it is the forced fallback.
        _job.Dispose();
        if (!Process.WaitForExit(5000))
            throw new InvalidOperationException("等待引擎退出超时；请检查系统权限和安全软件日志。");
    }

    bool HasExited()
    {
        try { return Process.HasExited; }
        catch (InvalidOperationException) { return true; }
    }

    bool TrySendInterrupt()
    {
        // Never detach an existing console owned by the host/test runner.
        lock (ConsoleSignalLock)
        {
            var ids = new uint[1];
            if (GetConsoleProcessList(ids, 1) != 0) return false;
            if (!AttachConsole((uint)Process.Id)) return false;
            try
            {
                // AttachConsole resets handlers. Ignore Ctrl+C locally while the child handles it.
                if (!SetConsoleCtrlHandler(IntPtr.Zero, true)) return false;
                return GenerateConsoleCtrlEvent(0, 0);
            }
            finally
            {
                FreeConsole();
                SetConsoleCtrlHandler(IntPtr.Zero, false);
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _job.Dispose();
        try { if (!HasExited()) Process.WaitForExit(5000); } catch (InvalidOperationException) { }
        _stdout.Dispose();
        _stderr.Dispose();
        Process.Dispose();
    }

    static void ThrowLastError(string message) => throw new Win32Exception(Marshal.GetLastWin32Error(), message);

    [StructLayout(LayoutKind.Sequential)] struct SecurityAttributes { public int Length; public IntPtr SecurityDescriptor; public int InheritHandle; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct StartupInfo
    {
        public int Cb; public string? Reserved, Desktop, Title;
        public uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public ushort ShowWindow, Reserved2Size; public IntPtr Reserved2, StdInput, StdOutput, StdError;
    }
    [StructLayout(LayoutKind.Sequential)] struct StartupInfoEx { public StartupInfo StartupInfo; public IntPtr AttributeList; }
    [StructLayout(LayoutKind.Sequential)] struct ProcessInformation { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool CreatePipe(out SafeFileHandle readPipe, out SafeFileHandle writePipe, ref SecurityAttributes attributes, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetHandleInformation(SafeFileHandle handle, uint mask, uint flags);
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)] static extern SafeFileHandle CreateFile(string path, uint access, uint share, ref SecurityAttributes attributes, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref nuint size);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, nuint attribute, IntPtr value, nuint size, IntPtr previous, IntPtr returnSize);
    [DllImport("kernel32.dll")] static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool CreateProcess(string application, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles, uint flags, IntPtr environment, string directory, ref StartupInfoEx startup, out ProcessInformation information);
    [DllImport("kernel32.dll", SetLastError = true)] static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool TerminateProcess(IntPtr process, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)] static extern uint GetConsoleProcessList(uint[] processList, uint count);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool AttachConsole(uint processId);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool FreeConsole();
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetConsoleCtrlHandler(IntPtr handler, bool add);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GenerateConsoleCtrlEvent(uint ctrlEvent, uint processGroupId);
}
