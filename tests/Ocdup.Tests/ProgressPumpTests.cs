using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using Ocdup.Backend;
using Ocdup.Launcher;
using Ocdup.Tools;

namespace Ocdup.Tests;

/// <summary>
/// P1-4 进度泵的纯函数层：launcher 环形缓冲的差分切片（SliceNewLines）、
/// 进度文案截断（TruncateForProgress）、FlashProgress.Start 的开启守卫。
/// MCP progress 发送链路要真服务器会话，按仓库分工不在单测内（实机/客户端联调验收）。
/// </summary>
public sealed class ProgressPumpTests
{
    // ── SliceNewLines：游标差分读，环形缓冲只保最近 200 行的语义 ──

    [Fact]
    public void SliceNewLines_FreshBuffer_ReturnsAllAndAdvancesCursor()
    {
        long cursor = 0;
        var slice = OpenOcdLauncher.SliceNewLines(new[] { "Info: a", "Info: b", "wrote 3 bytes" }, totalLines: 3, ref cursor);

        Assert.Equal(new[] { "Info: a", "Info: b", "wrote 3 bytes" }, slice);
        Assert.Equal(3, cursor);
    }

    [Fact]
    public void SliceNewLines_IncrementalAppends_ReturnsOnlyTheTail()
    {
        long cursor = 3; // 泵已消费到第 3 行
        var slice = OpenOcdLauncher.SliceNewLines(new[] { "l1", "l2", "l3", "l4", "l5" }, totalLines: 5, ref cursor);

        Assert.Equal(new[] { "l4", "l5" }, slice);
        Assert.Equal(5, cursor);
    }

    [Fact]
    public void SliceNewLines_EvictedBeyondCapacity_ReturnsVisibleTailOnly()
    {
        // 泵落后超过一圈（新 210 行 > 缓冲 200 行）：旧行已被丢弃，可见窗口是 line51..line250。
        var snapshot = Enumerable.Range(51, 200).Select(i => $"line{i}").ToArray();
        long cursor = 40;
        var slice = OpenOcdLauncher.SliceNewLines(snapshot, totalLines: 250, ref cursor);

        Assert.Equal(200, slice.Count);
        Assert.Equal("line51", slice[0]);
        Assert.Equal("line250", slice[^1]);
        Assert.Equal(250, cursor);
    }

    [Fact]
    public void SliceNewLines_NoNewLines_ReturnsEmpty()
    {
        long cursor = 5;
        var slice = OpenOcdLauncher.SliceNewLines(new[] { "l1", "l2", "l3", "l4", "l5" }, totalLines: 5, ref cursor);

        Assert.Empty(slice);
        Assert.Equal(5, cursor);
    }

    // ── TruncateForProgress：openocd 日志行可能很长，进度消息要截断 ──

    [Fact]
    public void TruncateForProgress_ShortLine_PassesThrough()
    {
        Assert.Equal("wrote 1024 bytes in 0.5s", FlashProgress.TruncateForProgress("wrote 1024 bytes in 0.5s"));
    }

    [Fact]
    public void TruncateForProgress_LongLine_TruncatedWithEllipsis()
    {
        var line = new string('x', FlashProgress.MaxMessageChars + 50);
        var truncated = FlashProgress.TruncateForProgress(line);

        Assert.Equal(FlashProgress.MaxMessageChars + 3, truncated.Length);
        Assert.EndsWith("...", truncated);
    }

    // ── Start 守卫：没有服务器/不自拉捕获都不开泵（带 server 的路径要真会话，不在单测内）──

    [Fact]
    public void Start_WithoutServer_ReturnsNull()
    {
        Assert.Null(FlashProgress.Start(BackendSession.Instance, server: null, meta: null));
        Assert.Null(FlashProgress.Start(BackendSession.Instance, server: null, meta: new JsonObject()));
    }

    // ── 每拍自查（v0.4.18）：没有自拉后端就不报进度 ──

    [Fact]
    public async Task Pump_StaysSilent_WhileBackendIsNotOcdupsOwn()
    {
        // BackendSession.Instance 在本测试会话里没有自拉后端（CapturesProcessOutput=false）：
        // 冷启动未拉起 / 复用外部监听时环形缓冲是陈旧数据——跑满两拍也不该发出任何通知。
        var sent = 0;
        using var cts = new CancellationTokenSource(millisecondsDelay: 1700);
        var pump = new FlashProgressSession(
            BackendSession.Instance, (_, _, _) => { sent++; return Task.CompletedTask; });

        await pump.RunAsync(cts.Token);

        Assert.Equal(0, sent);
    }
}
