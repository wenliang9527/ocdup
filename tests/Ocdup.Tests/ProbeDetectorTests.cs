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
    [InlineData("USB 串行设备 (COM3)|USB\\VID_1A86&PID_7523\\...")]
    [InlineData("Generic USB Hub|USB\\VID_0424&PID_2514\\...")]
    // 以下取自真机 Get-PnpDevice 输出：名字里含 dap / adapter / debug 字样，但都不是调试探针
    [InlineData("Microsoft Wi-Fi Direct Virtual Adapter #2|{5D624F94-8850-40C3-A3FA-A4FD2080BAF3}\\VWIFIMP_WFD\\4&352A6737&0&12")]
    [InlineData("Microsoft Kernel Debug Network Adapter|ROOT\\KDNIC\\0000")]
    [InlineData("Microsoft AC Adapter|ACPI\\ACPI0003\\2&DABA3FF&1")]
    [InlineData("USB Composite Device|USB\\VID_0424&PID_2740\\...")]
    public void Unrelated_Devices_Are_Ignored(string line)
    {
        Assert.Empty(ProbeDetector.Parse(new[] { line }));
    }

    [Fact]
    public void Empty_And_Null_Lines_Are_Tolerated()
    {
        Assert.Empty(ProbeDetector.Parse(new string?[] { null, "", "   " }));
    }

    [Fact]
    public void RealWorld_MultiInterface_DapLink_Is_Recognized_As_CmsisDap()
    {
        // 真机样本：AT-Link 的 CMSIS-DAP v2 固件会把一个物理探针枚举成 5 台设备，
        // VID/PID 是 DAPLink 的 0D28:0204（不是 Artery 原厂的 2E3C），所以只会命中 CMSIS-DAP 规则。
        // 注意：这类多接口设备真正能用起来需要显式指定接口脚本
        //（atopenocd 的 interface/atlink_dap_v2.cfg 里就有 cmsis_dap_usb interface 3），
        // 通用 interface/cmsis-dap.cfg 不带这个设置——auto 只负责「认出是什么」，脚本细化由 ocd.json 显式配置。
        var probes = ProbeDetector.Parse(new[]
        {
            "USB Composite Device|USB\\VID_0D28&PID_0204\\00000080066EFF48",
            "USB 串行设备 (COM4)|USB\\VID_0D28&PID_0204&MI_00\\7&1E04DCD9&1&0000",
            "USB Input Device|USB\\VID_0D28&PID_0204&MI_02\\7&1E04DCD9&1&0002",
            "WebUSB: CMSIS-DAP|USB\\VID_0D28&PID_0204&MI_03\\7&1E04DCD9&1&0003",
            "CMSIS-DAP v2|USB\\VID_0D28&PID_0204&MI_04\\7&1E04DCD9&1&0004",
        });

        var probe = Assert.Single(probes);
        Assert.Equal("CMSIS-DAP/DAPLink", probe.Kind);
        Assert.Equal("interface/cmsis-dap.cfg", probe.Config);
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
