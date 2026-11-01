namespace Ocdup;

/// <summary>
/// 全部 BackendSession.ExecuteAsync 的 op 值单一真源（v0.4.37，audit F-8）：op 既决定
/// mcp-traffic.jsonl 的 tool 字段归属，也决定断链后能否自动重放（BackendSession.ReplaySafeOps）。
/// 此前 40+ 处调用点手写字符串、白名单靠人肉同步——v0.4.31 前 list_breakpoints 的白名单
/// 条目就因此死过一次。新增工具：这里加一个常量，调用点与白名单都引用它，编译期错不了。
/// </summary>
internal static class OpNames
{
    /// <summary>ExecuteAsync 的缺省 op（无调用方标注时的兜底流量归属）。</summary>
    public const string Tcl = "tcl";

    // ── MCP 工具（有后端交互的那些；纯配置类工具不经 ExecuteAsync，无 op）──
    public const string Connect = "connect";
    public const string Status = "status";
    public const string Disconnect = "disconnect";
    public const string BackendLog = "backend_log";
    public const string RunCommand = "run_command";
    public const string Halt = "halt";
    public const string Resume = "resume";
    public const string Reset = "reset";
    public const string AddBreakpoint = "add_breakpoint";
    public const string RemoveBreakpoint = "remove_breakpoint";
    public const string ListBreakpoints = "list_breakpoints";
    public const string AddWatchpoint = "add_watchpoint";
    public const string RemoveWatchpoint = "remove_watchpoint";
    public const string ReadMemory = "read_memory";
    public const string WriteMemory = "write_memory";
    public const string DumpMemory = "dump_memory";
    public const string IdentifyChip = "identify_chip";
    public const string FlashInfo = "flash_info";
    public const string FlashWrite = "flash_write";
    public const string VerifyImage = "verify_image";
    public const string FlashEraseSector = "flash_erase_sector";
    public const string ReadPeripheralRegister = "read_peripheral_register";
    public const string WritePeripheralRegister = "write_peripheral_register";
    public const string ReadVariable = "read_variable";
    public const string WriteVariable = "write_variable";

    // ── CLI 子命令（走同一 ExecuteAsync，op 即流量归属）──
    public const string Doctor = "doctor";
    public const string Rtt = "rtt";
    public const string Watch = "watch";
}
