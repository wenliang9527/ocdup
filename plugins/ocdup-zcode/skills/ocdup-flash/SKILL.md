---
name: ocdup-flash
description: Use when flashing firmware or reading/writing target memory, registers or variables on a connected MCU via the Ocdup toolchain - the mcp__ocdup__* OpenOCD-facing MCP tools (connect/status, flash_info/flash_write/verify_image/flash_erase_sector, read/write_memory, dump_memory, halt/resume/reset, add/remove/list_breakpoint, add/remove_watchpoint, run_command, backend_log, identify_chip, set_target, set_permissions, list_variables, read/write_variable, load_svd, read/write_peripheral_register) and the ocdup watch/rtt/burn/verify CLIs. Covers the flash workflow (connect, flash_info, flash_write, verify read-back), halt/resume for reliable reads and crash triage, chip identify and hot target switching, breakpoints/watchpoints for catching runaway code, permission gates (BLOCKED > show_config, never bypass), probe auto-detect boundaries, and port discipline (single backend on 6666 shared by DSH and ZCode). 触发词：烧录、刷机、固件下载、烧写、读flash、读内存、dump、向量表、卡死、跑飞、复位、识别芯片、什么芯片、切换芯片、换板子、变量名、按名读写、采样、寄存器、位域、SVD、RTT、printf日志、量产、探针、OpenOCD、DAPLink、AT-Link、ST-Link、J-Link、AT32、PY32、断点、观察点、watchpoint、breakpoint。
---

# Ocdup 烧录工具链（AI 操作手册）

Ocdup 是 PC 侧的 OpenOCD 门面（MCP 服务器，stdio），把「连接探针 → 烧录固件 →
按地址读回数据」封装成 MCP 工具。本技能只管**流程与纪律**；参数细节以工具
docstring 与 `show_config` 输出为准，不要凭记忆猜。

## 工具总览（ZCode 内以 `mcp__ocdup__*` 形式提供；DSH 内为 `mcp__ocdup__<tool>`；共 30 个——含 v0.4.6 的 disconnect（探针让渡）、v0.4.21 的 SVD 三件（load_svd / read_peripheral_register / write_peripheral_register）与 v0.4.30 的断点/观察点五件（add/remove/list_breakpoint + add/remove_watchpoint），随版本演进以实际列表为准）

| 工具 | 作用 | 变更目标？ |
|---|---|---|
| `connect` | 连接控制通道；无监听时按 ocd.json 自动拉起 OpenOCD（探针 auto 识别）；成功后 best-effort 自动识芯 + flash 布局 | 拉起后端 |
| `status` | 后端是否在跑、版本、target 一行式摘要（只读探测，**不启动**任何东西） | 否 |
| `show_config` | 有效设置与每项来源；`reload=true` 重读 ocd.json | 否 |
| `flash_info` | 列出 flash banks（先逐 bank probe：自动容量配置/双 bank 的 bank1 必须 probe 过才有真实大小） | 否 |
| `flash_write` | 烧录镜像（默认 verify + reset run） | **是** |
| `flash_erase_sector` | 擦除扇区（last=-1 = 到 bank 末尾；**默认关闭**） | **是** |
| `verify_image` | 镜像与目标 flash 比对，**不写任何东西**（回答「板子里到底是谁」） | 否 |
| `read_memory` | 按地址/宽度读回；`format=json` 返回结构化值（≤ `memory_read_max_bytes`，默认 4096） | 否 |
| `write_memory` | 写内存/寄存器（一条命令多值，单次 ≤256） | **是** |
| `dump_memory` | 大块读落盘（整片 flash/RAM 快照），返回路径 + 字节数 + sha256 | 否 |
| `halt` / `resume` / `reset` | 执行控制：halt 后读写才可靠，并带回核心寄存器现场（pc/lr/sp/xpsr，"跑飞在哪"一步定位）；`reset halt` 同样带回 | **是**（执行状态） |
| `add_breakpoint` / `remove_breakpoint` / `list_breakpoints` | 断点（`bp`/`rbp`）：add 带 address + 可选 length（默认 2，恒下发）/hw；remove 不带地址 = 全清；list 是纯读无闸。**真机核定（2026-10-19）**：软断点需目标 halted（运行态设回 `can't add breakpoint: target running`），硬件断点（hw=true）运行态可设 | **是**（执行状态） |
| `add_watchpoint` / `remove_watchpoint` | 数据观察点（`wp`/`rwp`）：access 为 r/w/a（命中即停）；需目标 halted；remove 不带地址 = 全清（后端无统一 all 形态，走「列出逐个删」）；数量受芯片硬件限制 | **是**（执行状态） |
| `run_command` | 原始 TCL 逃生舱（单行；兜底专用工具没覆盖的操作） | 视命令 |
| `backend_log` | ocdup 自己拉起的 openocd 近期 stdout/stderr（异步 `Info:`/`Error:` 日志） | 否 |
| `identify_chip` | 读 CPUID（ARM 内核，任何 Cortex-M 有效）+ 厂商 DEV_ID（默认 0xE0042000），按 ocd.json 的 `chip_id_map` 查家族；与当前 target_cfg 不符时报 `target mismatch` 行 | 否 |
| `set_target` | **换芯片不重启会话**：按 ocd.json 的 `target_presets` 注册表切 target_cfg 并重启后端（只重启 ocdup 自拉实例；起不来自动回滚；外部实例占端口时警告） | **是**（重启后端） |
| `set_permissions` | 运行时改权限（免改文件；闸 = `allow_set_permissions`，默认关；reload/重启丢弃） | 改的是闸本身 |
| `list_variables` | 列出 .elf 的变量（地址/大小/名，可过滤）——PC 侧读文件，不动目标 | 否 |
| `read_variable` | 按名读变量（ELF 定位 → 目标读回；受 `memory_read_max_bytes`） | 否 |
| `write_variable` | 按名写 1/2/4 字节变量（大对象引导用 write_memory） | **是** |

