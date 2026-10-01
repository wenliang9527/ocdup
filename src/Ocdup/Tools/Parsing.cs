using System.Globalization;

namespace Ocdup.Tools;

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

        return long.TryParse(trimmed, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>把地址渲染回 hex 形式：0x 前缀、小写、至少 8 位（保持 32 位地址的可读性）。</summary>
    public static string Hex(long value) => $"0x{value:x8}";

    /// <summary>按宽度渲染值：0x 前缀、小写、8/16/32 位分别为 2/4/8 位（read_memory JSON 输出用）。</summary>
    public static string HexValue(long value, int width) => width switch
    {
        8 => $"0x{value:x2}",
        16 => $"0x{value:x4}",
        _ => $"0x{value:x8}",
    };

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
}
