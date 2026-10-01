using System.IO;
using System.Text.Json;
using Ocdup.Backend;

namespace Ocdup.Tests;

/// <summary>
/// 流量镜像(TrafficLog):JSONL 行格式与 acccom 的 mcp-traffic.jsonl 同字段,
/// 文本截断、按大小轮转、Dispose 后静默。
/// </summary>
public sealed class TrafficLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ocdup-traffic-" + Guid.NewGuid().ToString("n"));

    public TrafficLogTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* 清理失败不影响结论 */ }
    }

    private string PathFor(string name) => Path.Combine(_dir, name);

    [Fact]
    public void Record_WritesJsonLinesWithAcccomSchema()
    {
        var path = PathFor("traffic.jsonl");
        using (var log = new TrafficLog(path))
        {
            log.Record("read_memory", "cmd", "mdw 0x20000000 16");
            log.Record("read_memory", "rsp", "0x20000000: 11111111 22222222");
        }

        var lines = File.ReadAllLines(path);
        Assert.Equal(2, lines.Length);

        using var doc = JsonDocument.Parse(lines[0]);
        var root = doc.RootElement;
        Assert.Equal("read_memory", root.GetProperty("tool").GetString());
        Assert.Equal("cmd", root.GetProperty("direction").GetString());
        Assert.Contains("mdw 0x20000000", root.GetProperty("text").GetString());
        Assert.True(root.TryGetProperty("timestamp", out _));
        // 与 acccom 同字段(id/rawHex/portTag 对 ocdup 是占位),面板尾随逻辑可原样复用
        foreach (var field in new[] { "id", "tool", "timestamp", "direction", "rawHex", "text", "portTag" })
            Assert.True(root.TryGetProperty(field, out _), $"missing field: {field}");
    }

    [Fact]
    public void LongText_IsTruncated()
    {
        var path = PathFor("trunc.jsonl");
        using (var log = new TrafficLog(path, maxTextChars: 32))
            log.Record("dump_memory", "rsp", new string('x', 10_000));

        using var doc = JsonDocument.Parse(File.ReadAllText(path).TrimEnd());
        var text = doc.RootElement.GetProperty("text").GetString();
        Assert.NotNull(text);
        Assert.StartsWith(new string('x', 32), text);
        Assert.Contains("(+9968 chars)", text);
    }

    [Fact]
    public void OversizeFile_RotatesToBackup()
    {
        var path = PathFor("rot.jsonl");
        using (var log = new TrafficLog(path, maxFileBytes: 600))
        {
            for (var i = 0; i < 20; i++)
                log.Record("t", "cmd", new string('y', 100));
        }

        // 超限内容进 .1,当前文件回到小体积,总量有界
        Assert.True(File.Exists(path + ".1"));
        Assert.True(new FileInfo(path).Length < 600 + 400, $"current file too big: {new FileInfo(path).Length}");
        Assert.True(new FileInfo(path + ".1").Length > 0);
    }

    [Fact]
    public void Record_AfterDispose_IsSilentNoOp()
    {
        var path = PathFor("disposed.jsonl");
        var log = new TrafficLog(path);
        log.Dispose();

        log.Record("t", "cmd", "x"); // 不应抛出

        Assert.False(File.Exists(path)); // 懒打开:从未写过就没有文件
    }

    [Fact]
    public void LazyOpen_NoFileUntilFirstRecord()
    {
        var path = PathFor("lazy.jsonl");
        using (var log = new TrafficLog(path))
        {
            Assert.False(File.Exists(path));
        }
        Assert.False(File.Exists(path));
    }
}
