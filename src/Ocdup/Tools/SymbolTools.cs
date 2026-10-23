using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using Ocdup.Backend;
using Ocdup.Config;

namespace Ocdup.Tools;

/// <summary>
/// 符号层 L1（设计文档第 7 节）：按变量名读写。地址解析来自 .elf 的 .symtab（PC 侧），
/// 数据读写走既有控制通道（目标侧）。镜像路径：工具参数 file 优先，其次 ocd.json 的 elf_path。
/// </summary>
[McpServerToolType]
public static class SymbolTools
{
    private const int MaxListLines = 200;

    [McpServerTool(Name = "list_variables")]
    [Description(
        "List object symbols (global/static variables) from an ELF image's .symtab: address, size, name, sorted by address. " +
        "PC-side only - reads the .elf file, the backend is not involved. " +
        "Pass file explicitly or set elf_path in ocd.json once. pattern is a case-insensitive substring filter on names. " +
        "Pairs with read_variable / write_variable.")]
    public static string ListVariables(
        [Description("Optional case-insensitive substring filter on variable names.")] string? pattern = null,
        [Description("Path to the .elf image; relative paths resolve against the ocd.json directory. Overrides elf_path.")] string? file = null)
    {
        var cfg = ConfigStore.Instance.Current;
        List<ElfSymbol> symbols;
        try
        {
            symbols = LoadSymbols(cfg, file);
        }
        catch (Exception ex) when (ex is ElfParseException or FileNotFoundException)
        {
            return $"ERROR: {ex.Message}";
        }
        catch (Exception ex)
        {
            return $"ERROR: cannot read the ELF: {ex.Message}";
        }

        if (symbols.Count == 0)
            return "no object symbols found in the .symtab (only functions, or nothing).";

        IEnumerable<ElfSymbol> selected = symbols;
        if (!string.IsNullOrWhiteSpace(pattern))
            selected = selected.Where(s => s.Name.Contains(pattern.Trim(), StringComparison.OrdinalIgnoreCase));

        var matches = selected.Take(MaxListLines + 1).ToList();
        if (matches.Count == 0)
            return $"no variable name contains '{pattern}' ({symbols.Count} symbols in the table).";

        var sb = new StringBuilder();
        sb.Append(matches.Count).Append(" variable(s)");
        if (!string.IsNullOrWhiteSpace(pattern)) sb.Append(" matching '").Append(pattern.Trim()).Append('\'');
        sb.AppendLine(" (address size name):");
        foreach (var s in matches.Take(MaxListLines))
            sb.AppendLine($"{Parsing.Hex(s.Address)}  {s.Size,4}  {s.Name}");
        if (matches.Count > MaxListLines)
            sb.Append($"... output capped at {MaxListLines} lines - narrow it with pattern.");
        return sb.ToString();
    }

