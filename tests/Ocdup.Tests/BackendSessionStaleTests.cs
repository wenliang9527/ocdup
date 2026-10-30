using System.Net;
using System.Net.Sockets;
using System.Text;
using Ocdup.Backend;
using Ocdup.Config;

namespace Ocdup.Tests;

/// <summary>
/// 会话层串帧回归（2026-10-30 现场事故的真正触发层）。
///
/// 为什么必须单独测这一层：<see cref="StaleChannelTests"/> 那批用例直接调
/// <c>TclRpcClient.CommandAsync</c>，<b>绕过了 BackendSession</b>。而现场事故的
/// 触发点在 BackendSession 的异常过滤器上：
///
///   catch (Exception ex) when (ex is IOException or SocketException
///                              or ObjectDisposedException or TimeoutException)
///
/// 调用方取消时抛的是 <see cref="OperationCanceledException"/>，<b>不在这四个类型里</b>，
/// 于是连接既没被作废、也没被丢弃，原样留在了 <c>_client</c> 里。OpenOCD 的应答
/// 随后到达并排在 socket 中，下一条命令读走的正是它。日志里机械核对的结果：
///     请求 0xe000e018 <- 实际拿到 0xe0001004
/// 之后每条都落后一帧（tcl_server.c 严格一问一答、无序号）。
///
/// 变异验证记录：把 BackendSession 里"失败即作废"那段去掉后，
/// <b>只有本文件的用例变红</b>，StaleChannelTests 那 14 条全绿——
/// 这正是本文件存在的理由。
/// </summary>
public sealed class BackendSessionStaleTests : IDisposable
{
    private readonly string _dir;

