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
        var listed = (await BackendSession.Instance.ExecuteAsync(cfg, "flash banks", startIfNeeded: true, ct, op: "flash_info")).Trim();
        if (listed.Length == 0) return "";

        var count = 0;
        foreach (Match m in Regex.Matches(listed, @"#(\d+)\s*:"))
            count = Math.Max(count, int.Parse(m.Groups[1].Value) + 1);
        if (count == 0) count = 1; // 解析不出编号时至少探 bank 0

        for (var i = 0; i < count; i++)
        {
            try
            {
                await BackendSession.Instance.ExecuteAsync(cfg, $"flash probe {i}", startIfNeeded: false, ct, op: "flash_info").ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is BackendException or OperationCanceledException)
            {
                // 某个 bank 探不到(没接/驱动不支持):不中断,让最终 banks 列表说话
            }
        }

        return (await BackendSession.Instance.ExecuteAsync(cfg, "flash banks", startIfNeeded: false, ct, op: "flash_info")).Trim();
    }

    [McpServerTool(Name = "flash_write")]
    [Description(
        "Download an image to the target flash. Parameters: file (path to .elf/.hex/.bin; relative paths resolve against the ocd.json directory), " +
        "address (hex load address like '0x08000000'; required for raw .bin images, ignored for .elf/.hex), verify (default true), " +
        "reset (default true: run 'reset run' after programming). " +
        "The outcome is decided by a read-back: after 'program', a separate 'verify_image' step is the authoritative verdict - " +
        "'programmed + verified' (reset runs), 'programmed but verify FAILED' / 'write failed' (reset is skipped). " +
        "Error text from the program step is advisory only (some drivers emit spurious errors, e.g. PY32's fake AHB error). " +
        "With verify=false the outcome is 'programmed (unverified)' and only that advisory text gates the reset. " +
        "While running, openocd's log lines (erased sectors / wrote bytes) are forwarded as MCP progress notifications " +
        "when the caller requested a progress token. " +
        "Gated by read_only / allow_flash / flash_allowed_paths / flash_max_bytes - a BLOCKED answer means the settings forbid it (see show_config). " +
        "The same orchestration is available outside MCP as 'ocdup burn <file>' (v0.4.20).")]
    public static async Task<string> FlashWrite(
        [Description("Path to the image to program (.elf/.hex/.bin).")] string file,
        [Description("Load address for raw .bin images, hex string like '0x08000000'. Ignored for .elf/.hex.")] string? address = null,
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
            return outcome.Report;
        }
        finally
        {
            // 进度是尽力而为的旁路：先停泵再收尾，绝不影响主操作的结论。
            pumpCts.Cancel();
            try { await pumpTask.ConfigureAwait(false); } catch { /* 泵的收尾 */ }
        }
    }

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

            programOutput = (await BackendSession.Instance.ExecuteAsync(cfg, programCommand.ToString(), startIfNeeded: true, ct, op: "flash_write")).Trim();
        }
        catch (BackendException ex)
        {
            return new CoreOutcome(false, CoreFailure.Backend, ex.Message, default);
        }

        string? verifyProblem = null;
        if (verify)
        {
            var verifyCommand = new StringBuilder("verify_image ").Append(Tcl.Quote(resolved));
            if (loadAddress is { } vaddr) verifyCommand.Append(' ').Append(Parsing.Hex(vaddr));

            string verifyOutput;
            try
            {
                verifyOutput = (await BackendSession.Instance.ExecuteAsync(cfg, verifyCommand.ToString(), startIfNeeded: false, ct, op: "flash_write")).Trim();
            }
            catch (BackendException ex)
            {
                verifyOutput = ex.Message; // 校验通道失败：按存疑判失败（保守），reset 不会执行
            }

            verifyProblem = InterpretVerifyOutput(verifyOutput);
        }

        var verdict = ClassifyFlashWrite(verify, programOutput, verifyProblem);

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
            var resetOutput = (await BackendSession.Instance.ExecuteAsync(cfg, "reset run", startIfNeeded: false, ct, op: "flash_write")).Trim();
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
        "Compare an image file against the target flash WITHOUT writing anything (backend command 'verify_image'). " +
        "Use it to answer 'what firmware is actually on the board' - e.g. re-verify a previously flashed build. " +
        "Parameters: file (.elf/.hex/.bin; relative paths resolve against the ocd.json directory), " +
        "address (required for raw .bin, hex string like '0x08000000'; ignored for .elf/.hex). " +
        "Read-only: no permission gate applies and no file is written on this PC. " +
        "The same comparison is available outside MCP as 'ocdup verify <file>' (v0.4.26, exit-code mapped).")]
    public static async Task<string> VerifyImage(
        [Description("Path to the image to verify against target flash (.elf/.hex/.bin).")] string file,
        [Description("Offset for raw .bin images, hex string like '0x08000000'. Ignored for .elf/.hex.")] string? address = null,
        CancellationToken ct = default)
    {
        // 编排本体在 VerifyCoreAsync（v0.4.26 起与 `ocdup verify` CLI 共用；判据单一真源）
        var outcome = await VerifyCoreAsync(ConfigStore.Instance.Current, file, address, ct).ConfigureAwait(false);
        return outcome.Report;
    }

    /// <summary>verify 结果：Ok=false 时 Failure 标明类别（CLI 退出码 2/3）；
    /// Ok=true 时 Verified 是权威结论（CLI 退出码 0/1）、Report 与 MCP 工具逐字一致。</summary>
    internal readonly record struct VerifyOutcome(bool Ok, CoreFailure Failure, string Report, bool Verified);

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

        var command = new StringBuilder("verify_image ").Append(Tcl.Quote(resolved));
        if (loadAddress is { } addr) command.Append(' ').Append(Parsing.Hex(addr));

        string output;
        try
        {
            output = (await BackendSession.Instance.ExecuteAsync(cfg, command.ToString(), startIfNeeded: true, ct, op: "verify_image")).Trim();
        }
        catch (BackendException ex)
        {
            return new VerifyOutcome(false, CoreFailure.Backend, ex.Message, false);
        }

        if (InterpretVerifyOutput(output) is { } problem)
            return new VerifyOutcome(true, CoreFailure.Usage,
                $"VERIFY FAILED: {resolved} ({image.Length} bytes) does not match the target flash.\n" +
                $"problem: {problem}\nbackend output:\n{output}", Verified: false);

        return new VerifyOutcome(true, CoreFailure.Usage,
            $"verified: {resolved} ({image.Length} bytes) matches the target flash.\nbackend: " +
            (output.Length == 0 ? "(no output)" : output), Verified: true);
    }

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

        var lastText = last == -1 ? "last" : last.ToString();
        var command = $"flash erase_sector {bank} {first} {lastText}";

        var progressPump = FlashProgress.Start(BackendSession.Instance, context?.Server, context?.Params?.Meta);
        using var pumpCts = new CancellationTokenSource();
        var pumpTask = progressPump?.RunAsync(pumpCts.Token) ?? Task.CompletedTask;
        try
        {
            var output = (await BackendSession.Instance.ExecuteAsync(cfg, command, startIfNeeded: true, ct, op: "flash_erase_sector")).Trim();
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

    /// <summary>保守判断：OpenOCD 失败时会输出 Error:/failed 文本，命中就不做后续动作。</summary>
    private static bool LooksLikeFailure(string output) =>
        output.Contains("Error:", StringComparison.OrdinalIgnoreCase) ||
        output.Contains("failed", StringComparison.OrdinalIgnoreCase);

    /// <summary>flash_write 的结论分类（P0-2，纯函数可单测）：verify 读回是唯一判据；
    /// program 输出的错误文本降级为辅助信息，用来区分两种失败形态——
    /// WriteFailed（写步骤已报错，固件很可能没写入/只写入一部分）与
    /// ProgrammedButVerifyFailed（写声称成功但读回不符，最危险，结论单列）。</summary>
    internal enum FlashWriteVerdict { ProgrammedVerified, ProgrammedButVerifyFailed, WriteFailed, Unverified }

    internal static FlashWriteVerdict ClassifyFlashWrite(bool verifyEnabled, string programOutput, string? verifyProblem) =>
        !verifyEnabled ? FlashWriteVerdict.Unverified
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
