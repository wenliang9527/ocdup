using System.ComponentModel;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Ocdup.Backend;
using Ocdup.Config;

namespace Ocdup.Tools;

/// <summary>烧录（本轮目标之一：下载）。</summary>
[McpServerToolType]
public static class FlashTools
{
    [McpServerTool(Name = "flash_info")]
    [Description("List the flash banks the backend knows (driver, base address, size). Read-only. Requires a running backend - call connect first if this fails. Probes every flash bank first, because auto-sized target cfgs (FLASH_SIZE=0, e.g. the generic at32f403axG.cfg) report size 0 until probed - and a second bank is NOT covered by probing bank 0 (measured on AT32F403AVGT7 dual-bank).")]
    public static async Task<string> FlashInfo(CancellationToken ct = default)
    {
        var cfg = ConfigStore.Instance.Current;
        try
        {
            var banks = await BanksAfterProbeAsync(cfg, ct).ConfigureAwait(false);
            if (banks.Length == 0)
                return "ERROR: the backend reported no flash banks. Check target_cfg in ocd.json (see show_config).";
            if (ZeroSizedBank.IsMatch(banks))
                banks += "\nNOTE: a flash bank reports size 0 even after probing it - the target_cfg uses runtime size detection (FLASH_SIZE=0). " +
                         "Check the probe/target connection (SWD wiring, power), then retry; a healthy auto-sized " +
                         "target reports its real size here.";
            return $"flash banks:\n{banks}";
        }
        catch (BackendException ex)
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// 列出 banks 前**逐个** probe（auto-size 配置 probe 过才有真实容量；双 bank 芯片的
    /// bank 1 不会因为 probe 0 被带上——实测 at32f403axG：`flash probe 0` 只给 bank0 的
    /// 真实容量，bank1 必须显式 `flash probe 1`，驱动自己会报
    /// "main flash size: 0x100000 ... bank size: 0x80000"）。
    /// best-effort：单个 bank probe 失败不中断；零 bank 返回 ""（调用方给「无 banks」文案）。
    /// 公开供 BackendTools 的 connect 自动探测复用（同一套镜像 tool=flash_info）。
    /// </summary>
    public static async Task<string> BanksAfterProbeAsync(OcdConfig cfg, CancellationToken ct)
    {
        var listed = (await BackendSession.Instance.ExecuteAsync(cfg, "flash banks", startIfNeeded: true, ct, op: OpNames.FlashInfo)).Trim();
        if (listed.Length == 0) return "";

        var count = 0;
        foreach (Match m in Regex.Matches(listed, @"#(\d+)\s*:"))
            count = Math.Max(count, int.Parse(m.Groups[1].Value) + 1);
        if (count == 0) count = 1; // 解析不出编号时至少探 bank 0

        for (var i = 0; i < count; i++)
        {
            try
            {
                await BackendSession.Instance.ExecuteAsync(cfg, $"flash probe {i}", startIfNeeded: false, ct, op: OpNames.FlashInfo).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is BackendException or OperationCanceledException)
            {
                // 某个 bank 探不到(没接/驱动不支持):不中断,让最终 banks 列表说话
            }
        }

        return (await BackendSession.Instance.ExecuteAsync(cfg, "flash banks", startIfNeeded: false, ct, op: OpNames.FlashInfo)).Trim();
    }

    [McpServerTool(Name = "flash_write")]
    [Description(
        "Download an image to the target flash. " +
        "The verdict comes from an independent read-back verify_image step: 'programmed + verified' (reset runs), " +
        "'programmed but verify FAILED' / 'write failed' / 'verify INCONCLUSIVE' (the backend could not run its CRC algorithm - " +
        "that says nothing about a mismatch; halt the target and retry verify_image). " +
        "reset is skipped for every non-clean verdict. Error text from the program step is advisory only " +
        "(some drivers emit spurious errors, e.g. PY32's fake AHB error); with verify=false it is also what gates the reset. " +
        "While running, openocd's log lines are forwarded as MCP progress notifications when the caller requested a progress token. " +
        "Gated by read_only / allow_flash / flash_allowed_paths / flash_max_bytes - a BLOCKED answer means the settings forbid it (see show_config). " +
        "Last line is the machine-readable verdict: OCDUP-RESULT: flash-write=<OK|VERIFY-FAILED|WRITE-FAILED|INCONCLUSIVE|UNVERIFIED>. " +
        "Same orchestration outside MCP as 'ocdup burn <file>'.")]
    public static async Task<string> FlashWrite(
        [Description("Path to the image to program (.elf/.hex/.bin; relative = ocd.json dir).")] string file,
        [Description("Load address for raw .bin (required there), hex like '0x08000000'; ignored for .elf/.hex.")] string? address = null,
        [Description("Verify the written image via read-back (default true).")] bool verify = true,
        [Description("Run 'reset run' after programming (default true).")] bool reset = true,
        RequestContext<CallToolRequestParams>? context = null,
        CancellationToken ct = default)
    {
        // P1-4：进度泵——调用方带 progressToken 且是自拉后端时，把 openocd 增量日志行
        // （擦扇区 / wrote 行）以 progress notification 推回；不带就零开销。
        // 编排本体在 FlashWriteCoreAsync（v0.4.20 起与 ocdup burn CLI 共用）。
        var progressPump = FlashProgress.Start(BackendSession.Instance, context?.Server, context?.Params?.Meta);
        using var pumpCts = new CancellationTokenSource();
        var pumpTask = progressPump?.RunAsync(pumpCts.Token) ?? Task.CompletedTask;
        try
        {
            var outcome = await FlashWriteCoreAsync(ConfigStore.Instance.Current, file, address, verify, reset, ct).ConfigureAwait(false);
            // A-10：机器可读结论只加在 MCP 侧（CLI 用退出码族）；早退的 ERROR/BLOCKED 不加。
            return outcome.Ok ? ResultTag.Append(outcome.Report, "flash-write", VerdictTag(outcome.Verdict)) : outcome.Report;
        }
        finally
        {
            // 进度是尽力而为的旁路：先停泵再收尾，绝不影响主操作的结论。
            pumpCts.Cancel();
            try { await pumpTask.ConfigureAwait(false); } catch { /* 泵的收尾 */ }
        }
    }

    /// <summary>flash_write 结论 → OCDUP-RESULT 词表（纯函数，可单测）。</summary>
    internal static string VerdictTag(FlashWriteVerdict verdict) => verdict switch
    {
        FlashWriteVerdict.ProgrammedVerified => "OK",
        FlashWriteVerdict.ProgrammedButVerifyFailed => "VERIFY-FAILED",
        FlashWriteVerdict.WriteFailed => "WRITE-FAILED",
        FlashWriteVerdict.ProgrammedVerifyInconclusive => "INCONCLUSIVE",
        _ => "UNVERIFIED",
    };

    // ── v0.4.20：烧录编排核心（flash_write 工具与 `ocdup burn` CLI 共用；闸与判据单一真源）──

    /// <summary>核心失败类别：CLI 据此区分退出码（2=用法/权限，3=后端）；MCP 工具形态一律回 Report 文本。</summary>
    internal enum CoreFailure { Usage, Blocked, Backend }

    /// <summary>核心结果：Ok=false 时 Failure 标明类别、Report 是可直接展示的错误文案；
    /// Ok=true 时 Report 是完整烧录报告、Verdict 是三段式结论（CLI 的退出码由它决定）。</summary>
    internal readonly record struct CoreOutcome(bool Ok, CoreFailure Failure, string Report, FlashWriteVerdict Verdict);

    /// <summary>
    /// 烧录编排：路径解析 → C2 闸（read_only/allow_flash/白名单/上限）→ `program`（不带
    /// verify）→ 独立 `verify_image` 读回 → <see cref="ClassifyFlashWrite"/> 三段式结论 →
    /// <see cref="ShouldResetRun"/> 门控 reset。v0.4.13（P0-2）定案：校验与写相分离——
    /// PY32 等驱动会在写相吐虚假 AHB 错误（操作实际已生效），「读回是否与镜像一致」才是
    /// 烧录成功的权威回答；program 错误文本降级为辅助信息，只用来区分两种失败形态。
    /// 校验通道失败按存疑判失败（保守），reset 不会执行。行为与 v0.4.13 起的 flash_write
    /// 完全一致（e2e 链路 4 钉死）；CLI 侧仅多出退出码映射（见 BurnCli.MapExitCode）。
    /// </summary>
    internal static async Task<CoreOutcome> FlashWriteCoreAsync(
        OcdConfig cfg, string file, string? address, bool verify, bool reset, CancellationToken ct)
    {
        string resolved;
        try
        {
            resolved = cfg.ResolvePath(file);
        }
        catch (Exception ex)
        {
            return new CoreOutcome(false, CoreFailure.Usage, $"ERROR: '{file}' is not a usable path: {ex.Message}", default);
        }

        if (!File.Exists(resolved))
            return new CoreOutcome(false, CoreFailure.Usage,
                $"ERROR: '{resolved}' does not exist. Pass the path of the built image (.elf/.hex/.bin); " +
                "relative paths are resolved against the ocd.json directory (see show_config for its location).", default);

        var image = new FileInfo(resolved);
        if (PermissionGate.CheckFlashWrite(cfg, resolved, image.Length) is { } blocked)
            return new CoreOutcome(false, CoreFailure.Blocked, blocked, default);

        long? loadAddress = null;
        if (!string.IsNullOrWhiteSpace(address))
        {
            if (!Parsing.TryParseHex(address, out var parsed))
                return new CoreOutcome(false, CoreFailure.Usage,
                    $"ERROR: address '{address}' is not a hex value. Use a hex string like '0x08000000'.", default);
            loadAddress = parsed;
        }
        else if (resolved.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
        {
            return new CoreOutcome(false, CoreFailure.Usage,
                "ERROR: a raw .bin image needs an explicit load address, e.g. flash_write(file=\"build/fw.bin\", address=\"0x08000000\"). " +
                "An .elf/.hex image carries its own addresses and needs no address argument.", default);
        }

        // program 不再带 verify——校验由下一步独立的 verify_image 承担。
        string programOutput;
        try
        {
            var programCommand = new StringBuilder("program ").Append(Tcl.Quote(resolved));
            if (loadAddress is { } addr) programCommand.Append(' ').Append(Parsing.Hex(addr));

            programOutput = (await BackendSession.Instance.ExecuteAsync(cfg, programCommand.ToString(), startIfNeeded: true, ct, op: OpNames.FlashWrite)).Trim();
        }
        catch (BackendException ex)
        {
            return new CoreOutcome(false, CoreFailure.Backend, ex.Message, default);
        }

        string? verifyProblem = null;
        // v0.4.30：CRC 算法起不来 ≠ 镜像不匹配（真机实测 2026-10-13）——burn/flash_write 与
        // verify_image 工具用同一份 <see cref="DetectCrcAlgorithmFailure"/> 判定，否则固件在跑时
        // 读回验不成会被误报成「VERIFY FAILED」（v0.4.29 只在 verify 侧修掉，burn 侧漏了同一误报）。
        string? crcProblem = null;
        if (verify)
        {
            var verifyCommand = new StringBuilder("verify_image ").Append(Tcl.Quote(resolved));
            if (loadAddress is { } vaddr) verifyCommand.Append(' ').Append(Parsing.Hex(vaddr));

            string verifyOutput;
            try
            {
                verifyOutput = (await BackendSession.Instance.ExecuteAsync(cfg, verifyCommand.ToString(), startIfNeeded: false, ct, op: OpNames.FlashWrite)).Trim();
            }
            catch (BackendException ex)
            {
                verifyOutput = ex.Message; // 校验通道失败：按存疑判失败（保守），reset 不会执行
            }

            crcProblem = DetectCrcAlgorithmFailure(verifyOutput);
            verifyProblem = InterpretVerifyOutput(verifyOutput);
        }

        var verdict = ClassifyFlashWrite(verify, programOutput, verifyProblem, crcProblem);

        var sb = new StringBuilder();
        sb.AppendLine($"programmed: {resolved} ({image.Length} bytes)");
        if (loadAddress is { } reported) sb.AppendLine($"load address: {Parsing.Hex(reported)}");
        sb.AppendLine(programOutput.Length == 0 ? "(the program step produced no output)" : programOutput);

        switch (verdict)
        {
            case FlashWriteVerdict.ProgrammedVerified:
                sb.AppendLine($"verified: {resolved} ({image.Length} bytes) matches the target flash (read-back).");
                break;
            case FlashWriteVerdict.ProgrammedButVerifyFailed:
                sb.AppendLine($"VERIFY FAILED: the program step reported success, but the read-back does not match {resolved} ({image.Length} bytes).");
                sb.AppendLine($"problem: {verifyProblem}");
                break;
            case FlashWriteVerdict.WriteFailed:
                sb.AppendLine($"WRITE FAILED: the program step reported an error, and the read-back does not confirm the image ({image.Length} bytes).");
                sb.AppendLine($"problem: {verifyProblem}");
                break;
            case FlashWriteVerdict.ProgrammedVerifyInconclusive:
                // v0.4.30：与 verify_image 的 INCONCLUSIVE 同款措辞——「没验成」不是「固件坏了」，
                // 否则模型会无谓重烧（真机实测：同一条命令目标静止时 verified、运行态时 CRC 起不来）。
                sb.AppendLine("VERIFY INCONCLUSIVE: the read-back did not run to completion, so this says nothing about " +
                              $"whether {resolved} ({image.Length} bytes) matches the target flash.");
                sb.AppendLine($"reason: {crcProblem}");
                break;
            case FlashWriteVerdict.Unverified:
                sb.AppendLine("unverified: verify=false skipped the read-back check - run mcp__ocdup__verify_image to confirm what landed.");
                break;
        }

        if (!reset) return new CoreOutcome(true, CoreFailure.Usage, sb.ToString(), verdict);

        if (!ShouldResetRun(verdict, programOutput))
        {
            sb.Append("NOTE: 'reset run' was skipped - the verdict above is not a clean pass. " +
                      "Check the log above (probe wiring, flash bank, target_cfg), then flash again.");
            return new CoreOutcome(true, CoreFailure.Usage, sb.ToString(), verdict);
        }

        try
        {
            var resetOutput = (await BackendSession.Instance.ExecuteAsync(cfg, "reset run", startIfNeeded: false, ct, op: OpNames.FlashWrite)).Trim();
            sb.AppendLine(resetOutput.Length == 0 ? "reset run: done" : $"reset run: {resetOutput}");
        }
        catch (BackendException ex)
        {
            sb.AppendLine($"NOTE: the image was programmed, but 'reset run' failed: {ex.Message}");
        }

        return new CoreOutcome(true, CoreFailure.Usage, sb.ToString(), verdict);
    }

    [McpServerTool(Name = "verify_image")]
    [Description(
        "Compare an image file against the target flash WITHOUT writing anything (backend command 'verify_image') - " +
        "answers 'what firmware is actually on the board'. " +
        "Read-only: no permission gate applies and no file is written on this PC. " +
        "IMPORTANT: the backend's flash-verify algorithm needs a stably halted core. If the firmware is running, " +
        "the answer can be 'VERIFY INCONCLUSIVE' (the CRC algorithm could not run) - that says NOTHING about whether " +
        "the content matches; halt (allow_exec), verify again, then resume. " +
        "Last line is the machine-readable verdict: OCDUP-RESULT: verify=<OK|FAILED|INCONCLUSIVE>. " +
        "Also available outside MCP as 'ocdup verify <file>' (exit-code mapped).")]
    public static async Task<string> VerifyImage(
        [Description("Path to the image to verify against target flash (.elf/.hex/.bin; relative = ocd.json dir).")] string file,
        [Description("Offset for raw .bin (required there), hex like '0x08000000'; ignored for .elf/.hex.")] string? address = null,
        CancellationToken ct = default)
    {
        // 编排本体在 VerifyCoreAsync（v0.4.26 起与 `ocdup verify` CLI 共用；判据单一真源）
        var outcome = await VerifyCoreAsync(ConfigStore.Instance.Current, file, address, ct).ConfigureAwait(false);
        return outcome.Ok
            ? ResultTag.Append(outcome.Report, "verify", VerifyTag(outcome.Verified, outcome.Inconclusive))
            : outcome.Report;
    }

    /// <summary>verify 结果：Ok=false 时 Failure 标明类别（CLI 退出码 2/3）；
    /// Ok=true 时 Verified 是权威结论（CLI 退出码 0/1）、Inconclusive 区分 FAILED 与
    /// 「CRC 没跑成」（两者 CLI 都 exit 1，MCP 侧的 OCDUP-RESULT 词表不同）。Report 与 MCP 工具逐字一致。</summary>
    internal readonly record struct VerifyOutcome(bool Ok, CoreFailure Failure, string Report, bool Verified, bool Inconclusive = false);

    /// <summary>
    /// 比对编排（v0.4.26 从 verify_image 工具抽出，与 CLI 共用）：路径解析 → .bin 必须显式
    /// 偏移 → `verify_image` 后端命令 → <see cref="InterpretVerifyOutput"/> 保守判据
    /// （无 "verified N bytes" 确认即判不通过，宁误报失败）。只读，无权限闸（与 MCP 工具
    /// 同语义）；报告文本与 v0.3.5 起的 verify_image 工具逐字一致（e2e 链路 4 钉死）。
    /// </summary>
    internal static async Task<VerifyOutcome> VerifyCoreAsync(
        OcdConfig cfg, string file, string? address, CancellationToken ct)
    {
        string resolved;
        try
        {
            resolved = cfg.ResolvePath(file);
        }
        catch (Exception ex)
        {
            return new VerifyOutcome(false, CoreFailure.Usage, $"ERROR: '{file}' is not a usable path: {ex.Message}", false);
        }

        if (!File.Exists(resolved))
            return new VerifyOutcome(false, CoreFailure.Usage,
                $"ERROR: '{resolved}' does not exist. Pass the path of the built image (.elf/.hex/.bin); " +
                "relative paths are resolved against the ocd.json directory (see show_config for its location).", false);

        var image = new FileInfo(resolved);
        long? loadAddress = null;
        if (!string.IsNullOrWhiteSpace(address))
        {
            if (!Parsing.TryParseHex(address, out var parsed))
                return new VerifyOutcome(false, CoreFailure.Usage,
                    $"ERROR: address '{address}' is not a hex value. Use a hex string like '0x08000000'.", false);
            loadAddress = parsed;
        }
        else if (resolved.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
        {
            return new VerifyOutcome(false, CoreFailure.Usage,
                "ERROR: a raw .bin image needs an explicit offset, e.g. verify_image(file=\"build/fw.bin\", address=\"0x08000000\"). " +
                "An .elf/.hex image carries its own addresses and needs no address argument.", false);
        }

        // 只读工具的失败文案与 CLI 共用 VerifyCoreAsync；halt 提示文案在 tool description 里也写了。

        var command = new StringBuilder("verify_image ").Append(Tcl.Quote(resolved));
        if (loadAddress is { } addr) command.Append(' ').Append(Parsing.Hex(addr));

        string output;
        try
        {
            output = (await BackendSession.Instance.ExecuteAsync(cfg, command.ToString(), startIfNeeded: true, ct, op: OpNames.VerifyImage)).Trim();
        }
        catch (BackendException ex)
        {
            return new VerifyOutcome(false, CoreFailure.Backend, ex.Message, false);
        }

        // v0.4.29（真机实测）：CRC 算法起不来 ≠ 镜像不匹配。先认这一类输出，
        // 否则保守判据会把「没验成」说成「固件坏了」，模型据此无谓重烧。
        var crcProblem = DetectCrcAlgorithmFailure(output);
        var problem = InterpretVerifyOutput(output);
        var report = ComposeVerifyReport(resolved, image.Length, problem, output, crcProblem);

        // CRC 起不来 = 存疑：既不算通过也不算失败（exit 1），但报告措辞已说明原因。
        return new VerifyOutcome(true, CoreFailure.Usage, report, Verified: problem is null, Inconclusive: crcProblem is not null);
    }

    /// <summary>verify_image 结论 → OCDUP-RESULT 词表（纯函数，可单测）：INCONCLUSIVE
    ///（CRC 算法没跑成，≠ 不匹配）优先于 FAILED 判定。</summary>
    internal static string VerifyTag(bool verified, bool inconclusive) =>
        inconclusive ? "INCONCLUSIVE" : verified ? "OK" : "FAILED";

    /// <summary>
    /// 判定 verify_image 的后端输出（纯函数，便于单测）：返回 null = 校验通过；
    /// 返回字符串 = 失败/存疑的原因。成功形态是 "verified N bytes in Ts"；
    /// 任何 Error:/failed/mismatch/No source/空输出 都判不通过（保守：宁误报失败）。
    /// </summary>
    public static string? InterpretVerifyOutput(string output)
    {
        if (output.Length == 0) return "the backend produced no output";
        if (output.Contains("verified", StringComparison.OrdinalIgnoreCase) && !LooksLikeFailure(output))
            return null;
        return LooksLikeFailure(output) ? "the backend reported an error" : "no 'verified N bytes' confirmation in the output";
    }

    /// <summary>
    /// 后端报了「CRC 校验算法跑不起来」这一类输出（真机实测 2026-10-13，AT32F403AVGT7 +
    /// atopenocd-py32）：`target not halted` / `error executing cortex_m crc algorithm` /
    /// `timed out while waiting for target halted`。**这不表示镜像与 flash 不匹配**——
    /// atopenocd 的 CRC 算法需要一个稳定的 halt 态，固件在跑时它的时序不稳；实测同一条
    /// verify_image 命令在目标静止时能回 `verified 524288 bytes`，在运行态/竞争态下失败。
    /// 纯函数：命中即返回可操作文案，否则 null（调用方据此改判「存疑」而非「不匹配」）。
    /// </summary>
    public static string? DetectCrcAlgorithmFailure(string output)
    {
        if (output.Length == 0) return null;
        return output.Contains("crc algorithm", StringComparison.OrdinalIgnoreCase)
            || output.Contains("target not halted", StringComparison.OrdinalIgnoreCase)
            || output.Contains("waiting for target halted", StringComparison.OrdinalIgnoreCase)
            ? "the backend could not run its CRC algorithm over the target (" +
              "openocd's flash-verify algorithm needs a stably halted core; the messages were: " +
              Compact(output) + "). This is NOT evidence that the image differs from the flash - " +
              "halt the target (allow_exec), run the verify again, then resume. " +
              "If it still fails while halted, the content really does differ."
            : null;
    }

    /// <summary>把多行后端输出压成单行片段（引进文案的诊断尾巴，限长避免撑爆回复）。</summary>
    private static string Compact(string output)
    {
        var flat = output.ReplaceLineEndings(" / ").Trim();
        return flat.Length <= 160 ? flat : flat[..160] + "…";
    }

    /// <summary>
    /// verify_image 报告组装（纯函数，可单测）：`crcProblem` 非空 = CRC 算法起不来，
    /// 结论用「VERIFY INCONCLUSIVE」而不是「VERIFY FAILED」——措辞必须让模型知道
    /// 「固件没坏，只是这次没验成」，否则会误判固件损坏并触发无谓的重烧。
    /// </summary>
    internal static string ComposeVerifyReport(
        string resolved, long sizeBytes, string? problem, string output, string? crcProblem)
    {
        if (crcProblem is not null)
            return $"VERIFY INCONCLUSIVE: the comparison did not run to completion, so this says nothing about " +
                   $"whether {resolved} ({sizeBytes} bytes) matches the target flash.\n" +
                   $"reason: {crcProblem}\nbackend output:\n{output}";

        return problem is not null
            ? $"VERIFY FAILED: {resolved} ({sizeBytes} bytes) does not match the target flash.\n" +
              $"problem: {problem}\nbackend output:\n{output}"
            : $"verified: {resolved} ({sizeBytes} bytes) matches the target flash.\nbackend: " +
              (output.Length == 0 ? "(no output)" : output);
    }

    [McpServerTool(Name = "flash_erase_sector")]
    [Description(
        "Erase flash sectors without writing anything. DESTRUCTIVE and OFF by default: gated by read_only and allow_flash_erase " +
        "(default false - set \"allow_flash_erase\": true in the ocd.json permissions block). " +
        "Parameters: bank (index from flash_info, default 0), first/last (inclusive sector numbers; last may be -1 meaning 'to the end of the bank'). " +
        "While running, openocd's log lines are forwarded as MCP progress notifications (when the caller requested a progress token).")]
    public static async Task<string> FlashEraseSector(
        [Description("Flash bank index from flash_info (default 0).")] int bank = 0,
        [Description("First sector to erase (inclusive, default 0).")] int first = 0,
        [Description("Last sector to erase (inclusive; -1 = to the end of the bank, default -1).")] int last = -1,
        RequestContext<CallToolRequestParams>? context = null,
        CancellationToken ct = default)
    {
        var cfg = ConfigStore.Instance.Current;

        if (PermissionGate.CheckFlashErase(cfg) is { } blocked)
            return blocked;

        if (bank < 0) return "ERROR: bank must be >= 0 (see flash_info for the bank list).";
        if (first < 0) return "ERROR: first must be >= 0.";
        if (last != -1 && last < first) return "ERROR: last must be >= first, or -1 for 'to the end of the bank'.";

        string lastText;
        if (last == -1)
        {
            // v0.4.35（audit F-2）：主线 openocd 不认字面量 'last'（旧实现发出去必失败——
            // e2e 假后端自证回显把它掩盖了）。「到 bank 末尾」改为先 probe 再数 `flash info`
            // 扇区表；解析不出就带指引报错，让调用方改传显式 first/last。
            var sectors = await ResolveSectorCountAsync(cfg, bank, ct).ConfigureAwait(false);
            if (sectors is null)
                return $"ERROR: cannot resolve last=-1 (to the end of bank {bank}): the sector table from " +
                       $"'flash info {bank}' did not parse. Run flash_info, then retry with explicit first/last sector numbers.";
            if (first >= sectors.Value)
                return $"ERROR: first={first} is out of range for bank {bank} ({sectors.Value} sectors per flash info).";
            lastText = (sectors.Value - 1).ToString();
        }
        else
        {
            lastText = last.ToString();
        }
        var command = $"flash erase_sector {bank} {first} {lastText}";

        var progressPump = FlashProgress.Start(BackendSession.Instance, context?.Server, context?.Params?.Meta);
        using var pumpCts = new CancellationTokenSource();
        var pumpTask = progressPump?.RunAsync(pumpCts.Token) ?? Task.CompletedTask;
        try
        {
            var output = (await BackendSession.Instance.ExecuteAsync(cfg, command, startIfNeeded: true, ct, op: OpNames.FlashEraseSector)).Trim();
            return $"erase issued: bank {bank}, sectors {first}..{lastText}.\n" +
                   (output.Length == 0 ? "(no output - check flash_info / verify_image afterwards)" : output);
        }
        catch (BackendException ex)
        {
            return ex.Message;
        }
        finally
        {
            pumpCts.Cancel();
            try { await pumpTask.ConfigureAwait(false); } catch { /* 泵的收尾 */ }
        }
    }

    /// <summary>
    /// last=-1（到 bank 末尾）的扇区数来源（v0.4.35，audit F-2）：probe（幂等；未 probe 的补上，
    /// 否则扇区表出不来）→ `flash info {bank}` 数扇区行。形态注记：`#N:` 编号行与 `flash banks`
    /// 的 bank 编号同形（AT32 双 bank 实测先例见 BanksAfterProbeAsync）；atopenocd fork 的
    /// flash info 扇区表归真机批 T 复核。失败返回 null → 调用方带指引报错。
    /// </summary>
    private static async Task<int?> ResolveSectorCountAsync(OcdConfig cfg, int bank, CancellationToken ct)
    {
        string info;
        try
        {
            await BackendSession.Instance.ExecuteAsync(cfg, $"flash probe {bank}", startIfNeeded: true, ct, op: OpNames.FlashEraseSector).ConfigureAwait(false);
            info = await BackendSession.Instance.ExecuteAsync(cfg, $"flash info {bank}", startIfNeeded: false, ct, op: OpNames.FlashEraseSector).ConfigureAwait(false);
        }
        catch (BackendException)
        {
            return null;
        }
        return CountSectors(info);
    }

    /// <summary>纯函数：`flash info` 输出 → 扇区数（取 `#N:` 最大编号 + 1；一行都解析不出 → null）。
    /// `#` 后的空白可选——banks 行实测是 `#0 :`，flash info 扇区行是 `#   0:`，一式两吃。</summary>
    internal static int? CountSectors(string info)
    {
        var max = -1;
        foreach (Match m in Regex.Matches(info, @"#\s*(\d+)\s*:"))
            max = Math.Max(max, int.Parse(m.Groups[1].Value));
        return max < 0 ? null : max + 1;
    }

    /// <summary>保守判断：OpenOCD 失败时会输出 Error:/failed 文本，命中就不做后续动作。</summary>
    private static bool LooksLikeFailure(string output) =>
        output.Contains("Error:", StringComparison.OrdinalIgnoreCase) ||
        output.Contains("failed", StringComparison.OrdinalIgnoreCase);

    /// <summary>flash_write 的结论分类（P0-2，纯函数可单测）：verify 读回是唯一判据；
    /// program 输出的错误文本降级为辅助信息，用来区分两种失败形态——
    /// WriteFailed（写步骤已报错，固件很可能没写入/只写入一部分）与
    /// ProgrammedButVerifyFailed（写声称成功但读回不符，最危险，结论单列）。
    /// v0.4.30 增 ProgrammedVerifyInconclusive：后端 CRC 算法没跑起来（目标在跑时真机复现），
    /// 「没验成」既不是通过也不是不匹配——reset 不放行，burn exit 1，但措辞必须说清原因。</summary>
    internal enum FlashWriteVerdict { ProgrammedVerified, ProgrammedButVerifyFailed, WriteFailed, Unverified, ProgrammedVerifyInconclusive }

    internal static FlashWriteVerdict ClassifyFlashWrite(bool verifyEnabled, string programOutput, string? verifyProblem, string? crcProblem = null) =>
        !verifyEnabled ? FlashWriteVerdict.Unverified
        : crcProblem is not null ? FlashWriteVerdict.ProgrammedVerifyInconclusive
        : verifyProblem is null ? FlashWriteVerdict.ProgrammedVerified
        : LooksLikeFailure(programOutput) ? FlashWriteVerdict.WriteFailed
        : FlashWriteVerdict.ProgrammedButVerifyFailed;

    /// <summary>reset run 门控（纯函数，可单测）：读回校验通过才放行；verify=false 时
    /// 退回文本嗅探（辅助判据——此时没有更好的）。</summary>
    internal static bool ShouldResetRun(FlashWriteVerdict verdict, string programOutput) =>
        verdict switch
        {
            FlashWriteVerdict.ProgrammedVerified => true,
            FlashWriteVerdict.Unverified => !LooksLikeFailure(programOutput),
            _ => false,
        };

    /// <summary>"size 0x0…,"（任意个 0）：容量未探测到的 bank 行（纯函数，便于单测）。</summary>
    private static readonly Regex ZeroSizedBank = new(@"size\s+0x0+\s*,", RegexOptions.IgnoreCase | RegexOptions.Compiled);
}
