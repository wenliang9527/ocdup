using Ocdup.Config;
using Ocdup.Tools;

namespace Ocdup.Cli;

/// <summary>
/// `ocdup burn`（v0.4.20）：量产烧录 CLI。烧录编排直接复用 flash_write 抽出的
/// <see cref="FlashTools.FlashWriteCoreAsync"/>——C2 闸（read_only/allow_flash/白名单/上限）
/// 与「program → 独立 verify_image 读回 → 三段式结论 → reset 门控」的判据只有一份真源，
/// CLI 不复制权限逻辑（防漂移）。与 MCP 工具的差异只有两点：退出码（产线/CI 判定）
/// 与非交互输出。**刻意不接 Ctrl+C 取消**：烧录写相中途取消 = 半截固件，产线语义是
/// 「要么跑完、要么如实报失败」；真要中止请拔探针（read_only 配置下闸会拦住一切写）。
/// 典型用法：治具上电 → `ocdup burn --file build/fw.bin --address 0x08000000` →
/// 按 exit code 分流（0 = 写入且读回一致；1 = 读回不符/写失败；2 = 用法·配置·权限；
/// 3 = 后端/通道故障）。
/// </summary>
public static class BurnCli
{
    public static async Task<int> RunAsync(string[] args)
    {
        var options = BurnOptions.Parse(args);
        if (options.Help)
        {
            Console.Write(HelpText);
            return 0;
        }
        if (options.Errors.Count > 0)
        {
            foreach (var e in options.Errors) Console.Error.WriteLine("ERROR: " + e);
            Console.Error.WriteLine("run 'ocdup burn --help' for usage.");
            return 2;
        }

        var cfg = ConfigStore.Instance.Current;

        // 刻意不用可取消 token：写相中途取消 = 半截固件（见类注释）。Ctrl+C 由外壳兜底。
        var outcome = await FlashTools.FlashWriteCoreAsync(
            cfg, options.File, options.Address, options.Verify, options.Reset, CancellationToken.None)
            .ConfigureAwait(false);

        if (outcome.Ok)
            Console.Out.Write(outcome.Report);
        else
            Console.Error.WriteLine(outcome.Report);

        return MapExitCode(outcome);
    }

    /// <summary>退出码映射（纯函数，可单测）：Ok 与否不直接决定成败——verdict 才是。</summary>
    internal static int MapExitCode(FlashTools.CoreOutcome outcome) =>
        outcome.Ok
            ? outcome.Verdict is FlashTools.FlashWriteVerdict.ProgrammedVerified
                or FlashTools.FlashWriteVerdict.Unverified
                ? 0
                : 1 // ProgrammedButVerifyFailed / WriteFailed：流程跑完但固件没站住
            : outcome.Failure switch
            {
                FlashTools.CoreFailure.Usage => 2,
                FlashTools.CoreFailure.Blocked => 2,
                _ => 3,
            };

    private const string HelpText = """
        ocdup burn - production flash programming with an authoritative read-back verdict (v0.4.20)

        usage: ocdup burn --file <path> [options]

        options:
          --file <path>    image to program (.elf/.hex/.bin); relative paths resolve
                           against the ocd.json directory
          --address <hex>  load address, e.g. 0x08000000 (required for raw .bin images;
                           .elf/.hex carry their own addresses)
          --no-verify      skip the read-back verification (NOT recommended in production)
          --no-reset       skip 'reset run' after a clean pass (fixture does the cycling)
          --help           this text

        exit codes:
          0  programmed and verified (or explicitly --no-verify)
          1  programmed but read-back MISMATCH, or the write step failed
          2  usage / config error / permission gate (BLOCKED)
          3  backend or control-channel failure

        the orchestration (permission gates, 'program' -> independent 'verify_image'
        read-back, reset gating) is the exact same code path as the flash_write MCP
        tool. the run is deliberately non-interruptible: Ctrl+C is not wired to
        cancellation, because a cancelled write phase means a half-written image.
        config/backend come from ocd.json (OCD_CONFIG) exactly like the MCP tools.
        """;
}

/// <summary>`ocdup burn` 的命令行选项（解析为纯函数，可单测；错误攒在 Errors 里一次报告）。</summary>
public sealed record BurnOptions(
    string File,
    string? Address,
    bool Verify,
    bool Reset,
    bool Help,
    IReadOnlyList<string> Errors)
{
    public static BurnOptions Parse(string[] args)
    {
        var errors = new List<string>();
        string? file = null;
        string? address = null;
        var verify = true;
        var reset = true;

        // v0.4.25：解析循环收敛进 ArgScanner（两个无值旗标不吃下一个选项）
        var scanner = new ArgScanner("burn", args,
            knownKeys: new[] { "--file", "--address", "--no-verify", "--no-reset" },
            flags: new[] { "--no-verify", "--no-reset" });
        while (scanner.Next(out var key, out var value))
        {
            switch (key)
            {
                case "--file":
                    file = value;
                    break;
                case "--address":
                    if (value is null) break;
                    if (!Parsing.TryParseHex(value, out _))
                    {
                        errors.Add($"address '{value}' is not a hex value - use e.g. --address 0x08000000.");
                        break;
                    }
                    address = value;
                    break;
                case "--no-verify":
                    verify = false;
                    break;
                case "--no-reset":
                    reset = false;
                    break;
                default:
                    break; // 未知选项：ArgScanner 已报错且不消费下一个参数
            }
        }

        if (!scanner.Help)
        {
            if (string.IsNullOrWhiteSpace(file))
                errors.Add("no image to burn - pass --file <path> (.elf/.hex/.bin).");
            else if (file.EndsWith(".bin", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(address))
                errors.Add("a raw .bin image needs an explicit load address - pass --address 0x08000000 " +
                           "(.elf/.hex carry their own addresses).");
        }

        errors.AddRange(scanner.Errors); // 扫描器自己的缺值/未知错误并入统一报告
        return new BurnOptions(file ?? "", address, verify, reset, scanner.Help, errors);
    }
}
