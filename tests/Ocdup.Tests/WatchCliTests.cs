using Ocdup.Backend;
using Ocdup.Cli;

namespace Ocdup.Tests;

/// <summary>watch CLI（M-watch）的纯函数面：选项解析、对齐取值、行渲染。</summary>
public sealed class WatchCliTests
{
    // ── WatchOptions.Parse ──

    [Fact]
    public void Parse_Defaults_WithoutNames_ProduceError()
    {
        var o = WatchOptions.Parse([]);
        Assert.False(o.Help);
        Assert.Equal(250, o.IntervalMs);
        Assert.Equal("pretty", o.Format);
        Assert.False(o.Halt);
        Assert.Equal(0, o.Count);
        Assert.Empty(o.Names);
        Assert.Contains(o.Errors, e => e.Contains("no variable to watch"));
    }

    [Fact]
    public void Parse_RepeatableNames_AndCommaSplitting()
    {
        var o = WatchOptions.Parse(["--name", "a", "--name", "b,c"]);
        Assert.Equal(["a", "b", "c"], o.Names);
        Assert.Empty(o.Errors);
    }

    [Fact]
    public void Parse_InlineEqualsForm()
    {
        var o = WatchOptions.Parse(["--name=gCounter", "--elf=fw.elf", "--interval=100"]);
        Assert.Equal(["gCounter"], o.Names);
        Assert.Equal("fw.elf", o.ElfPath);
        Assert.Equal(100, o.IntervalMs);
        Assert.Empty(o.Errors);
    }

    [Fact]
    public void Parse_AllOptions()
    {
        var o = WatchOptions.Parse(["--name", "x", "--interval", "20", "--format", "CSV", "--halt", "--count", "5"]);
        Assert.Equal(20, o.IntervalMs);
        Assert.Equal("csv", o.Format);
        Assert.True(o.Halt);
        Assert.Equal(5, o.Count);
        Assert.Empty(o.Errors);
    }

    [Fact]
    public void Parse_Help_SuppressesNameError()
    {
        var o = WatchOptions.Parse(["--help"]);
        Assert.True(o.Help);
        Assert.Empty(o.Errors);
    }

    [Theory]
    [InlineData("--interval", "abc")]
    [InlineData("--interval", "5")]       // 低于 20
    [InlineData("--interval", "600001")]  // 高于上限
    public void Parse_RejectsOutOfRangeInterval(string flag, string value)
    {
        var o = WatchOptions.Parse(["--name", "a", flag, value]);
        Assert.Contains(o.Errors, e => e.Contains("interval"));
    }

    [Fact]
    public void Parse_RejectsUnknownOption_AndBadFormat()
    {
        var o = WatchOptions.Parse(["--name", "a", "--foo", "--format", "xml"]);
        Assert.Contains(o.Errors, e => e.Contains("unknown option '--foo'"));
        Assert.Contains(o.Errors, e => e.Contains("format 'xml'"));
    }

    [Fact]
    public void Parse_MissingValue_ReportsOption()
    {
        var o = WatchOptions.Parse(["--name", "a", "--interval"]);
        Assert.Contains(o.Errors, e => e.Contains("option '--interval' needs a value"));
    }

    // ── WatchValue.Extract:mdw 对齐字 → 小端摘标量 ──

    [Theory]
    [InlineData(0x20000010, 1, 0x11u)]  // 对齐 u8:取最低字节
    [InlineData(0x20000011, 1, 0x22u)]  // 非对齐 u8:按地址低 2 位移位
    [InlineData(0x20000013, 1, 0x44u)]
    [InlineData(0x20000012, 2, 0x4433u)] // 半字对齐 u16
    [InlineData(0x20000010, 4, 0x44332211u)]
    public void Extract_ShiftsByAddressAlignment(long address, int size, uint expected)
    {
        // 小端内存 0x11 0x22 0x33 0x44 → mdw 读回 word = 0x44332211
        Assert.Equal(expected, WatchValue.Extract(0x44332211u, address, size));
    }

    [Theory]
    [InlineData(1, 0xab, "0xab")]
    [InlineData(2, 0xbeef, "0xbeef")]
    [InlineData(4, 0xdeadbeef, "0xdeadbeef")]
    public void FormatHex_WidthFollowsSize(int size, uint value, string expected)
    {
        Assert.Equal(expected, WatchValue.FormatHex(value, size));
    }

    // ── 渲染 ──

    [Fact]
    public void RenderCsv_TimeThenDecimalValues()
    {
        var vars = new List<ElfSymbol> { new("gCounter", 0x20000004, 4), new("flag", 0x20000010, 1) };
        var line = WatchCli.RenderCsv(new DateTime(2026, 10, 6, 10, 0, 1, 123), vars, [0x42, 1]);
        Assert.Equal("10:00:01.123,66,1", line);
    }

    [Fact]
    public void RenderPretty_AlignedNames_HexAndDecimal()
    {
        var vars = new List<ElfSymbol> { new("gCounter", 0x20000004, 4), new("flag", 0x20000010, 1) };
        var line = WatchCli.RenderPretty(new DateTime(2026, 10, 6, 10, 0, 1, 123), vars, [0x42, 1]);
        Assert.Equal("10:00:01.123  gCounter=0x00000042 (66)  flag    =0x01 (1)", line);
    }
}
