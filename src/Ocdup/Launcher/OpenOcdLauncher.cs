using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using Ocdup.Backend;
using Ocdup.Config;

namespace Ocdup.Launcher;

/// <summary>拉起结果：Ok=false 时 Message 是可直接给模型看的诊断文案。</summary>
public readonly record struct LaunchOutcome(bool Ok, string Message);

/// <summary>
/// OpenOCD 子进程生命周期（设计文档 4.5 / 不变量 C5）：
/// 先探测端口（无论谁启动的，已在运行就不重复拉起）→ 找二进制 → 拼命令行 → 启动
/// → 后台抽干 stdout/stderr（否则管道填满会把 OpenOCD 挂死）→ 轮询端口等就绪（超时 15s）；
/// 启动失败或超时都回报近期输出（rolling buffer 200 行）；退出时只回收自己启动的进程。
/// </summary>
public sealed class OpenOcdLauncher : IDisposable
{
    /// <summary>输出缓冲上限（行）。公开供 backend_log 工具对齐上限。</summary>
    public const int MaxBufferLines = 200;
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(15);

    private readonly object _sync = new();
    private readonly Queue<string> _output = new();
    private Process? _process;
    private IntPtr _jobHandle;
    private bool _disposed;

    public OpenOcdLauncher()
    {
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
    }

    /// <summary>轻量端口探测：能连上说明有监听（无论谁启动的）。</summary>
    public static async Task<bool> ProbePortAsync(string host, int port, int timeoutMs, CancellationToken ct = default)
    {
        try
        {
            using var client = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeoutMs);
            await client.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 找 openocd 可执行文件：OCD_BIN 环境变量 → PATH（设计文档 4.6 解析顺序的前两步；
    /// 缓存目录与下载式 provisioning 后置）。找不到返回 null。
    /// </summary>
    public static string? FindBinary()
    {
        var configured = Environment.GetEnvironmentVariable("OCD_BIN");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
            return Path.GetFullPath(configured);

        var names = OperatingSystem.IsWindows()
            ? new[] { "openocd.exe", "openocd.cmd", "openocd.bat" }
            : new[] { "openocd" };

        var pathVariable = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var rawDir in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var dir = rawDir.Trim().Trim('"');
            foreach (var name in names)
            {
                try
                {
                    var candidate = Path.Combine(dir, name);
                    if (File.Exists(candidate)) return candidate;
                }
                catch
                {
                    // PATH 里的怪条目（非法字符等）直接跳过
                }
            }
        }
        return null;
    }

    /// <summary>
    /// 找 OpenOCD scripts 目录（-s 参数的取值）：OCD_SCRIPTS → 各发行版相对二进制的常见布局。
    /// </summary>
    public static string? FindScriptsDir(string? binaryPath)
    {
        var configured = Environment.GetEnvironmentVariable("OCD_SCRIPTS");
        if (!string.IsNullOrWhiteSpace(configured) && Directory.Exists(configured))
            return Path.GetFullPath(configured);

        if (binaryPath is null) return null;
        var binDir = Path.GetDirectoryName(binaryPath);
        if (binDir is null) return null;

        var candidates = new[]
        {
            Path.Combine(binDir, "..", "share", "openocd", "scripts"), // Linux/发行版布局
            Path.Combine(binDir, "..", "openocd", "scripts"),          // xPack 布局
            Path.Combine(binDir, "scripts"),                           // 便携布局
        };
        foreach (var candidate in candidates)
        {
            try
            {
                var full = Path.GetFullPath(candidate);
                if (Directory.Exists(full)) return full;
            }
            catch
            {
                // 非法路径忽略
            }
        }
        return null;
    }

