using System.Net;
using System.Net.Sockets;
using System.Text;
using Ocdup.Backend;

namespace Ocdup.Tests;

/// <summary>
/// 串帧回归（2026-10-30 现场事故："读什么都返回同一条缓存旧值"）。
///
/// 现场链条（流量日志 %LOCALAPPDATA%\Ocdup\mcp-traffic.jsonl 可复核）：
///   1. 调用方在 30s 处取消一条 read_memory（MCP 宿主掐工具调用）；
///   2. TclRpcClient 的命令超时是 60s，所以抛出的是 OperationCanceledException；
///   3. BackendSession 的失效过滤器只看 IOException/SocketException/
///      ObjectDisposedException/TimeoutException —— OperationCanceledException 不在列；
///   4. 连接原样留在了 _client 里，而 OpenOCD 那条应答随后到达、排在 socket 中；
///   5. 下一条命令读走的正是它。日志里机械核对的结果：
///        请求 0xe000e018 <- 实际拿到 0xe0001004
///      之后每条都落后一帧，因为 tcl_server.c 是严格一问一答、没有序号。
///
/// 本文件不碰真实硬件：用一个"应答故意迟到"的假 TCL-RPC 服务端复现第 2~5 步。
/// </summary>
public sealed class StaleChannelTests
{
    /// <summary>
    /// 假 TCL-RPC 服务端：读命令 → 回 <c>addr: value</c>（模仿 mdw 的输出形状）。
    /// firstDelayMs 让第一条应答迟到，制造"客户端已放弃、应答却还在路上"的局面。
    /// </summary>
    private sealed class LaggingTclServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _acceptLoop;
        private readonly int _firstDelayMs;
        private readonly Func<string, string> _responder;
        private readonly List<string> _commands = new();
        private int _responded;

        public int Port { get; }
        public IReadOnlyList<string> Commands { get { lock (_commands) return _commands.ToList(); } }

        public LaggingTclServer(Func<string, string> responder, int firstDelayMs = 0)
        {
            _responder = responder;
            _firstDelayMs = firstDelayMs;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _acceptLoop = Task.Run(AcceptLoopAsync);
        }