    public BackendSessionStaleTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ocdup-stale-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* 清理失败不影响结论 */ }
    }

    /// <summary>写一份指向假端口的 ocd.json，让 BackendSession 连它、且不会去拉起真 openocd。</summary>
    private OcdConfig ConfigFor(int port)
    {
        var path = Path.Combine(_dir, "ocd.json");
        File.WriteAllText(path, $$"""
        {
          "host": "127.0.0.1",
          "port": {{port}},
          "command_timeout_seconds": 30,
          "permissions": { "read_only": false, "allow_exec": true }
        }
        """);
        var cfg = OcdConfig.LoadFrom(path);
        Assert.Equal(port, cfg.Port); // 配置真的生效了，否则后面的断言全是假的
        return cfg;
    }

    /// <summary>
    /// 假 TCL-RPC 服务端：回 <c>addr: deadbeef</c>（模仿 mdw 输出）。
    /// <paramref name="slowAddr"/> 指定的那条命令延迟应答，制造"客户端已放弃、应答还在路上"。
    /// 用地址而不是"第几条"来选：预热命令会先占掉序号，按序号选会挑错对象（踩过）。
    /// </summary>
    private sealed class LaggingServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _acceptLoop;
        private readonly int _firstDelayMs;
        private readonly string? _slowAddr;
        private readonly List<string> _commands = new();

        public int Port { get; }
        public IReadOnlyList<string> Commands { get { lock (_commands) return _commands.ToList(); } }

        public LaggingServer(int firstDelayMs = 0, string? slowAddr = null)
        {
            _firstDelayMs = firstDelayMs;
            _slowAddr = slowAddr;
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
                    var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                    _ = Task.Run(() => HandleAsync(client));
                }
            }
            catch { /* 已停止 */ }
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
                        var read = await stream.ReadAsync(buffer, _cts.Token);
                        if (read == 0) break;
                        for (var i = 0; i < read; i++) pending.Add(buffer[i]);

                        var delimiter = pending.IndexOf(TclRpcClient.Delimiter);
                        while (delimiter >= 0)
                        {
                            var raw = Encoding.UTF8.GetString(pending.Take(delimiter).ToArray());
                            pending.RemoveRange(0, delimiter + 1);

                            var command = Unwrap(raw);
                            lock (_commands) _commands.Add(command);

                            var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                            var addr = parts.Length > 1 ? parts[1] : "0x0";

                            // 只让目标地址那条慢：它的应答会在客户端放弃之后才到。
                            if (_firstDelayMs > 0 && _slowAddr is not null
                                && string.Equals(addr, _slowAddr, StringComparison.OrdinalIgnoreCase))
                                await Task.Delay(_firstDelayMs, _cts.Token);

                            var bytes = Encoding.UTF8.GetBytes(addr + ": deadbeef");
                            await stream.WriteAsync(bytes, _cts.Token);
                            await stream.WriteAsync(new[] { TclRpcClient.Delimiter }, _cts.Token);
                            await stream.FlushAsync(_cts.Token);

                            delimiter = pending.IndexOf(TclRpcClient.Delimiter);
                        }
                    }
                }
                catch { /* 客户端关连接 */ }
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
            try { await _acceptLoop; } catch { /* 已停止 */ }
            _cts.Dispose();
        }
    }

    /// <summary>
    /// 现场还原：调用方取消一条命令后，<b>同一个会话</b>的下一条命令不能拿到上一条的答案。
    ///
    /// 修复前的行为（已实测复现）：第二条命令正常返回，内容是 <c>0xE0001004: ...</c>，
    /// 也就是第一条命令的地址。修复后会话丢弃连接并重连，第二条拿到的是自己的答案。
    /// </summary>
    [Fact]
    public async Task SessionDiscardsTheChannelWhenTheCallerCancels()
    {
        await using var server = new LaggingServer(firstDelayMs: 800, slowAddr: "0xE0001004");
        var cfg = ConfigFor(server.Port);
        var session = BackendSession.Instance;

        // 单例会话在测试间共享：先清掉上一条测试可能留下的连接，免得上一条的连接
        // 被这里复用、命令发到上一条测试的端口上去（会变成顺序依赖的假绿）。
        session.Invalidate();
        try
        {
            // 关键前置：先跑一条正常命令，把连接**真正建立起来**。
            // 少了这一步，取消会发生在 AcquireAsync/ProbePort 阶段——那时 _client 还是 null，
            // 根本没有"脏连接"可留，测试就变成了空转（第一版正是栽在这里：
            // 变异注入后照样绿，因为压根没走到被变异的那行）。
            var warmup = await session.ExecuteAsync(cfg, "mdw 0x08000000 1", startIfNeeded: false,
                CancellationToken.None, op: "read_memory");
            Assert.StartsWith("0x08000000:", warmup.Trim());
            Assert.True(session.ChannelState.Connected, "预热之后连接必须在位上，否则下面的取消测不到任何东西");

            // 第一条：让它在写完之后被取消（OpenOCD 那边稍后仍会回话）。
            using (var cts = new CancellationTokenSource(150))
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    () => session.ExecuteAsync(cfg, "mdw 0xE0001004 1", startIfNeeded: false, cts.Token, op: "read_memory"));
            }

            // 被取消打断的连接不配继续叫 connected：它上面还挂着一帧没人认领的应答。
            // 这条断言钉住 ExecuteOnceAsync 的"失败即作废"——没有它，下次命令也能靠
            // TclRpcClient 的脏标记兜住，但代价是多绕一轮重连重放，而且写类 op 会在
            // 那一轮里报"命令可能已生效"的误报。
            Assert.False(session.ChannelState.Connected,
                "取消之后会话必须立刻丢弃连接，而不是留一条脏连接等下次命令踩");

            // 等迟到帧真的落进 socket，否则测出来只会是"它还没到"。
            await Task.Delay(900);

            // 核心断言：第二条命令必须拿到自己的答案。
            var reply = await session.ExecuteAsync(cfg, "mdw 0x20000000 4", startIfNeeded: false,
                CancellationToken.None, op: "read_memory");

            Assert.StartsWith("0x20000000:", reply.Trim());
            Assert.DoesNotContain("0xE0001004", reply);
        }
        finally
        {
            session.Invalidate();
        }
    }

    /// <summary>
    /// 反向钉子：正常命令之后连接要能继续复用，不能因为"怕串帧"就每条都重连。
    /// </summary>
    [Fact]
    public async Task SessionKeepsReusingTheChannelAfterCleanCommands()
    {
        await using var server = new LaggingServer();
        var cfg = ConfigFor(server.Port);
        var session = BackendSession.Instance;

        var first = await session.ExecuteAsync(cfg, "mdw 0xE0001004 1", startIfNeeded: false,
            CancellationToken.None, op: "read_memory");
        var second = await session.ExecuteAsync(cfg, "mdw 0x20000000 4", startIfNeeded: false,
            CancellationToken.None, op: "read_memory");

        Assert.StartsWith("0xE0001004:", first.Trim());
        Assert.StartsWith("0x20000000:", second.Trim());

        // 仍然是一条连接上完成的（服务端没看到第二次握手，只看到两条命令）。
        Assert.Equal(2, server.Commands.Count);
    }
}