    [McpServerTool(Name = "read_variable")]
    [Description(
        "Read a variable by NAME: resolves the address/size from the ELF .symtab, then reads its bytes from the target. " +
        "Size is capped by memory_read_max_bytes (for big buffers use dump_memory with the address from list_variables). " +
        "If the read fails while the target runs, halt first and resume afterwards (allow_exec). " +
        "Pass file explicitly or set elf_path in ocd.json once.")]
    public static async Task<string> ReadVariable(
        [Description("Variable name exactly as in the ELF (discover with list_variables).")] string name,
        [Description("Path to the .elf image; relative paths resolve against the ocd.json directory. Overrides elf_path.")] string? file = null,
        CancellationToken ct = default)
    {
        var cfg = ConfigStore.Instance.Current;

        var lookup = ResolveOne(cfg, name, file);
        if (lookup.Problem is { } problem) return problem;
        var symbol = lookup.Symbol!;
        var totalBytes = symbol.Size;

        if (PermissionGate.CheckReadLimit(cfg, totalBytes, "read_variable") is { } blocked)
            return blocked;

        var count = (int)((totalBytes + 3) / 4);
        string output;
        try
        {
            output = (await BackendSession.Instance
                .ExecuteAsync(cfg, $"mdw {Parsing.Hex(symbol.Address)} {count}", startIfNeeded: true, ct, op: "read_variable")).Trim();
        }
        catch (BackendException ex)
        {
            return ex.Message;
        }

        if (!Parsing.TryParseMemoryOutput(output, 32, count, out var words))
            return $"ERROR: could not parse the backend output as {count} x 32-bit words. Raw output:\n{output}";

        var sb = new StringBuilder();
        sb.Append($"{symbol.Name} @ {Parsing.Hex(symbol.Address)} ({totalBytes} bytes): ");
        // 小端逐字节呈现：1/2/4 字节给出单值解释，更大的对象给 hex 串
        if (totalBytes == 1) sb.Append($"0x{words[0] & 0xFF:x2}");
        else if (totalBytes == 2) sb.Append($"0x{(words[0] & 0xFFFF):x4}");
        else if (totalBytes == 4) sb.Append($"0x{words[0] & 0xFFFFFFFF:x8}");
        else
        {
            var bytes = new byte[totalBytes];
            for (var i = 0; i < count; i++)
                for (var b = 0; b < 4 && i * 4 + b < totalBytes; b++)
                    bytes[i * 4 + b] = (byte)(words[i] >> (8 * b));
            sb.Append(Convert.ToHexString(bytes, 0, (int)Math.Min(totalBytes, 64)).ToLowerInvariant());
            if (totalBytes > 64) sb.Append($" ... ({totalBytes} bytes total, little-endian)");
        }
        sb.Append("\nraw words: ").Append(output);
        return sb.ToString();
    }

    [McpServerTool(Name = "write_variable")]
    [Description(
        "Write a hex value to a variable by NAME (address/size resolved from the ELF .symtab). " +
        "Only 1/2/4-byte variables - for bigger objects use write_memory with the address from list_variables. " +
        "Destructive: gated by read_only / allow_memory_write - a BLOCKED answer means the settings forbid it (see show_config).")]
    public static async Task<string> WriteVariable(
        [Description("Variable name exactly as in the ELF (discover with list_variables).")] string name,
        [Description("Value to write, hex string like '0x42' (masked to the variable's width).")] string value,
        [Description("Path to the .elf image; relative paths resolve against the ocd.json directory. Overrides elf_path.")] string? file = null,
        CancellationToken ct = default)
    {
        var cfg = ConfigStore.Instance.Current;

        // C2:权限检查前置到第一步,再谈参数。
        if (PermissionGate.CheckMemoryWrite(cfg, "variable write") is { } blocked)
            return blocked;

        var lookup = ResolveOne(cfg, name, file);
        if (lookup.Problem is { } problem) return problem;
        var symbol = lookup.Symbol!;

        if (symbol.Size is not (1 or 2 or 4))
            return $"ERROR: '{name}' is {symbol.Size} bytes - write_variable only covers 1/2/4-byte variables. " +
                   $"Use write_memory at {Parsing.Hex(symbol.Address)} for bigger objects.";
        if (!Parsing.TryParseHex(value, out var parsed))
            return $"ERROR: value '{value}' is not a hex value. Use forms like '0x42' or '42'.";

        var width = (int)symbol.Size * 8;
        var writer = symbol.Size switch { 1 => "mwb", 2 => "mwh", _ => "mww" };
        var command = $"{writer} {Parsing.Hex(symbol.Address)} {Parsing.HexValue(parsed, width)}";

        try
        {
            var output = (await BackendSession.Instance
                .ExecuteAsync(cfg, command, startIfNeeded: true, ct, op: "write_variable")).Trim();
            return $"wrote {name} ({symbol.Size} byte(s)) @ {Parsing.Hex(symbol.Address)} = {Parsing.HexValue(parsed, width)}:\n" +
                   (output.Length == 0 ? "(no output - write completed silently)" : output);
        }
        catch (BackendException ex)
        {
            return ex.Message;
        }
    }

