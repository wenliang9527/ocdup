namespace Ocdup.Config;

/// <summary>
/// run_command 原始命令的内容分类闸（4.2-4 逃生舱的补充定案）：让 raw TCL 通道面对
/// 与专用工具相同的权限约束。能映射到既有 PermissionGate 检查的走同一条闸（与专用工具
/// 同权）；带路径/体积维度的写（program / load_image）与 PC 侧落盘（dump_image）、
/// 后端控制（shutdown/exit）在 raw 通道上无法可靠校验，直接 BLOCKED 并指引专用工具。
/// 定位是纵深防御而非唯一边界：分类清单之外的命令仍会透传，通道本身的闸是
/// allow_raw_command（默认 false，见 OcdConfig）。
/// v0.4.30 起断点/观察点命令（bp/rbp/wp/rwp）也归 ExecControl 类——它们改变目标的执行
/// 行为（命中即停），与 halt/resume 同级，不能留在 Passthrough 里绕过 allow_exec。
/// </summary>
public static class RawCommandPolicy
{
    /// <summary>单段命令的分类（private：单测经 Segment/Evaluate 断言行为，不绑实现细节）。</summary>
    private enum Class
    {
        Passthrough,       // 只读/未知命令：透传（allow_raw_command 已是显式授权）
        FlashFileWrite,    // program / flash write_image / load_image：带文件，路径与大小闸无法还原
        FlashErase,        // flash erase_sector / erase_address（erase_check 是只读，不算）
        FlashNonFileWrite, // flash fill* / flash protect：无文件实参，走 allow_flash 闸
        MemoryWrite,       // mww / mwh / mwb：走 allow_memory_write 闸
        ExecControl,       // reset / halt / resume / step：走 allow_exec 闸
        PcDump,            // dump_image：PC 侧落盘，重定向 dump_memory
        BackendControl,    // shutdown / exit：重定向 disconnect
    }

    /// <summary>
    /// 评估整条命令：按 ';' 切段逐段分类，任一段被拦即返回 BLOCKED 文案
    /// （6.3 约定：拦截必须带「怎么做」的指引）；返回 null = 全部放行。
    /// </summary>
    public static string? Evaluate(OcdConfig cfg, string command)
    {
        foreach (var segment in Segment(command))
        {
            var head = FirstToken(segment);
            string? blocked = Classify(segment) switch
            {
                Class.FlashFileWrite => $"BLOCKED: '{head}' writes flash through the raw channel, where " +
                    "flash_allowed_paths / flash_max_bytes cannot be enforced. Use the dedicated tool " +
                    "mcp__ocdup__flash_write (and mcp__ocdup__verify_image to check what landed). See show_config.",
                Class.FlashErase => PermissionGate.CheckFlashErase(cfg),
                Class.FlashNonFileWrite => PermissionGate.CheckFlashOp(cfg, OpLabel(segment)),
                Class.MemoryWrite => PermissionGate.CheckMemoryWrite(cfg, $"{head} (raw memory write)"),
                Class.ExecControl => PermissionGate.CheckExec(cfg, $"{head} (raw exec control)"),
                Class.PcDump => "BLOCKED: 'dump_image' writes a file on this PC through the raw channel, where " +
                    "dump_allowed_paths / dump_max_bytes cannot be enforced (its relative paths also resolve " +
                    "against openocd's cwd, not ocd.json). Use the dedicated tool mcp__ocdup__dump_memory. See show_config.",
                Class.BackendControl => $"BLOCKED: '{head}' stops the OpenOCD backend through the raw channel. " +
                    "Use the dedicated tool mcp__ocdup__disconnect - it is gated by read_only, idempotent, and " +
                    "reports who owns the listener. See show_config.",
                _ => null,
            };
            if (blocked is not null) return blocked;
        }

        return null;
    }

    /// <summary>
    /// 按 ';' 切段（TCL 单行内的合法语句分隔符；换行已在上游挡掉）。
    /// 引号/花括号里的 ';' 会被多切——分类方向保守：宁多拦、不漏拦。
    /// </summary>
    public static IReadOnlyList<string> Segment(string command) =>
        command.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private static Class Classify(string segment)
    {
        var tokens = Tokenize(segment);
        if (tokens.Length == 0) return Class.Passthrough;
        if (tokens[0].StartsWith('#')) return Class.Passthrough; // TCL 注释段

        var head = tokens[0].ToLowerInvariant();
        if (head == "flash" && tokens.Length > 1)
        {
            var sub = tokens[1].ToLowerInvariant();
            if (sub == "write_image") return Class.FlashFileWrite;
            if (sub is "erase_sector" or "erase_address") return Class.FlashErase;
            if (sub.StartsWith("fill") || sub == "protect") return Class.FlashNonFileWrite;
            return Class.Passthrough; // banks / probe / info / erase_check …只读
        }

        return head switch
        {
            "program" or "load_image" => Class.FlashFileWrite,
            "mww" or "mwh" or "mwb" => Class.MemoryWrite,
            "reset" or "halt" or "resume" or "step" => Class.ExecControl,
            // v0.4.30：断点/观察点同属执行控制——命中即停，与 halt/resume 同一条 allow_exec 闸
            // （此前它们是 Passthrough：allow_raw_command 一开就能绕过硬闸）。
            "bp" or "rbp" or "wp" or "rwp" => Class.ExecControl,
            "dump_image" => Class.PcDump,
            "shutdown" or "exit" => Class.BackendControl,
            _ => Class.Passthrough,
        };
    }

    /// <summary>闸门文案里的操作名：flash 子命令取两段（"flash fillw"），其余取首词。</summary>
    private static string OpLabel(string segment)
    {
        var tokens = Tokenize(segment);
        if (tokens.Length == 0) return "";
        var head = tokens[0].ToLowerInvariant();
        return head == "flash" && tokens.Length > 1
            ? $"flash {tokens[1].ToLowerInvariant()}"
            : head;
    }

    private static string FirstToken(string segment)
    {
        var tokens = Tokenize(segment);
        return tokens.Length > 0 ? tokens[0].ToLowerInvariant() : "";
    }

    private static string[] Tokenize(string segment) =>
        segment.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
}
