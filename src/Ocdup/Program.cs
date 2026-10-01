using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;

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

static string VersionText()
{
    var v = Assembly.GetExecutingAssembly().GetName().Version;
    return v is null ? "0.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
}
