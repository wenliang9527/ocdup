# Ocdup 设计计划方案

| 项目 | 内容 |
|---|---|
| 文档版本 | v0.4.23 |
| 日期 | 2026-10-09 |
| 状态 | **阶段一完成**：控制层 + 烧录 + 读内存 + 执行控制 + 逃生舱 + 运行时权限 + 符号层 L1/L2 + target 注册表 + watch 采样 CLI 均已实现并验证（23 个 MCP 工具 + 4 个 CLI 子命令：watch 采样 / doctor 环境体检 / rtt 实时读取 / burn 量产烧录，S4 真机通过）；v0.4.12–v0.4.16 落地评审定案的安全/正确性加固（run_command 内容分类、flash_write 独立读回权威判据、断链只读自动重放 + 空闲心跳、执行期 progress notification）；后端二进制升级为自编译 atopenocd-py32（AT32 + PY32 双烧录支持，两芯片烧录均真机闭环）；SEGGER RTT 经 `ocdup rtt` CLI 落地（v0.4.19，fork 命令面实证 + 本机 TCP 数据面）；量产烧录经 `ocdup burn` CLI 落地（v0.4.20，与 flash_write 同一编排核心）；符号层 L2（SVD 按名外设寄存器 + 位域解码）v0.4.21 落地；断点/观察点/L3 后置 |
| 读者 | 项目负责人（硬件/嵌入式）、后续实现者（Agent 或工程师） |
| 上游参考 | [microhenrio/openocd-mcp](https://github.com/microhenrio/openocd-mcp) v0.4.5 架构（已逐文件审查） |

---

## 变更记录

| 版本 | 变更 |
|---|---|
| v0.4.24 | **CLI 复查毛边两处（全项目复查 P1 项 ①②）**：① `ocdup rtt` 控制块扫描超时不再静默开流——自拉后端扫 2s 没等到 `Control block found` 结论时，补一句 stderr 提示（流可能一直安静 + 提示查固件是否真用 RTT / 查 map 文件的 `_SEGGER_RTT` 精确地址），让 `--seconds`/`--lines` 的兜底语义有明确解释，不再「对着空 TCP 干等到 Ctrl+C」。② burn 的 **exit 3 端到端钉死**：此前链路 18 只覆盖 0/1/2，后端/通道故障路径只有单测映射——e2e 第 20 条链路：独立死端口配置（无 OCD_BIN）跑 `ocdup burn`，拉后端必败 → `ERROR: no openocd executable found` 进 stderr + **exit 3**。rtt 文案改动无行为面（31 例既有测试照绿）；C# 全套 299 全绿，e2e 20 条链路 exit 0。csproj 0.4.15→0.4.16 |
| v0.4.23 | **SDK 升级核查 + HANDOFF 刷新（P1-7 实测收口，docs-only）**：① `dotnet package search`（含 `--prerelease`）实测：ModelContextProtocol 全家最新版就是 **2.2.0**——v0.4.17 钉住的版本即当前最新态，「升级实操」无对象，P1-7 以实测收口（升级回归要点维持第 17 节第 5 条，出新版时照单执行）；② Q8 多后端注册表**实施方案定案**（见第 16 节：覆盖式 backends 配置 + 会话注册表 + 工具可选 backend 参数 + 分后端权限；验收标准与工程量在案，实施待排期）；③ HANDOFF 刷新至本轮六项收口后的实际状态。C# 零改动，csproj 不 bump |
| v0.4.22 | **recipes/playbooks：四份实战手册（评审 P2 收尾项）**：`docs/recipes/`——① 新板首次烧录闭环（doctor 预检 → connect 认芯片/失配切 set_target → flash_info 确认布局 → flash_write 三段式结论 → 运行态读回验证，附四个常见坑）；② 救板（通道排障 → 深睡板 connect_assert_srst 姿势 → halt 核心寄存器现场 → 整片擦除重烧，按「每次只放宽一点」排序）；③ 观测运行中的固件（watch 采样 / rtt 日志流 / halt 现场三入口怎么选 + MCP 与 CLI 共探针的独占性提醒）；④ 量产治具骨架（`ocdup burn` exit code 0/1/2/3 分流的 bash 脚本 + 治具 ocd.json 钉死清单 + 金板自检）。README 加「常用配方」索引。C# 零改动，csproj 不 bump（v0.3.19 先例） |
| v0.4.21 | **符号层 L2：SVD 按名外设寄存器（M-sym 后置项 → TDD 落地）**：① 新增 3 个工具（总数 20→**23**）——`load_svd`（PC 侧解析 + 外设/寄存器清单，peripheral 参数可只列一个外设）、`read_peripheral_register`（`"RCC.CR"` 按名读：地址 = 外设基址 + 偏移，读回带**位域解码 + reset 比对**）、`write_peripheral_register`（按名写：值按寄存器宽度掩码；走 CheckMemoryWrite 闸，SVD access 为 read-only 的寄存器**即使权限放行也语义拒写**）。② 自研 `Backend/SvdFile` 解析（System.Xml.Linq，零新依赖，C7 纪律）：三种位域写法（bitOffset/bitWidth、lsb/msb、bitRange）、cluster 递归展平（偏移累加 + 名字路径前缀 `PWM.CCR`）、size/access/resetValue 缺省兜底、命名空间不敏感。③ 实测踩坑入库：SVD 的 scaledNonNegativeInteger **无前缀 = 十进制**（lsb "24"、size "32" 是十进制，先按 hex 解就读成 0x24/0x32）、`0x` 前缀钉死十六进制（"0x40021000" 剥前缀后不能落 decimal 分支）、写方向助记符是 mw*（mwb/mwh/mww）与读方向 md* 是两套——三处都是测试先红的真回归。④ 缓存按「完整路径 + mtime」，`svd_path` 新配置键作默认镜像（elf_path 同款约定）；未命中名给「Did you mean」建议（C3）。⑤ 数据面复用 read/write_memory 的 md*/mw* 路径（op 标 read_memory/write_memory，断链重放语义随 op 一致）。⑥ 测试：SvdFileTests +15（解析 8 / 解析与建议 2 / 位域与解码 3 / 按宽度选 monitor 3 / 写掩码与 access 2 等理论展开），C# 全套 284→**299 全绿**；e2e 第 19 条链路：load → 大小写不敏感按名读（decode + reset diff）→ 簇寄存器（mdh 选型断言）→ mww 写 → read-only 拒写 → 未知名建议，19 条链路 exit 0。csproj 0.4.14→0.4.15。**真机验收待插板**：真实厂商 SVD（AT32/ST 全量文件）的解析健壮性 |
| v0.4.20 | **`ocdup burn`：量产烧录 CLI（评审 P2 → TDD 落地）**：① 形态：产线治具/CI 按 exit code 分流——0=写入且读回一致（`--no-verify` 显式选择时 unverified 也 0）/ 1=读回不符或写失败（VERIFY FAILED / WRITE FAILED）/ 2=用法·配置·权限（BLOCKED）/ 3=后端或通道故障；报告文本与 flash_write 工具逐字一致。② **单一真源抽取**：flash_write 的编排（路径解析 → C2 闸 → `program` 不带 verify → 独立 `verify_image` 读回 → `ClassifyFlashWrite` 三段式结论 → `ShouldResetRun` 门控 reset）整体抽成 `FlashTools.FlashWriteCoreAsync`（`CoreOutcome`：Ok/Failure/Report/Verdict），MCP 工具变薄壳（进度泵照旧包裹核心），CLI 复用同一核心——闸与判据零复制（防两处漂移）；行为由既有 FlashToolsTests + e2e 链路 4 钉死不变。③ 量产语义两处刻意为之：Ctrl+C **不**接取消（写相中断 = 半截固件，要中止请拔探针）；.bin 缺显式地址在**解析期**就拒（失败发生在碰后端之前）。④ 测试：BurnCliTests +14（选项解析 8 含 .bin 早拒 / 退出码映射 6），C# 全套 270→**284 全绿**；e2e 第 18 条链路：verified → exit 0、arm 假后端回读失配 → VERIFY FAILED + exit 1、白名单外 → BLOCKED + exit 2，18 条链路 exit 0。csproj 0.4.13→0.4.14 |
| v0.4.19 | **`ocdup rtt`：SEGGER RTT 实时读取 CLI（后置项 → TDD 落地）**：① 命令面实证先行：本机两份 atopenocd（旧版与 py32 自编译）的 rtt 命令面都不完整——不带 target cfg 时 `help rtt` 只列 `rtt server`；带 target cfg 复测 + ArteryTek fork 源码（src/rtt/tcl.c）核对定案：fork 是**老式 target 命令表**——`rtt setup <addr> <size> <ID>`（valid any time）/ `rtt start` / `rtt stop` / `rtt channels` / `rtt channellist` / `rtt polling_interval` + 全局 `rtt server start/stop <port> <channel>`；**没有**上游 0.11 的 `rtt terminal`，且 setup/start 两步分离（上游 setup 自带 start）、`rtt start` 找不到控制块时只发 `rtt: No control block found` 日志**不返回 TCL 错**。② 形态定 CLI 而非 MCP 工具（同 watch / C1 理由）：长时连续流不该占死 stdio 串行通道，stdout 重定向到文件/管道才是 RTT 日志的正常用法。③ 数据面 = `rtt server start <port>` + 本机 TcpClient 直连，**原始字节直通 stdout**（不重新编码，二进制协议友好）；进度与结论走 stderr，重定向互不污染。④ 修法细节：游标基线取在 `rtt start` 之前、扫 openocd 捕获确认 `Control block found at 0x…`（fork 找不到不报错，不扫就会对着永不产出的 TCP 端口干等）；外部监听无捕获时如实标注 control block not verified；收尾序列 `rtt server stop` → `rtt stop` best-effort（外部实例也清理干净）；自拉后端随进程退出被 Job 收割（与 watch 同）。⑤ 选项：--address（默认 0x20000000，精确值查 map 文件的 _SEGGER_RTT）/--size（搜索区字节数，默认 4096）/--id（≤15 字符）/--channel/--port（默认 19221）/--info（只报通道清单不开流）/--lines/--bytes/--seconds。⑥ 测试：RttCliTests +31（解析 12 / 命令构造 4 / setup 判定 5 / 控制块扫描 7 / 限流 3），C# 全套 239→**270 全绿**；e2e 第 17 条链路：假后端起迷你 RTT TCP 数据面，CLI 子进程 `--lines 2` 收满即停（payload 原样到达 stdout + 命令序列五步断言）+ `--info` 通道清单，17 条链路 exit 0。csproj 0.4.12→0.4.13。**待真机验收**：板上的控制块搜索与数据流（本机无硬件；命令面与协议行为已实证） |
| v0.4.18 | **复查修正三处（v0.4.12–v0.4.16 加固落地后的复查结论）**：① **文档数字纠错**：design-plan 状态头与 HANDOFF「20 个 MCP 工具 + **3 个** CLI 子命令」实为 **2 个**——`Program.cs` 只分发 `watch` 与 `doctor`，v0.4.17 回填时多数了一个（原 19 工具 + 1 CLI → 20 工具 + 2 CLI）；v0.4.17 changelog 行同步改正。② **`status` 通道状态行顺序修正**：原先先发 `version`/`targets` 再读 `ChannelState`——两次成功查询把「最近活动」水位刷新成当下，idle 恒显示 0s，P1-5 的「空闲时长」设计意图落空；改为查询**之前**读状态（注释钉死顺序要求，防回归），tool description 补「control channel 状态」一句。③ **进度泵冷启动修正（P1-4 边界）**：`FlashProgress.Start` 原要求「调用这一刻在自拉后端」，而冷启动的 `flash_write`/`dump_memory` 是在 `ExecuteAsync` 内部才拉起 openocd——首次烧录全程无进度；守卫下沉为泵内每拍自查 `CapturesProcessOutput`（`Start` 只留 server + progressToken 两重），既覆盖冷启动（后端就绪后进度自动恢复），也保守挡住外部监听的陈旧环形缓冲。④ 测试：ProgressPumpTests +1（无自拉后端时跑满两拍零通知，行为级钉死），C# 全套 238→**239 全绿**，e2e 16 条链路 exit 0。**待真机验收**：冷启动烧录（未先 connect、由 flash_write 内部拉起后端）时进度是否如期恢复，需插板实跑——本机无硬件，只覆盖了「无自拉后端不报」这一半。csproj 0.4.11→0.4.12 |
| v0.4.17 | **文档全面对齐（C# 零改动，csproj 不 bump）**：把 v0.4.5–v0.4.16 六个版本滚积的滞后描述一次性回填——① README：工具一览 19→**20 个**（补 disconnect 行，v0.4.6 时漏更）、项目状态表补「安全与正确性加固 v0.4.12–v0.4.16」与「doctor CLI」两行、S1 行测试数 173→238、S5 行补「换机第一步 doctor」；② design-plan：状态头（19 工具 + 1 CLI → 20 工具 + 2 CLI，补安全定案一句）、HANDOFF「当前状态」块 v0.4.4→v0.4.16（工具/CLI/加固/测试数/doctor 全刷新）、单元测试统计行 173→238 并补五项新测试域、e2e 行补 16 条链路与内容分类/断链重放、下一步清单第 3 条补 doctor、**新增第 5 条 P1-7 维护节奏记录**（SDK 钉 2.2.0 的升级回归要点 + Job Object Windows-only 的 Linux 记录项——P1-7 至此以文档形式收口）；③ zcode 插件 SKILL：工具总数 19→20（注明 disconnect）。评审建议顺序的 P0-1 / P0-2 / P1-5 / P1-6 / P1-4 五项 + P1-7 记录项全部收口 |
| v0.4.16 | **P1-4：烧录/擦除/dump 执行期间的 MCP progress notification（评审定案 → TDD 落地）**：① 现状澄清：launcher 的 PumpAsync 早已把 openocd 每行 stdout/stderr 以 log 方向镜像进 traffic（面板进度条吃的就是那条路），真正缺的是到**调用方客户端**的标准事件通道。② 修法 = 差分泵：launcher 环形缓冲加累计行数水位 `_totalLines` + `TakeNewLines(ref cursor)`（`SliceNewLines` 纯函数：落后超圈只回可见尾部，游标单调推进）→ 新增 `Tools/FlashProgress`（`Start` 三重守卫：有 server + 调用方 `_meta.progressToken` + ocdup 自拉捕获；`RunAsync` 750ms 一拍把增量行以 `notifications/progress` 推回，Progress 计数 + 行文本截断 200 字符）→ `flash_write` / `flash_erase_sector` / `dump_memory` 注入 `RequestContext<CallToolRequestParams>`，泵生命周期 try/finally 显式收口（防泵泄漏与二次烧录双泵重复推送）。③ 边界如实：不带 progressToken 的客户端零开销零行为（SDK 不发通知），进度旁路任何失败静默收工绝不影响主操作结论；带 token 的 progress-capable 客户端在烧录期间逐行实时收到通知。④ SDK 2.2.0 实测细节：`Params.Meta` 是裸 JsonObject（非类型化 metadata），progressToken 按 MCP 规范数字/字符串双形态手工解析成 `ProgressToken(long|String)`；`SendNotificationAsync<T>` 走 `McpJsonUtilities.DefaultOptions`。⑤ 测试：ProgressPumpTests +7（差分切片 4：fresh/incremental/驱逐超圈/无新行 + 截断 2 + Start 守卫 1），C# 全套 231→**238 全绿**，e2e 16 条链路无回归 exit 0。csproj 0.4.10→0.4.11 |
| v0.4.15 | **P1-6：`ocdup doctor` 环境体检子命令（S5「换机 10 分钟」抓手，评审定案 → TDD 落地）**：① 五项只读检查拼装现成积木——`ocd.json`（Load + `Sources["ocd_json_error"]` 解析错误 + target_cfg 缺失）/ `openocd binary`（`FindBinary`（OCD_BIN→PATH）+ 实跑 `--version` 取版本行，5s 预算）/ `openocd scripts`（`FindScriptsDir` + target/ 子目录存在性）/ `debug probe`（`ProbeDetector.DetectAsync` 四家族签名，列出 Kind→cfg）/ `backend port`（`ProbePortAsync`；有监听则只读 version 查询验证 TCL-RPC 通道，无监听为 WARN 冷启动正常）。② 每项 [OK]/[WARN]/[FAIL] + 「-> how to fix:」修复指引（6.3 约定）；退出码有 FAIL 即 1，可直接当 CI 预检。③ 全程只读：不拉起 openocd、不碰目标。④ 渲染/退出码为纯函数（`FormatReport`/`ExitCode`，ASCII 图标 CI 日志零乱码），DoctorCliTests +3；检查本体是系统调用，按仓库分工实机验收——本机两跑：无 OCD_BIN/OCD_SCRIPTS 的裸 shell 正确报 2 FAIL + exit 1（FAIL 路径与指引实锤），带真实 atopenocd 环境变量后 4 ok + 1 WARN（冷启动）+ exit 0，`--version` 回真实版本行。C# 全套 228→**231 全绿**。csproj 0.4.9→0.4.10 |
| v0.4.14 | **P1-5 健壮性：断链只读自动重放 + 空闲心跳（评审定案 → TDD 落地）**：① 病根：命令已发出后通道断链一律抛「call connect and retry」——只读命令（read_memory/verify_image/flash_info…）重放是安全的，白白多一轮模型往返；openocd 被外部杀掉后通道半死，要等下一次真命令（最长等满 command_timeout）才报错。② 修法一（重放）：`ExecuteAsync` 断链分支按 op 白名单自动 `Invalidate → 重连 → 重放一次`（`ReplaySafeOps`：status/connect/flash_info/identify_chip/read_memory/verify_image/dump_memory/read_variable）；`flash_write`/`run_command`/halt/resume/reset/write_*/flash_erase_sector 不在列，错误文案改为如实说明「命令可能已生效，NOT replayed」；重放再失败合并两次原因抛出。③ 修法二（心跳）：后台 Timer 15s 一拍 + 空闲 ≥30s 才发 `version` 探测；新增 `_channelGate` 通道串行闸让心跳与业务命令互斥（顺带收敛并发 ExecuteAsync 理论交叉的旧隐患）；探测失败只记 traffic SYS + Invalidate，下次调用快速重连；成功探测不进流量日志防刷屏。④ `status` 顺带报告通道状态行（connected, idle Ns / not connected + 自动重连说明）。⑤ 测试：BackendSessionTests +19（重放白名单 8 正 9 反 + ShouldProbe 阈值 + 通道文案），C# 全套 209→**228 全绿**；e2e 第 16 条链路：假后端 arm 后对指定读命令直接断开连接（serve 线程 return 不回包 → ocdup 侧 IOException("connection closed by OpenOCD")），read_memory 自动重连重放成功返回数据（假后端命令流水可见两次 mdw），exit 0。csproj 0.4.8→0.4.9 |
| v0.4.13 | **P0-2 正确性修复：flash_write 以独立读回校验为权威判据（评审定案 → TDD 落地）**：① 病根：烧录成败靠 `LooksLikeFailure` 文本嗅探（匹配 "Error:"/"failed" 子串）——两个方向都不可靠：PY32 驱动写相吐虚假 `Failed to write memory`（AHB 错误，操作实际已生效，README 实测记录）→ 误判失败并跳过 reset run；不含这两个词的失败（如 program 静默退出）→ 误判成功并 `reset run` 把错固件跑起来。② 修法 = 判据与写相分离：`program <file> [<addr>]` 不再带 verify → 独立 `verify_image <file> [<addr>]` 读回，`InterpretVerifyOutput` 解析（正向 "verified N bytes" 判据）→ 新增纯函数 `ClassifyFlashWrite` 三段式结论：`programmed + verified`（reset run 执行）/ `programmed but verify FAILED`（写声称成功但读回不符——最危险，单列醒目）/ `write failed`（写步骤已报错且读回未确认）；program 错误文本降级为辅助信息，只用来区分两种失败形态。③ reset 门控 = 新增纯函数 `ShouldResetRun`：读回通过才放行；`verify=false` 时结论 `unverified`、门控退回文本嗅探（此时没有更好的判据）；校验通道失败按存疑判失败（保守），reset 不会执行。④ 成本：1MB flash 多一次读回约几秒，`verify=false` 可关（tool description 写明）。⑤ 测试：FlashToolsTests +5（PY32 虚假错误回归用例在列：program 输出吓人 + 读回通过 → ProgrammedVerified）；C# 全套 204→**209 全绿**；e2e 第 4 条链路改为 program + 独立 verify_image + reset run，exit 0。csproj 0.4.7→0.4.8 |
| v0.4.12 | **P0-1 安全修复：run_command 绕过权限矩阵封堵（评审定案 → TDD 落地）**：① 病根：run_command 只过 CheckRawCommand（read_only + allow_raw_command 两个布尔，不看命令内容），本机 read_only:false 形态下 raw TCL 可绕过整个权限矩阵——program/flash write_image 绕开 flash_allowed_paths/flash_max_bytes、flash erase_sector 绕开 allow_flash_erase=false、mww/mwh/mwb 绕开 allow_memory_write、reset/halt 绕开 allow_exec、dump_image 绕开 dump_allowed_paths/dump_max_bytes、shutdown 直接杀后端；';' 是单行内合法分隔符还能走私（`mdb 0x0 1; shutdown`）。② 修法 = 内容分类与专用工具同权：新增 `Config/RawCommandPolicy` 纯函数（Segment 按 ';' 切段 → Classify 逐段分类 → Evaluate 任一段命中即拦）；能映射既有闸的走同一条闸（flash erase_sector/erase_address→CheckFlashErase、flash fill*/protect→新增 CheckFlashOp=allow_flash、mww/mwh/mwb→CheckMemoryWrite、reset/halt/resume/step→CheckExec），带路径/体积维度或动后端的无法可靠校验直接 BLOCKED 并重定向专用工具（program/load_image/flash write_image→flash_write、dump_image→dump_memory、shutdown/exit→disconnect）。已知陷阱入测：flash erase_check 以 "flash erase" 开头但只读不拦；'#' 注释段跳过。③ 默认收紧：allow_raw_command 默认 true→**false**（逃生舱 = opt-in 显式授权；分类清单之外的命令仍透传，通道本身的闸才是主边界）；RuntimePatchableKeys 不动——allow_set_permissions 是总钥匙，运行时可改性是既有语义。本机 ocd.json / e2e 配置显式 true 不变行为。④ run_command tool description 改写（模型靠它学习新行为）；csproj 0.4.6→0.4.7（面板-only 的 v0.4.7–v0.4.11 未 bump C# 版本，此为 C# 版本号非文档版本号）。⑤ 测试：新增 RawCommandPolicyTests 27 断言（RED：22×CS0103 编译失败 → GREEN：27/27）；OcdConfigTests 补默认值断言；C# 全套 177→204 全绿；e2e 补第 15 条链路（mdb 透传 + program BLOCKED 重定向），exit 0 |
| v0.4.11 | **面板 v1.3.9:轮询链自愈(纯 Client,C# 零改动;用户实测「后端卡停在已断开不动」)**:① 现场取证:后端其实在跑(openocd pid 1888,16:06:19 起,日志 16:06:20 `started` 之后无任何断开,status 复核 target running + 双 bank 1MB),Host 侧 ring 491 条全收齐(实测 `?since=343` 返回含 `started (pid 1888)` 的 148 条),但客户端视图冻在 343 条/16:06:20——**轮询链楔死**。根因:单条 setTimeout 链 + 无超时的 fetch = 单点故障——一个挂起的请求让 `inFlightRef` 永远 true,之后每一拍都被 `if (inFlightRef.current)` 挡在 fetch 之外,**连 visibilitychange 的立即刷新也救不回**(onVis→tick→被 inFlight 挡→schedule→再被挡,无限空转),用户主动切标签都无效,只能重挂载。② 修法一:`fetchWithTimeout(url, opts, ms)`(AbortController + setTimeout abort + `.finally` 清定时器),轮询走 10s 超时——挂起即 abort 落回 catch/finally,下一拍自动恢复;`inFlightRef` 不再可能被永久占住。③ 修法二:kick 机制——轮询 effect 经 `kickRef` 暴露 `kick()`(清掉排队中的定时器直接 tick,tick 自身尊重 hidden/paused/inFlight),三个入口触发:切回本标签(tabVisible false→true 的 effect)、窗口重新聚焦(`window` focus 事件)、visibilitychange;后台这些拍可能整段没收到(挂起/节流),回前台不该还看旧状态。④ 测试:T30 覆盖 fetchWithTimeout 两条路径(正常路径透传 AbortSignal;挂起的 fetch 在 ms 后 abort 抛出——用真 AbortController + 永不 settle 的假 fetch + 30ms 真实定时器)。测试:面板四模式 207→210(client 148→151),C# 177 与 e2e 不变(未碰)。教训入库:互斥位(inFlight)被挂起操作占住时,所有恢复入口都会被它挡死——异步链必须配超时,且恢复入口要能绕过互斥位 |
| v0.4.10 | **面板 v1.3.8:失配胶囊一键切换 + 「连上就自己断开」病根确诊(C# 零改动;用户截图问「怎么连接上后自己断开了?」)**:① 日志取证:三次 connect(14:43:45/14:44:01/14:45:10)全是同一条死法——openocd 起得来、SWD DPIDR 0x2ba01477、CPUID 0x410fc241(Cortex-M4 r0p1,所以面板显示「连上了」),但 DEV_ID@0xE0042000 读回 **0x00000000** → at32f4xx 驱动 `flash reg address: 0x0, usd addr: 0x0` → `read at32 flash size failed, 0k flash` → **断言 `num_pages > 0` 失败(at32f4xx.c:958),openocd 进程直接 abort** → 控制通道被迫关闭 → ocdup 只能报 nothing is listening → 面板显示「已断开」。不是谁点了断开;核过代码:面板轮询只 GET 日志尾巴,POST 仅在点击时发,无自动重连。② 病根:AT32F403AVGT7 开发板插着,ocd.json 的 target_cfg 却钉在 423 钥匙板专用 cfg(连 `srst_only srst_nogate connect_assert_srst` 复位姿势都是按 423 调的)——「DEV_ID 读 0 + flash 尺寸读 0 而 CPUID 正常」正是 cfg 与板子不配的 signature;.1 归档(旧二进制时代)零断言记录,排除二进制回归。③ 处置:`set_target AT32F403A`(→ `target/at32f403axG.cfg`,不带 srst 姿势)后 DEV_ID 读回 0x70050344,flash 双 bank 512K+512K=1MB 全通——「换板子不用改 ocd.json,identify 认出来 + set_target 切过去」的注册表设计兑现。④ 面板升级:v1.3.0 的失配胶囊只会把 `mcp__ocdup__set_target X` 复制到剪贴板(那时面板不能调 MCP);v1.3.8 起点击 → 新增 `POST /api/ocdup-target` → Host 经 `ctx.tools.execute` 直调 `mcp__ocdup__set_target`(切换=用新 cfg 重启 ocdup 自拉的 openocd)。成功后父组件顺手 connect 一次——否则 set_target 重启后端后「后端」卡显示已连接、链路行却还是旧报告,再点「后端」反而变成断开(toggle 陷阱);烧录进行中(flashCall open)要 3s 内二次确认;只读会话被拦 BLOCKED 时如实显示失败且不自动重连。⑤ harness 限制记录:子组件的 hook 作用域每次渲染都是新的(flatten 对每个函数组件 push 新 scope,只有主组件跨重渲染保留),胶囊的状态翻词不能靠 rerender 断言,T29 因此聚焦 fetch 契约(路由/body/成功后链式 connect/失败不链式)。测试:面板四模式 199→207(api 29→34:target 路由五断言;client 145→148:T29 三断言),C# 177 与 e2e 不变(未碰) |
| v0.4.9 | **面板 v1.3.7:空态也要能控后端 + 两个自家 bug(用户截图问「这个界面没有控制后端的开关嘛?」,C# 零改动)**:① 用户截图:重启后没流量,链路行整行不渲染——「后端」断开/恢复开关跟着消失,而那恰恰是最该在手边的时候(探针要不要让给别的 agent 软件,不必等 AI 先跑一轮)。修法=链路行**常驻**:不再要求 probe/chip/flash 至少有一项,一条链路读数都没有时只留「后端」一张卡(三张占位卡说的是「日志里缺这项」,空态是「还没有日志」,摆三张「未识别 · 复制提问」只是添乱);≤620px 网格下 `.op-linkrow > .op-link:only-of-type { grid-column: 1 / -1 }` 让单卡吃满整行(Chrome 实测 271.5px → 551px,半张卡配右半空白看着像渲染坏了)。② 顺带查出并修掉截图里自相矛盾画面的成因:现场活动文件 0 字节但 mtime=14:18:46(`writeFileSync` 截断才会改 mtime 而大小不变;ocdup 的 Append 开文件不动 mtime)→ 用户先点「加载 .1 尾部」(1470 条进环)又点了「清空」,而 `clear()` 只清 entries 不清 rotNote,「已加载 .1 尾部 1470 条」在条目被清掉后继续说谎。现在 clear 一并清备注;Host clear 路由同时重置 `rotLoaded`(否则「加载过→清空→再点加载」被 already loaded 挡住,而环里其实已空)。③ 加载备注从空态里挪到列表上方独立一行 + 6s 自清——放在空态里条目一加载上来空态整块消失,备注闪一下就没了。④ 测试 harness 升级成带 hook 作用域的迷你 React(每个函数组件一份 hooks、主组件跨重渲染保留;`rerender` 必须把 idx 归零,否则第二次跑组件全部重新初始化成默认值——这个坑值得记),「点按钮→setState→重渲染→看树」能真跑,T28 端到端复现并钉死上述画面。测试:面板四模式 185→199(api 27→29:清空后同一份 .1 可再加载;client 133→145:空态后端卡/点击走 POST /api/ocdup-backend/加载合并与列表渲染/两段式清空清备注),C# 177 与 e2e 不变(未碰) |
| v0.4.8 | **面板 v1.3.6:轮转的历史不再从面板上消失(Host 尾随逻辑 + 空态入口,C# 零改动;用户截图:重启后全空,footer 却写「轮转文件已生成」)**:① 先证伪「重启把日志截断了」——`TrafficLog.OpenWriterLocked` 用 `FileMode.Append` 开文件,重启只追加不截断;2MB 轮转(`RotateIfNeededLocked`:先删旧 .1 再移入)是设计内的有界策略。现场:.1 = 2,097,171 字节 / 11,765 行,末条 2026-10-07T07:22:32 的 `CMSIS-DAP CMD_CONNECT/DISCONNECT failed`(上次「外部实例占着端口」实测留下),活动文件 0 字节(10月8 13:17 生成,其后没有任何 ocdup 工具被调用——板子没插、没有 AI 会话),所以「空」本身正常,数据没丢。② 真缺陷 = 面板对 .1 失明:Host 把「活动文件变小」一律当清空 `reset()`,轮转发生的瞬间正在看的面板直接白屏;DSH 重启后更只读活动文件、从不读 .1,footer 那句「已生成」等于废话。③ 修法——尾随发现活动文件消失/收缩**且 .1 刚换过**(size/mtime 与上一轮不同)时判定为 ocdup 轮转:先把 .1 的尾部窗口(FIRST_TAIL_BYTES,默认 256KB)接进内存环,再继续跟新文件(老条目在前、新周期条目接在后,时序正确);`.1` 没动 = 面板清空路由自己 reset 或外部截断,照旧全清——两种收缩因此可区分,旧历史不会在清空后回流。④ 新增 `POST /api/ocdup-log/rotated`:按需把 .1 尾部补进环(空态「加载 .1 尾部」按钮),与自动续接共用同一条 `ingestRotated` 读取路径;自动续接会记住这份 .1(size+mtime),同一份重复加载返回 `note=already loaded`,条目不翻倍。⑤ stats 增 `rotSize`/`rotMtimeMs`,footer 轮转列从没信息量的「已生成/未生成」改成「.1 2.0 MB 10/7 07:22」/「.1 无」;空态显示「更早的 2.0 MB 已轮转到 .1(10/7 07:22)— 当前周期还没有新流量」+ 加载按钮 + 一次性备注(已加载 N 条 / 已经加载过了 / 失败原因)。⑥ 测试抓到的真 bug:`ingestRotated` 原先无条件丢窗口首行,而小文件 `from=0` 时窗口起点就是行边界,第一条完整行被当半行丢掉——改成只在 `from > 0` 时丢半行(整个窗口都是半行则整段丢)。测试:面板四模式 166→185(api 17→27:轮转续接/清空不回流/rotated 路由三态;client 124→133:空态两支/点击走 POST/fmtStamp),C# 177 与 e2e 不变(未碰) |
| v0.4.7 | **面板 v1.3.5:后端段结论压缩 + 链路行 2×2(纯 Client,C# 零改动;用户截图反馈)**:① 断开后「后端」卡把整句 rsp 文案(`backend was not running (nothing on 127.0.0.1:6666) - nothing to disconnect.`)铺在卡片外,把链路行撑爆——新增 `backendVerdict(action, ok, text)` 纯函数把结论压成一个词(已断开/未运行/未释放/已连接/失败 + 语义色圆点),完整文案进 tooltip,且断开动作本来就镜像成 cmd/rsp + backend SYS 行,流水可追溯。② 状态机补漏:`backend was not running` 也判 stopped——旧逻辑跳过该行、回退读到更早的 `started openocd`,把「没跑」显示成「已连接」;无 backend 行时显示「未知」。③ 「后端」段调到段首(这一行唯一能动手的开关,前置=最易点),段名带 `.op-seg-label`(≤560px 隐藏),补中性左色条不抢三条数据通道色。④ 链路行布局从内联 style 迁到 `.op-linkrow` class(**内联 display:flex 会压过容器查询的 grid**,这是个值得记住的坑),≤620px 换 2×2 网格 + 刻度标签横跨两列——flex 贪心打包在 575px 停留下排出「4+1」这种一行挤满、一行只剩一张卡的不均衡两行。测试:面板四模式 160→166(client 124,新增 backendVerdict 五态 + 状态机补漏断言),C# 177 与 e2e 不变(未碰)。预览:stub-React 渲染 + headless Chrome 双宽度(340/575)截图核对,深色主题 token 从用户截图采样 |
| v0.4.6 | **面板 v1.3.4:右侧停靠回归 + 「后端」断开/恢复开关(用户拍板:探针共用,不用时手动断开让给别的 agent 软件)**:① 入口改版——左侧 panellist 图标整体移除(全局面板在窄栏没使用体验),改右栏 tab:`sidebarRightTabs.register({id:'dsh-ocdup-panel', kind:'ocdup-log', multiple:false, priority:'plugin', title, keepMounted:true, guide:[{id:'new', order:30, title, description, icon:ChipGlyph}]})` + body 注册 `sidebar.right.pane.tab`(key = 同一个 id;契约读 app.asar 的 ui-sidebar-terminal 确认:guide 不需要 commandId,openTab 认 kind、body 认 id);宿主无右栏服务时退回 main 全局面板。副作用:guide 卡只在有会话时可见。② 新增第 20 个工具 `disconnect`:`BackendSession.StopAsync`(占 _connectGate → Invalidate + `_launcher.Stop()` 只杀自拉 openocd + 等端口释放 ≤2s,不重新拉起)+ `StopVerdict(wasUp, stillUp, endpoint)` 三态纯函数(真断了 / 本来就没跑 / 外部实例杀不动——第三种必须说清楚,否则用户以为探针已释放);read_only 拦 BLOCKED(停机 = 改变后端状态,与 set_target 同例)。③ 面板 Host `inject: ['webServer','tools']` + 路由 `POST /api/ocdup-backend {action:'disconnect'|'connect'}` → `ctx.tools.execute` 调 `mcp__ocdup__*`(MCP 工具在 Host 侧就是普通工具;宿主文档明确直接调用只是不受 agent 提醒跟踪;10s 超时)。④ 面板 Client 链路行第四段「后端」:`backendStateOf(entries)` 从 backend SYS 行派生状态;点击断开/恢复,烧录进行中二次确认(3s 窗口 + 危险色),结果一次性反馈并镜像进流水。⑤ 窄停靠排版:芯片卡 grow 改 `.op-link-grow` class,≤560px 收回自然宽。⑥ 删除死代码 PanelIcon/badgeStore(未读徽章原依赖左侧图标)。验证:C# 单测 173→177,e2e exit 0(disconnect 外部实例如实回报 + read_only BLOCKED),面板四模式 158→160(client 118);`dsh-serial-panel`(ACCOM)同病灶同改法(Xcom 仓库) |
| v0.4.5 | **面板 v1.3.3:修「侧栏图标点了没反应」(宿主 0.2.0-rc.2 注册契约换代，纯 Client，C# 零改动)**：读 `D:\DSH\resources\app.asar`(未压缩 JS)的 ui-sidebar 源码逐行确认：① 侧栏 `PanelRow` 自己绑 `onClick → ctx.layout.selectPanel(id)`,图标只是渲染进宿主 `<button>` 的 glyph,插件自造 onClick 永远不触发;② `selectPanel(id)` 只认 **main keyed slot 里同一个 id** 的注册项,**缺 key 直接抛错**并保持原面板(ui-sidebar 文档原文);③ 侧栏图标 = 全局面板(main)启动器,右栏 tab 归 chat/browser/document 那类 session 级内容。而旧插件把 body 注册在 `sidebar.right.pane.tab`(key=插件 id `dsh-ocdup-panel`)、点击却 `selectPanel('ocdup-log')`,且两个 rightbar 服务在新宿主**仍然存在**(官方 ui-sidebar-browser/ui-plan 在用)→ `hasRightbar` 恒真 → main 槽一次都没注册 → 抛错静默;附带 bug:`openTab('ocdup-log')` 传的是 kind 而 body key 是插件 id,老宿主路径同样是错的。修法:无条件按全局面板注册——`panellist id` 与 `main key` 严格同值、`inject` 只声明 `slots`,`PanelIcon.onClick` 退为老宿主兜底(带 panelId);删除右栏 tab 注册与 openTab。副作用(有意):面板从右侧停靠 tab 变主视图区整屏面板,容器查询降级照旧生效。测试:新增 T25(inject 只 slots / id-key 同值 / main 只注册一次 / 右栏服务在也不走分支 / 图标点击转发 selectPanel),client 116 项、四模式 158 项全绿;部署 4 份副本 hash 一致。`dsh-serial-panel`(ACCOM)同一病灶(Xcom 仓库真源,同改法) |
| v0.4.4 | **PY32 烧录打通：puyaf0x flash 驱动移植进 Artery fork（路线 3 落地）+ 真机修正**：① 将 l0ud/openocd-puya 的实验性 PY32F0 flash 驱动移植进 ArteryTek/openocd 基座（drivers.c/Makefile.am 三处注册），自编译后端二进制 atopenocd-py32（gcc16 + jimtcl master），Ocdup 侧零改动——`target_presets` 加 `PY32F002A-FLASH` 一行即接入（C4 扩展点设计兑现）。② 构建期重大发现与修复：fork 钉的 jimtcl（2021, a77ef1a）配现代 gcc 有内存级 bug——`source [<命令替换>]` 静默不执行（-f/script 加载 cfg 全失效）+ 参数间歇丢失；换 jimtcl master 并打 5 个兼容补丁（log.h/configuration.h/jim-nvp.c 补 stdio、command.c isproc→flags 位、openocd.c expr 去 currentScriptObj、LIBS=-lz、jimtcl/configure.ac dummy）。全程记录 docs/openocd-puya-build.md。③ 顺带修复：atopenocd 的 reg 输出为冒号形态且寄存器名 xPSR 大小写敏感（真机抓到，v0.4.3 补丁已入库）。④ 真机：AT32F423 钥匙板 bring-up 打通（connect_assert_srst 治深睡，DEV_ID 0x250 已填 chip_id_map）；PY32F002A 调试链路验证通过（Puya 无 DEV_ID 惯例的结论入库）；⑤ 烧录闭环真机全过（2026-10-07）：PY32F002A 整片 20KB 擦+写回（verify OK + 独立回读 sha256 一致）+ AT32F403A 整 bank 512KB 同法回归（flash_write mass erase+写回+verify OK+回读一致，813350e）；途中抓到 cfg 坑——`reset_config connect_assert_srst` 不含 RESET_HAS_SRST，缺 `srst_only` 位时 openocd 报 "BUG: can't assert SRST"、深睡芯片救不活，key cfg 已补 `srst_only srst_nogate` 并 clean attach 验证。 |
| v0.4.3 | **halt 核心寄存器现场 + watch 采样 CLI（M-watch 翻牌）+ 收尾**：① `halt`/`reset halt` 成功后 best-effort 回读核心寄存器（`reg pc/lr/sp/xpsr`，2s 总预算 + 全吞异常，取不到只少一行）——"程序跑飞在哪"一步定位：PC 看现场、LR 看调用来源、xPSR 看异常态；`Parsing.ParseRegValue` 纯函数容忍 `pc = 0x…`/位宽注/尾注各形态。② `ocdup watch` 子命令 CLI（`Cli/WatchCli.cs`）：按符号名定时采样变量——`--name` 可重复/逗号分隔、`--interval`（默认 250ms）、`--format csv` 重定向画曲线、`--halt` 每拍 halt/resume 读一致快照（过 `allow_exec` 闸）、`--count` 限次；ELF/路径解析复用 SymbolTools（LoadSymbols 转 internal），取值 = mdw 对齐字 + 小端移位掩码（`WatchValue` 纯函数，兼容非对齐 u8/u16）；连续 30 拍失败退出并提示，Ctrl+C 优雅收尾（--halt 途中保证 resume）。独立 CLI 而非 MCP 工具：长时运行会占死 MCP 串行通道（C1）。③ 收尾：ocd.json.example 补 `target_presets`/`chip_id_map` 示例键；面板 test.js 按 mode 自设 env + 日志落临时目录（README 一行命令零配置可跑）+ 面板测试进 CI 独立 job；板级 cfg 入库 `cfgs/`（钥匙板 AT32F423 connect_assert_srst 治深睡禁调试；锁芯板 PY32F002A 最小 cortex_m 调试 cfg）+ README 板级分工说明；e2e 假后端补 `reg` 响应与 `reset halt` 用例；插件 0.2.7（SKILL 同步 19 工具 + set_target/watch）。C# 测试 140→169（ParseRegValue 9 例 + WatchCli 纯函数 20 例），e2e exit 0，面板 158 不变。**④ 真机修正（同日 AT32F403A 实测抓到）**：atopenocd 的 `reg` 输出实为 "pc (/32): 0x0800ab62"（**冒号**+位宽注）而非假后端原设的 "=" 形态，且寄存器名大小写敏感（`xpsr` 报 not found，须 `xPSR`）——ParseRegValue 兼容 '='/':' 双分隔并逐行跳过日志前缀/错误文本，ExecTools 改查 xPSR，假后端改按实测形态响应。测试 169→173 |
| v0.4.2 | **面板 v1.3.2 界面精修(纯 Client,C# 零改动)**：用户回报「按键和文字显示以及配色」——用 stub-React 渲染 + headless Chrome 截图/量尺寸的方式,先在真像素上定位崩坏,再改:① **控件折行崩坏**:所有控件 `nowrap + flex:0 0 auto`,工具栏两行都允许整组换行(旧版窄面板把「暂停/跟随」挤成上下两行、状态胶囊里第 4~5 个方向计数被裁掉)。② **配色撞车**:engaged 态统一「墨色实底」(`label-primary` 底 + `bg-layer` 字,深浅主题自动反转)——DSH 主题里 `bg-layer-4` 缺席,旧版 `var(--dsw-alias-bg-layer-4, #eef1fe)` 会露出浅蓝底配墨色描边,和宿主近黑品牌色打架;`.op-sel` 选中底色也改 `color-mix(brand 12%)`。③ **方向过滤改通道 chip**:原生 `<select>` 换成带色方点 + 计数的 chip(cmd 69/rsp 78/sys 14/log 159,`dirLegend(stats, active)` 纯函数,当前生效方向即使归零也保留),与工具 chip 同一套「点中即过滤、再点取消」语言;顺带删掉与占比表重复的图例、`SYS` 统一 `sys`。④ **工具条单行横滚**(右缘渐隐 + 滚轮转横向 + 计数贴右):旧版 11 枚 chip 换行成 3~4 行、窄面板吃掉半个屏幕;清空/导出随计数挪到该行右侧。⑤ 正则 `.*` 内嵌搜索框右端;目标下拉按需出现(单目标且未过滤不占位);计数文案 `counterLabel` 单调化(替掉「渲染 300 / 320 / 320 条」)。⑥ **链路读数条层级翻转**:值(型号/容量/探针名)做主读数(主文本 + 600 + tabular),段名退成弱标签,段色只留左色条 + 图标;`chipLabel` 增 `head/tail`,`tail`(内核/dev)在 ≤620px 整段隐藏(不再半词截断),型号未识别时芯片图标转警示色。⑦ 传输控件改 SVG(暂停/继续/跟随),详情区 `log` 段名改石板灰对齐通道色、标题行去 `·` 串与重复日期。验证:C# 140/140 不变,e2e exit 0,面板断言 142→158(client 100→116,新增 T23 图例/计数/芯片分层与 T24 面板整体渲染冒烟) |
| v0.4.1 | **双 bank flash 探测修复 + 面板 v1.3.1 精修**：① 实测 AT32F403AVGT7(G 版 = 双 bank:bank1 0x08000000 + bank2 0x08080000,各 512K,合计 **1024K**)——`at32f403axG.cfg` 两个 bank 都 FLASH_SIZE=0,而 **`flash probe 0` 不带动 bank1**(驱动只报 "main flash size: 0x100000 ... bank size: 0x80000"),导致 bank2 显示 size 0。`FlashTools.BanksAfterProbeAsync`(公开,connect 自动探测复用):先 `flash banks` 数 bank 数(#N 正则),再**逐个** `flash probe N`,最后列 banks;零 bank/单 bank 探不动都有明确文案。② ocd.json 转 G 版:target_cfg `target/at32f403axG.cfg`,presets 增 `AT32F403A-512K → at32f403axx.cfg`(单 bank 旧版备用)。③ 面板 v1.3.1(纯 Client):Flash 段报 bank 合计(`parseFlashInfo.total`,1024K 一眼可见)、工具栏下排分组(方向/目标/正则 | 搜索 | 操作 | 计数,组间发丝线,窄屏整组换行)、stats 计数改方向色图例(替 '·' 串)、调用视图补吸顶列头条、链路卡左侧 2px 段色条。真机验证:`flash_info` → 两 bank 各 0x00080000,合计 1024K;C# 140/140,面板断言 139→142 |
| v0.4.0 | **target 注册表 + set_target:换芯片不重启会话（C# 与面板双侧）**：① ocd.json 新键 `target_presets`（家族名 → openocd target cfg,值就是 `-f` 参数,scripts 名或绝对路径均可；show_config 展示清单）与 `chip_id_map` 联动；② 新工具 `set_target <家族名|list>`（第 19 个工具）——ResolvePreset 纯函数校验（大小写不敏感/'list' 保留字/未知名报错带清单）→ 已在跑同脚本则空转 → `read_only` 拦截（切换=重启后端,属改变后端状态）→ ConfigStore.ApplyTargetSwitch 运行时换 target_cfg（独立通道,target_cfg 刻意不在 RuntimePatchableKeys,set_permissions 碰不到它）→ BackendSession.RestartAsync（Invalidate + launcher.Stop 只杀自拉进程 + 等端口释放 + 重新拉起,失败自动回滚 target_cfg）→ 外部实例占端口时如实警告；成功镜像一条 SYS 进流量日志。③ identify 失配检测：`TargetMismatch(currentCfg, family, presets)` 纯函数（basename 忽略大小写比较,绝对路径与 scripts 名视为同脚本）,命中时 identify/connect 报告末行追加 `target mismatch: identified X, current target_cfg: Y -> use set_target X`。④ connect 成功后顺带 best-effort flash 布局（`flash probe 0`+`flash banks`,auto-size 配置必须 probe 才有真实容量;失配时跳过并说明;镜像 tool=flash_info 同格式）。⑤ 面板 v1.3.0：链路行「⚡ 切换 <家族名>」胶囊（parseTargetMismatch 解析失配行,点击复制 `mcp__ocdup__set_target` 调用——面板不能调 MCP,沿用复制提问模式;胶囊跟着最近一次 identify 报告走,切完即消失）。C# 测试 128→140（TargetMismatch 四形态/ResolvePreset 五形态/target_presets 解析/WithTargetChange）,面板断言 135→139（T22 失配行解析+胶囊跟随语义）。本机 ocd.json 转多芯片配置：interface cmsis-dap 钉死不变、target_cfg 切 `target/at32f403axx.cfg`（通用,flash 自动探测不挑封装）、chip_id_map 填实测 `"0x344": "AT32F403A"`、presets 收录 AT32F403A/STM32F103/PY32F002A/AT32F423 |
| v0.3.19 | **DSH 侧边栏面板 v1.2.5:芯片段「只信实测」+ 链路三段语义色（纯 Client 侧,C# 零改,csproj 不 bump）**：① chipLabel 重写——family(chip_id_map 命中实测 DEV_ID)→ 型号+内核;否则只报 CPUID 内核 + 有效 dev_id + 琥珀「型号未识别」,identified=false;target_cfg 文件名推定的 model 不再上卡(用户钉错 cfg 时就是误导,实测插的根本不是那片子),退到 tooltip 并标注「仅配置线索,非实测」+ 填 chip_id_map 指引。② 链路三段各一个语义色(LINK_TONE:探针=sys 蓝 #2563eb / 芯片=rsp 绿 #059669 / Flash=cmd 琥珀 #d97706,与流水方向色同一套仪器语言),图标+标签着色、值留主文本色,hover 改中性加深(品牌色不与段色打架),缺段占位卡同色虚线。面板测试 133→135 |
| v0.3.18 | **DSH 侧边栏面板 v1.2.4「台架仪器」界面改版（纯 Client 侧,C# 与流量契约零改动,csproj 不 bump）**：① 工具栏两行化——上排「看什么」(状态胶囊 + 流水|调用分段 + 暂停/跟随),下排「过滤与操作」(方向/目标/正则/搜索/清空/导出 + 计数),行间发丝线;治单行 flexWrap 在窄侧栏把控件冲散。② 链路行改「规格读数条」:三枚方角(4px)发丝线读数卡(仪器图标:信号弧/芯片/叠层),芯片卡 flex:1 优先展示型号,内核缩写 coreShort(Cortex-M4 r0p1,架构细节进 tooltip)。③ 列表加 sticky 列头条(时间/方向/tool/B/Δt/命令·响应),列宽与行严格对齐,.op-tool/.op-len/.op-dt/.op-tag 复用容器查询窄屏同步降级。④ 控件统一 28px 高 + 6px 圆角,数字全 tabular-nums,工具 chips 22px pill,阴影只留新数据浮钮。设计定向 frontend-design:示波器/逻辑分析仪语言(发丝线/tabular/通道表),避开奶油底陶土色、编号 01/02、全大写 eyebrow、meta 串「·」、按钮「→」等模板痕迹。面板测试 130→133(新增 T21 coreShort 等),C# 128 不变 |
| v0.3.17 | **DSH 侧边栏面板 v1.2.3（界面美化 + 链路三段「说人话」，C# 只加一行推定）**：① `IdentifyTools.TryIdentifyAsync` 在 `chip identity:` 报告里追加 `model (from target_cfg <file>): <MODEL>`——纯函数 `DeriveModelFromTargetCfg`（`target_py32f002a_lock.cfg → PY32F002A`；去 `target_`/`_lock` 修饰后须字母数字混排且≥4 字符才认，auto/不像型号不加行），DEV_ID 读不出（无 STM32/AT32 DBGMCU 惯例的芯片，读回地址本身）时也能显示型号;仍是只读。② 面板 v1.2.3：工具栏统一 28px 控件/圆角/hover/focus 环（新增 .op-btn/.op-seg/.op-select/.op-search/.op-stat CSS 类，开关态品牌色浅底、确认清空危险红、统计胶囊、搜索内嵌放大镜）；链路行改小卡片（图标+标签+值，hover 描品牌边）；探针段按接口脚本映射烧录器类型名（friendlyProbeName/probeLabel,cmsis-dap→CMSIS-DAP、jlink→J-Link、stlink→ST-Link、atlink→AT-Link）；芯片段型号优先（chipLabel：family→model→core，认不出补琥珀「型号未识别」）、dev 0x000 判无效不再显示（isBogusDev）；Flash 段只写容量（fmtKb:`1024 KB`），bank/基址进 tooltip。C# 测试 115→128(Theory 13 例);面板断言 121→130(新增 T20) |
| v0.3.16 | **connect 自动芯片识别 + 链路显示补齐（C# 与面板双侧小改）**：① `connect` 成功后 best-effort 自动识别芯片——`IdentifyTools.TryIdentifyAsync`（从 identify_chip 工具主体抽出，可复用）：读 CPUID(0xE000ED00)+DEV_ID(0xE0042000),**3 秒短超时 + 全吞异常**，目标不应答只补一行说明、绝不拖垮 connect（通道层超时会 invalidate 连接，后续工具自动重 Acquire）;两个读都没拿到数返回 null 不镜像；拿到数把 `chip identity:` 块镜像一条 rsp(tool=identify_chip) 进流量日志。② `OpenOcdLauncher` 钉死 interface_cfg 时启动文案补 `interface: <cfg>`（原来只有 auto 模式有说明）。③ 面板 v1.2.2 配套：芯片来源放宽（connect 的 rsp 按 `chip identity:` 文本形态也认）、探针固定配置模式（显示接口脚本名 baseName)、链路行缺段给「未识别 · 复制提问」占位。C# 测试 114→115;e2e 假后端覆盖 connect 自动识别；面板断言 116→121 |
| v0.3.15 | **DSH 侧边栏面板 v1.2.1（链路状态区；同样仅动 `tools/dsh-ocdup-panel/`）**：工具栏常驻「链路」行——探针（connect 的 backend SYS `probe auto-detected: kind -> cfg (evidence)` 三要素）、芯片（`identify_chip` 的 rsp：CPUID 解码 core + dev_id + rev_id + chip_id_map 家族，未配时 unknown）、Flash 布局（`flash_info` 的 bank 行：基址 + 大小，fmtSize 三档）；三项各取最近一次成功解析（从新往旧扫），点击跳来源（探针→搜 auto-detected；芯片/Flash→调用视图按工具过滤）；面板仍永不主动连硬件。测试 104→116 项（新增 T19 链路解析） |
| v0.3.14 | **DSH 侧边栏面板 v1.2.0（插件优化；C# 服务端与流量日志契约零改动，仅动 `tools/dsh-ocdup-panel/`）**：功能——调用配对视图（「流水/调用」双视图：cmd↔rsp/err 按 tool 流式配对，控制通道串行故无歧义；期间的 openocd `log` 行归入该调用；未配对行原样保留）、烧录进度条（解析 openocd 日志 `erased sector`/`wrote N bytes`/`verified N bytes`/`** Verified OK **`，擦除/写入/校验分段，完成度未知时段流光动画；Verified OK 走 rsp 回到也识别）、会话分组（`backend` sys 事件分隔条，可折叠）、工具 chips 快捷过滤（按频次）+ err 快捷过滤、关键行高亮（Verified OK/err）、键盘导航 ↑/↓/Enter；界面——方向色条、cmd/rsp 底纹交替、stats 五色占比条、pill 徽章、空态/详情区卡片化。零依赖行为测试 71→104 项断言（新增 T15 进度解析 / T16 调用配对 / T17 会话折叠与清单 / T18 关键行标记） |
| v0.3.13 | **M-sym 第一层（L1）落地**：新增 3 个工具——`list_variables`（ELF .symtab 对象符号清单：地址/大小/名，可 pattern 过滤，PC 侧读文件不碰目标）、`read_variable`（按名读：ELF 定位 → 控制通道读回，受 memory_read_max_bytes；1/2/4 字节给单值解释，大对象给 hex 串）、`write_variable`（按名写 1/2/4 字节变量，大对象引导用 write_memory；C2 权限前置）。新增 `Backend/ElfSymbols` 纯函数解析器（ELF32 LE，拒绝 stripped/大端/ELF64 并给可操作文案；Keil/GCC 产物通用）与 ocd.json 新键 `elf_path`（默认镜像）。同名 static（多个编译单元）分列地址引导用 read_memory。工具数 18；测试 114/114，e2e 内嵌最小 ELF32 夹具覆盖三工具 |
| v0.3.12 | **M-perm 收尾 + P3 清理**：① 新增第 17 个工具 `set_permissions`——运行时改权限（免改 ocd.json），闸 = 新权限键 `allow_set_permissions`（**默认 false**：放宽权限是人的决定，该键就是那份人类授权；刻意不挂 read_only 总闸，否则锁死的会话永远无法解除）；运行时补丁在 `OcdConfig.WithRuntimeChanges` 入口按 `RuntimePatchableKeys` 白名单强制拒绝越界键（防御纵深，路径白名单/探针/芯片配置只走文件），来源标 `(runtime)`，reload/重启丢弃；② `OCD_READONLY=1/true` 环境变量加载时强制只读（优先于 ocd.json）；③ P3 三件：`HexValue` 按宽度掩码（杜绝 33 位输入送成多字写）、TCL-RPC 写方向纳入命令超时（与读对称）、C2 澄清（本地文件解析允许先于闸门，但不得触碰后端）。工具数 17；测试 110/110，e2e 补「runtime 补丁→read_only 拦截→解除→show_config 展示 (runtime) 来源」 |
| v0.3.11 | **identify_chip 工具 + 启动即死诊断**：① 新增第 16 个工具 `identify_chip`——读 ARM 标准 CPUID（0xE000ED00，解码 Cortex-M 内核型号，任何 Cortex-M 有效）+ 厂商 DBGMCU_IDCODE（默认 0xE0042000，可参数覆盖），DEV_ID 低 12 位在 ocd.json 新增的 `chip_id_map`（外置映射，守 C4 零硬编码）中按数值匹配给出家族名——回答「找到烧录器后，连的是什么芯片」；② `EnsureStartedAsync` 端口就绪判定改为「连续 3 次探测成功且进程未退」——openocd 先绑 tcl_port 随后才连探针，DP 失败会即刻退出，原判定会把刚死的监听当就绪（真机复现：目标未应答时 connect 报 `cannot open channel: refused` 而非真正失败原因）；③ ConnectAsync 失败时附上自拉后端的近期输出诊断。工具数 16；测试 96/96，e2e 补 identify_chip 两用例 |
| v0.3.10 | **健壮性修复 + 文档全面对齐**：① `dump_memory` 执行前先删除已存在的目标文件——此前 dump_image 失败但路径上有旧 dump 时，会把旧文件的字节数/sha256/预览当本次结果返回（陈旧数据误导验证）；② 自拉 openocd 绑进 Windows Job Object（KILL_ON_JOB_CLOSE）——ocdup 被强杀/崩溃时 ProcessExit 钩子不触发，作业句柄随进程消亡由内核关闭并收割整棵进程树，补 C5 的孤儿缺口；③ csproj 版本补 bump（0.3.8 → 0.3.10，对齐变更记录）；④ 全文回填 v0.3.3–v0.3.9 期间滞留的过时描述（工具清单 7→15、权限表补 allow_exec、S4/S5/CI 状态、测试数 49→90、项目结构补 ExecTools/TrafficLog 等） |
| v0.3.9 | **工程化 S5 收尾**：根目录 `publish.cmd`（单测 Release → publish → 提示重启 DSH；检测到 ocdup.exe 运行即拒绝，防文件锁）；CI workflow（build + 单测 + e2e，windows-latest）。DSH 注册侧的发布顺序（退出 DSH → publish → 启动）已写入 README 与脚本头部 |
| v0.3.8 | **执行控制 M-exec**（后置里程碑首个落地）：`ExecTools`——`halt [timeoutMs]` / `resume [address]` / `reset run|halt|init`。新权限开关 `allow_exec`（默认 true：读失败→halt→resume 是常规调试路径；`read_only` 总闸照常全拦）。`read_memory` 描述同步改为「失败先 halt，完事 resume」。M-bp（断点）、M-sym（符号层）仍后置；halt/resume 的真机行为待下次 S4 联调记录。工具数 15；测试 90/90，e2e 补「halt→read→resume→reset run」与 read_only 拦截 |
| v0.3.7 | **openocd 异步日志镜像 + 面板 v1.1**：`OpenOcdLauncher.PumpAsync` 把抽干的 stdout/stderr 逐行 `Record("backend","log",…)` 进流量 JSONL——烧录进度（逐扇区 erase/write）、`Info:`/`Error:`、target 事件从此在侧边栏面板实时可见（此前只在 `backend_log` 工具与启动失败诊断里）。面板（v1.1.0）：direction 新增 `log`（石板灰），过滤下拉与 stats 同步；行为测试 71 项断言全过 |
| v0.3.6 | **补全「闸就位、工具缺位」三件套**（能力矩阵闭环）：`write_memory`（mww/mwh/mwb 一条命令写多值，单次 ≤256 值，`CheckMemoryWrite` 前置）；`flash_erase_sector`（`flash erase_sector bank first last`，last 支持 -1=到 bank 末尾，`CheckFlashErase`——**默认关闭**，破坏性操作显式开启）；`run_command` 原始 TCL 逃生舱（单行命令，`CheckRawCommand`）。工具数 12；权限闸逻辑沿用既有单测，e2e 补「放行写内存 / 默认拦截擦除 / 逃生舱透传 / read_only 全拦」四条链路 |
| v0.3.5 | **读路径体验升级**（高频路径直接受益）：① `read_memory` 新增 `format=json`——`Parsing.TryParseMemoryOutput` 解析 mdw/mdh/mdb 文本为结构化 `{address,width,count,bytes,values:[hex]}`，免得模型自己解析 OpenOCD 文本；解析失败带原文返回（运行态读错误不被吞）；② `status`/`connect` 的 `targets` 表格解析成一行式摘要（`TrySummarizeTargets`，`0* name (type, endian) state`），失败回退原文；③ 新增 `verify_image` 工具（后端 `verify_image` 命令）——镜像与目标 flash 比对、**不写任何东西**，回答「板子里到底是谁」；判定 `InterpretVerifyOutput` 保守策略（无 `verified N bytes` 确认即判存疑）。工具数 9；测试 89/89，e2e 补 format=json 与 verify_image 用例 |
| v0.3.4 | **流量镜像 + backend_log 工具**（侧边栏面板的数据层前奏）：新增 `Backend/TrafficLog`——每次工具调用发出的 TCL 命令/响应（`ExecuteAsync` 单点镜像，`op` 参数标注发起工具）、后端生命周期事件（拉起/复用/失败诊断）追加到 `%LOCALAPPDATA%\Ocdup\mcp-traffic.jsonl`，字段与 acccom 的 mcp-traffic.jsonl 同一套（id/tool/timestamp/direction/rawHex/text/portTag），DSH 插件可复用 dsh-serial-panel 的尾随逻辑；ocdup 是低频命令-响应，不需要 acccom 版的批量刷盘/内存环（逐条落盘 + 2 MB 轮转）。新增 `backend_log` 工具——launcher 的 200 行滚动缓冲（原仅启动失败诊断用）暴露成只读工具，TCL-RPC 响应里看不到的 `Info:`/`Error:` 异步日志（target reset 等）从此可查。测试 70/70，e2e 补 backend_log 用例 |
| v0.3.3 | **dump_memory 落盘安全闸**：dump 是「目标侧读、PC 侧写」，此前无任何闸——模型可让 openocd 往任意路径写/覆盖文件，`length` 无上限可请求写满磁盘（提示注入的风险面）。新增 `dump_allowed_paths`（默认 `["dumps/"]`；显式 `[]` = 不限，与 `flash_allowed_paths` 的空值约定一致）与 `dump_max_bytes`（默认 64 MB，0=不限），`PermissionGate.CheckDumpWrite` 复用现有 BLOCKED 文案模式（拦截必带解除指引）；不挂 `read_only` 总闸——dump 正是只读分析 flash 的手段。权限表与 show_config 同步展示；测试 63/63，e2e 补「白名单外 / 超 dump_max_bytes」两用例 |
| v0.3.2 | **新增 dump_memory 工具**（后端 dump_image：大块读直接落盘，返回路径/字节数/sha256 前缀/前 64 字节 hex 字，绕开 read_memory 的 4KB 文本上限）；探针识别加 60s 进程内缓存（只缓存非空结果，插上即用）+ VID/PID 双通道匹配、ST-Link PID 白名单扩展（V3/V3E）；ServerInfo 版本改随程序集（csproj Version 属性）；ocd.json 改真 JSONC 注释；**A/B 实测定论：多接口 DAPLink 探针（0D28:0204）必须走 cmsis-dap.cfg（auto 默认），atlink_dap_v2.cfg 反而找不到设备**；测试 60/60 |
| v0.3.1 | **多烧录器兼容**：`interface_cfg` 支持 `"auto"`——连接时经 USB 枚举自动识别探针（AT-Link / CMSIS-DAP / ST-Link / J-Link，按此优先级）并选择接口脚本；gdb/telnet 服务改为 `disabled` 真正关闭（原 `0` 实为随机端口，已实测）；J-Link on Windows 的 SEGGER 驱动冲突写入启动失败诊断；版本命令 `ocd_version` 修正为 `version`（真机报 `invalid command name`）；识别结果经 `connect`/`status` 的 `backend:` 行回报（`BackendSession.LastBackendNote`）；修正探针探测未抽干 stderr 的隐患；文档补齐命令行形态、命令名核对表、项目结构与测试数 |
| v0.3 | **范围收敛为「下载 + 读指定地址」**：断点/观察点/执行控制/条件断点/符号层（ELF/SVD）/L3 协议字典/串口协同/watch 全部后置（见第 12 节）；实现层落地 TclRpcClient、OpenOcdLauncher、BackendSession、PermissionGate、ConfigStore 与 6 个工具；配置文件新增 `command_timeout_seconds` / `memory_read_max_bytes`，权限项支持 `permissions` 嵌套块；来源追踪改为按「JSON 属性是否存在」判定；清除 v0.2 残留的 Python 表述 |
| v0.2 | Q1 定案：C# / .NET 8；探针画像：AT-Link（主力，CMSIS-DAP）+ ST-Link / J-Link / DAPLink；新增「阶段一：烧录闭环先行」（见第 12 节）；第 11 节项目结构改为 C# 形态 |
| v0.1 | 初稿 |

---

## 1. 能力定义（CAPABILITY）

**一句话**：Ocdup 是一个运行在 PC 侧的 OpenOCD 门面（MCP 服务器），把「连接探针 → 烧录固件 → 按地址读回数据」封装成 MCP 工具，让任何 MCP 客户端（DSH、Claude Code、Cursor 等）都能用自然语言驱动硬件，并与既有串口工具链（acccom）协同。

**本轮（v0.3）范围**——只做两件事，做到真机可用：

1. **下载**：`flash_write` 把构建产物烧进目标 flash（写 + 可选校验 + 可选复位运行）；`flash_info` 报告 flash 布局。
2. **读指定地址的数据**：`read_memory` 按地址与宽度读回目标内存；大块读取（整片 flash / RAM 快照）用 `dump_memory` 直接落盘，绕开文本上限。

支撑这两件事的是**控制层**（TclRpcClient / OpenOcdLauncher / BackendSession）与**权限闸**（PermissionGate）——它们是后续所有能力的地基，本轮必须打牢。

**明确后置**（见第 12 节后置里程碑）：断点/观察点/条件断点、`step`（halt/resume/reset 执行控制已在 v0.3.8 落地）、符号层（ELF 变量名、SVD 外设寄存器）、L3 协议字典、`watch_variables`、`set_permissions`/`OCD_READONLY`（`run_command` 逃生舱已在 v0.3.6 落地）、与 acccom 的串口协同。

**能力变化**：调试动作的发起方从「人手动敲 TCL / 点 IDE 按钮」变为「模型调用工具」；调试知识的载体从个人经验变为可版本化的项目配置与符号层（符号层后置）。

---

## 2. 背景与参考架构提炼

对 openocd-mcp 的源码审查（10 个文件，约 92KB Python）得出四个被实战验证过的设计决策，Ocdup 的架构直接继承它们：

| # | 决策 | 做法 | 来源 |
|---|---|---|---|
| 1 | **走干净的请求/响应控制通道** | 连 OpenOCD TCL-RPC 端口 6666 而非 telnet 4444：telnet 会回显、打提示符、混入异步日志；TCL-RPC 每条命令/响应用单个 `0x1a` 分隔，命令包一层 `capture {…}` 收回日志文本 | `openocd.py` |
| 2 | **条件断点在服务器侧驱动** | OpenOCD 的 `halted` 事件里调 `resume` 会被忽略，所以服务器侧存 `地址→TCL条件` 字典，`resume` 时轮询：等 halt → 读 PC → `echo [eval {cond}]` → 假则续跑、真则停（**本轮后置**） | `server.py` |
| 3 | **权限闸是一等公民** | `read_only` 总闸 + 分能力开关 + 文件路径白名单 + 大小上限；被拦时返回 `BLOCKED: … See show_config`（报错即教模型下一步） | `config.py` |
| 4 | **自 provisioning，许可证洁癖** | GPL 的 OpenOCD 二进制不进发布产物；首次使用时从 xPack 下载、**校验钉死的 SHA-256**、缓存到各平台用户数据目录（**本轮只实现解析顺序的前两步**，下载后置） | `provision.py` |

同时继承它的**项目无关性**：interface_cfg / target_cfg 等全部外置到项目配置文件，服务器本身不绑定任何芯片——这是「任何板子都能用」的关键。

Ocdup 与它的关系：**复刻架构，不复刻功能边界**。Ocdup 保留这些机制，但工具集按自己的工具链定制、按阶段收敛。

---

## 3. 总体架构

```
┌────────────────────────────────────────────────────────┐
│  MCP 客户端层（DSH / Claude Code / Cursor / …）          │
│    stdio 传输（本地） ｜ 预留 Streamable HTTP（远程）      │
├────────────────────────────────────────────────────────┤
│  接入层  ModelContextProtocol SDK：工具注册 / schema / 分发│
│          （Program.cs：Host + WithStdioServerTransport）  │
├────────────────────────────────────────────────────────┤
│  工具层  Tools/*.cs：每个 [McpServerTool] = 一个能力      │
│          已实现 15 个（清单见第 5 节）：connect / status / show_config /  │
│           backend_log / flash_info / flash_write / verify_image /  │
│           flash_erase_sector / read_memory / write_memory / dump_memory / │
│           halt / resume / reset / run_command / identify_chip /  │
│           set_permissions │
├──────────────┬────────────────┬────────────────────────┤
│ 控制层        │ 权限层          │ 配置层                  │
│ BackendSession│ PermissionGate │ ConfigStore             │
│ TclRpcClient  │ read_only      │ 机器默认 → ocd.json      │
│ (TCL-RPC 6666)│ 分能力开关      │ → 运行时 → 工具参数      │
│ OpenOcdLauncher│ 路径白名单     │ （来源追踪）             │
│ ProbeDetector │ 大小上限        │                         │
│ (子进程/端口) │                │                         │
├──────────────┴────────────────┴────────────────────────┤
│  被控对象  OpenOCD（TCL-RPC 6666）→ 探针 → 目标 MCU        │
│  协同对象  acccom 串口栈（独立 MCP server，松耦合，后置）   │
└────────────────────────────────────────────────────────┘
```

**分层职责**：

| 层 | 职责 | 不许做的事 |
|---|---|---|
| 接入层 | 传输、schema 校验、工具分发 | 不含任何硬件逻辑 |
| 工具层 | one tool = one capability；docstring 写给模型看 | 不直接拼协议帧 |
| 控制层 | 与被控进程的所有 I/O；请求串行化（锁） | 不解析业务语义 |
| 权限层 | 所有变更类操作的闸门（前置检查） | 不做「事后审计」式拦截 |
| 配置层 | 分层合并与来源追踪（单一入口 + 可重载） | 不写入任何配置（只读合并） |

**关键不变量**（实现时逐条保持，测试逐条覆盖）：

1. 控制通道的每个请求必须串行化；响应必须能和无交互日志区分开。
2. 任何会写目标（memory/register/flash/erase/raw）的工具，**第一行**就是权限检查。
3. 被拦截的操作返回的字符串必须包含「如何解除拦截」的指引。
4. 项目配置（芯片型号、探针、ELF/SVD 路径）不得硬编码在任何模块里。
5. 服务器退出时必须回收自己启动的子进程，且不得杀掉非自己启动的实例。

---

## 4. 核心机制设计

### 4.1 控制通道选型

优先级：**TCP 命令端口 > REST > 可解析输出的 CLI > 交互式终端**。判据是「响应干净、不被异步日志污染」。OpenOCD 的 TCL-RPC（6666）即标准答案。

实现（`Backend/TclRpcClient.cs`）：

- 发送 `capture { <cmd> }` + `0x1a`，读到 `0x1a` 为止（一条命令 ↔ 一条响应）。
- 响应字节先累积到 `MemoryStream` 再统一 UTF-8 解码——避免多字节字符被 TCP 分段切裂。
- `SemaphoreSlim(1,1)` 串行化命令（C1）；并发调用被排队而不是交错。
- **连接超时与命令超时分离**：握手 5s，命令用 `command_timeout_seconds`（默认 60s）。烧录一条 `program` 可能跑几十秒，不能用握手超时去掐它。
- 参数引用见 `Backend/Tcl.cs`：优先大括号字面量 `{…}`（TCL 对大括号内不做替换，Windows 路径的反斜杠与空格安全）；仅当值含 `{`/`}` 时退回双引号形式并转义 `\ " $ [ ]`。

**控制通道命令约定**（真机核对：atopenocd 0.11.0+dev-snapshot 2024-08-05）：

| 用途 | 命令 |
|---|---|
| 版本 | `version` |
| 目标与状态 | `targets` |
| flash 布局 | `flash banks` |
| 烧录 | `program <file> [verify] [<addr>]` |
| 复位运行 | `reset run` |
| 读内存 | `mdw` / `mdh` / `mdb <addr> <count>` |

> `version` 才是命令名：同一版本上执行 `ocd_version` 会返回 `invalid command name "ocd_version"`。
> v0.3 从参考实现沿用了 `ocd_version`，v0.3.1 修正为 `version`——这类"命令名靠推断"的地方必须在真机上核对一次。

### 4.2 工具设计规范

1. **docstring 写给模型看**：何时调用、参数形态（`hex address, e.g. '0x20000000'`）、失败时怎么办。
2. **返回格式化文本**：带上下文的行式输出，人和模型都能直接读；不返回裸 JSON。
3. **报错带行动指引**：非法参数（`ERROR: address 'hello' is not a hex value…`）、权限（`BLOCKED: … See show_config.`）、后端（`ERROR: … call connect and retry.`）。
4. **逃生舱**：`run_command` 原始命令通道挂在 `allow_raw_command` 后（v0.3.6 已实现，单行命令）。v0.4.12 起带内容分类（`RawCommandPolicy`：写类命令与专用工具同权或重定向），且 `allow_raw_command` 默认收紧为 false。

### 4.3 分层配置

```
机器默认（只含探针等本机固定项，当前为空）
  → 项目文件 ocd.json（CWD 自动加载；OCD_CONFIG 可指路径）
  → 运行时修改（ConfigStore.Reload；configure/set_permissions 工具后置）
  → 单个工具显式参数（最高优先）
```

- **单一入口**：所有工具从 `ConfigStore.Instance.Current` 取配置，不允许各自 `Load()`——否则会出现「有的工具用启动快照、有的每次重读」的时机不一致。
- **来源追踪按「属性是否存在」判定**：显式写 `"read_only": false` 也会标为来自 ocd.json，而不是错标成默认值（v0.2 实现的缺陷，v0.3 修正）。
- `show_config(reload: true)` 可在编辑 ocd.json 后原地重读，无需重启客户端。
- **相对路径基准 = ocd.json 所在目录**（Q7 定案）；例外：interface_cfg / target_cfg 是 OpenOCD scripts 目录下的脚本名，由 `-s` 参数解析，不走本地路径解析。

### 4.4 权限与安全

| 权限项 | 默认 | 闸住的操作 | 状态 |
|---|---|---|---|
| `read_only` | false（总闸） | 以下全部 | ✅ 已实现 |
| `allow_memory_write` | true | write_memory（write_register 后置） | ✅ 已实现（v0.3.6） |
| `allow_flash` | true | flash_write | ✅ 已实现 |
| `allow_flash_erase` | **false** | flash_erase_sector（破坏性，显式开启） | ✅ 已实现（v0.3.6） |
| `allow_exec` | true | halt / resume / reset（执行控制，改目标运行状态） | ✅ 已实现（v0.3.8） |
| `allow_raw_command` | **false** | run_command（内容分类：写类命令同权/重定向专用工具，见 `RawCommandPolicy`） | ✅ 已实现（v0.3.6；默认收紧+内容分类 v0.4.12） |
| `allow_set_permissions` | **false** | set_permissions（运行时改权限）——**人类授权开关**：放宽权限的决定权在人 | ✅ 已实现（v0.3.12） |
| `flash_allowed_paths` | []（不限） | 烧录文件必须位于白名单目录内 | ✅ 已实现 |
| `flash_max_bytes` | 0（不限） | 超过大小的烧录文件拒绝 | ✅ 已实现 |
| `memory_read_max_bytes` | 4096 | 单次读回体积上限（防呆，不是安全边界） | ✅ 已实现 |
| `dump_allowed_paths` | `["dumps/"]` | dump_memory 落盘文件必须位于白名单目录内（PC 侧写入面；`[]` = 不限） | ✅ 已实现 |
| `dump_max_bytes` | 64 MB（0=不限） | dump_memory 单次落盘体积上限 | ✅ 已实现 |

- 拦截文案模板（`PermissionGate.Blocked`）：`BLOCKED: '<op>' is not permitted by the current safety settings ('<setting>'). To allow it, <如何开启>. See show_config.`
- 设置途径（后者优先）：ocd.json 的 `permissions` 块（或顶层平铺同名字段）→ 环境变量 `OCD_READONLY=1`（✅ v0.3.12：加载时强制只读，优先于文件）→ `set_permissions` 工具（✅ v0.3.12：运行时补丁，受 `allow_set_permissions` 闸约束；`flash_allowed_paths`/`dump_allowed_paths` 只走文件）。运行时补丁在 reload 或重启时丢弃。
- 检查必须前置到操作第一步（C2）；被拦时**不得**触碰后端。澄清（v0.3.12）：烧录/落盘类工具的**本地**文件解析（确定白名单路径与大小）允许先于闸门执行——它不触碰后端，闸门仍在任何目标 I/O 之前。
- 路径白名单支持「目录」与「文件」两种写法；相对项以 ocd.json 目录为基准解析。
- run_command 内容分类（v0.4.12，`Config/RawCommandPolicy`）：raw 通道与专用工具同权——`flash erase_sector`/`erase_address`→`allow_flash_erase`、`flash fill*`/`flash protect`→`allow_flash`（新增 `CheckFlashOp`：无「路径/大小」维度可校验，只要求同级人类授权）、`mww`/`mwh`/`mwb`→`allow_memory_write`、`reset`/`halt`/`resume`/`step`→`allow_exec`；带文件/落盘/动后端的（`program`/`load_image`/`flash write_image`/`dump_image`/`shutdown`/`exit`）在 raw 通道无法可靠校验，BLOCKED 并重定向 `flash_write`/`dump_memory`/`disconnect`。按 `;` 切段逐段分类（防走私，TCL 单行内 `;` 是合法分隔符）；`flash erase_check` 是只读不拦；`#` 注释段跳过。分类清单之外仍透传——主边界是 `allow_raw_command` 本身（v0.4.12 起默认 false）。

### 4.5 进程生命周期

实现（`Launcher/OpenOcdLauncher.cs`）：

- **启动**：拼命令行 → `Process.Start`（用 `ArgumentList`，含空格的参数自动引用）→ **后台两个 Task 抽干 stdout/stderr**（否则管道填满会把 OpenOCD 挂死）→ 轮询端口等就绪（超时 15s）。
- **重复启动防护**：先探测端口；已在运行（无论是谁启动的）就不重复拉起。
- **启动失败诊断**：进程提前退出或超时，回报**近期输出**（rolling buffer 200 行）+ 一句「下一步怎么做」。
- **退出**：`AppDomain.ProcessExit` 钩子 + `Dispose` 回收自己启动的进程（`Kill(entireProcessTree: true)`）；超时也回收，不留半死实例占着探针（C5）。
- **输出缓冲可读**（v0.3.4）：抽干的 stdout/stderr 除启动失败诊断外，还经 `RecentOutput(maxLines)`（纯函数 `TailLines` 可单测）暴露给 `backend_log` 工具——TCL-RPC 是请求/响应通道，target reset/halt 这类异步日志只在进程输出里。
- 命令行固定形态：`-s <scripts>` `-c bindto <host>` `-f <interface_cfg>` `[-c "transport select <t>"]` `[-f <target_cfg>]` `-c gdb_port disabled` `-c telnet_port disabled` `-c tcl_port <port>`——用 `disabled` **真正关闭** gdb/telnet 服务，TCL-RPC 是唯一对外端口；`bindto` 保证只监听 localhost（C8）。
- 会话层断链重放（P1-5，`BackendSession.ExecuteAsync`）：命令已发出后通道断链——只读 op（白名单 `ReplaySafeOps`：status / connect / flash_info / identify_chip / read_memory / verify_image / dump_memory / read_variable，目标侧纯读无二次效应）自动 `Invalidate → 重连 → 重放一次`，业务无感（traffic 镜像一条 SYS 备注）；写/执行类 op（flash_write / run_command / halt / resume / reset / write_* / flash_erase_sector / watch）维持「不重放」——命令可能已生效，重放会产生二次效应，错误文案如实说明 NOT replayed；重放再失败合并两次原因抛出。会话层新增通道串行闸 `_channelGate`（业务命令与心跳互斥，顺带收敛并发 ExecuteAsync 理论交叉的旧隐患）。
- 空闲心跳（P1-5）：后台 Timer 15s 一拍，通道空闲 ≥30s 才发一次 `version` 探测（经 `_channelGate` 与业务命令互斥，空闲期拿不到闸就让路）——提前发现 openocd 被外部杀掉，下次调用快速重连而不是等满 command_timeout 才报错；探测失败只记 traffic SYS 并丢连接，成功的探测不进流量日志（免得空闲期刷屏）。
- 进度泵（P1-4，`Tools/FlashProgress`）：flash_write / flash_erase_sector / dump_memory 执行期间，750ms 一拍差分读环形缓冲（`_totalLines` 水位 + `TakeNewLines(ref cursor)`，落后超圈只回可见尾部），把 openocd 增量日志行以 MCP `notifications/progress` 推回调用方——仅当请求带 `_meta.progressToken` 才开泵（双重守卫：server + token，任一不满足零开销）；「后端是 ocdup 自拉」由**泵每拍自查** `CapturesProcessOutput` 把关（v0.4.18：冷启动的 flash_write 是在 ExecuteAsync 内部才拉起 openocd，守卫若只在开泵时判会让首次烧录全程无进度；每拍自查同时挡住外部监听/后端刚退出的陈旧环形缓冲）；进度旁路发送失败静默收工，绝不影响主操作结论；泵生命周期由工具 try/finally 显式收口（防泄漏与双泵重复推送）。traffic 的 log 镜像通道（面板进度条数据源）不变，两条路并行。

> **端口语义实测**（atopenocd 0.11.0+dev-snapshot 2024-08-05，本机复验）：
> `-c "telnet_port 0"` → `Info : Listening on port 53427 for telnet connections`（**0 = 随机端口，仍在监听**）；
> `-c "telnet_port disabled"` → `Info : telnet server disabled`。
> 所以禁用必须写 `disabled`，写 `0` 会让 OpenOCD 另开一个随机端口，容易在真机排障时被误认为"已关闭"。

**探针自动识别（`interface_cfg: "auto"`）**：

- 连接阶段枚举 USB 设备（Windows 走 `Get-PnpDevice -PresentOnly`），按签名表 **AT-Link → CMSIS-DAP/DAPLink → ST-Link → J-Link** 选接口脚本（同种探针去重；结果按表优先级排序，与枚举顺序无关）。
- 识别不到就返回可操作报错：提示插上探针或显式指定 `interface_cfg`，不静默乱猜。
- 识别结果通过 `connect` / `status` 输出里的 `backend: probe auto-detected: CMSIS-DAP/DAPLink -> interface/cmsis-dap.cfg (evidence: <设备行>)` 行回报（`BackendSession.LastBackendNote`），**不写回配置**；启动失败时的诊断里也会带这一行——否则"它到底选了哪个脚本"无从得知。
- 解析逻辑（`ProbeDetector.Parse`，纯函数）与系统调用（`DetectAsync`）分离，前者可无硬件单测；探测失败/超时（10s）一律降级为「未找到」，不阻断烧录流程。
- **已知边界 1（脚本选择，A/B 实测定论）**：本机这根探针为 DAPLink 公版固件（`0D28:0204`），一个物理设备枚举成 5 个 `MI_` 接口。同机对照实验：`interface/cmsis-dap.cfg`（auto 的选择）→ OpenOCD 自报 `Using CMSIS-DAPv2 interface with VID:PID=0x0d28:0x0204`，examine 成功（**S4 烧录就是用它完成的**）；`interface/atlink_dap_v2.cfg`（钉死 `usb_bulk + interface 3`）→ `unable to find a matching CMSIS-DAP device`（exit 1）。**结论：多接口 DAPLink 探针就用 auto 的 `cmsis-dap.cfg`，不要换供应商脚本**；`atlink_dap_v2.cfg` 留给原生 AT-Link（VID 2E3C）设备。
  **可复用原则：让 OpenOCD 自选接口，胜过在脚本里钉死接口号**——`cmsis_dap_usb interface 3` 是硬编码的 bInterfaceNumber，换 USB 口、重插或固件改版后都可能失配，而通用脚本（不带该参数）由驱动自行匹配 VID/PID 与接口。选脚本时优先选「不钉死接口」的那份。
- **已知边界 2（枚举开销）**：`Get-PnpDevice` 在 189 台设备的机器上实测约 **3.1–3.4s**（瓶颈在 PnP 枚举本身）。已加 **60s 进程内缓存**（`ProbeDetector.DetectAsync`），且只缓存非空结果——空结果不缓存，保证「刚插上探针就 connect」能被 auto 发现。
- 已知边界 3（驱动）：J-Link 在 Windows 上若被 SEGGER 驱动独占，OpenOCD 会报 `LIBUSB_ERROR_NOT_FOUND`，失败诊断里已附「换 AT-Link/CMSIS-DAP 或用 Zadig 切 WinUSB」的说明。

### 4.6 自 provisioning

- OpenOCD 二进制**不进发布产物**（GPL + 平台相关）。
- 解析顺序：`OCD_BIN`/`OCD_SCRIPTS` 环境变量 → PATH（**✅ 已实现**）→ 随包分发 → 用户缓存目录 → 下载 + SHA-256 校验（⏸ 后置）。
- 找不到二进制时给出可操作报错：`ERROR: no openocd executable found. Set OCD_BIN … or install OpenOCD and put it on PATH …`。

---

## 5. 功能设计（工具清单）

图例：**✅ 已实现** ｜ ⏸ 后置（截至 v0.3.13，共 18 个工具已落地）

| 优先级 | 工具 | 用途 | 权限 | 状态 |
|---|---|---|---|---|
| P0 | `connect` | 连接控制通道；无监听时按配置自动拉起后端（`interface_cfg: "auto"` 时自动识别探针） | — | ✅ |
| P0 | `status` | 后端是否运行、版本、target 列表（只读探测，不启动任何东西） | 只读 | ✅ |
| P0 | `show_config` | 查看有效设置与每项来源（`reload=true` 重读磁盘） | — | ✅ |
| **本轮** | `flash_info` | 列出 flash banks（驱动、基址、大小） | 只读 | ✅ |
| **本轮** | `flash_write` | **下载**：烧录镜像（写 + 可选校验 + 可选复位运行） | read_only / allow_flash / 白名单 / 上限 | ✅ |
| **本轮** | `read_memory` | **读指定地址**：按宽度读回目标内存 | 只读 + 体积上限 | ✅ |
| **本轮** | `dump_memory` | **大块读落盘**：`dump_image` 到文件（整片 flash / RAM 快照），返回路径 + 字节数 + sha256 + 前 64 字节 | 目标侧只读；PC 侧落盘走 dump_allowed_paths / dump_max_bytes | ✅ |
| v0.3.5 | `verify_image` | **烧后独立校验**：镜像与目标 flash 比对，不写任何东西（回答「板子里到底是谁」） | 只读 | ✅ |
| v0.3.4 | `backend_log` | 读 ocdup 自拉 openocd 的近期 stdout/stderr（200 行滚动；异步 `Info:`/`Error:` 日志） | 只读 | ✅ |
| v0.3.11 | `identify_chip` | 连上后确认芯片：CPUID（ARM 内核）+ 厂商 DEV_ID（默认 0xE0042000，可参数覆盖），按 ocd.json 的 `chip_id_map` 查家族 | 只读 | ✅ |
| v0.3.13 | `list_variables` | 符号层 L1：列出 .elf 的对象符号（地址/大小/名，可 pattern 过滤；PC 侧读文件） | — | ✅ |
| v0.3.13 | `read_variable` | 按名读变量（ELF 定位 → 控制通道读回；受 memory_read_max_bytes） | 只读 | ✅ |
| v0.3.13 | `write_variable` | 按名写 1/2/4 字节变量（大对象引导用 write_memory） | `read_only` / `allow_memory_write` | ✅ |
| P1 | `halt` / `resume` / `reset` | 执行控制 | `allow_exec` | ✅（v0.3.8；`step` 仍后置） |
| P1 | `write_memory` / `write_register` | 存储器与寄存器写 | `allow_memory_write` | ✅（v0.3.6；`write_register` 仍后置） |
| P1 | `add_breakpoint` / `remove_breakpoint` / `list_breakpoints` | 断点管理 | — | ⏸ |
| P1 | `add_conditional_breakpoint` | 条件断点（服务器侧驱动，见决策 2） | — | ⏸ |
| P1 | `add_watchpoint` / `remove_watchpoint` | 数据观察点 | — | ⏸ |
| P1 | `flash_erase_sector` | 擦除（破坏性，默认关闭） | `allow_flash_erase` | ✅（v0.3.6） |
| P1 | `load_elf` / `read_variable` / `write_variable` / `list_variables` | 按名访问变量（符号层 L1） | 写操作走 memory 闸 | ⏸ |
| P1 | `load_svd` / `read_peripheral_register` / `write_peripheral_register` | 按名访问外设寄存器（L2，位域解码 + reset 比对；写走 memory 闸，read-only access 语义拒写） | 同上 | ✅（v0.4.21） |
| P1 | `run_command` | 原始命令逃生舱（单行；v0.4.12 起写类命令内容分类） | `allow_raw_command`（默认 false） | ✅（v0.3.6；分类 v0.4.12） |
| P2 | `watch_variables` | 不停 CPU 的定时采样 | 只读 | ⏸ |
| P2 | `set_permissions` | 运行时调整权限（补丁入口强制键白名单；reload/重启丢弃） | `allow_set_permissions`（默认 false，人类授权） | ✅（v0.3.12） |
| P2 | 串口协同工具组 | 烧录后走 acccom 验证协议帧 | — | ⏸ |
| P2 | `start_openocd` / `stop_openocd` / `install_openocd` | 后端进程与 provisioning 管理 | — | ⏸ |
| P2 | L3 协议字典工具组 | `ping_req` → 字节模板 + 期望响应 | — | ⏸ |

**本轮验收标准**：模型经 MCP 工具完成「连接 → 烧录 → 校验 → 复位运行」与「按地址读回数据」。

**工具行为细节**：

- `flash_write`（v0.4.13 verify 权威判据）：默认 `verify=true`、`reset=true`。命令序列为 `program <file> [<address>]` → 独立 `verify_image <file> [<address>]`（读回校验是唯一判据，`InterpretVerifyOutput` 解析）→ 结论三段式：`programmed + verified`（reset run 执行）/ `programmed but verify FAILED`（写声称成功但读回不符——最危险，单列）/ `write failed`（写步骤已报错且读回未确认）。program 输出里的错误文本降级为辅助信息（PY32 虚假 AHB 错误不再误跳 reset）；`verify=false` 时结论 `unverified`，reset 门控退回文本嗅探（`ShouldResetRun`）。`.bin` 必须显式给 `address`（`.elf`/`.hex` 自带地址）。实测依据：PY32 整片擦写闭环中 `Failed to write memory` 为 Puya 驱动虚假错误（操作已生效，以 verify 为准）。执行期间 openocd 增量日志行以 progress notification 推回带 progressToken 的调用方（P1-4，见 4.5 进度泵）。
- `read_memory`：`mdb`/`mdh`/`mdw` 对应 `width=8/16/32`；返回 `read N x W-bit from 0xADDR (B bytes):` + 后端原始数据行。
- `dump_memory`：`dump_image {file} {addr} {length}`，输出文件父目录自动创建；返回 dumped 路径/字节数/sha256 前缀/前 64 字节 hex 字；请求字节数与实际不符或后端报错时附 NOTE。
- `status`：只用 300ms 端口探测 + 已连通的 TCL-RPC 查询，**不启动**任何进程；顺带报告通道状态行（P1-5：`channel: connected, idle Ns` / `not connected（下次调用自动重连）`——状态在 version/targets 查询**之前**读取，否则两次成功查询会把「最近活动」水位刷成当下、idle 恒显示 0s，v0.4.18 修正）。
- `identify_chip`：读 `mdw 0xE000ED00 1`（CPUID，ARM 标准位域解码）与 `mdw <dbgmcu_address> 1`（默认 0xE0042000）；DEV_ID 低 12 位在 `chip_id_map`（ocd.json）中按数值匹配；单次读失败不硬失败（该地址因厂商而异），原文带回报告。CPUID 解码是纯函数（`DescribeCpuid`），未知 partno 原样报告不猜。
- `set_permissions`：运行时权限补丁（不改文件）。闸 = `allow_set_permissions`（默认 false）；补丁入口按 `RuntimePatchableKeys` 白名单拒绝越界键；变更来源标 `(runtime)`，`show_config reload=true` 或重启丢弃。
- `run_command`（v0.4.12 内容分类）：`Segment` 按 `;` 切段 → `Classify` 逐段分类 → `Evaluate` 任一段命中即拦。同权类走既有 `PermissionGate` 闸；`program`/`flash write_image`/`load_image`、`dump_image`、`shutdown`/`exit` 无法在 raw 通道可靠校验（路径白名单/大小上限/解析基准），BLOCKED 并重定向专用工具。纯函数，`RawCommandPolicyTests` 27 断言覆盖。

---

## 6. 接口设计

### 6.1 工具约定

- 命名：动词_名词，小写下划线；地址类参数一律 hex 字符串（`'0x20000000'`），避免 int 溢出与歧义（也接受不带 `0x` 前缀的 hex 与 `_` 分隔）。
- schema：每个工具声明参数类型与必填项；枚举类参数（如 `width: 8|16|32`）在 docstring 给出合法值。
- 返回：一律 `str`；成功带上下文，失败带下一步指引。
- 工具输出用英文（面向模型与跨客户端一致性）；代码注释用中文。

### 6.2 项目配置文件（`ocd.json`）

放项目根（或用 `OCD_CONFIG` 指向）；仓库根目录有一份按本机板子配好的实际 `ocd.json`。完整字段：

```json
{
  "interface_cfg": "auto",
  "target_cfg": "target/at32f403axx.cfg",
  "transport": "swd",
  "host": "127.0.0.1",
  "port": 6666,
  "command_timeout_seconds": 60,
  "permissions": {
    "read_only": false,
    "allow_memory_write": true,
    "allow_flash": true,
    "allow_flash_erase": false,
    "allow_exec": true,
    "allow_raw_command": true,
    "flash_allowed_paths": ["build/"],
    "flash_max_bytes": 1048576,
    "memory_read_max_bytes": 4096,
    "dump_allowed_paths": ["dumps/"],
    "dump_max_bytes": 67108864
  }
}
```

- `interface_cfg` 建议用 `"auto"`：连接时按 USB 设备自动识别已插探针（AT-Link / CMSIS-DAP / ST-Link / J-Link，按此优先级）并选择接口脚本；也可显式指定（如 `interface/cmsis-dap.cfg`）。识别结果不写回配置，只在 `connect` / `status` 的 `backend:` 行回报。
- 权限项既可写在 `permissions` 块里（规范写法），也可写在顶层（同名字段）；**同名时嵌套块优先**。
- 未知键被忽略；类型不符或超范围的值降级为默认值，并由 `show_config` 报 `WARNING`（配置文件坏不该让服务器起不来）。
- 相对路径基准 = 本文件所在目录；`interface_cfg`/`target_cfg` 例外（见 4.3）。
- 环境变量：`OCD_CONFIG`（配置文件路径）、`OCD_BIN`（openocd 可执行文件）、`OCD_SCRIPTS`（scripts 目录）。

### 6.3 错误消息约定

| 场景 | 返回形态 |
|---|---|
| 权限拦截 | `BLOCKED: '<op>' is not permitted by the current safety settings ('<setting>'). To allow it, <how>. See show_config.` |
| 参数非法 | `ERROR: address 'hello' is not a hex value. Use a hex string like '0x20000000'.` |
| 文件缺失 | `ERROR: '<resolved>' does not exist. Pass the path of the built image (.elf/.hex/.bin); relative paths are resolved against the ocd.json directory …` |
| 后端未就绪 | `ERROR: nothing is listening on <host>:<port>. Call connect to start the backend …` |
| 后端启动失败 | `ERROR: <binary> did not become ready: <原因>.` + `--- recent openocd output ---`（最后 200 行）+ 下一步建议 |
| 控制通道中断 | `ERROR: the control channel failed while running '<cmd>': … The connection was reset; call connect and retry.` |

---

## 7. 符号层设计（L1 ✅ v0.3.13；L2 ✅ v0.4.21；L3 后置）

三层名字映射，自底向上：**L1（ELF 变量名）已落地**，**L2（SVD 外设寄存器）已落地（v0.4.21）**，L3 后置，接口形态先定：

| 层 | 数据源 | 提供 | 依赖 |
|---|---|---|---|
| L1 变量名 | ELF `.symtab`（`STT_OBJECT` 且地址非 0） | 变量名 → (地址, 字节数) | 自研 ELF 解析（C# 无 pyelftools 等价物） |
| L2 外设寄存器 | CMSIS-SVD | `RCC.CR` → (地址, 位域表) | 自研 SVD 解析（XML） |
| L3 协议字典（自有扩展） | 项目内的协议描述文件（帧名/ID/字段表） | `ping_req` → 字节模板 + 期望响应 | 自研 |

- L3 是 Ocdup 相对参考项目的**主要增量**：把「你的板子的协议」变成一等公民，模型可以直接说「发 ping_req 然后等 ack」而不是拼字节。
- 聚合类型：>4 字节变量按字 dump（watch 工具注明非原子读）；结构体/数组/枚举展开再往后（DWARF 类型解析）。
- 扩展点：符号层接口固定为 `lookup(name) -> (addr, size)` / `find(pattern)`，换数据源不改工具层。

**L1 已落地（v0.3.13）**：`list_variables` / `read_variable` / `write_variable` + ocd.json 的 `elf_path`；`Backend/ElfSymbols` 解析 ELF32 LE 的 .symtab（STT_OBJECT、地址非 0、大小 > 0），坏输入（stripped/大端/ELF64）拒绝并给可操作文案。**L2 已落地（v0.4.21）**：`load_svd` / `read_peripheral_register` / `write_peripheral_register` + ocd.json 的 `svd_path`；`Backend/SvdFile` 自研解析（cluster 展平 + 三种位域写法 + 十六进制/十进制语义修正），写方向与 L1 同闸（allow_memory_write）、read-only access 语义拒写。**L3 仍后置**：无协议字典。

---

## 8. 非目标（NON-GOALS）

- 不做断点/观察点/条件断点（M-bp 后置）。执行控制 halt/resume/reset 已在 v0.3.8 落地（Q9 真机验证：运行态直读 flash 可行；halt 用于可靠读写现场）。
- 不做通用逻辑分析仪/示波器驱动（那是另一条产品线）。
- 不做 GUI 调试器本体。
- 不替代 OpenOCD / probe-rs / J-Link 官方工具；Ocdup 只是它们的 MCP 门面。
- 不封装编译工具链（make/cmake 由客户端已有的 shell 工具执行；Ocdup 只消费构建产物）。
- 不做多目标并发调试（单连接模型，见 C6）。

---

## 9. 约束与不变量（CONSTRAINTS）

| # | 约束 | 性质 | 状态 |
|---|---|---|---|
| C1 | 控制通道请求串行化，响应与异步日志可区分 | 不变量 | ✅ 单测覆盖 |
| C2 | 变更类工具权限检查前置到第一行 | 不变量 | ✅ 烧录路径已覆盖 |
| C3 | 拦截报错必须带解除指引 | 不变量 | ✅ 单测 + e2e 覆盖 |
| C4 | 芯片/探针/路径零硬编码，全部外置 | 不变量 | ✅ |
| C5 | 退出即回收自有子进程，不动外部实例 | 不变量 | ✅ 已实现，待真机复验 |
| C6 | 单连接、单目标模型；多客户端并发共享同一后端状态（可接受，需文档明示） | 边界 | ✅ 文档明示 |
| C7 | 第三方二进制不进产物，下载必须校验 SHA-256 | 固定策略 | ⏸（下载式 provisioning 后置） |
| C8 | 仅监听 localhost，无认证（stdio 场景不暴露网络面） | 固定策略 | ✅ `bindto` + 默认 host |
| C9 | 语言已定 **C# / .NET 8**，接口按官方 MCP SDK 实现 | 已决 | ✅ |

---

## 10. 技术选型

| 决策点 | 结论 | 理由 |
|---|---|---|
| 语言/框架 | **C# / .NET 8 + 官方 MCP SDK**（`ModelContextProtocol` 2.2.0、`Microsoft.Extensions.Hosting` 8.0.1） | 与 acccom（C#）同运行时、后期可合并部署 |
| 传输 | stdio（本地）；预留 Streamable HTTP | 远程/云端场景再开 HTTP |
| 分发形态 | 独立 MCP server（任何客户端可用） | 可开源、可分发、与 acccom 对等；DSH 插件形态留后期做深度集成 |
| 配置格式 | JSON（`ocd.json`） | 与参考实现一致，错误信息更简单 |
| ELF/SVD 解析 | 后置（届时自研解析器） | 本轮不涉符号层，避免过早引入依赖 |
| 测试框架 | xUnit + `Microsoft.NET.Test.Sdk`（用假 `TcpListener` 做控制通道） | 无需硬件即可回归权限与协议层 |

---

## 11. 项目结构（当前实际形态）

```
Ocdup/
├── docs/design-plan.md              ← 本文档
├── cfgs/                            ← 板级手写 target cfg（target_presets 绝对路径引用；头部注释=排障记录）
│   ├── target_at32f423_key.cfg      #   钥匙板：connect_assert_srst 治 deepsleep 睡死
│   └── target_py32f002a_lock.cfg    #   锁芯板：最小通用 cortex_m 调试 cfg（烧录走 Keil）
├── src/Ocdup/                       # MCP 服务器（.NET 8 控制台，stdio）+ watch/rtt/doctor CLI
│   ├── Program.cs                   # watch/rtt/doctor 子命令分发 + Host 构建 + MCP 服务器/工具注册
│   ├── Cli/WatchCli.cs              # watch 采样 CLI（M-watch）：选项解析/对齐取值/渲染均为纯函数
│   ├── Cli/RttCli.cs                # rtt 实时读取 CLI（v0.4.19）：fork 命令面 + rtt server TCP 直读
│   ├── Cli/BurnCli.cs               # burn 量产烧录 CLI（v0.4.20）：复用 FlashWriteCoreAsync + exit code 分流
│   ├── Cli/DoctorCli.cs             # doctor 环境体检 CLI（P1-6）：五项只读检查 + 可操作修复指引
│   ├── Backend/TclRpcClient.cs      # 控制通道客户端（6666 · 0x1a 分帧 · capture{…} · 串行化）
│   ├── Backend/BackendSession.cs    # 会话：确保后端在跑 + 复用/丢弃控制通道
│   ├── Backend/TrafficLog.cs        # 流量镜像（mcp-traffic.jsonl，2MB 轮转；面板数据源）
│   ├── Backend/ElfSymbols.cs        # 符号层 L1：ELF32 .symtab 解析（纯函数，无 IO）
│   ├── Backend/SvdFile.cs           # 符号层 L2（v0.4.21）：CMSIS-SVD 解析（cluster 展平/位域/命令构造，纯函数）
│   ├── Backend/Tcl.cs               # TCL 字面量引用（大括号 / 转义引号）
│   ├── Launcher/OpenOcdLauncher.cs  # 子进程生命周期/端口探测/输出抽干/退出回收/Job Object
│   ├── Launcher/ProbeDetector.cs    # auto 模式：USB 枚举探针识别（AT-Link/CMSIS-DAP/ST-Link/J-Link）
│   ├── Config/OcdConfig.cs          # 分层配置合并 + 来源追踪 + 路径解析
│   ├── Config/ConfigStore.cs        # 配置单一入口（可 Reload）
│   ├── Config/PermissionGate.cs     # 权限闸（前置检查 + BLOCKED 文案）
│   ├── Tools/BackendTools.cs        # connect / status / backend_log / run_command
│   ├── Tools/ConfigTools.cs         # show_config / set_permissions
│   ├── Tools/SymbolTools.cs         # list_variables / read_variable / write_variable（L1）
│   ├── Tools/SvdTools.cs            # load_svd / read_peripheral_register / write_peripheral_register（L2，v0.4.21）
│   ├── Tools/ExecTools.cs           # halt / resume / reset
│   ├── Tools/FlashTools.cs          # flash_info / flash_write / verify_image / flash_erase_sector
│   ├── Tools/MemoryTools.cs         # read_memory / write_memory / dump_memory
│   ├── Tools/Parsing.cs             # 地址解析/渲染/后端输出解析
│   └── Ocdup.csproj
├── tests/Ocdup.Tests/               # 假 socket 分帧/串行/超时 + 权限矩阵 + 配置加载 + 探针识别
├── tools/                           # e2e_fake_backend.py（无硬件回归）+ dsh-ocdup-panel/（DSH 侧边栏面板）
├── plugins/                         # ocdup-zcode（ZCode 本地插件市场：ocdup-flash skill + 快捷命令）
├── publish.cmd                      # 一键发布（防锁检测）
├── .github/workflows/ci.yml         # CI（build + 单测 + e2e）
├── ocd.json.example
├── README.md
├── .gitignore
└── Ocdup.sln
```

后置目录（到时再加）：`Backend/Symbols/`（L1/L2）、`Provision/OpenOcdProvisioner.cs`（下载 + SHA-256）。

---

## 12. 实施计划

### 阶段一（本轮）：烧录 + 读内存

| 里程碑 | 内容 | 完成判据（验收） | 状态 |
|---|---|---|---|
| S1 骨架 | 项目结构、MCP stdio 跑通、工具注册 | 客户端能列出工具 | ✅ |
| S2 控制层 | `TclRpcClient`（分帧/串行/超时）+ `OpenOcdLauncher`（拉起/探测/抽干/回收）+ `BackendSession` | 假 socket 单测通过；connect/status 可用 | ✅ |
| S3 烧录与读内存 | `flash_info` / `flash_write` / `read_memory` + 权限闸（含路径白名单与大小上限） | 权限矩阵单测全绿；假后端 e2e 跑通完整链路 | ✅ |
| S4 真机联调 | AT-Link + 目标板：连接 → 烧录 → 校验 → 复位运行 → 按地址读回 | 记入测试日志：芯片、探针、ocd.json、OpenOCD 版本 | ✅（2026-10，AT32F403AVGT7 + CMSIS-DAP v2，详见第 17 节） |
| S5 打包与注册 | `dotnet publish` 产物 + 各客户端注册矩阵 + README | 换一台机器 10 分钟内跑通 | 🟡 publish.cmd + CI + DSH/ZCode 注册已就位；v0.4.15 起换机第一步用 `ocdup doctor` 环境体检（五项 ✅/❌ + 如何修）；换机验收待做 |

### 后置里程碑（本轮不做）

| 里程碑 | 内容 |
|---|---|
| M-exec | ~~halt/resume/step/reset 执行控制~~ halt/resume/reset 已落地（v0.3.8）；`step` 仍后置 |
| M-bp | 断点/观察点/条件断点（服务器侧驱动，见决策 2） |
| M-sym | 符号层 ~~L1（ELF）~~ ✅ v0.3.13 / ~~L2（SVD）~~ ✅ v0.4.21 + 按名读写（均落地；L3 协议字典仍后置） |
| M-perm | ~~`set_permissions` + `OCD_READONLY` 环境变量 + `run_command` 逃生舱~~ ✅ 全部落地（run_command v0.3.6；其余 v0.3.12） |
| M-prov | provisioning：缓存目录 + 下载 + SHA-256 校验 + `install_openocd` |
| M-l3 | L3 协议字典（帧名/模板/期望响应） |
| M-serial | ~~与 acccom 的串口协同~~ 方案 A 已落地：会话层协同 + 插件 `/flash-verify` 命令（0.2.6）；L3 字典化的帧自动比对仍后置 |
| M-watch | ~~`watch_variables` 定时采样（独立 CLI 形态）~~ ✅ v0.4.3 `ocdup watch` 子命令（--format csv 重定向画曲线、--halt 一致快照；MCP 工具形态刻意不做——长时运行占死 C1 串行通道） |
| M-rtt | ~~SEGGER RTT 实时读取~~ ✅ v0.4.19 `ocdup rtt` 子命令（fork 命令面实证：setup/start/stop/channels + rtt server；数据走 rtt server 本机 TCP 直读原始字节；--info/--lines/--bytes/--seconds；MCP 工具形态刻意不做——同 watch 的 C1 理由） |
| M-burn | ~~量产烧录 CLI~~ ✅ v0.4.20 `ocdup burn` 子命令（与 flash_write 同一编排核心 `FlashWriteCoreAsync`——闸与读回判据单一真源；exit code 分流 0/1/2/3；Ctrl+C 刻意不接取消——写相中断 = 半截固件） |

---

## 13. 测试方案

| 层 | 方法 | 现状 |
|---|---|---|
| 单元 | xUnit + **假 `TcpListener`**（脚本化响应） | 299 个用例：控制通道（capture 包装 / `0x1a` 分帧 / 跨 TCP 分段组装 / 并发串行化 / 读超时 / 连接失败）、权限矩阵、配置加载（嵌套块 / 顶层平铺 / 来源追踪 / 坏输入降级 / 路径基准）、探针识别（优先级 / 同种去重 / 无关设备忽略 / 空行容错）、TCL 引用、命令行构造、地址解析、流量日志轮转、CPUID 解码与 chip_id_map 匹配、运行时权限补丁与 OCD_RDONLY、ELF32 解析（对象符号筛选 / 坏输入拒绝）、SVD 解析（cluster 展平 / 三种位域写法 / 十六进制-十进制语义 / 按名解析与建议）、reg 输出解析、target_presets 解析与切换（ResolvePreset / TargetMismatch / WithTargetChange）、watch CLI 选项/取值/渲染、run_command 内容分类（分类白名单 / `;` 走私 / erase_check 不误杀）、flash_write 三段式结论与 reset 门控、doctor 渲染与退出码、进度泵差分切片与守卫（含「无自拉后端时零通知」的行为级断言）、断链重放白名单与通道状态、rtt CLI（选项/命令构造/setup 判定/控制块日志扫描/限流）、burn CLI（选项含 .bin 早拒/退出码映射） |
| 端到端 | `tools/e2e_fake_backend.py`：Python 假 TCL-RPC 后端 + 真实 MCP stdio 会话（无需硬件） | 已验证：23 个工具注册（含 v0.4.6 的 disconnect）、烧录/读/写/执行控制（halt/reset halt 回读 PC/LR/SP/xPSR）/逃生舱/芯片识别与 target 失配/set_target 切换（list/未知名/大小写/外部实例警告）/运行时权限/按名读写全链路、format=json 与 verify_image、dump 白名单与上限拦截、read_only 全拦、`reload` 生效与 runtime 补丁丢弃、无 openocd 与坏二进制时的诊断输出、run_command 内容分类（mdb 透传 / program BLOCKED 重定向）、断链重放（假后端断开连接后只读自动重连重放）、`ocdup rtt` CLI 全流程（假 RTT TCP 数据面收满 --lines 即停 + 命令序列断言 + --info）、`ocdup burn` CLI 三态（verified exit 0 / arm 回读失配 VERIFY FAILED exit 1 / 白名单外 BLOCKED exit 2）、SVD 按名读写（load → 大小写不敏感读 + 位域解码 + reset 比对 → 簇寄存器 mdh 选型 → mww 写 → read-only 拒写 → 未知名建议；19 条链路） |
| 集成 | 真实探针 + 开发板（S4） | ✅ 已通过（2026-10）；每次记入测试日志（芯片、探针、ocd.json、OpenOCD 版本） |
| CI | build → test → e2e（windows-latest） | ✅ `.github/workflows/ci.yml`（v0.3.9 建） |

回归重点：**权限矩阵逐格断言** 与 **错误文案可操作性**（C2/C3）——它们最容易被后续改动悄悄破坏。

---

## 14. 风险与缓解

| 风险 | 影响 | 缓解 |
|---|---|---|
| 真机联调环境不可得（S4 卡壳） | 硬件问题暴露晚 | 假后端 e2e 已覆盖协议与权限层；真机联调排到固定时段一次完成 |
| `read_memory` 在目标运行时读失败（缺 halt） | 「按地址读回」在部分芯片/场景不可用 | 本轮先只做读取路径；真机一复现就补 M-exec（halt/resume） |
| `program` 命令语义随 OpenOCD 版本变化 | 烧录行为不一致 | 命令行固定形态 + S4 记录版本；必要时改用 `flash write_image` + `verify_image` 组合 |
| OpenOCD 发布包 scripts 布局差异 | 找不到 `-s` 目录 | 已实现三种候选路径（share/openocd/scripts、xpack、bin/scripts）+ `OCD_SCRIPTS` 覆盖 |
| 第三方二进制供应链（下载式 provisioning） | 安全 | 后置时严格执行 C7：版本钉死 + SHA-256 校验 + 官方 Release 源 |
| 多客户端并发把后端状态搞乱（C6） | 难复现的 bug | 文档明示单目标模型；`status` 工具暴露当前状态便于定位 |
| 大固件烧录超过命令超时 | 误报失败 | `command_timeout_seconds` 可配置（默认 60s），失败文案提示该设置 |

---

## 15. 分发与发布

1. **单机一键**（S5）：`dotnet publish -c Release` → 把产物注册到各客户端（Claude Code / DSH 的 cordis.patch.yml `mcp-client` 条目 / Cursor 等）；DSH 侧注册片段与现有 acccom 条目同款格式。
2. **依赖前置**：目标机器需有 OpenOCD（xPack 或发行版），用 `OCD_BIN`/`OCD_SCRIPTS` 或 PATH 提供；S4 完成后在 README 写清各探针的 scripts 名。
3. **NuGet / MCP Registry**：稳定后再考虑（与 C7 的「二进制不进产物」策略一致）。
4. **DSH 深度集成（后期）**：如需侧栏面板/设置卡，再以 cordis 插件形态包一层（参考 dsh-serial-panel 的结构）。

---

## 16. 开放问题（OPEN QUESTIONS）

| # | 问题 | 阻塞 | 状态 |
|---|---|---|---|
| Q1 | 语言选型 | — | ✅ 关闭：C# / .NET 8 |
| Q2 | 探针与目标芯片 | — | ✅ 关闭：探针 = AT-Link（CMSIS-DAP）；目标芯片 = **AT32F403AVGT7**（LQFP100 / 1MB flash，`target/at32f403axx.cfg`，flash 大小自动探测）；备用探针 ST-Link / J-Link / DAPLink |
| Q3 | 后端：OpenOCD vs probe-rs | 后期里程碑 | ✅ 阶段一采用 OpenOCD（AT32 生态成熟） |
| Q4 | 是否随包分发 Windows 版 OpenOCD（git/zip 形态）还是纯下载 | 不阻塞，影响 M-prov | ⏳ 待定 |
| Q5 | L3 协议字典覆盖哪些协议（私有帧？Modbus？CAN？） | M-l3 | ⏳ 待定 |
| Q6 | 与 acccom 的分工边界：Ocdup 只发「去烧录/去验证」指令，帧交互仍走 acccom？ | M-serial | ✅ 关闭（**方案 A：会话层协同**）：两个 MCP server 挂在同一会话由 AI 编排，帧交互全走 acccom，Ocdup 侧零代码；流程固化为插件 `/flash-verify` 命令（0.2.6） |
| Q7 | 相对路径基准 = ocd.json 所在目录 | — | ✅ 关闭并已实现 |
| Q8 | 单目标（C6）是否满足？多探针场景是否需要多连接 | M-serial 等 | 📋 方案已定（见下方「Q8 实施方案」，v0.4.22 定案）：单板场景 C6 满足；多板并调按注册表方案实施，待排期 |
| **Q9** | `read_memory` 是否需要先 halt？ | S4 | ✅ 真机验证：运行态直读 flash（0x08000000 向量表）成功，无需 halt；RAM 变量的「一致性快照」场景留待 M-exec |
| **Q10** | 目标芯片实际可用的 OpenOCD 版本与 scripts 目录名 | — | ✅ 关闭：Artery 定制版 atopenocd（本机安装，路径不入库，0.11.0+dev 2024-08-05）+ `interface_cfg: "auto"`（本机探针 0D28:0204 解析为 `interface/cmsis-dap.cfg`）+ `target/at32f403axx.cfg`，实机烧录通过；**注意 `atlink_dap_v2.cfg` 在本机这根公版 DAPLink 探针上会 `unable to find a matching CMSIS-DAP device`（见 4.5 已知边界 1）**；非 AT 芯片走主线版（EIDE xPack 或 `openocd-20240916.7z` 解压）换 `OCD_BIN`/`OCD_SCRIPTS` 即可 |
| **Q11** | 镜像路径是否也接受相对 CWD（当前只认 ocd.json 目录） | 不阻塞 | ⏳ 可后置 |

### Q8 实施方案：多后端注册表（v0.4.22 定案，实施待排期）

**动机**：Q8 原问题「单目标（C6）是否满足？多探针场景是否需要多连接」——当前答案：
单板场景完全满足（探针本身独占，多数场景一块板一个会话）；**多板并调**（如双板对拷
通信调试、产线多工位一机多探针）才需要。以下方案把改动收敛在会话层，工具层只加一个
可选参数。

**1. 配置形态**（向后兼容：没有 `backends` 键 = 现状单后端，零行为变化）：

```jsonc
{
  "interface_cfg": "auto", "target_cfg": "target/at32f403axx.cfg", "port": 6666,  // 默认后端（现有键不动）
  "backends": {
    "left":  { "port": 6666, "target_cfg": "target/at32f403axx.cfg" },   // 覆盖默认值，未给的键继承顶层
    "right": { "port": 6667, "interface_cfg": "interface/cmsis-dap.cfg" }
  }
}
```

- 标签即身份（`left`/`right`），每个后端一份**覆盖式**配置（继承顶层键）；
- `OcdConfig` 加 `IReadOnlyDictionary<string, OcdConfig> Backends`——每个条目就是一份
  完整的 `OcdConfig`（现有 WithRuntimeChanges/WithTargetChange/ResolvePath 逻辑原样复用，
  来源标注 `<tag> (backends)`）。

**2. 会话层**：`BackendSession` 单例 → `BackendSessions` 注册表（`ConcurrentDictionary<string,
BackendSession>`，空标签 = 默认实例 = 现在的 `Instance`）。心跳/断链重放/通道串行闸都是
实例内部状态，**每标签一套，互不干扰**——这正是它当初收在类里的红利。launcher/traffic 的
endpoint 字段已带 host:port，天然可区分。

**3. 工具面**：23 个工具各加一个可选参数 `backend`（默认空 = 默认后端）；工具开头一行
`var cfg = ConfigStore.Instance.Current.ResolveBackend(backend)`（不存在标签 → ERROR 带可用
标签清单，C3）。`run_command` 内容分类、权限闸全部在各自 cfg 上跑——**不同后端可以有不同
权限**（左板只读分析、右板可烧），这是注册表最值钱的一点。

**4. 联动点**：`status`/`show_config` 列出全部后端与各自通道状态；doctor 增加逐后端端口
检查；面板流量按 endpoint 已可区分（可选：工具参数透传 tag 进 portTag 字段）；SKILL 补
「多板会话」一节。**探针独占性**照旧成立——不同后端必须是不同探针/不同端口，同一探针
起两份 openocd 仍会打架（文档明示）。

**5. 测试与验收标准**：

- 单测（预估 +25）：配置解析（继承/覆盖/坏标签降级）、ResolveBackend（存在/不存在/大小写）、
  注册表并发获取同标签同实例、跨标签会话隔离（A 标签断链不影响 B）；
- e2e（+2 链路）：双假后端（29171/29172）——`flash_info(backend="right")` 路由到 29172、
  默认参数仍走 29171、`write_memory(backend="left")` 在左后端只读配置下 BLOCKED 而右后端
  放行（分后端权限）；
- **验收**：现有单后端用法零回归（全量测试 + 19 条链路不改动照绿）；双后端链路各走各的
  命令序列；真机验收 = 双探针双板并调一次记录。

**6. 工程量与风险**：预计 1 个完整迭代（配置 0.5d + 注册表 0.5d + 23 工具参数过一遍
0.5d + 测试/e2e/文档 1d）。风险：工具签名变更触发 MCP 客户端缓存（schema 变化，客户端需
重连刷新 tools/list）；面板 v1.3.x 的「后端」卡语义需要跟进（单卡 → 按标签分组）——刻意
放到二期。

---

## 17. 交付结论与下一步（HANDOFF）

**当前状态（v0.4.23）**：**S4 真机联调已通过** ✅（AT32F403AVGT7 + CMSIS-DAP v2 探针 + atopenocd：connect → flash_info → flash_write S300plus.hex 139077 字节，擦 25 扇区、写+校验 Verified OK、reset run、运行态读回向量表 0x08000000 = `20019d28 080002ad …`）。**23 个 MCP 工具 + 4 个 CLI 子命令（`ocdup watch` 采样 / `ocdup doctor` 环境体检 / `ocdup rtt` SEGGER RTT 实时读取 / `ocdup burn` 量产烧录）全部落地**（含符号层 L1 按名读写、**L2 SVD 按名外设寄存器与位域解码（v0.4.21）**、target 注册表与 set_target 切换、halt 核心寄存器现场、disconnect 探针让渡）；v0.4.12–v0.4.16 落地评审定案的安全/正确性加固（run_command 内容分类、flash_write 独立读回权威判据、断链只读自动重放 + 空闲心跳、执行期 progress notification）；v0.4.18 复查修正三处（CLI 子命令计数纠错、`status` 通道状态读取时机、进度泵冷启动守卫下沉）；**v0.4.19–v0.4.22 完成评审 P2 清单**：rtt CLI（fork 命令面实证 + rtt server TCP 数据面）、burn 量产 CLI（与 flash_write 同一编排核心 + exit code 分流）、SVD 外设寄存器层（自研解析 + 位域解码 + 语义拒写）、recipes 四份实战手册（docs/recipes/）；**v0.4.23 P1-7 实测收口**（SDK 2.2.0 即最新，含 prerelease；Q8 多后端实施方案定案待排期）。299 个单元测试全绿 + e2e 19 条链路无硬件回归 + CI 就绪。烧录后端升级为自编译 **atopenocd-py32**（AT32 + PY32 双支持，见 docs/openocd-puya-build.md），PY32F002A 整片烧录与 AT32F403A 整 bank 回归均已真机闭环。S5 的 publish.cmd / CI / DSH 与 ZCode 注册已完成，换机第一步用 `ocdup doctor`，剩「换一台机器 10 分钟跑通」的换机验收。**待真机验收清单**：`ocdup rtt` 板上控制块搜索与数据流（命令面已实证）；`ocdup burn` 治具全流程；真实厂商 SVD（AT32/ST 全量文件）解析健壮性。

**建议的下一步顺序**：

1. ~~跑 S4~~ ✅ 已完成（2026-10：AT32F403AVGT7 + CMSIS-DAP v2，完整闭环验收通过）；
2. ~~运行态读失败补 M-exec~~ ✅ v0.3.8 落地 halt/resume/reset（Q9 真机验证运行态直读可行；halt 用于可靠读写现场）；
3. ~~S5 打包与客户端注册~~ 🟡 主体完成（publish.cmd + CI + DSH 双 profile + ZCode MCP/插件）；换机第一步 `ocdup doctor`（v0.4.15）；剩换机 10 分钟验收；
4. ~~评审 P0/P1/P2 清单~~ ✅ v0.4.12–v0.4.22 全部收口（P0-1/P0-2/P1-4/P1-5/P1-6 + rtt/burn/SVD/recipes；P1-7 于 v0.4.23 实测收口）；
5. **待真机验收**（插板即做，见上清单）；
6. 之后的大项：**Q8 多后端注册表实施**（方案与验收标准已在第 16 节定案，约 1 个迭代）、断点/观察点（M-bp，`step` 顺路）、provisioning（M-prov）、L3 协议字典（M-l3）；
7. 维护节奏（P1-7 记录项）：ModelContextProtocol SDK 钉在 2.2.0（Core 1.4.0）——2026-10-09 核查即 NuGet 最新（含 prerelease），出新版时全量单测 + e2e 回归，重点看 tool description 长度上限、stdio 行为、progress notification（`Tools/FlashProgress.cs` 是 progress API 的现成参考实现：`Params.Meta` 为裸 JsonObject、progressToken 手工解析成 `ProgressToken(long|String)`、`McpJsonUtilities.DefaultOptions` 发送）；Job Object 孤儿兜底是 Windows-only（`OpenOcdLauncher.Job`），Linux 部署需补 `Process.Kill(entireProcessTree)` + SIGTERM 处理——当前 Windows 单机部署，记录在案。

本文档随项目演进维护；每关闭一个重要开放问题就相应升级文档状态。






