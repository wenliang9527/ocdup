using System.Net;
using System.Net.Sockets;
using System.Text;
using Ocdup.Backend;

namespace Ocdup.Tests;

/// <summary>
/// 控制通道回归（设计文档 13 节：假 socket、脚本化响应）。
/// 覆盖：capture 包装、0x1a 分帧、跨 TCP 分段的响应组装、并发调用的串行化、读超时。
/// </summary>
public sealed class TclRpcClientTests
{
    /// <summary>
    /// 假 TCL-RPC 服务器：按 0x1a 分帧读命令 → 调 responder 生成响应 → 以 0x1a 结尾写回。
    /// respond=false 用来制造「服务端静默」以触发读超时；chunkSize>0 用来制造跨包响应。
    /// </summary>
    private sealed class FakeTclServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _acceptLoop;
        private readonly Func<string, string> _responder;
        private readonly bool _respond;
        private readonly int _chunkSize;
        private readonly int _firstResponseDelayMs;
        private readonly List<string> _commands = new();
        private readonly List<string> _rawFrames = new();
        private int _responded;

        public int Port { get; }
        public IReadOnlyList<string> Commands { get { lock (_commands) return _commands.ToList(); } }
        public IReadOnlyList<string> RawFrames { get { lock (_rawFrames) return _rawFrames.ToList(); } }

