using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
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
    private const int MaxBufferLines = 200;
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(15);

    private readonly object _sync = new();
    private readonly Queue<string> _output = new();
    private Process? _process;
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
    /// bindto 限定 localhost（C8）；gdb/telnet 端口置 0 禁用，只保留 TCL-RPC 端口；
    /// transport 必须在 interface 脚本之后、target 脚本之前 select。公开以便单测。
    /// </summary>
    public static List<string> BuildArguments(OcdConfig cfg, string? scriptsDir)
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
        args.Add(cfg.InterfaceCfg!);

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
        args.Add("gdb_port 0");
        args.Add("-c");
        args.Add("telnet_port 0");
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
        foreach (var arg in BuildArguments(cfg, scripts))
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
        _ = Task.Run(() => PumpAsync(process.StandardOutput));
        _ = Task.Run(() => PumpAsync(process.StandardError));

        var deadline = DateTime.UtcNow + StartupTimeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            if (process.HasExited)
                return new LaunchOutcome(false, BuildFailureMessage(binary, $"the process exited with code {process.ExitCode}"));

            if (await ProbePortAsync(cfg.Host, cfg.Port, 250, ct).ConfigureAwait(false))
                return new LaunchOutcome(true, $"started {binary} (pid {process.Id}); command port {cfg.Host}:{cfg.Port} is up");

            await Task.Delay(200, ct).ConfigureAwait(false);
        }

        // 超时：回收自己刚启动的进程，别留一个半死的实例占着探针。
        Stop();
        return new LaunchOutcome(false, BuildFailureMessage(binary,
            $"the command port {cfg.Host}:{cfg.Port} did not come up within {StartupTimeout.TotalSeconds:0}s"));
    }

    /// <summary>回收自己启动的进程（只动自己的，不碰外部实例）。</summary>
    public void Stop()
    {
        Process? process;
        lock (_sync)
        {
            process = _process;
            _process = null;
        }
        if (process is null) return;

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
            }
        }
        catch
        {
            // 进程退出时流会断，属正常路径
        }
    }

    private string BuildFailureMessage(string binary, string reason)
    {
        var sb = new StringBuilder();
        sb.Append($"ERROR: {binary} did not become ready: {reason}.");

        var recent = RecentOutput();
        if (recent.Length > 0)
        {
            sb.AppendLine();
            sb.AppendLine("--- recent openocd output ---");
            sb.AppendLine(recent);
        }
        sb.Append("Check interface_cfg / target_cfg (and OCD_SCRIPTS) in show_config; fix ocd.json and retry.");
        return sb.ToString();
    }

    private string RecentOutput()
    {
        lock (_sync)
        {
            return _output.Count == 0 ? "" : string.Join("\n", _output);
        }
    }

    private void OnProcessExit(object? sender, EventArgs e) => Stop();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
        Stop();
    }
}
