using Ocdup.Cli;

namespace Ocdup.Tests;

/// <summary>
/// ocdup doctor 的纯函数层（P1-6）：体检单渲染 + 退出码。五项检查本身是
/// 系统调用（文件/进程/PnP/端口），按仓库分工交给实机运行验收，不进单测。
/// </summary>
public sealed class DoctorCliTests
{
    private static readonly DoctorCheck Ok = new("config", DoctorVerdict.Ok, "C:\\proj\\ocd.json");
    private static readonly DoctorCheck Warn = new("backend port", DoctorVerdict.Warn,
        "nothing listening on 127.0.0.1:6666",
        "this is fine for a cold start - connect will launch openocd");
    private static readonly DoctorCheck Fail = new("openocd binary", DoctorVerdict.Fail,
        "OCD_BIN not set and nothing named openocd on PATH",
        "set OCD_BIN to openocd.exe, or install OpenOCD and put it on PATH");

    [Fact]
    public void FormatReport_RendersVerdictsDetailsHintsAndSummary()
    {
        var report = DoctorCli.FormatReport(new[] { Ok, Warn, Fail });

        Assert.Contains("[OK] config", report);
        Assert.Contains("[WARN] backend port", report);
        Assert.Contains("[FAIL] openocd binary", report);
        Assert.Contains("OCD_BIN not set", report);
        // ❌/⚠️ 都带「如何修」
        Assert.Contains("-> how to fix: set OCD_BIN", report);
        Assert.Contains("-> how to fix: this is fine for a cold start", report);
        Assert.Contains("summary: 1 ok, 1 warning(s), 1 failed", report);
        Assert.Contains("re-run `ocdup doctor`", report);
    }

    [Fact]
    public void FormatReport_AllClean_SaysReadyWithoutHints()
    {
        var report = DoctorCli.FormatReport(new[] { Ok, new DoctorCheck("probe", DoctorVerdict.Ok, "CMSIS-DAP/DAPLink") });

        Assert.Contains("environment looks ready", report);
        Assert.DoesNotContain("how to fix", report);
        Assert.DoesNotContain("FAIL", report);
    }

    [Fact]
    public void ExitCode_FailMeansOne_WarnAloneIsZero()
    {
        Assert.Equal(0, DoctorCli.ExitCode(new[] { Ok }));
        Assert.Equal(0, DoctorCli.ExitCode(new[] { Ok, Warn }));
        Assert.Equal(1, DoctorCli.ExitCode(new[] { Ok, Warn, Fail }));
    }

    // ── v0.4.29：--help 契约与其余四个 CLI 对齐（此前 --help 被忽略、直接跑体检，
    //    在环境不全的机器上还会因 FAIL 返回 1）──

    [Fact]
    public async Task RunAsync_Help_PrintsUsageAndExitsZero()
    {
        // 不设 OCD_CONFIG / OCD_BIN：环境必然不全，若 --help 没被识别就会因 FAIL 返回 1。
        var previous = Environment.GetEnvironmentVariable("OCD_CONFIG");
        Environment.SetEnvironmentVariable("OCD_CONFIG", "");
        var previousBin = Environment.GetEnvironmentVariable("OCD_BIN");
        Environment.SetEnvironmentVariable("OCD_BIN", "");
        var previousOut = Console.Out;
        var sw = new StringWriter();
        Console.SetOut(sw);
        try
        {
            var exit = await DoctorCli.RunAsync(["--help"]);

            Assert.Equal(0, exit);
            Assert.Contains("usage: ocdup doctor", sw.ToString());
            Assert.Contains("exit codes:", sw.ToString());
            Assert.DoesNotContain("[FAIL]", sw.ToString()); // 没真跑体检
        }
        finally
        {
            Console.SetOut(previousOut);
            Environment.SetEnvironmentVariable("OCD_CONFIG", previous);
            Environment.SetEnvironmentVariable("OCD_BIN", previousBin);
        }
    }

    [Fact]
    public async Task RunAsync_UnknownOption_ExitsTwoWithGuidance()
    {
        var previousErr = Console.Error;
        var sw = new StringWriter();
        Console.SetError(sw);
        try
        {
            var exit = await DoctorCli.RunAsync(["--bogus"]);

            Assert.Equal(2, exit);
            Assert.Contains("unknown option '--bogus' (ocdup doctor)", sw.ToString());
            Assert.Contains("run 'ocdup doctor --help'", sw.ToString());
        }
        finally
        {
            Console.SetError(previousErr);
        }
    }
}
