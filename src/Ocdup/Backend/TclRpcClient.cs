using System.IO;
using System.Net.Sockets;
using System.Text;

namespace Ocdup.Backend;

/// <summary>
/// 通道上有一次往返半途放弃（超时 / 调用方取消 / 写读失败）：请求可能已经写出，
/// 应答却还没收完，或者已经排在 socket 里没人取。此时这条连接 <b>不可复用</b>。
///
/// 为什么只能断链而不能就地恢复：tcl_server.c 的 tcl_input 是严格一问一答
/// （收到 0x1a 就回一帧），协议里没有序号，客户端无从判断收到的这帧属于哪条命令。
/// 一旦有一条应答"晚了一步"，之后每条命令拿到的都是上一条的答案——现象就是
/// "读什么都返回同一个值"，而且会无限向后传染。唯一能回到确定状态的做法是重新建连。
/// </summary>
public sealed class StaleChannelException : IOException
{
    public StaleChannelException(string message) : base(message) { }
}

/// <summary>
/// OpenOCD TCL-RPC 控制通道（设计文档 4.1）：
/// 连 tcl_port（默认 6666）而不是 telnet 4444——telnet 会回显、混入异步日志；
/// TCL-RPC 的每条响应以单个 0x1a 分隔，命令用 capture {…} 包一层收回日志文本。
/// 所有请求经内部信号量串行化（不变量 C1），保证响应与命令一一对应。
///
/// "一一对应"只在本对象内部成立：一旦某次往返没走完（超时/取消），这条连接上的
/// 对应关系就永久失效，此后本对象拒绝再发命令（见 <see cref="IsStale"/>）。
/// </summary>
public sealed class TclRpcClient : IDisposable
{
    /// <summary>响应分隔符（OpenOCD 的 TCL-RPC 协议终止字节）。</summary>
    public const byte Delimiter = 0x1A;

    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly byte[] _buffer = new byte[8192];
    private readonly TimeSpan _commandTimeout;
    private bool _disposed;

    /// <summary>
    /// 有过一次没走完的往返（请求可能已写出、应答留在 socket 里）：这条连接
    /// 上的"一问一答"对应关系已经不可信，任何后续命令都可能读到上一条的应答。
    /// 置起后 <see cref="CommandAsync"/> 一律抛 <see cref="StaleChannelException"/>，
    /// 逼调用方丢弃本对象、重新建连。
    /// </summary>
    private volatile bool _stale;

    /// <summary>这条连接是否已被一次失败的往返污染（不可复用）。</summary>
    public bool IsStale => _stale;

    private TclRpcClient(TcpClient client, TimeSpan commandTimeout)
    {
        _client = client;
        _client.NoDelay = true;
        _stream = client.GetStream();
        _commandTimeout = commandTimeout;
    }

    /// <summary>
    /// 建立到 OpenOCD TCL-RPC 端口的连接。
    /// connectTimeout 管握手，commandTimeout 管每条命令等响应——两者必须分开：
    /// 烧录一条 program 可能跑几十秒，不能拿握手用的短超时去掐它。
    /// </summary>
    public static async Task<TclRpcClient> ConnectAsync(
        string host, int port, TimeSpan connectTimeout, TimeSpan commandTimeout, CancellationToken ct = default)
    {
        var client = new TcpClient();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(connectTimeout);
            await client.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
            return new TclRpcClient(client, commandTimeout);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            client.Dispose();
            throw new TimeoutException($"connect to {host}:{port} timed out after {connectTimeout.TotalSeconds:0.#}s");
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 发送一条 TCL 命令并等待完整响应（读到 0x1a 为止）。内部串行化，可并发调用。
    /// 命令会被包成 capture { … }，这样 OpenOCD 把命令的输出（含错误文本）作为响应返回。
    /// </summary>
    public async Task<string> CommandAsync(string command, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(command);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // 闸内检查：一次失败的往返把它置起后，后来者必须被挡在这里而不是去复用。
            if (_stale)
                throw new StaleChannelException(
                    "the TCL-RPC connection was left in an unknown state by an earlier timeout or cancellation " +
                    "(a late reply may still be queued), so reusing it could hand this command the previous " +
                    "command's response. Reconnect before issuing more commands.");

            return await RoundTripAsync(command, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<string> RoundTripAsync(string command, CancellationToken ct)
    {
        var body = Encoding.UTF8.GetBytes("capture { " + command + " }");
        var frame = new byte[body.Length + 1];
        body.CopyTo(frame, 0);
        frame[^1] = Delimiter;

        // 命令超时同时管写与读：写方向同样可能因对端异常挂住（本机 TCP 概率极低，但闸要对称）。
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_commandTimeout);

        // 累积原始字节、最后统一解码：避免多字节 UTF-8 字符被 TCP 分段切裂。
        using var payload = new MemoryStream(256);
        var completed = false;
        try
        {
            await _stream.WriteAsync(frame, cts.Token).ConfigureAwait(false);
            while (true)
            {
                var read = await _stream.ReadAsync(_buffer.AsMemory(), cts.Token).ConfigureAwait(false);
                if (read == 0)
                    throw new IOException("connection closed by OpenOCD");

                var end = Array.IndexOf(_buffer, Delimiter, 0, read);
                if (end >= 0)
                {
                    payload.Write(_buffer, 0, end);
                    completed = true;
                    break;
                }
                payload.Write(_buffer, 0, read);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"no TCL-RPC response within {_commandTimeout.TotalSeconds:0.#}s");
        }
        finally
        {
            // 无论如何没读到终止符：请求已写出（或写出了一半），应答的位置就不再确定。
            // 取消/超时/断流/解码失败全部落在这里——置起脏标记，让这条连接彻底作废。
            if (!completed)
                _stale = true;
        }

        return Encoding.UTF8.GetString(payload.GetBuffer(), 0, (int)payload.Length);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _stream.Dispose(); } catch { /* 关闭路径上的异常无需上报 */ }
        _client.Dispose();
        _gate.Dispose();
    }
}
