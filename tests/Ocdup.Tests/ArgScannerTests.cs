using Ocdup.Cli;

namespace Ocdup.Tests;

/// <summary>
/// ArgScanner（v0.4.25）：watch/rtt/burn/verify 四个 CLI 的选项解析循环收敛于此。
/// 三个 *Options 的既有测试（60+ 断言）是行为主钉；这里只补扫描器自身的边界——
/// 两条吃参数的铁律：无值旗标不吃下一个选项；未知选项也不吃下一个选项
/// （`--foo --format xml` 报 unknown 后 --format 照常解析——测试先红抓回的历史语义）。
/// </summary>
public sealed class ArgScannerTests
{
    private static readonly string[] WatchKeys = ["--name", "--elf", "--interval", "--format", "--halt", "--count"];

    [Fact]
    public void Flag_DoesNotSwallowTheNextOption()
    {
        var s = new ArgScanner("test", ["--halt", "--count", "5"], WatchKeys, ["--halt"]);
        Assert.True(s.Next(out var k1, out var v1));
        Assert.Equal("--halt", k1);
        Assert.Null(v1);
        Assert.True(s.Next(out var k2, out var v2));
        Assert.Equal("--count", k2);
        Assert.Equal("5", v2);
        Assert.False(s.Next(out _, out _));
        Assert.Empty(s.Errors);
    }

    [Fact]
    public void UnknownOption_DoesNotSwallowTheNextOption_Either()
    {
        // 历史语义（测试先红抓回）：--foo 报 unknown 后，--format xml 照常解析
        var s = new ArgScanner("test", ["--foo", "--count", "5"], WatchKeys, ["--halt"]);
        Assert.True(s.Next(out var k1, out var v1));
        Assert.Equal("--foo", k1);
        Assert.Null(v1);
        Assert.Contains(s.Errors, e => e.Contains("unknown option '--foo'"));
        Assert.True(s.Next(out var k2, out var v2));
        Assert.Equal("--count", k2);
        Assert.Equal("5", v2);
    }

    [Fact]
    public void InlineForm_SplitsKeyAndValue()
    {
        var s = new ArgScanner("test", ["--name=gCounter"], WatchKeys, ["--halt"]);
        Assert.True(s.Next(out var key, out var value));
        Assert.Equal("--name", key);
        Assert.Equal("gCounter", value);
    }

    [Fact]
    public void MissingValue_AtEnd_IsRecorded()
    {
        var s = new ArgScanner("test", ["--elf"], WatchKeys, ["--halt"]);
        Assert.True(s.Next(out var key, out var value));
        Assert.Equal("--elf", key);
        Assert.Null(value);
        Assert.Contains(s.Errors, e => e.Contains("'--elf' needs a value"));
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("--help=x")] // 内联形态也认 help（按 key 识别，value 丢弃）
    public void HelpTokens_SetHelpFlag_AndAreSkipped(string token)
    {
        var s = new ArgScanner("test", [token, "--name", "x"], WatchKeys, ["--halt"]);
        Assert.True(s.Next(out var key, out _));
        Assert.Equal("--name", key); // help 不进选项流
        Assert.True(s.Help);
    }
}
