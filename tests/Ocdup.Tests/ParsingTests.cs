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
