using Ocdup.Launcher;

namespace Ocdup.Tests;

/// <summary>
/// 探针自动识别的纯解析逻辑:优先级(AT-Link > CMSIS-DAP > ST-Link > J-Link)、
/// 同种去重、无匹配返回空。不触碰 PowerShell,不需要硬件。
/// </summary>
public sealed class ProbeDetectorTests
{
    private static readonly string[] AtLink =
    {
        "AT-Link CMSIS-DAP|USB\\VID_2E3C&PID_0110\\5&2f3a",
    };
    private static readonly string[] JLink =
    {
        "JLink CDC UART Port (COM9)|USB\\VID_1366&PID_0101\\6&1b2c",
        "J-Link driver|USB\\VID_1366&PID_0101\\6&1b2c",
    };

    [Fact]
    public void AtLink_IsRecognized_WithHighestPriorityConfig()
    {
        var probes = ProbeDetector.Parse(AtLink);
        var probe = Assert.Single(probes);
        Assert.Equal("AT-Link", probe.Kind);
        Assert.Equal("interface/atlink_dap_v2.cfg", probe.Config);
    }

    [Fact]
    public void JLink_IsRecognized_AndDeduplicated()
    {
        var probes = ProbeDetector.Parse(JLink);
        var probe = Assert.Single(probes);
        Assert.Equal("J-Link", probe.Kind);
        Assert.Equal("interface/jlink.cfg", probe.Config);
    }

    [Fact]
    public void AtLink_Wins_Over_JLink_WhenBothPresent()
    {
        var probes = ProbeDetector.Parse(JLink.Concat(AtLink));
        Assert.Equal(2, probes.Count);
        Assert.Equal("AT-Link", probes[0].Kind);
        Assert.Equal("J-Link", probes[1].Kind);
    }

    [Theory]
    [InlineData("USB 串行设备 (COM3)|USB\\VID_1A86&PID_7523\\...", "")]
    [InlineData("Generic USB Hub|USB\\VID_0424&PID_2514\\...", "")]
    public void Unrelated_Devices_Are_Ignored(string line, string _)
    {
        Assert.Empty(ProbeDetector.Parse(new[] { line }));
    }

    [Fact]
    public void Empty_And_Null_Lines_Are_Tolerated()
    {
        Assert.Empty(ProbeDetector.Parse(new string?[] { null, "", "   " }));
    }

    [Fact]
    public void Vid_Matches_Even_When_FriendlyName_Is_Generic()
    {
        // 显示名随驱动/系统变化;VID/PID 是更稳的识别依据
        var probes = ProbeDetector.Parse(new[]
        {
            "USB \u4e32\u53e3\u8bbe\u5907|USB\\VID_2E3C&PID_0110\\5&2f3a",   // AT-Link by VID
            "USB Composite Device|USB\\VID_1366&PID_0101\\6&1b2c",              // J-Link by VID
            "USB Composite Device|USB\\VID_0483&PID_374B\\7&1d9e",              // ST-Link by VID+PID
        });
        Assert.Equal(3, probes.Count);
        Assert.Equal("AT-Link", probes[0].Kind);      // 优先级 0
        Assert.Equal("ST-Link", probes[1].Kind);      // 优先级 2
        Assert.Equal("J-Link", probes[2].Kind);       // 优先级 3
        Assert.DoesNotContain(probes, p => p.Kind == "CMSIS-DAP/DAPLink");
    }

    [Fact]
    public void St_Vid_Without_Probe_Pid_Is_Not_STLink()
    {
        // ST 的 VID 也用在非 ST-Link 设备上(如 VCP 串口):PID 不对就不能认成探针
        var probes = ProbeDetector.Parse(new[] { "USB \u4e32\u53e3\u8bbe\u5907 (COM3)|USB\\VID_0483&PID_5740\\..." });
        Assert.Empty(probes);
    }
}
