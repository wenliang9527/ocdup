using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Ocdup.Launcher;

/// <summary>探测到的调试探针(一种探针一条记录)。</summary>
public sealed record ProbeMatch(string Kind, string Config, string Evidence);

/// <summary>
/// 探针自动识别:interface_cfg = "auto" 时,在连接阶段枚举 USB 设备,
/// 按特征挑选接口脚本,让「插什么烧录器就用什么」。
/// Parse(纯函数)与 DetectAsync(系统调用)分离,便于无硬件单测。
/// </summary>
public static class ProbeDetector
{
    /// <summary>
    /// 签名表:数组顺序即优先级,同时插多根探针时排前的赢。
    /// 每条签名有名称正则 + 可选 VID/PID 正则(行内是 "FriendlyName|InstanceId",InstanceId 含 VID_xxxx&PID_yyyy)。
    /// 显示名会随驱动/系统变化,VID/PID 更稳;两者命中其一即认为匹配。
    /// </summary>
    private static readonly (Regex Name, Regex? Vid, string Kind, string Config)[] Signatures =
    {
        (new Regex(@"at-?link", RegexOptions.IgnoreCase | RegexOptions.Compiled),
         new Regex(@"VID_2E3C", RegexOptions.IgnoreCase | RegexOptions.Compiled),
         "AT-Link", "interface/atlink_dap_v2.cfg"),
        (new Regex(@"cmsis-?dap|daplink", RegexOptions.IgnoreCase | RegexOptions.Compiled),
         new Regex(@"VID_0D28&PID_0204|VID_C251&PID_00[0-9A-F]{2}", RegexOptions.IgnoreCase | RegexOptions.Compiled),
         "CMSIS-DAP/DAPLink", "interface/cmsis-dap.cfg"),
        (new Regex(@"st-?link", RegexOptions.IgnoreCase | RegexOptions.Compiled),
         new Regex(@"VID_0483&PID_374[8BEF]|VID_0483&PID_3752", RegexOptions.IgnoreCase | RegexOptions.Compiled),
         "ST-Link", "interface/stlink.cfg"),
        (new Regex(@"j-?link", RegexOptions.IgnoreCase | RegexOptions.Compiled),
         new Regex(@"VID_1366", RegexOptions.IgnoreCase | RegexOptions.Compiled),
         "J-Link", "interface/jlink.cfg"),
    };

    /// <summary>从设备行(FriendlyName|DeviceId)里识别探针;同种探针只留一条,结果按签名表优先级排序(与枚举顺序无关)。</summary>
    public static IReadOnlyList<ProbeMatch> Parse(IEnumerable<string?> deviceLines)
    {
        var found = new List<(int Priority, ProbeMatch Match)>();
        foreach (var line in deviceLines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            for (var i = 0; i < Signatures.Length; i++)
            {
                var (name, vid, kind, config) = Signatures[i];
                if (found.Any(f => f.Match.Kind == kind)) continue;
                if (name.IsMatch(line) || (vid?.IsMatch(line) ?? false))
                {
                    found.Add((i, new ProbeMatch(kind, config, line.Trim())));
                    break;
                }
            }
        }
        return found.OrderBy(f => f.Priority).Select(f => f.Match).ToList();
    }

    /// <summary>枚举当前 USB 设备并识别探针;系统调用失败一律静默降级为「未找到」。</summary>
    public static async Task<IReadOnlyList<ProbeMatch>> DetectAsync(CancellationToken ct = default)
    {
        var powershell = Path.Combine(
            Environment.SystemDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32"),
            "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(powershell)) powershell = "powershell.exe";

        try
        {
            var psi = new ProcessStartInfo(powershell)
            {
                Arguments = "-NoProfile -NonInteractive -Command \"Get-PnpDevice -PresentOnly | ForEach-Object { '{0}|{1}' -f $_.FriendlyName, $_.DeviceId }\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var process = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null");
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));

            // 两个流都必须抽干：只读其中一个时，另一个的管道缓冲区写满会把子进程卡死，
            // 于是 WaitForExitAsync 永远等不到退出，只能靠超时兜底。
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* 超时清理 */ }
                await DrainAsync(outputTask, errorTask).ConfigureAwait(false);
                return Array.Empty<ProbeMatch>();
            }

            var output = await outputTask.ConfigureAwait(false);
            await errorTask.ConfigureAwait(false); // stderr 不参与识别，读掉即可
            return Parse(output.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries));
        }
        catch
        {
            // 探测属于锦上添花:任何环境问题都不该阻断烧录流程
            return Array.Empty<ProbeMatch>();
        }
    }

    /// <summary>超时路径：把两个读任务收干净，避免留下未观察的异常。</summary>
    private static async Task DrainAsync(params Task<string>[] readers)
    {
        foreach (var reader in readers)
        {
            try { await reader.ConfigureAwait(false); } catch { /* 进程被杀导致的读取中断 */ }
        }
    }
}
