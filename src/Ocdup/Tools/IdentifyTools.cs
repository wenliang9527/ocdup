using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using Ocdup.Backend;
using Ocdup.Config;

namespace Ocdup.Tools;

/// <summary>
/// 目标芯片身份识别：连上后回答「对面到底是谁」。
/// 读 ARM 标准的 CPUID（任何 Cortex-M 都有，解码内核型号）+ 厂商惯例的 DBGMCU_IDCODE
/// （低 12 位 DEV_ID 标识器件家族；地址默认 0xE0042000——STM32/AT32 惯例，可参数覆盖）。
/// DEV_ID -> 家族名的映射外置在 ocd.json 的 chip_id_map（C4：不把具体芯片家族硬编码进程序）。
/// </summary>
[McpServerToolType]
public static class IdentifyTools
{
    [McpServerTool(Name = "identify_chip")]
    [Description(
        "Identify the connected chip: read the ARM-standard CPUID at 0xE000ED00 (decodes the Cortex-M core - reliable on every Cortex-M) " +
        "and the vendor ID register at dbgmcu_address (default '0xE0042000', the STM32/AT32 DBGMCU_IDCODE convention; override for other vendors), " +
        "whose low 12 bits are the device ID (DEV_ID). DEV_ID is looked up in the optional \"chip_id_map\" in ocd.json " +
        "(keys are DEV_ID hex like \"0x410\", values are family names / suggested target_cfg). " +
        "Read-only. If reads fail while the target runs, halt first and resume afterwards (allow_exec). " +
        "Cross-check with flash_info: the probed flash size helps tell apart same-family variants.")]
    public static async Task<string> IdentifyChip(
        [Description("Vendor ID register address, hex string (default '0xE0042000', STM32/AT32 DBGMCU_IDCODE convention).")] string dbgmcuAddress = "0xE0042000",
        CancellationToken ct = default)
    {
        var cfg = ConfigStore.Instance.Current;

        if (!Parsing.TryParseHex(dbgmcuAddress, out var dbgAddr))
            return $"ERROR: dbgmcu_address '{dbgmcuAddress}' is not a hex value. Use a hex string like '0xE0042000'.";

        var sb = new StringBuilder();
        sb.AppendLine("chip identity:");

        var cpuid = await ReadWord(cfg, "0xE000ED00", "cpu (CPUID, ARM standard)", sb, ct);
        if (cpuid is { } c)
            sb.AppendLine($"    -> {Parsing.DescribeCpuid(c)}");

        var idRaw = await ReadWord(cfg, Parsing.Hex(dbgAddr), "dev_id (vendor register)", sb, ct);
        if (idRaw is { } d)
        {
            var devId = d & 0xFFF;
            var revId = (d >> 16) & 0xFFFF;
            sb.AppendLine($"    -> dev_id 0x{devId:x3}, rev_id 0x{revId:x4}");
            var family = MatchChipFamily(cfg.ChipIdMap, devId);
            sb.AppendLine(family is null
                ? "    -> family: unknown - add it to \"chip_id_map\" in ocd.json (key = DEV_ID hex)"
                : $"    -> family: {family}");
        }

        sb.Append("next: flash_info cross-checks the probed flash size (family variants differ); " +
                  "the target_cfg in ocd.json decides which driver OpenOCD loads.");
        return sb.ToString();
    }

    /// <summary>在 chip_id_map 里按 DEV_ID 数值匹配（键是 hex 字符串，容许前导零/大小写差异）。纯函数，可单测。</summary>
    public static string? MatchChipFamily(IReadOnlyDictionary<string, string> map, long devId)
    {
        foreach (var (key, value) in map)
        {
            if (Parsing.TryParseHex(key, out var parsed) && parsed == devId)
                return value;
        }
        return null;
    }

    /// <summary>读一个 32 位字。读失败不硬失败（该地址因厂商而异），把原因带回报告。</summary>
    private static async Task<long?> ReadWord(OcdConfig cfg, string address, string label, StringBuilder sb, CancellationToken ct)
    {
        try
        {
            var output = (await BackendSession.Instance
                .ExecuteAsync(cfg, $"mdw {address} 1", startIfNeeded: true, ct, op: "identify_chip")).Trim();
            if (Parsing.TryParseMemoryOutput(output, 32, 1, out var values))
            {
                sb.AppendLine($"  {label} @ {address}: 0x{values[0]:x8}");
                return values[0];
            }
            sb.AppendLine($"  {label} @ {address}: (no parsable response){(output.Length == 0 ? "" : $" raw: {output.Replace("\n", " | ")}")}");
            return null;
        }
        catch (BackendException ex)
        {
            sb.AppendLine($"  {label} @ {address}: (read failed) {ex.Message.Split('\n')[0]}");
            return null;
        }
    }
}