**符号层 L2（v0.4.21）**：`load_svd(file)` 加载 CMSIS-SVD（列外设/寄存器清单；
`svd_path` 可作默认镜像）→ `read_peripheral_register("RCC.CR")` 按名读 + 位域解码 +
reset 比对（簇寄存器带路径，如 `"TIM1.PWM.CCR"`）→ `write_peripheral_register` 按名写
（走 allow_memory_write 闸；SVD access 为 read-only 的寄存器即使放行也拒写）。

另有 **`ocdup watch` 子命令 CLI**（M-watch，不是 MCP 工具）：按符号名定时采样变量
（`--name` 多个、`--interval`、`--format csv` 重定向画曲线、`--halt` 每拍暂停固件读
一致快照），长时运行不占 MCP 通道；`ocdup watch --help` 看用法。csv 形态下 stdout
只出表头+数据行（banner/清单/收尾走 stderr），`> log.csv` 即得干净文件；退出码
对齐 burn/verify/rtt 家族（0=正常结束含 Ctrl+C，2=用法/权限，3=后端丢失）。

另有 **`ocdup rtt` 子命令 CLI**（v0.4.19，不是 MCP 工具）：读固件 printf / RTT 日志流——
原始字节直通 stdout，`--address`（查 map 文件 `_SEGGER_RTT`）+ `--lines/--seconds` 限长、
`--info` 列通道；长时连续流同样不占 MCP 通道；`ocdup rtt --help` 看用法。

另有 **`ocdup burn` 子命令 CLI**（v0.4.20，不是 MCP 工具）：产线/CI 非交互烧录，与
`flash_write` 同一编排核心，按退出码分流（0=verified / 1=读回不符·写失败·或读回
INCONCLUSIVE / 2=用法·权限 / 3=后端故障）；烧录中刻意不响应 Ctrl+C（写相中断 = 半截
固件）。配套 **`ocdup verify` 子命令 CLI**（v0.4.26）：只读比对镜像与目标 flash
（金板抽检/烧后自检），判据与 `verify_image` 工具同一份，exit 0/1/2/3 同族。后端 CRC
算法需目标静止，固件在跑时可能回 `VERIFY INCONCLUSIVE`——**那不代表固件不匹配**，
halt 后重试（v0.4.30 起 burn/flash_write 与 verify_image 用同一份判据，不会再把它
误报成 VERIFY FAILED）。

