using Ocdup.Backend;

namespace Ocdup.Tests;

/// <summary>TCL 字面量引用：路径必须原样到达 OpenOCD，转义后仍要能被 TCL 解析回原值。</summary>
public sealed class TclQuoteTests
{
    [Theory]
    [InlineData("/usr/share/openocd/build/fw.bin")]
    [InlineData(@"C:\proj\build\fw.bin")]
    [InlineData(@"C:\Program Files\fw.bin")]
    [InlineData("relative/fw.bin")]
    public void Quote_UsesBraceLiteral_ForOrdinaryPaths(string path)
    {
        Assert.Equal("{" + path + "}", Tcl.Quote(path));
    }

    [Fact]
    public void Quote_EscapesSpecialsInsideQuotedForm()
    {
        // 含花括号时必须走双引号形式：TCL 在双引号里会做替换，$ [ ] " \ 需要转义；
        // 而 { } 在双引号形式里是普通字符，不需要（也不能）转义。
        var quoted = Tcl.Quote("a{b}c$d[e]\"f\\g");

        Assert.Equal("\"a{b}c\\$d\\[e\\]\\\"f\\\\g\"", quoted);
    }

    [Fact]
    public void Quote_KeepsWindowsPathBackslashesIntact()
    {
        // 大括号字面量里 TCL 不做转义，反斜杠必须原样保留（否则 Windows 路径会被吃掉）。
        Assert.Equal(@"{C:\a b\c.d}", Tcl.Quote(@"C:\a b\c.d"));
    }
}
