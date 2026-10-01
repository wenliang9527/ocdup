using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using Ocdup.Backend;
using Ocdup.Config;
using Ocdup.Launcher;

namespace Ocdup.Tools;

/// <summary>后端管理与状态（设计文档第 5 节的 connect / status）。</summary>
[McpServerToolType]
public static class BackendTools
{
    [McpServerTool(Name = "status")]
    [Description("Report whether an OpenOCD backend is listening on the configured command port (default 127.0.0.1:6666) and, when it is, its version and target list. Read-only: starts nothing and does not disturb the target.")]
    public static async Task<string> Status(CancellationToken ct = default)
    {
        var cfg = ConfigStore.Instance.Current;

        if (!await BackendSession.IsBackendUpAsync(cfg, ct))
            return $"OpenOCD: not running - nothing is listening on {cfg.Host}:{cfg.Port}. " +
                   "Call connect to start the backend from interface_cfg/target_cfg.";

        try
        {
            var version = (await BackendSession.Instance.ExecuteAsync(cfg, "version", startIfNeeded: false, ct, op: "status")).Trim();
            var targets = (await BackendSession.Instance.ExecuteAsync(cfg, "targets", startIfNeeded: false, ct, op: "status")).Trim();

            var sb = new StringBuilder();
            sb.AppendLine($"OpenOCD: running on {cfg.Host}:{cfg.Port}");
            sb.AppendLine($"version: {(version.Length == 0 ? "(no version reported)" : version)}");
            if (BackendSession.Instance.LastBackendNote is { Length: > 0 } note)
                sb.AppendLine($"backend: {note}");
            sb.AppendLine("targets:");
            sb.Append(targets.Length == 0 ? "(none - is target_cfg set in ocd.json?)" : targets);
            return sb.ToString();
        }
        catch (BackendException ex)
        {
            return $"OpenOCD: {cfg.Host}:{cfg.Port} has a listener, but the TCL-RPC channel failed: {ex.Message}";
        }
    }

    [McpServerTool(Name = "connect")]
    [Description("Connect to the OpenOCD backend on the configured command port, starting openocd from ocd.json (interface_cfg/target_cfg) when nothing is listening yet. Returns the OpenOCD version and the target state. Call this before flash_write/read_memory in a fresh session.")]
    public static async Task<string> Connect(CancellationToken ct = default)
    {
        var cfg = ConfigStore.Instance.Current;

        try
        {
            var version = (await BackendSession.Instance.ExecuteAsync(cfg, "version", startIfNeeded: true, ct, op: "connect")).Trim();
            var targets = (await BackendSession.Instance.ExecuteAsync(cfg, "targets", startIfNeeded: false, ct, op: "connect")).Trim();

            var sb = new StringBuilder();
            sb.AppendLine($"connected to OpenOCD on {cfg.Host}:{cfg.Port}");
            sb.AppendLine($"version: {(version.Length == 0 ? "(no version reported)" : version)}");
            if (BackendSession.Instance.LastBackendNote is { Length: > 0 } note)
                sb.AppendLine($"backend: {note}");
            sb.AppendLine("targets:");
            sb.Append(targets.Length == 0 ? "(none - is target_cfg set in ocd.json?)" : targets);
            return sb.ToString();
        }
        catch (BackendException ex)
        {
            return ex.Message;
        }
    }

    [McpServerTool(Name = "backend_log")]
    [Description(
        "Return recent stdout/stderr lines (Info:/Error: logs) of the OpenOCD process that ocdup itself started - a rolling 200-line buffer. " +
        "Use it to diagnose flash/read failures and target events that never reach the TCL-RPC response text. Read-only. " +
        "Returns an explanation instead of log lines when the listener on the command port was started outside ocdup (its output is not captured).")]
    public static Task<string> BackendLog(int lines = 40, CancellationToken ct = default)
    {
        var capped = Math.Clamp(lines, 1, OpenOcdLauncher.MaxBufferLines);
        var recent = BackendSession.Instance.RecentBackendOutput(capped);
        return Task.FromResult(recent.Length == 0
            ? "no captured backend output: ocdup has not started an OpenOCD process in this session. " +
              "An already-running listener's output goes to its own console, not here. " +
              "Call connect first so ocdup can start (and capture) the backend."
            : $"last {capped} openocd output line(s):\n{recent}");
    }
}
