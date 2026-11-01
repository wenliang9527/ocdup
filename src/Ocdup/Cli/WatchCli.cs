using System.Globalization;
using Ocdup.Backend;
using Ocdup.Config;
using Ocdup.Tools;

namespace Ocdup.Cli;

/// <summary>
/// `ocdup watch`（M-watch，设计文档后置里程碑）：按符号名定时采样变量，盯曲线/调参。
/// 独立 CLI 而非 MCP 工具——长时运行会占死 MCP 控制通道（C1 串行），且 stdout 重定向
/// 成 CSV 文件画曲线更顺手。全部复用既有层：ocd.json 配置、BackendSession 控制通道、
/// ElfSymbols 符号表（与 read_variable 同一套路径解析）。变量值读法：mdw 按对齐地址
/// 读一个 32 位字，按小端移位掩码摘出 1/2/4 字节——兼容非对齐的 u8/u16 符号。
/// </summary>
public static class WatchCli
{
    /// <summary>退出码（对齐 burn/verify/rtt 家族 0/1/2/3）：0 = 正常结束（含 Ctrl+C）；
    /// 2 = 用法/配置/权限（BLOCKED）；3 = 后端或控制通道丢失。v0.4.31 起与家族一致。</summary>
    internal const int ExitUsage = 2;
    internal const int ExitBackend = 3;

