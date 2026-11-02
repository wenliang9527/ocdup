namespace Ocdup;

/// <summary>
/// 已知后端/硬件故障模式 → 可操作提示（v0.4.40，现场实战 session cad81f：目标断电后
/// "Error connecting DP: cannot read IDR" 淹在启动输出里，模型只能瞎猜供电还是固件）。
/// 匹配是对小写归一文本的子串查找，命中第一条即返回；hint 是给模型/人看的行动指引，
/// 与 doctor 的 "how to fix" 同一形状（6.3 错误约定：报错必须带下一步）。
/// </summary>
public static class KnownIssues
{
    private static readonly (string Pattern, string Hint)[] Entries =
    [
        ("cannot read idr",
            "the debug port did not answer (DP IDR unreadable) - the target is likely unpowered, held in reset, " +
            "or the SWD wiring is faulty. Check the board power and the SWD cable, then retry connect."),
        ("error connecting dp",
            "the debug port could not be brought up - check the probe wiring and the target power, then retry connect."),
        ("connection closed by openocd",
            "the OpenOCD process dropped the control channel (it may have exited after a target fault) - " +
            "the next call restarts it automatically; backend_log shows the last backend output."),
        ("remote host forcibly closed",
            "the control channel was torn down at the transport level (backend exit or link loss) - " +
            "the next call reconnects automatically; for writes, read the value back to confirm what landed."),
    ];

    /// <summary>在输出/异常文本里找已知故障模式；命中返回可追加的提示段，否则 null。</summary>
    public static string? HintFor(string text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var normalized = text.ToLowerInvariant();
        foreach (var (pattern, hint) in Entries)
        {
            if (normalized.Contains(pattern))
                return "NOTE: " + char.ToUpperInvariant(hint[0]) + hint[1..];
        }
        return null;
    }
}
