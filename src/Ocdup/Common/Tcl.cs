using System.Text;

namespace Ocdup;

/// <summary>
/// TCL 命令字面量处理。命令最终会被包进 capture { … } 送到 OpenOCD 的 TCL-RPC 端口，
/// 所以路径里的空格、反斜杠必须先被正确引用。
/// </summary>
public static class Tcl
{
    /// <summary>
    /// 把任意字符串安全地嵌进 TCL 命令。
    /// 优先用大括号字面量 {…}：TCL 对大括号内的内容不做任何替换，
    /// 所以 Windows 路径的 \ 与空格都是字面量（这是路径的首选形态）。
    /// 只有当值本身含 { 或 } 时才退回双引号形式，转义 \ " $ [ ]。
    /// </summary>
    public static string Quote(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (value.IndexOfAny(BraceChars) < 0)
            return "{" + value + "}";

        var sb = new StringBuilder(value.Length + 2);
        sb.Append('"');
        foreach (var c in value)
        {
            if (c is '\\' or '"' or '$' or '[' or ']')
                sb.Append('\\');
            sb.Append(c);
        }
        sb.Append('"');
        return sb.ToString();
    }

    private static readonly char[] BraceChars = { '{', '}' };
}
