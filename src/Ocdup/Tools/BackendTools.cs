using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using Ocdup.Backend;
using Ocdup.Config;

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
            var version = (await BackendSession.Instance.ExecuteAsync(cfg, "version", startIfNeeded: false, ct)).Trim();
            var targets = (await BackendSession.Instance.ExecuteAsync(cfg, "targets", startIfNeeded: false, ct)).Trim();

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
            var version = (await BackendSession.Instance.ExecuteAsync(cfg, "version", startIfNeeded: true, ct)).Trim();
            var targets = (await BackendSession.Instance.ExecuteAsync(cfg, "targets", startIfNeeded: false, ct)).Trim();

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
}
