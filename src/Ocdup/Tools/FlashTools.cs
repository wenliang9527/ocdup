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

    /// <summary>保守判断：OpenOCD 失败时会输出 Error:/failed 文本，命中就不做后续动作。</summary>
    private static bool LooksLikeFailure(string output) =>
        output.Contains("Error:", StringComparison.OrdinalIgnoreCase) ||
        output.Contains("failed", StringComparison.OrdinalIgnoreCase);
}
