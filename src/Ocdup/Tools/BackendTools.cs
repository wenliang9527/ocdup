using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using Ocdup.Backend;
using Ocdup.Config;
using Ocdup.Launcher;

namespace Ocdup.Tools;

/// <summary>后端管理与状态（设计文档第 5 节的 connect / status）。</summary>
[McpServerToolType]
public static class BackendTools
{
    [McpServerTool(Name = "status")]
    [Description("Report whether an OpenOCD backend is listening on the configured command port (default 127.0.0.1:6666) and, when it is, its version and target list. Read-only: starts nothing and does not disturb the target.")]
    public static async Task<string> Status(CancellationToken ct = default)
    {
        var cfg = ConfigStore.Instance.Current;

        if (!await BackendSession.IsBackendUpAsync(cfg, ct))
            return $"OpenOCD: not running - nothing is listening on {cfg.Host}:{cfg.Port}. " +
                   "Call connect to start the backend from interface_cfg/target_cfg.";

        try
        {
            var version = (await BackendSession.Instance.ExecuteAsync(cfg, "version", startIfNeeded: false, ct, op: "status")).Trim();
            var targets = (await BackendSession.Instance.ExecuteAsync(cfg, "targets", startIfNeeded: false, ct, op: "status")).Trim();

            var sb = new StringBuilder();
            sb.AppendLine($"OpenOCD: running on {cfg.Host}:{cfg.Port}");
            sb.AppendLine($"version: {(version.Length == 0 ? "(no version reported)" : version)}");
            if (BackendSession.Instance.LastBackendNote is { Length: > 0 } note)
                sb.AppendLine($"backend: {note}");
            sb.AppendLine("targets:");
            sb.Append(FormatTargets(targets));
            return sb.ToString();
        }
        catch (BackendException ex)
        {
            return $"OpenOCD: {cfg.Host}:{cfg.Port} has a listener, but the TCL-RPC channel failed: {ex.Message}";
        }
    }

    /// <summary>
    /// 断开后端（v0.4.6）：停掉 ocdup **自己拉起**的 openocd 并释放探针，MCP 门面不断。
    /// 用途：探针是独占资源，DSH 不用时交给别的 agent 软件（Claude Code / ZCode …）用；
    /// 之后任何硬件工具都会失败，直到 connect（或任何一次会自动拉起的调用）。
    /// read_only 会话拦截——停机属于改变后端状态（与 set_target 同一先例）。
    /// 面板的「断开」按钮走的是同一条通道（Host 侧 ctx.tools.execute）。
    /// </summary>
    [McpServerTool(Name = "disconnect")]
    [Description(
        "Stop the OpenOCD backend that ocdup itself started and release the probe, without dropping the MCP connection " +
        "(this facade stays alive; every hardware tool fails until connect). Use it when another agent program needs the " +
        "probe - the command port is a shared exclusive resource. Idempotent: disconnecting an already-stopped backend is " +
        "a no-op with an explanation. A listener started outside ocdup is reported, not stopped. Read-only sessions are blocked.")]
    public static async Task<string> Disconnect(CancellationToken ct = default)
    {
        var cfg = ConfigStore.Instance.Current;

        // 停机 = 改变后端状态：过 read_only 总闸（与 set_target 同一先例）
        if (cfg.ReadOnly)
            return "BLOCKED: 'disconnect' stops the OpenOCD backend, which read_only forbids. " +
                   "To release the probe, set \"read_only\": false in ocd.json (top level or inside the permissions block), " +
                   "or start ocdup with OCD_READONLY unset. See show_config.";

        return await BackendSession.Instance.StopAsync(ct).ConfigureAwait(false);
    }

    /// <summary>targets 输出优先用一行式摘要（省 token）；解析失败回退原始表格。</summary>
    private static string FormatTargets(string rawTargets) =>
        rawTargets.Length == 0
            ? "(none - is target_cfg set in ocd.json?)"
            : Parsing.TrySummarizeTargets(rawTargets) ?? rawTargets;

    [McpServerTool(Name = "connect")]
    [Description("Connect to the OpenOCD backend on the configured command port, starting openocd from ocd.json (interface_cfg/target_cfg) when nothing is listening yet. Returns the OpenOCD version, the target state, a best-effort chip identity (CPUID + DEV_ID reads with a short timeout, so a silent target never blocks connecting), the flash layout (best-effort, skipped when the identify reports a target mismatch), and a 'target mismatch' hint when the identified chip family is in \"target_presets\" but the running target_cfg is a different chip (fix it with set_target). Call this before flash_write/read_memory in a fresh session.")]
    public static async Task<string> Connect(CancellationToken ct = default)
    {
        var cfg = ConfigStore.Instance.Current;

        try
        {
            var version = (await BackendSession.Instance.ExecuteAsync(cfg, "version", startIfNeeded: true, ct, op: "connect")).Trim();
            var targets = (await BackendSession.Instance.ExecuteAsync(cfg, "targets", startIfNeeded: false, ct, op: "connect")).Trim();

            var sb = new StringBuilder();
            sb.AppendLine($"connected to OpenOCD on {cfg.Host}:{cfg.Port}");
            sb.AppendLine($"version: {(version.Length == 0 ? "(no version reported)" : version)}");
            if (BackendSession.Instance.LastBackendNote is { Length: > 0 } note)
                sb.AppendLine($"backend: {note}");
            sb.AppendLine("targets:");
            sb.Append(FormatTargets(targets));
            sb.AppendLine();
            var identify = await AutoIdentifyAsync(cfg, ct).ConfigureAwait(false);
            sb.Append(identify);
            sb.AppendLine();
            // v0.4.0:连接后顺带探一次 flash 布局(target 配错时跳过并说明)——
            // 面板的 Flash 段不用再等 AI 显式调 flash_info。镜像 tool=flash_info,同格式。
            sb.Append(await AutoFlashInfoAsync(cfg, ct,
                identify.Contains("target mismatch", StringComparison.OrdinalIgnoreCase)).ConfigureAwait(false));
            return sb.ToString();
        }
        catch (BackendException ex)
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// connect 成功后的 best-effort 芯片识别（插上就知道对面是谁）。
    /// 短超时（3s）+ 全吞异常：目标不应答时只补一行说明，绝不拖垮 connect、
    /// 不把连接判失败——通道层超时会顺带 invalidate 连接，后续工具调用会自动重 Acquire。
    /// </summary>
    private static async Task<string> AutoIdentifyAsync(OcdConfig cfg, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            return await IdentifyTools.TryIdentifyAsync(cfg, "0xE0042000", timeout.Token).ConfigureAwait(false)
                ?? "chip identity: unavailable (the target did not answer - check power/SWD wiring; " +
                   "retry mcp__ocdup__identify_chip once the board answers)";
        }
        catch (Exception ex) when (ex is OperationCanceledException or BackendException)
        {
            return "chip identity: unavailable (the read timed out or the control channel dropped - " +
                   "the connection itself is fine; retry mcp__ocdup__identify_chip later)";
        }
    }