        private async Task AcceptLoopAsync()
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
                    _ = Task.Run(() => HandleAsync(client));
                }
            }
            catch
            {
                // 取消或侦听器停止
            }
        }

        private async Task HandleAsync(TcpClient client)
        {
            using (client)
            {
                var stream = client.GetStream();
                var pending = new List<byte>();
                var buffer = new byte[4096];
                try
                {
                    while (!_cts.IsCancellationRequested)
                    {
                        var read = await stream.ReadAsync(buffer, _cts.Token).ConfigureAwait(false);
                        if (read == 0) break;
                        for (var i = 0; i < read; i++) pending.Add(buffer[i]);

                        var delimiter = pending.IndexOf(TclRpcClient.Delimiter);
                        while (delimiter >= 0)
                        {
                            var raw = Encoding.UTF8.GetString(pending.Take(delimiter).ToArray());
                            pending.RemoveRange(0, delimiter + 1);

                            var command = Unwrap(raw);
                            lock (_commands) _commands.Add(command);

                            if (_firstDelayMs > 0 && Interlocked.Increment(ref _responded) == 1)
                                await Task.Delay(_firstDelayMs, _cts.Token);

                            var bytes = Encoding.UTF8.GetBytes(_responder(command));
                            await stream.WriteAsync(bytes, _cts.Token).ConfigureAwait(false);
                            await stream.WriteAsync(new[] { TclRpcClient.Delimiter }, _cts.Token).ConfigureAwait(false);
                            await stream.FlushAsync(_cts.Token).ConfigureAwait(false);

                            delimiter = pending.IndexOf(TclRpcClient.Delimiter);
                        }
                    }
                }
                catch
                {
                    // 客户端关连接
                }
            }
        }

        private static string Unwrap(string raw)
        {
            const string prefix = "capture { ";
            var text = raw;
            if (text.StartsWith(prefix, StringComparison.Ordinal)) text = text[prefix.Length..];
            if (text.EndsWith(" }", StringComparison.Ordinal)) text = text[..^2];
            return text;
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            _listener.Stop();
            try { await _acceptLoop.ConfigureAwait(false); } catch { /* 已停止 */ }
            _cts.Dispose();
        }
    }

    /// <summary>按命令里的地址回一条 mdw 形状的应答：<c>0xADDR: deadbeef</c>。</summary>
    private static string EchoAddress(string command)
    {
        var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var addr = parts.Length > 1 ? parts[1] : "0x0";
        return addr + ": deadbeef";
    }

    private static Task<TclRpcClient> ConnectAsync(LaggingTclServer server, int commandTimeoutMs = 30000) =>
        TclRpcClient.ConnectAsync(
            "127.0.0.1", server.Port,
            connectTimeout: TimeSpan.FromSeconds(2),
            commandTimeout: TimeSpan.FromMilliseconds(commandTimeoutMs));

    /// <summary>
    /// 现场还原：取消一条命令后，同一条连接上的下一条命令绝不能拿到上一条的答案。
    ///
    /// 手工复核过现场日志：修复前这里返回的是 <c>0xe0001004: ...</c>（上一条命令的地址），
    /// 修复后连接被判脏、拒绝复用，调用方拿到明确的 StaleChannelException 去重连。
    /// </summary>
    [Fact]
    public async Task AfterCancellation_TheNextCommandNeverSeesThePreviousReply()
    {
        await using var server = new LaggingTclServer(EchoAddress, firstDelayMs: 5000);
        using var client = await ConnectAsync(server);

        using (var cts = new CancellationTokenSource())
        {
            var inFlight = client.CommandAsync("mdw 0xE0001004 1", cts.Token);
            await Task.Delay(120); // 等写落地
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => inFlight);
        }

        // 核心断言：不能把 0xe0001004 那条迟到帧当成 0x20000000 的答案还回来。
        var ex = await Assert.ThrowsAsync<StaleChannelException>(
            () => client.CommandAsync("mdw 0x20000000 4"));
        Assert.Contains("Reconnect", ex.Message);

        // 而且第二条命令根本没被发出去——发出去了就有被旧应答错配的风险。
        await Task.Delay(150);
        Assert.Equal(new[] { "mdw 0xE0001004 1" }, server.Commands.ToArray());
    }

    /// <summary>
    /// 现场对照（这条故意演"如果没有这道闸会怎样"）：同一个假服务端、同样的迟到应答，
    /// 只要客户端还肯复用这条连接，第二条命令拿到的就是第一条的地址。
    ///
    /// 为什么单独写：上面那条只证明"会抛异常"，这条证明"串帧本身确实会发生"。
    /// 将来有人把判据改宽松，上面那条可能还是绿的，这条会把事故原样摆出来。
    /// </summary>
    [Fact]
    public async Task LateReplyIsExactlyWhatWouldLeakWithoutTheGuard()
    {
        // 第一条命令用取消令牌掐掉（命令超时留长，免得第二条也被自己的超时提前打断）。
        await using var server = new LaggingTclServer(EchoAddress, firstDelayMs: 400);
        using var client = await TclRpcClient.ConnectAsync(
            "127.0.0.1", server.Port,
            connectTimeout: TimeSpan.FromSeconds(2),
            commandTimeout: TimeSpan.FromMilliseconds(5000));

        using (var cts = new CancellationTokenSource(120))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => client.CommandAsync("mdw 0xE0001004 1", cts.Token));

        Assert.True(client.IsStale);

        // 等迟到帧真的落进 socket —— 没有这一段，第二条命令只会被自己的读等到，
        // 测出来的就是超时而不是串帧（第一版就栽在这里）。
        await Task.Delay(600);

        // 强行摘掉脏标记，模拟"没有这道闸"的世界——只在这条对照里做，生产路径摸不到。
        var field = typeof(TclRpcClient).GetField("_stale",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(client, false);

        var reply = await client.CommandAsync("mdw 0x20000000 4");

        // 事故现场的形状：要 0x20000000，拿回 0xE0001004。
        Assert.StartsWith("0xE0001004:", reply);
        Assert.DoesNotContain("0x20000000", reply);
    }

    /// <summary>
    /// 重连之后必须恢复正常：脏标记只作废那一条连接，不是把整个会话焊死。
    /// </summary>
    [Fact]
    public async Task ReconnectingAfterAStaleChannelRestoresCleanReplies()
    {
        await using var server = new LaggingTclServer(EchoAddress, firstDelayMs: 5000);

        using (var dirty = await ConnectAsync(server))
        {
            using var cts = new CancellationTokenSource();
            var inFlight = dirty.CommandAsync("mdw 0xE0001004 1", cts.Token);
            await Task.Delay(120);
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => inFlight);
            await Assert.ThrowsAsync<StaleChannelException>(() => dirty.CommandAsync("mdw 0x20000000 4"));
        }

        // 新连接 = 新的 TCP 会话，服务端那边迟到的旧应答属于已经关掉的那条 socket。
        using var fresh = await ConnectAsync(server);
        var reply = await fresh.CommandAsync("mdw 0x20000000 4");

        Assert.Equal("0x20000000: deadbeef", reply);
        Assert.False(fresh.IsStale);
    }

    /// <summary>
    /// 反向钉子：正常往返不能被误判成脏，否则每条命令都要重连，性能与语义一起坏掉。
    /// </summary>
    [Fact]
    public async Task CleanRoundTripsKeepTheChannelReusable()
    {
        await using var server = new LaggingTclServer(EchoAddress);
        using var client = await ConnectAsync(server);

        Assert.Equal("0xE0001004: deadbeef", await client.CommandAsync("mdw 0xE0001004 1"));
        Assert.Equal("0x20000000: deadbeef", await client.CommandAsync("mdw 0x20000000 4"));
        Assert.Equal("0xE000E018: deadbeef", await client.CommandAsync("mdw 0xE000E018 2"));

        Assert.False(client.IsStale);
        Assert.Equal(3, server.Commands.Count);
    }
}
