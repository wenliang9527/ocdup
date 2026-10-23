using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using Ocdup.Cli;

const string HelpText = """
    ocdup - OpenOCD facade as an MCP server (.NET 8)

    usage:
      ocdup                    run as an MCP stdio server (what clients register)
      ocdup --version          print the version and exit
      ocdup --help             this text

    subcommands (long-running CLIs - they do not occupy the MCP channel):
      ocdup watch --name gCounter [--interval 250] [--format csv] [--halt]
      ocdup rtt [--address 0x20000400] [--channel 0] [--info] [--lines 1000]
      ocdup burn --file build/fw.bin --address 0x08000000
      ocdup verify --file build/fw.bin --address 0x08000000
      ocdup doctor

    each subcommand has its own --help; config/backend come from ocd.json
    (OCD_CONFIG) exactly like the MCP tools.
    """;

// `ocdup watch ...`：M-watch 采样 CLI（长时运行会占死 stdio/MCP 通道，所以走子命令
// 而不是 MCP 工具）。不匹配时按 MCP stdio 服务器正常启动。
if (args.Length > 0 && args[0].Equals("watch", StringComparison.OrdinalIgnoreCase))
    return await WatchCli.RunAsync(args.Skip(1).ToArray());

// `ocdup rtt ...`：SEGGER RTT 实时读取 CLI（v0.4.19）——同理走子命令：长时连续流
// 不能占死 MCP 串行通道（C1），原始字节重定向到文件/管道才是 RTT 的正常用法。
if (args.Length > 0 && args[0].Equals("rtt", StringComparison.OrdinalIgnoreCase))
    return await RttCli.RunAsync(args.Skip(1).ToArray());

// `ocdup burn ...`：量产烧录 CLI（v0.4.20）——产线/CI 按 exit code 分流，编排复用
// flash_write 的核心（闸与读回判据单一真源）；刻意不接取消（写相中断 = 半截固件）。
if (args.Length > 0 && args[0].Equals("burn", StringComparison.OrdinalIgnoreCase))
    return await BurnCli.RunAsync(args.Skip(1).ToArray());

// `ocdup verify ...`：只读比对镜像与目标 flash 的 CLI（v0.4.26）——量产金板抽检/
// 烧后自检，判据复用 verify_image 的核心，exit code 与 burn 同族。
if (args.Length > 0 && args[0].Equals("verify", StringComparison.OrdinalIgnoreCase))
    return await VerifyCli.RunAsync(args.Skip(1).ToArray());

// `ocdup doctor`：环境体检（P1-6）——五项只读检查，不拉后端、不碰目标。
if (args.Length > 0 && args[0].Equals("doctor", StringComparison.OrdinalIgnoreCase))
    return await DoctorCli.RunAsync(args.Skip(1).ToArray());

// `ocdup --version` / `-V` / `version`：版本自报——治具脚本、doctor 排障、issue 反馈都用它。
// MCP 客户端经 stdio 启动时不会带任何参数，这两个入口只对人工/脚本可见。
if (args.Length > 0 && args[0] is "--version" or "-V" or "version")
{
    Console.WriteLine($"ocdup {VersionText()}");
    return 0;
}

// `ocdup --help` / `-h`：子命令总览（不带参数启动 = MCP stdio 服务器，会挂起等协议流）。
if (args.Length > 0 && args[0] is "--help" or "-h")
{
    Console.Write(HelpText);
    return 0;
}

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