        public FakeTclServer(Func<string, string>? responder = null, bool respond = true, int chunkSize = 0,
            int firstResponseDelayMs = 0)
        {
            _responder = responder ?? (_ => "ok");
            _respond = respond;
            _chunkSize = chunkSize;
            _firstResponseDelayMs = firstResponseDelayMs;
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
                            lock (_rawFrames) _rawFrames.Add(raw);

                            if (_respond)
                            {
                                // 只让第一条应答迟到（模拟"客户端已经超时放弃，服务端这才回话"）：
                                // 这条迟到帧会留在 socket 里，正是串帧事故的原料。
                                if (_firstResponseDelayMs > 0 && Interlocked.Increment(ref _responded) == 1)
                                    await Task.Delay(_firstResponseDelayMs, _cts.Token);
                                await WriteResponseAsync(stream, _responder(command)).ConfigureAwait(false);
                            }

                            delimiter = pending.IndexOf(TclRpcClient.Delimiter);
                        }
                    }
                }
                catch
                {
                    // 连接被客户端关闭
                }
            }
        }

        private async Task WriteResponseAsync(NetworkStream stream, string response)
        {
            var bytes = Encoding.UTF8.GetBytes(response);
            var terminator = new[] { TclRpcClient.Delimiter };

            if (_chunkSize <= 0)
            {
                await stream.WriteAsync(bytes, _cts.Token).ConfigureAwait(false);
                await stream.WriteAsync(terminator, _cts.Token).ConfigureAwait(false);
                await stream.FlushAsync(_cts.Token).ConfigureAwait(false);
                return;
            }

            for (var offset = 0; offset < bytes.Length; offset += _chunkSize)
            {
                var count = Math.Min(_chunkSize, bytes.Length - offset);
                await stream.WriteAsync(bytes.AsMemory(offset, count), _cts.Token).ConfigureAwait(false);
                await stream.FlushAsync(_cts.Token).ConfigureAwait(false);
                await Task.Delay(2);
            }

            await stream.WriteAsync(terminator, _cts.Token).ConfigureAwait(false);
            await stream.FlushAsync(_cts.Token).ConfigureAwait(false);
        }

        /// <summary>去掉 capture { … } 外壳，取出真实命令。</summary>
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

    private static Task<TclRpcClient> ConnectAsync(FakeTclServer server, int commandTimeoutMs = 5000) =>
        TclRpcClient.ConnectAsync(
            "127.0.0.1", server.Port,
            connectTimeout: TimeSpan.FromSeconds(2),
            commandTimeout: TimeSpan.FromMilliseconds(commandTimeoutMs));

    [Fact]
    public async Task CommandAsync_WrapsInCaptureAndReturnsResponse()
    {
        await using var server = new FakeTclServer(cmd => $"output for {cmd}");
        using var client = await ConnectAsync(server);

        var response = await client.CommandAsync("mdw 0x20000000 4");

        Assert.Equal("output for mdw 0x20000000 4", response);
        Assert.Equal("mdw 0x20000000 4", Assert.Single(server.Commands));
        Assert.StartsWith("capture { ", Assert.Single(server.RawFrames));
    }

    [Fact]
    public async Task CommandAsync_AssemblesResponseAcrossManyTcpSegments()
    {
        var payload = string.Join("\n", Enumerable.Range(0, 1200).Select(i => $"0x2000{i:x4}: deadbeef {i:x8} cafebabe"));
        Assert.True(payload.Length > 16384, "测试数据需大于客户端缓冲区，才能逼出跨多次 ReadAsync 的组装路径");

        await using var server = new FakeTclServer(_ => payload, chunkSize: 1000);
        using var client = await ConnectAsync(server);

        var response = await client.CommandAsync("mdw 0x20000000 1200");

        Assert.Equal(payload, response);
    }

    [Fact]
    public async Task CommandAsync_SerializesConcurrentCalls()
    {
        await using var server = new FakeTclServer(cmd =>
        {
            Thread.Sleep(3); // 拉长响应窗口，串行化被破坏时更容易错配
            return "reply:" + cmd;
        });
        using var client = await ConnectAsync(server);

        var responses = await Task.WhenAll(
            Enumerable.Range(0, 20).Select(i => client.CommandAsync($"probe {i}")));

        for (var i = 0; i < responses.Length; i++)
            Assert.Equal($"reply:probe {i}", responses[i]);

        var expected = Enumerable.Range(0, 20).Select(i => $"probe {i}").OrderBy(x => x).ToList();
        Assert.Equal(expected, server.Commands.OrderBy(x => x).ToList());
    }

    [Fact]
    public async Task CommandAsync_ReusesConnectionForSequentialCalls()
    {
        await using var server = new FakeTclServer(cmd => cmd.ToUpperInvariant());
        using var client = await ConnectAsync(server);

        Assert.Equal("FLASH BANKS", await client.CommandAsync("flash banks"));
        Assert.Equal("TARGETS", await client.CommandAsync("targets"));

        Assert.Equal(2, server.Commands.Count);
    }

    [Fact]
    public async Task CommandAsync_TimesOutWhenServerStaysSilent()
    {
        await using var server = new FakeTclServer(respond: false);
        using var client = await ConnectAsync(server, commandTimeoutMs: 300);

        await Assert.ThrowsAsync<TimeoutException>(() => client.CommandAsync("mdw 0x0 1"));
    }

    // ── 串帧事故（2026-10-30 现场：读什么都返回同一条旧值）──
    //
    // 事故链：调用方在 30s 处取消 → 请求已写出、连接没作废 → OpenOCD 的应答稍后
    // 到达并排在 socket 里 → 下一条命令把它当成自己的答案读走。tcl_server.c 严格
    // 一问一答且没有序号，所以错位会一直向后传染。
    // 下面三条分别钉住：脏标记、拒绝复用、以及"没有它就会错"的现场对照。

    [Fact]
    public async Task CommandAsync_CancelledMidFlight_MarksTheChannelStale()
    {
        // 服务端第一条应答迟到，客户端等不及先取消——正是"请求已写出、应答还在路上"。
        await using var server = new FakeTclServer(cmd => "reply:" + cmd, firstResponseDelayMs: 5000);
        using var client = await ConnectAsync(server, commandTimeoutMs: 30000);

        using var cts = new CancellationTokenSource();
        var inFlight = client.CommandAsync("mdw 0xE0001004 1", cts.Token);
        await Task.Delay(120); // 让写真正落地，别在写之前就取消
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => inFlight);

        Assert.True(client.IsStale,
            "一次没走完的往返必须把连接标脏：应答位置已经不确定，复用就会串帧");
    }

    [Fact]
    public async Task CommandAsync_RefusesToReuseAStaleChannel()
    {
        await using var server = new FakeTclServer(cmd => "reply:" + cmd, firstResponseDelayMs: 5000);
        using var client = await ConnectAsync(server, commandTimeoutMs: 30000);

        using (var cts = new CancellationTokenSource())
        {
            var inFlight = client.CommandAsync("mdw 0xE0001004 1", cts.Token);
            await Task.Delay(120);
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => inFlight);
        }

        // 这条连接上"一问一答"已经不可信：第二条命令必须被挡下，而不是拿旧应答糊弄过去。
        await Assert.ThrowsAsync<StaleChannelException>(() => client.CommandAsync("mdw 0x20000000 4"));

        // 挡下之后服务端不该收到第二条命令——真要收到了，旧应答就会被错配给它。
        await Task.Delay(150);
        Assert.Equal("mdw 0xE0001004 1", Assert.Single(server.Commands));
    }

    [Fact]
    public async Task CommandAsync_TimeoutAlsoMarksTheChannelStale()
    {
        // 超时和取消是同一类：都是"半次往返"。漏掉任何一类，错位就照旧传染。
        await using var server = new FakeTclServer(respond: false);
        using var client = await ConnectAsync(server, commandTimeoutMs: 300);

        await Assert.ThrowsAsync<TimeoutException>(() => client.CommandAsync("mdw 0x0 1"));

        Assert.True(client.IsStale);
        await Assert.ThrowsAsync<StaleChannelException>(() => client.CommandAsync("version"));
    }

    [Fact]
    public async Task CommandAsync_CleanRoundTripDoesNotMarkTheChannelStale()
    {
        // 反向钉子：正常往返绝不能被误标脏，否则每条命令都要重连一次。
        await using var server = new FakeTclServer(cmd => "reply:" + cmd);
        using var client = await ConnectAsync(server);

        Assert.Equal("reply:a", await client.CommandAsync("a"));
        Assert.Equal("reply:b", await client.CommandAsync("b"));

        Assert.False(client.IsStale);
        Assert.Equal(2, server.Commands.Count);
    }

    [Fact]
    public async Task ConnectAsync_FailsWhenNothingIsListening()
    {
        await Assert.ThrowsAnyAsync<Exception>(() =>
            TclRpcClient.ConnectAsync("127.0.0.1", 1, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)));
    }
}
