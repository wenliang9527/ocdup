using System.IO;
using Ocdup.Config;
using Ocdup.Launcher;

namespace Ocdup.Tests;

/// <summary>openocd 命令行构造（端口固定、gdb/telnet 禁用、transport 顺序）。</summary>
public sealed class LauncherArgumentsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ocdup-args-" + Guid.NewGuid().ToString("n"));

    public LauncherArgumentsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* 清理失败不影响结论 */ }
    }

    private OcdConfig Load(string json)
    {
        var path = Path.Combine(_dir, "ocd.json");
        File.WriteAllText(path, json);
        return OcdConfig.LoadFrom(path);
    }

    [Fact]
    public void BuildArguments_PinsPortsAndTransportOrder()
    {
        var cfg = Load("""
        {
          "interface_cfg": "interface/cmsis-dap.cfg",
          "target_cfg": "target/at32f435xx.cfg",
          "transport": "swd",
          "port": 7777
        }
        """);

        var args = OpenOcdLauncher.BuildArguments(cfg, @"C:\ocd\scripts", cfg.InterfaceCfg!);

        Assert.Equal(new[]
        {
            "-s", @"C:\ocd\scripts",
            "-c", "bindto {127.0.0.1}",
            "-f", "interface/cmsis-dap.cfg",
            "-c", "transport select {swd}",
            "-f", "target/at32f435xx.cfg",
            "-c", "gdb_port disabled",
            "-c", "telnet_port disabled",
            "-c", "tcl_port 7777",
        }, args);
    }

    [Fact]
    public void BuildArguments_SkipsScriptsTransportAndTargetWhenUnset()
    {
        var cfg = Load("""{ "interface_cfg": "interface/stlink.cfg" }""");

        var args = OpenOcdLauncher.BuildArguments(cfg, null, cfg.InterfaceCfg!);

        Assert.Equal(new[]
        {
            "-c", "bindto {127.0.0.1}",
            "-f", "interface/stlink.cfg",
            "-c", "gdb_port disabled",
            "-c", "telnet_port disabled",
            "-c", "tcl_port 6666",
        }, args);
    }

    [Fact]
    public void BuildArguments_HonorsConfiguredHostAndPort()
    {
        var cfg = Load("""{ "interface_cfg": "interface/x.cfg", "host": "127.0.0.2", "port": 1234 }""");

        var args = OpenOcdLauncher.BuildArguments(cfg, null, cfg.InterfaceCfg!);

        Assert.Contains("bindto {127.0.0.2}", args);
        Assert.Contains("tcl_port 1234", args);
    }

    [Fact]
    public void BuildArguments_QuotesHostAndTransportAsTclLiterals()
    {
        // ocd.json 侧写进 host/transport 的特殊字符不得逃出 TCL 字面量（A-13）：
        // 大括号分组后 ]; 与空格都是字面量，注入串行不通
        var cfg = Load("""{ "interface_cfg": "interface/x.cfg", "host": "127.0.0.1]; shutdown", "transport": "swd] ; halt" }""");

        var args = OpenOcdLauncher.BuildArguments(cfg, null, cfg.InterfaceCfg!);

        Assert.Contains("bindto {127.0.0.1]; shutdown}", args);
        Assert.Contains("transport select {swd] ; halt}", args);
        Assert.DoesNotContain("bindto 127.0.0.1", args); // 无引号形态必须消失
    }
}
