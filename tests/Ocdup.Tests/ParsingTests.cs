using Ocdup.Tools;

namespace Ocdup.Tests;

/// <summary>地址解析与渲染（工具层所有地址参数的入口）。</summary>
public sealed class ParsingTests
{
    [Theory]
    [InlineData("0x20000000", 0x20000000L)]
    [InlineData("20000000", 0x20000000L)]
    [InlineData("0X0800_0000", 0x08000000L)]
    [InlineData("  0xffffffff  ", 0xffffffffL)]
    public void TryParseHex_AcceptsHexForms(string text, long expected)
    {
        Assert.True(Parsing.TryParseHex(text, out var value));
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("hello")]
    [InlineData("0x")]
    [InlineData("12g4")]
    public void TryParseHex_RejectsInvalidInput(string text)
    {
        Assert.False(Parsing.TryParseHex(text, out _));
    }

    [Fact]
    public void Hex_PadsToEightDigits()
    {
        Assert.Equal("0x08000000", Parsing.Hex(0x08000000));
        Assert.Equal("0x20000000", Parsing.Hex(0x20000000));
        Assert.Equal("0x00000100", Parsing.Hex(0x100));
    }

    // ── ParseRegValue:halt 后 `reg <name>` 输出的寄存器值解析 ──

    [Theory]
    [InlineData("pc = 0x080002ad", 0x080002adu)]
    [InlineData("pc (/32): 0x0800ab62", 0x0800ab62u)]            // atopenocd 实测:冒号分隔
    [InlineData("xPSR (/32): 0x81000000", 0x81000000u)]          // 寄存器名大小写敏感,值同格式
    [InlineData("sp = 0x20019D28", 0x20019D28u)]                 // 大写 hex
    [InlineData("xpsr = 0x01000000 (APSR/nzcvq...)", 0x01000000u)] // 带尾注
    [InlineData("pc = 080002ad", 0x080002adu)]                   // 无 0x 前缀
    [InlineData("pc (/32) = 0x00000100", 0x100u)]                // 版本差异:名带位宽注
    [InlineData("Info : accepting 'gdb' connection\npc (/32): 0x0800ab62", 0x0800ab62u)] // 日志前缀行跳过
    public void ParseRegValue_AcceptsRegOutputForms(string output, uint expected)
    {
        Assert.Equal(expected, Parsing.ParseRegValue(output));
    }

    [Theory]
    [InlineData("")]
    [InlineData("target not halted")]                 // 无分隔符的错误文本
    [InlineData("pc = unknown register")]             // 分隔符后无 hex token
    [InlineData("Error: Target not halted")]          // 部分版本错误形态
    [InlineData("register xpsr not found in current target")] // atopenocd 实测:寄存器名不识别
    public void ParseRegValue_RejectsNonValueOutput(string output)
    {
        Assert.Null(Parsing.ParseRegValue(output));
    }

    // ── TryParseMemoryOutput:read_memory format=json 的解析器 ──

    private const string Mdw16 = """
        0x20000000: 11111111 22222222 33333333 44444444
        0x20000010: 55555555 66666666 77777777 88888888
        0x20000020: 99999999 aaaaaaaa bbbbbbbb cccccccc
        0x20000030: dddddddd eeeeeeee ffffffff 00000000
        """;

    [Fact]
    public void TryParseMemoryOutput_ParsesMdwLines()
    {
        Assert.True(Parsing.TryParseMemoryOutput(Mdw16, 32, 16, out var values));
        Assert.Equal(16, values.Length);
        Assert.Equal(0x11111111L, values[0]);
        Assert.Equal(0x88888888L, values[7]);
        Assert.Equal(0x00000000L, values[15]);
    }

    [Fact]
    public void TryParseMemoryOutput_ToleratesTrailingSpacesAndCrlf()
    {
        // 真实 OpenOCD 每行末尾有空格,e2e 假后端同样;\r\n 换行也不能坏
        var text = "0x20000000: aabbccdd \r\n0x20000004: 11223344 \r\n";
        Assert.True(Parsing.TryParseMemoryOutput(text, 32, 2, out var values));
        Assert.Equal(new long[] { 0xAABBCCDD, 0x11223344 }, values);
    }

    [Fact]
    public void TryParseMemoryOutput_HandlesByteAndHalfwordWidths()
    {
        var bytes = "0x20000000: ff 01 02 03 04 05 06 07\n";
        Assert.True(Parsing.TryParseMemoryOutput(bytes, 8, 8, out var v8));
        Assert.Equal(new long[] { 0xFF, 1, 2, 3, 4, 5, 6, 7 }, v8);

        var halves = "0x20000000: ffff 0001 0002 0003\n";
        Assert.True(Parsing.TryParseMemoryOutput(halves, 16, 4, out var v16));
        Assert.Equal(new long[] { 0xFFFF, 1, 2, 3 }, v16);

        Assert.Equal("0xff", Parsing.HexValue(0xFF, 8));
        Assert.Equal("0x0001", Parsing.HexValue(1, 16));
        Assert.Equal("0xffffffff", Parsing.HexValue(0xFFFFFFFF, 32));
    }

    [Theory]
    [InlineData("")]
    [InlineData("mem_ap read failed at 0x20000000")]   // 目标运行时读失败的错误文本
    [InlineData("0x20000000: zz not-hex")]             // 值位置非 hex
    [InlineData("0x20000000: 11111111\n0x20000004: 22222222")] // 值数不足
    public void TryParseMemoryOutput_RejectsUnparseableOutput(string output)
    {
        Assert.False(Parsing.TryParseMemoryOutput(output, 32, 4, out var values));
        Assert.Empty(values);
    }

    // ── TrySummarizeTargets:status/connect 的 targets 摘要 ──

    private static string TargetsText() => string.Join('\n',
        "    TargetName         Type       Endian TapName            State",
        "--  ------------------ ---------- ------ ------------------ ------------",
        " 0* at32f435zmt7       cortex_m   little at32f435zmt7.cpu   running");

    [Fact]
    public void TrySummarizeTargets_CollapsesTableToOneLine()
    {
        var summary = Parsing.TrySummarizeTargets(TargetsText());
        Assert.NotNull(summary);
        Assert.Equal("0* at32f435zmt7 (cortex_m, little) running", summary);
        Assert.DoesNotContain("TargetName", summary);
        Assert.DoesNotContain("----", summary);
    }

    [Fact]
    public void TrySummarizeTargets_KeepsEveryTargetRow()
    {
        var text = TargetsText() + "\n 1  second.core       cortex_m   big    tap2               halted";
        var summary = Parsing.TrySummarizeTargets(text);
        Assert.NotNull(summary);
        Assert.Equal(2, summary.Split('\n').Length);
        Assert.Contains("1 second.core (cortex_m, big) halted", summary);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Error: target named \"at32\" does not exist")]
    [InlineData("    TargetName         Type       Endian TapName            State")]
    public void TrySummarizeTargets_ReturnsNullForNonTableOutput(string output)
    {
        Assert.Null(Parsing.TrySummarizeTargets(output));
    }
}
