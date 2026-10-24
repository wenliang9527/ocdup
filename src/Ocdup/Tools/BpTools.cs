using System.ComponentModel;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;
using Ocdup.Backend;
using Ocdup.Config;

namespace Ocdup.Tools;

/// <summary>
/// 断点 / 观察点（M-bp，设计文档第 12 节后置里程碑；v0.4.30 落地第一层）：
/// OpenOCD 的 <c>bp</c> / <c>rbp</c> / <c>wp</c> / <c>rwp</c> 命令按名封装。
/// 五个工具都只做「参数校验 → 组命令 → 透传输出」，不复制 OpenOCD 的语义；
/// 断点命中后的现场（PC/LR/SP/xPSR）由 halt 工具同一套 <see cref="ExecTools"/> 回读。
///
/// 权限定性：断点/观察点改变目标的执行行为（命中即停、写即停），与 halt/resume 同级——
/// set/remove 四件过 read_only 总闸 + allow_exec；list_breakpoints 是纯读（<c>bp</c> 无参即列），
/// 与 flash_info 同款不设闸。裸通道侧（run_command）同名命令在 RawCommandPolicy 归 ExecControl，
/// 两条通道同一把闸（v0.4.30 补的分类，此前 bp/wp 是 Passthrough 漏洞）。
///
/// 真机边界（2026-10-19，AT32F403AVGT7 + atopenocd-py32 0.11.0+dev 实测）：
/// ① **length 恒显式下发**——该 fork 的签名是 <c>bp [&lt;address&gt; [&lt;asid&gt;] &lt;length&gt; ['hw'|'hw_ctx']]]</c>
///    （length 必填），省了它后端只回 usage 文本、什么都设不到；<c>wp</c> 同样 length 必填
///    （省了报 <c>Invalid command argument</c>）。上游把 length 设为可选，恒下发两边都成立。
/// ② **运行态只有硬件断点能设**——软断点（flash 区）与观察点都要求目标 halted
///    （<c>can't add breakpoint: target running</c> / <c>can't add write watchpoint ... target running</c>），
///    先 halt（allow_exec）再设、完事 resume；硬件断点（hw=true）运行态直接可设。
/// ③ **观察点没有统一的 all 删除形态**——<c>rbp all</c> 两边都认，但 fork 的 <c>rwp</c> 只认
///    address（<c>rwp all</c> → <c>Invalid command argument</c>），全清走「列出逐个删」。
/// ④ 断点/观察点数量受芯片硬件限制（Cortex-M 常见 4-8 个），超出以后端报错为准。
/// </summary>
[McpServerToolType]
public static class BpTools
{
    /// <summary>length 参数的实用上限（字节）：硬件观察点常见 1/2/4，给足余量；后端会按能力拒绝。</summary>
    internal const int MaxLengthBytes = 65536;

    /// <summary>断点长度默认值（字节）：ARM Thumb 指令宽度 2。恒显式下发——见类注释真机边界①。</summary>
    internal const int DefaultBreakpointLength = 2;

    /// <summary>观察点长度默认值（字节）：4（自然字对齐）。恒显式下发——见类注释真机边界①。</summary>
    internal const int DefaultWatchpointLength = 4;

