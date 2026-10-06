using Ocdup.Config;
using Ocdup.Tools;

namespace Ocdup.Tests;

/// <summary>
/// set_target 的纯函数层（v0.4.0）：预设名解析（list/空/未知名/命中大小写不敏感）。
/// 工具层涉及后端重启与配置单例，交由 e2e 假后端覆盖；这里只测不碰硬件的分支。
/// </summary>
public sealed class TargetToolsTests
{
    private static OcdConfig Cfg(string json)
    {
        var dir = Path.Combine(Path.GetTempPath(), "ocdup-target-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "ocd.json");
        File.WriteAllText(path, json);
        return OcdConfig.LoadFrom(path);
    }

    [Fact]
    public void ResolvePreset_Hits_CaseInsensitive_AndReturnsCanonicalKey()
    {
        var cfg = Cfg("""{ "target_presets": { "AT32F403A": "target/at32f403axx.cfg" } }""");

        var (error, presetCfg, suggested) = TargetTools.ResolvePreset(cfg, "at32f403a");

        Assert.Null(error);
        Assert.Equal("target/at32f403axx.cfg", presetCfg);
        Assert.Equal("AT32F403A", suggested); // 规范名回显，用户传小写也显示注册表的写法
    }

    [Fact]
    public void ResolvePreset_List_ReturnsCatalog()
    {
        var cfg = Cfg("""{ "target_presets": { "AT32F403A": "target/at32f403axx.cfg" } }""");

        var (error, _, _) = TargetTools.ResolvePreset(cfg, "list");

        Assert.NotNull(error); // list 不是错误通道：文案走 error 位带回清单
        Assert.Contains("AT32F403A -> target/at32f403axx.cfg", error);
    }

    [Fact]
    public void ResolvePreset_UnknownName_ListsAvailable()
    {
        var cfg = Cfg("""{ "target_presets": { "AT32F403A": "target/at32f403axx.cfg" } }""");

        var (error, presetCfg, _) = TargetTools.ResolvePreset(cfg, "GD32F303");

        Assert.Null(presetCfg);
        Assert.Contains("not a configured target preset", error);
        Assert.Contains("AT32F403A", error);
    }

    [Fact]
    public void ResolvePreset_NoPresetsConfigured_ExplainsHowToAdd()
    {
        var cfg = Cfg("{}");

        var (error, _, _) = TargetTools.ResolvePreset(cfg, "AT32F403A");

        Assert.Contains("no \"target_presets\" entries", error);
        Assert.Contains("target_presets", error); // 文案里给出添加范例的键名
    }

    [Fact]
    public void ResolvePreset_EmptyName_RequiresName()
    {
        var cfg = Cfg("""{ "target_presets": { "AT32F403A": "target/at32f403axx.cfg" } }""");

        var (error, _, _) = TargetTools.ResolvePreset(cfg, "   ");

        Assert.Contains("name is required", error);
    }
}
