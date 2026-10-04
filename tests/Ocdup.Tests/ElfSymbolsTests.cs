using Ocdup.Backend;

namespace Ocdup.Tests;

/// <summary>符号层 L1：ELF32 .symtab 解析。测试内嵌一个最小合法 ELF32 构造器，不依赖真实编译产物。</summary>
public sealed class ElfSymbolsTests
{
    /// <summary>构造最小 ELF32 LE：ELF 头 + .symtab + .strtab + 3 个 section header。</summary>
    private static byte[] BuildElf(params (string Name, uint Addr, uint Size, byte Type)[] symbols)
    {
        var strtab = new byte[] { 0 };
        var nameOffsets = new List<int>();
        foreach (var s in symbols)
        {
            nameOffsets.Add(strtab.Length);
            strtab = strtab.Concat(System.Text.Encoding.ASCII.GetBytes(s.Name)).Append((byte)0).ToArray();
        }

        var symtab = new List<byte>();
        symtab.AddRange(new byte[16]); // null symbol
        for (var i = 0; i < symbols.Length; i++)
        {
            symtab.AddRange(BitConverter.GetBytes((uint)nameOffsets[i]));
            symtab.AddRange(BitConverter.GetBytes(symbols[i].Addr));
            symtab.AddRange(BitConverter.GetBytes(symbols[i].Size));
            symtab.Add((byte)(symbols[i].Type)); // st_info: type, binding=LOCAL(0)
            symtab.Add(0);                       // st_other
            symtab.AddRange(BitConverter.GetBytes((ushort)1)); // st_shndx
        }

        const int ehsize = 52, shentsize = 40;
        var symOff = ehsize;
        var strOff = symOff + symtab.Count;
        var shoff = strOff + strtab.Length;

        byte[] Shdr(uint type, uint off, uint size, uint link = 0, uint entsize = 0)
        {
            var b = new List<byte>();
            b.AddRange(BitConverter.GetBytes(0u));   // sh_name
            b.AddRange(BitConverter.GetBytes(type)); // sh_type
            b.AddRange(BitConverter.GetBytes(0u));   // sh_flags
            b.AddRange(BitConverter.GetBytes(0u));   // sh_addr
            b.AddRange(BitConverter.GetBytes(off));  // sh_offset
            b.AddRange(BitConverter.GetBytes(size)); // sh_size
            b.AddRange(BitConverter.GetBytes(link)); // sh_link
            b.AddRange(BitConverter.GetBytes(0u));   // sh_info
            b.AddRange(BitConverter.GetBytes(4u));   // sh_addralign
            b.AddRange(BitConverter.GetBytes(entsize));
            return b.ToArray();
        }

        var elf = new List<byte>();
        elf.AddRange(new byte[] { 0x7F, (byte)'E', (byte)'L', (byte)'F', 1, 1, 1, 0 });
        elf.AddRange(new byte[8]); // e_ident 填充
        elf.AddRange(BitConverter.GetBytes((ushort)2));  // e_type EXEC
        elf.AddRange(BitConverter.GetBytes((ushort)40)); // e_machine ARM
        elf.AddRange(BitConverter.GetBytes(1u));         // e_version
        elf.AddRange(BitConverter.GetBytes(0u));         // e_entry
        elf.AddRange(BitConverter.GetBytes(0u));         // e_phoff
        elf.AddRange(BitConverter.GetBytes(shoff));      // e_shoff
        elf.AddRange(BitConverter.GetBytes(0u));         // e_flags
        elf.AddRange(BitConverter.GetBytes((ushort)ehsize));
        elf.AddRange(BitConverter.GetBytes((ushort)0));  // e_phentsize
        elf.AddRange(BitConverter.GetBytes((ushort)0));  // e_phnum
        elf.AddRange(BitConverter.GetBytes((ushort)shentsize));
        elf.AddRange(BitConverter.GetBytes((ushort)4));  // e_shnum
        elf.AddRange(BitConverter.GetBytes((ushort)0));  // e_shstrndx
        elf.AddRange(symtab);
        elf.AddRange(strtab);
        elf.AddRange(Shdr(0, 0, 0));                                  // NULL
        elf.AddRange(Shdr(2, (uint)symOff, (uint)symtab.Count, 2, 16)); // SHT_SYMTAB, link=strtab
        elf.AddRange(Shdr(3, (uint)strOff, (uint)strtab.Length));      // SHT_STRTAB
        elf.AddRange(Shdr(1, 0, 0));                                  // PROGBITS 占位
        return elf.ToArray();
    }

    [Fact]
    public void ParseObjects_FindsVariables_KeepsOrder_IgnoresFunctionsAndNulls()
    {
        var elf = BuildElf(
            ("gCounter", 0x20000004, 4, 1),      // STT_OBJECT
            ("s_local", 0x20000010, 1, 1),       // LOCAL object,同样有效
            ("doThing", 0x08000100, 88, 2),      // STT_FUNCTION → 排除
            ("zeroSize", 0x20000020, 0, 1),      // 零大小 → 排除
            ("atZero", 0x00000000, 4, 1));       // 地址 0 → 排除

        var result = ElfSymbols.ParseObjects(elf);

        Assert.Equal(2, result.Count);
        Assert.Equal("gCounter", result[0].Name);
        Assert.Equal(0x20000004, result[0].Address);
        Assert.Equal(4, result[0].Size);
        Assert.Equal("s_local", result[1].Name);
        Assert.Equal(0x20000010, result[1].Address);
    }

    [Fact]
    public void ParseObjects_RejectsBadMagic()
    {
        var elf = new byte[64];
        elf[1] = (byte)'E'; elf[2] = (byte)'L'; elf[3] = (byte)'X';
        var ex = Assert.Throws<ElfParseException>(() => ElfSymbols.ParseObjects(elf));
        Assert.Contains("magic", ex.Message);
    }

    [Fact]
    public void ParseObjects_RejectsBigEndian()
    {
        var elf = BuildElf(("x", 1, 1, 1));
        elf[5] = 2; // EI_DATA = big-endian
        Assert.Throws<ElfParseException>(() => ElfSymbols.ParseObjects(elf));
    }

    [Fact]
    public void ParseObjects_RejectsNoSymtab()
    {
        var elf = BuildElf(("x", 1, 1, 1));
        // 把唯一的 SHT_SYMTAB 改成 PROGBITS
        var shoff = BitConverter.ToInt32(elf, 0x20);
        BitConverter.GetBytes(1u).CopyTo(elf, shoff + 40 + 4);
        var ex = Assert.Throws<ElfParseException>(() => ElfSymbols.ParseObjects(elf));
        Assert.Contains("symtab", ex.Message);
    }
}
