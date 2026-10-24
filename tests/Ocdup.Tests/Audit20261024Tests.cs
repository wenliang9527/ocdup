using System.Text;
using Ocdup.Backend;
using Ocdup.Cli;
using Ocdup.Config;
using Ocdup.Tools;

namespace Ocdup.Tests;

/// <summary>
/// 第三轮全项目复查（docs/audit-2026-10-24.md）的回归测试：
/// A-1 watch --halt Ctrl+C 窗口 resume 必达 / A-3 read·write_variable 非对齐地址 /
/// A-4 SvdTools.GetLoaded 错误透出 / A-5 SVD 数组寄存器 [%s] 按名可达 /
/// A-8 BackendSession 定向失效 / watch·doctor 退出码家族对齐。
/// </summary>
public sealed class Audit20261024Tests : IDisposable
{
    private readonly string _dir;

    public Audit20261024Tests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ocdup-audit-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* 临时目录尽力清理 */ }
    }

    private OcdConfig ConfigWithSvd(string? svdPath = null)
    {
        var json = svdPath is null ? "{}" : $$"""{ "svd_path": {{JsonEncode(svdPath)}} }""";
        var path = Path.Combine(_dir, "ocd.json");
        File.WriteAllText(path, json);
        return OcdConfig.LoadFrom(path);
    }

    private static string JsonEncode(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    // ── A-1：watch --halt 的 Ctrl+C 窗口，resume 仍必须发出去（收尾不用已取消的 token）──

    [Fact]
    public async Task WatchSample_CoreSendsResumeEvenWhenCancelledMidRead()
    {
        var commands = new List<string>();
        var cts = new CancellationTokenSource();

        Task<string> Execute(OcdConfig cfg, string command, bool startIfNeeded, CancellationToken ct, string op)
        {
            lock (commands) commands.Add(command);
            // 模拟真实 BackendSession.ExecuteAsync：闸等待撞上已取消的 token 直接抛 OCE。
            // 旧实现把采样 ct 传给收尾的 resume → resume 永远发不出去（A-1 的病灶）。
            if (ct.IsCancellationRequested)
                throw new OperationCanceledException();
            if (command.StartsWith("mdw", StringComparison.Ordinal))
            {
                cts.Cancel(); // Ctrl+C 恰落在 halt 之后、读取途中
                throw new OperationCanceledException();
            }
            return Task.FromResult("0x20000000: 44332211");
        }

        var vars = new List<ElfSymbol> { new("gCounter", 0x20000000, 4) };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => WatchCli.SampleCoreAsync(Execute, ConfigWithSvd(), vars, halt: true, cts.Token));

        lock (commands)
            Assert.Equal(["halt 200", "mdw 0x20000000 1", "resume"], commands);
    }

    [Fact]
    public async Task WatchSample_WithoutHalt_DoesNotTouchExecCommands()
    {
        var commands = new List<string>();
        var vars = new List<ElfSymbol> { new("gCounter", 0x20000000, 4) };
        var values = await WatchCli.SampleCoreAsync(
            (cfg, cmd, start, ct, op) => { lock (commands) commands.Add(cmd); return Task.FromResult("0x20000000: 44332211"); },
            ConfigWithSvd(), vars, halt: false, CancellationToken.None);

        Assert.Equal([0x44332211u], values);
        lock (commands)
            Assert.Equal(["mdw 0x20000000 1"], commands); // 无 halt：只有读取，没有 halt/resume
    }

    // ── A-6：watch 退出码对齐 burn/verify/rtt 家族（0/1/2/3：usage=2、backend=3）──

    [Fact]
    public void WatchExitCodes_AlignWithCliFamily()
    {
        Assert.Equal(2, WatchCli.ExitUsage);
        Assert.Equal(3, WatchCli.ExitBackend);
    }

    // ── A-3：read/write_variable 的非对齐地址 ──

    [Theory]
    [InlineData(0x20000000, 4, 0x20000000, 1)] // 对齐 u32：一字
    [InlineData(0x20000001, 1, 0x20000000, 1)] // 非对齐 u8：仍一字
    [InlineData(0x20000003, 2, 0x20000000, 2)] // 骑界 u16：两字
    [InlineData(0x20000002, 4, 0x20000000, 2)] // 骑界 u32：两字
    [InlineData(0x20000001, 7, 0x20000000, 2)] // 大对象跨三字界：两字够
    public void ReadPlan_AlignsDownAndCoversWholeVariable(long address, long size, long expectedAddr, int expectedWords)
    {
        var (aligned, words) = SymbolTools.ReadPlan(address, size);
        Assert.Equal(expectedAddr, aligned);
        Assert.Equal(expectedWords, words);
    }

    [Fact]
    public void ExtractBytes_LittleEndianFromWordSequence()
    {
        // 小端内存 11 22 33 44 | 55 66 77 88 → mdw 字 0x44332211 / 0x88776655
        // words 与「ReadPlan 对齐基址上读回的字序列」一一对应（ExtractBytes 的调用契约）。
        long[] words = [0x44332211, 0x88776655];
        Assert.Equal(new byte[] { 0x11, 0x22, 0x33, 0x44 }, SymbolTools.ExtractBytes(words, 0x20000000, 4));
        Assert.Equal(new byte[] { 0x33, 0x44, 0x55 }, SymbolTools.ExtractBytes(words, 0x20000002, 3));
        // 基址 0x20000004 的读取：words 只有第二个字
        Assert.Equal(new byte[] { 0x55 }, SymbolTools.ExtractBytes([0x88776655], 0x20000004, 1));
        Assert.Equal(new byte[] { 0x88 }, SymbolTools.ExtractBytes([0x88776655], 0x20000007, 1));
    }

    [Fact]
    public void WritePlan_AlignedKeepsSingleCommand()
    {
        Assert.Equal(["mww 0x20000000 0xdeadbeef"], SymbolTools.WritePlan(0x20000000, 4, 0xdeadbeef));
        Assert.Equal(["mwh 0x20000004 0xbeef"], SymbolTools.WritePlan(0x20000004, 2, 0xbeef));
        Assert.Equal(["mwb 0x20000001 0xab"], SymbolTools.WritePlan(0x20000001, 1, 0xab));
    }

    [Fact]
    public void WritePlan_UnalignedFallsBackToByteWrites()
    {
        Assert.Equal(
            ["mwb 0x20000002 0xef", "mwb 0x20000003 0xbe", "mwb 0x20000004 0xad", "mwb 0x20000005 0xde"],
            SymbolTools.WritePlan(0x20000002, 4, 0xdeadbeef));
        Assert.Equal(
            ["mwb 0x20000003 0xef", "mwb 0x20000004 0xbe"],
            SymbolTools.WritePlan(0x20000003, 2, 0xbeef));
    }

    // ── A-4：svd_path 配置但文件损坏/缺失时，GetLoaded 透出真实原因 ──

    [Fact]
    public void GetLoaded_CorruptSvdFile_ReportsParseProblemNotNoSvd()
    {
        var corrupt = Path.Combine(_dir, "corrupt.svd");
        File.WriteAllText(corrupt, "<html>definitely not a CMSIS-SVD device file</html>");
        var loaded = SvdTools.GetLoaded(ConfigWithSvd(corrupt));

        Assert.Null(loaded.Device);
        Assert.NotNull(loaded.Problem);
        Assert.Contains("could not be parsed", loaded.Problem);
    }

    [Fact]
    public void GetLoaded_MissingSvdFile_ReportsPath()
    {
        var missing = Path.Combine(_dir, "nope.svd");
        var loaded = SvdTools.GetLoaded(ConfigWithSvd(missing));

        Assert.Null(loaded.Device);
        Assert.NotNull(loaded.Problem);
        Assert.Contains("does not exist", loaded.Problem);
    }

    [Fact]
    public void GetLoaded_NoSvdPathConfigured_GivesNeutralResult()
    {
        var loaded = SvdTools.GetLoaded(ConfigWithSvd());
        Assert.Null(loaded.Device);
        Assert.Null(loaded.Problem); // 引导文案由调用方出（svd_path 根本没配）
    }

    // ── A-5：SVD <dim> 数组寄存器（名含 [%s]）按名可达 ──

    private const string DimSvd = """
        <?xml version="1.0"?>
        <device>
          <peripherals>
            <peripheral>
              <name>UART</name>
              <baseAddress>0x40011000</baseAddress>
              <registers>
                <register>
                  <name>DR[%s]</name>
                  <addressOffset>0x04</addressOffset>
                  <size>32</size>
                  <access>read-write</access>
                </register>
                <register>
                  <name>SR</name>
                  <addressOffset>0x00</addressOffset>
                  <size>32</size>
                  <access>read-only</access>
                </register>
              </registers>
            </peripheral>
          </peripherals>
        </device>
        """;

    [Fact]
    public void DimArrayRegister_IsAddressableByCleanName()
    {
        var dev = SvdFile.ParseXml(DimSvd);
        var uart = SvdFile.FindPeripheral(dev, "UART");
        Assert.NotNull(uart);

        // 名单里出现的是剥掉占位符的净名（不再是 DR[%s]）
        Assert.Contains("DR", SvdFile.RegisterNames(uart));
        Assert.DoesNotContain("[%s]", string.Join(",", SvdFile.RegisterNames(uart)));

        var dr = SvdFile.FindRegister(uart, "DR");
        Assert.NotNull(dr);
        Assert.Equal(0x40011004, SvdFile.AbsoluteAddress(uart, dr));
    }

    [Fact]
    public void StripDimPlaceholder_RemovesAllOccurrences()
    {
        Assert.Equal("DR", SvdFile.StripDimPlaceholder("DR[%s]"));
        Assert.Equal("PWM.CCR", SvdFile.StripDimPlaceholder("PWM.CCR[%s]"));
        Assert.Equal("SR", SvdFile.StripDimPlaceholder("SR")); // 无占位符：原样
    }

    // ── A-8：BackendSession 定向失效——只丢失败的那条连接 ──

    [Fact]
    public async Task DropIfCurrent_DropsOnlyTheFailedClient()
    {
        var server = new AuditFakeTclServer();
        var failed = await TclRpcClient.ConnectAsync(
            "127.0.0.1", server.Port, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5));
        var replacement = await TclRpcClient.ConnectAsync(
            "127.0.0.1", server.Port, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5));

        TclRpcClient? current = failed;

        // 别的调用刚换上的新连接：不是失败的那条 → 不动
        Assert.False(BackendSession.DropIfCurrent(ref current, replacement));
        Assert.Same(failed, current);
        replacement.Dispose();

        // 失败的那条仍在位：清空并 Dispose
        Assert.True(BackendSession.DropIfCurrent(ref current, failed));
        Assert.Null(current);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => failed.CommandAsync("version"));

        server.Dispose();
    }

    /// <summary>极简假 TCL-RPC 服务器：只负责让 TclRpcClient.ConnectAsync 能握手成功。</summary>
    private sealed class AuditFakeTclServer : IDisposable
    {
        private readonly System.Net.Sockets.TcpListener _listener;

        public AuditFakeTclServer()
        {
            _listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((System.Net.IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(AcceptLoopAsync);
        }

        public int Port { get; }

        private async Task AcceptLoopAsync()
        {
            try
            {
                while (true)
                {
                    var client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
                    _ = Task.Run(async () =>
                    {
                        using var _ = client;
                        var stream = client.GetStream();
                        var buffer = new byte[4096];
                        while (await stream.ReadAsync(buffer).ConfigureAwait(false) > 0)
                        {
                            var response = Encoding.UTF8.GetBytes("ok" + (char)TclRpcClient.Delimiter);
                            await stream.WriteAsync(response).ConfigureAwait(false);
                        }
                    });
                }
            }
            catch
            {
                // listener 停了
            }
        }

        public void Dispose() => _listener.Stop();
    }
}