另有 **`ocdup doctor` 子命令 CLI**（v0.4.15，不是 MCP 工具）：环境体检，换机/排障
第一步——把 ocd.json / OCD_BIN / OCD_SCRIPTS / 探针 / 后端端口五项拼成一张
`[OK]/[WARN]/[FAIL]` + 「如何修」的清单，只读、不拉起后端、不碰目标，有 FAIL 退出码 1（用法错 2）。

## 标准烧录流程

1. `connect`——先连后端。输出里的 `backend:` 行会回报探针自动识别结果
   （探针 → 接口脚本），把这一行报给用户。
2. `flash_info`——确认 flash bank 基址与大小（本机 AT32F403AVGT7 为
   0x08000000 起、1MB）。
3. `flash_write file=<构建产物> [address=<hex>]`——**`.bin` 必须显式给
   address；`.elf`/`.hex` 自带地址不用给**。默认 `verify=true`、`reset=true`。
   结论由烧录后**独立的 `verify_image` 读回**判定（v0.4.13）：
   `programmed + verified`（随后 reset run）/ `programmed but verify FAILED`
   （写声称成功但读回不符，最危险）/ `write failed`；program 阶段的
   `Error:`/`failed` 文本只是辅助信息（PY32 虚假 AHB 错误不再误跳 reset）。
4. 读回验证：`read_memory address=0x08000000 count=16 width=32 format=json`
   看向量表（json 免解析 OpenOCD 文本）；要独立确认内容与镜像一致，用
   `verify_image` 再比一次（只读不写）；**运行态读失败** → `halt` → 重读 →
   `resume`（`allow_exec` 闸默认开）；整片快照用 `dump_memory`。

## 惯例与陷阱

- **地址一律 hex 字符串**（`'0x20000000'`），不是整数；也接受不带 `0x` 前缀
  的 hex 与 `_` 分隔。
- **相对路径基准 = ocd.json 所在目录**，不是客户端 CWD；烧录文件若配置了
  `flash_allowed_paths` 白名单，必须位于白名单目录内。
- **权限拦截是设计行为**：被拦文案形如 `BLOCKED: 'flash_write' is not
  permitted ... See show_config.`——正确动作是先 `show_config` 核对生效权限，
  再改 ocd.json 的 `permissions` 块并 `show_config reload=true`，**不要换
  花样绕过**。写类权限一览：`allow_flash` 管 flash_write；
  `allow_flash_erase` 管 flash_erase_sector（默认 false，破坏性显式开）；
  `allow_memory_write` 管 write_memory；`allow_exec` 管 halt/resume/reset
  （默认 true）**与断点/观察点五件**（add/remove_breakpoint、add/remove_watchpoint——
  命中即停，与 halt/resume 同级；list_breakpoints 纯读无闸）；
  `allow_raw_command` 管 run_command（默认 false，开启后写类
  命令仍有内容分类——同权或重定向专用工具，bp/rbp/wp/rwp 归 allow_exec 类）；dump_memory 在
  目标侧只读，但 PC 侧落盘受 `dump_allowed_paths`（白名单，默认 `dumps/`）
  与 `dump_max_bytes`（默认 64 MB，0=不限）约束；read_memory 受
  `memory_read_max_bytes`（默认 4096）约束。
- **后端未就绪**：`ERROR: nothing is listening on ... Call connect and
  retry`——除 `status` 外的工具都要求先 `connect`；`status` 只探测不启动。
- **6666 端口单实例**：同一时间只有一个 OpenOCD 后端；外部已开着 openocd
  （包括 DSH 侧拉起的）时 Ocdup 直接复用。**DSH 与 ZCode 共享同一后端——
  不要两边同时烧录**；先 connect 的一方占用探针。
- **探针识别边界**（A/B 实测定论）：识别优先级 AT-Link → CMSIS-DAP/DAPLink
  → ST-Link → J-Link；多接口 DAPLink 探针（0D28:0204）必须走 auto 默认的
  `cmsis-dap.cfg`，`atlink_dap_v2.cfg` 反而找不到设备（留给原生 AT-Link，
  VID 2E3C）。探针刚插上就 connect 能被发现（空结果不缓存，60s 内缓存非空
  结果）；J-Link 被 SEGGER 驱动独占时会启动失败，诊断里附处理建议。
