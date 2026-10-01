using Ocdup.Launcher;

namespace Ocdup.Tests;

/// <summary>backend_log 的数据源:OpenOcdLauncher 输出缓冲的尾部截取(纯函数)。</summary>
public sealed class LauncherOutputTests
{
    [Fact]
    public void TailLines_ReturnsLastNJoinedByNewline()
    {
        var lines = new[] { "Info : a", "Info : b", "Error : c", "Info : d" };

        Assert.Equal("Info : d", OpenOcdLauncher.TailLines(lines, 1));
        Assert.Equal("Error : c\nInfo : d", OpenOcdLauncher.TailLines(lines, 2));
        Assert.Equal("Info : a\nInfo : b\nError : c\nInfo : d", OpenOcdLauncher.TailLines(lines, 10));
    }

    [Fact]
    public void TailLines_HandlesEmptyAndDegenerateInputs()
    {
        Assert.Equal("", OpenOcdLauncher.TailLines(Array.Empty<string>(), 5));
        Assert.Equal("x", OpenOcdLauncher.TailLines(new[] { "x" }, 0)); // 非法上限钳到 1
    }
}
