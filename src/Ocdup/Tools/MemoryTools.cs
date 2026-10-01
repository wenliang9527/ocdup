using System.ComponentModel;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using ModelContextProtocol.Server;
using Ocdup.Backend;
using Ocdup.Config;

namespace Ocdup.Tools;

/// <summary>按地址读目标内存/flash(设计文档「读指定地址的数据」)。</summary>
[McpServerToolType]
public static class MemoryTools
{
    [McpServerTool(Name = "read_memory")]
    [Description(
        "Read target memory at an address. Parameters: address (hex string like '0x20000000'), count (how many units to read, default 16), " +
        "width (unit size in bits: 8, 16 or 32, default 32), format ('text' = the backend's '<addr>: <values>' lines, default; " +
        "'json' = structured {address,width,count,bytes,values:[hex strings]} parsed from the same text - an unparseable output returns an ERROR with the raw text). " +
        "Read-only. If the read fails while the CPU is running, call halt first and resume afterwards (both gated by allow_exec). " +
        "For large ranges (whole flash bank, big RAM snapshot) use dump_memory instead - this tool is capped by memory_read_max_bytes.")]
    public static async Task<string> ReadMemory(
        [Description("Start address, hex string like '0x20000000'.")] string address,
        [Description("Number of units to read (default 16). The total size is capped by memory_read_max_bytes.")] int count = 16,
        [Description("Unit width in bits: 8, 16 or 32 (default 32).")] int width = 32,
        [Description("Output format: 'text' (backend lines, default) or 'json' (structured values).")] string format = "text",
        CancellationToken ct = default)
    {
        var cfg = ConfigStore.Instance.Current;

        if (!Parsing.TryParseHex(address, out var start))
            return $"ERROR: address '{address}' is not a hex value. Use a hex string like '0x20000000'.";
        if (count <= 0)
            return "ERROR: count must be a positive integer.";
        if (width is not (8 or 16 or 32))
            return "ERROR: width must be 8, 16 or 32.";
        var json = string.Equals(format, "json", StringComparison.OrdinalIgnoreCase);
        if (!json && !string.Equals(format, "text", StringComparison.OrdinalIgnoreCase))
            return $"ERROR: format '{format}' is not supported. Use 'text' or 'json'.";

        var totalBytes = (long)count * width / 8;
        if (PermissionGate.CheckReadLimit(cfg, totalBytes, "read_memory") is { } blocked)
            return blocked;

        var monitor = width switch { 8 => "mdb", 16 => "mdh", _ => "mdw" };
        var command = $"{monitor} {Parsing.Hex(start)} {count}";

        try
        {
            var output = (await BackendSession.Instance.ExecuteAsync(cfg, command, startIfNeeded: true, ct, op: "read_memory")).Trim();
            if (output.Length == 0)
                return $"ERROR: the backend produced no output for '{command}'.";

            if (json)
            {
                if (!Parsing.TryParseMemoryOutput(output, width, count, out var values))
                    return $"ERROR: could not parse the backend output as {count} x {width}-bit values. Raw output:\n{output}";
                var sb = new StringBuilder(count * 14 + 96);
                sb.Append("{\"address\":\"").Append(Parsing.Hex(start))
                  .Append("\",\"width\":").Append(width)
                  .Append(",\"count\":").Append(count)
                  .Append(",\"bytes\":").Append(totalBytes)
                  .Append(",\"values\":[");
                for (var i = 0; i < values.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append('"').Append(Parsing.HexValue(values[i], width)).Append('"');
                }
                sb.Append("]}");
                return sb.ToString();
            }

            return $"read {count} x {width}-bit from {Parsing.Hex(start)} ({totalBytes} bytes):\n" + output;
        }
        catch (BackendException ex)
        {
            return ex.Message;
        }
    }

    [McpServerTool(Name = "write_memory")]
    [Description(
        "Write values to target memory or memory-mapped registers in ONE backend command. Parameters: address (hex string like '0x40010800'), " +
        "values (one or more hex values, e.g. [\"0x11111111\"] or [\"0x01\",\"0x02\"]), width (8/16/32 bits, default 32). " +
        "Destructive: gated by read_only and allow_memory_write - a BLOCKED answer means the settings forbid it (see show_config). " +
        "Max 256 values per call; for firmware images use flash_write instead.")]
    public static async Task<string> WriteMemory(
        [Description("Start address, hex string like '0x40010800'.")] string address,
        [Description("Hex values to write, in order.")] string[] values,
        [Description("Unit width in bits: 8, 16 or 32 (default 32).")] int width = 32,
        CancellationToken ct = default)
    {
        var cfg = ConfigStore.Instance.Current;

        // C2:权限检查前置到第一步,再谈参数。
        if (PermissionGate.CheckMemoryWrite(cfg, "memory write") is { } blocked)
            return blocked;

        if (!Parsing.TryParseHex(address, out var start))
            return $"ERROR: address '{address}' is not a hex value. Use a hex string like '0x40010800'.";
        if (values is null || values.Length == 0)
            return "ERROR: values must contain at least one hex value.";
        if (values.Length > 256)
            return "ERROR: too many values (max 256 per call). For firmware images use flash_write instead.";
        if (width is not (8 or 16 or 32))
            return "ERROR: width must be 8, 16 or 32.";

        var parsed = new long[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            if (!Parsing.TryParseHex(values[i], out var v))
                return $"ERROR: values[{i}] '{values[i]}' is not a hex value. Use forms like '0x1F' or '1F'.";
            parsed[i] = v;
        }

        var writer = width switch { 8 => "mwb", 16 => "mwh", _ => "mww" };
        var sb = new StringBuilder(writer.Length + 16 + values.Length * 12);
        sb.Append(writer).Append(' ').Append(Parsing.Hex(start));
        foreach (var v in parsed)
            sb.Append(' ').Append(Parsing.HexValue(v, width));
        var command = sb.ToString();

        try
        {
            var output = (await BackendSession.Instance.ExecuteAsync(cfg, command, startIfNeeded: true, ct, op: "write_memory")).Trim();
            return $"wrote {parsed.Length} x {width}-bit to {Parsing.Hex(start)} ({parsed.Length * width / 8} bytes):\n" +
                   (output.Length == 0 ? "(no output - write completed silently)" : output);
        }
        catch (BackendException ex)
        {
            return ex.Message;
        }
    }

