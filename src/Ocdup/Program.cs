using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using Ocdup.Cli;

// `ocdup watch ...`：M-watch 采样 CLI（长时运行会占死 stdio/MCP 通道，所以走子命令
// 而不是 MCP 工具）。不匹配时按 MCP stdio 服务器正常启动。
if (args.Length > 0 && args[0].Equals("watch", StringComparison.OrdinalIgnoreCase))
    return await WatchCli.RunAsync(args.Skip(1).ToArray());

// `ocdup doctor`：环境体检（P1-6）——五项只读检查，不拉后端、不碰目标。
if (args.Length > 0 && args[0].Equals("doctor", StringComparison.OrdinalIgnoreCase))
    return await DoctorCli.RunAsync();

var builder = Host.CreateApplicationBuilder(args);

// stdio 传输:进程的 stdout 属于 MCP 协议,任何控制台日志都会污染协议流。
builder.Logging.ClearProviders();

builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new Implementation { Name = "ocdup", Version = VersionText() };
    })
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
return 0;

static string VersionText()
{
    var v = Assembly.GetExecutingAssembly().GetName().Version;
    return v is null ? "0.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
}
