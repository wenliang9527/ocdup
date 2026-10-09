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

    // ── flash_write 三段式结论(P0-2):verify 读回是唯一判据,program 输出只作辅助信息 ──

    [Fact]
    public void ClassifyFlashWrite_VerifyPass_WinsOverScaryProgramOutput()
    {
        // PY32 回归:写相的虚假 AHB 错误(操作实际已生效,README 实测记录)不得推翻读回校验通过。
        var verdict = FlashTools.ClassifyFlashWrite(
            verifyEnabled: true,
            programOutput: "Error: Failed to write memory (spurious Puya AHB error)",
            verifyProblem: null);

        Assert.Equal(FlashTools.FlashWriteVerdict.ProgrammedVerified, verdict);
    }

    [Fact]
    public void ClassifyFlashWrite_VerifyFailWithCleanProgramOutput_IsTheDangerousCase()
    {
        // 写声称成功但读回不符——最危险形态,结论要单列(reset 必须跳过)。
        var verdict = FlashTools.ClassifyFlashWrite(
            verifyEnabled: true,
            programOutput: "** Programming Finished **",
            verifyProblem: "content mismatch: addr=0x08000000, target=0xff, file=0x00");

        Assert.Equal(FlashTools.FlashWriteVerdict.ProgrammedButVerifyFailed, verdict);
    }

    [Fact]
    public void ClassifyFlashWrite_VerifyFailWithFailedProgramOutput_IsWriteFailed()
    {
        var verdict = FlashTools.ClassifyFlashWrite(
            verifyEnabled: true,
            programOutput: "Error: flash driver does not support this bank",
            verifyProblem: "no 'verified N bytes' confirmation in the output");

        Assert.Equal(FlashTools.FlashWriteVerdict.WriteFailed, verdict);
    }

    [Fact]
    public void ClassifyFlashWrite_VerifyDisabled_IsUnverified()
    {
        Assert.Equal(
            FlashTools.FlashWriteVerdict.Unverified,
            FlashTools.ClassifyFlashWrite(verifyEnabled: false, programOutput: "", verifyProblem: null));
    }

    [Fact]
    public void ShouldResetRun_OnlyAfterVerifiedOrCleanUnverified()
    {
        Assert.True(FlashTools.ShouldResetRun(FlashTools.FlashWriteVerdict.ProgrammedVerified, "Error: ignored"));
        Assert.False(FlashTools.ShouldResetRun(FlashTools.FlashWriteVerdict.ProgrammedButVerifyFailed, ""));
        Assert.False(FlashTools.ShouldResetRun(FlashTools.FlashWriteVerdict.WriteFailed, "Error: bank missing"));

        // verify=false:没有读回判据,退回文本嗅探作门控(辅助判据,此时没有更好的)。
        Assert.True(FlashTools.ShouldResetRun(FlashTools.FlashWriteVerdict.Unverified, "** Programming Finished **"));
        Assert.False(FlashTools.ShouldResetRun(FlashTools.FlashWriteVerdict.Unverified, "Error: flash bank missing"));
    }
}