    private record SymbolLookup(ElfSymbol? Symbol, string? Problem);

    /// <summary>解析镜像路径 → 读文件 → 找符号。处理：未配镜像 / 重名（不同地址的 static 同名）/ 未找到。</summary>
    private static SymbolLookup ResolveOne(OcdConfig cfg, string name, string? file)
    {
        List<ElfSymbol> symbols;
        try
        {
            symbols = LoadSymbols(cfg, file);
        }
        catch (Exception ex)
        {
            return new SymbolLookup(null, $"ERROR: {ex.Message}");
        }

        var exact = symbols.Where(s => s.Name == name).ToList();
        if (exact.Count == 1) return new SymbolLookup(exact[0], null);
        if (exact.Count > 1)
        {
            var lines = string.Join("\n", exact.Select(s => $"  {Parsing.Hex(s.Address)}  {s.Size,4}  {s.Name}"));
            return new SymbolLookup(null,
                $"ERROR: '{name}' is defined at {exact.Count} addresses (static in multiple translation units?). Pick one via read_memory:\n{lines}");
        }

        var ci = symbols.Where(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (ci.Count > 0)
            return new SymbolLookup(null,
                $"ERROR: variable '{name}' not found (names are case-sensitive). Did you mean: {string.Join(", ", ci.Take(5).Select(s => s.Name))}?");
        return new SymbolLookup(null,
            $"ERROR: variable '{name}' not found in the symbol table. list_variables shows what the ELF contains.");
    }

    // internal:watch CLI（M-watch）复用同一套 ELF 路径解析与报错文案
    internal static List<ElfSymbol> LoadSymbols(OcdConfig cfg, string? file)
    {
        var pathArg = string.IsNullOrWhiteSpace(file) ? cfg.ElfPath : file;
        if (string.IsNullOrWhiteSpace(pathArg))
            throw new FileNotFoundException("no ELF image given. Pass file, or set \"elf_path\" in ocd.json once.");
        var resolved = cfg.ResolvePath(pathArg);
        if (!File.Exists(resolved))
            throw new FileNotFoundException(
                $"'{resolved}' does not exist. Pass the path of the built (unstripped) .elf; " +
                "relative paths are resolved against the ocd.json directory.");
        return ParseObjectsCached(resolved);
    }

    // ── v0.4.30：ELF 解析缓存（按「完整路径 + mtime + 长度」）──
    // read/write_variable 每次调用都走 LoadSymbols；此前每次都 File.ReadAllBytes + 全 symtab
    // 扫描（1 MB 级 ELF 是毫秒到几十毫秒的纯浪费）。与 SvdTools.GetOrParse 同款纪律：
    // 重新构建后 mtime 变化自动失效；缓存条数有界（最旧先走），防长时间会话里反复构建攒条目。
    // 返回的列表按只读使用——调用方（list/read/write/watch）都不修改它。
    private static readonly object SymbolCacheSync = new();
    private static readonly Dictionary<string, (DateTime MtimeUtc, long Length, List<ElfSymbol> Symbols)> SymbolCache =
        new(StringComparer.OrdinalIgnoreCase);
    private const int MaxCachedElves = 8;

    private static List<ElfSymbol> ParseObjectsCached(string resolved)
    {
        var mtime = File.GetLastWriteTimeUtc(resolved);
        var length = new FileInfo(resolved).Length;

        lock (SymbolCacheSync)
        {
            if (SymbolCache.TryGetValue(resolved, out var hit) && hit.MtimeUtc == mtime && hit.Length == length)
                return hit.Symbols;
        }

        var parsed = ElfSymbols.ParseObjects(File.ReadAllBytes(resolved));

        lock (SymbolCacheSync)
        {
            if (SymbolCache.Count >= MaxCachedElves && !SymbolCache.ContainsKey(resolved))
                SymbolCache.Remove(SymbolCache.Keys.First());
            SymbolCache[resolved] = (mtime, length, parsed);
        }
        return parsed;
    }
}
