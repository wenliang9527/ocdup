using System.IO;
using Ocdup.Config;

namespace Ocdup.Tests;

/// <summary>权限闸矩阵（设计文档 4.4 / 13 节：逐格断言 + 拦截文案可操作）。</summary>
public sealed class PermissionGateTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ocdup-perm-" + Guid.NewGuid().ToString("n"));

    public PermissionGateTests() => Directory.CreateDirectory(_dir);

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

    private string ImagePath(string relative = "build/fw.bin")
    {
        var full = Path.Combine(_dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, "x");
        return full;
    }

    [Fact]
    public void Defaults_AllowFlashOnLocalPath()
    {
        var cfg = Load("{}");
        var image = Path.Combine(_dir, "build", "fw.bin");

        Assert.Null(PermissionGate.CheckFlashWrite(cfg, image, 1024));
    }

    [Fact]
    public void ReadOnly_BlocksFlashWrite_AndTellsHowToUnblock()
    {
        var cfg = Load("""{ "read_only": true }""");

        var blocked = PermissionGate.CheckFlashWrite(cfg, Path.Combine(_dir, "fw.bin"), 10);

        Assert.NotNull(blocked);
        Assert.StartsWith("BLOCKED:", blocked);
        Assert.Contains("read_only", blocked);
        Assert.Contains("show_config", blocked);
    }

    [Fact]
    public void AllowFlashFalse_BlocksAndNamesTheSetting()
    {
        var cfg = Load("""{ "allow_flash": false }""");

        var blocked = PermissionGate.CheckFlashWrite(cfg, Path.Combine(_dir, "fw.bin"), 10);

        Assert.NotNull(blocked);
        Assert.Contains("allow_flash", blocked);
        Assert.Contains("permissions", blocked);
    }

    [Fact]
    public void OversizeImage_IsBlocked()
    {
        var cfg = Load("""{ "flash_max_bytes": 100 }""");

        var blocked = PermissionGate.CheckFlashWrite(cfg, Path.Combine(_dir, "fw.bin"), 101);

        Assert.NotNull(blocked);
        Assert.Contains("flash_max_bytes", blocked);
        Assert.Null(PermissionGate.CheckFlashWrite(cfg, Path.Combine(_dir, "fw.bin"), 100));
    }

    [Fact]
    public void PathWhitelist_BlocksOutsideAndAllowsInside()
    {
        var inside = ImagePath("build/fw.bin");
        var outside = ImagePath("elsewhere/fw.bin");
        var cfg = Load("""{ "flash_allowed_paths": ["build/"] }""");

        Assert.Null(PermissionGate.CheckFlashWrite(cfg, inside, 10));
        var blocked = PermissionGate.CheckFlashWrite(cfg, outside, 10);
        Assert.NotNull(blocked);
        Assert.Contains("flash_allowed_paths", blocked);
    }

    [Fact]
    public void ReadsAreNotBlockedByReadOnly()
    {
        // read_only 是「写」的总闸：读操作不受影响，只受体积上限约束。
        var cfg = Load("""{ "read_only": true, "memory_read_max_bytes": 16 }""");

        Assert.Null(PermissionGate.CheckReadLimit(cfg, 16, "read_memory"));
        var blocked = PermissionGate.CheckReadLimit(cfg, 17, "read_memory");
        Assert.NotNull(blocked);
        Assert.Contains("memory_read_max_bytes", blocked);
    }

    [Fact]
    public void FlashErase_IsOffByDefault()
    {
        var cfg = Load("{}");

        var blocked = PermissionGate.CheckFlashErase(cfg);

        Assert.NotNull(blocked);
        Assert.Contains("allow_flash_erase", blocked);
        Assert.Null(PermissionGate.CheckFlashErase(Load("""{ "allow_flash_erase": true }""")));
    }

    [Fact]
    public void MemoryWrite_UsesItsOwnSwitch()
    {
        Assert.Null(PermissionGate.CheckMemoryWrite(Load("{}")));
        var blocked = PermissionGate.CheckMemoryWrite(Load("""{ "allow_memory_write": false }"""));
        Assert.NotNull(blocked);
        Assert.Contains("allow_memory_write", blocked);
    }
}
