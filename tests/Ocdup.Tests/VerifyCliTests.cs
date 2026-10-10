using Ocdup.Cli;
using Ocdup.Tools;

namespace Ocdup.Tests;

/// <summary>
/// verify CLI（v0.4.26）的纯函数面：选项解析（ArgScanner 消费；.bin 必须显式偏移）与
/// 退出码映射。比对本体复用 flash_write/verify_image 家族抽出的 VerifyCoreAsync
/// （InterpretVerifyOutput 判据单一真源），核心行为由 FlashToolsTests 与 e2e 链路 4/21 钉死。
/// </summary>
public sealed class VerifyCliTests
{
    // ── VerifyOptions.Parse ──

    [Fact]
    public void Parse_WithoutFile_IsAnError()
    {
        var o = VerifyOptions.Parse([]);
        Assert.False(o.Help);
        Assert.Contains(o.Errors, e => e.Contains("--file"));
    }

    [Fact]
    public void Parse_AllOptions_AndInlineForm()
    {
        var o = VerifyOptions.Parse(["--file=build/fw.bin", "--address=0x08000000"]);
        Assert.Equal("build/fw.bin", o.File);
        Assert.Equal("0x08000000", o.Address);
        Assert.Empty(o.Errors);
    }

    [Fact]
    public void Parse_BinWithoutAddress_IsRejected_Early()
    {
        var o = VerifyOptions.Parse(["--file", "build/fw.bin"]);
        Assert.Contains(o.Errors, e => e.Contains("explicit offset"));
    }

    [Fact]
    public void Parse_HexWithoutAddress_IsFine()
    {
        var o = VerifyOptions.Parse(["--file", "build/fw.hex"]);
        Assert.Empty(o.Errors);
    }

    [Fact]
    public void Parse_UnknownOption_IsReported()
    {
        var o = VerifyOptions.Parse(["--file", "fw.elf", "--foo"]);
        Assert.Contains(o.Errors, e => e.Contains("unknown option '--foo'"));
    }

    [Fact]
    public void Parse_Help_Alone_ProducesNoErrors()
    {
        var o = VerifyOptions.Parse(["--help"]);
        Assert.True(o.Help);
        Assert.Empty(o.Errors);
    }

    // ── 退出码映射 ──

    [Fact]
    public void ExitCode_Verified_IsZero()
    {
        var outcome = new FlashTools.VerifyOutcome(true, FlashTools.CoreFailure.Usage, "report", Verified: true);
        Assert.Equal(0, VerifyCli.MapExitCode(outcome));
    }

    [Fact]
    public void ExitCode_VerifyFailed_IsOne()
    {
        var outcome = new FlashTools.VerifyOutcome(true, FlashTools.CoreFailure.Usage, "report", Verified: false);
        Assert.Equal(1, VerifyCli.MapExitCode(outcome));
    }

    [Fact]
    public void ExitCode_Usage_IsTwo()
    {
        Assert.Equal(2, VerifyCli.MapExitCode(new FlashTools.VerifyOutcome(false, FlashTools.CoreFailure.Usage, "usage", false)));
    }

    [Fact]
    public void ExitCode_Backend_IsThree()
    {
        Assert.Equal(3, VerifyCli.MapExitCode(new FlashTools.VerifyOutcome(false, FlashTools.CoreFailure.Backend, "channel failed", false)));
    }
}