    [McpServerTool(Name = "dump_memory")]
    [Description(
        "Dump a block of target memory/flash into a file on this PC (backend command 'dump_image'). " +
        "Use this instead of read_memory for large ranges - a whole flash bank or big RAM snapshot - " +
        "because read_memory is capped by memory_read_max_bytes and floods the conversation with text. " +
        "Read-only on the target, but it writes one file on this PC: gated by dump_allowed_paths (default 'dumps/' next to ocd.json) " +
        "and dump_max_bytes (default 64 MB, 0 = unlimited). " +
        "Returns the output path, byte count, a sha256 prefix and the first 64 bytes as hex words.")]
    public static async Task<string> DumpMemory(
        [Description("Output file path; relative paths resolve against the ocd.json directory. Parent directories are created.")] string file,
        [Description("Start address, hex string like '0x08000000'.")] string address,
        [Description("How many bytes to dump (decimal, e.g. 1048576 for 1 MB). Large dumps can take a while - raise command_timeout_seconds if needed.")] long length,
        CancellationToken ct = default)
    {
        var cfg = ConfigStore.Instance.Current;

        if (!Parsing.TryParseHex(address, out var start))
            return $"ERROR: address '{address}' is not a hex value. Use a hex string like '0x08000000'.";
        if (length <= 0)
            return "ERROR: length must be a positive byte count.";

        string resolved;
        try { resolved = cfg.ResolvePath(file); }
        catch (Exception ex) { return $"ERROR: '{file}' is not a usable path: {ex.Message}"; }

        // 落盘闸前置:路径白名单 + 体积上限。必须在建目录/写文件之前拦(不变量 C2)。
        if (PermissionGate.CheckDumpWrite(cfg, resolved, length) is { } blocked)
            return blocked;

        var dir = Path.GetDirectoryName(resolved);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var command = $"dump_image {Tcl.Quote(resolved)} {Parsing.Hex(start)} {length}";
        string output;
        try
        {
            output = (await BackendSession.Instance.ExecuteAsync(cfg, command, startIfNeeded: true, ct, op: "dump_memory")).Trim();
        }
        catch (BackendException ex)
        {
            return ex.Message;
        }

        if (!File.Exists(resolved))
            return $"ERROR: the backend produced no file at '{resolved}'. " +
                   $"Backend output: {(output.Length == 0 ? "(none)" : output)}";

        string sha16;
        using (var stream = File.OpenRead(resolved))
        using (var sha = SHA256.Create())
        {
            sha16 = Convert.ToHexString(sha.ComputeHash(stream), 0, 8);
        }

        long actual = new FileInfo(resolved).Length;
        var preview = new byte[Math.Min(64, actual)];
        using (var stream = File.OpenRead(resolved))
        {
            var read = 0;
            while (read < preview.Length)
            {
                var n = stream.Read(preview, read, preview.Length - read);
                if (n == 0) break;
                read += n;
            }
            if (read < preview.Length) preview = preview[..read];
        }

        var report = FormatDumpReport(start, length, resolved, actual, sha16, preview);
        return LooksLikeError(output)
            ? report + "\nNOTE: the backend output contains errors - double-check the dumped file. Output: " + output
            : report + "\nbackend: " + (output.Length == 0 ? "(no output)" : output);
    }

    private static bool LooksLikeError(string output) =>
        output.Contains("Error:", StringComparison.OrdinalIgnoreCase) ||
        output.Contains("failed", StringComparison.OrdinalIgnoreCase);

    /// <summary>dump 结果的文本报告(纯函数,便于单测):路径/字节数/大小不符提示/sha 前缀/前 64 字节 hex 字。</summary>
    public static string FormatDumpReport(long startAddr, long requestedLength, string resolvedPath, long actualBytes, string sha16, byte[] preview)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"dumped: {resolvedPath} ({actualBytes} bytes)");
        if (actualBytes != requestedLength)
            sb.AppendLine($"NOTE: requested {requestedLength} bytes but the file holds {actualBytes}.");
        sb.AppendLine($"sha256: {sha16}...");
        if (preview.Length == 0)
        {
            sb.Append("(file is empty)");
            return sb.ToString();
        }
        sb.Append("first bytes:");
        for (var i = 0; i < preview.Length; i += 4)
        {
            var take = Math.Min(4, preview.Length - i);
            sb.AppendLine();
            sb.Append($"0x{(startAddr + i):x8}: {Convert.ToHexString(preview, i, take)}");
        }
        return sb.ToString();
    }
}