    public static async Task<int> RunAsync(string[] args)
    {
        var options = WatchOptions.Parse(args);
        if (options.Help)
        {
            Console.Write(HelpText);
            return 0;
        }
        if (options.Errors.Count > 0)
        {
            foreach (var e in options.Errors) Console.Error.WriteLine("ERROR: " + e);
            Console.Error.WriteLine("run 'ocdup watch --help' for usage.");
            return ExitUsage;
        }

        var cfg = ConfigStore.Instance.Current;

        // --halt 每拍暂停目标固件再读：执行控制，与 halt 工具同级设防。
        if (options.Halt && PermissionGate.CheckExec(cfg, "watch --halt") is { } blocked)
        {
            Console.Error.WriteLine(blocked);
            return ExitUsage;
        }

        List<ElfSymbol> symbols;
        try
        {
            symbols = SymbolTools.LoadSymbols(cfg, options.ElfPath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("ERROR: " + ex.Message);
            return ExitUsage;
        }

        // 逐名解析：精确命中才可盯；>4 字节警告跳过（watch 只盯标量，大对象用 dump_memory）；
        // 重名/未找到给出与 read_variable 同款的引导。全部落空 = 退出。
        var watchable = new List<ElfSymbol>();
        foreach (var name in options.Names)
        {
            var exact = symbols.Where(s => s.Name == name).ToList();
            if (exact.Count > 1)
            {
                Console.Error.WriteLine($"ERROR: '{name}' is defined at {exact.Count} addresses (static in multiple units?) - " +
                                        "watch by address is not supported; rename or pick one via dump_memory.");
                continue;
            }
            var symbol = exact.SingleOrDefault();
            if (symbol is null)
            {
                var ci = symbols.Where(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Take(3).ToList();
                Console.Error.WriteLine($"ERROR: variable '{name}' not found in the symbol table" +
                                        (ci.Count > 0 ? " (names are case-sensitive). Did you mean: " + string.Join(", ", ci.Select(s => s.Name)) + "?" : "."));
                continue;
            }
            if (symbol.Size is not (1 or 2 or 4))
            {
                Console.Error.WriteLine($"WARNING: skipping '{name}' ({symbol.Size} bytes) - watch covers 1/2/4-byte scalars; " +
                                        $"use dump_memory at {Parsing.Hex(symbol.Address)} for bigger objects.");
                continue;
            }
            watchable.Add(symbol);
        }

        if (watchable.Count == 0)
        {
            Console.Error.WriteLine("ERROR: nothing to watch. list_variables shows what the ELF contains.");
            return ExitUsage;
        }

        var pad = watchable.Max(s => s.Name.Length);
        // v0.4.30：csv 形态下人读信息（banner/变量清单/收尾）一律走 stderr——stdout 只许出现
        // CSV 表头与数据行，`> log.csv` 才拿得到干净可画曲线的文件（与 rtt 的「数据 stdout /
        // 进度 stderr」同一纪律；此前 banner 混进 stdout，绘图工具直接噎住）。
        var notes = WatchCli.NotesToStderr(options.Format) ? Console.Error : Console.Out;
        notes.WriteLine($"ocdup watch: {watchable.Count} variable(s) every {options.IntervalMs}ms " +
                        $"(format={options.Format}, halt={options.Halt switch { true => "on (firmware pauses each sample)", false => "off" }})");
        foreach (var s in watchable)
            notes.WriteLine($"  {s.Name.PadRight(pad)} @ {Parsing.Hex(s.Address)} ({s.Size} bytes)");
        notes.WriteLine("Ctrl+C to stop.");

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true; // 自己收尾，不粗暴杀（--halt 途中保证 resume）
            cts.Cancel();
        };

        if (options.Format == "csv")
            Console.WriteLine("time," + string.Join(",", watchable.Select(s => s.Name)));

        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(options.IntervalMs));
        var samples = 0;
        var failures = 0;
        try
        {
            while (await timer.WaitForNextTickAsync(cts.Token).ConfigureAwait(false))
            {
                List<uint> values;
                try
                {
                    values = await SampleAsync(cfg, watchable, options.Halt, cts.Token).ConfigureAwait(false);
                    failures = 0;
                }
                catch (BackendException ex)
                {
                    failures++;
                    if (failures == 1)
                        Console.Error.WriteLine($"!! sample failed ({ex.Message.Split('\n')[0]}) - retrying; " +
                                                "the connection recovers automatically.");
                    else if (failures % 10 == 0)
                        Console.Error.WriteLine($"!! still failing ({failures} samples in a row).");
                    if (failures >= 30)
                    {
                        Console.Error.WriteLine("ERROR: 30 consecutive failed samples - the backend is probably gone. Exiting.");
                        return ExitBackend;
                    }
                    continue;
                }

                samples++;
                var now = DateTime.Now;
                Console.WriteLine(options.Format == "csv"
                    ? RenderCsv(now, watchable, values)
                    : RenderPretty(now, watchable, values));
                if (options.Count > 0 && samples >= options.Count)
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            // Ctrl+C：正常收尾。--halt 时收尾 resume 已在 SampleCoreAsync 的 finally 里发出
            //（v0.4.35 起含取消落在 halt 窗口的情形）；这里只提示核实目标状态。
            if (options.Halt)
                notes.WriteLine("Ctrl+C: the final resume was sent; if the target still looks halted, run `resume` to check.");
        }

        notes.WriteLine($"done: {samples} sample(s).");
        return 0;
    }

    /// <summary>
    /// 人读信息（banner/变量清单/收尾行）是否走 stderr（纯函数，可单测）：csv 形态走——
    /// stdout 是数据通道（表头 + 行），重定向进文件的就是纯 CSV；pretty 形态无所谓，走 stdout。
    /// </summary>
    internal static bool NotesToStderr(string format) =>
        string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 采一拍：逐变量 mdw 对齐字；--halt 时先 halt 200ms 再读、finally 里 resume。
    /// 走 BackendSession 单例；测试走 <see cref="SampleCoreAsync"/> 接缝。
    /// </summary>
    private static Task<List<uint>> SampleAsync(OcdConfig cfg, List<ElfSymbol> vars, bool halt, CancellationToken ct) =>
        SampleCoreAsync(BackendSession.Instance.ExecuteAsync, cfg, vars, halt, ct);

    /// <summary>SampleAsync 的执行通道接缝（签名与 BackendSession.ExecuteAsync 一致），测试注入假通道。</summary>
    internal delegate Task<string> ExecuteFn(OcdConfig cfg, string command, bool startIfNeeded, CancellationToken ct, string op);

    /// <summary>
    /// 采样核心（内部接缝，可单测）。P1 修复（v0.4.31，RttCli 同款）：Ctrl+C 落在
    /// halt→读取窗口时，采样 ct 已取消——finally 里的 resume 若还用它，
    /// 通道闸 WaitAsync(ct) 会直接抛 OCE，resume 永远到不了后端，目标被留在 halted。
    /// 所以收尾命令一律 <see cref="CancellationToken.None"/>：收尾必须真正发出去。
    /// v0.4.35（audit F-4）：halt 也移进 try——`halt 200` 要等目标停稳（≥200ms），
    /// 是取消高发窗口；halt 在 try 之外时，取消落在这里 finally 不执行，resume 不发。
    /// </summary>
    internal static async Task<List<uint>> SampleCoreAsync(ExecuteFn execute, OcdConfig cfg, List<ElfSymbol> vars, bool halt, CancellationToken ct)
    {
        try
        {
            if (halt)
                await execute(cfg, "halt 200", startIfNeeded: true, ct, "watch").ConfigureAwait(false);
            var values = new List<uint>(vars.Count);
            foreach (var s in vars)
            {
                var aligned = s.Address & ~3L;
                var output = await execute(cfg, $"mdw {Parsing.Hex(aligned)} 1", startIfNeeded: !halt, ct, "watch").ConfigureAwait(false);
                if (!Parsing.TryParseMemoryOutput(output, 32, 1, out var words))
                    throw new BackendException($"could not read '{s.Name}' @ {Parsing.Hex(s.Address)}: {output.ReplaceLineEndings(" ").Trim()}");
                values.Add(WatchValue.Extract((uint)words[0], s.Address, (int)s.Size));
            }
            return values;
        }
        finally
        {
            if (halt)
            {
                try
                {
                    await execute(cfg, "resume", startIfNeeded: false, CancellationToken.None, "watch").ConfigureAwait(false);
                }
                catch (BackendException)
                {
                    // resume 失败不吞掉取样本身的结果；下一拍 halt 会先于一切执行
                }
            }
        }
    }

    // ── 渲染（纯函数，便于单测）──

    internal static string RenderCsv(DateTime time, IReadOnlyList<ElfSymbol> vars, IReadOnlyList<uint> values) =>
        $"{time:HH:mm:ss.fff}," + string.Join(",", values);

    internal static string RenderPretty(DateTime time, IReadOnlyList<ElfSymbol> vars, IReadOnlyList<uint> values)
    {
        var pad = vars.Max(s => s.Name.Length);
        var cells = vars.Zip(values, (s, v) => $"{s.Name.PadRight(pad)}={WatchValue.FormatHex(v, (int)s.Size)} ({v})");
        return $"{time:HH:mm:ss.fff}  " + string.Join("  ", cells);
    }

    private const string HelpText = """
        ocdup watch - sample ELF variables by name on a timer (M-watch)

        usage: ocdup watch --name <var> [--name <var2> ...] [options]

        options:
          --name <n>      variable name to watch (repeatable; commas also split)
          --elf <path>    .elf image (default: "elf_path" in ocd.json; relative to it)
          --interval <ms> sampling period, 20..600000 (default 250)
          --format <f>    pretty (default) or csv - csv rows: time,value1,value2,...
                          (in csv mode stdout carries ONLY the header+rows; the
                          banner, variable list and summary go to stderr, so
                          `> log.csv` yields a clean file for plotting)
          --halt          pause the firmware (halt/resume) around each sample for
                          consistent reads; needs allow_exec and pauses the target
          --count <n>     stop after n samples (default: until Ctrl+C)
          --help          this text

        exit codes:
          0  stopped normally (count reached or Ctrl+C)
          2  usage / config error / permission gate (BLOCKED) / no watchable symbol
          3  backend or control-channel lost (30 consecutive failed samples)

        config/backend come from ocd.json (OCD_CONFIG) exactly like the MCP tools;
        the OpenOCD backend is started on demand and cleaned up on exit.
        """;
}

/// <summary>`ocdup watch` 的命令行选项（解析为纯函数，可单测；错误攒在 Errors 里一次报告）。</summary>
public sealed record WatchOptions(
    IReadOnlyList<string> Names,
    string? ElfPath,
    int IntervalMs,
    string Format,
    bool Halt,
    int Count,
    bool Help,
    IReadOnlyList<string> Errors)
{
    public static WatchOptions Parse(string[] args)
    {
        var names = new List<string>();
        var errors = new List<string>();
        string? elf = null;
        var interval = 250;
        string format = "pretty";
        var halt = false;
        var count = 0;

        // v0.4.25：解析循环收敛进 ArgScanner（--key value / --key=value / 旗标 / help / 未知项）
        var scanner = new ArgScanner("watch", args,
            knownKeys: new[] { "--name", "--elf", "--interval", "--format", "--halt", "--count" },
            flags: new[] { "--halt" });
        while (scanner.Next(out var key, out var value))
        {
            switch (key)
            {
                case "--name":
                    if (value is not null)
                        names.AddRange(value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                    break;
                case "--elf":
                    elf = value;
                    break;
                case "--interval":
                    if (value is not null && (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out interval) || interval is < 20 or > 600_000))
                    {
                        errors.Add($"interval '{value}' is out of range - use milliseconds between 20 and 600000.");
                        interval = 250;
                    }
                    break;
                case "--format":
                    if (value is not null && !value.Equals("pretty", StringComparison.OrdinalIgnoreCase) && !value.Equals("csv", StringComparison.OrdinalIgnoreCase))
                        errors.Add($"format '{value}' is not supported - use 'pretty' or 'csv'.");
                    else if (value is not null)
                        format = value.ToLowerInvariant();
                    break;
                case "--halt":
                    halt = true;
                    break;
                case "--count":
                    if (value is not null && (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out count) || count < 0))
                    {
                        errors.Add($"count '{value}' is invalid - use a non-negative integer (0 = unlimited).");
                        count = 0;
                    }
                    break;
                default:
                    break; // 未知选项：ArgScanner 已报错且不消费下一个参数
            }
        }

        if (!scanner.Help && names.Count == 0)
            errors.Add("no variable to watch - pass --name at least once (list_variables shows what the ELF contains).");

        errors.AddRange(scanner.Errors); // 扫描器自己的缺值/未知错误并入统一报告
        return new WatchOptions(names, elf, interval, format, halt, count, scanner.Help, errors);
    }
}

/// <summary>变量取值工具（纯函数）：mdw 只能读对齐的 32 位字，小端移位掩码摘出 1/2/4 字节标量。</summary>
public static class WatchValue
{
    public static uint Extract(uint word, long address, int size) => size switch
    {
        1 => (word >> (int)((address & 3) * 8)) & 0xFF,
        2 => (word >> (int)((address & 3) * 8)) & 0xFFFF,
        _ => word,
    };

    public static string FormatHex(uint value, int size) => size switch
    {
        1 => $"0x{value & 0xFF:x2}",
        2 => $"0x{value & 0xFFFF:x4}",
        _ => $"0x{value:x8}",
    };
}
