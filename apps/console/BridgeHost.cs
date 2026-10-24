using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace WorkBuddyRemote.WinUI;

internal sealed record BridgeStartResult(bool Ok, string Message, bool NeedsWorkBuddyRestart = false);

/// <summary>
/// 直接拉起 `node bridge/main.mjs`，用 Job Object 绑定生命周期。
/// v2 之后不再经过 PowerShell，因此不会闪窗口，也不依赖脚本的退出码契约。
/// </summary>
internal sealed class BridgeHost : IDisposable
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    private readonly object _gate = new();
    private Process? _bridge;
    private Process? _workBuddy;
    private IntPtr _job = IntPtr.Zero;
    private StreamWriter? _logWriter;
    private string _logPath = "";
    private int _port;
    private bool _externalBridge;

    public event Action<string>? Log;

    public event Action? StateChanged;

    public int Port => _port;

    public bool IsRunning => _bridge is { HasExited: false } || _externalBridge;

    public bool IsExternal => _externalBridge;

    public int ProcessId
    {
        get
        {
            try
            {
                return _bridge is { HasExited: false } ? _bridge.Id : 0;
            }
            catch
            {
                return 0;
            }
        }
    }

    // ---------- 启动 ----------

    public async Task<BridgeStartResult> StartAsync(AppSettings settings, bool restartWorkBuddy, CancellationToken cancellationToken = default)
    {
        await StopAsync(cancellationToken).ConfigureAwait(false);

        var tempDir = settings.RuntimeTempDir;
        Directory.CreateDirectory(tempDir);

        // 1. 停掉上一次遗留的桥接（控制台退出时选择保留的那种）
        await TryKillLeftoverBridgeAsync(tempDir, settings.BridgePort).ConfigureAwait(false);

        // 2. 挑一个可用端口：先看首选端口，被外部程序占用就顺延
        var port = await AllocateBridgePortAsync(settings.BridgePort).ConfigureAwait(false);
        if (port == 0)
        {
            if (_externalBridge)
            {
                return new BridgeStartResult(true, $"端口 {_port} 上已有桥接在运行，直接复用。");
            }

            return new BridgeStartResult(false, $"端口 {settings.BridgePort}~{settings.BridgePort + 19} 都被占用，无法启动桥接。");
        }

        if (port != settings.BridgePort)
        {
            WriteLog($"桥接端口 {settings.BridgePort} 已被占用，自动改用 {port}。");
            settings.BridgePort = port;
            try
            {
                settings.Save();
            }
            catch (Exception ex)
            {
                WriteLog("写回端口配置失败：" + ex.Message);
            }
        }

        _port = port;

        // 3. WorkBuddy 的 CDP 端口：没开就自己拉起来
        var cdpReady = await EnsureWorkBuddyAsync(settings, restartWorkBuddy, cancellationToken).ConfigureAwait(false);
        if (!cdpReady.Ok)
        {
            return new BridgeStartResult(false, cdpReady.Message, cdpReady.NeedsRestart);
        }

        // 4. node
        var nodePath = ResolveNodeExecutable(settings.ProjectRoot);
        if (string.IsNullOrWhiteSpace(nodePath))
        {
            return new BridgeStartResult(false,
                "找不到 node.exe。安装 Node.js 20+，或在程序目录放置 runtime\\node\\node.exe。");
        }

        var scriptPath = Path.Combine(settings.ProjectRoot, "bridge", "main.mjs");
        if (!File.Exists(scriptPath))
        {
            return new BridgeStartResult(false, "找不到桥接脚本：" + scriptPath);
        }

        // 5. 启动 node
        _logPath = Path.Combine(tempDir, $"bridge-{port}.log");
        try
        {
            _logWriter = new StreamWriter(new FileStream(_logPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite),
                new UTF8Encoding(false)) { AutoFlush = true };
        }
        catch
        {
            _logWriter = null;
        }

        var psi = new ProcessStartInfo(nodePath)
        {
            WorkingDirectory = settings.ProjectRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(scriptPath);
        psi.ArgumentList.Add("--port");
        psi.ArgumentList.Add(port.ToString());
        psi.ArgumentList.Add("--host");
        psi.ArgumentList.Add(settings.ListenHost);
        psi.ArgumentList.Add("--cdp-port");
        psi.ArgumentList.Add(settings.CdpPort.ToString());
        psi.ArgumentList.Add("--log-path");
        psi.ArgumentList.Add(Path.Combine(tempDir, $"bridge-{port}.log"));
        if (!string.IsNullOrWhiteSpace(settings.PasswordHash))
        {
            psi.ArgumentList.Add("--password-hash");
            psi.ArgumentList.Add(settings.PasswordHash);
        }
        if (!string.IsNullOrWhiteSpace(settings.WorkBuddyUserDataDir))
        {
            psi.ArgumentList.Add("--user-data-dir");
            psi.ArgumentList.Add(settings.WorkBuddyUserDataDir);
        }
        if (settings.HideWorkBuddyWindowAfterStart)
        {
            psi.ArgumentList.Add("--hide-workbuddy-window-after-start");
        }
        if (_workBuddy is { HasExited: false })
        {
            psi.ArgumentList.Add("--workbuddy-pid");
            psi.ArgumentList.Add(_workBuddy.Id.ToString());
        }

        // 桥接读同一个配置文件（功能开关、隐藏字段都在里面）
        psi.Environment["WORKBUDDY_REMOTE_CONFIG"] = settings.ConfigPath;
        if (!string.IsNullOrWhiteSpace(settings.WorkBuddyExePath))
        {
            // 桥接要靠它定位 app.asar（自定义安装路径下猜不到）
            psi.Environment["WORKBUDDY_EXE_PATH"] = settings.WorkBuddyExePath;
        }
        RemoveEnv(psi, "ELECTRON_RUN_AS_NODE");
        RemoveEnv(psi, "NODE_OPTIONS");

        try
        {
            var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, e) => WriteLog(e.Data);
            process.ErrorDataReceived += (_, e) => WriteLog(e.Data);
            process.Exited += OnBridgeExited;
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            _bridge = process;

            if (!settings.KeepBridgeOnExit)
            {
                AttachToJob(process);
            }
            else
            {
                WriteLog("已按设置不绑定 Job Object：控制台退出后桥接继续运行。");
            }
        }
        catch (Exception ex)
        {
            CloseLogWriter();
            return new BridgeStartResult(false, "启动桥接进程失败：" + ex.Message);
        }

        WriteLog($"已启动桥接进程（PID {ProcessId}，端口 {port}）。");

        // 6. 等 /healthz
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await NetProbe.HttpOkAsync($"http://127.0.0.1:{port}/healthz").ConfigureAwait(false))
            {
                StateChanged?.Invoke();
                return new BridgeStartResult(true, $"桥接已就绪：http://127.0.0.1:{port}/agent-manager/");
            }

            if (_bridge is { HasExited: true })
            {
                var code = SafeExitCode(_bridge);
                var tail = ReadLogTail();
                CloseLogWriter();
                return new BridgeStartResult(false,
                    $"桥接进程启动后立即退出（退出码 {code}）。{Environment.NewLine}{tail}".Trim());
            }

            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }

        StateChanged?.Invoke();
        return new BridgeStartResult(false, "桥接进程已启动，但 30 秒内没有响应 /healthz，请查看运行日志。");
    }

    // ---------- 停止 ----------

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        var bridge = _bridge;
        _bridge = null;

        if (bridge is not null)
        {
            try
            {
                if (_job != IntPtr.Zero)
                {
                    TerminateJobObject(_job, 1);
                }
                else if (!bridge.HasExited)
                {
                    bridge.Kill(entireProcessTree: true);
                }
            }
            catch
            {
            }

            var deadline = DateTime.UtcNow.AddSeconds(8);
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    if (bridge.HasExited) break;
                }
                catch
                {
                    break;
                }
                await Task.Delay(120, cancellationToken).ConfigureAwait(false);
            }

            try
            {
                bridge.Dispose();
            }
            catch
            {
            }
        }

        CloseJob();
        CloseLogWriter();
        _externalBridge = false;
        StateChanged?.Invoke();
    }

    /// <summary>控制台退出时调用：按设置决定杀掉子进程还是放手。</summary>
    public void DetachForExit(bool keepRunning)
    {
        if (keepRunning)
        {
            CloseJob();
            _bridge = null;
        }
        else
        {
            try
            {
                if (_job != IntPtr.Zero) TerminateJobObject(_job, 1);
            }
            catch
            {
            }
            CloseJob();
        }

        CloseLogWriter();
    }

    public void Dispose()
    {
        CloseJob();
        CloseLogWriter();
    }

    // ---------- WorkBuddy ----------

    private async Task<(bool Ok, string Message, bool NeedsRestart)> EnsureWorkBuddyAsync(
        AppSettings settings, bool restartWorkBuddy, CancellationToken cancellationToken)
    {
        if (await NetProbe.PortOpenAsync(settings.CdpPort).ConfigureAwait(false))
        {
            WriteLog($"复用已有的 WorkBuddy 调试端口 {settings.CdpPort}。");
            return (true, "", false);
        }

        var exePath = settings.WorkBuddyExePath;
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
        {
            exePath = WorkBuddyLocator.GuessExecutable() ?? "";
        }
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
        {
            return (false, "找不到 WorkBuddy.exe。请先在设置里指定主程序路径。", false);
        }

        settings.WorkBuddyExePath = exePath;

        if (WorkBuddyLocator.IsRunning())
        {
            if (!restartWorkBuddy && !settings.KillWorkBuddyProcessesBeforeStart)
            {
                WriteLog("WorkBuddy 正在运行，但没有开启调试端口。");
                return (false,
                    "WorkBuddy 已在运行但没有调试端口，必须重启一次才能接上桥接。点“重启 WorkBuddy”即可。",
                    true);
            }

            WriteLog("正在结束现有的 WorkBuddy 进程…");
            WorkBuddyLocator.KillAll();
            var stopDeadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < stopDeadline && WorkBuddyLocator.IsRunning())
            {
                await Task.Delay(300, cancellationToken).ConfigureAwait(false);
            }
        }

        WriteLog($"正在启动 WorkBuddy（调试端口 {settings.CdpPort}）…");
        var psi = new ProcessStartInfo(exePath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        // WorkBuddy 5.9 只认环境变量开关，命令行参数会在自动更新重启后丢失。
        psi.Environment["WORKBUDDY_REMOTE_DEBUGGING_PORT"] = settings.CdpPort.ToString();
        // 从 WorkBuddy 终端里启动控制台时这两个变量会被继承，导致 WorkBuddy.exe 当 node 跑。
        RemoveEnv(psi, "ELECTRON_RUN_AS_NODE");
        RemoveEnv(psi, "NODE_OPTIONS");

        var outLog = Path.Combine(settings.RuntimeTempDir, $"workbuddy-{settings.CdpPort}.out.log");
        var errLog = Path.Combine(settings.RuntimeTempDir, $"workbuddy-{settings.CdpPort}.err.log");
        try
        {
            var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            var outWriter = OpenAppend(outLog);
            var errWriter = OpenAppend(errLog);
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) TryWrite(outWriter, e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) TryWrite(errWriter, e.Data); };
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            _workBuddy = process;
        }
        catch (Exception ex)
        {
            return (false, "启动 WorkBuddy 失败：" + ex.Message, false);
        }

        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await NetProbe.PortOpenAsync(settings.CdpPort).ConfigureAwait(false))
            {
                WriteLog($"WorkBuddy 调试端口 {settings.CdpPort} 已就绪。");
                return (true, "", false);
            }
            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }

        return (false,
            $"WorkBuddy 未能在 60 秒内开启调试端口 {settings.CdpPort}。请确认没有其他实例占用单实例锁。",
            false);
    }

    // ---------- 端口 ----------

    private async Task<int> AllocateBridgePortAsync(int preferred)
    {
        for (var offset = 0; offset < 20; offset++)
        {
            var candidate = preferred + offset;
            if (candidate > 65535) break;

            if (!await NetProbe.PortOpenAsync(candidate).ConfigureAwait(false))
            {
                return candidate;
            }

            // 端口上已经有一个桥接在跑：先确认是不是自己的残留
            if (await NetProbe.HttpOkAsync($"http://127.0.0.1:{candidate}/healthz").ConfigureAwait(false))
            {
                WriteLog($"端口 {candidate} 上已有桥接在运行，直接复用。");
                _port = candidate;
                _externalBridge = true;
                StateChanged?.Invoke();
                return 0;
            }
        }

        return 0;
    }

    private async Task TryKillLeftoverBridgeAsync(string tempDir, int port)
    {
        try
        {
            var pidFile = Path.Combine(tempDir, $"bridge-{port}.pid");
            if (!File.Exists(pidFile)) return;

            if (!int.TryParse(File.ReadAllText(pidFile).Trim(), out var pid) || pid <= 0)
            {
                return;
            }

            Process? existing = null;
            try
            {
                existing = Process.GetProcessById(pid);
            }
            catch
            {
                return;
            }

            using (existing)
            {
                if (existing.HasExited || !existing.ProcessName.Equals("node", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                WriteLog($"停止上一次保留下来的桥接进程（PID {pid}）…");
                try
                {
                    existing.Kill(entireProcessTree: true);
                }
                catch
                {
                }

                var deadline = DateTime.UtcNow.AddSeconds(6);
                while (DateTime.UtcNow < deadline)
                {
                    try
                    {
                        if (existing.HasExited) break;
                    }
                    catch
                    {
                        break;
                    }
                    await Task.Delay(150).ConfigureAwait(false);
                }
            }
        }
        catch
        {
        }
    }

    // ---------- 定位 node ----------

    public static string ResolveNodeExecutable(string projectRoot)
    {
        var candidates = new List<string>
        {
            Path.Combine(projectRoot, "runtime", "node", "node.exe"),
            Path.Combine(AppContext.BaseDirectory, "runtime", "node", "node.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "node.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "nodejs", "node.exe"),
        };

        foreach (var candidate in candidates)
        {
            try
            {
                if (File.Exists(candidate)) return candidate;
            }
            catch
            {
            }
        }

        // 最后交给 PATH
        return "node";
    }

    // ---------- Job Object ----------

    private void AttachToJob(Process process)
    {
        try
        {
            _job = CreateJobObject(IntPtr.Zero, null);
            if (_job == IntPtr.Zero) return;

            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            info.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;
            var length = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
            var pointer = Marshal.AllocHGlobal(length);
            try
            {
                Marshal.StructureToPtr(info, pointer, false);
                if (!SetInformationJobObject(_job, JobObjectExtendedLimitInformation, pointer, (uint)length))
                {
                    WriteLog("设置 Job Object 失败，桥接进程不会随控制台自动结束。");
                    CloseJob();
                    return;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(pointer);
            }

            if (!AssignProcessToJobObject(_job, process.Handle))
            {
                WriteLog("把桥接进程加入 Job Object 失败（可能已被上层作业对象限制）。");
                CloseJob();
            }
        }
        catch (Exception ex)
        {
            WriteLog("创建 Job Object 失败：" + ex.Message);
            CloseJob();
        }
    }

    private void CloseJob()
    {
        if (_job == IntPtr.Zero) return;
        try
        {
            CloseHandle(_job);
        }
        catch
        {
        }
        _job = IntPtr.Zero;
    }

    private void OnBridgeExited(object? sender, EventArgs e)
    {
        var code = SafeExitCode(sender as Process);
        WriteLog($"桥接进程已退出（退出码 {code}）。");
        StateChanged?.Invoke();
    }

    private static int SafeExitCode(Process? process)
    {
        try
        {
            return process?.ExitCode ?? -1;
        }
        catch
        {
            return -1;
        }
    }

    // ---------- 日志 ----------

    private void WriteLog(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;

        lock (_gate)
        {
            try
            {
                _logWriter?.WriteLine(line);
            }
            catch
            {
            }
        }

        Log?.Invoke(line);
    }

    private void CloseLogWriter()
    {
        lock (_gate)
        {
            try
            {
                _logWriter?.Flush();
                _logWriter?.Dispose();
            }
            catch
            {
            }
            _logWriter = null;
        }
    }

    private string ReadLogTail()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_logPath) || !File.Exists(_logPath)) return "";
            var text = File.ReadAllText(_logPath);
            return text.Length <= 1200 ? text : text[^1200..];
        }
        catch
        {
            return "";
        }
    }

    private static StreamWriter? OpenAppend(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            return new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite),
                new UTF8Encoding(false)) { AutoFlush = true };
        }
        catch
        {
            return null;
        }
    }

    private static void TryWrite(StreamWriter? writer, string line)
    {
        try
        {
            writer?.WriteLine(line);
        }
        catch
        {
        }
    }

    private static void RemoveEnv(ProcessStartInfo psi, string name)
    {
        try
        {
            psi.Environment.Remove(name);
            psi.Environment.Remove(name.ToUpperInvariant());
            psi.Environment.Remove(name.ToLowerInvariant());
        }
        catch
        {
        }
    }

    // ---------- P/Invoke ----------

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr securityAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateJobObject(IntPtr job, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