    /// <summary>
    /// 构造 openocd 命令行参数。要点：
    /// bindto 限定 localhost（C8）；gdb/telnet 服务用 "disabled" 真正关闭（实测 0.11+ 支持；
    /// 注意传 0 的语义是「随机端口」而不是禁用），只保留 TCL-RPC 端口；
    /// transport 必须在 interface 脚本之后、target 脚本之前 select；
    /// interfaceCfg 由调用方解析（auto 模式下是探针识别的结果）。公开以便单测。
    /// </summary>
    public static List<string> BuildArguments(OcdConfig cfg, string? scriptsDir, string interfaceCfg)
    {
        var args = new List<string>();
        if (scriptsDir is not null)
        {
            args.Add("-s");
            args.Add(scriptsDir);
        }

        args.Add("-c");
        args.Add($"bindto {cfg.Host}");
        args.Add("-f");
        args.Add(interfaceCfg);

        if (!string.IsNullOrWhiteSpace(cfg.Transport))
        {
            args.Add("-c");
            args.Add($"transport select {cfg.Transport}");
        }

        if (!string.IsNullOrWhiteSpace(cfg.TargetCfg))
        {
            args.Add("-f");
            args.Add(cfg.TargetCfg);
        }

        args.Add("-c");
        args.Add("gdb_port disabled");
        args.Add("-c");
        args.Add("telnet_port disabled");
        args.Add("-c");
        args.Add($"tcl_port {cfg.Port}");
        return args;
    }

    /// <summary>确保后端在跑：已有监听则直接成功，否则拉起自己的一份并等就绪。</summary>
    public async Task<LaunchOutcome> EnsureStartedAsync(OcdConfig cfg, CancellationToken ct = default)
    {
        if (await ProbePortAsync(cfg.Host, cfg.Port, 300, ct).ConfigureAwait(false))
            return new LaunchOutcome(true, $"an OpenOCD listener is already up on {cfg.Host}:{cfg.Port} (not started by ocdup)");

        if (string.IsNullOrWhiteSpace(cfg.InterfaceCfg))
            return new LaunchOutcome(false,
                "ERROR: interface_cfg is not set, so no backend can be started. " +
                "Add e.g. \"interface_cfg\": \"interface/cmsis-dap.cfg\" to ocd.json, then retry (see show_config for the effective settings).");

        var binary = FindBinary();
        if (binary is null)
            return new LaunchOutcome(false,
                "ERROR: no openocd executable found. Set OCD_BIN to the openocd binary path, or install OpenOCD and put it on PATH, then retry. See show_config.");

        var scripts = FindScriptsDir(binary);

        // interface_cfg = "auto":按 USB 设备识别探针并选接口脚本(设计目标「插什么烧录器就用什么」)
        string interfaceCfg;
        string probeNote;
        if (cfg.IsInterfaceAuto)
        {
            var probes = await ProbeDetector.DetectAsync(ct).ConfigureAwait(false);
            if (probes.Count == 0)
                return new LaunchOutcome(false,
                    "ERROR: interface_cfg is \"auto\", but no known debug probe is plugged in " +
                    "(looked for AT-Link / CMSIS-DAP / ST-Link / J-Link on USB). " +
                    "Plug one in, or set interface_cfg explicitly in ocd.json (e.g. \"interface/cmsis-dap.cfg\"). See show_config.");
            var chosen = probes[0];
            interfaceCfg = chosen.Config;
            // 把「凭哪一行认出来的」也带上：多探针/误判时这是第一手排障信息。
            probeNote = $"probe auto-detected: {chosen.Kind} -> {chosen.Config} (evidence: {chosen.Evidence})";
        }
        else
        {
            interfaceCfg = cfg.InterfaceCfg!;
            probeNote = "";
        }

        var psi = new ProcessStartInfo(binary)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
            WorkingDirectory = cfg.ConfigFilePath is not null
                ? Path.GetDirectoryName(cfg.ConfigFilePath)!
                : Directory.GetCurrentDirectory(),
        };
        foreach (var arg in BuildArguments(cfg, scripts, interfaceCfg))
            psi.ArgumentList.Add(arg);

