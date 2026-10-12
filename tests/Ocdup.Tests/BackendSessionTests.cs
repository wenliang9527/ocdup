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

    // ── P0-3 边界定案(v0.4.28):disconnect 是后端生命周期操作——不向目标发执行控制
    //    命令,只过 read_only 总闸,不进 allow_exec;halt 后断开会把 halted 的目标留给
    //    下一个探针使用者,这个残余用停机前报告收口(纯函数层钉死组装规则)。──

    [Fact]
    public void StopReport_AppendsHaltedNoteOnlyWhenOurBackendWasReleased()
    {
        var released = BackendSession.StopReport(wasUp: true, stillUp: false, "127.0.0.1:6666",
            BackendSession.HaltedTargetNote);
        Assert.Contains("backend stopped", released);
        Assert.Contains("halted", released);
        Assert.Contains("reset run", released); // 可操作指引(C3):要保持运行,断开前先恢复

        // 外部实例杀不动 / 本来就没跑:没停任何东西,附 halted 提示反而是误导
        var foreign = BackendSession.StopReport(true, true, "127.0.0.1:6666", BackendSession.HaltedTargetNote);
        Assert.Contains("still running", foreign);
        Assert.DoesNotContain("reset run", foreign);

        var wasDown = BackendSession.StopReport(false, false, "127.0.0.1:6666", BackendSession.HaltedTargetNote);
        Assert.Contains("nothing to disconnect", wasDown);
        Assert.DoesNotContain("reset run", wasDown);
    }

    [Fact]
    public void StopReport_WithoutHaltedProbe_IsPlainVerdict()
    {
        var text = BackendSession.StopReport(true, false, "127.0.0.1:6666", haltedNote: null);
        Assert.Contains("backend stopped", text);
        Assert.DoesNotContain("halted", text);
    }

    [Fact]
    public void HaltedTargetNote_IsActionable()
    {
        Assert.Contains("stays halted", BackendSession.HaltedTargetNote);
        Assert.Contains("resume", BackendSession.HaltedTargetNote);
    }
}
