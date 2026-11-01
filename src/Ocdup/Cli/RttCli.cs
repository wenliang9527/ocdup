using System.Globalization;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Ocdup.Backend;
using Ocdup.Config;
using Ocdup.Tools;

namespace Ocdup.Cli;

/// <summary>
/// `ocdup rtt`（v0.4.19）：SEGGER RTT 实时读取 CLI。独立子命令而非 MCP 工具——
/// 与 watch 同一理由（C1）：RTT 是长时连续流，占死 MCP 串行通道毫无意义，而
/// stdout 重定向到文件/管道才是 RTT 日志的正常用法。
///
/// 命令面按本机后端（atopenocd-py32，ArteryTek fork）实测定案（2026-10-09，dummy/cmsis-dap
/// 探测 + ArteryTek/openocd 源码核对）：fork 是老式 target 命令表——`rtt setup <addr> <size> <ID>`
/// （valid any time）/ `rtt start` / `rtt stop` / `rtt channels` / `rtt channellist` /
/// `rtt polling_interval` + 全局 `rtt server start/stop <port> <channel>`。注意三件事：
/// ① fork 把 setup 和 start 拆成两步（上游 0.11 的 setup 自动 start，fork 不是）；
/// ② `rtt start` 找不到控制块时只发 `rtt: No control block found` 日志、不返回 TCL 错——
///    必须扫 openocd 的 stdout 捕获来确认（自拉后端才有捕获，外部监听时跳过并如实说明）；
/// ③ fork 没有 `rtt terminal`，数据通路就是 rtt server 的本机 TCP 端口。
///
/// 数据面：本机 TcpClient 直连 openocd 的 rtt server，原始字节直通 stdout（不重新编码，
/// 二进制友好）。收尾顺序：rtt server stop → rtt stop（best-effort，外部实例也清理干净）。
/// 自拉后端随进程退出被 Job 收割（与 watch 相同）——外部监听不动。
/// </summary>
public static class RttCli
{
    public static async Task<int> RunAsync(string[] args)
    {
        var options = RttOptions.Parse(args);
        if (options.Help)
        {
            Console.Write(HelpText);
            return ExitCode(RttFailure.None);
        }
        if (options.Errors.Count > 0)
        {
            foreach (var e in options.Errors) Console.Error.WriteLine("ERROR: " + e);
            Console.Error.WriteLine("run 'ocdup rtt --help' for usage.");
            return ExitCode(RttFailure.Usage);
        }

        var cfg = ConfigStore.Instance.Current;

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true; // 自己收尾：把 rtt server / rtt 停干净再退
            cts.Cancel();
        };

        var exitCode = 0;
        try
        {
            exitCode = await RunCoreAsync(cfg, options, cts.Token).ConfigureAwait(false);
        }
        catch (BackendException ex)
        {
            Console.Error.WriteLine("ERROR: " + ex.Message);
            exitCode = ExitCode(RttFailure.Backend);
        }
        catch (OperationCanceledException)
        {
            // Ctrl+C：正常收尾（cleanup 在 finally）
        }
        catch (SocketException ex)
        {
            // 数据面：rtt server 端口连不上（拒绝连接重试满 2s / 中途被重置）。控制通道本身
            // 是好的，所以这不是 BackendException——但绝不能让它逃逸成未处理异常
            // （v0.4.27 复现：端口没起来时重试窗口结束后 SocketException 直接崩进程）。
            Console.Error.WriteLine($"ERROR: cannot use the RTT data port 127.0.0.1:{options.Port}: {ex.SocketErrorCode}. " +
                                     "Nothing accepted a connection there - 'rtt server start' reported no error, so either the " +
                                     "port is taken by another program or the backend did not open it. Pass --port <n> to pick another " +
                                     "(the control channel itself is fine).");
            exitCode = ExitCode(RttFailure.Data);
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine($"ERROR: the RTT data connection to 127.0.0.1:{options.Port} was lost: {ex.Message}. " +
                                     "The bytes streamed so far are already on stdout; rerun to resume.");
            exitCode = ExitCode(RttFailure.Data);
        }
        finally
        {
            // 收尾不使用已取消的 token：清理命令必须真正发出去（外部 openocd 实例尤其要紧）。
            try
            {
                await BackendSession.Instance.ExecuteAsync(cfg, BuildServerStopCommand(options),
                    startIfNeeded: false, CancellationToken.None, op: OpNames.Rtt).ConfigureAwait(false);
                await BackendSession.Instance.ExecuteAsync(cfg, BuildRttStopCommand(),
                    startIfNeeded: false, CancellationToken.None, op: OpNames.Rtt).ConfigureAwait(false);
            }
            catch
            {
                // best-effort：后端已经没了就算清理完成
            }
        }

