using System.IO;
using System.Net.Sockets;
using System.Text;

namespace Ocdup.Backend;

/// <summary>
/// OpenOCD TCL-RPC 控制通道（设计文档 4.1）：
/// 连 tcl_port（默认 6666）而不是 telnet 4444——telnet 会回显、混入异步日志；
/// TCL-RPC 的每条响应以单个 0x1a 分隔，命令用 capture {…} 包一层收回日志文本。
/// 所有请求经内部信号量串行化（不变量 C1），保证响应与命令一一对应。
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
        await _stream.WriteAsync(frame, ct).ConfigureAwait(false);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_commandTimeout);

        // 累积原始字节、最后统一解码：避免多字节 UTF-8 字符被 TCP 分段切裂。
        using var payload = new MemoryStream(256);
        try
        {
            while (true)
            {
                var read = await _stream.ReadAsync(_buffer.AsMemory(), cts.Token).ConfigureAwait(false);
                if (read == 0)
                    throw new IOException("connection closed by OpenOCD");

                var end = Array.IndexOf(_buffer, Delimiter, 0, read);
                if (end >= 0)
                {
                    payload.Write(_buffer, 0, end);
                    break;
                }
                payload.Write(_buffer, 0, read);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"no TCL-RPC response within {_commandTimeout.TotalSeconds:0.#}s");
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
