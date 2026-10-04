using System.Text;

namespace Ocdup.Backend;

/// <summary>ELF 符号表里的一个对象符号（变量）。</summary>
public sealed record ElfSymbol(string Name, long Address, long Size);

/// <summary>ELF 解析失败（坏 magic / 非 32 位 / 无 symtab 等），Message 是可直接给模型看的文案。</summary>
public sealed class ElfParseException : Exception
{
    public ElfParseException(string message) : base(message) { }
}

/// <summary>
/// 符号层 L1（设计文档第 7 节）：解析 ELF32 小端的 .symtab，给出对象符号
/// （STT_OBJECT 且地址非 0、大小 &gt; 0——即变量，不含函数）的名字 → (地址, 字节数)。
/// 纯函数、无 IO。大端/ELF64 明确拒绝（Cortex-M 工具链产物实际都是 ELF32 LE，
/// 拒绝文案告诉模型该换镜像而不是猜）。Keil(armcc/armclang) 与 GCC 产物均适用。
/// </summary>
public static class ElfSymbols
{
    private const int ShtSymtab = 2;
    private const int SttObject = 1;

    /// <summary>解析 .symtab 里的对象符号，按地址升序。异常信息面向模型（带下一步建议）。</summary>
    public static List<ElfSymbol> ParseObjects(byte[] elf)
    {
        if (elf.Length < 52 || elf[0] != 0x7F || elf[1] != (byte)'E' || elf[2] != (byte)'L' || elf[3] != (byte)'F')
            throw new ElfParseException("not an ELF file (bad magic). Pass the path of the built .elf image.");
        if (elf[4] != 1)
            throw new ElfParseException($"only 32-bit ELF is supported (file declares EI_CLASS={elf[4]}). " +
                                        "Cortex-M toolchains emit 32-bit ELF - is this the right image?");
        if (elf[5] != 1)
            throw new ElfParseException($"only little-endian ELF is supported (file declares EI_DATA={elf[5]}).");

        var shoff = (int)ReadU32(elf, 0x20);
        var shentsize = ReadU16(elf, 0x2E);
        var shnum = ReadU16(elf, 0x30);
        if (shoff == 0 || shnum == 0)
            throw new ElfParseException("no section headers (file stripped?). Rebuild without stripping, or pass the unstripped .elf.");
        if (shentsize < 40)
            throw new ElfParseException($"unexpected section header entry size {shentsize}");
        if ((long)shoff + (long)shentsize * shnum > elf.Length)
            throw new ElfParseException("section header table out of bounds (truncated file?)");

        var symtabIndex = -1;
        for (var i = 0; i < shnum; i++)
        {
            if (ReadU32(elf, shoff + i * shentsize + 4) == ShtSymtab) { symtabIndex = i; break; }
        }
        if (symtabIndex < 0)
            throw new ElfParseException("no .symtab section (built with -s / stripped?). " +
                                        "Rebuild without stripping, or pass the unstripped .elf.");

        var symSec = shoff + symtabIndex * shentsize;
        var dataOff = (int)ReadU32(elf, symSec + 16);
        var dataSize = (int)ReadU32(elf, symSec + 20);
        var link = (int)ReadU32(elf, symSec + 24);          // 关联的 .strtab section index
        var entsize = (int)ReadU32(elf, symSec + 36);
        if (entsize < 16)
            throw new ElfParseException($"unexpected symbol entry size {entsize}");
        if (link < 0 || link >= shnum)
            throw new ElfParseException("symtab sh_link out of range (malformed file?)");
        var strOff = (int)ReadU32(elf, shoff + link * shentsize + 16);
        var strSize = (int)ReadU32(elf, shoff + link * shentsize + 20);
        if ((long)dataOff + dataSize > elf.Length || (long)strOff + strSize > elf.Length)
            throw new ElfParseException("symtab/strtab data out of bounds (truncated file?)");

        var result = new List<ElfSymbol>();
        for (var off = dataOff; off + 16 <= dataOff + dataSize; off += entsize)
        {
            var nameOff = ReadU32(elf, off);
            var value = ReadU32(elf, off + 4);
            var size = ReadU32(elf, off + 8);
            var info = elf[off + 12];
            if ((info & 0xF) != SttObject) continue;        // 只要变量，不要函数
            if (value == 0 || size == 0) continue;          // 未定位/零大小没有读的意义
            var name = ReadCString(elf, (long)strOff + nameOff, strOff + strSize);
            if (string.IsNullOrEmpty(name)) continue;
            result.Add(new ElfSymbol(name, value, size));
        }
        result.Sort((a, b) => a.Address.CompareTo(b.Address));
        return result;
    }

    private static uint ReadU32(byte[] b, int off) =>
        b[off] | (uint)b[off + 1] << 8 | (uint)b[off + 2] << 16 | (uint)b[off + 3] << 24;

    private static int ReadU16(byte[] b, int off) => b[off] | b[off + 1] << 8;

    private static string ReadCString(byte[] b, long start, int limit)
    {
        if (start < 0 || start >= limit) return "";
        var end = start;
        while (end < limit && b[end] != 0) end++;
        return Encoding.ASCII.GetString(b, (int)start, (int)(end - start));
    }
}
