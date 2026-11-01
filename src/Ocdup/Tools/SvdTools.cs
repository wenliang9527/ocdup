using System.ComponentModel;
using ModelContextProtocol.Server;
using Ocdup.Backend;
using Ocdup.Config;

namespace Ocdup.Tools;

/// <summary>
/// 符号层 L2（v0.4.21）：CMSIS-SVD 按名访问外设寄存器（设计文档第 7 节）。三个工具：
/// `load_svd`（PC 侧解析 + 清单，无权限面）、`read_peripheral_register`（按名读 + 位域
/// 解码）、`write_peripheral_register`（按名写，走 CheckMemoryWrite 闸，read-only access
/// 的寄存器在闸之外再加一道语义拒写）。数据面完全复用 read/write_memory 的 md*/mw*
/// 路径——地址从 SVD 解出来之后就是普通内存访问，断链重放白名单语义随 op 一致。
/// 缓存：按「完整路径 + mtime」缓存解析结果，同一文件重复 load 零开销；最后加载的
/// 设备留在内存里供 read/write 免 file 参数使用，ocd.json 的 `svd_path` 可作默认镜像
/// （与 elf_path 同款约定）。
/// </summary>
[McpServerToolType]
public static class SvdTools
{
    private static readonly object Sync = new();
    private static readonly Dictionary<string, (DateTime MtimeUtc, SvdDevice Device)> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static SvdDevice? _latest;

    [McpServerTool(Name = "load_svd")]
    [Description(
        "Load a CMSIS-SVD file (XML) and list its peripherals - or one peripheral's registers when 'peripheral' is given. " +
        "file is optional when \"svd_path\" is set in ocd.json (it serves as the default). " +
        "The parsed device stays loaded for read_peripheral_register / write_peripheral_register (they need no file argument after this). " +
        "PC-side file read only: no permission gate, the target is not touched.")]
    public static Task<string> LoadSvd(
        [Description("Path to the .svd file (relative = ocd.json dir). Omit when svd_path is set in ocd.json.")] string? file = null,
        [Description("Optional peripheral name: list only its registers (name, address, size, access).")] string? peripheral = null,
        CancellationToken ct = default)
    {
        var cfg = ConfigStore.Instance.Current;

        var pathArg = string.IsNullOrWhiteSpace(file) ? cfg.SvdPath : file;
        if (string.IsNullOrWhiteSpace(pathArg))
            return Task.FromResult("ERROR: no SVD file given. Pass file, or set \"svd_path\" in ocd.json once " +
                                   "(read_peripheral_register / write_peripheral_register then need no file argument either). " +
                                   "See show_config.");

        string resolved;
        try
        {
            resolved = cfg.ResolvePath(pathArg);
        }
        catch (Exception ex)
        {
            return Task.FromResult($"ERROR: '{pathArg}' is not a usable path: {ex.Message}");
        }

        if (!File.Exists(resolved))
            return Task.FromResult($"ERROR: '{resolved}' does not exist. Pass the path of a CMSIS-SVD file; " +
                                   "relative paths are resolved against the ocd.json directory (see show_config for its location).");

        SvdDevice device;
        try
        {
            device = GetOrParse(resolved);
        }
        catch (InvalidDataException ex)
        {
            return Task.FromResult($"ERROR: could not parse the SVD file: {ex.Message}");
        }

        Volatile.Write(ref _latest, device);

        if (peripheral is { Length: > 0 })
        {
            var p = SvdFile.FindPeripheral(device, peripheral);
            if (p is null)
                return Task.FromResult($"ERROR: no peripheral named '{peripheral}' in {Path.GetFileName(resolved)}. " +
                                       $"Did you mean: {string.Join(", ", SvdFile.SuggestPeripherals(device, peripheral))}?");
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"{p.Name} @ {Hex(p.BaseAddress)} - {p.Registers.Count} register(s):");
            foreach (var r in p.Registers)
            {
                var abs = SvdFile.AbsoluteAddress(p, r);
                sb.AppendLine($"  {r.Name.PadRight(16)} {Hex(abs)}  {r.SizeBits,2}-bit  {r.Access}{(r.Fields.Count > 0 ? $"  ({r.Fields.Count} fields)" : "")}");
            }
            sb.Append($"read with read_peripheral_register(\"{p.Name}.<REG>\").");
            return Task.FromResult(sb.ToString());
        }

