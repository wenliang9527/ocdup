using System.Diagnostics;
using Ocdup.Backend;
using Ocdup.Config;
using Ocdup.Launcher;

namespace Ocdup.Cli;

/// <summary>单项体检结果（记录用，渲染见 <see cref="DoctorCli.FormatReport"/>）。</summary>
internal enum DoctorVerdict { Ok, Warn, Fail }

internal sealed record DoctorCheck(string Name, DoctorVerdict Verdict, string Detail, string? Hint = null);

/// <summary>
/// `ocdup doctor`（P1-6 / S5「换机 10 分钟」抓手）：把快速开始的四步外部依赖拼成一张
/// [OK]/[WARN]/[FAIL] + 如何修的体检单。全部检查只读——不拉起 openocd、不碰目标；
/// 端口上已有监听时只做一次只读的 version 查询。退出码：有 FAIL = 1（可直接进 CI），否则 0。
/// </summary>
public static class DoctorCli
{
    private const string HelpText = """
        ocdup doctor - environment checklist (v0.4.15)

        usage: ocdup doctor [--help]

        checks (all read-only: nothing is started, the target is not touched):
          ocd.json        found / parsed without errors / target_cfg present
          openocd binary  OCD_BIN or PATH, verified by running --version
          openocd scripts OCD_SCRIPTS or a layout next to the binary; target/ must exist
          debug probe     AT-Link / CMSIS-DAP / ST-Link / J-Link on USB
          backend port    a listener on the command port; if present, one read-only
                          version query proves the TCL-RPC channel works

        exit codes:
          0  no FAIL items (warnings are fine - a cold start has no backend yet)
          1  at least one FAIL item - fix the listed items and re-run

        typical first command on a new machine; config/backend come from ocd.json
        (OCD_CONFIG) exactly like the MCP tools.
        """;

    public static async Task<int> RunAsync(string[] args)
    {
        // v0.4.29：doctor 也走 ArgScanner 的 help 识别——此前 --help 被忽略、直接跑体检，
        // 在环境不全的机器上还会因 FAIL 返回 1，与其余四个 CLI 的 --help 约定不一致。
        var scanner = new ArgScanner("doctor", args, Array.Empty<string>(), Array.Empty<string>());
        while (scanner.Next(out _, out _)) { /* doctor 无选项；未知项由 scanner 记错 */ }

        if (scanner.Help)
        {
            Console.Write(HelpText);
            return 0;
        }
        if (scanner.Errors.Count > 0)
        {
            foreach (var e in scanner.Errors) Console.Error.WriteLine("ERROR: " + e);
            Console.Error.WriteLine("run 'ocdup doctor --help' for usage.");
            return 2;
        }

        var cfg = OcdConfig.Load();
        var checks = new List<DoctorCheck>
        {
            CheckConfig(cfg),
            await CheckBinaryAsync().ConfigureAwait(false),
            await CheckScriptsAsync().ConfigureAwait(false),
            await CheckProbeAsync().ConfigureAwait(false),
            await CheckBackendAsync(cfg).ConfigureAwait(false),
        };

        Console.Out.WriteLine(FormatReport(checks));
        return ExitCode(checks);
    }

    // ── 五项检查（系统调用，薄壳；文案的可判定部分尽量前置成纯数据）──

    private static DoctorCheck CheckConfig(OcdConfig cfg)
    {
        if (cfg.ConfigFilePath is null)
            return new DoctorCheck("ocd.json", DoctorVerdict.Fail, "no config file found",
                "run ocdup from the project dir that has an ocd.json, or point OCD_CONFIG at one (copy ocd.json.example)");

        if (cfg.Sources.TryGetValue("ocd_json_error", out var error))
            return new DoctorCheck("ocd.json", DoctorVerdict.Fail, $"{cfg.ConfigFilePath} parsed with errors: {error}",
                "fix the JSON above; broken values silently fall back to defaults (show_config lists what took effect)");

        if (cfg.TargetCfg is null)
            return new DoctorCheck("ocd.json", DoctorVerdict.Fail, $"{cfg.ConfigFilePath} has no target_cfg",
                "set \"target_cfg\" in ocd.json (an OpenOCD script name under the scripts dir, or an absolute path)");

        return new DoctorCheck("ocd.json", DoctorVerdict.Ok,
            $"{cfg.ConfigFilePath} (target_cfg: {cfg.TargetCfg}, interface_cfg: {cfg.InterfaceCfg ?? "(not set)"}, read_only: {cfg.ReadOnly})");
    }

    private static async Task<DoctorCheck> CheckBinaryAsync()
    {
        var binary = OpenOcdLauncher.FindBinary();
        if (binary is null)
            return new DoctorCheck("openocd binary", DoctorVerdict.Fail,
                "OCD_BIN is not set and nothing named openocd(.exe/.cmd/.bat) was found on PATH",
                "set OCD_BIN to the openocd executable, or install OpenOCD and put it on PATH (see the README registration snippet)");

        var version = await TryVersionAsync(binary).ConfigureAwait(false);
        if (version is null)
            return new DoctorCheck("openocd binary", DoctorVerdict.Fail, $"{binary} exists but did not answer --version",
                "check that the file is a working openocd build for this OS/architecture (run it by hand to see the error)");

        return new DoctorCheck("openocd binary", DoctorVerdict.Ok, $"{binary} ({version})");
    }

