using System.IO;
using System.Net.Sockets;
using Ocdup.Config;
using Ocdup.Launcher;

namespace Ocdup.Backend;

/// <summary>后端不可用 / 控制通道失败时抛出；Message 是可直接给模型看的可操作文案（6.3）。</summary>
public sealed class BackendException : Exception
{
    public BackendException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// 进程内的后端会话：把「确保有后端在跑 → 建立可复用的 TCL-RPC 连接 → 发命令 → 失败时丢弃连接」
/// 这条流程收在一处（设计文档 4.5；C6：单连接、单目标模型）。
/// 连接建立阶段失败会重试一次；命令已发出后的超时/断链不会自动重试——写操作重放不安全。
/// </summary>
public sealed class BackendSession
{
    public static BackendSession Instance { get; } = new();

    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private readonly OpenOcdLauncher _launcher = new();
    private readonly TrafficLog _traffic = TrafficLog.Shared;
    private TclRpcClient? _client;

    /// <summary>
    /// 最近一次「确保后端可用」的说明：auto 模式识别到的探针、或发现已有监听。
    /// 供工具层回报给模型——否则 auto 模式最关键的信息（最终用了哪个接口脚本）会丢失。
    /// </summary>
    public string? LastBackendNote { get; private set; }

    private BackendSession() { }

    /// <summary>当前是否有后端监听（不启动任何东西）。</summary>
    public static Task<bool> IsBackendUpAsync(OcdConfig cfg, CancellationToken ct = default) =>
        OpenOcdLauncher.ProbePortAsync(cfg.Host, cfg.Port, 300, ct);

    /// <summary>
    /// 执行一条 TCL 命令。startIfNeeded=false 时不会拉起后端（status 这类只读探测用）。
    /// op 是发起调用的工具名，只用于流量镜像（mcp-traffic.jsonl 的 tool 字段）。
    /// </summary>
    public async Task<string> ExecuteAsync(OcdConfig cfg, string command, bool startIfNeeded, CancellationToken ct = default, string op = "tcl")
    {
        var client = await AcquireAsync(cfg, startIfNeeded, ct).ConfigureAwait(false);
        _traffic.Record(op, "cmd", command, $"{cfg.Host}:{cfg.Port}");
        try
        {
            var output = await client.CommandAsync(command, ct).ConfigureAwait(false);
            _traffic.Record(op, "rsp", output, $"{cfg.Host}:{cfg.Port}");
            return output;
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or TimeoutException)
        {
            _traffic.Record(op, "err", $"control channel failed: {ex.Message}", $"{cfg.Host}:{cfg.Port}");
            Invalidate(); // 连接已坏，下次调用重新建立
            throw new BackendException(
                $"ERROR: the control channel failed while running '{command}': {ex.Message}. " +
                "The connection was reset; call connect and retry.", ex);
        }
    }

    private async Task<TclRpcClient> AcquireAsync(OcdConfig cfg, bool startIfNeeded, CancellationToken ct)
    {
        await _connectGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var existing = Volatile.Read(ref _client);
            if (existing is not null) return existing;

            if (!await OpenOcdLauncher.ProbePortAsync(cfg.Host, cfg.Port, 300, ct).ConfigureAwait(false))
            {
                if (!startIfNeeded)
                {
                    var nothing = $"ERROR: nothing is listening on {cfg.Host}:{cfg.Port}. Call connect to start the backend, or fix the port in ocd.json. See show_config.";
                    _traffic.Record("backend", "SYS", nothing, $"{cfg.Host}:{cfg.Port}");
                    throw new BackendException(nothing);
                }

                var outcome = await _launcher.EnsureStartedAsync(cfg, ct).ConfigureAwait(false);
                _traffic.Record("backend", "SYS", outcome.Message, $"{cfg.Host}:{cfg.Port}");
                if (!outcome.Ok)
                    throw new BackendException(outcome.Message);
                LastBackendNote = outcome.Message;
            }
            else
            {
                LastBackendNote = $"using the OpenOCD listener already present on {cfg.Host}:{cfg.Port}";
                _traffic.Record("backend", "SYS", LastBackendNote, $"{cfg.Host}:{cfg.Port}");
            }

            TclRpcClient connected;
            try
            {
                connected = await TclRpcClient
                    .ConnectAsync(
                        cfg.Host, cfg.Port,
                        connectTimeout: TimeSpan.FromSeconds(5),
                        commandTimeout: TimeSpan.FromSeconds(cfg.CommandTimeoutSeconds),
                        ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                var message =
                    $"ERROR: cannot open the OpenOCD TCL-RPC channel on {cfg.Host}:{cfg.Port}: {ex.Message}. " +
                    "Check the probe wiring and interface_cfg/target_cfg in ocd.json, then retry or call connect.";
                // 端口曾被探测到、随即又拒绝连接 → openocd 大概率起来后死了（探针/DP 初始化失败）。
                // 把自拉后端的近期输出带出来，别让模型只看到一句 refused 无从下手。
                var recent = _launcher.RecentOutput(OpenOcdLauncher.MaxBufferLines);
                if (recent.Length > 0)
                    message += "\n--- recent openocd output ---\n" + recent;
                _traffic.Record("backend", "SYS", message, $"{cfg.Host}:{cfg.Port}");
                throw new BackendException(message, ex);
            }

            Volatile.Write(ref _client, connected);
            return connected;
        }
        finally
        {
            _connectGate.Release();
        }
    }

    /// <summary>丢弃当前连接（后端可能已被外部重启或半死）。</summary>
    public void Invalidate() => Interlocked.Exchange(ref _client, null)?.Dispose();

    /// <summary>
    /// 重启后端（set_target 的切换通道）：丢连接 → 杀 ocdup 自己拉起的 openocd（外部
    /// 实例杀不动，stop 是 no-op）→ 等端口释放 → 用当前配置重新拉起并就绪。
    /// 返回启动说明；启动失败抛 BackendException（文案由启动路径给全）。
    /// </summary>
    public async Task<string> RestartAsync(CancellationToken ct = default)
    {
        var cfg = ConfigStore.Instance.Current;
        Invalidate();
        _launcher.Stop();

        // 端口linger：Kill+WaitForExit 后通常已释放，最多再等 2s（外部实例占着则等满也没用，
        // 让 Acquire 走「复用已有监听」的分支，语义正确但不换 target —— set_target 会自己解释）。
        for (var i = 0; i < 20 && await OpenOcdLauncher.ProbePortAsync(cfg.Host, cfg.Port, 100, ct).ConfigureAwait(false); i++)
            await Task.Delay(100, ct).ConfigureAwait(false);

        await AcquireAsync(cfg, startIfNeeded: true, ct).ConfigureAwait(false);
        return LastBackendNote ?? "backend restarted";
    }

    /// <summary>自己拉起的 openocd 的近期输出（backend_log 工具的数据源）。</summary>
    public string RecentBackendOutput(int maxLines) => _launcher.RecentOutput(maxLines);
}
