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
        "whose low 12 bits are the DEV_ID, looked up in the optional \"chip_id_map\" in ocd.json " +
        "(keys are DEV_ID hex like \"0x410\", values are family names / suggested target_cfg). " +
        "Read-only. If reads fail while the target runs, halt first and resume afterwards (allow_exec). " +
        "Cross-check with flash_info to tell apart same-family variants; falls back to a model inferred from the pinned " +
        "target_cfg filename (heuristic, not a hardware read) when DEV_ID is not decodable. " +
        "NOTE: connect already runs this automatically after a successful connect, so you rarely need to call it manually.")]
    public static async Task<string> IdentifyChip(
        [Description("Vendor ID register address, hex (default '0xE0042000', STM32/AT32 convention).")] string dbgmcuAddress = "0xE0042000",
        CancellationToken ct = default)
    {
        var cfg = ConfigStore.Instance.Current;

        if (!Parsing.TryParseHex(dbgmcuAddress, out _))
            return $"ERROR: dbgmcu_address '{dbgmcuAddress}' is not a hex value. Use a hex string like '0xE0042000'.";

        return await TryIdentifyAsync(cfg, dbgmcuAddress, ct).ConfigureAwait(false)
            ?? "chip identity: unavailable - the target did not answer the reads " +
               "(check power and SWD wiring; if the target runs, a halt/read/resume cycle may be needed). " +
               "retry identify_chip later.";
    }

    /// <summary>
    /// 识别主体的可复用形态：connect 成功后 best-effort 自动调用（短超时由调用方给），
    /// identify_chip 工具也走它。读失败的行以说明文本进块，不抛；
    /// 两个读都没拿到数 → 返回 null（调用方静默跳过，绝不拖垮 connect）。
    /// 拿到数时把完整报告镜像一条 rsp（tool=identify_chip）进流量日志——
    /// DSH 面板的链路状态区靠这条显示芯片（面板侧 parseChipIdentity 解析同款文本）。
    /// </summary>
    public static async Task<string?> TryIdentifyAsync(OcdConfig cfg, string dbgmcuAddress, CancellationToken ct)
    {
        if (!Parsing.TryParseHex(dbgmcuAddress, out var dbgAddr))
            return null;

        var sb = new StringBuilder();
        sb.AppendLine("chip identity:");

        var cpuid = await ReadWord(cfg, "0xE000ED00", "cpu (CPUID, ARM standard)", sb, ct).ConfigureAwait(false);
        if (cpuid is { } c)
            sb.AppendLine($"    -> {Parsing.DescribeCpuid(c)}");

        var idRaw = await ReadWord(cfg, Parsing.Hex(dbgAddr), "dev_id (vendor register)", sb, ct).ConfigureAwait(false);
        string? family = null;
        if (idRaw is { } d)
        {
            var devId = d & 0xFFF;
            var revId = (d >> 16) & 0xFFFF;
            sb.AppendLine($"    -> dev_id 0x{devId:x3}, rev_id 0x{revId:x4}");
            family = MatchChipFamily(cfg.ChipIdMap, devId);
            sb.AppendLine(family is null
                ? "    -> family: unknown - add it to \"chip_id_map\" in ocd.json (key = DEV_ID hex)"
                : $"    -> family: {family}");
        }

        // 钉死 target_cfg 时,文件名本身就说得出对面大概是谁(最佳努力,非硬件读):
        // target_py32f002a_lock.cfg → PY32F002A。DSH 面板链路区的「芯片」段据此显示型号。
        var model = DeriveModelFromTargetCfg(cfg.TargetCfg);
        if (model is not null)
            sb.AppendLine($"  model (from target_cfg {Path.GetFileName(cfg.TargetCfg!)}): {model}");

        // v0.4.0 target 失配:识别出的家族在 target_presets 里、而当前 target_cfg 不是
        // 那一条 → 追加一行可操作的切换提示(DSH 面板据此给「一键切换」胶囊)。
        if (TargetMismatch(cfg.TargetCfg, family, cfg.TargetPresets) is { } mismatch)
            sb.AppendLine($"  target mismatch: identified {family}, current target_cfg: {cfg.TargetCfg ?? "(not set)"} " +
                          $"-> use set_target {mismatch.Suggested}");

        sb.Append("next: flash_info cross-checks the probed flash size (family variants differ); " +
                  "the target_cfg in ocd.json decides which driver OpenOCD loads.");

        if (cpuid is null && idRaw is null)
            return null; // 目标没应答：不回报、不镜像

        var text = sb.ToString();
        TrafficLog.Shared.Record("identify_chip", "rsp", text);
        return text;
    }

    /// <summary>
    /// target 失配检测（v0.4.0，纯函数）：识别出的家族名能对上 target_presets 的某个条目、
    /// 而当前 target_cfg 不是那一条时，返回「建议名 + 预设 cfg」，调用方在 identify 报告里
    /// 追加一行失配提示（DSH 面板据此给「一键切换」胶囊）。不匹配/没预设/family 未知 → null。
    /// 比较按 basename 忽略大小写：绝对路径与 scripts 名（"target/at32f403axx.cfg"）视为同一脚本。
    /// </summary>
    public static (string Suggested, string PresetCfg)? TargetMismatch(
        string? currentTargetCfg, string? family, IReadOnlyDictionary<string, string> presets)
    {
        if (presets.Count == 0 || string.IsNullOrWhiteSpace(family)) return null;
        var name = family!.Trim();
        if (name.StartsWith("unknown", StringComparison.OrdinalIgnoreCase)) return null;
        if (!presets.TryGetValue(name, out var presetCfg) || string.IsNullOrWhiteSpace(presetCfg))
            return null;
        if (SameScript(currentTargetCfg, presetCfg)) return null;
        return (name, presetCfg);
    }

    /// <summary>两个 target 写法是否指向同一个脚本（basename 忽略大小写；空值互不相等）。</summary>
    internal static bool SameScript(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        return string.Equals(Path.GetFileName(a.Trim().Replace('\\', '/')),
            Path.GetFileName(b.Trim().Replace('\\', '/')), StringComparison.OrdinalIgnoreCase);
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

    /// <summary>
    /// 从钉死的 target_cfg 文件名推定芯片型号（最佳努力，不读硬件）：
    /// 'target_py32f002a_lock.cfg' → 'PY32F002A'，'target_stm32f1x.cfg' → 'STM32F1X'。
    /// auto / 空 / 去掉 target_ 与 _lock 修饰后不像型号（无字母数字混排或太短）→ null。
    /// 纯函数，可单测；面板会把结果标注成「由 target_cfg 推定」，与硬件读出的 family 区分。
    /// </summary>
    public static string? DeriveModelFromTargetCfg(string? targetCfg)
    {
        if (string.IsNullOrWhiteSpace(targetCfg)) return null;
        var trimmed = targetCfg.Trim();
        if (string.Equals(trimmed, "auto", StringComparison.OrdinalIgnoreCase)) return null;

        // 取文件名去扩展（两种分隔符都容，测试里会传 D:/.../target_xxx.cfg）
        var fileName = trimmed.Replace('\\', '/');
        var slash = fileName.LastIndexOf('/');
        if (slash >= 0) fileName = fileName[(slash + 1)..];
        var dot = fileName.LastIndexOf('.');
        if (dot > 0) fileName = fileName[..dot];
        if (fileName.Length == 0) return null;

        // 去掉通用修饰词:target_ 前缀、_lock 后缀（大小写不敏感,容许 -/_ 混用）
        var body = fileName;
        if (body.StartsWith("target_", StringComparison.OrdinalIgnoreCase)
            || body.StartsWith("target-", StringComparison.OrdinalIgnoreCase))
            body = body["target_".Length..]; // 连分隔符一起去掉(target_=7)
        var lower = body.ToLowerInvariant();
        foreach (var suffix in new[] { "_lock", "-lock", "_cfg", "-cfg" })
        {
            if (lower.EndsWith(suffix) && body.Length > suffix.Length)
            {
                body = body[..^suffix.Length];
                break;
            }
        }

        // 型号样:至少 4 字符,且字母、数字都出现过(stm32f1x / py32f002a);否则不猜
        if (body.Length < 4) return null;
        var hasDigit = false;
        var hasAlpha = false;
        foreach (var ch in body)
        {
            if (char.IsDigit(ch)) hasDigit = true;
            else if (char.IsLetter(ch)) hasAlpha = true;
        }
        return hasDigit && hasAlpha ? body.ToUpperInvariant() : null;
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
