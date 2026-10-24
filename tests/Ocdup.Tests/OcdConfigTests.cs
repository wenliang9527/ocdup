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
        Assert.False(cfg.AllowRawCommand); // P0-1 定案：逃生舱默认收紧为 opt-in
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
    public void JsoncComments_AreAccepted()
    {
        // OcdConfig 用 JsonCommentHandling.Skip 解析：ocd.json 里可以直接写 // 与 /* */ 注释，
        // 不必用 "//key": "value" 这种伪注释键（伪键虽然也被忽略，但语义上更脏、且可能被误当成配置项）。
        var path = WriteConfig("""
        {
          // 探针与目标
          "interface_cfg": "auto",
          "target_cfg": "target/at32f403axx.cfg",
          /* 权限：个人开发机放开烧录，只留体积上限 */
          "permissions": { "read_only": false, "flash_max_bytes": 1048576 }
        }
        """);

        var cfg = OcdConfig.LoadFrom(path);

        Assert.True(cfg.IsInterfaceAuto);
        Assert.Equal("target/at32f403axx.cfg", cfg.TargetCfg);
        Assert.Equal(1048576, cfg.FlashMaxBytes);
        Assert.False(cfg.Sources.ContainsKey("ocd_json_error"));
    }

    [Fact]
    public void UnknownKeys_AreIgnored()
    {
        var path = WriteConfig("""{ "//": "comment", "not_a_setting": 1, "permissions": { "allow_flash": true } }""");

        var cfg = OcdConfig.LoadFrom(path);

        Assert.True(cfg.AllowFlash);
        Assert.False(cfg.Sources.ContainsKey("ocd_json_error"));
    }

    [Fact]
    public void TargetPresets_Parsed_CaseInsensitive_DescribeListsThem()
    {
        var path = WriteConfig("""
        {
          "target_presets": { "AT32F403A": "target/at32f403axx.cfg", "stm32f103": "target/stm32f1x.cfg" }
        }
        """);

        var cfg = OcdConfig.LoadFrom(path);

        Assert.Equal(2, cfg.TargetPresets.Count);
        Assert.True(cfg.TargetPresets.ContainsKey("at32f403a")); // 名字大小写不敏感
        Assert.Equal("target/at32f403axx.cfg", cfg.TargetPresets["AT32F403A"]);
        Assert.Contains("AT32F403A -> target/at32f403axx.cfg", cfg.Describe()); // show_config 展示注册表
    }

    [Fact]
    public void TargetPresets_WrongShape_NotesError_KeepsDefaults()
    {
        var path = WriteConfig("""{ "target_presets": [1, 2] }""");

        var cfg = OcdConfig.LoadFrom(path);

        Assert.Empty(cfg.TargetPresets);
        Assert.Contains("target_presets", cfg.Sources["ocd_json_error"]);
    }

    [Fact]
    public void WithTargetChange_SwapsOnlyTarget_SourceRuntime_OriginalUntouched()
    {
        var cfg = OcdConfig.LoadFrom(WriteConfig("""{ "target_cfg": "target_py32f002a_lock.cfg" }"""));

        var next = cfg.WithTargetChange("target/at32f403axx.cfg");

        Assert.Equal("target/at32f403axx.cfg", next.TargetCfg);
        Assert.Equal("(runtime via set_target)", next.Sources["target_cfg"]);
        Assert.Equal("target_py32f002a_lock.cfg", cfg.TargetCfg); // 原实例不动
    }

    // ── A-15（v0.4.33）：ConfigStore 读-改-写并发不丢更新 ──

    [Fact]
    public async Task ConfigStore_ConcurrentRuntimePatches_AllKeysSurvive()
    {
        // 无锁的读-改-写会丢更新：两个补丁都基于同一旧实例计算，后写者覆盖先写者。
        // 每个键多线程反复打：锁修复后终态必须包含全部变更。
        var store = new ConfigStore(new OcdConfig());
        var patches = new[]
        {
            "{\"allow_flash_erase\": true}",
            "{\"allow_raw_command\": true}",
            "{\"allow_exec\": false}",
            "{\"allow_memory_write\": false}",
            "{\"flash_max_bytes\": 2097152}",
            "{\"memory_read_max_bytes\": 8192}",
        };
        var tasks = patches
            .SelectMany(p => Enumerable.Range(0, 8).Select(_ => Task.Run(() => store.ApplyRuntimePatch(p))))
            .ToArray();
        await Task.WhenAll(tasks);

        var c = store.Current;
        Assert.True(c.AllowFlashErase);
        Assert.True(c.AllowRawCommand);
        Assert.False(c.AllowExec);
        Assert.False(c.AllowMemoryWrite);
        Assert.Equal(2097152L, c.FlashMaxBytes);
        Assert.Equal(8192L, c.MemoryReadMaxBytes);
    }

    [Fact]
    public void ConfigStore_TargetSwitchAndPatchShareOneCurrent()
    {
        // set_target 与 set_permissions 共用写锁：先后应用时互不覆盖对方字段
        var store = new ConfigStore(new OcdConfig());

        store.ApplyTargetSwitch("target/at32f403axx.cfg");
        store.ApplyRuntimePatch("{\"allow_flash_erase\": true}");

        Assert.Equal("target/at32f403axx.cfg", store.Current.TargetCfg);
        Assert.True(store.Current.AllowFlashErase);
    }
}
