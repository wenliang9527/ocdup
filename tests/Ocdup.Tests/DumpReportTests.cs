using Ocdup.Tools;

namespace Ocdup.Tests;

/// <summary>dump_memory 报告格式:路径/字节数/大小不符提示/sha 前缀/hex 字预览。</summary>
public sealed class DumpReportTests
{
    [Fact]
    public void Report_Contains_Path_Size_Hash_And_Hex_Words()
    {
        var preview = new byte[] { 0x28, 0x9D, 0x01, 0x20, 0xAD, 0x02, 0x00, 0x08 };
        var report = MemoryTools.FormatDumpReport(0x08000000, 64, "D:\\tmp\\fw.bin", 64, "ABCD1234EFGH5678", preview);

        Assert.Contains("D:\\tmp\\fw.bin", report);
        Assert.Contains("64 bytes", report);
        Assert.Contains("ABCD1234EFGH5678", report);
        Assert.Contains("0x08000000: 289D0120", report);
        Assert.Contains("0x08000004: AD020008", report);
        Assert.DoesNotContain("NOTE", report);
    }

    [Fact]
    public void Report_Notes_Size_Mismatch()
    {
        var report = MemoryTools.FormatDumpReport(0x08000000, 1024, "C:\\x.bin", 512, "ABC", new byte[] { 1, 2, 3, 4 });
        Assert.Contains("requested 1024 bytes", report);
        Assert.Contains("holds 512", report);
    }

    [Fact]
    public void Report_Handles_Empty_File()
    {
        var report = MemoryTools.FormatDumpReport(0x20000000, 128, "C:\\e.bin", 0, "DEAD", Array.Empty<byte>());
        Assert.Contains("(file is empty)", report);
    }
}
