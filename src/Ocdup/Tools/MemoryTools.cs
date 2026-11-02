using System.ComponentModel;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using ModelContextProtocol.Protocol;
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
        "Read target memory at an address. Parameters: address (hex string like '0x20000000'), count (units to read, default 16), " +
        "width (8, 16 or 32 bits, default 32), format ('text' = the backend's '<addr>: <values>' lines, default; " +
        "'json' = structured {address,width,count,bytes,values:[hex strings]} parsed from the same text - an unparseable output " +
        "returns an ERROR with the raw text). " +
        "If a 16/32-bit read fails, it retries once as byte reads and labels the fallback (no extra FIFO side effects). " +
        "Read-only. If the read fails while the CPU is running, halt first and resume afterwards (both gated by allow_exec). " +
        "Large ranges (whole flash bank, big RAM snapshot) belong to dump_memory - this tool is capped by memory_read_max_bytes.")]
    public static async Task<string> ReadMemory(
        [Description("Start address, hex string like '0x20000000'.")] string address,
        [Description("Units to read (default 16); total capped by memory_read_max_bytes.")] int count = 16,
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
            var output = (await BackendSession.Instance.ExecuteAsync(cfg, command, startIfNeeded: true, ct, op: OpNames.ReadMemory)).Trim();
            if (output.Length == 0)
                return $"ERROR: the backend produced no output for '{command}'.";

            // v0.4.39（现场实战 2026-11-02，session cad81f 的 0x40020808）：宽读取失败自动降级
            // 逐字节重试一次——AT32 真机上 mdw/mdh 偶发 "Failed to read memory at 0x..."，
            // 逐字节 mdb 能过。首读已失败，降级不会对 FIFO 外设产生二次读副作用；
            // 降级也失败则沿用原失败输出走下方常规路径。
            if (width != 8 && LooksLikeReadFailure(output))
            {
                var bytesToRead = (int)totalBytes;
                var byteOutput = (await BackendSession.Instance
                    .ExecuteAsync(cfg, $"mdb {Parsing.Hex(start)} {bytesToRead}", startIfNeeded: false, ct, op: OpNames.ReadMemory)).Trim();
                if (byteOutput.Length > 0 && !LooksLikeReadFailure(byteOutput))
                {
                    if (!json)
                        return $"NOTE: {count} x {width}-bit read failed at {Parsing.Hex(start)}; " +
                               $"auto-fell back to byte reads ({bytesToRead} bytes):\n" + byteOutput;

                    if (Parsing.TryParseMemoryOutput(byteOutput, 8, bytesToRead, out var byteValues) &&
                        AssembleLittleEndian(byteValues, count, width) is { } assembled)
                    {
                        var sj = new StringBuilder(count * 14 + 160);
                        sj.Append("{\"address\":\"").Append(Parsing.Hex(start))
                          .Append("\",\"width\":").Append(width)
                          .Append(",\"count\":").Append(count)
                          .Append(",\"bytes\":").Append(totalBytes)
                          .Append(",\"fallback\":\"byte reads (the wide read failed)\"")
                          .Append(",\"values\":[");
                        for (var i = 0; i < assembled.Length; i++)
                        {
                            if (i > 0) sj.Append(',');
                            sj.Append('"').Append(Parsing.HexValue(assembled[i], width)).Append('"');
                        }
                        sj.Append("]}");
                        return sj.ToString();
                    }
                    // 降级读到了但解析不成 → 交回原失败输出，由下方常规路径如实报错
                }
            }

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
        "values (one or more hex values, e.g. [\"0x11111111\"] or [\"0x01\",\"0x02\"]), width (8/16/32 bits, default 32), " +
        "verify (default false): read the values back and compare - catches a write that did not stick " +
        "(running firmware overwrote it, write-once register). " +
        "Destructive: gated by read_only and allow_memory_write - a BLOCKED answer means the settings forbid it (see show_config). " +
        "Max 256 values per call; for firmware images use flash_write instead.")]
    public static async Task<string> WriteMemory(
        [Description("Start address, hex string like '0x40010800'.")] string address,
        [Description("Hex values to write, in order.")] string[] values,
        [Description("Unit width in bits: 8, 16 or 32 (default 32).")] int width = 32,
        [Description("Read the values back and compare after writing (default false).")] bool verify = false,
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
            var output = (await BackendSession.Instance.ExecuteAsync(cfg, command, startIfNeeded: true, ct, op: OpNames.WriteMemory)).Trim();
            var wrote = $"wrote {parsed.Length} x {width}-bit to {Parsing.Hex(start)} ({parsed.Length * width / 8} bytes):\n" +
                        (output.Length == 0 ? "(no output - write completed silently)" : output);
            if (!verify)
                return wrote;

            // v0.4.39（现场实战 2026-11-02，session cad81f）：写后回读比对——「写命令被接受」
            // ≠「值进去了」，运行态固件/写保护寄存器会静默吞写，现场只能人肉写后读回。
            // 回读失败不算写失败（写已发生），如实降级为 NOTE 交人工判断。
            var reader = width switch { 8 => "mdb", 16 => "mdh", _ => "mdw" };
            try
            {
                var readOutput = (await BackendSession.Instance
                    .ExecuteAsync(cfg, $"{reader} {Parsing.Hex(start)} {parsed.Length}", startIfNeeded: false, ct, op: OpNames.WriteMemory)).Trim();
                if (!Parsing.TryParseMemoryOutput(readOutput, width, parsed.Length, out var readBack))
                    return wrote + $"\nNOTE: verify inconclusive - the read-back could not be parsed as {parsed.Length} x {width}-bit " +
                           $"values (unaligned word address on a byte-limited transport?). Raw output: {readOutput}";

                return FirstReadBackMismatch(parsed, readBack, start, width) is { } mismatch
                    ? "ERROR: verify FAILED - the write was accepted, but the value did not stick.\n" + mismatch +
                      "\nCommon causes: the firmware overwrote it (target running), a write-once/protected register, or a wrong address. " +
                      "Halt the target and retry, or read back manually."
                    : wrote + $"\nverified: read-back matches all {parsed.Length} value(s).";
            }
            catch (BackendException ex)
            {
                return wrote + "\nNOTE: verify inconclusive - the read-back failed: " + ex.Message;
            }
        }
        catch (BackendException ex)
        {
            return ex.Message;
        }
    }

    /// <summary>OpenOCD 读失败的输出特征（真机形态："Failed to read memory at 0x..."，session cad81f 实测）。</summary>
    internal static bool LooksLikeReadFailure(string output) =>
        output.Contains("failed to read memory", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 把逐字节读回的值按小端组装成 width 位值（纯函数，可单测；Cortex-M 是小端）。
    /// 字节数不足 → null（调用方按解析失败处理）。
    /// </summary>
    internal static long[]? AssembleLittleEndian(long[] byteValues, int count, int width)
    {
        var step = width / 8;
        if (byteValues.Length < checked(count * step)) return null;
        var values = new long[count];
        for (var i = 0; i < count; i++)
        {
            long v = 0;
            for (var b = step - 1; b >= 0; b--)
                v = (v << 8) | (byteValues[i * step + b] & 0xFF);
            values[i] = v;
        }
        return values;
    }

    /// <summary>写后回读比对（纯函数，可单测）：返回第一条不一致的描述（含不一致总数），全部一致 → null。</summary>
    internal static string? FirstReadBackMismatch(long[] expected, long[] readBack, long startAddr, int width)
    {
        var mismatches = 0;
        var first = -1;
        for (var i = 0; i < expected.Length; i++)
        {
            if (i < readBack.Length && readBack[i] != expected[i])
            {
                mismatches++;
                if (first < 0) first = i;
            }
        }
        if (first < 0) return null;
        return $"first mismatch at {Parsing.Hex(startAddr + first * (width / 8))}: " +
               $"wrote {Parsing.HexValue(expected[first], width)}, read back {Parsing.HexValue(readBack[first], width)} " +
               $"({mismatches} of {expected.Length} location(s) differ).";
    }

    [McpServerTool(Name = "dump_memory")]
    [Description(
        "Dump a block of target memory/flash into a file on this PC (backend command 'dump_image') - the large-range alternative " +
        "to read_memory, which is capped by memory_read_max_bytes and floods the conversation with text. " +
        "Read-only on the target, but it writes one file on this PC: gated by dump_allowed_paths (default 'dumps/' next to ocd.json) " +
        "and dump_max_bytes (default 64 MB, 0 = unlimited). " +
        "While running, openocd's log lines are forwarded as MCP progress notifications (when the caller requested a progress token). " +
        "Returns the output path, byte count, a sha256 prefix and the first 64 bytes as hex words. " +
        "Last line is the machine-readable verdict: OCDUP-RESULT: dump=<OK|PARTIAL> (PARTIAL = the backend output contained errors).")]
    public static async Task<string> DumpMemory(
        [Description("Output file path (relative = ocd.json dir); parent dirs are created.")] string file,
        [Description("Start address, hex string like '0x08000000'.")] string address,
        [Description("Bytes to dump (decimal, e.g. 1048576 = 1 MB). Slow for large ranges - raise command_timeout_seconds if needed.")] long length,
        RequestContext<CallToolRequestParams>? context = null,
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

        // 先清掉可能存在的旧文件：dump_image 失败时，不能把上一次的产物当本次结果报告（sha256/预览全是旧的）。
        try
        {
            if (File.Exists(resolved)) File.Delete(resolved);
        }
        catch (Exception ex)
        {
            return $"ERROR: cannot replace '{resolved}': {ex.Message}. Close whatever holds the file, or choose another path.";
        }

        var command = $"dump_image {Tcl.Quote(resolved)} {Parsing.Hex(start)} {length}";
        string output;

        // P1-4：进度泵——大 dump 期间把 openocd 增量日志行以 progress notification 推回（带 token 才发）。
        var progressPump = FlashProgress.Start(BackendSession.Instance, context?.Server, context?.Params?.Meta);
        using var pumpCts = new CancellationTokenSource();
        var pumpTask = progressPump?.RunAsync(pumpCts.Token) ?? Task.CompletedTask;
        try
        {
            output = (await BackendSession.Instance.ExecuteAsync(cfg, command, startIfNeeded: true, ct, op: OpNames.DumpMemory)).Trim();
        }
        catch (BackendException ex)
        {
            return ex.Message;
        }
        finally
        {
            pumpCts.Cancel();
            try { await pumpTask.ConfigureAwait(false); } catch { /* 泵的收尾 */ }
        }

        if (!File.Exists(resolved))
            return $"ERROR: the backend produced no file at '{resolved}'. " +
                   $"Backend output: {(output.Length == 0 ? "(none)" : output)}";

        // v0.4.30：sha256 与前 64 字节预览单趟读完（原先哈希一遍、预览又开一遍文件）。
        long actual;
        string sha16;
        var preview = new byte[64];
        var previewLen = 0;
        using (var stream = File.OpenRead(resolved))
        using (var sha = SHA256.Create())
        {
            var buffer = new byte[64 * 1024];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                sha.TransformBlock(buffer, 0, read, buffer, 0);
                var take = Math.Min(read, preview.Length - previewLen);
                if (take > 0)
                {
                    Buffer.BlockCopy(buffer, 0, preview, previewLen, take);
                    previewLen += take;
                }
            }
            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            sha16 = Convert.ToHexString(sha.Hash!, 0, 8);
            actual = stream.Length;
        }
        if (previewLen < preview.Length) preview = preview[..previewLen];

        var report = FormatDumpReport(start, length, resolved, actual, sha16, preview);
        // A-10：机器可读结论（dump 的 PARTIAL = 后端输出带错，文件已落但内容存疑）
        return LooksLikeError(output)
            ? ResultTag.Append(report + "\nNOTE: the backend output contains errors - double-check the dumped file. Output: " + output, "dump", "PARTIAL")
            : ResultTag.Append(report + "\nbackend: " + (output.Length == 0 ? "(no output)" : output), "dump", "OK");
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
