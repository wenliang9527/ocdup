using Ocdup.Config;
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

    [Theory]
    [InlineData("target_py32f002a_lock.cfg", "PY32F002A")]
    [InlineData("D:/WORK_VSCODE/Vibe-coding/Ocdup/target_py32f002a_lock.cfg", "PY32F002A")]
    [InlineData(@"C:\ocd\target_stm32f1x.cfg", "STM32F1X")]
    [InlineData("target_AT32F423.cfg", "AT32F423")]      // 大小写归一
    [InlineData("target-at32f423-lock.cfg", "AT32F423")] // -/_ 混用
    [InlineData("interface/cmsis-dap.cfg", null)]       // 非 target_ 开头且无数字 → 不猜
    [InlineData("auto", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("target_.cfg", null)]        // 去掉修饰后什么都不剩
    [InlineData("target_foo.cfg", null)]     // 没有数字,不像型号 → 不猜
    [InlineData("cortex_m.cfg", null)]       // 没有数字
    [InlineData("target_ab.cfg", null)]      // 太短
    public void DeriveModel_From_TargetCfg_FileName(string? targetCfg, string? expected)
    {
        Assert.Equal(expected, IdentifyTools.DeriveModelFromTargetCfg(targetCfg));
    }

    [Fact]
    public async Task TryIdentifyAsync_RejectsBadHex_Before_Touching_The_Backend()
    {
        // connect 的自动识别复用同一入口：坏地址必须在发起任何读之前就返回 null
        // （不建连接、不抛、不镜像流量）。
        var dir = Path.Combine(Path.GetTempPath(), "ocdup-identify-" + Guid.NewGuid().ToString("n"));
        try
        {
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "ocd.json");
            File.WriteAllText(path, """{ "interface_cfg": "interface/cmsis-dap.cfg" }""");
            var cfg = OcdConfig.LoadFrom(path);

            var text = await IdentifyTools.TryIdentifyAsync(cfg, "not-hex", CancellationToken.None);

            Assert.Null(text);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* 清理失败不影响结论 */ }
        }
    }

    private static Dictionary<string, string> Presets() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["AT32F403A"] = "target/at32f403axx.cfg",
        ["PY32F002A"] = "D:/work/ocdup/target_py32f002a_lock.cfg",
    };

    [Fact]
    public void TargetMismatch_Fires_WhenFamilyPresetDiffersFromRunningTarget()
    {
        // 实测场景：板子是 AT32F403A，target_cfg 却钉着 py32（绝对路径 vs scripts 名也要认得出不同）
        var hit = IdentifyTools.TargetMismatch(
            "D:/work/ocdup/target_py32f002a_lock.cfg", "AT32F403A", Presets());

        Assert.NotNull(hit);
        Assert.Equal("AT32F403A", hit!.Value.Suggested);
        Assert.Equal("target/at32f403axx.cfg", hit.Value.PresetCfg);
    }

    [Fact]
    public void TargetMismatch_Silent_WhenAlreadyRunningThatScript()
    {
        // 同脚本不同写法（scripts 名 vs 绝对路径）视为一致：不唠叨
        Assert.Null(IdentifyTools.TargetMismatch(
            "target/at32f403axx.cfg", "AT32F403A", Presets()));
        Assert.Null(IdentifyTools.TargetMismatch(
            @"C:\openocd\scripts\target\at32f403axx.cfg", "at32f403a", Presets()));
    }

    [Fact]
    public void TargetMismatch_Silent_WhenNothingToSuggest()
    {
        Assert.Null(IdentifyTools.TargetMismatch(null, "AT32F403A", new Dictionary<string, string>()));   // 没预设
        Assert.Null(IdentifyTools.TargetMismatch("x.cfg", "unknown - add ...", Presets()));                 // family 未识别
        Assert.Null(IdentifyTools.TargetMismatch("x.cfg", "GD32F303", Presets()));                         // 家族不在预设里
        Assert.Null(IdentifyTools.TargetMismatch("x.cfg", "", Presets()));                                 // 空 family
    }

    [Fact]
    public void TargetMismatch_Suggests_WhenTargetCfgNotSetAtAll()
    {
        // target_cfg 完全没配：更该提示（None 与任何脚本都不相等）
        var hit = IdentifyTools.TargetMismatch(null, "AT32F403A", Presets());
        Assert.NotNull(hit);
        Assert.Equal("AT32F403A", hit!.Value.Suggested);
    }
}
