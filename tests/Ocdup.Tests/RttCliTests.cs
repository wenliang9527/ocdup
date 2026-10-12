using Ocdup.Cli;
using Ocdup.Backend;

namespace Ocdup.Tests;

/// <summary>
/// rtt CLI（v0.4.19）的纯函数面：选项解析、TCL 命令构造、setup 结果判定、
/// 控制块日志扫描（fork 的 rtt start 找不到控制块只发 LOG_INFO、不报 TCL 错）、限流判定。
/// 命令面按 atopenocd-py32（ArteryTek fork）实测：rtt setup/start/stop/channels +
/// rtt server start/stop（见 design-plan v0.4.19 变更记录）。
/// </summary>
public sealed class RttCliTests
{
    // ── RttOptions.Parse ──

    [Fact]
    public void Parse_Defaults_AreTheDocumentedOnes()
    {
        var o = RttOptions.Parse([]);
        Assert.False(o.Help);
        Assert.Equal(0x20000000L, o.Address);
        Assert.Equal(4096, o.Size);
        Assert.Equal("SEGGER RTT", o.Identifier);
        Assert.Equal(0, o.Channel);
        Assert.Equal(19221, o.Port);
        Assert.False(o.Info);
        Assert.Equal(0, o.Lines);
        Assert.Equal(0L, o.Bytes);
        Assert.Equal(0, o.Seconds);
        Assert.Empty(o.Errors);
    }

    [Fact]
    public void Parse_CustomValues_AndInlineForm()
    {
        var o = RttOptions.Parse([
            "--address", "0x20000100", "--size=2048", "--id", "MY RTT",
            "--channel", "2", "--port", "19300", "--info",
            "--lines", "10", "--bytes", "4096", "--seconds", "30",
        ]);
        Assert.Equal(0x20000100L, o.Address);
        Assert.Equal(2048, o.Size);
        Assert.Equal("MY RTT", o.Identifier);
        Assert.Equal(2, o.Channel);
        Assert.Equal(19300, o.Port);
        Assert.True(o.Info);
        Assert.Equal(10, o.Lines);
        Assert.Equal(4096L, o.Bytes);
        Assert.Equal(30, o.Seconds);
        Assert.Empty(o.Errors);
    }

    [Fact]
    public void Parse_NoHexPrefix_IsAccepted()
    {
        var o = RttOptions.Parse(["--address", "20000000"]);
        Assert.Equal(0x20000000L, o.Address);
        Assert.Empty(o.Errors);
    }

    [Fact]
    public void Parse_BadAddress_IsReported()
    {
        var o = RttOptions.Parse(["--address", "hello"]);
        Assert.Contains(o.Errors, e => e.Contains("address") && e.Contains("hex"));
    }

    [Theory]
    [InlineData("15")]        // 低于下限
    [InlineData("1048577")]   // 高于上限
    [InlineData("abc")]
    public void Parse_BadSize_IsReported(string value)
    {
        var o = RttOptions.Parse(["--size", value]);
        Assert.Contains(o.Errors, e => e.Contains("size"));
    }

    [Fact]
    public void Parse_BadPort_IsReported()
    {
        var o = RttOptions.Parse(["--port", "80"]); // < 1024
        Assert.Contains(o.Errors, e => e.Contains("port"));
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("16")]
    public void Parse_BadChannel_IsReported(string value)
    {
        var o = RttOptions.Parse(["--channel", value]);
        Assert.Contains(o.Errors, e => e.Contains("channel"));
    }

    [Fact]
    public void Parse_TooLongIdentifier_IsReported()
    {
        // fork 的 rtt_setup：id_length >= RTT_CB_MAX_ID_LENGTH(16) 即拒
        var o = RttOptions.Parse(["--id", "1234567890123456"]);
        Assert.Contains(o.Errors, e => e.Contains("id") && e.Contains("15"));
    }

    [Fact]
    public void Parse_Help_Alone_ProducesNoErrors()
    {
        // 家规与 watch 一致：--help 在 RunAsync 层面最先短路（打印 usage 返回 0）；
        // 选项级错误照样收集——它不影响 help 路径的行为。
        var o = RttOptions.Parse(["--help"]);
        Assert.True(o.Help);
        Assert.Empty(o.Errors);
    }

    [Fact]
    public void Parse_UnknownOption_IsReported()
    {
        var o = RttOptions.Parse(["--foo"]);
        Assert.Contains(o.Errors, e => e.Contains("unknown option '--foo'"));
    }

    // ── TCL 命令构造 ──

    [Fact]
    public void BuildSetupCommand_QuotesIdentifier_AsTclLiteral()
    {
        var o = RttOptions.Parse(["--address", "0x20000100", "--size", "2048", "--id", "MY RTT"]);
        var cmd = RttCli.BuildSetupCommand(o);
        Assert.Equal("rtt setup 0x20000100 2048 {MY RTT}", cmd);
    }

