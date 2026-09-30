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
}
