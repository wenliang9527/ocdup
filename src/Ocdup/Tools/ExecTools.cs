using System.ComponentModel;
using ModelContextProtocol.Server;
using Ocdup.Backend;
using Ocdup.Config;

namespace Ocdup.Tools;

/// <summary>
/// 执行控制（设计文档后置里程碑 M-exec）：halt/resume/reset。
/// 主要用途：read_memory/write_memory 在目标运行时可能失败——先 halt 再操作，完事 resume。
/// 这些操作改变目标执行状态，与写操作同级设防（read_only 总闸 + allow_exec 独立开关）。
/// </summary>
[McpServerToolType]
public static class ExecTools
{
    [McpServerTool(Name = "halt")]
    [Description(
        "Halt the target CPU so memory/flash can be read or written reliably (reads on a running target often fail). " +
        "timeoutMs = how long to wait for the core to actually halt (default 500). " +
        "Gated by read_only and allow_exec. Remember to resume afterwards, or use reset mode='run' to restart firmware.")]
    public static async Task<string> Halt(
        [Description("Wait for the halt, milliseconds (default 500).")] int timeoutMs = 500,
        CancellationToken ct = default)
    {
        var cfg = ConfigStore.Instance.Current;

        if (PermissionGate.CheckExec(cfg, "halt") is { } blocked)
            return blocked;
        if (timeoutMs is < 0 or > 30_000)
            return "ERROR: timeoutMs must be between 0 and 30000.";

        var command = timeoutMs > 0 ? $"halt {timeoutMs}" : "halt";
        try
        {
            var output = (await BackendSession.Instance.ExecuteAsync(cfg, command, startIfNeeded: true, ct, op: "halt")).Trim();
            var regs = await CoreRegsReportAsync(cfg, ct).ConfigureAwait(false);
            return $"halt issued ({command}).\n" +
                   (output.Length == 0 ? "(no output - check the target state with status)" : output) +
                   (regs.Length == 0 ? "" : $"\n{regs}") +
                   "\nnext: read/write now works reliably; call resume when done.";
        }
        catch (BackendException ex)
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// halt 后 best-effort 回读核心寄存器（PC/LR/SP/xPSR）："程序跑飞在哪"一步定位——
    /// PC 看现场、LR 看调用来源、xPSR 看异常态。2 秒总预算 + 全吞异常：目标不应答或
    /// 驱动不支持时只少一行，绝不把成功的 halt 判失败（与 connect 自动识芯同一纪律）。
    /// </summary>
    internal static async Task<string> CoreRegsReportAsync(OcdConfig cfg, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));

        var parts = new List<string>(4);
        // 寄存器名大小写敏感（atopenocd 实测：xPSR 大写才认,xpsr 报 not found）
        foreach (var reg in new[] { "pc", "lr", "sp", "xPSR" })
        {
            try
            {
                var output = await BackendSession.Instance
                    .ExecuteAsync(cfg, $"reg {reg}", startIfNeeded: false, timeout.Token, op: "halt").ConfigureAwait(false);
                if (Parsing.ParseRegValue(output) is { } value)
                    parts.Add($"{reg}={Parsing.Hex(value)}");
            }
            catch (Exception ex) when (ex is OperationCanceledException or BackendException)
            {
                break; // 预算耗尽或通道异常:拿多少算多少
            }
        }

        return parts.Count == 0 ? "" : "core registers: " + string.Join("  ", parts);
    }

    [McpServerTool(Name = "resume")]
    [Description(
        "Resume the target CPU after a halt. Gated by read_only and allow_exec. " +
        "address (optional): resume from a specific address instead of the current PC.")]
    public static async Task<string> Resume(
        [Description("Address to resume from, hex like '0x08000100'; omit = current PC.")] string? address = null,
        CancellationToken ct = default)
    {
        var cfg = ConfigStore.Instance.Current;

        if (PermissionGate.CheckExec(cfg, "resume") is { } blocked)
            return blocked;

        string command;
        if (string.IsNullOrWhiteSpace(address))
        {
            command = "resume";
        }
        else
        {
            if (!Parsing.TryParseHex(address, out var addr))
                return $"ERROR: address '{address}' is not a hex value. Use a hex string like '0x08000100'.";
            command = $"resume {Parsing.Hex(addr)}";
        }

        try
        {
            var output = (await BackendSession.Instance.ExecuteAsync(cfg, command, startIfNeeded: true, ct, op: "resume")).Trim();
            return $"resume issued ({command}).\n" +
                   (output.Length == 0 ? "(no output - target is running)" : output);
        }
        catch (BackendException ex)
        {
            return ex.Message;
        }
    }

    [McpServerTool(Name = "reset")]
    [Description(
        "Reset the target. mode: 'run' (default - reset and let firmware start), 'halt' (reset and halt, ready for flash/debug), " +
        "'init' (reset and run init scripts). Restarting firmware loses volatile state - gated by read_only and allow_exec.")]
    public static async Task<string> Reset(
        [Description("Reset mode: run | halt | init (default run).")] string mode = "run",
        CancellationToken ct = default)
    {
        var cfg = ConfigStore.Instance.Current;

        if (PermissionGate.CheckExec(cfg, "reset") is { } blocked)
            return blocked;

        var m = (mode ?? "run").Trim().ToLowerInvariant();
        if (m is not ("run" or "halt" or "init"))
            return $"ERROR: mode '{mode}' is not supported. Use run, halt or init.";

        try
        {
            var output = (await BackendSession.Instance.ExecuteAsync(cfg, $"reset {m}", startIfNeeded: true, ct, op: "reset")).Trim();
            var regs = m == "halt" ? await CoreRegsReportAsync(cfg, ct).ConfigureAwait(false) : "";
            return $"reset {m} issued.\n" +
                   (output.Length == 0 ? "(no output)" : output) +
                   (regs.Length == 0 ? "" : $"\n{regs}");
        }
        catch (BackendException ex)
        {
            return ex.Message;
        }
    }
}
