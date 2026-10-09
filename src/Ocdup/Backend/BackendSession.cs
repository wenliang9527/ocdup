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
/// 连接建立阶段失败会重试一次；命令已发出后的断链：只读 op 自动重连并重放一次（P1-5），
/// 写/执行类 op 不重放——命令可能已生效，重放会产生二次效应（白名单见 <see cref="ReplaySafeOps"/>）。
/// 空闲心跳（P1-5）：通道空闲超过阈值时后台发一次 version 探测，提前发现后端被外部杀掉，
/// 空闲时长经 ChannelState 供给 status 上报。
/// </summary>
public sealed class BackendSession
{
    public static BackendSession Instance { get; } = new();

    private readonly SemaphoreSlim _connectGate = new(1, 1);
    /// <summary>通道串行闸（P1-5）：同一时刻只允许一条命令占用 TCL-RPC 连接——业务命令与心跳探测互斥。</summary>
    private readonly SemaphoreSlim _channelGate = new(1, 1);
    private readonly OpenOcdLauncher _launcher = new();
    private readonly TrafficLog _traffic = TrafficLog.Shared;
    private TclRpcClient? _client;

    /// <summary>心跳节奏（P1-5）：后台探测每 15s 醒一次，通道空闲满 30s 才真正发探测。</summary>
    internal static readonly TimeSpan HeartbeatTick = TimeSpan.FromSeconds(15);
    internal static readonly TimeSpan HeartbeatIdleThreshold = TimeSpan.FromSeconds(30);

    /// <summary>最近一次通道活动（Environment.TickCount64，monotonic，不回拨）。</summary>
    private long _lastChannelActivityTicks = Environment.TickCount64;
    private readonly Timer _heartbeat;

    /// <summary>
    /// 断链后允许自动重放一次的 op 白名单（P1-5）：目标侧纯读，重放不产生二次效应。
    /// flash_write（program 可能已生效，重放 = 二次擦写）/ run_command（内容不可静态判定）/
    /// halt / resume / reset / write_* / flash_erase_sector / watch 一律不在列——重放不安全。
    /// </summary>
    internal static readonly HashSet<string> ReplaySafeOps = new(StringComparer.Ordinal)
    {
        "status", "connect", "flash_info", "identify_chip", "read_memory",
        "verify_image", "dump_memory", "read_variable",
    };

    internal static bool IsReplaySafe(string op) => ReplaySafeOps.Contains(op);

    /// <summary>
    /// 最近一次「确保后端可用」的说明：auto 模式识别到的探针、或发现已有监听。
    /// 供工具层回报给模型——否则 auto 模式最关键的信息（最终用了哪个接口脚本）会丢失。
    /// </summary>
    public string? LastBackendNote { get; private set; }

    private BackendSession()
    {
        _heartbeat = new Timer(_ => _ = ProbeIdleAsync(), null, HeartbeatTick, HeartbeatTick);
    }

    /// <summary>当前是否有后端监听（不启动任何东西）。</summary>
    public static Task<bool> IsBackendUpAsync(OcdConfig cfg, CancellationToken ct = default) =>
        OpenOcdLauncher.ProbePortAsync(cfg.Host, cfg.Port, 300, ct);

    /// <summary>
    /// 执行一条 TCL 命令。startIfNeeded=false 时不会拉起后端（status 这类只读探测用）。
    /// op 是发起调用的工具名：既用于流量镜像（mcp-traffic.jsonl 的 tool 字段），
    /// 也决定断链后能否自动重放（P1-5：<see cref="IsReplaySafe"/>）。
    /// </summary>
    public async Task<string> ExecuteAsync(OcdConfig cfg, string command, bool startIfNeeded, CancellationToken ct = default, string op = "tcl")
    {
        try
        {
            return await ExecuteOnceAsync(cfg, command, startIfNeeded, ct, op).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or TimeoutException)
        {
            Invalidate(); // 连接已坏，下次调用重新建立
            _traffic.Record(op, "err", $"control channel failed: {ex.Message}", Endpoint(cfg));

            if (!IsReplaySafe(op))
                throw new BackendException(
                    $"ERROR: the control channel failed while running '{command}': {ex.Message}. " +
                    "The command may have taken effect, so it was NOT replayed; call connect and retry.", ex);

            // P1-5：只读命令重放安全——自动重连并重放一次；写操作维持「不重放」。
            try
            {
                var replayed = await ExecuteOnceAsync(cfg, command, startIfNeeded, ct, op).ConfigureAwait(false);
                _traffic.Record("backend", "SYS", $"read-only op '{op}' replayed once after reconnect", Endpoint(cfg));
                return replayed;
            }
            catch (Exception retry) when (retry is IOException or SocketException or ObjectDisposedException
                or TimeoutException or BackendException)
            {
                Invalidate();
                throw new BackendException(
                    $"ERROR: the control channel failed while running '{command}', and the read-only replay " +
                    $"after reconnect failed too: {retry.Message} (original failure: {ex.Message}). " +
                    "Call connect and retry.", retry);
            }
        }
    }

