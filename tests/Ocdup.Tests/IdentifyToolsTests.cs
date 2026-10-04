using Ocdup.Tools;

namespace Ocdup.Tests;

/// <summary>identify_chip 的纯函数部分：CPUID 解码（ARM 标准位域）与 chip_id_map 数值匹配。</summary>
public sealed class IdentifyToolsTests
{
    [Fact]
    public void Cpuid_Decodes_CortexM4_With_Revision()
    {
        var text = Parsing.DescribeCpuid(0x410FC241);
        Assert.Contains("Cortex-M4", text);
        Assert.Contains("r0p1", text);
        Assert.Contains("'A'", text);
    }

    [Theory]
    [InlineData(0x410FC241, "Cortex-M4")]
    [InlineData(0x411FC231, "Cortex-M3")]
    [InlineData(0x410CC601, "Cortex-M0+")]
    [InlineData(0x410FC999, "Cortex-M (partno 0xc99)")] // 未知 partno 按原样报告，不猜
    public void Cpuid_Decodes_Core_Family(long cpuid, string expected)
    {
        Assert.Contains(expected, Parsing.DescribeCpuid(cpuid));
    }

    [Fact]
    public void Chip_Match_Ignores_Case_And_Padding_And_Junk_Keys()
    {
        var map = new Dictionary<string, string> { ["0x0410"] = "STM32F1xx", ["junk!!"] = "never" };
        Assert.Equal("STM32F1xx", IdentifyTools.MatchChipFamily(map, 0x410));
        Assert.Null(IdentifyTools.MatchChipFamily(map, 0x411));
        Assert.Null(IdentifyTools.MatchChipFamily(new Dictionary<string, string>(), 0x410));
    }
}