        Process process;
        try
        {
            process = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null");
        }
        catch (Exception ex)
        {
            return new LaunchOutcome(false,
                $"ERROR: cannot start '{binary}': {ex.Message}. Check OCD_BIN and file permissions. See show_config.");
        }

        lock (_sync)
        {
            _process = process;
            _output.Clear();
        }

        // C5 兜底层：把子进程绑进 kill-on-close 的 Job。ocdup 被强杀/崩溃时 ProcessExit
        // 钩子不会触发，作业句柄随进程消亡由内核关闭，整棵 openocd 进程树随之被收割——
        // 不会留一个占着探针和 6666 端口的孤儿。失败降级为无作业（退回 ProcessExit 路径）。
        if (OperatingSystem.IsWindows())
        {
            var job = Job.CreateAndAssign(process);
            if (job != IntPtr.Zero)
                lock (_sync) { _jobHandle = job; }
        }

        _ = Task.Run(() => PumpAsync(process.StandardOutput));
        _ = Task.Run(() => PumpAsync(process.StandardError));

        var deadline = DateTime.UtcNow + StartupTimeout;
        var stableProbes = 0;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            if (process.HasExited)
                return new LaunchOutcome(false, BuildFailureMessage(binary, $"the process exited with code {process.ExitCode}", probeNote));

            if (await ProbePortAsync(cfg.Host, cfg.Port, 250, ct).ConfigureAwait(false))
            {
                // 端口起来不等于活稳了：openocd 先绑 tcl_port、随后才连探针，DP 初始化失败会即刻退出。
                // 连续数次探测都通且进程未退才认定就绪——否则 connect 会撞在刚死的监听上（真机复现过）。
                stableProbes++;
                if (stableProbes >= 3)
                    return new LaunchOutcome(true,
                        $"started {binary} (pid {process.Id}); command port {cfg.Host}:{cfg.Port} is up" +
                        (probeNote.Length > 0 ? $" ({probeNote})" : ""));
            }
            else
            {
                stableProbes = 0;
            }

