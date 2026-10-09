using Ocdup.Backend;

namespace Ocdup.Tests;

/// <summary>
/// disconnect 的纯函数层（v0.4.6）：断开结果文案的三态（真断了 / 本来就没跑 /
/// 端口上是外部实例、杀不动）。工具层要碰进程与端口，交给 e2e 假后端覆盖。
/// </summary>
public sealed class BackendSessionTests
{
    [Fact]
    public void StopVerdict_StoppedOurBackend_SaysReleased()
    {
        var text = BackendSession.StopVerdict(wasUp: true, stillUp: false, "127.0.0.1:6666");

        Assert.Contains("backend stopped", text);
        Assert.Contains("released for other software", text);
        Assert.Contains("connect", text); // 告诉模型/用户怎么拉回来
    }

    [Fact]
    public void StopVerdict_AlreadyDown_IsANoOpExplanation()
    {
        var text = BackendSession.StopVerdict(wasUp: false, stillUp: false, "127.0.0.1:6666");

        Assert.Contains("was not running", text);
        Assert.Contains("nothing to disconnect", text);
    }

    [Fact]
    public void StopVerdict_ForeignListener_SaysItCannotStopIt()
    {
        var text = BackendSession.StopVerdict(wasUp: true, stillUp: true, "127.0.0.1:6666");

        // 关键：不能让人以为探针已经释放
        Assert.Contains("still running", text);
        Assert.Contains("started outside ocdup", text);
        Assert.DoesNotContain("released", text);
    }

    [Fact]
    public void StopVerdict_CarriesTheEndpoint()
    {
        Assert.Contains("127.0.0.1:7777", BackendSession.StopVerdict(true, false, "127.0.0.1:7777"));
    }

    // ── P1-5:断链自动重放(只读白名单)+ 空闲心跳 ──

    [Theory]
    [InlineData("status")]
    [InlineData("connect")]
    [InlineData("flash_info")]
    [InlineData("identify_chip")]
    [InlineData("read_memory")]
    [InlineData("verify_image")]
    [InlineData("dump_memory")]
    [InlineData("read_variable")]
    public void ReplaySafeOps_CoverPureReads(string op)
    {
        // 目标侧纯读:命令可能已在中断前生效,重放不产生二次效应。
        Assert.True(BackendSession.IsReplaySafe(op));
    }

    [Theory]
    [InlineData("flash_write")]         // program 可能已生效——重放 = 二次擦写
    [InlineData("flash_erase_sector")]  // 破坏性,绝不重放
    [InlineData("run_command")]         // 内容不可静态判定,保守不重放
    [InlineData("halt")]
    [InlineData("resume")]
    [InlineData("reset")]
    [InlineData("write_memory")]
    [InlineData("write_variable")]
    [InlineData("watch")]               // CLI 自带 30 拍重试,不走会话层重放
    public void ReplaySafeOps_ExcludeStateChangingOps(string op)
    {
        Assert.False(BackendSession.IsReplaySafe(op));
    }

    [Fact]
    public void ShouldProbe_OnlyWhenConnectedAndIdlePastThreshold()
    {
        Assert.False(BackendSession.ShouldProbe(connected: false, TimeSpan.FromSeconds(999)));
        Assert.False(BackendSession.ShouldProbe(connected: true, TimeSpan.FromSeconds(29)));
        Assert.True(BackendSession.ShouldProbe(connected: true, TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void FormatChannelState_ReportsConnectedIdleOrNotConnected()
    {
        var up = BackendSession.FormatChannelState(connected: true, TimeSpan.FromSeconds(12));
        Assert.Contains("connected", up);
        Assert.Contains("12s", up);

        var down = BackendSession.FormatChannelState(connected: false, TimeSpan.FromSeconds(12));
        Assert.Contains("not connected", down);
        Assert.Contains("reconnect", down);
    }
}
