namespace Ocdup;

/// <summary>
/// A-10（v0.4.34）：机器可读结论行。flash_write / verify_image / dump_memory 的输出
/// 末行追加 <c>OCDUP-RESULT: &lt;op&gt;=&lt;verdict&gt;</c>——失败结论原先包在成功形态的
/// 人读文本里（"programmed but verify FAILED"、"NOTE: ... contains errors"），面板与
/// 脚本没有可靠的判定锚点。约定：
/// ① 只加在「操作已到达后端」的结论上；早退的 ERROR:/BLOCKED: 文本本就有机器前缀，不加；
/// ② 人读文本一字不动，标记只作为追加的最后一行；CLI 不加（退出码族是它的机器通道）。
/// </summary>
internal static class ResultTag
{
    internal const string Prefix = "OCDUP-RESULT: ";

    /// <summary>把标记追加为 report 的最后一行（吞掉原文本的尾随空白行，保证「末行」成立）。</summary>
    internal static string Append(string report, string op, string verdict) =>
        report.TrimEnd() + "\n" + Prefix + op + "=" + verdict;
}