            await Task.Delay(150, ct).ConfigureAwait(false);
        }

        // 超时：回收自己刚启动的进程，别留一个半死的实例占着探针。
        Stop();
        return new LaunchOutcome(false, BuildFailureMessage(binary,
            $"the command port {cfg.Host}:{cfg.Port} did not come up within {StartupTimeout.TotalSeconds:0}s", probeNote));
    }

    /// <summary>回收自己启动的进程（只动自己的，不碰外部实例）。</summary>
    public void Stop()
    {
        Process? process;
        IntPtr job;
        lock (_sync)
        {
            process = _process;
            _process = null;
            job = _jobHandle;
            _jobHandle = IntPtr.Zero;
        }

        if (process is not null)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(3000);
                }
            }
            catch
            {
                // 进程已在退出中
            }
            finally
            {
                process.Dispose();
            }
        }

        // Job 句柄最后关：即便上面的 Kill 因任何原因没杀干净，KILL_ON_JOB_CLOSE 也会让内核收割整棵进程树。
        Job.Close(job);
    }

    private async Task PumpAsync(StreamReader reader)
    {
        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                lock (_sync)
                {
                    _output.Enqueue(line);
                    while (_output.Count > MaxBufferLines) _output.Dequeue();
                }
                // 异步日志(Info:/Error:/target 事件)同步镜像进流量 JSONL:面板实时可见,
                // TCL-RPC 响应里永远看不到这些行。Record 自身吞错,不影响抽干循环。
                TrafficLog.Shared.Record("backend", "log", line);
            }
        }
        catch
        {
            // 进程退出时流会断，属正常路径
        }
    }

    /// <summary>
    /// 取 lines 的最后 maxLines 行，"\n" 连接。纯函数，公开以便单测。
    /// </summary>
    public static string TailLines(IReadOnlyList<string> lines, int maxLines)
    {
        if (maxLines < 1) maxLines = 1;
        var start = Math.Max(0, lines.Count - maxLines);
        var sb = new StringBuilder();
        for (var i = start; i < lines.Count; i++)
        {
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(lines[i]);
        }
        return sb.ToString();
    }

    /// <summary>
    /// 自己启动的 openocd 的近期输出（旧行先丢）。backend_log 工具与启动失败诊断共用。
    /// </summary>
    public string RecentOutput(int maxLines = MaxBufferLines)
    {
        List<string> snapshot;
        lock (_sync)
        {
            snapshot = _output.ToList();
        }
        return TailLines(snapshot, maxLines);
    }

    private string BuildFailureMessage(string binary, string reason, string probeNote = "")
    {
        var sb = new StringBuilder();
        sb.Append($"ERROR: {binary} did not become ready: {reason}.");
        if (probeNote.Length > 0)
        {
            // auto 模式下必须让用户看到「这次选了哪个接口脚本」，否则启动失败根本无从排障。
            sb.AppendLine();
            sb.Append(probeNote);
        }

        var recent = RecentOutput(MaxBufferLines);
        if (recent.Length > 0)
        {
            sb.AppendLine();
            sb.AppendLine("--- recent openocd output ---");
            sb.AppendLine(recent);
        }
        sb.Append("Check interface_cfg / target_cfg (and OCD_SCRIPTS) in show_config; fix ocd.json and retry.");

        if (recent.Contains("LIBUSB_ERROR", StringComparison.OrdinalIgnoreCase) ||
            recent.Contains("No J-Link device found", StringComparison.OrdinalIgnoreCase))
        {
            sb.AppendLine();
            sb.Append("NOTE: a J-Link probe is attached, but on Windows OpenOCD opens USB through libusb, " +
                      "and the installed SEGGER driver owns the device (LIBUSB_ERROR_NOT_FOUND). " +
                      "Options: plug an AT-Link / CMSIS-DAP probe instead, or swap the J-Link driver to WinUSB with Zadig " +
                      "(SEGGER tools will not see the probe until you switch the driver back).");
        }
        return sb.ToString();
    }

    private void OnProcessExit(object? sender, EventArgs e) => Stop();

    /// <summary>
    /// Windows Job Object 封装（仅 Windows 调用）：KILL_ON_JOB_CLOSE——
    /// 句柄关闭（含创建它的 ocdup 进程消亡）时内核自动终止作业内全部进程。
    /// </summary>
    private static class Job
    {
        private const int JobObjectExtendedLimitInformationClass = 9;
        private const uint LimitFlagsKillOnJobClose = 0x2000;

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

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateJobObjectW(IntPtr lpJobAttributes, string? lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(
            IntPtr hJob, int jobObjectInfoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION lpJobObjectInfo, int cbJobObjectInfoLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        /// <summary>创建 kill-on-close 作业并把进程放进去；任何一步失败返回 IntPtr.Zero（主流程不受影响）。</summary>
        internal static IntPtr CreateAndAssign(Process process)
        {
            var handle = IntPtr.Zero;
            try
            {
                handle = CreateJobObjectW(IntPtr.Zero, null);
                if (handle == IntPtr.Zero) return IntPtr.Zero;

                var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                info.BasicLimitInformation.LimitFlags = LimitFlagsKillOnJobClose;
                if (!SetInformationJobObject(handle, JobObjectExtendedLimitInformationClass, ref info, Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
                    return IntPtr.Zero;
                if (!AssignProcessToJobObject(handle, process.Handle))
                    return IntPtr.Zero;
                return handle;
            }
            catch
            {
                Close(handle);
                return IntPtr.Zero;
            }
        }

        internal static void Close(IntPtr handle)
        {
            if (handle != IntPtr.Zero) CloseHandle(handle);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
        Stop();
    }
}
