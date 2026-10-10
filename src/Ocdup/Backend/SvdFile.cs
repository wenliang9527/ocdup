using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Ocdup.Backend;

/// <summary>SVD 寄存器位域。</summary>
public sealed record SvdField(string Name, int BitOffset, int BitWidth);

/// <summary>SVD 寄存器（cluster 内的寄存器已展平，AddressOffset 含 cluster 偏移累加）。</summary>
public sealed record SvdRegister(string Name, long AddressOffset, int SizeBits, string Access, long? ResetValue, IReadOnlyList<SvdField> Fields);

/// <summary>SVD 外设。</summary>
public sealed record SvdPeripheral(string Name, long BaseAddress, IReadOnlyList<SvdRegister> Registers);

/// <summary>SVD 设备。</summary>
public sealed record SvdDevice(IReadOnlyList<SvdPeripheral> Peripherals);

/// <summary>
/// CMSIS-SVD 解析（L2 符号层，v0.4.21；设计文档第 7 节）。自研 System.Xml.Linq 解析，
/// 不引第三方依赖（C7 同款纪律：二进制/包不进产物）。覆盖点按「最小可用」取舍：
/// 外设基址 + 寄存器（含 cluster 递归展平）+ 三种位域写法（bitOffset/bitWidth、
/// lsb/msb、bitRange "[msb:lsb]"）+ size/access/resetValue 缺省兜底。SVD 的数值约定
/// 是十六进制（带不带 0x 前缀都认）。命名空间不敏感（本地名匹配）——厂商 SVD 有无
/// 默认命名空间都能解析（实测定案：Keil/ST/AT32 三种形态混用）。
/// </summary>
public static class SvdFile
{
    /// <summary>从文件解析（坏文件抛 InvalidDataException，Message 带路径，可直接展示）。</summary>
    public static SvdDevice ParseFile(string path)
    {
        try
        {
            return ParseXml(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            throw new InvalidDataException($"{path}: {ex.Message}", ex);
        }
    }

    public static SvdDevice ParseXml(string xml)
    {
        var root = XDocument.Parse(xml).Root ?? throw new InvalidDataException("the file has no root element");
        if (root.Name.LocalName != "device")
            throw new InvalidDataException($"the root element is '{root.Name.LocalName}', expected 'device' - is this a CMSIS-SVD file?");
        var defaultSize = ParseInt(Text(root, "size")) ?? 32;

        var peripherals = new List<SvdPeripheral>();
        // peripheral 是根的孙节点（device > peripherals > peripheral），必须 Descendants
        foreach (var p in root.Descendants().Where(x => x.Name.LocalName == "peripheral"))
        {
            var name = Text(p, "name") ?? "(unnamed)";
            var baseAddress = ParseNumber(Text(p, "baseAddress")) ?? 0;
            var registers = new List<SvdRegister>();
            if (Named(p, "registers").FirstOrDefault() is { } container)
                CollectRegisters(container, 0, defaultSize, registers, prefix: "");
            peripherals.Add(new SvdPeripheral(name, baseAddress, registers));
        }

        return new SvdDevice(peripherals);
    }

    private static void CollectRegisters(XElement container, long baseOffset, int defaultSize, List<SvdRegister> into, string prefix)
    {
        foreach (var r in Named(container, "register"))
        {
            // cluster 内的寄存器带路径前缀（"PWM.CCR"），按名解析与清单展示都用全名
            var name = prefix + (Text(r, "name") ?? "(unnamed)");
            var offset = baseOffset + (ParseNumber(Text(r, "addressOffset")) ?? 0);
            var size = ParseInt(Text(r, "size")) ?? defaultSize;
            var access = Text(r, "access") ?? "read-write";
            long? resetValue = Text(r, "resetValue") is { } rv ? ParseNumber(rv) : null;

            var fields = new List<SvdField>();
            if (Named(r, "fields").FirstOrDefault() is { } fieldContainer)
            {
                foreach (var f in Named(fieldContainer, "field"))
                {
                    var (bitOffset, bitWidth) = FieldBits(f);
                    if (bitWidth > 0)
                        fields.Add(new SvdField(Text(f, "name") ?? "(unnamed)", bitOffset, bitWidth));
                }
            }

            into.Add(new SvdRegister(name, offset, size, access, resetValue, fields));
        }

        // cluster：偏移累加 + 名字路径前缀，递归（嵌套 cluster 也吃）
        foreach (var c in Named(container, "cluster"))
        {
            var clusterOffset = ParseNumber(Text(c, "addressOffset")) ?? 0;
            var clusterPrefix = prefix + (Text(c, "name") ?? "") + ".";
            CollectRegisters(c, baseOffset + clusterOffset, defaultSize, into, clusterPrefix);
        }
    }

    /// <summary>三种位域写法（CMSIS-SVD spec §Register Properties）：bitOffset/bitWidth、lsb/msb、bitRange。</summary>
    private static (int BitOffset, int BitWidth) FieldBits(XElement f)
    {
        var offset = ParseInt(Text(f, "bitOffset"));
        var width = ParseInt(Text(f, "bitWidth"));
        if (offset is { } o && width is { } w) return (o, w);

        var lsb = ParseInt(Text(f, "lsb"));
        var msb = ParseInt(Text(f, "msb"));
        if (lsb is { } lo && msb is { } hi) return (lo, hi - lo + 1);

        var range = Text(f, "bitRange");
        if (range is not null)
        {
            var m = BitRangeRegex.Match(range);
            if (m.Success)
            {
                var hi2 = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                var lo2 = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                return (lo2, hi2 - lo2 + 1);
            }
        }

        return (0, 0);
    }

    // ── 按名解析 ──

    public static SvdPeripheral? FindPeripheral(SvdDevice dev, string name) =>
        dev.Peripherals.FirstOrDefault(p => p.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));

