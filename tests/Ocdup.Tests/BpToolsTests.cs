using Ocdup.Tools;

namespace Ocdup.Tests;

/// <summary>
/// 断点/观察点（M-bp，v0.4.30）的纯函数面：OpenOCD 命令构造（bp/rbp/wp/rwp 的
/// 可选参数形态）与 access 归一化。命令形态按 2026-10-19 真机核定（AT32F403AVGT7
/// + atopenocd-py32）：bp/wp 的 length 必填、恒显式下发；rwp 只认地址（无 all）。
/// 闸门行为（read_only/allow_exec）与真实后端交互由 e2e 链路钉死——工具本体要连
/// TCL-RPC，单测层不覆盖。
/// </summary>
public sealed class BpToolsTests
{
    // ── 断点：bp <addr> <length> [hw]（真机：length 必填，恒显式）──

    [Fact]
    public void BreakpointCommand_MinimalForm_CarriesDefaultLength()
    {
        Assert.Equal("bp 0x08000100 2", BpTools.BreakpointCommand(0x08000100, length: 2, hw: false));
    }

    [Fact]
    public void BreakpointCommand_CustomLength()
    {
        Assert.Equal("bp 0x08000100 4", BpTools.BreakpointCommand(0x08000100, length: 4, hw: false));
    }

    [Fact]
    public void BreakpointCommand_HwAfterLength()
    {
        Assert.Equal("bp 0x08000100 2 hw", BpTools.BreakpointCommand(0x08000100, length: 2, hw: true));
        Assert.Equal("bp 0x08000100 4 hw", BpTools.BreakpointCommand(0x08000100, length: 4, hw: true));
    }

    [Fact]
    public void DefaultBreakpointLength_IsThumbWidth()
    {
        // 默认 2 = ARM Thumb 指令宽度；工具层恒定显式下发（fork 的 bp 省 length 只回 usage）
        Assert.Equal(2, BpTools.DefaultBreakpointLength);
        Assert.Equal(4, BpTools.DefaultWatchpointLength);
    }

    [Fact]
    public void RemoveBreakpointCommand_AddressOrAll()
    {
        Assert.Equal("rbp 0x08000100", BpTools.RemoveBreakpointCommand("0x08000100"));
        Assert.Equal("rbp all", BpTools.RemoveBreakpointCommand(null));
        Assert.Equal("rbp all", BpTools.RemoveBreakpointCommand("   "));
    }

    // ── 观察点：wp <addr> <length> <access> [value]（真机：length 必填，恒显式）──

    [Fact]
    public void WatchpointCommand_LengthAndAccessAreAlwaysExplicit()
    {
        // access 恒显式给出——不猜后端默认值（不同 openocd 分支默认可能不同）
        Assert.Equal("wp 0x20000000 4 w", BpTools.WatchpointCommand(0x20000000, length: 4, access: "w", value: null));
    }

    [Fact]
    public void WatchpointCommand_FullForm()
    {
        Assert.Equal("wp 0x20000000 4 a 0x0000002a", BpTools.WatchpointCommand(0x20000000, length: 4, access: "a", value: 0x2A));
    }

    [Fact]
    public void WatchpointCommand_ReadAccess_AndShortLength()
    {
        Assert.Equal("wp 0x20000000 1 r", BpTools.WatchpointCommand(0x20000000, length: 1, access: "r", value: null));
    }

    [Fact]
    public void RemoveWatchpointCommand_AddressOnly_NoAllForm()
    {
        // 真机：rwp 只认 address（'rwp all' → Invalid command argument），全清走列表逐个删
        Assert.Equal("rwp 0x20000000", BpTools.RemoveWatchpointCommand("0x20000000"));
    }

    // ── wp 列表地址解析（全清观察点的数据源；真机形态）──

    [Fact]
    public void ParseWatchpointAddresses_RealMachineForm()
    {
        var listing = "address: 0x20000000, len: 0x00000004, r/w/a: 1, value: 0x00000000, mask: 0xffffffff\n" +
                      "address: 0x20000004, len: 0x00000004, r/w/a: 1, value: 0x00000000, mask: 0xffffffff";

        Assert.Equal(["0x20000000", "0x20000004"], BpTools.ParseWatchpointAddresses(listing));
    }

    [Fact]
    public void ParseWatchpointAddresses_EmptyOrUnparseable_ReturnsEmpty()
    {
        Assert.Empty(BpTools.ParseWatchpointAddresses(""));
        Assert.Empty(BpTools.ParseWatchpointAddresses(null));
        Assert.Empty(BpTools.ParseWatchpointAddresses("(no output)"));
        Assert.Empty(BpTools.ParseWatchpointAddresses("no watchpoints armed"));
    }

    [Fact]
    public void ParseWatchpointAddresses_NormalizesToLowerHex()
    {
        Assert.Equal(["0x2000abcd"], BpTools.ParseWatchpointAddresses("address: 0x2000ABCD, len: 0x4, r/w/a: 2"));
    }

    // ── access 归一化 ──

    [Theory]
    [InlineData("r", "r")]
    [InlineData("w", "w")]
    [InlineData("a", "a")]
    [InlineData(" R ", "r")]
    [InlineData("W", "w")]
    public void NormalizeAccess_AcceptsRwaCaseInsensitive(string input, string expected)
    {
        Assert.Equal(expected, BpTools.NormalizeAccess(input));
    }

    [Theory]
    [InlineData("")]
    [InlineData("x")]
    [InlineData("read")]
    [InlineData("write")]
    [InlineData(null)]
    public void NormalizeAccess_RejectsAnythingElse(string? input)
    {
        Assert.Null(BpTools.NormalizeAccess(input));
    }
}