        return exitCode;
    }

    private static async Task<int> RunCoreAsync(OcdConfig cfg, RttOptions options, CancellationToken ct)
    {
        // 1. setup：注册控制块搜索（fork：valid any time；成功时 capture 无输出）
        var setupOutput = (await BackendSession.Instance.ExecuteAsync(
            cfg, BuildSetupCommand(options), startIfNeeded: true, ct, op: OpNames.Rtt)).Trim();
        if (!SetupAccepted(setupOutput))
        {
            Console.Error.WriteLine("ERROR: 'rtt setup' was rejected by the backend: " +
                (setupOutput.Length == 0 ? "(no output)" : setupOutput));
            return ExitCode(RttFailure.Runtime);
        }

        // 2. start：fork 语义是显式启动轮询（上游 0.11 的 setup 自带 start，这里不适用）。
        //    游标基线取在 start 之前——控制块搜索结论只可能出现在这之后。
        var cursor = BackendSession.Instance.TotalLinesAppended;
        await BackendSession.Instance.ExecuteAsync(cfg, BuildStartCommand(),
            startIfNeeded: false, ct, op: OpNames.Rtt).ConfigureAwait(false);

        // 3. 确认控制块：fork 找不到时只发日志（"No control block found"）、不返回错误，
        //    不扫日志就会以为一切正常、然后对着一个永不产出的 TCP 端口干等。
        //    只有 ocdup 自拉的后端才有捕获；外部监听时跳过扫描并如实说明。
        long? controlBlockAddress = null;
        if (BackendSession.Instance.CapturesProcessOutput)
        {
            var scanDeadline = DateTime.UtcNow + ControlBlockScanBudget;
            while (DateTime.UtcNow < scanDeadline)
            {
                var lines = BackendSession.Instance.TakeNewLines(ref cursor);
                if (ScanControlBlockLines(lines) is { } hit)
                {
                    if (hit.Outcome == ControlBlockOutcome.NotFound)
                    {
                        Console.Error.WriteLine(
                            "ERROR: the RTT control block was not found in the searched area. " +
                            "Find the exact placement in the firmware map file (symbol _SEGGER_RTT) and pass it via " +
                            "--address (e.g. --address 0x20000100), or widen the search with --size.");
                        return ExitCode(RttFailure.Runtime);
                    }
                    controlBlockAddress = hit.Address;
                    break;
                }
                try { await Task.Delay(100, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return ExitCode(RttFailure.Runtime); }
            }

            // 扫描超时（有捕获但没等到结论）：不能静默开流——固件没跑 RTT / 搜索区不对时，
            // 数据面会永远安静，用户只会看到干等。给一句 stderr 提示，让 --seconds/--lines
            // 的兜底语义有明确的解释。
            if (controlBlockAddress is null)
                Console.Error.WriteLine(
                    $"ocdup rtt: control block not confirmed within {ControlBlockScanBudget.TotalSeconds:0}s - " +
                    "the stream may stay empty. Check that the firmware actually uses RTT, and pass the exact " +
                    "_SEGGER_RTT address from the firmware map file via --address.");
        }

        // --info：只报通道清单，不开数据流。
        if (options.Info)
        {
            var channels = (await BackendSession.Instance.ExecuteAsync(cfg, BuildChannelsCommand(),
                startIfNeeded: false, ct, op: OpNames.Rtt)).Trim();
            Console.WriteLine(channels.Length == 0
                ? "(the backend produced no channel listing)"
                : channels);
            return ExitCode(RttFailure.None);
        }

        // 4. rtt server：让 openocd 在本机开一个 TCP 端口转发该上行通道。
        var serverOutput = (await BackendSession.Instance.ExecuteAsync(cfg, BuildServerStartCommand(options),
            startIfNeeded: false, ct, op: OpNames.Rtt)).Trim();
        if (!SetupAccepted(serverOutput))
        {
            Console.Error.WriteLine("ERROR: 'rtt server start' was rejected by the backend: " +
                (serverOutput.Length == 0 ? "(no output)" : serverOutput) +
                $" - is port {options.Port} already taken? Pass --port <n> to pick another.");
            return ExitCode(RttFailure.Runtime);
        }

        // 5. 连上数据端口（rtt server 在命令返回时可能还没开始 accept，短重试）。
        using var tcp = new TcpClient();
        var connectDeadline = DateTime.UtcNow + ConnectBudget;
        while (true)
        {
            try
            {
                await tcp.ConnectAsync("127.0.0.1", options.Port, ct).ConfigureAwait(false);
                break;
            }
            catch (SocketException) when (DateTime.UtcNow < connectDeadline)
            {
                await Task.Delay(100, ct).ConfigureAwait(false);
            }
        }

        Console.Error.WriteLine(
            $"ocdup rtt: streaming channel {options.Channel}" +
            (controlBlockAddress is { } cb ? $", control block at {Parsing.Hex(cb)}" : "") +
            $" via 127.0.0.1:{options.Port}" +
            (BackendSession.Instance.CapturesProcessOutput ? "" : " (external listener: control block not verified)") +
            " - Ctrl+C to stop.");

        // 6. 数据面：原始字节直通 stdout（不重新编码；二进制协议友好）。
        long totalBytes = 0;
        long totalLines = 0;
        var stream = tcp.GetStream();
        var stdout = Console.OpenStandardOutput();
        var buffer = new byte[8192];
        var hardDeadline = options.Seconds > 0
            ? DateTime.UtcNow + TimeSpan.FromSeconds(options.Seconds)
            : (DateTime?)null;

        while (!ct.IsCancellationRequested)
        {
            if (hardDeadline is { } deadline && DateTime.UtcNow >= deadline)
            {
                Console.Error.WriteLine($"ocdup rtt: --seconds {options.Seconds} elapsed, stopping.");
                break;
            }

            var read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read == 0)
            {
                Console.Error.WriteLine("NOTE: the rtt server closed the connection (backend gone or channel closed).");
                break;
            }

            stdout.Write(buffer, 0, read);
            stdout.Flush();
            totalBytes += read;
            for (var i = 0; i < read; i++)
                if (buffer[i] == (byte)'\n') totalLines++;

            if (LimitReached(totalBytes, totalLines, options))
            {
                Console.Error.WriteLine($"ocdup rtt: limit reached ({totalBytes} bytes / {totalLines} lines), stopping.");
                break;
            }
        }

        Console.Error.WriteLine(
            $"done: {totalBytes} bytes / {totalLines} line(s) from channel {options.Channel}" +
            (controlBlockAddress is { } doneCb ? $", control block at {Parsing.Hex(doneCb)}" : "") + ".");
        return ExitCode(RttFailure.None);
    }

    // ── 纯函数面（单测钉死；语义注释见测试文件头部）──

    /// <summary>rtt 的失败类别（退出码映射用，纯函数可单测）。</summary>
    internal enum RttFailure
    {
        None,     // 成功/help/流式收尾（含限额到达、Ctrl+C 主动停止）
        Runtime,  // 运行期失败：setup 被拒 / 控制块没找到 / rtt server start 被拒 / 流提前结束
        Data,     // 数据面 IO：rtt server 端口连不上、连接中途丢失（控制通道本身是好的）
        Usage,    // 用法/配置错误（选项解析）
        Backend,  // 控制通道/后端故障（BackendException）
    }

    /// <summary>
    /// 退出码映射（纯函数，可单测）：与 `ocdup burn` / `ocdup verify` 同族——
    /// 0 成功 / 1 运行期失败 / 2 用法配置 / 3 后端或控制通道故障。
    /// v0.4.27 修正：此前用法错误返回 1、BackendException 返回 2，与 burn/verify 的
    /// 0/1/2/3 家族正好错位；数据面 IO 失败更是未处理异常直接崩进程。
    /// </summary>
    internal static int ExitCode(RttFailure failure) => failure switch
    {
        RttFailure.None => 0,
        RttFailure.Usage => 2,
        RttFailure.Backend => 3,
        _ => 1, // Runtime / Data
    };

    internal enum ControlBlockOutcome { Found, NotFound }

    internal readonly record struct ControlBlockHit(ControlBlockOutcome Outcome, long? Address);

    internal static string BuildSetupCommand(RttOptions o) =>
        $"rtt setup {Parsing.Hex(o.Address)} {o.Size} {Tcl.Quote(o.Identifier)}";

    internal static string BuildStartCommand() => "rtt start";

    internal static string BuildRttStopCommand() => "rtt stop";

    internal static string BuildChannelsCommand() => "rtt channels";

    internal static string BuildServerStartCommand(RttOptions o) =>
        $"rtt server start {o.Port} {o.Channel}";

    internal static string BuildServerStopCommand(RttOptions o) =>
        $"rtt server stop {o.Port}";

    /// <summary>
    /// capture 回来的文本判定 setup / server start 是否被接受：fork 的这两个命令成功时
    /// 都不产生输出；产生输出基本就是错误（Invalid control block ID / invalid command name /
    /// wrong # args / Error: …）。
    /// </summary>
    internal static bool SetupAccepted(string output)
    {
        if (output.Trim().Length == 0) return true;
        return !(output.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                 output.Contains("invalid", StringComparison.OrdinalIgnoreCase) ||
                 output.Contains("wrong # args", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 扫一行 openocd 输出找控制块结论。命中返回结论；噪声行（含 rtt server 自己的
    /// "Connection for channel N closed"——那是 TCP 连接事件）返回 null 继续等。
    /// </summary>
    internal static ControlBlockHit? ScanControlBlock(string line)
    {
        var m = ControlBlockFoundRegex.Match(line);
        if (m.Success)
            return new ControlBlockHit(ControlBlockOutcome.Found, long.Parse(m.Groups["addr"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
        if (line.Contains("No control block found", StringComparison.OrdinalIgnoreCase))
            return new ControlBlockHit(ControlBlockOutcome.NotFound, null);
        return null;
    }

    /// <summary>扫一批行：第一条有结论的行定案（openocd 按时间顺序输出）。</summary>
    internal static ControlBlockHit? ScanControlBlockLines(IReadOnlyList<string> lines)
    {
        foreach (var line in lines)
            if (ScanControlBlock(line) is { } hit)
                return hit;
        return null;
    }

    /// <summary>是否达到退出限额（0 = 不限）；--info 没有数据流，限额永不触发。</summary>
    internal static bool LimitReached(long bytes, long lines, RttOptions o) =>
        !o.Info && ((o.Bytes > 0 && bytes >= o.Bytes) || (o.Lines > 0 && lines >= o.Lines));

    private static readonly Regex ControlBlockFoundRegex =
        new(@"Control block found at 0x(?<addr>[0-9a-fA-F]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly TimeSpan ControlBlockScanBudget = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ConnectBudget = TimeSpan.FromSeconds(2);

    private const string HelpText = """
        ocdup rtt - stream SEGGER RTT data from the target (v0.4.19)

        usage: ocdup rtt [options]

        options:
          --address <hex>   where to start searching for the RTT control block
                            (default 0x20000000; the exact value is the _SEGGER_RTT
                            symbol in the firmware map file)
          --size <n>        size of the search area in bytes, 16..1048576 (default 4096)
          --id <s>          control block identifier, max 15 chars (default "SEGGER RTT")
          --channel <n>     up-channel to stream, 0..15 (default 0 - firmware printf)
          --port <n>        localhost TCP port for openocd's rtt server, 1024..65535
                            (default 19221)
          --info            list the control block's channels and exit (no data stream)
          --lines <n>       stop after n lines (0 = unlimited, default)
          --bytes <n>       stop after n bytes (0 = unlimited, default)
          --seconds <n>     stop after n seconds (0 = until Ctrl+C, default)
          --help            this text

        data is written to stdout verbatim (raw bytes - safe for binary protocols);
        progress notes go to stderr. Example:
          ocdup rtt --address 0x20000400 --lines 1000 > rtt.log

        exit codes (same family as 'ocdup burn' / 'ocdup verify'):
          0  streamed to completion / --info listing done (limits reached count)
          1  setup rejected, control block not found, data port unusable,
             or the stream ended early
          2  usage / config error
          3  backend or control-channel failure

        config/backend come from ocd.json (OCD_CONFIG) exactly like the MCP tools;
        the OpenOCD backend is started on demand and cleaned up on exit (an openocd
        started by ocdup is stopped when this command exits - same as 'ocdup watch').
        """;
}

/// <summary>`ocdup rtt` 的命令行选项（解析为纯函数，可单测；错误攒在 Errors 里一次报告）。</summary>
public sealed record RttOptions(
    long Address,
    int Size,
    string Identifier,
    int Channel,
    int Port,
    bool Info,
    int Lines,
    long Bytes,
    int Seconds,
    bool Help,
    IReadOnlyList<string> Errors)
{
    public const int DefaultPort = 19221;

    public static RttOptions Parse(string[] args)
    {
        var errors = new List<string>();
        long address = 0x20000000;
        var size = 4096;
        var id = "SEGGER RTT";
        var channel = 0;
        var port = DefaultPort;
        var info = false;
        var lines = 0;
        long bytes = 0;
        var seconds = 0;

        // v0.4.25：解析循环收敛进 ArgScanner
        var scanner = new ArgScanner("rtt", args,
            knownKeys: new[] { "--address", "--size", "--id", "--channel", "--port", "--info", "--lines", "--bytes", "--seconds" },
            flags: new[] { "--info" });
        while (scanner.Next(out var key, out var value))
        {
            switch (key)
            {
                case "--address":
                    if (value is null) break;
                    if (!Parsing.TryParseHex(value, out var parsedAddress))
                    {
                        errors.Add($"address '{value}' is not a hex value - use e.g. --address 0x20000000 (the _SEGGER_RTT symbol in the map file).");
                        break;
                    }
                    address = parsedAddress;
                    break;
                case "--size":
                    if (value is not null && (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out size) || size is < 16 or > 1_048_576))
                    {
                        errors.Add($"size '{value}' is out of range - use a byte count between 16 and 1048576.");
                        size = 4096;
                    }
                    break;
                case "--id":
                    if (value is not null)
                    {
                        if (value.Length == 0 || value.Length > 15)
                            errors.Add($"id '{value}' must be 1..15 characters (the control block ID the firmware was built with, e.g. \"SEGGER RTT\").");
                        else
                            id = value;
                    }
                    break;
                case "--channel":
                    if (value is not null && (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out channel) || channel is < 0 or > 15))
                    {
                        errors.Add($"channel '{value}' is out of range - use an up-channel index between 0 and 15.");
                        channel = 0;
                    }
                    break;
                case "--port":
                    if (value is not null && (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out port) || port is < 1024 or > 65535))
                    {
                        errors.Add($"port '{value}' is out of range - use a TCP port between 1024 and 65535.");
                        port = DefaultPort;
                    }
                    break;
                case "--info":
                    info = true;
                    break;
                case "--lines":
                    if (value is not null && (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out lines) || lines < 0))
                    {
                        errors.Add($"lines '{value}' is invalid - use a non-negative integer (0 = unlimited).");
                        lines = 0;
                    }
                    break;
                case "--bytes":
                    if (value is not null && (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out bytes) || bytes < 0))
                    {
                        errors.Add($"bytes '{value}' is invalid - use a non-negative integer (0 = unlimited).");
                        bytes = 0;
                    }
                    break;
                case "--seconds":
                    if (value is not null && (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out seconds) || seconds is < 0 or > 86400))
                    {
                        errors.Add($"seconds '{value}' is out of range - use 0 (until Ctrl+C) or 1..86400.");
                        seconds = 0;
                    }
                    break;
                default:
                    break; // 未知选项：ArgScanner 已报错且不消费下一个参数
            }
        }

        errors.AddRange(scanner.Errors); // 扫描器自己的缺值/未知错误并入统一报告
        return new RttOptions(address, size, id, channel, port, info, lines, bytes, seconds, scanner.Help, errors);
    }
}