    private async Task<string> ExecuteOnceAsync(OcdConfig cfg, string command, bool startIfNeeded, CancellationToken ct, string op)
    {
        // 通道串行（P1-5）：同一时刻只允许一条命令占用 TCL-RPC 连接——业务命令与心跳互斥。
        await _channelGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var client = await AcquireAsync(cfg, startIfNeeded, ct).ConfigureAwait(false);
            _traffic.Record(op, "cmd", command, Endpoint(cfg));
            var output = await client.CommandAsync(command, ct).ConfigureAwait(false);
            Volatile.Write(ref _lastChannelActivityTicks, Environment.TickCount64);
            _traffic.Record(op, "rsp", output, Endpoint(cfg));
            return output;
        }
        finally
        {
            _channelGate.Release();
        }
    }

    private static string Endpoint(OcdConfig cfg) => $"{cfg.Host}:{cfg.Port}";

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

    /// <summary>
    /// 断开后端（v0.4.6 的 disconnect 通道）：丢连接 → 杀 ocdup **自己拉起**的 openocd
    /// （外部实例杀不动，stop 是 no-op）→ 等端口释放。**不重新拉起**——探针交还给别的
    /// 软件用；调 connect 随时拉回来。
    /// 幂等：本来就没在跑也回可读文案，不抛。占着 _connectGate：断开不能和一次正在
    /// 进行的 Acquire 交叉（否则可能刚断开又被顺手拉起来）。
    /// </summary>
    public async Task<string> StopAsync(CancellationToken ct = default)
    {
        var cfg = ConfigStore.Instance.Current;
        await _connectGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var wasUp = Volatile.Read(ref _client) is not null
                || await OpenOcdLauncher.ProbePortAsync(cfg.Host, cfg.Port, 300, ct).ConfigureAwait(false);

            Invalidate();
            _launcher.Stop();

            // 端口 linger：Kill+WaitForExit 后通常已释放，最多再等 2s
            for (var i = 0; i < 20 && await OpenOcdLauncher.ProbePortAsync(cfg.Host, cfg.Port, 100, ct).ConfigureAwait(false); i++)
                await Task.Delay(100, ct).ConfigureAwait(false);

            var message = StopVerdict(wasUp,
                await OpenOcdLauncher.ProbePortAsync(cfg.Host, cfg.Port, 300, ct).ConfigureAwait(false),
                $"{cfg.Host}:{cfg.Port}");
            _traffic.Record("backend", "SYS", message, $"{cfg.Host}:{cfg.Port}");
            return message;
        }
        finally
        {
            _connectGate.Release();
        }
    }

    /// <summary>
    /// 断开结果的文案（纯函数，可单测）：区分「真断了」/「本来就没跑」/「端口上不是
    /// 我们起的实例，杀不动」三种——第三种必须说清楚，否则用户会以为探针已经释放。
    /// </summary>
    internal static string StopVerdict(bool wasUp, bool stillUp, string endpoint) =>
        stillUp
            ? $"backend on {endpoint} is still running and ocdup cannot stop it: that listener was started outside ocdup. " +
              "Stop that openocd yourself (or its owner) to free the probe."
            : wasUp
                ? $"backend stopped - the probe behind {endpoint} is released for other software. Call connect to bring it back."
                : $"backend was not running (nothing on {endpoint}) - nothing to disconnect.";

    // ── P1-5：空闲心跳 + 通道状态 ──

    /// <summary>当前通道状态（status 上报用）：是否已连接 + 距上次成功命令的空闲时长。</summary>
    public (bool Connected, TimeSpan Idle) ChannelState
    {
        get
        {
            var connected = Volatile.Read(ref _client) is not null;
            var idle = TimeSpan.FromMilliseconds(Environment.TickCount64 - Volatile.Read(ref _lastChannelActivityTicks));
            return (connected, idle);
        }
    }

    /// <summary>通道状态一行文案（纯函数，可单测）。</summary>
    internal static string FormatChannelState(bool connected, TimeSpan idle) =>
        connected
            ? $"channel: connected, idle {(int)idle.TotalSeconds}s (heartbeat probes while idle)"
            : "channel: not connected (the next call reconnects automatically)";

    /// <summary>心跳是否该发（纯函数，可单测）：仅当通道健在且空闲超过阈值。</summary>
    internal static bool ShouldProbe(bool connected, TimeSpan idle) =>
        connected && idle >= HeartbeatIdleThreshold;

    /// <summary>
    /// 心跳探测（后台 Timer 回调，15s 一拍）：空闲满阈值且拿到通道闸才发 version——
    /// 提前发现 openocd 被外部杀掉（否则要等下一次真命令才报错）。探测失败只记 SYS、
    /// 丢连接，绝不向外抛（Timer 回调）；成功的探测不进流量日志（免得空闲期刷屏）。
    /// </summary>
    private async Task ProbeIdleAsync()
    {
        try
        {
            var (connected, idle) = ChannelState;
            if (!ShouldProbe(connected, idle)) return;

            if (!await _channelGate.WaitAsync(0).ConfigureAwait(false)) return; // 业务命令在跑：让路
            try
            {
                var (stillConnected, stillIdle) = ChannelState;
                if (!ShouldProbe(stillConnected, stillIdle)) return;
                var client = Volatile.Read(ref _client);
                if (client is null) return;

                await client.CommandAsync("version", CancellationToken.None).ConfigureAwait(false);
                Volatile.Write(ref _lastChannelActivityTicks, Environment.TickCount64);
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or TimeoutException)
            {
                Invalidate();
                _traffic.Record("backend", "SYS",
                    $"heartbeat lost ({ex.Message}) - the backend appears gone; the next call will reconnect automatically.",
                    Endpoint(ConfigStore.Instance.Current));
            }
            finally
            {
                _channelGate.Release();
            }
        }
        catch
        {
            // 心跳是尽力而为的探针：任何意外都不允许外泄（Timer 回调线程）。
        }
    }
}
