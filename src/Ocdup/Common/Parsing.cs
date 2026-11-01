using System.Globalization;

namespace Ocdup;

/// <summary>工具层共用的参数解析（设计文档 6.1：地址类参数一律 hex 字符串）。</summary>
public static class Parsing
{
    /// <summary>
    /// 解析地址：接受 "0x20000000"、"20000000"（含下划线分隔）等十六进制写法。
    /// 不接受十进制——避免 "20000000" 被误解成 0x20000000 还是 20000000。
    /// </summary>
    public static bool TryParseHex(string? text, out long value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var trimmed = text.Trim().Replace("_", "");
        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[2..];
        if (trimmed.Length == 0) return false;
        // NumberStyles.HexNumber 的 .NET 实现默默接受正负号（"-5" → -5），地址参数不接受带符号值
        if (trimmed[0] is '+' or '-') return false;

        return long.TryParse(trimmed, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>把地址渲染回 hex 形式：0x 前缀、小写、至少 8 位（保持 32 位地址的可读性）。</summary>
    public static string Hex(long value) => $"0x{value:x8}";

    /// <summary>按宽度渲染值：0x 前缀、小写、8/16/32 位分别为 2/4/8 位，并掩掉超出宽度的位
    /// （防止 33 位输入把 mww 送成多字写）。read_memory JSON 输出与 write_memory 命令构造共用。</summary>
    public static string HexValue(long value, int width) => width switch
    {
        8 => $"0x{value & 0xFF:x2}",
        16 => $"0x{value & 0xFFFF:x4}",
        _ => $"0x{value & 0xFFFFFFFF:x8}",
    };

    /// <summary>
    /// 解码 ARM CPUID（地址 0xE000ED00，任何 Cortex-M 都有）：
    /// [31:24] implementer（'A' = ARM）、[23:20] variant、[19:16] architecture、[15:4] partno、[3:0] patch。
    /// partno 表只收录 ARM 公开的标准值；未知值按 partno 原样报告，不猜。
    /// </summary>
    public static string DescribeCpuid(long cpuid)
    {
        var implementer = (cpuid >> 24) & 0xFF;
        var variant = (cpuid >> 20) & 0xF;
        var architecture = (cpuid >> 16) & 0xF;
        var partno = (cpuid >> 4) & 0xFFF;
        var patch = cpuid & 0xF;

        var core = partno switch
        {
            0xC20 => "Cortex-M0",
            0xC21 => "Cortex-M1",
            0xC23 => "Cortex-M3",
            0xC24 => "Cortex-M4",
            0xC27 => "Cortex-M7",
            0xC60 => "Cortex-M0+",
            0xD20 => "Cortex-M23",
            0xD21 => "Cortex-M33",
            _ => $"Cortex-M (partno 0x{partno:x3})",
        };
        var arch = architecture switch
        {
            0xC => "ARMv6-M",
            0xF => "ARMv7-M/v8-M",
            _ => $"arch 0x{architecture:x}",
        };
        var impl = implementer is >= 0x20 and <= 0x7E
            ? $"'{(char)implementer}'"
            : $"0x{implementer:x2}";
        return $"{core} ({arch}, implementer {impl}, r{variant}p{patch})";
    }

    /// <summary>
    /// 解析 OpenOCD mdw/mdh/mdb 的文本输出：每行 "0xADDR: v v v v"（值的位宽随参数，
    /// 值本身无 0x 前缀；每行末尾常有尾随空格）。只从形态匹配的行收集值，其余行
    /// （警告/日志混入）忽略；收集满 <paramref name="count"/> 个即成功（多余值丢弃）。
    /// 目标运行时读失败会返回错误文本，收集不满 → false（调用方把原文带回给模型）。
    /// </summary>
    public static bool TryParseMemoryOutput(string output, int width, int count, out long[] values)
    {
        values = Array.Empty<long>();
        if (string.IsNullOrWhiteSpace(output) || count <= 0) return false;

        var collected = new List<long>(count);
        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.Trim();
            // 行首:0xADDR: ;也容忍无 0x 前缀的 ADDR:（不同版本/日志包装的形态）
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var addrPart = line[..colon].Trim().Replace("_", "");
            if (addrPart.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) addrPart = addrPart[2..];
            if (addrPart.Length == 0 || !long.TryParse(addrPart, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _))
                continue;

            foreach (var tok in line[(colon + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!long.TryParse(tok, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v))
                    return false; // 值位置出现非 hex 文本 = 错误输出,整体判失败
                collected.Add(v);
                if (collected.Count >= count) goto done;
            }
        }

    done:
        if (collected.Count < count) return false;
        values = collected.ToArray();
        return true;
    }

    /// <summary>
    /// 解析 OpenOCD `reg &lt;name&gt;` 的输出，取寄存器值。实测两种形态：
    /// atopenocd（0.11.0+dev）"pc (/32): 0x080002ab62"（<b>冒号</b>），
    /// 主线旧版 "pc = 0x080002ad"（<b>等号</b>）——逐行找第一个 '=' 或 ':'，
    /// 其后第一个 hex token（0x 前缀可有可无）即值；分隔符后是普通文本的行
    /// （日志前缀/错误文本）整行跳过。全部行都取不到 → null（best-effort 调用方只少一行）。
    /// </summary>
    public static uint? ParseRegValue(string output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;

        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.Trim();
            var sep = line.IndexOfAny(SeparatorChars);
            if (sep < 0) continue;
            foreach (var tok in line[(sep + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var t = tok.Replace("_", "");
                if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) t = t[2..];
                if (uint.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v))
                    return v;
                // 尾注/括号/散落的分隔符（"( = 0x… ) 形态）:跳过继续找;普通文本 = 这行不是读数
                if (t.StartsWith('(') || t is "=" or ")") continue;
                break;
            }
        }
        return null;
    }

    private static readonly char[] SeparatorChars = ['=', ':'];

    /// <summary>
    /// 把 OpenOCD `targets` 表格解析成一行式摘要（每目标一行：
    /// "0* at32f435zmt7 (cortex_m, little) running"），给模型省 token。
    /// 没有任何数据行（表头不符/空输出/错误文本）→ null，调用方回退展示原文。
    /// </summary>
    public static string? TrySummarizeTargets(string output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;

        var lines = new List<string>();
        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.Trim();
            // 数据行以 "0*" / "1" 这类序号开头,且至少含 6 列(序号 名称 类型 字节序 tap 状态)。
            // 跳过表头(含 TargetName)与分隔行(-- 开头)。
            if (line.Length == 0 || line.StartsWith("--") || line.Contains("TargetName")) continue;
            var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 6) continue;
            if (!char.IsDigit(tokens[0][0])) continue;

            var idx = tokens[0];
            var name = tokens[1];
            var type = tokens[2];
            var endian = tokens[3];
            var state = tokens[^1];
            lines.Add($"{idx} {name} ({type}, {endian}) {state}");
        }

        return lines.Count > 0 ? string.Join("\n", lines) : null;
    }

    /// <summary>
    /// P0-3（v0.4.28）：`targets` 输出里是否有目标处于 halted 态（与 <see cref="TrySummarizeTargets"/>
    /// 同一套数据行判定：序号开头、≥6 列、末列是状态）。disconnect 停机前用它决定要不要附 halted 提示。
    /// null/空/解析不出的文本一律 false——提示是 best-effort，宁缺勿误。
    /// </summary>
    public static bool AnyTargetHalted(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return false;

        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("--") || line.Contains("TargetName")) continue;
            var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 6) continue;
            if (!char.IsDigit(tokens[0][0])) continue;
            if (tokens[^1].Equals("halted", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}