    [McpServerTool(Name = "add_breakpoint")]
    [Description(
        "Set a breakpoint at an address (backend command 'bp <address> <length> [hw]'). " +
        "REAL-MACHINE BOUNDARY (atopenocd-py32, verified 2026-10-19): SOFTWARE breakpoints (flash region) need a HALTED target - " +
        "with the firmware running the backend answers \"can't add breakpoint: target running\"; halt first (allow_exec), set, then resume. " +
        "HARDWARE breakpoints (hw=true) can be set while the target runs. " +
        "When the breakpoint hits, the core halts - read the scene with read_memory / read_variable, then resume. " +
        "Gated by read_only and allow_exec (a breakpoint changes how the target executes, same as halt/resume).")]
    public static async Task<string> AddBreakpoint(
        [Description("Breakpoint address, hex string like '0x08000100'.")] string address,
        [Description("Length in bytes (default 2, the ARM Thumb instruction width; always sent - some backends require it).")] int length = DefaultBreakpointLength,
        [Description("Force a hardware breakpoint (default false).")] bool hw = false,
        CancellationToken ct = default)
    {
        var cfg = ConfigStore.Instance.Current;

        if (PermissionGate.CheckExec(cfg, "add_breakpoint") is { } blocked)
            return blocked;

        if (!Parsing.TryParseHex(address, out var addr))
            return $"ERROR: address '{address}' is not a hex value. Use a hex string like '0x08000100'.";
        if (length is < 1 or > MaxLengthBytes)
            return $"ERROR: length must be between 1 and {MaxLengthBytes} bytes.";

        var command = BreakpointCommand(addr, length, hw);
        return await RunAsync(cfg, command, ct, op: "add_breakpoint",
            $"breakpoint set @ {Parsing.Hex(addr)}" + (hw ? " (hardware)" : "")).ConfigureAwait(false);
    }

    [McpServerTool(Name = "remove_breakpoint")]
    [Description(
        "Remove a breakpoint by address (backend command 'rbp <addr>'), or ALL breakpoints when address is omitted ('rbp all'). " +
        "Gated by read_only and allow_exec (same as add_breakpoint).")]
    public static async Task<string> RemoveBreakpoint(
        [Description("Breakpoint address to remove, hex string. Omit to remove all ('rbp all').")] string? address = null,
        CancellationToken ct = default)
    {
        var cfg = ConfigStore.Instance.Current;

        if (PermissionGate.CheckExec(cfg, "remove_breakpoint") is { } blocked)
            return blocked;

        if (string.IsNullOrWhiteSpace(address))
            return await RunAsync(cfg, RemoveBreakpointCommand(null), ct, op: "remove_breakpoint",
                "all breakpoints removed").ConfigureAwait(false);

        if (!Parsing.TryParseHex(address, out var addr))
            return $"ERROR: address '{address}' is not a hex value. Use a hex string like '0x08000100'.";

        return await RunAsync(cfg, RemoveBreakpointCommand(Parsing.Hex(addr)), ct, op: "remove_breakpoint",
            $"breakpoint removed @ {Parsing.Hex(addr)}").ConfigureAwait(false);
    }

    [McpServerTool(Name = "list_breakpoints")]
    [Description(
        "List the breakpoints the backend knows (backend command 'bp' with no arguments). Read-only: no permission gate. " +
        "Call it after add_breakpoint to confirm what is armed, or to find the number a hit report refers to. " +
        "Output form is backend-specific (atopenocd: 'Breakpoint(IVA): 0x08000400, 0x2, 0').")]
    public static async Task<string> ListBreakpoints(CancellationToken ct = default)
    {
        var cfg = ConfigStore.Instance.Current;
        return await RunAsync(cfg, "bp", ct, op: "list_breakpoints", "breakpoints listed").ConfigureAwait(false);
    }

