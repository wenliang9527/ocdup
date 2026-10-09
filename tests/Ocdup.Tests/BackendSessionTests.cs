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
}
