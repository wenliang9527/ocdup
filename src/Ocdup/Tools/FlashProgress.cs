using System.Diagnostics;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Ocdup.Backend;

namespace Ocdup.Tools;

/// <summary>
/// 长耗时操作的进度泵（P1-4）：周期性差分读 launcher 环形缓冲的增量行，以 MCP 标准
/// progress notification（notifications/progress）推回调用方——仅当请求带了 progressToken
/// 才发（调用方主动要进度；不带就完全静默，零开销）。launcher 的 log→traffic 镜像通道
/// 不受影响（面板进度条继续吃那条路）。环形缓冲只对 ocdup 自拉的后端有捕获：复用外部
/// 监听时直接不开泵。进度是尽力而为的旁路：任何发送失败都静默收工，绝不影响主操作。
/// </summary>
internal static class FlashProgress
{
    /// <summary>轮询节奏：openocd 的擦除/wrote 行足够密，亚秒级即可感知。</summary>
    internal static readonly TimeSpan PollPeriod = TimeSpan.FromMilliseconds(750);
    internal const int MaxMessageChars = 200;

    /// <summary>
    /// 三重守卫：要发通知得有 server；要发得有 progressToken（调用方主动要进度，按 MCP 规范
    /// 是 _meta.progressToken，数字或字符串）；要有真实增量行得是 ocdup 自拉的后端（外部监听
    /// 的缓冲是陈旧数据）。任一不满足返回 null。
    /// </summary>
    public static FlashProgressSession? Start(BackendSession session, McpServer? server, JsonObject? meta)
    {
        if (server is null || !session.CapturesProcessOutput)
            return null;

        var token = TryGetToken(meta);
        if (token is null)
            return null;

        // token 在闭包里保持其真实类型（ProgressToken 值的拷贝）：ProgressNotificationParams
        // 的赋值由编译器对着 SDK 的实际签名校验，不在泵里做任何类型猜测。
        return new FlashProgressSession(session, (line, progressCount, ct) => server.SendNotificationAsync(
            "notifications/progress",
            new ProgressNotificationParams
            {
                ProgressToken = token.Value,
                Progress = new ProgressNotificationValue { Progress = progressCount, Message = FlashProgress.TruncateForProgress(line) },
            },
            McpJsonUtilities.DefaultOptions,
            ct));
    }

    /// <summary>从请求的 _meta（SDK 里是裸 JsonObject）取 progressToken：规范允许数字或字符串。</summary>
    private static ProgressToken? TryGetToken(JsonObject? meta)
    {
        if (meta is null || !meta.TryGetPropertyValue("progressToken", out var node) || node is not JsonValue value)
            return null;

        try
        {
            if (value.TryGetValue<long>(out var number)) return new ProgressToken(number);
            if (value.TryGetValue<string>(out var text)) return new ProgressToken(text);
        }
        catch
        {
            // 形态不对（对象/数组等）：当调用方没要进度处理
        }

        return null;
    }

    /// <summary>进度消息截断（纯函数，可单测）：openocd 日志行可能很长，notification 里只带前 200 字符。</summary>
    public static string TruncateForProgress(string line) =>
        line.Length <= MaxMessageChars ? line : line[..MaxMessageChars] + "...";
}

/// <summary>一次长耗时操作的进度泵会话：RunAsync 随主操作的取消/完成而结束。</summary>
internal sealed class FlashProgressSession
{
    private readonly BackendSession _session;
    private readonly Func<string, long, CancellationToken, Task> _send;
    private long _cursor;
    private long _count;

    public FlashProgressSession(BackendSession session, Func<string, long, CancellationToken, Task> send)
    {
        _session = session;
        _send = send;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        _cursor = _session.TotalLinesAppended; // 只报本次操作期间的增量，不回放历史

        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(FlashProgress.PollPeriod, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }

            IReadOnlyList<string> lines;
            try { lines = _session.TakeNewLines(ref _cursor); }
            catch { return; }

            foreach (var line in lines)
            {
                _count++;
                try
                {
                    await _send(line, _count, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { return; }
                catch { return; } // 进度没有观众了（客户端断开/不支持）：静默收工，绝不影响主操作
            }
        }
    }
}
