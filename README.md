# Ocdup

PC 侧的 OpenOCD 门面（MCP 服务器）：让 MCP 客户端（DSH / Claude Code / Cursor…）用自然语言完成
**连接探针 → 烧录固件 → 按地址读回数据**。

设计文档：[docs/design-plan.md](docs/design-plan.md)（v0.3.2，范围收敛为「下载 + 读指定地址」）。

## 当前能力（阶段一）

| 工具 | 作用 | 权限 |
|---|---|---|
| `connect` | 连接控制通道；无监听时按 ocd.json 自动拉起 OpenOCD | — |
| `status` | 后端是否运行、版本、target 列表（只读，不启动任何东西） | — |
| `show_config` | 有效设置与每项来源（`reload=true` 重读 ocd.json） | — |
| `flash_info` | 列出 flash banks（驱动、基址、大小） | 只读 |
| `flash_write` | **下载**：烧录镜像（写 + 可选校验 + 可选复位运行） | read_only / allow_flash / 白名单 / 上限 |
| `read_memory` | **读指定地址**：按 8/16/32 位宽度读回 | 只读 + 体积上限 |
| `dump_memory` | **大块读落盘**：整片 flash / RAM 快照存成文件，返回 sha256 + 前 64 字节 | 只读 |

断点 / 观察点 / 执行控制（halt、resume）/ 符号层（按名读变量）/ L3 协议字典为后续阶段。

## 前置条件

- **.NET 8 SDK**（构建与运行 Ocdup）
- **OpenOCD**（提供探针驱动与 target 脚本），二选一：
  - `OCD_BIN` 指向 openocd 可执行文件、`OCD_SCRIPTS` 指向 scripts 目录；
  - 或把 openocd 放进 PATH。

## 使用

1. 复制配置模板并填写目标板信息（探针脚本 + target 脚本）：

   ```bash
   cp ocd.json.example ocd.json
   ```

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
dotnet test Ocdup.sln                     # 43 个单测，不需要硬件
python tools/e2e_fake_backend.py          # 端到端：假 TCL-RPC 后端 + 真实 MCP 会话
```

- `tools/e2e_fake_backend.py` 会启动一个假的 TCL-RPC 后端（模拟 6666 端口）与真实的 MCP stdio 会话，
  覆盖工具注册、烧录/读内存链路、权限拦截文案、后端启动失败诊断——**没有硬件也能回归**。
  可用 `OCDUP_DLL=<path>` 指向 Release 产物。
- 权限收紧：把 `read_only`、`allow_flash`、`flash_allowed_paths`、`flash_max_bytes` 按需写进
  `ocd.json`（见 `ocd.json.example`），随时用 `show_config` 核对生效值与来源。

## 状态与下一步

阶段一（S1–S3）已完成：控制层、烧录、读内存均已实现并有单测/端到端验证。
剩余：**S4 真机联调**（AT-Link + 目标板）与 **S5 打包与客户端注册**——见设计文档第 12/17 节。
