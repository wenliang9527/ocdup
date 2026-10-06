<div align="center">

# 🔧 Ocdup

**PC 侧的 OpenOCD 门面（MCP 服务器）**

让 MCP 客户端（DSH / ZCode / Claude Code / Cursor…）用自然语言驱动硬件调试器：
**连接探针 → 烧录固件 → 读写内存 → 执行控制**，全程无需手敲 TCL。

[![CI](https://github.com/wenliang9527/ocdup/actions/workflows/ci.yml/badge.svg)](https://github.com/wenliang9527/ocdup/actions/workflows/ci.yml)
[![设计文档](https://img.shields.io/badge/设计文档-design--plan.md-2ea44f)](docs/design-plan.md)
[![ZCode 插件](https://img.shields.io/badge/ZCode插件-ocdup--zcode-8A2BE2)](plugins/ocdup-zcode/README.md)

</div>

---

## 💡 它能做什么

把调试器变成 AI 的「手」：你说目标，Ocdup 把它翻译成 OpenOCD 的 TCL 命令，
再把结果带上上下文整理成人话返回。典型场景：

| 你说 | AI 实际调用 | 说明 |
|---|---|---|
| 「把 build/fw.hex 烧进板子，烧完跑起来」 | `connect` → `flash_info` → `flash_write` | 自动擦写 + 校验（Verified OK）+ 复位运行 |
| 「板子里现在跑的是什么固件？」 | `verify_image` | 只读比对镜像与 flash 内容，不写任何东西 |
| 「读一下 0x20000000 处的 16 个字」 | `read_memory` | 8/16/32 位宽度；`format=json` 返回结构化值 |
| 「把整片 flash 备份下来」 | `dump_memory` | 大块落盘，返回路径 + 字节数 + sha256 |
| 「板子好像卡死了，看看现场」 | `halt` → 读写现场 → `reset run` | 执行控制；halt 后读写才可靠 |
| 「擦掉 0x080C0000 那个扇区」 | `flash_erase_sector` | 破坏性操作，默认关闭，显式开启才可用 |
| 「跑这条 TCL：`targets`」 | `run_command` | 原始命令逃生舱，兜底专用工具没覆盖的操作 |

## 🧰 工具一览（19 个）

### 会话与诊断

| 工具 | 作用 | 权限 |
|---|---|---|
| `connect` | 连接控制通道；无监听时按 ocd.json 自动拉起 OpenOCD（`interface_cfg: "auto"` 时先识别探针）；成功后 best-effort 自动识别芯片（CPUID + DEV_ID,3s 短超时，目标不应答不拖垮连接）+ flash 布局（`flash probe`+`flash banks`，target 失配时跳过）；identify 与 `target_presets` 不符时回报 `target mismatch` 行 | — |
| `status` | 后端是否运行、版本、target 列表（只读，不启动任何东西） | — |
| `show_config` | 有效设置与每项来源（`reload=true` 重读 ocd.json） | — |
| `backend_log` | ocdup 自己拉起的 openocd 近期 stdout/stderr（异步 `Info:` / `Error:` 日志从这里出） | 只读 |
| `identify_chip` | **连上后确认芯片**：读 CPUID（0xE000ED00，ARM 内核标准）+ 厂商 ID 寄存器（默认 0xE0042000），DEV_ID 按 ocd.json 的 `chip_id_map` 给出家族；家族与当前 `target_cfg` 不符时附失配行 | 只读 |
| `set_target` | **换芯片不重启会话**：按 ocd.json 的 `target_presets` 注册表切 target_cfg 并重启后端（target 是启动参数，必须重启；只重启 ocdup 自拉的实例；起不来自动回滚） | `read_only` |
| `set_permissions` | **运行时改权限**（免改 ocd.json；闸 = ocd.json 的 `allow_set_permissions`，默认关；reload/重启丢弃） | 自身的授权闸 |

### 烧录

| 工具 | 作用 | 权限 |
|---|---|---|
| `flash_info` | 列出 flash banks（驱动、基址、大小） | 只读 |
| `flash_write` | **下载**：烧录镜像（写 + 可选校验 + 可选复位运行） | `read_only` / `allow_flash` / 白名单 / 上限 |
| `verify_image` | **烧后独立校验**：镜像与目标 flash 比对，不写任何东西（回答「板子里到底是谁」） | 只读 |
| `flash_erase_sector` | 擦除 flash 扇区（破坏性，默认关闭） | `read_only` / **`allow_flash_erase`（默认 false）** |

### 内存读写

| 工具 | 作用 | 权限 |
|---|---|---|
| `read_memory` | **读指定地址**：按 8/16/32 位宽度读回；`format=json` 返回结构化值（免解析 OpenOCD 文本） | 只读 + 体积上限 |
| `dump_memory` | **大块读落盘**：整片 flash / RAM 快照存成文件，返回 sha256 + 前 64 字节 | 目标侧只读；PC 侧落盘受 `dump_allowed_paths`（默认 `dumps/`）+ `dump_max_bytes`（默认 64 MB）约束 |
| `write_memory` | 写内存/寄存器（一条命令写多个值） | `read_only` / `allow_memory_write` |

### 执行控制与逃生舱

| 工具 | 作用 | 权限 |
|---|---|---|
| `halt` / `resume` / `reset` | 执行控制：halt 后读写才可靠；reset run 重启固件 | `read_only` / **`allow_exec`（默认 true）** |
| `run_command` | 原始 TCL 逃生舱（单行命令，兜底专用工具没覆盖的操作） | `read_only` / `allow_raw_command` |

### 符号层（L1，按变量名读写）

| 工具 | 作用 | 权限 |
|---|---|---|
| `list_variables` | 列出 .elf 里的变量（地址/大小/名，可 pattern 过滤）——PC 侧读文件，不动目标 | — |
| `read_variable` | 按名读变量（ELF 定位 → 目标读回；受 `memory_read_max_bytes` 约束） | 只读 |
| `write_variable` | 按名写 1/2/4 字节变量（大对象走 `write_memory`） | `read_only` / `allow_memory_write` |

> 用法：`ocd.json` 里把 `elf_path` 指到编译产物（未 strip 的 .elf），三个工具就都有默认镜像了。

> 后续里程碑：断点 / 观察点 / 符号层（按名读变量）/ L3 协议字典，见[设计文档第 12 节](docs/design-plan.md)。

## 🏗️ 架构

```mermaid
graph LR
    subgraph PC
        A["MCP 客户端<br/>DSH · ZCode · Claude Code · Cursor"] -->|"stdio"| B["Ocdup<br/>MCP 服务器（.NET 8）"]
        B -->|"TCL-RPC · 127.0.0.1:6666"| C["OpenOCD"]
        B -.->|"前置检查"| P["PermissionGate<br/>权限闸"]
        B -.->|"分层合并"| K["ocd.json<br/>项目配置"]
    end
    C -->|"USB"| D["探针<br/>AT-Link · CMSIS-DAP/DAPLink · ST-Link · J-Link"]
    D -->|"SWD / JTAG"| E["目标 MCU<br/>AT32F403A 等"]
```

分层职责：**接入层**（MCP SDK，不含硬件逻辑）→ **工具层**（one tool = one capability）→
**控制层**（TCL-RPC 客户端 + 子进程生命周期）／**权限层**（变更类操作前置检查）／**配置层**（分层合并 + 来源追踪）。
设计细节见 [docs/design-plan.md](docs/design-plan.md)。

## 🚀 快速开始

### 1. 前置条件

- **.NET 8 SDK**（构建与运行 Ocdup）
- **OpenOCD**（提供探针驱动与 target 脚本），二选一：
  - `OCD_BIN` 指向 openocd 可执行文件、`OCD_SCRIPTS` 指向 scripts 目录；
  - 或把 openocd 放进 PATH。
- **探针**：`ocd.json` 的 `interface_cfg` 设 `"auto"` 时，连接阶段自动识别已插探针
  （AT-Link / CMSIS-DAP / ST-Link / J-Link，带 60s 进程内缓存）；也可显式写脚本名。

### 2. 构建

```bash
dotnet build Ocdup.sln
```

### 3. 配置

复制 `ocd.json.example` 为 `ocd.json` 放在项目根（或用 `OCD_CONFIG` 指向它）。
多芯片场景（v0.4.0）：`target_presets` 注册表（家族名 → openocd target cfg）+
`chip_id_map`（DEV_ID → 家族名）——identify 认出板子与当前 `target_cfg` 不符时
回报失配行，`set_target <家族名>` 一键切换并重启后端，换板子不用改文件、不用重启
会话。权限按需收紧（见[权限与安全](#-权限与安全)）；
联调期把 `elf_path` 指到编译产物 .elf，即可按变量名读写（符号层 L1）。

**板级 cfg（[cfgs/](cfgs/)）**：仓库自带两块手写板的专用 target cfg（`target_presets`
用绝对路径指向它们），cfg 头部注释即排障记录：

- `cfgs/target_at32f423_key.cfg` — 钥匙板（AT32F423TCU7）。固件在 VBUS 掉电时进
  deepsleep（深睡禁调试、AHB-AP 全堵），裸 attach 必然 `can't assert SRST` /
  `Error connecting DP`；cfg 用 `reset_config connect_assert_srst` 在复位态完成附加。
- `cfgs/target_py32f002a_lock.cfg` — 锁芯板（PY32F002A，Cortex-M0+）。**烧录不走
  ocdup**（当前后端 atopenocd 无 PY32 flash 驱动，量产走 Keil+DAPLink / J-Link）；
  此 cfg 是最小通用 cortex_m 目标（不挂 flash 驱动），供 ocdup 做 halt / 读内存 /
  dump_memory / 符号读写等调试观察。

## 📱 客户端注册

> 路径说明：本文所有 `D:/path/to/...` 均为**占位符**，按本机实际位置替换
> （本机真实路径记录在 `LOCAL-MACHINE.md`，该文件已 gitignore、不入库）。

任何支持 stdio MCP 的客户端都可以注册：

```json
{
  "mcpServers": {
    "ocdup": {
      "command": "dotnet",
      "args": ["D:/path/to/Ocdup/publish/ocdup.dll"],
      "env": {
        "OCD_CONFIG": "D:/path/to/Ocdup/ocd.json",
        "OCD_BIN": "D:/path/to/openocd/bin/openocd.exe",
        "OCD_SCRIPTS": "D:/path/to/openocd/scripts"
      }
    }
  }
}
```

> `OCD_CONFIG` 省略时，Ocdup 按客户端进程的 CWD 找 `ocd.json`。

**通用纪律（所有客户端都适用）**：

- 注册一律指向 `publish/` 产物，**别指向 `bin/Debug`**——客户端是常驻进程，
  会锁住正在运行的 exe，导致开发时 `dotnet build` 报「文件被 ocdup 锁定」。
- **发布/升级顺序**：完全退出客户端 → `dotnet publish src/Ocdup/Ocdup.csproj -c Release -o publish` → 再启动客户端
  （或直接跑根目录的 `publish.cmd`，它带防锁检测）。
- **6666 端口单实例**：同一时间只能有一个 OpenOCD 后端；外部已开着 openocd 时
  Ocdup 直接复用。多个客户端（如 DSH + ZCode）共享同一后端，**不要两边同时烧录**。

<details>
<summary><b>在 DSH 中注册</b>（cordis.patch.yml）</summary>

往 `~/.dsh/profiles/<web|desktop>/cordis.patch.yml` 追加一个
`@deepseek-ai/dsh-mcp-client` 插件条目，完全退出并重启 DSH 后，工具以
`mcp__ocdup__<tool>` 出现在所有会话：

```yaml
- insert:
    - id: mcp-ocdup
      name: '@deepseek-ai/dsh-mcp-client'
      config:
        serverName: ocdup
        transport: stdio
        command: D:/path/to/Ocdup/publish/ocdup.exe
        args: []
        env:                       # DSH 不透传宿主 env，路径要显式给
          OCD_CONFIG: D:/path/to/Ocdup/ocd.json
          OCD_BIN: D:/path/to/openocd/bin/openocd.exe
          OCD_SCRIPTS: D:/path/to/openocd/scripts
```

> 卸载：删除该 insert 块并重启 DSH。

</details>

<details>
<summary><b>在 ZCode 中注册</b>（config.json + 可选插件）</summary>

编辑 `~/.zcode/cli/config.json`，在 `mcp.servers` 里加入：

```json
"ocdup": {
  "enabled": true,
  "command": "D:\\path\\to\\Ocdup\\publish\\ocdup.exe",
  "args": [],
  "env": {
    "OCD_CONFIG": "D:\\path\\to\\Ocdup\\ocd.json",
    "OCD_BIN": "D:\\path\\to\\openocd\\bin\\openocd.exe",
    "OCD_SCRIPTS": "D:\\path\\to\\openocd\\scripts"
  },
  "type": "stdio"
}
```

完全退出并重启 ZCode 后，工具以 `mcp__ocdup__<tool>` 出现在所有会话。

仓库还带 ZCode 插件 `plugins/ocdup-zcode`（ocdup-flash skill +
`/ocdup-status`、`/ocdup-flash`、`/flash-verify` 命令，纯知识层、不含 MCP server）：在 ZCode →
Settings → Plugin Management → Discover → `+` 添加本仓库的 `plugins/` 目录并
安装即可，详见 [plugins/ocdup-zcode/README.md](plugins/ocdup-zcode/README.md)。

</details>

## 📖 使用示例

注册好之后，直接用自然语言下指令即可（AI 会自动选工具与参数）。等价的手动工具序列：

**烧录 → 校验 → 复位运行 → 读向量表**

```text
connect
flash_info
flash_write file=build/fw.bin address=0x08000000    # .elf/.hex 不需要 address
read_memory address=0x08000000 count=16 width=32 format=json
```

**整片 flash 快照**（绕开 read_memory 的 4KB 文本上限）

```text
connect
dump_memory file=dumps/flash.bin address=0x08000000 length=1048576
```

**卡死排查**

```text
halt
read_memory ...        # 看现场
resume                 # 或 reset run 重启固件
```

**烧后独立校验**（不写任何东西）

```text
verify_image file=build/fw.bin
```

**权限被拦时**：返回形如 `BLOCKED: 'flash_write' is not permitted ... See show_config.`——
若 ocd.json 里开了 `allow_set_permissions`，直接对话式调整（`set_permissions` 运行时生效，
reload/重启丢弃）；否则 `show_config` 核对生效权限，改 ocd.json 的 `permissions` 块并
`show_config reload=true`。拦截是设计行为，不要绕过。

## 🔒 权限与安全

| 权限项 | 默认 | 闸住的操作 |
|---|---|---|
| `read_only` | false | 总闸：以下全部（含 `set_target` 的后端重启） |
| `allow_flash` | true | `flash_write` |
| `allow_memory_write` | true | `write_memory` |
| `allow_flash_erase` | **false** | `flash_erase_sector`（破坏性，显式开启） |
| `allow_exec` | true | `halt` / `resume` / `reset` |
| `allow_raw_command` | true | `run_command` |
| `allow_set_permissions` | **false** | `set_permissions`（运行时改权限）——人类授权开关：放宽权限的决定权在人 |
| `flash_allowed_paths` | []（不限） | 烧录文件必须在白名单目录内 |
| `flash_max_bytes` | 0（不限） | 超大烧录文件拒绝 |
| `dump_allowed_paths` | `dumps/` | dump 落盘目标白名单 |
| `dump_max_bytes` | 64 MB | dump 大小上限（0 = 不限） |
| `memory_read_max_bytes` | 4096 | 单次读回体积上限（防呆） |

所有设置写进 `ocd.json` 的 `permissions` 块（或顶层平铺同名字段），改完
`show_config reload=true` 立即生效；`show_config` 同时显示每项的来源。

## 🧪 开发与测试

```bash
dotnet test Ocdup.sln                     # 140 个单测，不需要硬件
python tools/e2e_fake_backend.py          # 端到端：假 TCL-RPC 后端 + 真实 MCP 会话
```

- `tools/e2e_fake_backend.py` 启动一个假 TCL-RPC 后端（模拟 6666 端口）与真实
  MCP stdio 会话，覆盖工具注册、烧录/读内存/落盘链路、权限拦截文案、后端启动
  失败诊断——**没有硬件也能回归**。可用 `OCDUP_DLL=<path>` 指向任意构建产物。
- **流量镜像**：每次工具调用的 TCL 命令/响应、后端生命周期事件追加到
  `%LOCALAPPDATA%\Ocdup\mcp-traffic.jsonl`（超 2 MB 轮转）；openocd 自己的
  异步日志（`Info:`/`Error:`/target 事件）也逐行镜像（`direction: "log"`）。
  不接 UI 就能排障（直接 grep）。
- **DSH 侧边栏面板**：[tools/dsh-ocdup-panel/](tools/dsh-ocdup-panel/)
  （`deploy.cmd web` 部署 + 重启 DSH），右侧停靠栏实时显示命令/响应/日志/后端
  事件，五向过滤、目标过滤、正则搜索、导出 JSONL。**v1.2.0**：调用配对视图
  （cmd↔rsp 成行、log 归入调用）、烧录进度条（擦除/写入/校验分段）、会话分组、
  工具 chips 快捷过滤、关键行高亮、键盘导航 ↑/↓/Enter；**v1.2.1**：链路状态区
  （探针/芯片/Flash 布局常驻工具栏，点击定位来源）；**v1.2.2**：connect 自动识芯后
  芯片段自动填充，缺段给「复制提问」占位；**v1.2.3**：工具栏/链路行整体美化（统一
  28px 控件、状态胶囊、小卡片），链路三段说人话——探针显示烧录器类型（CMSIS-DAP /
  J-Link / ST-Link 而非 .cfg 文件名）、芯片只信实测（`chip_id_map` 命中才显示型号，
  否则内核 + dev + 「型号未识别」，cfg 文件名推定不上卡）、Flash 只写容量（1024 KB）；
  **v1.2.4**：「台架仪器」界面改版（工具栏两行化、链路「规格读数条」、列表 sticky
  列头条、控件统一 28px/6px 圆角 + tabular 数字）；**v1.2.5**：链路三段语义色
  （探针蓝 / 芯片绿 / Flash 琥珀，与流水方向色同一套语言）；**v1.3.0**：配套 v0.4.0
  的 target 注册表——链路行出现「⚡ 切换 <家族名>」胶囊（identify 发现板子与
  target_cfg 不符时，点击复制 `mcp__ocdup__set_target` 调用）；**v1.3.1**：Flash 段报 bank 合计（双 bank 一片多大一眼可见）、工具栏下排分组、stats 方向色图例、调用视图列头条、链路卡左色条；
  **v1.3.2**：界面精修（按钮/文字/配色）——控件不折行、engaged 态统一墨色实底（不再撞宿主品牌色）、
  方向过滤改「通道 chip」、工具条单行横滚、链路读数条值/段名层级翻转、搜索框内嵌正则开关。零依赖行为测试
  `node test.js core|window|api|client`（158 项断言）；改代码只改真源目录再 `deploy.cmd`。
- **发布**：根目录 `publish.cmd`（单测 Release → publish → 提示重启 DSH；检测到
  ocdup.exe 在跑会拒绝执行）。CI：`.github/workflows/ci.yml`（build + 单测 + e2e）。
- 若客户端正在运行 ocdup 锁住了 `bin/Debug` 的 exe，测试改用 Release 通道：
  `dotnet test Ocdup.sln -c Release`。

## 📍 项目状态

| 里程碑 | 状态 |
|---|---|
| S1 骨架 / S2 控制层 / S3 烧录与读内存 | ✅ 140 个单测 + 假后端 e2e 覆盖 |
| S4 真机联调 | ✅ AT32F403AVGT7 + CMSIS-DAP v2（DAPLink `0D28:0204`）+ atopenocd：`connect` → `flash_info` → `flash_write`（擦 25 扇区、写 + Verified OK、reset run）→ `read_memory` 运行态读回向量表 |
| S5 打包与客户端注册 | 🟡 已注册进 DSH（desktop + web 两个 profile）与 ZCode（`mcp.servers` 条目 + `ocdup-zcode` 插件，均指 `publish/ocdup.exe`） |

后续里程碑（断点/观察点、符号层 L1/L2、L3 协议字典、与 acccom 的串口协同等）见
[设计文档第 12 节](docs/design-plan.md)。
