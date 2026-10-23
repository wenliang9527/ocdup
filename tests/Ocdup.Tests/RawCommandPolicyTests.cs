using Ocdup.Config;

namespace Ocdup.Tests;

/// <summary>
/// run_command 内容分类闸（P0-1 定案）：raw TCL 通道面对与专用工具相同的权限约束。
/// 覆盖：各类写命令的分类与重定向文案、';' 走私、注释段、erase_check 不误杀、
/// 与既有权限开关的联动（read_only / allow_flash / allow_flash_erase / allow_memory_write / allow_exec）。
/// </summary>
public sealed class RawCommandPolicyTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ocdup-rawcmd-" + Guid.NewGuid().ToString("n"));

    public RawCommandPolicyTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* 清理失败不影响结论 */ }
    }

    private OcdConfig Load(string permissionsJson)
    {
        var path = Path.Combine(_dir, "ocd.json");
        File.WriteAllText(path, $$"""{ "permissions": {{permissionsJson}} }""");
        return OcdConfig.LoadFrom(path);
    }

    // ── Segment：';' 是 TCL 单行内的合法语句分隔符，必须切段后逐段分类 ──

    [Fact]
    public void Segment_SplitsOnSemicolon_TrimsAndDropsEmpty()
    {
        var segments = RawCommandPolicy.Segment("mdb 0x0 1;  shutdown ;");

        Assert.Equal(new[] { "mdb 0x0 1", "shutdown" }, segments);
    }

    [Fact]
    public void Segment_WithoutSemicolon_IsSingleSegment()
    {
        Assert.Equal(new[] { "version" }, RawCommandPolicy.Segment("version"));
    }

    // ── 只读/未知命令：现状放行（逃生舱本义）──

    [Theory]
    [InlineData("mdb 0x40010800 4")]
    [InlineData("mdw 0x08000000 2; version")]
    [InlineData("flash banks")]
    [InlineData("flash probe 0")]
    [InlineData("flash info 0")]
    [InlineData("targets")]
    [InlineData("reg")]
    public void ReadAndUnknownCommands_PassThrough(string command)
    {
        Assert.Null(RawCommandPolicy.Evaluate(Load("{}"), command));
    }

    [Fact]
    public void FlashEraseCheck_IsReadOnly_NotBlockedByEraseGate()
    {
        // 陷阱：erase_check 以 "flash erase" 开头但是只读操作，不能被擦除闸误杀。
        Assert.Null(RawCommandPolicy.Evaluate(Load("{}"), "flash erase_check 0"));
    }

    [Fact]
    public void CommentSegment_IsIgnored()
    {
        // TCL 注释段：'#' 开头的段不参与分类。
        Assert.Null(RawCommandPolicy.Evaluate(Load("{}"), "# mdb 0x0 1"));
    }

    // ── 带文件的 flash 写：raw 通道无法执行路径白名单/大小上限，一律重定向专用工具 ──

    [Theory]
    [InlineData("program D:/anywhere/evil.bin 0x08000000")]
    [InlineData("PROGRAM build/fw.bin verify")]
    [InlineData("flash write_image 0 build/fw.bin")]
    [InlineData("load_image build/fw.bin 0x20000000")]
    public void FileCarryingFlashWrites_AreBlocked_AndRedirectToFlashWrite(string command)
    {
        var blocked = RawCommandPolicy.Evaluate(Load("{}"), command);

        Assert.NotNull(blocked);
        Assert.StartsWith("BLOCKED:", blocked);
        Assert.Contains("mcp__ocdup__flash_write", blocked);
    }

    // ── flash 擦除：与专用工具同一条闸（allow_flash_erase）──

    [Fact]
    public void FlashEraseSector_FollowsAllowFlashEraseGate()
    {
        var blocked = RawCommandPolicy.Evaluate(Load("{}"), "flash erase_sector 0 0 last");

        Assert.NotNull(blocked);
        Assert.Contains("allow_flash_erase", blocked);

        Assert.Null(RawCommandPolicy.Evaluate(
            Load("""{ "allow_flash_erase": true }"""), "flash erase_sector 0 0 last"));
    }

    [Fact]
    public void FlashEraseAddress_FollowsAllowFlashEraseGate()
    {
        var blocked = RawCommandPolicy.Evaluate(Load("{}"), "flash erase_address 0x08000000 0x1000");

        Assert.NotNull(blocked);
        Assert.Contains("allow_flash_erase", blocked);
    }

    // ── 无文件的 flash 写/保护：走 allow_flash 闸（CheckFlashOp）──

    [Fact]
    public void FlashFill_FollowsAllowFlashGate()
    {
        Assert.Null(RawCommandPolicy.Evaluate(Load("{}"), "flash fillw 0x08000000 16 0xFF"));

        var blocked = RawCommandPolicy.Evaluate(
            Load("""{ "allow_flash": false }"""), "flash fillw 0x08000000 16 0xFF");

        Assert.NotNull(blocked);
        Assert.Contains("allow_flash", blocked);
    }

    [Fact]
    public void FlashProtect_UnderReadOnly_IsBlockedByTotalGate()
    {
        var blocked = RawCommandPolicy.Evaluate(Load("""{ "read_only": true }"""), "flash protect 0 clear");

        Assert.NotNull(blocked);
        Assert.Contains("read_only", blocked);
    }

    // ── 内存写：与 write_memory 同一条闸（allow_memory_write）──

    [Fact]
    public void RawMemoryWrites_FollowAllowMemoryWriteGate()
    {
        Assert.Null(RawCommandPolicy.Evaluate(Load("{}"), "mww 0x40010800 0xffffffff"));

        var blocked = RawCommandPolicy.Evaluate(
            Load("""{ "allow_memory_write": false }"""), "mwb 0x40010800 0xFF");

        Assert.NotNull(blocked);
        Assert.Contains("allow_memory_write", blocked);
    }

    // ── 执行控制：与 halt/resume/reset 专用工具同一条闸（allow_exec）──

    [Fact]
    public void RawExecControl_FollowsAllowExecGate()
    {
        Assert.Null(RawCommandPolicy.Evaluate(Load("{}"), "reset run"));

        var blocked = RawCommandPolicy.Evaluate(Load("""{ "allow_exec": false }"""), "halt");

        Assert.NotNull(blocked);
        Assert.Contains("allow_exec", blocked);
    }

    // ── v0.4.30：断点/观察点同归执行控制（此前是 Passthrough 漏洞）──

    [Theory]
    [InlineData("bp 0x08000100")]
    [InlineData("bp 0x08000100 4 hw")]
    [InlineData("rbp 0x08000100")]
    [InlineData("rbp all")]
    [InlineData("wp 0x20000000 4 w")]
    [InlineData("rwp 0x20000000")]
    [InlineData("rwp all")]
    public void BreakpointAndWatchpointCommands_FollowAllowExecGate(string command)
    {
        // 放行时与 reset/halt 同态：allow_exec 开了就过（raw 通道本身另有过 allow_raw_command 闸）
        Assert.Null(RawCommandPolicy.Evaluate(Load("""{ "allow_raw_command": true }"""), command));

        var blocked = RawCommandPolicy.Evaluate(Load("""{ "allow_exec": false }"""), command);

        Assert.NotNull(blocked);
        Assert.Contains("allow_exec", blocked);
    }

    [Fact]
    public void SmuggledBreakpoint_AfterHarmlessCommand_IsBlocked()
    {
        var blocked = RawCommandPolicy.Evaluate(Load("""{ "allow_exec": false }"""), "mdb 0x0 1; bp 0x08000100");

        Assert.NotNull(blocked);
        Assert.Contains("allow_exec", blocked);
    }

    // ── PC 侧落盘：dump_image 重定向 dump_memory（保留白名单/上限的意义）──

    [Fact]
    public void DumpImage_IsBlocked_AndRedirectsToDumpMemory()
    {
        var blocked = RawCommandPolicy.Evaluate(
            Load("{}"), "dump_image D:/anywhere/evil.bin 0x08000000 0x1000");

        Assert.NotNull(blocked);
        Assert.StartsWith("BLOCKED:", blocked);
        Assert.Contains("mcp__ocdup__dump_memory", blocked);
    }

    // ── 后端控制：shutdown/exit 重定向 disconnect（read_only 闸与三态结论在那里）──

    [Fact]
    public void ShutdownAndExit_AreBlocked_AndRedirectToDisconnect()
    {
        foreach (var command in new[] { "shutdown", "exit" })
        {
            var blocked = RawCommandPolicy.Evaluate(Load("{}"), command);

            Assert.NotNull(blocked);
            Assert.StartsWith("BLOCKED:", blocked);
            Assert.Contains("mcp__ocdup__disconnect", blocked);
        }
    }

    // ── ';' 走私：每段独立分类，任一段命中即拦 ──

    [Fact]
    public void SmuggledStatement_AfterHarmlessCommand_IsBlocked()
    {
        var blocked = RawCommandPolicy.Evaluate(Load("{}"), "mdb 0x0 1; shutdown");

        Assert.NotNull(blocked);
        Assert.Contains("mcp__ocdup__disconnect", blocked);
    }

    [Fact]
    public void SmuggledProgram_AfterHarmlessCommand_IsBlocked()
    {
        var blocked = RawCommandPolicy.Evaluate(Load("""{ "allow_flash": true }"""), "version ; program evil.bin");

        Assert.NotNull(blocked);
        Assert.Contains("mcp__ocdup__flash_write", blocked);
    }

    [Fact]
    public void FirstBlockingSegment_Wins()
    {
        var blocked = RawCommandPolicy.Evaluate(Load("{}"), "shutdown; program evil.bin");

        Assert.Contains("mcp__ocdup__disconnect", blocked);
    }

    [Fact]
    public void WhitespaceOnlySegments_AreIgnored()
    {
        Assert.Null(RawCommandPolicy.Evaluate(Load("{}"), " ; ; version"));
    }
}
