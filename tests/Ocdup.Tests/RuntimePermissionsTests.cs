using Ocdup.Backend;
using Ocdup.Config;
using Ocdup.Tools;

namespace Ocdup.Tests;

/// <summary>
/// M-perm 运行时权限：WithRuntimeChanges 补丁语义（来源标 runtime / 原实例不动 / 坏输入拒绝）、
/// set_permissions 独立闸、OCD_RDONLY 环境变量强制只读、HexValue 宽度掩码。
/// </summary>
public sealed class RuntimePermissionsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ocdup-tests-" + Guid.NewGuid().ToString("n"));

    public RuntimePermissionsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* 清理失败不影响结论 */ }
    }

    private string WriteConfig(string json)
    {
        var path = Path.Combine(_dir, "ocd.json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void RuntimePatch_AppliesBoolAndLong_WithRuntimeSource_OriginalUntouched()
    {
        var cfg = OcdConfig.LoadFrom(WriteConfig("""{ "read_only": false, "allow_flash_erase": false, "flash_max_bytes": 1024 }"""));

        var next = cfg.WithRuntimeChanges("""{ "allow_flash_erase": true, "flash_max_bytes": 0 }""");

        Assert.True(next.AllowFlashErase);
        Assert.Equal(0, next.FlashMaxBytes);
        Assert.Equal("(runtime)", next.Sources["allow_flash_erase"]);
        Assert.Equal("(runtime)", next.Sources["flash_max_bytes"]);
        Assert.False(cfg.AllowFlashErase);
        Assert.Equal(1024, cfg.FlashMaxBytes);
        Assert.StartsWith("ocd.json", cfg.Sources["allow_flash_erase"]);
    }

    [Fact]
    public void RuntimePatch_RejectsNonPermissionKeys_AtTheGate()
    {
        var cfg = OcdConfig.LoadFrom(WriteConfig("""{ "read_only": false }"""));

        var ex = Assert.Throws<ArgumentException>(() =>
            cfg.WithRuntimeChanges("""{ "target_cfg": "target/hacked.cfg", "read_only": true }"""));

        Assert.Contains("target_cfg", ex.Message);
        Assert.False(cfg.ReadOnly); // 原实例不被半套用
    }

    [Fact]
    public void RuntimePatch_RejectsNonObjectAndNegativeLimits()
    {
        var cfg = OcdConfig.LoadFrom(WriteConfig("""{ "flash_max_bytes": 1024 }"""));

        Assert.Throws<ArgumentException>(() => cfg.WithRuntimeChanges("""[1]"""));

        var next = cfg.WithRuntimeChanges("""{ "flash_max_bytes": -1 }""");
        Assert.Equal(1024, next.FlashMaxBytes);
        Assert.Contains("flash_max_bytes", next.Sources["ocd_json_error"]);
    }

    [Fact]
    public void SetPermissions_Gate_BlocksByDefault_AndCarriesOptInHint()
    {
        var cfg = OcdConfig.LoadFrom(WriteConfig("{}"));
        var blocked = PermissionGate.CheckSetPermissions(cfg);
        Assert.NotNull(blocked);
        Assert.Contains("allow_set_permissions", blocked!);
        Assert.StartsWith("BLOCKED:", blocked);
    }

    [Fact]
    public void SetPermissions_Gate_AllowsWhenOptedIn()
    {
        var cfg = OcdConfig.LoadFrom(WriteConfig("""{ "permissions": { "allow_set_permissions": true } }"""));
        Assert.Null(PermissionGate.CheckSetPermissions(cfg));
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("0", false)]
    [InlineData(null, false)]
    public void OcdReadonlyEnv_ForcesReadOnly_WithEnvSource(string? envValue, bool expectedReadOnly)
    {
        var cfg = OcdConfig.LoadFrom(WriteConfig("""{ "read_only": false }"""));

        OcdConfig.ApplyOcdReadonlyEnv(cfg, envValue);

        Assert.Equal(expectedReadOnly, cfg.ReadOnly);
        if (expectedReadOnly)
            Assert.Equal("env OCD_READONLY", cfg.Sources["read_only"]);
    }

    [Theory]
    [InlineData(0x1FFFFFFFF, 32, "0xffffffff")]
    [InlineData(0x11234, 16, "0x1234")]
    [InlineData(0x1FF, 8, "0xff")]
    [InlineData(0xAB, 8, "0xab")]
    public void HexValue_Masks_To_Width(long value, int width, string expected)
    {
        Assert.Equal(expected, Parsing.HexValue(value, width));
    }

    // ── set_permissions 的数值上限校验（v0.4.27）：文案而非异常 ──

    [Theory]
    [InlineData(-1)]
    [InlineData(-4096)]
    public void ValidateLimit_NegativeValue_ReturnsActionableError(long value)
    {
        var problem = ConfigTools.ValidateLimit("memory_read_max_bytes", value);

        Assert.NotNull(problem);
        Assert.Contains("ERROR:", problem);
        Assert.Contains("must be >= 0", problem);
        Assert.Contains("show_config", problem);
    }

    [Fact]
    public void ValidateLimit_ZeroAndPositive_Pass()
    {
        Assert.Null(ConfigTools.ValidateLimit("flash_max_bytes", 0));
        Assert.Null(ConfigTools.ValidateLimit("flash_max_bytes", 1048576));
    }
}
