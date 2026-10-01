using System.IO;
using System.Text.Json;

namespace Ocdup.Config;

/// <summary>
/// 项目配置（阶段一：烧录 + 读内存）。
/// 分层：机器默认 → ocd.json（CWD 自动加载；OCD_CONFIG 可指路径）→ 运行时修改 → 工具参数。
/// 本阶段实现前两层（运行时修改的入口见 <see cref="ConfigStore"/>，工具参数由工具层处理）。
///
/// 两个关键约定：
/// 1) 每项值的来源按「JSON 里是否存在该属性」判定，而不是「值是否等于默认值」——
///    所以显式写 "read_only": false 也会如实标为来自 ocd.json，而不是错标成默认值。
/// 2) 权限项支持两种写法，permissions 嵌套块优先（设计文档 4.4 的规范写法）：
///      顶层平铺：{ "read_only": true }
///      嵌套块：  { "permissions": { "read_only": true } }
///
/// 相对路径的解析基准是 ocd.json 所在目录（设计文档 Q7 定案），见 <see cref="ResolvePath"/>。
/// 例外：interface_cfg / target_cfg 不是本地路径，而是 OpenOCD scripts 目录下的脚本名，
/// 由 openocd 的 -s 参数解析，不经过 ResolvePath。
/// </summary>
public sealed class OcdConfig
{
    public const string DefaultHost = "127.0.0.1";
    public const int DefaultPort = 6666;
    public const int DefaultCommandTimeoutSeconds = 60;
    public const long DefaultMemoryReadMaxBytes = 4096;

    private readonly List<string> _errors = new();

    // --- 项目设置（snake_case，与设计文档 7.2 节的 ocd.json 一致） ---
    public string? InterfaceCfg { get; private set; }
    public string? TargetCfg { get; private set; }
    public string? Transport { get; private set; }

    /// <summary>interface_cfg = "auto":连接阶段按 USB 设备自动识别探针并选择接口脚本。</summary>
    public bool IsInterfaceAuto => string.Equals(InterfaceCfg, "auto", StringComparison.OrdinalIgnoreCase);

    // --- 控制通道 ---
    public string Host { get; private set; } = DefaultHost;
    public int Port { get; private set; } = DefaultPort;
    public int CommandTimeoutSeconds { get; private set; } = DefaultCommandTimeoutSeconds;

    // --- 权限（设计文档 4.4） ---
    public bool ReadOnly { get; private set; }
    public bool AllowMemoryWrite { get; private set; } = true;
    public bool AllowFlash { get; private set; } = true;
    public bool AllowFlashErase { get; private set; }
    public bool AllowRawCommand { get; private set; } = true;
    public List<string> FlashAllowedPaths { get; private set; } = new();
    public long FlashMaxBytes { get; private set; }
    public long MemoryReadMaxBytes { get; private set; } = DefaultMemoryReadMaxBytes;

    /// <summary>生效的 ocd.json 绝对路径（null = 没找到配置文件）。</summary>
    public string? ConfigFilePath { get; private set; }

    /// <summary>每项设置的实际来源，如 "(default)" 或 "ocd.json (C:\proj\ocd.json)"。</summary>
    public Dictionary<string, string> Sources { get; } = new();

    private static readonly JsonDocumentOptions ParseOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly string[] TrackedKeys =
    {
        "interface_cfg", "target_cfg", "transport", "host", "port", "command_timeout_seconds",
        "read_only", "allow_memory_write", "allow_flash", "allow_flash_erase", "allow_raw_command",
        "flash_allowed_paths", "flash_max_bytes", "memory_read_max_bytes",
    };

    /// <summary>按分层顺序加载：OCD_CONFIG 指定的文件，否则 CWD 下的 ocd.json。</summary>
    public static OcdConfig Load()
    {
        var requested = Environment.GetEnvironmentVariable("OCD_CONFIG");
        if (string.IsNullOrWhiteSpace(requested)) requested = "ocd.json";
        return LoadFrom(requested);
    }

