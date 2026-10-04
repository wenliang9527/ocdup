# Ocdup 设计计划方案

| 项目 | 内容 |
|---|---|
| 文档版本 | v0.3.13 |
| 日期 | 2026-10-04 |
| 状态 | **阶段一完成**：控制层 + 烧录 + 读内存 + 执行控制 + 逃生舱 + 运行时权限 + 符号层 L1 均已实现并验证（18 个工具，S4 真机通过）；断点/观察点/L2(SVD)/L3 后置 |
| 读者 | 项目负责人（硬件/嵌入式）、后续实现者（Agent 或工程师） |
| 上游参考 | [microhenrio/openocd-mcp](https://github.com/microhenrio/openocd-mcp) v0.4.5 架构（已逐文件审查） |

---

## 变更记录

| 版本 | 变更 |
|---|---|
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
4. **逃生舱**：`run_command` 原始命令通道挂在 `allow_raw_command` 后（v0.3.6 已实现，单行命令）。

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
| `allow_raw_command` | true | run_command | ✅ 已实现（v0.3.6） |
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

### 4.5 进程生命周期

实现（`Launcher/OpenOcdLauncher.cs`）：

- **启动**：拼命令行 → `Process.Start`（用 `ArgumentList`，含空格的参数自动引用）→ **后台两个 Task 抽干 stdout/stderr**（否则管道填满会把 OpenOCD 挂死）→ 轮询端口等就绪（超时 15s）。
- **重复启动防护**：先探测端口；已在运行（无论是谁启动的）就不重复拉起。
- **启动失败诊断**：进程提前退出或超时，回报**近期输出**（rolling buffer 200 行）+ 一句「下一步怎么做」。
- **退出**：`AppDomain.ProcessExit` 钩子 + `Dispose` 回收自己启动的进程（`Kill(entireProcessTree: true)`）；超时也回收，不留半死实例占着探针（C5）。
- **输出缓冲可读**（v0.3.4）：抽干的 stdout/stderr 除启动失败诊断外，还经 `RecentOutput(maxLines)`（纯函数 `TailLines` 可单测）暴露给 `backend_log` 工具——TCL-RPC 是请求/响应通道，target reset/halt 这类异步日志只在进程输出里。
- 命令行固定形态：`-s <scripts>` `-c bindto <host>` `-f <interface_cfg>` `[-c "transport select <t>"]` `[-f <target_cfg>]` `-c gdb_port disabled` `-c telnet_port disabled` `-c tcl_port <port>`——用 `disabled` **真正关闭** gdb/telnet 服务，TCL-RPC 是唯一对外端口；`bindto` 保证只监听 localhost（C8）。

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
| P1 | `load_svd` / `read_peripheral_register` / … | 按名访问外设寄存器（L2，位域解码） | 同上 | ⏸ |
| P1 | `run_command` | 原始命令逃生舱（单行） | `allow_raw_command` | ✅（v0.3.6） |
| P2 | `watch_variables` | 不停 CPU 的定时采样 | 只读 | ⏸ |
| P2 | `set_permissions` | 运行时调整权限（补丁入口强制键白名单；reload/重启丢弃） | `allow_set_permissions`（默认 false，人类授权） | ✅（v0.3.12） |
| P2 | 串口协同工具组 | 烧录后走 acccom 验证协议帧 | — | ⏸ |
| P2 | `start_openocd` / `stop_openocd` / `install_openocd` | 后端进程与 provisioning 管理 | — | ⏸ |
| P2 | L3 协议字典工具组 | `ping_req` → 字节模板 + 期望响应 | — | ⏸ |

**本轮验收标准**：模型经 MCP 工具完成「连接 → 烧录 → 校验 → 复位运行」与「按地址读回数据」。

**工具行为细节**：

- `flash_write`：默认 `verify=true`、`reset=true`。命令序列为 `program <file> [verify] [<address>]`，成功后若 `reset=true` 再执行 `reset run`。若后端输出含 `Error:`/`failed`（保守判定），跳过 `reset run` 并提示失败可能；`.bin` 必须显式给 `address`（`.elf`/`.hex` 自带地址）。
- `read_memory`：`mdb`/`mdh`/`mdw` 对应 `width=8/16/32`；返回 `read N x W-bit from 0xADDR (B bytes):` + 后端原始数据行。
- `dump_memory`：`dump_image {file} {addr} {length}`，输出文件父目录自动创建；返回 dumped 路径/字节数/sha256 前缀/前 64 字节 hex 字；请求字节数与实际不符或后端报错时附 NOTE。
- `status`：只用 300ms 端口探测 + 已连通的 TCL-RPC 查询，**不启动**任何进程。
- `identify_chip`：读 `mdw 0xE000ED00 1`（CPUID，ARM 标准位域解码）与 `mdw <dbgmcu_address> 1`（默认 0xE0042000）；DEV_ID 低 12 位在 `chip_id_map`（ocd.json）中按数值匹配；单次读失败不硬失败（该地址因厂商而异），原文带回报告。CPUID 解码是纯函数（`DescribeCpuid`），未知 partno 原样报告不猜。
- `set_permissions`：运行时权限补丁（不改文件）。闸 = `allow_set_permissions`（默认 false）；补丁入口按 `RuntimePatchableKeys` 白名单拒绝越界键；变更来源标 `(runtime)`，`show_config reload=true` 或重启丢弃。

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

## 7. 符号层设计（L1 ✅ v0.3.13；L2/L3 后置）

三层名字映射，自底向上：**L1（ELF 变量名）已落地**，L2/L3 后置，接口形态先定：

| 层 | 数据源 | 提供 | 依赖 |
|---|---|---|---|
| L1 变量名 | ELF `.symtab`（`STT_OBJECT` 且地址非 0） | 变量名 → (地址, 字节数) | 自研 ELF 解析（C# 无 pyelftools 等价物） |
| L2 外设寄存器 | CMSIS-SVD | `RCC.CR` → (地址, 位域表) | 自研 SVD 解析（XML） |
| L3 协议字典（自有扩展） | 项目内的协议描述文件（帧名/ID/字段表） | `ping_req` → 字节模板 + 期望响应 | 自研 |

- L3 是 Ocdup 相对参考项目的**主要增量**：把「你的板子的协议」变成一等公民，模型可以直接说「发 ping_req 然后等 ack」而不是拼字节。
- 聚合类型：>4 字节变量按字 dump（watch 工具注明非原子读）；结构体/数组/枚举展开再往后（DWARF 类型解析）。
- 扩展点：符号层接口固定为 `lookup(name) -> (addr, size)` / `find(pattern)`，换数据源不改工具层。

**L1 已落地（v0.3.13）**：`list_variables` / `read_variable` / `write_variable` + ocd.json 的 `elf_path`；`Backend/ElfSymbols` 解析 ELF32 LE 的 .symtab（STT_OBJECT、地址非 0、大小 > 0），坏输入（stripped/大端/ELF64）拒绝并给可操作文案。**L2(SVD)/L3 仍后置**：本轮无按名外设寄存器与协议字典。

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
├── src/Ocdup/                       # MCP 服务器（.NET 8 控制台，stdio）
│   ├── Program.cs                   # Host 构建 + MCP 服务器/工具注册
│   ├── Backend/TclRpcClient.cs      # 控制通道客户端（6666 · 0x1a 分帧 · capture{…} · 串行化）
│   ├── Backend/BackendSession.cs    # 会话：确保后端在跑 + 复用/丢弃控制通道
│   ├── Backend/TrafficLog.cs        # 流量镜像（mcp-traffic.jsonl，2MB 轮转；面板数据源）
│   ├── Backend/ElfSymbols.cs        # 符号层 L1：ELF32 .symtab 解析（纯函数，无 IO）
│   ├── Backend/Tcl.cs               # TCL 字面量引用（大括号 / 转义引号）
│   ├── Launcher/OpenOcdLauncher.cs  # 子进程生命周期/端口探测/输出抽干/退出回收/Job Object
│   ├── Launcher/ProbeDetector.cs    # auto 模式：USB 枚举探针识别（AT-Link/CMSIS-DAP/ST-Link/J-Link）
│   ├── Config/OcdConfig.cs          # 分层配置合并 + 来源追踪 + 路径解析
│   ├── Config/ConfigStore.cs        # 配置单一入口（可 Reload）
│   ├── Config/PermissionGate.cs     # 权限闸（前置检查 + BLOCKED 文案）
│   ├── Tools/BackendTools.cs        # connect / status / backend_log / run_command
│   ├── Tools/ConfigTools.cs         # show_config / set_permissions
│   ├── Tools/SymbolTools.cs         # list_variables / read_variable / write_variable（L1）
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
| S5 打包与注册 | `dotnet publish` 产物 + 各客户端注册矩阵 + README | 换一台机器 10 分钟内跑通 | 🟡 publish.cmd + CI + DSH/ZCode 注册已就位；换机验收待做 |

### 后置里程碑（本轮不做）

| 里程碑 | 内容 |
|---|---|
| M-exec | ~~halt/resume/step/reset 执行控制~~ halt/resume/reset 已落地（v0.3.8）；`step` 仍后置 |
| M-bp | 断点/观察点/条件断点（服务器侧驱动，见决策 2） |
| M-sym | 符号层 L1（ELF）/ L2（SVD）+ 按名读写 |
| M-perm | ~~`set_permissions` + `OCD_READONLY` 环境变量 + `run_command` 逃生舱~~ ✅ 全部落地（run_command v0.3.6；其余 v0.3.12） |
| M-prov | provisioning：缓存目录 + 下载 + SHA-256 校验 + `install_openocd` |
| M-l3 | L3 协议字典（帧名/模板/期望响应） |
| M-serial | ~~与 acccom 的串口协同~~ 方案 A 已落地：会话层协同 + 插件 `/flash-verify` 命令（0.2.6）；L3 字典化的帧自动比对仍后置 |
| M-watch | `watch_variables` 定时采样（独立 CLI 形态） |

---

## 13. 测试方案

| 层 | 方法 | 现状 |
|---|---|---|
| 单元 | xUnit + **假 `TcpListener`**（脚本化响应） | 114 个用例：控制通道（capture 包装 / `0x1a` 分帧 / 跨 TCP 分段组装 / 并发串行化 / 读超时 / 连接失败）、权限矩阵、配置加载（嵌套块 / 顶层平铺 / 来源追踪 / 坏输入降级 / 路径基准）、探针识别（优先级 / 同种去重 / 无关设备忽略 / 空行容错）、TCL 引用、命令行构造、地址解析、流量日志轮转、CPUID 解码与 chip_id_map 匹配、运行时权限补丁与 OCD_RDONLY、ELF32 解析（对象符号筛选 / 坏输入拒绝） |
| 端到端 | `tools/e2e_fake_backend.py`：Python 假 TCL-RPC 后端 + 真实 MCP stdio 会话（无需硬件） | 已验证：18 个工具注册、烧录/读/写/执行控制/逃生舱/芯片识别/运行时权限/按名读写全链路、format=json 与 verify_image、dump 白名单与上限拦截、read_only 全拦、`reload` 生效与 runtime 补丁丢弃、无 openocd 与坏二进制时的诊断输出 |
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
| Q8 | 单目标（C6）是否满足？多探针场景是否需要多连接 | M-serial 等 | ⏳ 待定 |
| **Q9** | `read_memory` 是否需要先 halt？ | S4 | ✅ 真机验证：运行态直读 flash（0x08000000 向量表）成功，无需 halt；RAM 变量的「一致性快照」场景留待 M-exec |
| **Q10** | 目标芯片实际可用的 OpenOCD 版本与 scripts 目录名 | — | ✅ 关闭：Artery 定制版 atopenocd（本机安装，路径不入库，0.11.0+dev 2024-08-05）+ `interface_cfg: "auto"`（本机探针 0D28:0204 解析为 `interface/cmsis-dap.cfg`）+ `target/at32f403axx.cfg`，实机烧录通过；**注意 `atlink_dap_v2.cfg` 在本机这根公版 DAPLink 探针上会 `unable to find a matching CMSIS-DAP device`（见 4.5 已知边界 1）**；非 AT 芯片走主线版（EIDE xPack 或 `openocd-20240916.7z` 解压）换 `OCD_BIN`/`OCD_SCRIPTS` 即可 |
| **Q11** | 镜像路径是否也接受相对 CWD（当前只认 ocd.json 目录） | 不阻塞 | ⏳ 可后置 |

---

## 17. 交付结论与下一步（HANDOFF）

**当前状态（v0.3.13）**：**S4 真机联调已通过** ✅（AT32F403AVGT7 + CMSIS-DAP v2 探针 + atopenocd：connect → flash_info → flash_write S300plus.hex 139077 字节，擦 25 扇区、写+校验 Verified OK、reset run、运行态读回向量表 0x08000000 = `20019d28 080002ad …`）。**18 个工具全部落地**（含符号层 L1 按名读写）；114 个单元测试全绿 + e2e 无硬件回归 + CI 就绪。S5 的 publish.cmd / CI / DSH 与 ZCode 注册已完成，剩「换一台机器 10 分钟跑通」的换机验收。

**建议的下一步顺序**：

1. ~~跑 S4~~ ✅ 已完成（2026-10：AT32F403AVGT7 + CMSIS-DAP v2，完整闭环验收通过）；
2. ~~运行态读失败补 M-exec~~ ✅ v0.3.8 落地 halt/resume/reset（Q9 真机验证运行态直读可行；halt 用于可靠读写现场）；
3. ~~S5 打包与客户端注册~~ 🟡 主体完成（publish.cmd + CI + DSH 双 profile + ZCode MCP/插件）；剩换机 10 分钟验收；
4. 之后才轮到断点/观察点（M-bp）与符号层（M-sym）——它们建立在同一个控制层之上，成本会显著低于本轮。

本文档随项目演进维护；每关闭一个重要开放问题就相应升级文档状态。






