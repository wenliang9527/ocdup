using System.ComponentModel;
using System.IO;
using System.Text;
using ModelContextProtocol.Server;
using Ocdup.Backend;
using Ocdup.Config;

namespace Ocdup.Tools;

/// <summary>烧录（本轮目标之一：下载）。</summary>
[McpServerToolType]
public static class FlashTools
{
    [McpServerTool(Name = "flash_info")]
    [Description("List the flash banks the backend knows (driver, base address, size). Read-only. Requires a running backend - call connect first if this fails.")]
    public static async Task<string> FlashInfo(CancellationToken ct = default)
    {
        var cfg = ConfigStore.Instance.Current;
        try
        {
            var banks = (await BackendSession.Instance.ExecuteAsync(cfg, "flash banks", startIfNeeded: true, ct, op: "flash_info")).Trim();
            return banks.Length == 0
                ? "ERROR: the backend reported no flash banks. Check target_cfg in ocd.json (see show_config)."
                : $"flash banks:\n{banks}";
        }
        catch (BackendException ex)
        {
            return ex.Message;
        }
    }

    [McpServerTool(Name = "flash_write")]
    [Description(
        "Download an image to the target flash. Parameters: file (path to .elf/.hex/.bin; relative paths resolve against the ocd.json directory), " +
        "address (hex load address like '0x08000000'; required for raw .bin images, ignored for .elf/.hex), verify (default true), " +
        "reset (default true: run 'reset run' after programming). " +
        "Gated by read_only / allow_flash / flash_allowed_paths / flash_max_bytes - a BLOCKED answer means the settings forbid it (see show_config).")]
    public static async Task<string> FlashWrite(
        [Description("Path to the image to program (.elf/.hex/.bin).")] string file,
        [Description("Load address for raw .bin images, hex string like '0x08000000'. Ignored for .elf/.hex.")] string? address = null,
        [Description("Verify the written image (default true).")] bool verify = true,
        [Description("Run 'reset run' after programming (default true).")] bool reset = true,
        CancellationToken ct = default)
    {
        var cfg = ConfigStore.Instance.Current;

        string resolved;
        try
        {
            resolved = cfg.ResolvePath(file);
        }
        catch (Exception ex)
        {
            return $"ERROR: '{file}' is not a usable path: {ex.Message}";
        }

        if (!File.Exists(resolved))
            return $"ERROR: '{resolved}' does not exist. Pass the path of the built image (.elf/.hex/.bin); " +
                   "relative paths are resolved against the ocd.json directory (see show_config for its location).";

        var image = new FileInfo(resolved);
        if (PermissionGate.CheckFlashWrite(cfg, resolved, image.Length) is { } blocked)
            return blocked;

        long? loadAddress = null;
        if (!string.IsNullOrWhiteSpace(address))
        {
            if (!Parsing.TryParseHex(address, out var parsed))
                return $"ERROR: address '{address}' is not a hex value. Use a hex string like '0x08000000'.";
            loadAddress = parsed;
        }
        else if (resolved.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
        {
            return "ERROR: a raw .bin image needs an explicit load address, e.g. flash_write(file=\"build/fw.bin\", address=\"0x08000000\"). " +
                   "An .elf/.hex image carries its own addresses and needs no address argument.";
        }

        // program 是 OpenOCD 的烧录入口：写 + 可选校验；参数顺序无关（非选项参数视为 offset）。
        var command = new StringBuilder("program ").Append(Tcl.Quote(resolved));
        if (verify) command.Append(" verify");
        if (loadAddress is { } addr) command.Append(' ').Append(Parsing.Hex(addr));

        string output;
        try
        {
            output = (await BackendSession.Instance.ExecuteAsync(cfg, command.ToString(), startIfNeeded: true, ct, op: "flash_write")).Trim();
        }
        catch (BackendException ex)
        {
            return ex.Message;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"programmed: {resolved} ({image.Length} bytes){(verify ? " with verify" : "")}");
        if (loadAddress is { } reported) sb.AppendLine($"load address: {Parsing.Hex(reported)}");
        sb.AppendLine(output.Length == 0 ? "(the backend produced no output)" : output);

        if (LooksLikeFailure(output))
        {
            sb.Append("NOTE: the backend output looks like a failure; 'reset run' was skipped. Check the log above (probe wiring, flash bank, target_cfg).");
            return sb.ToString();
        }

        if (reset)
        {
            try
            {
                var resetOutput = (await BackendSession.Instance.ExecuteAsync(cfg, "reset run", startIfNeeded: false, ct, op: "flash_write")).Trim();
                sb.AppendLine(resetOutput.Length == 0 ? "reset run: done" : $"reset run: {resetOutput}");
            }
            catch (BackendException ex)
            {
                sb.AppendLine($"NOTE: the image was programmed, but 'reset run' failed: {ex.Message}");
            }
        }

        return sb.ToString();
    }

    [McpServerTool(Name = "verify_image")]
    [Description(
        "Compare an image file against the target flash WITHOUT writing anything (backend command 'verify_image'). " +
        "Use it to answer 'what firmware is actually on the board' - e.g. re-verify a previously flashed build. " +
        "Parameters: file (.elf/.hex/.bin; relative paths resolve against the ocd.json directory), " +
        "address (required for raw .bin, hex string like '0x08000000'; ignored for .elf/.hex). " +
        "Read-only: no permission gate applies and no file is written on this PC.")]
    public static async Task<string> VerifyImage(
        [Description("Path to the image to verify against target flash (.elf/.hex/.bin).")] string file,
        [Description("Offset for raw .bin images, hex string like '0x08000000'. Ignored for .elf/.hex.")] string? address = null,
        CancellationToken ct = default)
    {
        var cfg = ConfigStore.Instance.Current;

        string resolved;
        try
        {
            resolved = cfg.ResolvePath(file);
        }
        catch (Exception ex)
        {
            return $"ERROR: '{file}' is not a usable path: {ex.Message}";
        }

        if (!File.Exists(resolved))
            return $"ERROR: '{resolved}' does not exist. Pass the path of the built image (.elf/.hex/.bin); " +
                   "relative paths are resolved against the ocd.json directory (see show_config for its location).";

        var image = new FileInfo(resolved);
        long? loadAddress = null;
        if (!string.IsNullOrWhiteSpace(address))
        {
            if (!Parsing.TryParseHex(address, out var parsed))
                return $"ERROR: address '{address}' is not a hex value. Use a hex string like '0x08000000'.";
            loadAddress = parsed;
        }
        else if (resolved.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
        {
            return "ERROR: a raw .bin image needs an explicit offset, e.g. verify_image(file=\"build/fw.bin\", address=\"0x08000000\"). " +
                   "An .elf/.hex image carries its own addresses and needs no address argument.";
        }

        var command = new StringBuilder("verify_image ").Append(Tcl.Quote(resolved));
        if (loadAddress is { } addr) command.Append(' ').Append(Parsing.Hex(addr));

        string output;
        try
        {
            output = (await BackendSession.Instance.ExecuteAsync(cfg, command.ToString(), startIfNeeded: true, ct, op: "verify_image")).Trim();
        }
        catch (BackendException ex)
        {
            return ex.Message;
        }

        return InterpretVerifyOutput(output) is { } problem
            ? $"VERIFY FAILED: {resolved} ({image.Length} bytes) does not match the target flash.\n" +
              $"problem: {problem}\nbackend output:\n{output}"
            : $"verified: {resolved} ({image.Length} bytes) matches the target flash.\nbackend: " +
              (output.Length == 0 ? "(no output)" : output);
    }

    /// <summary>
    /// 判定 verify_image 的后端输出（纯函数，便于单测）：返回 null = 校验通过；
    /// 返回字符串 = 失败/存疑的原因。成功形态是 "verified N bytes in Ts"；
    /// 任何 Error:/failed/mismatch/No source/空输出 都判不通过（保守：宁误报失败）。
    /// </summary>
    public static string? InterpretVerifyOutput(string output)
    {
        if (output.Length == 0) return "the backend produced no output";
        if (output.Contains("verified", StringComparison.OrdinalIgnoreCase) && !LooksLikeFailure(output))
            return null;
        return LooksLikeFailure(output) ? "the backend reported an error" : "no 'verified N bytes' confirmation in the output";
    }

    [McpServerTool(Name = "flash_erase_sector")]
    [Description(
        "Erase flash sectors without writing anything. DESTRUCTIVE and OFF by default: gated by read_only and allow_flash_erase " +
        "(default false - set \"allow_flash_erase\": true in the ocd.json permissions block). " +
        "Parameters: bank (index from flash_info, default 0), first/last (inclusive sector numbers; last may be -1 meaning 'to the end of the bank').")]
    public static async Task<string> FlashEraseSector(
        [Description("Flash bank index from flash_info (default 0).")] int bank = 0,
        [Description("First sector to erase (inclusive, default 0).")] int first = 0,
        [Description("Last sector to erase (inclusive; -1 = to the end of the bank, default -1).")] int last = -1,
        CancellationToken ct = default)
    {
        var cfg = ConfigStore.Instance.Current;

        if (PermissionGate.CheckFlashErase(cfg) is { } blocked)
            return blocked;

        if (bank < 0) return "ERROR: bank must be >= 0 (see flash_info for the bank list).";
        if (first < 0) return "ERROR: first must be >= 0.";
        if (last != -1 && last < first) return "ERROR: last must be >= first, or -1 for 'to the end of the bank'.";

        var lastText = last == -1 ? "last" : last.ToString();
        var command = $"flash erase_sector {bank} {first} {lastText}";

        try
        {
            var output = (await BackendSession.Instance.ExecuteAsync(cfg, command, startIfNeeded: true, ct, op: "flash_erase_sector")).Trim();
            return $"erase issued: bank {bank}, sectors {first}..{lastText}.\n" +
                   (output.Length == 0 ? "(no output - check flash_info / verify_image afterwards)" : output);
        }
        catch (BackendException ex)
        {
            return ex.Message;
        }
    }

    /// <summary>保守判断：OpenOCD 失败时会输出 Error:/failed 文本，命中就不做后续动作。</summary>
    private static bool LooksLikeFailure(string output) =>
        output.Contains("Error:", StringComparison.OrdinalIgnoreCase) ||
        output.Contains("failed", StringComparison.OrdinalIgnoreCase);
}
