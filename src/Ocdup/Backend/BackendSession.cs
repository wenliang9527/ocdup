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
    /// v0.4.30 增 list_breakpoints（`bp` 无参即列，纯读）；v0.4.37 引用 <see cref="OpNames"/>
    /// 单一真源，并增 read_peripheral_register（纯读；此前靠 SvdTools 的 "read_memory" 别名
    /// 间接命中白名单——流量归属因此失真，audit F-8）。
    /// </summary>
    internal static readonly HashSet<string> ReplaySafeOps = new(StringComparer.Ordinal)
    {
        OpNames.Status, OpNames.Connect, OpNames.FlashInfo, OpNames.IdentifyChip,
        OpNames.ReadMemory, OpNames.VerifyImage, OpNames.DumpMemory, OpNames.ReadVariable,
        OpNames.ListBreakpoints, OpNames.ReadPeripheralRegister,
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
        ChannelResult used = default;
        try
        {
            used = await ExecuteOnceAsync(cfg, command, startIfNeeded, ct, op).ConfigureAwait(false);
            return used.Output;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 调用方取消（MCP 宿主掐掉工具调用、Ctrl+C）不能只是往外抛：命令已经写进
            // socket 了，OpenOCD 稍后仍会把这条应答吐出来。连接留着，下一任命令就会
            // 把它当成自己的答案读走——现象是「读什么都返回同一个值」，而且
            // tcl_server.c 严格一问一答、没有序号，错位会一直向后传染。
            // 取消语义原样上抛（宿主在等这个取消），连接已在 ExecuteOnceAsync 里作废。
            _traffic.Record(op, "err",
                $"cancelled by the caller while running '{command}' - the control channel was dropped, " +
                "because a late reply would otherwise be handed to the next command", Endpoint(cfg));
            throw;
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or TimeoutException)
        {
            // 连接已坏，下次调用重新建立。v0.4.31 定向失效：只丢真正失败的那条连接——
            // 此前无条件 Invalidate 会把「失败与清理之间」别的调用刚 Acquire 的新连接一起杀掉
            //（B 得到伪 ObjectDisposedException，写类 op 还会误报「命令可能已生效」）。
            if (used.Client is not null) InvalidateClient(used.Client); else Invalidate();
            _traffic.Record(op, "err", $"control channel failed: {ex.Message}", Endpoint(cfg));

            if (!IsReplaySafe(op))
            {
                // v0.4.40（现场实战 session cad81f）：「可能已生效」的歧义收窄成确定性证据——
                // 命令记录（cmd）在发出前落流量日志，响应（rsp）没来 = 命令确实已下发、
                // 效果未确认；写类操作重连后回读才能定案。附已知故障 hint（KnownIssues）。
                var hint = KnownIssues.HintFor(ex.Message);
                throw new BackendException(
                    $"ERROR: the control channel failed while running '{command}': {ex.Message}. " +
                    "The command WAS dispatched to OpenOCD (the traffic log has its record without a matching reply), " +
                    "but its effect is unconfirmed - it was NOT replayed; call connect and retry, " +
                    "and for writes read the value back to confirm it stuck." +
                    (hint is null ? "" : "\n" + hint), ex);
            }

            // P1-5：只读命令重放安全——自动重连并重放一次；写操作维持「不重放」。
            // v0.4.35（audit F-3）：重放一律不以 startIfNeeded 执行——重放是「通道重试」，
            // 不该复刻「确保后端」的旧决定，否则显式 disconnect 后在飞只读命令的重放会把
            // 已交还的探针重新拉起占住。
            try
            {
                used = await ExecuteOnceAsync(cfg, command, startIfNeeded: false, ct, op).ConfigureAwait(false);
                _traffic.Record("backend", "SYS", $"read-only op '{op}' replayed once after reconnect", Endpoint(cfg));
                return used.Output;
            }
            catch (Exception retry) when (retry is IOException or SocketException or ObjectDisposedException
                or TimeoutException or BackendException)
            {
                if (used.Client is not null) InvalidateClient(used.Client); else Invalidate();
                throw new BackendException(
                    $"ERROR: the control channel failed while running '{command}', and the read-only replay " +
                    $"after reconnect failed too: {retry.Message} (original failure: {ex.Message}). " +
                    "Call connect and retry.", retry);
            }
        }
    }

    /// <summary>一次通道往返的结果：输出 + 实际承载它的连接（失败时据此定向失效）。</summary>
    private readonly record struct ChannelResult(string Output, TclRpcClient? Client);

    private async Task<ChannelResult> ExecuteOnceAsync(OcdConfig cfg, string command, bool startIfNeeded, CancellationToken ct, string op)
    {
        // 通道串行（P1-5）：同一时刻只允许一条命令占用 TCL-RPC 连接——业务命令与心跳互斥。
        await _channelGate.WaitAsync(ct).ConfigureAwait(false);
        TclRpcClient? client = null;
        try
        {
            client = await AcquireAsync(cfg, startIfNeeded, ct).ConfigureAwait(false);
            _traffic.Record(op, "cmd", command, Endpoint(cfg));
            var output = await client.CommandAsync(command, ct).ConfigureAwait(false);
            Volatile.Write(ref _lastChannelActivityTicks, Environment.TickCount64);
            _traffic.Record(op, "rsp", output, Endpoint(cfg));
            return new ChannelResult(output, client);
        }
        catch
        {
            // 这里必须作废，而不是留给 ExecuteAsync 的 catch 去做：异常从本方法穿出时
            // ExecuteAsync 手里的 ChannelResult 还是 default（Client 为 null），那条路径
            // 只会走无参 Invalidate()，而 _client 从未被置空过——等于什么都没清。
            // 「这条连接已经不可信」的判据也只有这里知道：一次没走完的往返（含调用方取消）
            // 会在 TclRpcClient 上标脏，脏连接的应答位置未知，必须整条丢弃。
            if (client is not null)
            {
                InvalidateClient(client);
            }
            else if (ct.IsCancellationRequested)
            {
                // 连 AcquireAsync 都没走完就被取消：连接可能是好的，也可能是半建状态。
                // 取消是罕见路径，宁可多断一次也不要留一条状态不明的连接。
                Invalidate();
            }
            throw;
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
    /// 定向失效（v0.4.31）：仅当 <paramref name="current"/> 仍是 <paramref name="failed"/>
    /// 时清空并 Dispose；期间被别的调用换上的新连接原样保留（返回 false，什么都不动）。
    /// 纯 CompareExchange 语义，internal 供单测。
    /// </summary>
    internal static bool DropIfCurrent(ref TclRpcClient? current, TclRpcClient failed)
    {
        if (!ReferenceEquals(Interlocked.CompareExchange(ref current, null, failed), failed))
            return false;
        failed.Dispose();
        return true;
    }

    private void InvalidateClient(TclRpcClient failed) => DropIfCurrent(ref _client, failed);

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

    // ── P1-4：进度泵的行源（launcher 环形缓冲差分读；仅自拉后端有捕获）──

    /// <summary>当前是否持有自拉 openocd 的输出捕获（外部监听时环形缓冲是陈旧数据）。</summary>
    public bool CapturesProcessOutput => _launcher.CapturesProcessOutput;

    /// <summary>累计抽干的行数（进度泵启动时取一次作游标基准）。</summary>
    public long TotalLinesAppended => _launcher.TotalLinesAppended;

    /// <summary>取自游标以来的新行（差分读），游标推进到当前总量。</summary>
    public IReadOnlyList<string> TakeNewLines(ref long cursor) => _launcher.TakeNewLines(ref cursor);

    /// <summary>
    /// 断开后端（v0.4.6 的 disconnect 通道）：丢连接 → 杀 ocdup **自己拉起**的 openocd
    /// （外部实例杀不动，stop 是 no-op）→ 等端口释放。**不重新拉起**——探针交还给别的
    /// 软件用；调 connect 随时拉回来。
    /// 幂等：本来就没在跑也回可读文案，不抛。占着 _connectGate：断开不能和一次正在
    /// 进行的 Acquire 交叉（否则可能刚断开又被顺手拉起来）。
    /// </summary>
    public async Task<string> StopAsync(CancellationToken ct = default, string? haltedNote = null)
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

            var message = StopReport(wasUp,
                await OpenOcdLauncher.ProbePortAsync(cfg.Host, cfg.Port, 300, ct).ConfigureAwait(false),
                $"{cfg.Host}:{cfg.Port}", haltedNote);
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

    /// <summary>
    /// P0-3（v0.4.28）：停机前的 targets 查询发现目标 halted 时附加的提示——halt 后断开，
    /// halted 的核会一直停在 halted（没人会 resume），下一个探针使用者面对的就是一块「假死」的板子。
    /// </summary>
    internal const string HaltedTargetNote =
        "\nnote: the targets query just before stopping reported the target halted - a halted core " +
        "stays halted after the backend stops (nothing will resume it). If the board must keep running, " +
        "resume or 'reset run' BEFORE disconnecting.";

    /// <summary>
    /// disconnect 结论组装（纯函数，可单测）：仅在「真的停掉了自拉后端」时附带 halted 提示——
    /// 外部实例杀不动 / 本来就没跑两种结论没停任何东西，附提示反而误导。
    /// </summary>
    internal static string StopReport(bool wasUp, bool stillUp, string endpoint, string? haltedNote)
    {
        var verdict = StopVerdict(wasUp, stillUp, endpoint);
        return wasUp && !stillUp && haltedNote is not null ? verdict + haltedNote : verdict;
    }

    /// <summary>
    /// P0-3 残余边界的探测：停机前 best-effort 查一次 targets，目标 halted 就带回
    /// <see cref="HaltedTargetNote"/>。只读、短超时、任何失败一律 null——绝不拖垮 disconnect 本身。
    /// op 用 "disconnect"（不在断链重放白名单：通道失败即抛，正好在这里被吞）。
    /// 注意：必须在 StopAsync **之外**调用——StopAsync 持 _connectGate，而 ExecuteAsync→AcquireAsync
    /// 要同一把闸，持锁内调 = 死锁。
    /// </summary>
    public async Task<string?> ProbeHaltedNoteAsync(OcdConfig cfg, CancellationToken ct = default)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(1500));
            var targets = await ExecuteAsync(cfg, "targets", startIfNeeded: false, timeout.Token, op: OpNames.Disconnect)
                .ConfigureAwait(false);
            return Parsing.AnyTargetHalted(targets) ? HaltedTargetNote : null;
        }
        catch (Exception)
        {
            return null; // best-effort：通道不在 / 超时 / 输出解析不出 → 当「没有 halted 信息」
        }
    }

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
            var client = Volatile.Read(ref _client);
            if (client is null) return;
            try
            {
                var (stillConnected, stillIdle) = ChannelState;
                if (!ShouldProbe(stillConnected, stillIdle)) return;

                await client.CommandAsync("version", CancellationToken.None).ConfigureAwait(false);
                Volatile.Write(ref _lastChannelActivityTicks, Environment.TickCount64);
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or TimeoutException)
            {
                InvalidateClient(client); // 定向失效：只丢探测失败的那条，不碰别的调用新建的连接
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