    private static Task<DoctorCheck> CheckScriptsAsync()
    {
        var scripts = OpenOcdLauncher.FindScriptsDir(OpenOcdLauncher.FindBinary());
        if (scripts is null)
            return Task.FromResult(new DoctorCheck("openocd scripts", DoctorVerdict.Fail,
                "OCD_SCRIPTS is not set and no scripts directory was found next to the binary",
                "set OCD_SCRIPTS to the openocd scripts directory (the one containing target/ and interface/)"));

        var hasTargets = Directory.Exists(Path.Combine(scripts, "target"));
        if (!hasTargets)
            return Task.FromResult(new DoctorCheck("openocd scripts", DoctorVerdict.Fail, $"{scripts} has no target/ subdirectory",
                "point OCD_SCRIPTS at the real openocd scripts directory (target/*.cfg live there)"));

        return Task.FromResult(new DoctorCheck("openocd scripts", DoctorVerdict.Ok, scripts));
    }

    private static async Task<DoctorCheck> CheckProbeAsync()
    {
        // PnP 枚举实测 3s+（内部 60s 缓存）；doctor 一次性行为，多等这一下值得。
        var probes = await ProbeDetector.DetectAsync().ConfigureAwait(false);
        if (probes.Count == 0)
            return new DoctorCheck("debug probe", DoctorVerdict.Fail,
                "no known probe found (AT-Link / CMSIS-DAP / ST-Link / J-Link)",
                "plug the probe in and check its driver; auto-detect enumerates USB PnP devices (Windows) and only knows those four families");

        return new DoctorCheck("debug probe", DoctorVerdict.Ok,
            string.Join("; ", probes.Select(p => $"{p.Kind} -> {p.Config}")));
    }

    private static async Task<DoctorCheck> CheckBackendAsync(OcdConfig cfg)
    {
        var endpoint = $"{cfg.Host}:{cfg.Port}";
        if (!await OpenOcdLauncher.ProbePortAsync(cfg.Host, cfg.Port, 300).ConfigureAwait(false))
            return new DoctorCheck("backend port", DoctorVerdict.Warn, $"nothing listening on {endpoint}",
                "fine for a cold start - connect (or any tool call) will launch openocd; re-run `ocdup doctor` after connect to also check the target");

        try
        {
            var version = (await BackendSession.Instance.ExecuteAsync(cfg, "version", startIfNeeded: false, op: "doctor").ConfigureAwait(false)).Trim();
            var firstLine = version.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "(empty)";
            return new DoctorCheck("backend port", DoctorVerdict.Ok, $"listener on {endpoint} answered: {firstLine}");
        }
        catch (BackendException ex)
        {
            return new DoctorCheck("backend port", DoctorVerdict.Fail, $"something listens on {endpoint} but the TCL-RPC channel failed: {ex.Message}",
                "the port may be owned by a non-OpenOCD program, or the listener is wedged - find and stop it (its owner), then connect again");
        }
    }

    /// <summary>跑 `openocd --version` 取首行（5s 预算；失败/超时一律 null 并顺手杀进程）。</summary>
    private static async Task<string?> TryVersionAsync(string binary)
    {
        Process? process = null;
        try
        {
            process = Process.Start(new ProcessStartInfo(binary, "--version")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            });
            if (process is null) return null;

            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var text = (await stdout.ConfigureAwait(false)) + (await stderr.ConfigureAwait(false));
            return text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();
        }
        catch
        {
            try { if (process is { HasExited: false }) process.Kill(entireProcessTree: true); } catch { /* 尽力而为 */ }
            return null;
        }
    }

    // ── 渲染与退出码（纯函数，可单测）──

    /// <summary>有 FAIL 即 1——可直接当 CI 预检用。</summary>
    internal static int ExitCode(IReadOnlyList<DoctorCheck> checks) =>
        checks.Any(c => c.Verdict == DoctorVerdict.Fail) ? 1 : 0;

    /// <summary>体检单渲染（纯函数，可单测）：每行 [OK]/[WARN]/[FAIL] 名字 + 细节，⚠️/❌ 缩进一行「如何修」，末尾汇总。ASCII 图标，CI 日志零乱码。</summary>
    internal static string FormatReport(IReadOnlyList<DoctorCheck> checks)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("ocdup doctor - environment checklist");
        foreach (var check in checks)
        {
            var icon = check.Verdict switch
            {
                DoctorVerdict.Ok => "[OK]",
                DoctorVerdict.Warn => "[WARN]",
                _ => "[FAIL]",
            };
            sb.AppendLine($"{icon} {check.Name}: {check.Detail}");
            if (check.Hint is { } hint)
                sb.AppendLine($"      -> how to fix: {hint}");
        }

        var ok = checks.Count(c => c.Verdict == DoctorVerdict.Ok);
        var warn = checks.Count(c => c.Verdict == DoctorVerdict.Warn);
        var fail = checks.Count(c => c.Verdict == DoctorVerdict.Fail);
        sb.AppendLine(fail == 0
            ? $"summary: {ok} ok, {warn} warning(s), 0 failed - environment looks ready."
            : $"summary: {ok} ok, {warn} warning(s), {fail} failed - fix the FAIL items above, then re-run `ocdup doctor`.");
        return sb.ToString();
    }
}
