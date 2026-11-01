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

    // ── A-10（v0.4.34）：OCDUP-RESULT 机器可读结论行 ──

    [Theory]
    [InlineData((int)FlashTools.FlashWriteVerdict.ProgrammedVerified, "OK")]
    [InlineData((int)FlashTools.FlashWriteVerdict.ProgrammedButVerifyFailed, "VERIFY-FAILED")]
    [InlineData((int)FlashTools.FlashWriteVerdict.WriteFailed, "WRITE-FAILED")]
    [InlineData((int)FlashTools.FlashWriteVerdict.ProgrammedVerifyInconclusive, "INCONCLUSIVE")]
    [InlineData((int)FlashTools.FlashWriteVerdict.Unverified, "UNVERIFIED")]
    public void VerdictTag_CoversTheWholeVerdictEnum(int verdict, string expected)
    {
        // 枚举是 internal（签名不能直接暴露），经 int 传入——值本身由 const 表达式钉死
        Assert.Equal(expected, FlashTools.VerdictTag((FlashTools.FlashWriteVerdict)verdict));
    }

    [Theory]
    [InlineData(true, false, "OK")]
    [InlineData(false, false, "FAILED")]
    [InlineData(false, true, "INCONCLUSIVE")] // CRC 没跑成 ≠ 不匹配：词表必须与 FAILED 区分
    public void VerifyTag_InconclusiveWinsOverFailed(bool verified, bool inconclusive, string expected)
    {
        Assert.Equal(expected, FlashTools.VerifyTag(verified, inconclusive));
    }

    [Fact]
    public void ResultTag_AppendIsAlwaysTheLastLine_AndKeepsTheHumanText()
    {
        // 人读文本一字不动（只吞尾随空白行），标记追加以换行分隔的最后一行
        var tagged = ResultTag.Append("report body\nnote line\n\n", "verify", "OK");
        Assert.Equal("report body\nnote line\nOCDUP-RESULT: verify=OK", tagged);
        Assert.EndsWith("\nOCDUP-RESULT: verify=OK", tagged);
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
    public void ClassifyFlashWrite_CrcAlgorithmFailure_IsInconclusive_NotMismatch()
    {
        // v0.4.30：burn/flash_write 与 verify_image 同一份判据——CRC 算法起不来（固件在跑时
        // 真机复现）必须落 INCONCLUSIVE，而不是 WriteFailed / ProgrammedButVerifyFailed。
        var verdict = FlashTools.ClassifyFlashWrite(
            verifyEnabled: true,
            programOutput: "** Programming Finished **",
            verifyProblem: "no 'verified N bytes' confirmation in the output",
            crcProblem: "the backend could not run its CRC algorithm over the target (...)");

        Assert.Equal(FlashTools.FlashWriteVerdict.ProgrammedVerifyInconclusive, verdict);
    }

    [Fact]
    public void ClassifyFlashWrite_CrcProblem_BeatsScaryProgramOutput()
    {
        // 与「verify 通过压过写相虚假错误」同构：CRC 判定优先，program 文本不再参与定性。
        var verdict = FlashTools.ClassifyFlashWrite(
            verifyEnabled: true,
            programOutput: "Error: Failed to write memory (spurious Puya AHB error)",
            verifyProblem: "the backend reported an error",
            crcProblem: "the backend could not run its CRC algorithm over the target (...)");

        Assert.Equal(FlashTools.FlashWriteVerdict.ProgrammedVerifyInconclusive, verdict);
    }

    [Fact]
    public void ClassifyFlashWrite_NoCrcProblem_KeepsTheOldVerdicts()
    {
        // 第四参默认 null：既有三态不受影响（回归护栏）。
        Assert.Equal(
            FlashTools.FlashWriteVerdict.ProgrammedVerified,
            FlashTools.ClassifyFlashWrite(verifyEnabled: true, programOutput: "", verifyProblem: null));
        Assert.Equal(
            FlashTools.FlashWriteVerdict.ProgrammedButVerifyFailed,
            FlashTools.ClassifyFlashWrite(verifyEnabled: true, programOutput: "ok", verifyProblem: "mismatch"));
    }

    [Fact]
    public void ShouldResetRun_OnlyAfterVerifiedOrCleanUnverified()
    {
        Assert.True(FlashTools.ShouldResetRun(FlashTools.FlashWriteVerdict.ProgrammedVerified, "Error: ignored"));
        Assert.False(FlashTools.ShouldResetRun(FlashTools.FlashWriteVerdict.ProgrammedButVerifyFailed, ""));
        Assert.False(FlashTools.ShouldResetRun(FlashTools.FlashWriteVerdict.WriteFailed, "Error: bank missing"));
        // v0.4.30：没验成同样不放行 reset——「不知道成没成」不是「成了」。
        Assert.False(FlashTools.ShouldResetRun(FlashTools.FlashWriteVerdict.ProgrammedVerifyInconclusive, ""));

        // verify=false:没有读回判据,退回文本嗅探作门控(辅助判据,此时没有更好的)。
        Assert.True(FlashTools.ShouldResetRun(FlashTools.FlashWriteVerdict.Unverified, "** Programming Finished **"));
        Assert.False(FlashTools.ShouldResetRun(FlashTools.FlashWriteVerdict.Unverified, "Error: flash bank missing"));
    }

    // ── v0.4.29 真机实测（AT32F403AVGT7 + atopenocd-py32）：CRC 算法起不来 ≠ 镜像不匹配 ──

    [Theory]
    [InlineData("target not halted\nerror executing cortex_m crc algorithm")]
    [InlineData("timed out while waiting for target halted\nerror executing cortex_m crc algorithm")]
    [InlineData("target not halted")]
    public void DetectCrcAlgorithmFailure_RecognisesTheRunningTargetSignatures(string output)
    {
        var problem = FlashTools.DetectCrcAlgorithmFailure(output);

        Assert.NotNull(problem);
        Assert.Contains("NOT evidence that the image differs", problem);
        Assert.Contains("halt", problem);
    }

    [Theory]
    [InlineData("")]
    [InlineData("verified 524288 bytes in 0.48s")]
    [InlineData("content mismatch: addr=0x08000000, target=0xff, file=0x00")]
    [InlineData("Error: flash bank missing")]
    public void DetectCrcAlgorithmFailure_LeavesOtherOutputsAlone(string output)
    {
        Assert.Null(FlashTools.DetectCrcAlgorithmFailure(output));
    }

    [Fact]
    public void ComposeVerifyReport_CrcFailure_SaysInconclusiveNotMismatch()
    {
        // 真机原文作为回归用例：这三种形态都不能被说成「固件不匹配」
        var crc = "target not halted\nerror executing cortex_m crc algorithm";
        var report = FlashTools.ComposeVerifyReport(
            "build/fw.bin", 524288, "the backend reported an error", crc,
            FlashTools.DetectCrcAlgorithmFailure(crc));

        Assert.StartsWith("VERIFY INCONCLUSIVE", report);
        Assert.Contains("says nothing about", report);
        Assert.DoesNotContain("does not match the target flash", report);
    }

    [Fact]
    public void ComposeVerifyReport_RealMismatch_StillSaysFailed()
    {
        var report = FlashTools.ComposeVerifyReport(
            "build/fw.bin", 4096, "content mismatch: target=0xff, file=0x00",
            "content mismatch: addr=0x08000000, target=0xff, file=0x00", null);

        Assert.StartsWith("VERIFY FAILED", report);
        Assert.Contains("does not match the target flash", report);
    }

    [Fact]
    public void ComposeVerifyReport_CleanPass_SaysVerified()
    {
        var report = FlashTools.ComposeVerifyReport(
            "build/fw.bin", 4096, null, "verified 4096 bytes in 0.01s", null);

        Assert.StartsWith("verified:", report);
        Assert.Contains("matches the target flash", report);
    }

    // ── F-2（v0.4.35）：flash info 扇区表 → 扇区数（erase last=-1「到 bank 末尾」的解析前提）──

    [Theory]
    [InlineData("#   0: 0x00000000 (0x800 2kB) not protected\n#   1: 0x00000800 (0x800 2kB) not protected\n#   2: 0x00001000 (0x800 2kB) not protected", 3)]
    [InlineData("# 7: 0x00007000 (0x800 2kB) not protected", 8)] // 只看最大编号，不假设连续
    public void CountSectors_TakesMaxIndexPlusOne(string info, int expected)
        => Assert.Equal(expected, FlashTools.CountSectors(info));

    [Theory]
    [InlineData("")]
    [InlineData("flash bank 0 not probed")]
    [InlineData("some output without sector rows")]
    public void CountSectors_Unparsable_ReturnsNull(string info)
        => Assert.Null(FlashTools.CountSectors(info));
}
