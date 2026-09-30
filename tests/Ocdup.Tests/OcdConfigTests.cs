using System.IO;
using Ocdup.Config;

namespace Ocdup.Tests;

/// <summary>
/// 配置层回归：permissions 嵌套块、顶层平铺、来源追踪、坏输入降级、路径基准。
/// 每个用例都通过 LoadFrom 指向临时文件，不依赖 CWD 与环境变量。
/// </summary>
public sealed class OcdConfigTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ocdup-tests-" + Guid.NewGuid().ToString("n"));

    public OcdConfigTests() => Directory.CreateDirectory(_dir);

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
    public void MissingFile_YieldsDefaultsAndNoConfigPath()
    {
        var cfg = OcdConfig.LoadFrom(Path.Combine(_dir, "nope.json"));

        Assert.Null(cfg.ConfigFilePath);
        Assert.Equal("127.0.0.1", cfg.Host);
        Assert.Equal(6666, cfg.Port);
        Assert.False(cfg.ReadOnly);
        Assert.True(cfg.AllowFlash);
        Assert.Equal("(default)", cfg.Sources["port"]);
    }

    [Fact]
    public void NestedPermissionsBlock_IsApplied()
    {
        var path = WriteConfig("""
        {
          "interface_cfg": "interface/cmsis-dap.cfg",
          "permissions": { "read_only": true, "allow_flash": false, "flash_max_bytes": 1024 }
        }
        """);

        var cfg = OcdConfig.LoadFrom(path);

        Assert.True(cfg.ReadOnly);
        Assert.False(cfg.AllowFlash);
        Assert.Equal(1024, cfg.FlashMaxBytes);
        Assert.StartsWith("ocd.json", cfg.Sources["read_only"]);
    }

    [Fact]
    public void NestedPermissionsBlock_WinsOverTopLevel()
    {
        var path = WriteConfig("""{ "read_only": false, "permissions": { "read_only": true } }""");

        Assert.True(OcdConfig.LoadFrom(path).ReadOnly);
    }

    [Fact]
    public void ExplicitValueEqualToDefault_IsStillAttributedToFile()
    {
        // 回归：旧实现按「值是否等于默认」判定来源，显式写 false / 6666 会被错标成 "(default)"。
        var path = WriteConfig("""{ "read_only": false, "port": 6666, "allow_flash": true }""");

        var cfg = OcdConfig.LoadFrom(path);

        Assert.StartsWith("ocd.json", cfg.Sources["read_only"]);
        Assert.StartsWith("ocd.json", cfg.Sources["port"]);
        Assert.StartsWith("ocd.json", cfg.Sources["allow_flash"]);
    }

    [Fact]
    public void WrongTypes_FallBackToDefaultsAndReportError()
    {
        var path = WriteConfig("""{ "port": "6666", "read_only": "yes", "flash_allowed_paths": "build" }""");

        var cfg = OcdConfig.LoadFrom(path);

        Assert.Equal(6666, cfg.Port);
        Assert.False(cfg.ReadOnly);
        Assert.Empty(cfg.FlashAllowedPaths);
        Assert.True(cfg.Sources.ContainsKey("ocd_json_error"));
        Assert.Contains("WARNING", cfg.Describe());
    }

    [Fact]
    public void BrokenJson_YieldsDefaultsAndWarning()
    {
        var path = WriteConfig("""{ "port": """);

        var cfg = OcdConfig.LoadFrom(path);

        Assert.Equal(6666, cfg.Port);
        Assert.True(cfg.Sources.TryGetValue("ocd_json_error", out var error));
        Assert.Contains("ocd.json", error);
    }

    [Fact]
    public void OutOfRangeValues_AreRejectedAndReported()
    {
        var path = WriteConfig("""{ "port": 0, "command_timeout_seconds": 99999, "flash_max_bytes": -5 }""");

        var cfg = OcdConfig.LoadFrom(path);

        Assert.Equal(6666, cfg.Port);
        Assert.Equal(OcdConfig.DefaultCommandTimeoutSeconds, cfg.CommandTimeoutSeconds);
        Assert.Equal(0, cfg.FlashMaxBytes);
        Assert.True(cfg.Sources.ContainsKey("ocd_json_error"));
    }

    [Fact]
    public void ResolvePath_UsesConfigFileDirectory()
    {
        var path = WriteConfig("""{ "flash_allowed_paths": ["build/"] }""");
        var cfg = OcdConfig.LoadFrom(path);

        var expected = Path.GetFullPath(Path.Combine(_dir, "build", "fw.bin"));
        Assert.Equal(expected, cfg.ResolvePath("build/fw.bin"));
        Assert.Equal(Path.GetFullPath(@"C:\absolute\x.bin"), cfg.ResolvePath(@"C:\absolute\x.bin"));
    }

    [Fact]
    public void Describe_ShowsSourcesAndPermissions()
    {
        var path = WriteConfig("""{ "interface_cfg": "interface/x.cfg", "permissions": { "read_only": true } }""");

        var text = OcdConfig.LoadFrom(path).Describe();

        Assert.Contains("interface/x.cfg", text);
        Assert.Contains("read_only", text);
        Assert.Contains("[src: ocd.json", text);
    }

    [Fact]
    public void UnknownKeys_AreIgnored()
    {
        var path = WriteConfig("""{ "//": "comment", "not_a_setting": 1, "permissions": { "allow_flash": true } }""");

        var cfg = OcdConfig.LoadFrom(path);

        Assert.True(cfg.AllowFlash);
        Assert.False(cfg.Sources.ContainsKey("ocd_json_error"));
    }
}