- **超时**：烧大固件可能超过 `command_timeout_seconds`（默认 60s），失败
  文案会提示该设置；改 ocd.json 后 `show_config reload=true` 生效。
- **产物与升级纪律**：客户端注册一律指向 `publish/ocdup.exe`，**不要指向
  `bin/Debug`**——ZCode/DSH 是常驻进程，会锁住正在运行的 exe，导致开发时
  `dotnet build` 报文件被锁定；升级顺序：退出客户端 → `dotnet publish
  src/Ocdup/Ocdup.csproj -c Release -o publish` → 再启动客户端。
- 本机配置：AT32F403AVGT7 + CMSIS-DAP v2 探针 + Artery 定制版 atopenocd
  （安装位置由 `OCD_BIN` / `OCD_SCRIPTS` 指定，`show_config` 可核对），`ocd.json`
  已按此配好。多芯片场景（v0.4.0）：identify 报 `target mismatch` 时按提示
  `set_target <家族名>` 一键切换（不用改文件、不用重启会话）；`set_target list`
  看注册表。仓库 `cfgs/` 里的板级 cfg（钥匙板 AT32F423 / 锁芯板 PY32F002A）
  已收录进本机 target_presets。PY32F002A 的烧录：v0.4.4 起后端二进制
  （atopenocd-py32，puyaf0x 驱动）原生支持，`set_target PY32F002A-FLASH` 切过去
  即可 `flash_write`（构建记录 docs/openocd-puya-build.md）；量产产线仍走 J-Link。

## 典型会话

烧录 → 校验 → 复位运行 → 读向量表：

```
connect
flash_info
flash_write file=build/S300plus.hex          # .bin 则加 address=0x08000000
read_memory address=0x08000000 count=16 width=32
```

大块读（整片 1MB flash 快照落盘，绕开 read_memory 的 4KB 文本上限）：

```
connect
dump_memory file=dumps/flash.bin address=0x08000000 length=1048576
```

## 烧录后串口验证（方案 A：会话层协同，Q6 定案）

烧录验证常常不止"读回 flash"——板子跑起来后串口该吐协议帧。Ocdup 与 acccom 是两个
独立 MCP server，**帧交互全部走 acccom**（Q6：Ocdup 只管烧录），会话层天然可以先后
编排两边。干净复现三步（细节纪律以 acccom-serial skill 为准）：

1. **记水位**：`mcp__acccom__traffic_log`(limit=1) 记下 `lastSeq = S` 并告知用户；
2. **烧录 + 让板子跑起来**：ocdup 标准流程（flash_write 默认 verify + reset run）；
   需要重新上电时停下来等用户确认；
3. **发帧 + 读回**：`send`/`send_and_wait` 发指定帧（端口未开先 `open_port`，用完
   `close_port`），然后 `traffic_log`(sinceSeq=S, compact=true) 读回本次全部收发复盘。

`/flash-verify` 命令把这套流程固化（参数：镜像路径 + 可选验证帧 + COM 口）。

## 故障升级路径

1. `connect` 失败/超时 → 看返回的 `--- recent openocd output ---` 诊断
   （末 200 行）与 `backend:` 行；常见原因：探针没插、驱动被占用
   （J-Link × SEGGER）、`OCD_SCRIPTS` 指错目录。
2. `flash_write` 结论不是 `verified`（VERIFY FAILED / WRITE FAILED /
   VERIFY INCONCLUSIVE）→ reset run 已自动跳过；INCONCLUSIVE 是「CRC 算法没跑起来」
   （固件在跑），**不是固件损坏**，halt 后重试 verify_image；真失配先 `flash_info`
   确认 bank，再用已知好镜像重试；烧录进度/异步
   `Info:`/`Error:` → `backend_log`（仅 ocdup 自己拉起的后端有捕获）；疑似版本
   语义差异 → 记录 OpenOCD 版本（`status` 输出）再定位。
3. 权限问题 → `show_config`；配置改动后 → `show_config reload=true`。
