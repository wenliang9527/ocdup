using Ocdup.Cli;
using Ocdup.Tools;

namespace Ocdup.Tests;

/// <summary>
/// burn CLI（v0.4.20）的纯函数面：选项解析（含 .bin 必须显式地址的量产语义）与
/// 退出码映射。烧录编排本体复用 flash_write 抽出的核心（闸与判据单一真源），
/// 核心行为由 FlashToolsTests 与 e2e 链路 4/18 钉死。
/// </summary>
public sealed class BurnCliTests
{
    // ── BurnOptions.Parse ──

    [Fact]
    public void Parse_WithoutFile_IsAnError()
    {
        var o = BurnOptions.Parse([]);
        Assert.False(o.Help);
        Assert.Contains(o.Errors, e => e.Contains("--file"));
    }

    [Fact]
    public void Parse_AllOptions()
    {
        var o = BurnOptions.Parse(["--file", "build/fw.elf", "--address", "0x08000000", "--no-verify", "--no-reset"]);
        Assert.Equal("build/fw.elf", o.File);
        Assert.Equal("0x08000000", o.Address);
        Assert.False(o.Verify);
        Assert.False(o.Reset);
        Assert.Empty(o.Errors);
    }

    [Fact]
    public void Parse_InlineEqualsForm()
    {
        var o = BurnOptions.Parse(["--file=build/fw.bin", "--address=0x08000000"]);
        Assert.Equal("build/fw.bin", o.File);
        Assert.Equal("0x08000000", o.Address);
        Assert.True(o.Verify);
        Assert.True(o.Reset);
        Assert.Empty(o.Errors);
    }

    [Fact]
    public void Parse_BinWithoutAddress_IsRejected_Already()
    {
        // 量产语义把「.bin 必须显式加载地址」提前到解析期——失败要发生在碰后端之前。
        var o = BurnOptions.Parse(["--file", "build/fw.bin"]);
        Assert.Contains(o.Errors, e => e.Contains("explicit load address"));
    }

    [Fact]
    public void Parse_BinWithAddress_IsAccepted()
    {
        var o = BurnOptions.Parse(["--file", "build/fw.bin", "--address", "0x08000000"]);
        Assert.Empty(o.Errors);
    }

    [Fact]
    public void Parse_BadAddress_IsReported()
    {
        var o = BurnOptions.Parse(["--file", "fw.elf", "--address", "hello"]);
        Assert.Contains(o.Errors, e => e.Contains("hex"));
    }

    [Fact]
    public void Parse_UnknownOption_IsReported()
    {
        var o = BurnOptions.Parse(["--file", "fw.elf", "--foo"]);
        Assert.Contains(o.Errors, e => e.Contains("unknown option '--foo'"));
    }

    [Fact]
    public void Parse_Help_Alone_ProducesNoErrors()
    {
        var o = BurnOptions.Parse(["--help"]);
        Assert.True(o.Help);
        Assert.Empty(o.Errors);
    }

    // ── 退出码映射 ──

    [Fact]
    public void ExitCode_Verified_IsZero()
    {
        var outcome = new FlashTools.CoreOutcome(true, FlashTools.CoreFailure.Usage, "report", FlashTools.FlashWriteVerdict.ProgrammedVerified);
        Assert.Equal(0, BurnCli.MapExitCode(outcome));
    }

    [Fact]
    public void ExitCode_Unverified_IsZero_BecauseTheOperatorAskedForIt()
    {
        var outcome = new FlashTools.CoreOutcome(true, FlashTools.CoreFailure.Usage, "report", FlashTools.FlashWriteVerdict.Unverified);
        Assert.Equal(0, BurnCli.MapExitCode(outcome));
    }

    [Theory]
    [InlineData("ProgrammedButVerifyFailed")]
    [InlineData("WriteFailed")]
    [InlineData("ProgrammedVerifyInconclusive")] // v0.4.30：CRC 算法没跑起来 = 没验成，不是通过
    public void ExitCode_VerdictFailures_AreOne(string verdictName)
    {
        // 用字符串承载枚举：public 测试签名不能暴露 internal 枚举（CS0051）
        var verdict = Enum.Parse<FlashTools.FlashWriteVerdict>(verdictName);
        var outcome = new FlashTools.CoreOutcome(true, FlashTools.CoreFailure.Usage, "report", verdict);
        Assert.Equal(1, BurnCli.MapExitCode(outcome));
    }

    [Fact]
    public void ExitCode_UsageAndBlocked_AreTwo()
    {
        Assert.Equal(2, BurnCli.MapExitCode(new FlashTools.CoreOutcome(false, FlashTools.CoreFailure.Usage, "usage", default)));
        Assert.Equal(2, BurnCli.MapExitCode(new FlashTools.CoreOutcome(false, FlashTools.CoreFailure.Blocked, "BLOCKED: ...", default)));
    }

    [Fact]
    public void ExitCode_BackendFailure_IsThree()
    {
        Assert.Equal(3, BurnCli.MapExitCode(new FlashTools.CoreOutcome(false, FlashTools.CoreFailure.Backend, "channel failed", default)));
    }
}
