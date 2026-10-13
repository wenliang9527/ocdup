using Ocdup.Config;
using Ocdup.Tools;

namespace Ocdup.Cli;

/// <summary>
/// `ocdup verify`（v0.4.26）：只读比对镜像与目标 flash 的 CLI。比对本体复用
/// verify_image 工具抽出的 <see cref="FlashTools.VerifyCoreAsync"/>——「无
/// 'verified N bytes' 确认即判不通过」的保守判据只有一份真源。用途：量产治具
/// 金板抽检（烧完治具镜像后抽一块板复核）、CI 里烧后自检——exit code 0/1/2/3
/// 与 burn 同族（0=verified，1=VERIFY FAILED，2=用法/配置，3=后端故障）。
/// 只读：无权限闸（与 verify_image 工具同语义），不动目标上一个字节。
/// </summary>
public static class VerifyCli
{
    public static async Task<int> RunAsync(string[] args)
    {
        var options = VerifyOptions.Parse(args);
        if (options.Help)
        {
            Console.Write(HelpText);
            return 0;
        }
        if (options.Errors.Count > 0)
        {
            foreach (var e in options.Errors) Console.Error.WriteLine("ERROR: " + e);
            Console.Error.WriteLine("run 'ocdup verify --help' for usage.");
            return 2;
        }

        var cfg = ConfigStore.Instance.Current;
        var outcome = await FlashTools.VerifyCoreAsync(cfg, options.File, options.Address, CancellationToken.None)
            .ConfigureAwait(false);

        if (outcome.Ok)
            Console.Out.Write(outcome.Report);
        else
            Console.Error.WriteLine(outcome.Report);

        return MapExitCode(outcome);
    }

    /// <summary>退出码映射（纯函数，可单测）：与 burn 同族，比对是只读操作。</summary>
    internal static int MapExitCode(FlashTools.VerifyOutcome outcome) =>
        outcome.Ok
            ? outcome.Verified ? 0 : 1
            : outcome.Failure switch
            {
                FlashTools.CoreFailure.Usage => 2,
                FlashTools.CoreFailure.Blocked => 2,
                _ => 3,
            };

    private const string HelpText = """
        ocdup verify - compare an image against the target flash, read-only (v0.4.26)

        usage: ocdup verify --file <path> [--address <hex>]

        options:
          --file <path>    image to compare (.elf/.hex/.bin); relative paths resolve
                           against the ocd.json directory
          --address <hex>  offset for raw .bin images, e.g. 0x08000000 (required for
                           .bin; .elf/.hex carry their own addresses)
          --help           this text

        exit codes:
          0  verified - the image matches the target flash
          1  VERIFY FAILED - no read-back confirmation (or the backend reported an error)
          2  usage / config error
          3  backend or control-channel failure

        read-only: nothing is written to the target or on this PC; the verdict uses the
        same conservative criterion as the verify_image MCP tool ("verified N bytes" or
        it did not pass). Typical use: re-check a board after 'ocdup burn' without an
        MCP session.

        the backend's flash-verify algorithm needs a stably halted core. With the
        firmware running the run can end in 'VERIFY INCONCLUSIVE' - that is NOT a
        mismatch, halt the target and run it again.
        """;
}

/// <summary>`ocdup verify` 的命令行选项（ArgScanner 消费；解析为纯函数，可单测）。</summary>
public sealed record VerifyOptions(
    string File,
    string? Address,
    bool Help,
    IReadOnlyList<string> Errors)
{
    public static VerifyOptions Parse(string[] args)
    {
        var errors = new List<string>();
        string? file = null;
        string? address = null;

        var scanner = new ArgScanner("verify", args,
            knownKeys: new[] { "--file", "--address" },
            flags: Array.Empty<string>());
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
                default:
                    break; // 未知选项：ArgScanner 已报错且不消费下一个参数
            }
        }

        if (!scanner.Help)
        {
            if (string.IsNullOrWhiteSpace(file))
                errors.Add("no image to verify - pass --file <path> (.elf/.hex/.bin).");
            else if (file.EndsWith(".bin", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(address))
                errors.Add("a raw .bin image needs an explicit offset - pass --address 0x08000000 " +
                           "(.elf/.hex carry their own addresses).");
        }

        errors.AddRange(scanner.Errors);
        return new VerifyOptions(file ?? "", address, scanner.Help, errors);
    }
}
