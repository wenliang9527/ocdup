using System.IO;

namespace Ocdup.Config;

/// <summary>
/// 变更类操作的统一闸门（设计文档 4.4 / 不变量 C2：检查必须前置到操作第一步）。
/// 每个 Check* 返回 null 表示放行，否则返回可直接交给模型看的 BLOCKED 文案（6.3 约定：
/// 拦截报错必须带「如何解除拦截」的指引）。
/// </summary>
public static class PermissionGate
{
    /// <summary>总闸：read_only 拦住所有会写目标的操作。</summary>
    public static string? CheckAnyWrite(OcdConfig cfg, string op) =>
        cfg.ReadOnly
            ? Blocked(op, "read_only",
                "set \"read_only\": false in ocd.json (top level or inside the \"permissions\" block), or start ocdup with a different OCD_CONFIG")
            : null;

    /// <summary>存储器/寄存器写（write_memory 等，工具层后续接入）。</summary>
    public static string? CheckMemoryWrite(OcdConfig cfg, string op = "memory write") =>
        CheckAnyWrite(cfg, op) ??
        (cfg.AllowMemoryWrite
            ? null
            : Blocked(op, "allow_memory_write", "set \"allow_memory_write\": true in the ocd.json permissions block"));

    /// <summary>烧录总闸（含路径白名单与大小上限）。</summary>
    public static string? CheckFlashWrite(OcdConfig cfg, string resolvedPath, long sizeBytes)
    {
        if (CheckAnyWrite(cfg, "flash write") is { } readOnlyBlock) return readOnlyBlock;

        if (!cfg.AllowFlash)
            return Blocked("flash write", "allow_flash", "set \"allow_flash\": true in the ocd.json permissions block");

        if (cfg.FlashMaxBytes > 0 && sizeBytes > cfg.FlashMaxBytes)
            return $"BLOCKED: the image is {sizeBytes} bytes, above the flash_max_bytes limit of {cfg.FlashMaxBytes}. " +
                   "Raise \"flash_max_bytes\" in ocd.json (0 = unlimited) if this is intended. See show_config.";

        if (cfg.FlashAllowedPaths.Count > 0 && !IsInsideAny(cfg, resolvedPath, cfg.FlashAllowedPaths))
            return $"BLOCKED: '{resolvedPath}' is outside flash_allowed_paths ({string.Join(", ", cfg.FlashAllowedPaths)}). " +
                   "Move the image into one of those directories, or extend \"flash_allowed_paths\" in ocd.json. See show_config.";

        return null;
    }

    /// <summary>擦除（破坏性，默认关闭；工具层后续接入）。</summary>
    public static string? CheckFlashErase(OcdConfig cfg) =>
        CheckAnyWrite(cfg, "flash erase") ??
        (cfg.AllowFlashErase
            ? null
            : Blocked("flash erase", "allow_flash_erase", "set \"allow_flash_erase\": true in the ocd.json permissions block"));

    /// <summary>原始命令逃生舱（工具层后续接入）。</summary>
    public static string? CheckRawCommand(OcdConfig cfg) =>
        CheckAnyWrite(cfg, "run_command") ??
        (cfg.AllowRawCommand
            ? null
            : Blocked("run_command", "allow_raw_command", "set \"allow_raw_command\": true in the ocd.json permissions block"));

    /// <summary>
    /// 执行控制（halt/resume/reset，M-exec）：不写内存，但改变目标执行状态——
    /// 对在跑的固件是可感知干预，与写操作同级设防（read_only 总闸 + allow_exec 独立开关）。
    /// </summary>
    public static string? CheckExec(OcdConfig cfg, string op = "exec control") =>
        CheckAnyWrite(cfg, op) ??
        (cfg.AllowExec
            ? null
            : Blocked(op, "allow_exec", "set \"allow_exec\": true in the ocd.json permissions block"));

    /// <summary>
    /// set_permissions 的闸（独立的 allow_set_permissions，默认 false）：放宽权限是人的决定，
    /// 模型只有在 ocd.json 里被显式授权后才可运行时改权限。刻意不挂 read_only 总闸——
    /// 否则被 read_only 锁死的会话永远无法解除；allow_set_permissions 本身就是那份人类授权。
    /// </summary>
    public static string? CheckSetPermissions(OcdConfig cfg) =>
        cfg.AllowSetPermissions
            ? null
            : Blocked("set_permissions", "allow_set_permissions",
                "set \"allow_set_permissions\": true in the ocd.json permissions block " +
                "(this authorizes the model to change permissions at runtime)");

    /// <summary>读操作的体积上限：不是安全边界，而是防呆（防止一次读回上百 KB 文本淹掉上下文）。</summary>
    public static string? CheckReadLimit(OcdConfig cfg, long bytes, string op)
    {
        if (cfg.MemoryReadMaxBytes <= 0 || bytes <= cfg.MemoryReadMaxBytes) return null;
        return $"BLOCKED: '{op}' would return {bytes} bytes, above the memory_read_max_bytes limit of {cfg.MemoryReadMaxBytes}. " +
               "Read in smaller chunks, or raise \"memory_read_max_bytes\" in ocd.json (0 = unlimited). See show_config.";
    }

    /// <summary>
    /// dump_memory 落盘闸：目标侧是读，PC 侧是写——后端会按给定路径建/覆盖文件，
    /// 长度直接来自模型参数。按「写入面」设防：路径白名单 + 体积上限（0 = 不限）。
    /// 不挂 read_only 总闸：dump 正是只读分析 flash 的手段，拦它就自相矛盾了。
    /// </summary>
    public static string? CheckDumpWrite(OcdConfig cfg, string resolvedPath, long lengthBytes)
    {
        if (cfg.DumpMaxBytes > 0 && lengthBytes > cfg.DumpMaxBytes)
            return $"BLOCKED: the dump would write {lengthBytes} bytes, above the dump_max_bytes limit of {cfg.DumpMaxBytes}. " +
                   "Dump in smaller ranges, or raise \"dump_max_bytes\" in ocd.json (0 = unlimited). See show_config.";

        if (cfg.DumpAllowedPaths.Count > 0 && !IsInsideAny(cfg, resolvedPath, cfg.DumpAllowedPaths))
            return $"BLOCKED: '{resolvedPath}' is outside dump_allowed_paths ({string.Join(", ", cfg.DumpAllowedPaths)}). " +
                   "Dump into one of those directories, or extend \"dump_allowed_paths\" in ocd.json. See show_config.";

        return null;
    }

    private static bool IsInsideAny(OcdConfig cfg, string path, IEnumerable<string> roots)
    {
        var target = Path.GetFullPath(path);
        foreach (var root in roots)
        {
            string full;
            try { full = cfg.ResolvePath(root); }
            catch { continue; } // 配错的项不该让整个检查崩掉

            var relative = Path.GetRelativePath(full, target);
            if (relative == ".." || Path.IsPathRooted(relative)) continue;
            if (relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)) continue;
            if (relative.StartsWith("../", StringComparison.Ordinal)) continue;
            return true;
        }
        return false;
    }

    private static string Blocked(string op, string setting, string howToEnable) =>
        $"BLOCKED: '{op}' is not permitted by the current safety settings ('{setting}'). To allow it, {howToEnable}. See show_config.";
}
