using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using Ocdup.Backend;
using Ocdup.Config;

namespace Ocdup.Tools;

[McpServerToolType]
public static class ConfigTools
{
    [McpServerTool(Name = "show_config")]
    [Description("Show the effective Ocdup settings (probe/target configs, command port, permissions) and where each value comes from: machine defaults, ocd.json, or session changes. Use this to answer \"why is this setting in effect?\". Set reload=true to re-read ocd.json from disk after editing it.")]
    public static string ShowConfig(
        [Description("Re-read ocd.json from disk instead of using the snapshot from startup (default false).")] bool reload = false)
    {
        var store = ConfigStore.Instance;
        if (reload) store.Reload();
        return store.Current.Describe();
    }

    [McpServerTool(Name = "set_permissions")]
    [Description(
        "Change permission settings at RUNTIME (no ocd.json edit needed). Gated by allow_set_permissions (default false - " +
        "loosening permissions is the human's decision, this key is that authorization; a BLOCKED answer means it is not enabled). " +
        "Pass only the settings you want to change; path whitelists (flash_allowed_paths / dump_allowed_paths) and probe/target " +
        "config are NOT settable here - only via ocd.json. " +
        "Runtime changes are lost on show_config reload=true or a server restart. See show_config afterwards to confirm.")]
    public static string SetPermissions(
        [Description("Master switch: block all target-changing operations.")] bool? readOnly = null,
        [Description("Gate for write_memory.")] bool? allowMemoryWrite = null,
        [Description("Gate for flash_write.")] bool? allowFlash = null,
        [Description("Gate for flash_erase_sector (destructive).")] bool? allowFlashErase = null,
        [Description("Gate for halt/resume/reset.")] bool? allowExec = null,
        [Description("Gate for run_command.")] bool? allowRawCommand = null,
        [Description("Gate for set_permissions itself.")] bool? allowSetPermissions = null,
        [Description("Max image size for flash_write in bytes (0 = unlimited).")] long? flashMaxBytes = null,
        [Description("Max bytes returned per read_memory (0 = unlimited).")] long? memoryReadMaxBytes = null,
        [Description("Max bytes per dump_memory write (0 = unlimited).")] long? dumpMaxBytes = null)
    {
        var store = ConfigStore.Instance;
        var cfg = store.Current;

        if (PermissionGate.CheckSetPermissions(cfg) is { } blocked)
            return blocked;

        var pairs = new List<string>();
        var problems = new List<string>();
        void AddBool(string key, bool? v)
        {
            if (v is { } b) pairs.Add($"\"{key}\": {(b ? "true" : "false")}");
        }
        void AddLong(string key, long? v)
        {
            if (v is not { } n) return;
            if (ValidateLimit(key, n) is { } problem)
            {
                problems.Add(problem); // 负值攒着一起报，不走抛异常——调用方是模型/CI，要的是可操作文案（C3）
                return;
            }
            pairs.Add($"\"{key}\": {n}");
        }

        AddBool("read_only", readOnly);
        AddBool("allow_memory_write", allowMemoryWrite);
        AddBool("allow_flash", allowFlash);
        AddBool("allow_flash_erase", allowFlashErase);
        AddBool("allow_exec", allowExec);
        AddBool("allow_raw_command", allowRawCommand);
        AddBool("allow_set_permissions", allowSetPermissions);
        AddLong("flash_max_bytes", flashMaxBytes);
        AddLong("memory_read_max_bytes", memoryReadMaxBytes);
        AddLong("dump_max_bytes", dumpMaxBytes);

        if (problems.Count > 0)
            return string.Join("\n", problems);

        if (pairs.Count == 0)
            return "ERROR: no changes requested. Pass at least one permission to change (see the tool parameters).";

        string json = "{ " + string.Join(", ", pairs) + " }";
        OcdConfig next;
        try
        {
            next = store.ApplyRuntimePatch(json);
        }
        catch (Exception ex)
        {
            return $"ERROR: could not apply the permission change: {ex.Message}";
        }

        var changed = string.Join(", ", pairs.Select(p => p.Split(':')[0].Trim('"')));
        var sb = new StringBuilder();
        sb.AppendLine($"updated (runtime): {changed}");
        sb.AppendLine(next.Describe());
        sb.Append("note: runtime changes are discarded by show_config reload=true or a server restart; " +
                  "path whitelists and probe/target config only change via ocd.json.");
        return sb.ToString();
    }

    /// <summary>
    /// 数值上限的取值校验（纯函数，可单测）：负值返回可直接展示的 ERROR 文案而不是抛异常——
    /// set_permissions 的调用方是模型或 CI 脚本，异常只会得到一句裸 message（C3 约定破例）。
    /// </summary>
    internal static string? ValidateLimit(string key, long value) =>
        value < 0
            ? $"ERROR: '{key}' must be >= 0 (0 = unlimited where the setting supports it). " +
              "Pass a non-negative value, or omit the parameter to leave it unchanged. See show_config."
            : null;
}

