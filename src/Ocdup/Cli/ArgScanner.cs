namespace Ocdup.Cli;

/// <summary>
/// CLI 选项扫描器（v0.4.25）：`--key value` / `--key=value` / 无值旗标三种形态统一处理，
/// 顺带识别 `--help`/`-h`/`/?`。watch/rtt/burn/verify 四个 CLI 的 Parse 循环原本是四胞胎
/// （内联拆分、缺值报错、help 识别各抄一份），收在这里——各 CLI 只剩 switch 分发与各自的
/// 取值校验。行为边界由三个 *Options 的既有测试（60+ 断言）钉死，本类测试只补扫描器自身
/// 的边界。两条吃参数的铁律（都曾被测试先红抓回）：
/// ① 无值旗标**不吃**紧随其后的选项；
/// ② **未知选项也不吃**下一个参数（`--foo --format xml` 报 unknown 之后，`--format xml`
///    照常解析——拼写错误的旗标不该吃掉真选项的实参）。
/// </summary>
internal sealed class ArgScanner(string commandName, string[] args, IReadOnlySet<string> knownKeys, IReadOnlySet<string> flags)
{
    private readonly List<string> _errors = new();
    private int _i;

    public bool Help { get; private set; }

    public IReadOnlyList<string> Errors => _errors;

    /// <summary>便捷构造：flags 是 knownKeys 的子集（无值旗标）。</summary>
    public ArgScanner(string commandName, string[] args, IReadOnlyCollection<string> knownKeys, IReadOnlyCollection<string> flags)
        : this(commandName, args, new HashSet<string>(knownKeys, StringComparer.Ordinal), new HashSet<string>(flags, StringComparer.Ordinal))
    {
    }

    /// <summary>
    /// 取下一个选项；返回 false = 扫描结束。value 的三种来源：`=` 内联、下一个参数、
    /// 旗标恒为 null；缺值时记「needs a value」错误并返回 value=null（调用方 switch 里
    /// 对 value is null 直接跳过即可）。未知选项：记标准报错、**不消费下一个参数**、
    /// 返回 key（调用方 switch 的 default 分支直接忽略）。
    /// </summary>
    public bool Next(out string key, out string? value)
    {
        while (_i < args.Length)
        {
            var arg = args[_i++];
            var eq = arg.IndexOf('=');
            var k = eq > 0 ? arg[..eq] : arg;

            if (k is "--help" or "-h" or "/?")
            {
                Help = true; // 内联形态也认（按 key 识别，value 丢弃）——与历史实现一致
                continue;
            }

            if (!knownKeys.Contains(k))
            {
                _errors.Add($"unknown option '{arg}'."); // 报完整原始 token（含内联部分）
                key = k;
                value = null;
                return true;
            }

            if (eq > 0)
            {
                key = k;
                value = arg[(eq + 1)..];
                return true;
            }

            if (flags.Contains(k))
            {
                key = k;
                value = null;
                return true; // 旗标：不消费下一个参数
            }

            if (_i < args.Length)
            {
                key = k;
                value = args[_i++];
                return true;
            }

            _errors.Add($"option '{k}' needs a value.");
            key = k;
            value = null;
            return true;
        }

        key = "";
        value = null;
        return false;
    }
}
