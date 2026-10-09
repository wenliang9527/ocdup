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
}