    /// <summary>从指定路径加载（便于测试：不依赖环境变量与 CWD）。</summary>
    public static OcdConfig LoadFrom(string path)
    {
        var cfg = new OcdConfig();
        foreach (var key in TrackedKeys) cfg.Sources[key] = "(default)";

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception ex)
        {
            cfg.NoteError($"ocd.json path '{path}' is invalid: {ex.Message}");
            return cfg;
        }

        // 路径基准在解析之前就固定下来：即使 JSON 内容坏了，相对路径的基准仍然有效。
        cfg.ConfigFilePath = fullPath;

        if (!File.Exists(fullPath))
        {
            cfg.ConfigFilePath = null;
            return cfg;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(fullPath), ParseOptions);
            var source = $"ocd.json ({fullPath})";
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                cfg.NoteError($"{fullPath}: the top level must be a JSON object");
                return cfg;
            }

            cfg.ApplyFrom(root, source);

            // permissions 嵌套块（规范写法），优先级高于顶层平铺。
            if (root.TryGetProperty("permissions", out var perms))
            {
                if (perms.ValueKind == JsonValueKind.Object)
                    cfg.ApplyFrom(perms, source);
                else
                    cfg.NoteError($"{fullPath}: 'permissions' must be a JSON object");
            }
        }
        catch (Exception ex)
        {
            // 配置文件坏不该让服务器起不来；show_config 会把错误带出去（降级为默认值）。
            cfg.NoteError($"{fullPath}: {ex.Message}");
        }

        return cfg;
    }

    private void ApplyFrom(JsonElement obj, string source)
    {
        TakeString(obj, "interface_cfg", source, v => InterfaceCfg = v);
        TakeString(obj, "target_cfg", source, v => TargetCfg = v);
        TakeString(obj, "transport", source, v => Transport = v);
        TakeString(obj, "host", source, v => Host = v, reject: string.IsNullOrWhiteSpace);

        TakeInt(obj, "port", source, v => Port = v, min: 1, max: 65535);
        TakeInt(obj, "command_timeout_seconds", source, v => CommandTimeoutSeconds = v, min: 1, max: 3600);

        TakeBool(obj, "read_only", source, v => ReadOnly = v);
        TakeBool(obj, "allow_memory_write", source, v => AllowMemoryWrite = v);
        TakeBool(obj, "allow_flash", source, v => AllowFlash = v);
        TakeBool(obj, "allow_flash_erase", source, v => AllowFlashErase = v);
        TakeBool(obj, "allow_raw_command", source, v => AllowRawCommand = v);

        TakeStringArray(obj, "flash_allowed_paths", source, v => FlashAllowedPaths = v);
        TakeLong(obj, "flash_max_bytes", source, v => FlashMaxBytes = v, min: 0);
        TakeLong(obj, "memory_read_max_bytes", source, v => MemoryReadMaxBytes = v, min: 0);
    }

    private void TakeString(JsonElement obj, string key, string source, Action<string> set, Func<string, bool>? reject = null)
    {
        if (!obj.TryGetProperty(key, out var el)) return;
        if (el.ValueKind != JsonValueKind.String)
        {
            NoteError($"'{key}' must be a string");
            return;
        }

        var value = el.GetString() ?? "";
        if (reject?.Invoke(value) == true)
        {
            NoteError($"'{key}' must not be empty");
            return;
        }

        set(value);
        Sources[key] = source;
    }

    private void TakeBool(JsonElement obj, string key, string source, Action<bool> set)
    {
        if (!obj.TryGetProperty(key, out var el)) return;
        if (el.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            NoteError($"'{key}' must be true or false");
            return;
        }

        set(el.GetBoolean());
        Sources[key] = source;
    }

    private void TakeInt(JsonElement obj, string key, string source, Action<int> set, int min, int max)
    {
        if (!obj.TryGetProperty(key, out var el)) return;
        if (el.ValueKind != JsonValueKind.Number || !el.TryGetInt32(out var value) || value < min || value > max)
        {
            NoteError($"'{key}' must be an integer between {min} and {max}");
            return;
        }

        set(value);
        Sources[key] = source;
    }

    private void TakeLong(JsonElement obj, string key, string source, Action<long> set, long min)
    {
        if (!obj.TryGetProperty(key, out var el)) return;
        if (el.ValueKind != JsonValueKind.Number || !el.TryGetInt64(out var value) || value < min)
        {
            NoteError($"'{key}' must be an integer >= {min} (0 = unlimited)");
            return;
        }

        set(value);
        Sources[key] = source;
    }

    private void TakeStringArray(JsonElement obj, string key, string source, Action<List<string>> set)
    {
        if (!obj.TryGetProperty(key, out var el)) return;
        if (el.ValueKind != JsonValueKind.Array)
        {
            NoteError($"'{key}' must be an array of strings");
            return;
        }

        var list = new List<string>();
        foreach (var item in el.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                NoteError($"'{key}' must contain only strings");
                return;
            }
            list.Add(item.GetString()!);
        }

        set(list);
        Sources[key] = source;
    }

    private void NoteError(string message)
    {
        _errors.Add(message);
        Sources["ocd_json_error"] = string.Join("; ", _errors);
    }

    /// <summary>
    /// 把配置里的相对路径解析为绝对路径：基准是 ocd.json 所在目录，没有配置文件时退回 CWD。
    /// 适用于镜像文件、flash_allowed_paths 等本地路径。
    /// </summary>
    public string ResolvePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (Path.IsPathRooted(path)) return Path.GetFullPath(path);

        var baseDir = ConfigFilePath is not null
            ? Path.GetDirectoryName(ConfigFilePath)!
            : Directory.GetCurrentDirectory();
        return Path.GetFullPath(Path.Combine(baseDir, path));
    }

    /// <summary>给人/模型看的有效设置报告，每项带来源（设计文档 4.3）。</summary>
    public string Describe()
    {
        var lines = new List<string>
        {
            $"config file:     {ConfigFilePath ?? "(none found - run ocdup from the project dir, or point OCD_CONFIG at an ocd.json)"}",
            $"interface_cfg:   {InterfaceCfg ?? "(not set)"}   [src: {Src("interface_cfg")}]",
            $"target_cfg:      {TargetCfg ?? "(not set)"}   [src: {Src("target_cfg")}]",
            $"transport:       {Transport ?? "(probe default)"}   [src: {Src("transport")}]",
            $"command_port:    {Host}:{Port}   [src: {Src("port")}]",
            $"command_timeout: {CommandTimeoutSeconds}s   [src: {Src("command_timeout_seconds")}]",
            "permissions:",
            $"  read_only             = {ReadOnly}   [src: {Src("read_only")}]",
            $"  allow_memory_write    = {AllowMemoryWrite}   [src: {Src("allow_memory_write")}]",
            $"  allow_flash           = {AllowFlash}   [src: {Src("allow_flash")}]",
            $"  allow_flash_erase     = {AllowFlashErase}   [src: {Src("allow_flash_erase")}]",
            $"  allow_raw_command     = {AllowRawCommand}   [src: {Src("allow_raw_command")}]",
            $"  flash_allowed_paths   = {(FlashAllowedPaths.Count == 0 ? "(unrestricted)" : string.Join(", ", FlashAllowedPaths))}   [src: {Src("flash_allowed_paths")}]",
            $"  flash_max_bytes       = {(FlashMaxBytes == 0 ? "(unlimited)" : FlashMaxBytes.ToString())}   [src: {Src("flash_max_bytes")}]",
            $"  memory_read_max_bytes = {(MemoryReadMaxBytes == 0 ? "(unlimited)" : MemoryReadMaxBytes.ToString())}   [src: {Src("memory_read_max_bytes")}]",
        };

        if (Sources.TryGetValue("ocd_json_error", out var error))
            lines.Add($"WARNING: ocd.json could not be fully applied ({error}); affected values fall back to defaults as shown above.");

        return string.Join("\n", lines);
    }

    private string Src(string key) => Sources.GetValueOrDefault(key, "?");
}

