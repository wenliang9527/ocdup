using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ocdup;

/// <summary>
/// 把 ocdup 与 OpenOCD 的每次交互镜像到本地 JSONL(%LOCALAPPDATA%\Ocdup\mcp-traffic.jsonl):
/// TCL 命令/响应、后端生命周期事件。字段与 acccom 的 mcp-traffic.jsonl 同一套
/// (id/tool/timestamp/direction/rawHex/text/portTag),将来接侧边栏面板时,
/// DSH 插件(如 dsh-serial-panel)的尾随/增量逻辑可以原样复用——唯一真源 = 日志文件。
///
/// ocdup 的流量是低频「命令-响应」(一次会话几十条),所以不需要 acccom 版本为串口
/// 热路径做的批量刷盘与内存环:AutoFlush 式逐条落盘 + 按大小轮转即可,正确性优先。
/// </summary>
public sealed partial class TrafficLog : IDisposable
{
    private readonly object _lock = new();
    private StreamWriter? _writer;
    private readonly string _filePath;
    private readonly long _maxFileBytes;
    private readonly int _maxTextChars;
    private bool _disposed;

    /// <summary>日志文件的默认位置(固定名,未来面板不用扫描目录)。</summary>
    public static string DefaultLogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Ocdup", "mcp-traffic.jsonl");

    public string FilePath => _filePath;

    /// <summary>进程级单例。lazy 创建(首次工具调用才打开文件,纯探测会话不产生文件),
    /// ProcessExit 时排空缓冲。</summary>
    private static readonly Lazy<TrafficLog> _shared = new(() =>
    {
        var log = new TrafficLog();
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { log.Dispose(); }
            catch { /* 退出中:绝不抛 */ }
        };
        return log;
    });

    public static TrafficLog Shared => _shared.Value;

    public TrafficLog(string? filePath = null, long maxFileBytes = 2 * 1024 * 1024, int maxTextChars = 4000)
    {
        _filePath = filePath ?? DefaultLogPath;
        _maxFileBytes = maxFileBytes;
        _maxTextChars = maxTextChars;
        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
    }

    /// <summary>追加一条镜像记录。任何失败都吞掉——日志绝不能影响工具调用本身。</summary>
    public void Record(string tool, string direction, string text, string tag = "")
    {
        if (_disposed) return;
        try
        {
            lock (_lock)
            {
                if (_writer is null)
                    _writer = OpenWriterLocked();

                _writer.WriteLine(JsonSerializer.Serialize(new TrafficRecord
                {
                    tool = tool,
                    timestamp = DateTime.Now.ToString("o"),
                    direction = direction,
                    text = Truncate(text),
                    portTag = tag,
                }, TrafficJsonContext.Default.TrafficRecord));
                _writer.Flush();

                RotateIfNeededLocked();
            }
        }
        catch
        {
            // 磁盘满/路径问题等:记不了就算了,下一次 Record 会重试打开
            try { _writer?.Dispose(); } catch { /* 尽力而为 */ }
            _writer = null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_lock)
        {
            try { _writer?.Flush(); } catch { /* 尽力而为 */ }
            _writer?.Dispose();
            _writer = null;
        }
    }

    private StreamWriter OpenWriterLocked()
    {
        // FileShare 读写删:面板/编辑器可以同时尾随这个文件(对齐 acccom 的共享语义)。
        var fs = new FileStream(_filePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        return new StreamWriter(fs) { AutoFlush = false };
    }

    /// <summary>超过大小就轮转成 .1 备份再开新文件,总量有界。写完之后检查:
    /// 低频场景下多出的一次超限无感,换来零额外系统调用。</summary>
    private void RotateIfNeededLocked()
    {
        if (_writer is null || _maxFileBytes <= 0) return;
        if (_writer.BaseStream.Length <= _maxFileBytes) return;

        _writer.Dispose();
        _writer = null;
        try
        {
            if (File.Exists(_filePath + ".1"))
                File.Delete(_filePath + ".1");
            if (File.Exists(_filePath))
                File.Move(_filePath, _filePath + ".1");
        }
        catch
        {
            // 轮转失败非致命:下次超限再试;至少当前 writer 已关,不会写坏文件
        }
    }

    private string Truncate(string text) =>
        text.Length <= _maxTextChars ? text : text[.._maxTextChars] + $"…(+{text.Length - _maxTextChars} chars)";

    /// <summary>一行 JSON 的形状。字段名与 acccom 的 mcp-traffic.jsonl 完全一致
    /// (id 对 ocdup 无意义,恒为 0,留给面板按行号分配游标)。</summary>
    private sealed class TrafficRecord
    {
        public int id { get; set; }
        public string tool { get; set; } = "";
        public string timestamp { get; set; } = "";
        public string direction { get; set; } = "";
        public string rawHex { get; set; } = "";
        public string text { get; set; } = "";
        public string portTag { get; set; } = "";
    }

    [JsonSourceGenerationOptions]
    [JsonSerializable(typeof(TrafficRecord))]
    private partial class TrafficJsonContext : JsonSerializerContext;
}
