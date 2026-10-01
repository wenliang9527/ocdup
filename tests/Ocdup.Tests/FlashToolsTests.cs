using Ocdup.Tools;

namespace Ocdup.Tests;

/// <summary>verify_image 的输出判定(纯函数):成功形态 / 失败形态 / 存疑保守判失败。</summary>
public sealed class FlashToolsTests
{
    [Theory]
    [InlineData("verified 4096 bytes in 0.01s")]
    [InlineData("Verified 28288 bytes in 1.537s")]
    public void InterpretVerifyOutput_AcceptsVerifiedOutput(string output)
    {
        Assert.Null(FlashTools.InterpretVerifyOutput(output));
    }

    [Theory]
    [InlineData("Error: checksum mismatch at 0x08000000")]
    [InlineData("verify_image failed")]
    [InlineData("content mismatch: addr=0x08000000, target=0xff, file=0x00")]
    public void InterpretVerifyOutput_RejectsErrorOutput(string output)
    {
        Assert.NotNull(FlashTools.InterpretVerifyOutput(output));
    }

    [Theory]
    [InlineData("")]
    [InlineData("echo: verify_image {…}")] // 没有 Error: 但也没有 verified 确认 → 保守判存疑
    public void InterpretVerifyOutput_IsConservativeWhenUnconclusive(string output)
    {
        Assert.NotNull(FlashTools.InterpretVerifyOutput(output));
    }
}