    [McpServerTool(Name = "add_watchpoint")]
    [Description(
        "Set a data watchpoint: halt when the target accesses a memory range (backend command 'wp <address> <length> <access> [value]'). " +
        "REAL-MACHINE BOUNDARY (atopenocd-py32, verified 2026-10-19): watchpoints need a HALTED target " +
        "(\"can't add write watchpoint ... target running\") - halt first (allow_exec), set, then resume. " +
        "Hardware watchpoints are a scarce resource (typically 4-8 per Cortex-M) - the backend reports when they run out. " +
        "Gated by read_only and allow_exec (a watchpoint changes how the target executes, same as halt/resume).")]
    public static async Task<string> AddWatchpoint(
        [Description("Watched address, hex string like '0x20000000'.")] string address,
        [Description("Length in bytes (default 4; always sent - some backends require it).")] int length = DefaultWatchpointLength,
        [Description("Access to watch: 'r' (read), 'w' (write, default) or 'a' (either).")] string access = "w",
        [Description("Optional hex value the access must match (backend semantics).")] string? value = null,
        CancellationToken ct = default)
    {
        var cfg = ConfigStore.Instance.Current;

        if (PermissionGate.CheckExec(cfg, "add_watchpoint") is { } blocked)
            return blocked;

        if (!Parsing.TryParseHex(address, out var addr))
            return $"ERROR: address '{address}' is not a hex value. Use a hex string like '0x20000000'.";
        if (length is < 1 or > MaxLengthBytes)
            return $"ERROR: length must be between 1 and {MaxLengthBytes} bytes.";
        if (NormalizeAccess(access) is not { } normalizedAccess)
            return $"ERROR: access '{access}' is not supported. Use 'r' (read), 'w' (write) or 'a' (either).";

        long? matchValue = null;
        if (!string.IsNullOrWhiteSpace(value))
        {
            if (!Parsing.TryParseHex(value, out var parsed))
                return $"ERROR: value '{value}' is not a hex value. Use forms like '0x2A' or '2A'.";
            matchValue = parsed;
        }

        var command = WatchpointCommand(addr, length, normalizedAccess, matchValue);
        return await RunAsync(cfg, command, ct, op: "add_watchpoint",
            $"watchpoint set @ {Parsing.Hex(addr)} ({normalizedAccess})").ConfigureAwait(false);
    }

    [McpServerTool(Name = "remove_watchpoint")]
    [Description(
        "Remove a watchpoint by address (backend command 'rwp <addr>'). Without an address, remove ALL watchpoints - " +
        "the backend has no universal 'rwp all' form (atopenocd-py32 rejects it: 'Invalid command argument', verified 2026-10-19), " +
        "so this lists the armed watchpoints first and removes them one by one. " +
        "Gated by read_only and allow_exec (same as add_watchpoint).")]
    public static async Task<string> RemoveWatchpoint(
        [Description("Watchpoint address to remove, hex string. Omit to remove all (list-then-remove).")] string? address = null,
        CancellationToken ct = default)
    {
        var cfg = ConfigStore.Instance.Current;

        if (PermissionGate.CheckExec(cfg, "remove_watchpoint") is { } blocked)
            return blocked;

        if (string.IsNullOrWhiteSpace(address))
            return await RemoveAllWatchpointsAsync(cfg, ct).ConfigureAwait(false);

        if (!Parsing.TryParseHex(address, out var addr))
            return $"ERROR: address '{address}' is not a hex value. Use a hex string like '0x20000000'.";

        return await RunAsync(cfg, RemoveWatchpointCommand(Parsing.Hex(addr)), ct, op: "remove_watchpoint",
            $"watchpoint removed @ {Parsing.Hex(addr)}").ConfigureAwait(false);
    }

    /// <summary>
    /// 全清观察点：先列（<c>wp</c>）再逐个按地址删——后端没有统一的 all 形态
    /// （真机边界③）。解析不出地址时如实说「没找到可删的」，不猜。
    /// </summary>
    private static async Task<string> RemoveAllWatchpointsAsync(OcdConfig cfg, CancellationToken ct)
    {
        string listing;
        try
        {
            listing = (await BackendSession.Instance.ExecuteAsync(cfg, "wp", startIfNeeded: true, ct, op: "remove_watchpoint")).Trim();
        }
        catch (BackendException ex)
        {
            return ex.Message;
        }

        var addresses = ParseWatchpointAddresses(listing);
        if (addresses.Count == 0)
            return "no watchpoints to remove (the backend listed none, or the listing could not be parsed).\n" +
                   (listing.Length == 0 ? "(the watchpoint list was empty)" : listing);

        var removed = new List<string>();
        foreach (var addr in addresses)
        {
            var result = await RunAsync(cfg, RemoveWatchpointCommand(addr), ct, op: "remove_watchpoint",
                $"watchpoint removed @ {addr}").ConfigureAwait(false);
            if (!result.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase))
                removed.Add(addr);
        }

