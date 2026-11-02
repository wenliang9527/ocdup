using Ocdup.Tools;

namespace Ocdup.Tests;

/// <summary>
/// MemoryTools 的纯函数面（v0.4.39，现场实战 session cad81f 驱动）：
/// 读失败识别（宽读取降级的触发条件）、逐字节小端组装（降级后的 json 重组）、
/// 写后回读比对（verify 参数的判定核心）。编排行为由 e2e 钉死。
/// </summary>
public sealed class MemoryToolsTests
{
    // ── LooksLikeReadFailure：真机形态是 "Failed to read memory at 0x..."（session cad81f 的 0x40020808）──

    [Theory]
    [InlineData("Failed to read memory at 0x4002080a")]
    [InlineData("failed to read memory")]
    [InlineData("fake wrapper\nFAILED TO READ MEMORY AT 0X1")]
    public void LooksLikeReadFailure_RecognisesTheBackendSignature(string output)
        => Assert.True(MemoryTools.LooksLikeReadFailure(output));

    [Theory]
    [InlineData("")]
    [InlineData("0x20000000: 20000000 ")]
    [InlineData("0x20000000: deadbeef\n0x20000004: 00000000")]
    [InlineData("Error: flash bank missing")] // 其它错误不算读失败，不触发降级
    public void LooksLikeReadFailure_LeavesNormalOutputAlone(string output)
        => Assert.False(MemoryTools.LooksLikeReadFailure(output));

    // ── AssembleLittleEndian：降级读回的逐字节值 → 宽值（Cortex-M 小端）──

    [Fact]
    public void AssembleLittleEndian_CombinesBytesLittleEndian()
    {
        // 字节序列 78 56 34 12 → u32 0x12345678
        var v = MemoryTools.AssembleLittleEndian([0x78, 0x56, 0x34, 0x12], 1, 32);
        Assert.NotNull(v);
        Assert.Equal(0x12345678, v[0]);
    }

    [Fact]
    public void AssembleLittleEndian_TwoWordsAndHalfwords()
    {
        var words = MemoryTools.AssembleLittleEndian(
            [0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88], 2, 32);
        Assert.NotNull(words);
        Assert.Equal(0x44332211, words[0]);
        Assert.Equal(0x88776655, words[1]);

        var halves = MemoryTools.AssembleLittleEndian([0xcd, 0xab, 0xef, 0x01], 2, 16);
        Assert.NotNull(halves);
        Assert.Equal(0xabcd, halves[0]);
        Assert.Equal(0x01ef, halves[1]);
    }

    [Fact]
    public void AssembleLittleEndian_InsufficientBytes_ReturnsNull()
    {
        Assert.Null(MemoryTools.AssembleLittleEndian([0x78, 0x56, 0x34], 1, 32));
        Assert.Null(MemoryTools.AssembleLittleEndian([], 2, 16));
    }

    // ── FirstReadBackMismatch：write_memory verify 的判定核心 ──

    [Fact]
    public void FirstReadBackMismatch_AllMatch_ReturnsNull()
        => Assert.Null(MemoryTools.FirstReadBackMismatch([0x1E, 0x17], [0x1E, 0x17], 0x200015B8, 8));

    [Fact]
    public void FirstReadBackMismatch_ReportsAddressWidthAndCounts()
    {
        var msg = MemoryTools.FirstReadBackMismatch([0x11111111, 0x22222222], [0x11111111, 0x99999999], 0x20004000, 32);
        Assert.NotNull(msg);
        Assert.Contains("0x20004004", msg);          // 第二个单元的地址（start + 1*4）
        Assert.Contains("wrote 0x22222222", msg);
        Assert.Contains("read back 0x99999999", msg);
        Assert.Contains("1 of 2 location(s) differ", msg);
    }

    [Fact]
    public void FirstReadBackMismatch_CountsAllDifferences_ReportsTheFirst()
    {
        var msg = MemoryTools.FirstReadBackMismatch([0xAA, 0xBB, 0xCC], [0x00, 0x00, 0xCC], 0x20000060, 8);
        Assert.NotNull(msg);
        Assert.Contains("2 of 3", msg); // 第三个值相等 → 2 处不一致，first=0 → 地址 0x20000060
        Assert.Contains("0x20000060", msg);
        Assert.Contains("wrote 0xaa", msg);
        Assert.Contains("read back 0x00", msg);
    }
}
