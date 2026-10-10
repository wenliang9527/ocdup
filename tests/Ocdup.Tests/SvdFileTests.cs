using Ocdup.Backend;

namespace Ocdup.Tests;

/// <summary>
/// SVD 外设寄存器层（L2，v0.4.21）的纯函数面：XML 解析（三种位域写法 / cluster 展平 /
/// 缺省值兜底 / 有无命名空间都认）、按名解析（大小写不敏感 + 建议清单）、位域抽取与
/// 渲染、按寄存器宽度选 monitor、写方向按宽度掩码。
/// </summary>
public sealed class SvdFileTests
{
    private const string MiniSvd = """
        <?xml version="1.0"?>
        <device xmlns="http://www.keil.com/svd" xmlns:xs="http://www.w3.org/2001/XMLSchema-instance">
          <name>TEST32</name>
          <peripherals>
            <peripheral>
              <name>RCC</name>
              <baseAddress>0x40021000</baseAddress>
              <registers>
                <register>
                  <name>CR</name>
                  <addressOffset>0x00</addressOffset>
                  <size>32</size>
                  <access>read-write</access>
                  <resetValue>0x00000081</resetValue>
                  <fields>
                    <field><name>HSION</name><bitOffset>0</bitOffset><bitWidth>1</bitWidth></field>
                    <field><name>PLLON</name><lsb>24</lsb><msb>24</msb></field>
                    <field><name>HPRE</name><bitRange>[7:4]</bitRange></field>
                  </fields>
                </register>
                <cluster>
                  <name>PWM</name>
                  <addressOffset>0x100</addressOffset>
                  <register><name>CCR</name><addressOffset>0x0</addressOffset><size>16</size></register>
                </cluster>
              </registers>
            </peripheral>
            <peripheral>
              <name>GPIOA</name>
              <baseAddress>0x40010800</baseAddress>
              <registers>
                <register><name>ODR</name><addressOffset>0x0C</addressOffset><access>read-only</access></register>
              </registers>
            </peripheral>
          </peripherals>
        </device>
        """;

    private static SvdDevice Parse() => SvdFile.ParseXml(MiniSvd);

    [Fact]
    public void Parse_PercievesPeripherals_AndBaseAddresses()
    {
        var dev = Parse();
        Assert.Equal(2, dev.Peripherals.Count);
        Assert.Equal("RCC", dev.Peripherals[0].Name);
        Assert.Equal(0x40021000L, dev.Peripherals[0].BaseAddress);
        Assert.Equal(0x40010800L, dev.Peripherals[1].BaseAddress);
    }

    [Fact]
    public void Parse_Fields_InAllThreeBitRangeForms()
    {
        var cr = SvdFile.FindRegister(SvdFile.FindPeripheral(Parse(), "RCC")!, "CR")!;
        Assert.Equal(3, cr.Fields.Count);
        Assert.Equal(("HSION", 0, 1), (cr.Fields[0].Name, cr.Fields[0].BitOffset, cr.Fields[0].BitWidth));
        Assert.Equal(("PLLON", 24, 1), (cr.Fields[1].Name, cr.Fields[1].BitOffset, cr.Fields[1].BitWidth));
        Assert.Equal(("HPRE", 4, 4), (cr.Fields[2].Name, cr.Fields[2].BitOffset, cr.Fields[2].BitWidth));
        Assert.Equal(0x81L, cr.ResetValue);
    }

    [Fact]
    public void Parse_ClusterRegisters_AreFlattenedWithAccumulatedOffset()
    {
        var ccr = SvdFile.FindRegister(SvdFile.FindPeripheral(Parse(), "RCC")!, "PWM.CCR")!;
        Assert.Equal(0x100L, ccr.AddressOffset); // cluster offset + register offset
        Assert.Equal(16, ccr.SizeBits);
    }

    [Fact]
    public void Parse_MissingSizeAccessReset_FallBackToDefaults()
    {
        var odr = SvdFile.FindRegister(SvdFile.FindPeripheral(Parse(), "GPIOA")!, "ODR")!;
        Assert.Equal(32, odr.SizeBits);          // device/peripheral 未给 size → 32
        Assert.Equal("read-only", odr.Access);   // 显式给的不覆盖
        Assert.Null(odr.ResetValue);             // 未给 resetValue → null（不做 reset 比对）
    }

