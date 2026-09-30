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
}
