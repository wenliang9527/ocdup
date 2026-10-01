# Ocdup

PC 侧的 OpenOCD 门面（MCP 服务器）：让 MCP 客户端（DSH / Claude Code / Cursor…）用自然语言完成
**连接探针 → 烧录固件 → 按地址读回数据**。

设计文档：[docs/design-plan.md](docs/design-plan.md)（v0.3.3，范围收敛为「下载 + 读指定地址」）。

## 当前能力（阶段一）

| 工具 | 作用 | 权限 |
|---|---|---|
| `connect` | 连接控制通道；无监听时按 ocd.json 自动拉起 OpenOCD（`interface_cfg: "auto"` 时先识别探针） | — |
| `status` | 后端是否运行、版本、target 列表（只读，不启动任何东西） | — |
| `show_config` | 有效设置与每项来源（`reload=true` 重读 ocd.json） | — |
| `flash_info` | 列出 flash banks（驱动、基址、大小） | 只读 |
| `flash_write` | **下载**：烧录镜像（写 + 可选校验 + 可选复位运行） | read_only / allow_flash / 白名单 / 上限 |
| `read_memory` | **读指定地址**：按 8/16/32 位宽度读回 | 只读 + 体积上限 |
| `dump_memory` | **大块读落盘**：整片 flash / RAM 快照存成文件，返回 sha256 + 前 64 字节 | 目标侧只读；PC 侧写入面：dump_allowed_paths 白名单（默认 `dumps/`）+ dump_max_bytes 上限（默认 64 MB，0=不限） |

断点 / 观察点 / 执行控制（halt、resume）/ 符号层（按名读变量）/ L3 协议字典为后续阶段。

## 前置条件

- **.NET 8 SDK**（构建与运行 Ocdup）
- **OpenOCD**（提供探针驱动与 target 脚本），二选一：
  - `OCD_BIN` 指向 openocd 可执行文件、`OCD_SCRIPTS` 指向 scripts 目录；
  - 或把 openocd 放进 PATH。
- **探针**：`ocd.json` 的 `interface_cfg` 设 `"auto"` 时，连接阶段自动识别已插探针
  （AT-Link / CMSIS-DAP / ST-Link / J-Link，带 60s 进程内缓存）；也可显式写脚本名。
  本机这根 DAPLink 公版固件（`0D28:0204`）解析为 `interface/cmsis-dap.cfg`。

## 使用

1. 核对目标板配置（探针脚本 + target 脚本），仓库根目录的 `ocd.json` 已按本机
   AT32F403AVGT7 + DAPLink 配好，换板子改 `target_cfg` 一行即可。

2. 构建：

   ```bash
   dotnet build Ocdup.sln
   ```

3. 在 MCP 客户端里注册（stdio 传输）：

   ```json
   {
     "mcpServers": {
       "ocdup": {
         "command": "dotnet",
         "args": ["D:\\path\\to\\Ocdup\\src\\Ocdup\\bin\\Debug\\net8.0\\ocdup.dll"],
         "env": { "OCD_CONFIG": "D:\\proj\\ocd.json" }
       }
     }
   }
   ```

   > `OCD_CONFIG` 省略时，Ocdup 按客户端进程的 CWD 找 `ocd.json`。
   > 也可以额外设置 `OCD_BIN` / `OCD_SCRIPTS` 指向 OpenOCD。

   **在 DSH 中注册**（本机已配置，2026-10）：往 `~/.dsh/profiles/<web|desktop>/cordis.patch.yml`
   追加一个 `@deepseek-ai/dsh-mcp-client` 插件条目，完全退出并重启 DSH 后，
   工具以 `mcp__ocdup__<tool>` 出现在所有会话：

   ```yaml
   - insert:
       - id: mcp-ocdup
         name: '@deepseek-ai/dsh-mcp-client'
         config:
           serverName: ocdup
           transport: stdio
           # 指向发布目录，别指向 bin/Debug：DSH 常驻本进程会锁住 exe，
           # 导致开发时 dotnet build 报「文件被 ocdup (pid) 锁定」
           command: D:/WORK_VSCODE/Vibe-coding/Ocdup/publish/ocdup.exe
           args: []
           env:                       # DSH 不透传宿主 env，路径要显式给
             OCD_CONFIG: D:/WORK_VSCODE/Vibe-coding/Ocdup/ocd.json
             OCD_BIN: D:/ARMGCCTool/atopenocd/bin/openocd.exe
             OCD_SCRIPTS: D:/ARMGCCTool/atopenocd/scripts
   ```

   > 卸载：删除该 insert 块并重启 DSH。
   > 6666 端口同一时间只能有一个 OpenOCD 后端；外部已开着 openocd 时 Ocdup 直接复用。
   > 发布/升级顺序（实测，DSH 会自动重新拉起死掉的 ocdup，就地覆盖必被锁）：
   > **完全退出 DSH → `dotnet publish src/Ocdup/Ocdup.csproj -c Release -o publish` → 再启动 DSH**。

4. 典型流程：

   ```
   connect
   flash_info
   flash_write file=build/fw.bin address=0x08000000     # .elf/.hex 不需要 address
   read_memory address=0x20000000 count=16 width=32
   dump_memory file=dumps/flash.bin address=0x08000000 length=1048576
   ```

## 开发

```bash
dotnet test Ocdup.sln                     # 63 个单测，不需要硬件
python tools/e2e_fake_backend.py          # 端到端：假 TCL-RPC 后端 + 真实 MCP 会话
```

- `tools/e2e_fake_backend.py` 会启动一个假的 TCL-RPC 后端（模拟 6666 端口）与真实的 MCP stdio 会话，
  覆盖工具注册、烧录/读内存/落盘链路、权限拦截文案、后端启动失败诊断——**没有硬件也能回归**。
  可用 `OCDUP_DLL=<path>` 指向任意构建产物（如 `publish/ocdup.dll`）。
- 若 DSH 正在运行 ocdup，它会锁住 `bin/Debug` 下的 exe，此时改用 Release 通道：
  `dotnet test Ocdup.sln -c Release`。
- 权限收紧：把 `read_only`、`allow_flash`、`flash_allowed_paths`、`flash_max_bytes`、
  `dump_allowed_paths`、`dump_max_bytes` 按需写进 `ocd.json`，
  随时用 `show_config` 核对生效值与来源。

## 状态与下一步

| 里程碑 | 状态 |
|---|---|
| S1 骨架 / S2 控制层 / S3 烧录与读内存 | ✅ 63 个单测 + 假后端 e2e 覆盖 |
| S4 真机联调 | ✅ AT32F403AVGT7 + CMSIS-DAP v2（DAPLink `0D28:0204`）+ atopenocd：`connect` → `flash_info` → `flash_write`（擦 25 扇区、写 + Verified OK、reset run）→ `read_memory` 运行态读回向量表 |
| S5 打包与客户端注册 | 🟡 已注册进 DSH（desktop + web 两个 profile）；建议把 `command` 切到 `publish/ocdup.exe`，见上 |

后续里程碑（执行控制、断点/观察点、符号层、L3 协议字典等）见设计文档第 12 节。