    public static SvdRegister? FindRegister(SvdPeripheral peripheral, string name) =>
        peripheral.Registers.FirstOrDefault(r => r.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));

    public static long AbsoluteAddress(SvdPeripheral peripheral, SvdRegister register) =>
        peripheral.BaseAddress + register.AddressOffset;

    public static IReadOnlyList<string> PeripheralNames(SvdDevice dev) =>
        dev.Peripherals.Select(p => p.Name).ToList();

    public static IReadOnlyList<string> RegisterNames(SvdPeripheral peripheral) =>
        peripheral.Registers.Select(r => r.Name).ToList();

    /// <summary>未命中时的「你是不是想要」建议：名字包含输入（去掉 ./空格 修饰）者优先，最多 3 个。</summary>
    public static IReadOnlyList<string> SuggestPeripherals(SvdDevice dev, string input)
    {
        var needle = input.Trim().TrimEnd('.', ' ', '/');
        var names = PeripheralNames(dev);
        var matches = names.Where(n => n.Contains(needle, StringComparison.OrdinalIgnoreCase)).Take(3).ToList();
        return matches.Count > 0 ? matches : names.Take(3).ToList();
    }

    public static IReadOnlyList<string> SuggestRegisters(SvdPeripheral peripheral, string input)
    {
        var needle = input.Trim();
        var names = RegisterNames(peripheral);
        var matches = names.Where(n => n.Contains(needle, StringComparison.OrdinalIgnoreCase)).Take(3).ToList();
        return matches.Count > 0 ? matches : names.Take(3).ToList();
    }

    // ── 位域解码与命令构造 ──

    public static long ExtractField(long value, SvdField field)
    {
        var mask = field.BitWidth >= 64 ? ~0L : (1L << field.BitWidth) - 1;
        return (value >> field.BitOffset) & mask;
    }

    /// <summary>寄存器读回的完整解码块：绝对地址、按宽度的十六进制值、逐位域抽取、reset 比对。</summary>
    public static string DescribeRegister(SvdRegister register, long absoluteAddress, long rawValue)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{register.Name} @ {Hex(absoluteAddress)} ({register.SizeBits}-bit, {register.Access}): {HexOf(rawValue, register.SizeBits)}");
        foreach (var f in register.Fields)
        {
            sb.AppendLine($"  {f.Name}[{f.BitOffset + f.BitWidth - 1}:{f.BitOffset}] = 0x{ExtractField(rawValue, f):x}");
        }
        if (register.ResetValue is { } reset)
            sb.AppendLine(rawValue == reset
                ? "  matches the reset value"
                : $"  differs from the reset value ({HexOf(reset, register.SizeBits)})");
        return sb.ToString().TrimEnd();
    }

    public static string ReadCommand(SvdRegister register, long absoluteAddress) =>
        $"{MonitorFor(register.SizeBits)} {Hex(absoluteAddress)} 1";

    /// <summary>写命令：值按寄存器宽度掩掉高位（杜绝 33 位输入送成多字写，C2 同款纪律）。
    /// 注意写方向的助记符是 mw*（mwb/mwh/mww），与读方向的 md* 是两套。</summary>
    public static string WriteCommand(SvdRegister register, long absoluteAddress, long value)
    {
        var masked = register.SizeBits switch
        {
            8 => value & 0xFF,
            16 => value & 0xFFFF,
            _ => value & 0xFFFFFFFF,
        };
        var monitor = register.SizeBits switch
        {
            8 => "mwb",
            16 => "mwh",
            _ => "mww",
        };
        return $"{monitor} {Hex(absoluteAddress)} 0x{masked:x}";
    }

    /// <summary>read-only / 只读为主的 access 语义拒写；write-only / read-write / writeOnce 都可写。</summary>
    public static bool IsWritable(SvdRegister register) =>
        !register.Access.Contains("read-only", StringComparison.OrdinalIgnoreCase);

    internal static string MonitorFor(int sizeBits) => sizeBits switch
    {
        8 => "mdb",
        16 => "mdh",
        _ => "mdw",
    };

    // ── 底层工具 ──

    private static readonly Regex BitRangeRegex = new(@"\[\s*(\d+)\s*:\s*(\d+)\s*\]", RegexOptions.Compiled);

    private static IEnumerable<XElement> Named(XElement e, string name) =>
        e.Elements().Where(x => x.Name.LocalName == name);

    private static string? Text(XElement e, string name) =>
        Named(e, name).FirstOrDefault()?.Value.Trim();

    private static int? ParseInt(string? text) =>
        ParseNumber(text) is { } v && v is >= int.MinValue and <= int.MaxValue ? (int)v : null;

    /// <summary>
    /// CMSIS-SVD 的 scaledNonNegativeInteger 语义：<c>0x</c> 前缀 = 十六进制，<c>#</c> =
    /// 二进制，**无前缀 = 十进制**（lsb/msb/bitOffset/size 全是十进制——"24" 是 24 不是
    /// 0x24，实测踩过）；无前缀但不是合法十进制（厂商手写的裸 hex 如 "0C"）再退回 hex 兜底。
    /// </summary>
    private static long? ParseNumber(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.Trim();
        // 有前缀：0x = 十六进制、# = 二进制——语义由前缀钉死，不走 decimal 分支
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return long.TryParse(t[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex) ? hex : null;
        if (t.StartsWith('#'))
        {
            try { return Convert.ToInt64(t[1..], 2); }
            catch { return null; }
        }
        // 无前缀：spec 语义是十进制（lsb/msb/size 都是十进制——"24" 是 24 不是 0x24）；
        // 厂商手写裸 hex（如 "0C"）不是合法十进制，再退回 hex 兜底。
        if (long.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dec)) return dec;
        return long.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var bareHex) ? bareHex : null;
    }

    private static string Hex(long value) => $"0x{value:x8}";

    private static string HexOf(long value, int sizeBits) => sizeBits switch
    {
        8 => $"0x{value & 0xFF:x2}",
        16 => $"0x{value & 0xFFFF:x4}",
        _ => $"0x{value & 0xFFFFFFFF:x8}",
    };
}
