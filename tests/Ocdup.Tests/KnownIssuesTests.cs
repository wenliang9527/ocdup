namespace Ocdup.Tests;

/// <summary>
/// KnownIssues（v0.4.40，现场实战 session cad81f）：已知故障模式 → 可操作提示。
/// 真机原文作为回归用例——目标断电后的 "Error connecting DP: cannot read IDR"
/// 此前淹在 openocd 启动输出里，模型只能瞎猜。
/// </summary>
public sealed class KnownIssuesTests
{
    [Theory]
    [InlineData("Error connecting DP: cannot read IDR")]
    [InlineData("Error: D:\nError connecting DP: cannot read IDR\n--- more output ---")]
    [InlineData("swd init failed: cannot read idr")] // 大小写归一
    public void HintFor_DeadDebugPort_CarriesThePowerHint(string output)
    {
        var hint = KnownIssues.HintFor(output);
        Assert.NotNull(hint);
        Assert.StartsWith("NOTE:", hint);
        Assert.Contains("unpowered", hint);
        Assert.Contains("SWD", hint);
    }

    [Theory]
    [InlineData("connection closed by OpenOCD")]
    [InlineData("Failed to read memory: connection closed by openocd")]
    public void HintFor_ChannelDropped_PointsAtReconnectAndLog(string output)
    {
        var hint = KnownIssues.HintFor(output);
        Assert.NotNull(hint);
        Assert.Contains("restarts it automatically", hint);
        Assert.Contains("backend_log", hint);
    }

    [Fact]
    public void HintFor_SocketTeardown_MentionsReadBack()
    {
        var hint = KnownIssues.HintFor("Remote host forcibly closed an existing connection."); // Windows socket 原文
        Assert.NotNull(hint);
        Assert.Contains("reconnects automatically", hint);
        Assert.Contains("read the value back", hint);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0x20000000: 20000000")]
    [InlineData("verified 4096 bytes in 0.01s")]
    [InlineData("Error: flash bank missing")] // 未知错误不硬凑提示
    public void HintFor_UnknownOrEmpty_ReturnsNull(string output)
        => Assert.Null(KnownIssues.HintFor(output));

    [Fact]
    public void HintFor_FirstMatchWins()
    {
        // 同文命中多条时取第一条（DP 断电优先于通道类），行为可预期
        var hint = KnownIssues.HintFor("cannot read idr ... connection closed by openocd");
        Assert.Contains("unpowered", hint!);
    }
}