    /// <summary>
    /// connect 成功后的 best-effort flash 布局探测（v0.4.0/v0.4.1）：复用 FlashTools 的
    /// 「逐个 bank probe 后列 banks」——容量自动探测的 target(at32f403axG:FLASH_SIZE=0)
    /// 必须 probe 过才有真实容量，且双 bank 的 bank1 要显式探(实测 probe 0 不带 bank1)。
    /// target 失配时跳过(此时 banks 是空的，提示先切换)。全程 best-effort：任何失败
    /// 只补一行说明，绝不拖垮 connect。
    /// </summary>
    private static async Task<string> AutoFlashInfoAsync(OcdConfig cfg, CancellationToken ct, bool targetMismatch)
    {
        if (targetMismatch)
            return "flash layout: skipped - the identify above reports a target mismatch (the running target_cfg does not " +
                   "match the chip on the board). Switch first - e.g. set_target AT32F403A per the mismatch line - " +
                   "then flash_info will report real banks.";

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            var banks = (await FlashTools.BanksAfterProbeAsync(cfg, timeout.Token).ConfigureAwait(false)).Trim();
            if (banks.Length == 0)
                return "flash layout: the backend reported no flash banks with the current target_cfg. " +
                       "SWD reads (identify / read_memory) still work; flashing needs a target cfg with a flash driver - " +
                       "see \"target_presets\" in ocd.json and set_target (show_config lists what is configured).";
            return "flash layout:\n" + banks;
        }
        catch (Exception ex) when (ex is OperationCanceledException or BackendException)
        {
            var first = ex.Message.Split('\n')[0];
            return $"flash layout: unavailable ({first}) - the connection itself is fine; " +
                   "retry mcp__ocdup__flash_info later.";
        }
    }

    [McpServerTool(Name = "backend_log")]
    [Description(
        "Return recent stdout/stderr lines (Info:/Error: logs) of the OpenOCD process that ocdup itself started - a rolling 200-line buffer. " +
        "Use it to diagnose flash/read failures and target events that never reach the TCL-RPC response text. Read-only. " +
        "Returns an explanation instead of log lines when the listener on the command port was started outside ocdup (its output is not captured).")]
    public static Task<string> BackendLog(int lines = 40, CancellationToken ct = default)
    {
        var capped = Math.Clamp(lines, 1, OpenOcdLauncher.MaxBufferLines);
        var recent = BackendSession.Instance.RecentBackendOutput(capped);
        return Task.FromResult(recent.Length == 0
            ? "no captured backend output: ocdup has not started an OpenOCD process in this session. " +
              "An already-running listener's output goes to its own console, not here. " +
              "Call connect first so ocdup can start (and capture) the backend."
            : $"last {capped} openocd output line(s):\n{recent}");
    }

    [McpServerTool(Name = "run_command")]
    [Description(
        "Raw TCL escape hatch: run an arbitrary OpenOCD/TCL command through the control channel and return its captured output. " +
        "For anything the dedicated tools don't cover (odd adapter commands, one-off introspection). " +
        "Single-line command only. Gated by read_only and allow_raw_command - a BLOCKED answer means the settings forbid it (see show_config).")]
    public static async Task<string> RunCommand(
        [Description("The OpenOCD/TCL command to run, e.g. 'mdb 0x40010800 4' or 'ap0 32 0x00000000'.")] string command,
        CancellationToken ct = default)
    {
        var cfg = ConfigStore.Instance.Current;

        if (PermissionGate.CheckRawCommand(cfg) is { } blocked)
            return blocked;

        if (string.IsNullOrWhiteSpace(command))
            return "ERROR: command must not be empty.";
        if (command.IndexOf('\n') >= 0 || command.IndexOf('\r') >= 0)
            return "ERROR: single-line command only (strip newlines; run each statement separately).";

        try
        {
            var output = (await BackendSession.Instance.ExecuteAsync(cfg, command.Trim(), startIfNeeded: true, ct, op: "run_command")).Trim();
            return $"ran: {command.Trim()}\n" + (output.Length == 0 ? "(no output)" : output);
        }
        catch (BackendException ex)
        {
            return ex.Message;
        }
    }
}