        return $"removed {removed.Count} watchpoint(s): {string.Join(", ", removed)}" +
               (removed.Count == addresses.Count ? "" : $"\nnote: {addresses.Count - removed.Count} removal(s) reported an error - see the lines above.");
    }

    /// <summary>执行并回报（与 halt/resume 同一形态：命令回显 + 透传输出，空输出给一句说明）。
    /// op 是发起调用的工具名（流量镜像的 tool 字段 + 断链重放白名单的判定键——v0.4.31 起
    /// 五个工具各传各的，此前恒 "bp"，list_breakpoints 的重放白名单条目因此从未生效）。</summary>
    private static async Task<string> RunAsync(OcdConfig cfg, string command, CancellationToken ct, string op, string what)
    {
        try
        {
            var output = (await BackendSession.Instance.ExecuteAsync(cfg, command, startIfNeeded: true, ct, op: op)).Trim();
            return $"{what} ({command}).\n" +
                   (output.Length == 0 ? "(no output - the backend accepted it silently)" : output);
        }
        catch (BackendException ex)
        {
            return ex.Message;
        }
    }

    // ── 命令构造（纯函数，可单测；真机核实的签名见各类注释）──

    /// <summary>bp <addr> <length> [hw]——length 恒下发（真机边界①）。</summary>
    internal static string BreakpointCommand(long address, int length, bool hw)
    {
        var sb = new System.Text.StringBuilder("bp ").Append(Parsing.Hex(address)).Append(' ').Append(length);
        if (hw) sb.Append(" hw");
        return sb.ToString();
    }

    /// <summary>remove：带地址移除单个，null = 全部（OpenOCD 的 'rbp all' 两边都认）。</summary>
    internal static string RemoveBreakpointCommand(string? address) =>
        string.IsNullOrWhiteSpace(address) ? "rbp all" : $"rbp {address}";

    /// <summary>wp <addr> <length> <access> [value]：length 与 access 恒显式（真机边界① + 不猜后端默认）。</summary>
    internal static string WatchpointCommand(long address, int length, string access, long? value)
    {
        var sb = new System.Text.StringBuilder("wp ").Append(Parsing.Hex(address)).Append(' ').Append(length)
            .Append(' ').Append(access);
        if (value is { } v) sb.Append(' ').Append(Parsing.HexValue(v, 32));
        return sb.ToString();
    }

    /// <summary>rwp <addr>——只认地址（真机边界③：没有 all 形态，全清走列表逐个删）。</summary>
    internal static string RemoveWatchpointCommand(string address) => $"rwp {address}";

    /// <summary>access 归一化（纯函数）：r/w/a 大小写不敏感；其余 null（调用方给可操作文案）。</summary>
    internal static string? NormalizeAccess(string? access)
    {
        var t = (access ?? "").Trim().ToLowerInvariant();
        return t is "r" or "w" or "a" ? t : null;
    }

    /// <summary>
    /// 从 <c>wp</c> 列表输出解析观察点地址（纯函数）。真机形态（atopenocd-py32）：
    /// <c>address: 0x20000000, len: 0x00000004, r/w/a: 1, value: 0x00000000, mask: 0xffffffff</c>；
    /// 上游形态不同也能被同一条正则覆盖（都带 "address: 0x…" 前缀）。解析不出 → 空列表，
    /// 调用方如实报告，不猜。
    /// </summary>
    internal static IReadOnlyList<string> ParseWatchpointAddresses(string? output)
    {
        var found = new List<string>();
        if (string.IsNullOrWhiteSpace(output)) return found;
        foreach (Match m in WatchpointAddressRegex.Matches(output))
        {
            var value = Convert.ToInt64(m.Groups[1].Value, 16);
            found.Add(Parsing.Hex(value));
        }
        return found;
    }

    private static readonly Regex WatchpointAddressRegex =
        new(@"address:\s*0x([0-9a-fA-F]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
}