    [Fact]
    public void BuildSetupCommand_Defaults()
    {
        Assert.Equal("rtt setup 0x20000000 4096 {SEGGER RTT}", RttCli.BuildSetupCommand(RttOptions.Parse([])));
    }

    [Fact]
    public void BuildServerCommands_ContainPortAndChannel()
    {
        var o = RttOptions.Parse(["--port", "19300", "--channel", "3"]);
        Assert.Equal("rtt server start 19300 3", RttCli.BuildServerStartCommand(o));
        Assert.Equal("rtt server stop 19300", RttCli.BuildServerStopCommand(o));
    }

    [Fact]
    public void BuildFixedCommands()
    {
        Assert.Equal("rtt start", RttCli.BuildStartCommand());
        Assert.Equal("rtt stop", RttCli.BuildRttStopCommand());
        Assert.Equal("rtt channels", RttCli.BuildChannelsCommand());
    }

    // ── setup 结果判定（capture 回来的文本）──

    [Theory]
    [InlineData("", true)]
    [InlineData(" ", true)]
    [InlineData("rtt: Invalid control block ID", false)]
    [InlineData("Error: invalid command name \"rtt setup\"", false)]
    [InlineData("invalid command name \"rtt\"", false)]
    public void SetupAccepted_ClassifiesBackendOutput(string output, bool expected)
    {
        Assert.Equal(expected, RttCli.SetupAccepted(output));
    }

    // ── 控制块日志扫描 ──

    [Fact]
    public void Scan_FoundLine_ParsesControlBlockAddress()
    {
        var hit = RttCli.ScanControlBlock("rtt: Control block found at 0x20000123");
        Assert.NotNull(hit);
        Assert.Equal(RttCli.ControlBlockOutcome.Found, hit.Value.Outcome);
        Assert.Equal(0x20000123L, hit.Value.Address);
    }

    [Fact]
    public void Scan_NotFoundLine_IsDecisive()
    {
        var hit = RttCli.ScanControlBlock("Info : rtt: No control block found");
        Assert.NotNull(hit);
        Assert.Equal(RttCli.ControlBlockOutcome.NotFound, hit.Value.Outcome);
        Assert.Null(hit.Value.Address);
    }

    [Theory]
    [InlineData("Info : SWD DPIDR 0x2ba01477")]
    [InlineData("wrote 12345 bytes")]
    [InlineData("rtt: Connection for channel 0 closed")]
    public void Scan_NoiseLines_AreIndeterminate(string line)
    {
        // "rtt: Connection for channel N closed" 是 rtt server 的 TCP 连接事件，
        // 与控制块搜索无关——不得误判成结论。
        Assert.Null(RttCli.ScanControlBlock(line));
    }

    [Fact]
    public void ScanControlBlockLines_FirstDecisiveLineWins()
    {
        var lines = new[] { "Info : pad", "rtt: No control block found", "rtt: Control block found at 0x1" };
        var hit = RttCli.ScanControlBlockLines(lines);
        Assert.NotNull(hit);
        Assert.Equal(RttCli.ControlBlockOutcome.NotFound, hit.Value.Outcome);

        var lines2 = new[] { "Info : pad", "rtt: Control block found at 0x20000040" };
        var hit2 = RttCli.ScanControlBlockLines(lines2);
        Assert.Equal(RttCli.ControlBlockOutcome.Found, hit2!.Value.Outcome);
        Assert.Equal(0x20000040L, hit2.Value.Address);
    }

    // ── 限流判定 ──

    [Fact]
    public void LimitReached_UnlimitedByDefault()
    {
        var o = RttOptions.Parse([]);
        Assert.False(RttCli.LimitReached(1_000_000, 1_000_000, o));
    }

    [Fact]
    public void LimitReached_BytesAndLinesBothContribute()
    {
        var o = RttOptions.Parse(["--bytes", "100", "--lines", "5"]);
        Assert.False(RttCli.LimitReached(99, 4, o));
        Assert.True(RttCli.LimitReached(100, 0, o));
        Assert.True(RttCli.LimitReached(0, 5, o));
    }

    [Fact]
    public void LimitReached_MinimumOneLine_InInfoModeIsNeverReached()
    {
        // info 模式没有流，limit 判定必须恒 false（不让 --lines/--bytes 误伤 --info）
        var o = RttOptions.Parse(["--info", "--lines", "1"]);
        Assert.False(RttCli.LimitReached(0, 0, o));
    }

    // ── 退出码家族（v0.4.27）：与 burn / verify 同族 0/1/2/3 ──

    [Fact]
    public void ExitCode_FollowsTheBurnVerifyFamily()
    {
        Assert.Equal(0, RttCli.ExitCode(RttCli.RttFailure.None));
        Assert.Equal(1, RttCli.ExitCode(RttCli.RttFailure.Runtime));
        Assert.Equal(1, RttCli.ExitCode(RttCli.RttFailure.Data));
        Assert.Equal(2, RttCli.ExitCode(RttCli.RttFailure.Usage));
        Assert.Equal(3, RttCli.ExitCode(RttCli.RttFailure.Backend));
    }
}