        var total = device.Peripherals.Sum(x => x.Registers.Count);
        var lines = new System.Text.StringBuilder();
        lines.AppendLine($"loaded {resolved}: {device.Peripherals.Count} peripheral(s), {total} register(s) total.");
        const int MaxList = 40;
        foreach (var p in device.Peripherals.Take(MaxList))
            lines.AppendLine($"  {p.Name.PadRight(16)} {Hex(p.BaseAddress)}  ({p.Registers.Count} registers)");
        if (device.Peripherals.Count > MaxList)
            lines.AppendLine($"  ... {device.Peripherals.Count - MaxList} more (list one with load_svd(file=..., peripheral=\"NAME\")).");
        lines.Append("read/write by name with read_peripheral_register(\"PERIPH.REG\") / write_peripheral_register.");
        return Task.FromResult(lines.ToString());
    }

    [McpServerTool(Name = "read_peripheral_register")]
    [Description(
        "Read a peripheral register by SVD name and decode its bit fields, e.g. name=\"RCC.CR\" (cluster registers use their path, e.g. \"TIM1.PWM.CCR\"). " +
        "Requires a loaded SVD (call load_svd first, or set \"svd_path\" in ocd.json). The register's absolute address comes from the SVD " +
        "(peripheral baseAddress + offset); the read itself is the same mdb/mdh/mdw path as read_memory. Read-only.")]
    public static async Task<string> ReadPeripheralRegister(
        [Description("Register name as PERIPH.REGISTER, e.g. \"RCC.CR\".")] string name,
        CancellationToken ct = default)
    {
        var cfg = ConfigStore.Instance.Current;
        var loaded = GetLoaded(cfg);
        if (loaded.Problem is { } svdProblem)
            return $"ERROR: {svdProblem}\n" +
                   "Run load_svd(file=...) for the full diagnostic, or fix \"svd_path\" in ocd.json (see show_config).";
        if (loaded.Device is not { } device)
            return "ERROR: no SVD file is loaded. Call load_svd(file=\"vendor.svd\") first, or set \"svd_path\" in ocd.json " +
                   "(show_config shows whether it is set).";

        if (!TrySplit(name, out var peripheralName, out var registerName))
            return $"ERROR: '{name}' is not a register name - use PERIPH.REGISTER, e.g. \"RCC.CR\" (load_svd lists what is available).";

        var peripheral = SvdFile.FindPeripheral(device, peripheralName);
        if (peripheral is null)
            return $"ERROR: no peripheral named '{peripheralName}' in the loaded SVD. " +
                   $"Did you mean: {string.Join(", ", SvdFile.SuggestPeripherals(device, peripheralName))}?";

        var register = SvdFile.FindRegister(peripheral, registerName);
        if (register is null)
            return $"ERROR: peripheral '{peripheral.Name}' has no register '{registerName}'. " +
                   $"Did you mean: {string.Join(", ", SvdFile.SuggestRegisters(peripheral, registerName))}?";

        var address = SvdFile.AbsoluteAddress(peripheral, register);
        string output;
        try
        {
            output = (await BackendSession.Instance.ExecuteAsync(cfg, SvdFile.ReadCommand(register, address),
                startIfNeeded: true, ct, op: OpNames.ReadPeripheralRegister)).Trim();
        }
        catch (BackendException ex)
        {
            return ex.Message;
        }

        if (!Parsing.TryParseMemoryOutput(output, register.SizeBits, 1, out var values))
            return $"ERROR: could not parse the backend output for {peripheral.Name}.{register.Name}. Raw output:\n{output}";

        return $"{peripheral.Name}.{SvdFile.DescribeRegister(register, address, values[0])}";
    }

    [McpServerTool(Name = "write_peripheral_register")]
    [Description(
        "Write a peripheral register by SVD name, e.g. name=\"RCC.CR\", value=\"0x00000001\" (hex string). " +
        "Gated by read_only / allow_memory_write (same switches as write_memory); registers whose SVD access is read-only are refused " +
        "even when the permission settings would allow the write. The value is masked to the register's width. " +
        "Read the register back afterwards to confirm - peripheral writes can have side effects the SVD cannot express.")]
    public static async Task<string> WritePeripheralRegister(
        [Description("Register name as PERIPH.REGISTER, e.g. \"RCC.CR\".")] string name,
        [Description("Value to write, hex string like \"0x00000001\"; masked to the register's width.")] string value,
        CancellationToken ct = default)
    {
        var cfg = ConfigStore.Instance.Current;

        if (PermissionGate.CheckMemoryWrite(cfg, "write_peripheral_register") is { } blocked)
            return blocked;

        var loaded = GetLoaded(cfg);
        if (loaded.Problem is { } svdProblem)
            return $"ERROR: {svdProblem}\n" +
                   "Run load_svd(file=...) for the full diagnostic, or fix \"svd_path\" in ocd.json (see show_config).";
        if (loaded.Device is not { } device)
            return "ERROR: no SVD file is loaded. Call load_svd(file=\"vendor.svd\") first, or set \"svd_path\" in ocd.json " +
                   "(show_config shows whether it is set).";

        if (!TrySplit(name, out var peripheralName, out var registerName))
            return $"ERROR: '{name}' is not a register name - use PERIPH.REGISTER, e.g. \"RCC.CR\" (load_svd lists what is available).";

        var peripheral = SvdFile.FindPeripheral(device, peripheralName);
        if (peripheral is null)
            return $"ERROR: no peripheral named '{peripheralName}' in the loaded SVD. " +
                   $"Did you mean: {string.Join(", ", SvdFile.SuggestPeripherals(device, peripheralName))}?";

        var register = SvdFile.FindRegister(peripheral, registerName);
        if (register is null)
            return $"ERROR: peripheral '{peripheral.Name}' has no register '{registerName}'. " +
                   $"Did you mean: {string.Join(", ", SvdFile.SuggestRegisters(peripheral, registerName))}?";

        if (!SvdFile.IsWritable(register))
            return $"ERROR: {peripheral.Name}.{register.Name} is '{register.Access}' in the SVD - a write would be a no-op or a fault. " +
                   "If the SVD is wrong for your device, use write_memory at the register's address instead.";

        if (!Parsing.TryParseHex(value, out var parsed))
            return $"ERROR: value '{value}' is not a hex string. Use e.g. \"0x00000001\".";

        var address = SvdFile.AbsoluteAddress(peripheral, register);
        var command = SvdFile.WriteCommand(register, address, parsed);
        string output;
        try
        {
            output = (await BackendSession.Instance.ExecuteAsync(cfg, command, startIfNeeded: true, ct, op: OpNames.WritePeripheralRegister)).Trim();
        }
        catch (BackendException ex)
        {
            return ex.Message;
        }

        return $"wrote {peripheral.Name}.{register.Name} @ {Parsing.Hex(address)} = 0x{parsed & ((1L << Math.Min(register.SizeBits, 63)) - 1):x}" +
               (output.Length == 0 ? "" : $"\nbackend: {output}") +
               $"\nread it back with read_peripheral_register(\"{peripheral.Name}.{register.Name}\") to confirm.";
    }

    // ── 加载与解析辅助 ──

    internal readonly record struct LoadedSvd(SvdDevice? Device, string? Problem);

    /// <summary>
    /// 取当前可用的 SVD 设备（internal 供单测）。Problem 非空 = svd_path 配了但不可用
    /// （解析失败/路径不存在）——v0.4.31 起如实透出真实原因，此前吞掉 InvalidDataException
    /// 会让 read/write_peripheral_register 误报「no SVD file is loaded」（用户明明已设 svd_path）。
    /// Problem 为空且 Device 为空 = 没配 svd_path（调用方报「未加载」引导）。
    /// </summary>
    internal static LoadedSvd GetLoaded(OcdConfig cfg)
    {
        var latest = Volatile.Read(ref _latest);
        if (latest is not null) return new(latest, null);

        if (string.IsNullOrWhiteSpace(cfg.SvdPath)) return default;

        string resolved;
        try
        {
            resolved = cfg.ResolvePath(cfg.SvdPath);
        }
        catch (Exception ex)
        {
            return new(null, $"svd_path '{cfg.SvdPath}' is not a usable path: {ex.Message}");
        }

        if (!File.Exists(resolved))
            return new(null, $"svd_path points to '{resolved}', which does not exist.");

        try
        {
            var device = GetOrParse(resolved);
            Volatile.Write(ref _latest, device);
            return new(device, null);
        }
        catch (InvalidDataException ex)
        {
            return new(null, $"the file at \"svd_path\" could not be parsed: {ex.Message}");
        }
    }

    private static SvdDevice GetOrParse(string resolvedPath)
    {
        var mtime = File.GetLastWriteTimeUtc(resolvedPath);
        lock (Sync)
        {
            if (Cache.TryGetValue(resolvedPath, out var hit) && hit.MtimeUtc == mtime)
                return hit.Device;
        }

        var parsed = SvdFile.ParseFile(resolvedPath);
        lock (Sync)
        {
            Cache[resolvedPath] = (mtime, parsed);
        }
        return parsed;
    }

    /// <summary>“PERIPH.REGISTER”按**第一个**点切：cluster 路径（PWM.CCR）整体归寄存器名。</summary>
    private static bool TrySplit(string name, out string peripheral, out string register)
    {
        peripheral = register = "";
        if (string.IsNullOrWhiteSpace(name)) return false;
        var dot = name.IndexOf('.');
        if (dot <= 0 || dot == name.Length - 1) return false;
        peripheral = name[..dot];
        register = name[(dot + 1)..];
        return true;
    }

    private static string Hex(long value) => $"0x{value:x8}";
}
