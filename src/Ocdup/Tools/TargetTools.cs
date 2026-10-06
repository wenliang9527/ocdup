using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using Ocdup.Backend;
using Ocdup.Config;

namespace Ocdup.Tools;

/// <summary>
/// target 切换（v0.4.0）：按 ocd.json 的 "target_presets" 注册表把 target_cfg 换成
/// 指定预设并重启后端。target_cfg 是 openocd 的启动参数（-f），所以切换必须重启——
/// 本工具是 ocdup 唯一的「换 target」通道（配置层另有 set_permissions 的权限补丁，
/// 两条通道互不覆盖：target_cfg 刻意不在 RuntimePatchableKeys 里）。
/// </summary>
[McpServerToolType]
public static class TargetTools
{
    [McpServerTool(Name = "set_target")]
    [Description(
        "Switch the OpenOCD target_cfg to an entry from \"target_presets\" in ocd.json and restart the backend " +
        "(target_cfg is an openocd startup argument, so switching means restarting openocd - it only restarts the " +
        "backend ocdup itself started). Use it when identify_chip reports a 'target mismatch' line: the identified " +
        "family is in target_presets but the running target_cfg is a different chip. Read-only sessions are blocked. " +
        "Pass 'list' to see the configured presets.")]
    public static async Task<string> SetTarget(
        [Description("Preset name from ocd.json's \"target_presets\" (case-insensitive), or 'list'.")] string name,
        CancellationToken ct = default)
    {
        var cfg = ConfigStore.Instance.Current;
        var (error, presetCfg, suggested) = ResolvePreset(cfg, name);
        if (error is not null || presetCfg is null || suggested is null)
            return error ?? "ERROR: could not resolve the target preset.";

        if (IdentifyTools.SameScript(cfg.TargetCfg, presetCfg))
            return $"target is already '{suggested}' (target_cfg: {cfg.TargetCfg}) - no restart needed.";

        // 切换 = 重启自拉后端，属于改变后端状态的操作：过 read_only 总闸。
        if (cfg.ReadOnly)
            return "BLOCKED: 'set_target' restarts the OpenOCD backend, which read_only forbids. " +
                   "To switch targets, set \"read_only\": false in ocd.json (top level or inside the permissions block), " +
                   "or start ocdup with OCD_READONLY unset. See show_config.";

        ConfigStore.Instance.ApplyTargetSwitch(presetCfg);

        string note;
        try
        {
            note = await BackendSession.Instance.RestartAsync(ct).ConfigureAwait(false);
        }
        catch (BackendException ex)
        {
            // 起不来就回滚 target_cfg，别留下「配置说切了、后端还是旧的」的悬空状态
            if (!string.IsNullOrWhiteSpace(cfg.TargetCfg))
                ConfigStore.Instance.ApplyTargetSwitch(cfg.TargetCfg);
            return $"ERROR: the backend did not come back up with target '{suggested}' ({presetCfg}): {ex.Message}\n" +
                   (string.IsNullOrWhiteSpace(cfg.TargetCfg)
                       ? "NOTE: the previous target_cfg was empty, so nothing was rolled back."
                       : $"target_cfg was rolled back to {cfg.TargetCfg}. Fix the preset path or the probe, then retry.");
        }

        TrafficLog.Shared.Record("set_target", "SYS", $"target switched to '{suggested}' -> {presetCfg}", $"{cfg.Host}:{cfg.Port}");

        var sb = new StringBuilder();
        sb.AppendLine($"target switched to '{suggested}' -> {presetCfg}");
        sb.AppendLine($"backend: {note}");
        if (note.Contains("already", StringComparison.OrdinalIgnoreCase))
            sb.AppendLine($"WARNING: the listener on {cfg.Host}:{cfg.Port} was started outside ocdup, so it still runs the " +
                          $"OLD target_cfg (target is a startup argument and ocdup cannot restart a process it does not own). " +
                          $"Stop that openocd (or let it exit) so ocdup can start its own with '{suggested}'.");
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// 预设名解析（纯函数，可单测；不碰后端与配置单例）：
    /// 成功 → (null, presetCfg, 命中的键)；失败 → (可直接给模型看的文案, null, null)。
    /// 'list' 是保留字：回清单不报错。
    /// </summary>
    internal static (string? Error, string? PresetCfg, string? Suggested) ResolvePreset(OcdConfig cfg, string? name)
    {
        var wanted = (name ?? string.Empty).Trim();

        if (wanted.Equals("list", StringComparison.OrdinalIgnoreCase))
            return (cfg.TargetPresets.Count == 0
                ? "no target presets configured: add a \"target_presets\" object to ocd.json (family name -> openocd target cfg). See show_config."
                : $"target presets ({cfg.TargetPresets.Count}):\n{Catalog(cfg)}", null, null);

        if (wanted.Length == 0)
            return ("ERROR: name is required (a target_presets key, or 'list')." +
                    (cfg.TargetPresets.Count > 0 ? $"\nAvailable:\n{Catalog(cfg)}" : ""), null, null);

        if (cfg.TargetPresets.Count == 0)
            return ("ERROR: ocd.json has no \"target_presets\" entries, so there is nothing to switch to. " +
                    "Add e.g. \"target_presets\": { \"AT32F403A\": \"target/at32f403axx.cfg\" } and retry. See show_config.", null, null);

        if (!cfg.TargetPresets.TryGetValue(wanted, out var presetCfg) || string.IsNullOrWhiteSpace(presetCfg))
            return ($"ERROR: '{wanted}' is not a configured target preset. Available:\n{Catalog(cfg)}", null, null);

        // 命中键按字典的实际大小写返回（用户传小写也显示规范名）
        var key = cfg.TargetPresets.Keys.First((k) => string.Equals(k, wanted, StringComparison.OrdinalIgnoreCase));
        return (null, presetCfg, key);
    }

    private static string Catalog(OcdConfig cfg) =>
        string.Join("\n", cfg.TargetPresets.Select((p) => $"  {p.Key} -> {p.Value}"));
}
