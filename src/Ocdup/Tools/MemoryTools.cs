using System.ComponentModel;
using ModelContextProtocol.Server;
using Ocdup.Backend;
using Ocdup.Config;

namespace Ocdup.Tools;

/// <summary>读指定地址的数据（本轮目标之一）。</summary>
[McpServerToolType]
public static class MemoryTools
{
    [McpServerTool(Name = "read_memory")]
    [Description(
        "Read target memory at an address. Parameters: address (hex string like '0x20000000'), count (how many units to read, default 16), " +
        "width (unit size in bits: 8, 16 or 32, default 32). Returns the '<addr>: <values>' lines reported by the backend. " +
        "Read-only. If the read fails while the CPU is running, halt it from the client first - halt/resume are not part of this stage.")]
    public static async Task<string> ReadMemory(
        [Description("Start address, hex string like '0x20000000'.")] string address,
        [Description("Number of units to read (default 16). The total size is capped by memory_read_max_bytes.")] int count = 16,
        [Description("Unit width in bits: 8, 16 or 32 (default 32).")] int width = 32,
        CancellationToken ct = default)
    {
        var cfg = ConfigStore.Instance.Current;

        if (!Parsing.TryParseHex(address, out var start))
            return $"ERROR: address '{address}' is not a hex value. Use a hex string like '0x20000000'.";
        if (count <= 0)
            return "ERROR: count must be a positive integer.";
        if (width is not (8 or 16 or 32))
            return "ERROR: width must be 8, 16 or 32.";

        var totalBytes = (long)count * width / 8;
        if (PermissionGate.CheckReadLimit(cfg, totalBytes, "read_memory") is { } blocked)
            return blocked;

        var monitor = width switch { 8 => "mdb", 16 => "mdh", _ => "mdw" };
        var command = $"{monitor} {Parsing.Hex(start)} {count}";

        try
        {
            var output = (await BackendSession.Instance.ExecuteAsync(cfg, command, startIfNeeded: true, ct)).Trim();
            return $"read {count} x {width}-bit from {Parsing.Hex(start)} ({totalBytes} bytes):\n" +
                   (output.Length == 0 ? "(the backend produced no output)" : output);
        }
        catch (BackendException ex)
        {
            return ex.Message;
        }
    }
}