    [Fact]
    public void Parse_XmlWithoutNamespace_AlsoWorks()
    {
        const string noNs = "<device><peripherals><peripheral><name>X</name><baseAddress>0x1</baseAddress>" +
            "<registers><register><name>R</name><addressOffset>0x2</addressOffset></register></registers>" +
            "</peripheral></peripherals></device>";
        var dev = SvdFile.ParseXml(noNs);
        var r = SvdFile.FindRegister(SvdFile.FindPeripheral(dev, "x")!, "r")!;
        Assert.Equal(0x3L, SvdFile.AbsoluteAddress(dev.Peripherals[0], r));
    }

    [Fact]
    public void Parse_BrokenXml_ThrowsWithFileHint()
    {
        var path = Path.Combine(Path.GetTempPath(), "ocdup-svdtest-broken.xml");
        File.WriteAllText(path, "<device><peripherals>oops");
        try
        {
            var ex = Assert.Throws<InvalidDataException>(() => SvdFile.ParseFile(path));
            Assert.Contains(path, ex.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Find_IsCaseInsensitive_AndFindsClusterRegisters()
    {
        var dev = Parse();
        Assert.Equal("RCC", SvdFile.FindPeripheral(dev, "rcc")!.Name);
        Assert.NotNull(SvdFile.FindRegister(SvdFile.FindPeripheral(dev, "RCC")!, "pwm.ccr"));
        Assert.Null(SvdFile.FindPeripheral(dev, "UART9"));
    }

    [Fact]
    public void Suggestions_IncludeTheClosestNames()
    {
        var dev = Parse();
        var suggestions = SvdFile.SuggestPeripherals(dev, "RCC.");
        Assert.Contains("RCC", suggestions);
        Assert.Equal(2, SvdFile.PeripheralNames(dev).Count);
    }

    [Fact]
    public void ExtractField_PicksTheConfiguredBits()
    {
        var cr = SvdFile.FindRegister(SvdFile.FindPeripheral(Parse(), "RCC")!, "CR")!;
        Assert.Equal(0x3L, SvdFile.ExtractField(0x1234, cr.Fields[2])); // HPRE [7:4] of 0x1234 = 0x3
        Assert.Equal(0x1L, SvdFile.ExtractField(0x01000000, cr.Fields[1])); // PLLON bit24
    }

    [Fact]
    public void DescribeRegister_ShowsValue_Fields_AndResetComparison()
    {
        var cr = SvdFile.FindRegister(SvdFile.FindPeripheral(Parse(), "RCC")!, "CR")!;
        var text = SvdFile.DescribeRegister(cr, 0x40021000L, 0x01000082);
        Assert.Contains("0x40021000", text);
        Assert.Contains("0x01000082", text);
        Assert.Contains("HPRE", text);
        Assert.Contains("reset", text, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(8, "mdb 0x40021000 1")]
    [InlineData(16, "mdh 0x40021000 1")]
    [InlineData(32, "mdw 0x40021000 1")]
    public void ReadCommand_PicksMonitorByWidth(int sizeBits, string expected)
    {
        var cr = SvdFile.FindRegister(SvdFile.FindPeripheral(Parse(), "RCC")!, "CR")!;
        var r = cr with { SizeBits = sizeBits };
        Assert.Equal(expected, SvdFile.ReadCommand(r, 0x40021000));
    }

    [Fact]
    public void WriteCommand_MasksByWidth()
    {
        var ccr = SvdFile.FindRegister(SvdFile.FindPeripheral(Parse(), "RCC")!, "PWM.CCR")!;
        // 16 位寄存器：传入 32 位值 → 高位被掩掉
        Assert.Equal("mwh 0x40021100 0x2345", SvdFile.WriteCommand(ccr, 0x40021100, 0x1_2345));
    }

    [Fact]
    public void IsWritable_RespectsAccess()
    {
        var odr = SvdFile.FindRegister(SvdFile.FindPeripheral(Parse(), "GPIOA")!, "ODR")!;
        Assert.False(SvdFile.IsWritable(odr));
        var cr = SvdFile.FindRegister(SvdFile.FindPeripheral(Parse(), "RCC")!, "CR")!;
        Assert.True(SvdFile.IsWritable(cr));
    }
}
