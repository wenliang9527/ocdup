# ocdup-zcode — Ocdup 烧录工具链 ZCode 插件

把 Ocdup 对 agent 的**知识层**打包成 ZCode 插件（纯 JSON + Markdown，无需改
ZCode 本身）。装好后 ZCode 会话自动获得烧录操作的流程纪律与两个快捷命令。

## 组件

| 组件 | 内容 |
|------|------|
| skill | `ocdup-flash`：标准烧录流程（connect → flash_info → flash_write → 读回验证，失败 halt→读→resume）、verify_image 独立校验、read_memory format=json、地址/宽度与相对路径惯例、权限闸纪律（BLOCKED → show_config → 改 ocd.json → reload，含 allow_exec 等五个写类开关）、探针 auto 识别边界（DAPLink 必走 cmsis-dap.cfg 的 A/B 实测定论）、端口纪律（6666 单实例，DSH/ZCode 共享后端）、backend_log 异步日志 |
| command | `/ocdup-status`：后端状态 + 权限生效值（不启动任何进程） |
| command | `/ocdup-flash`：引导式烧录（连探针 → 确认布局 → 烧录 → 读向量表） |

**刻意不包含 MCP server**：MCP 由用户级配置（`~/.zcode/cli/config.json` 的
`ocdup` 条目）持有。ZCode 对插件 MCP 键名做命名空间处理，若插件再声明一份
会出现双进程 + 工具重复，还可能争抢 6666 端口上的 OpenOCD——所以插件只带
知识层，装/卸不影响 MCP。

## 前置：MCP server 注册

先按仓库根 `README.md` 的「在 ZCode 中注册」一节把 `ocdup` 加进
`~/.zcode/cli/config.json` 的 `mcp.servers` 并重启 ZCode；没有这一步，插件
的 skill 只会讲流程而调不到工具。

## 安装（本地市场）

1. ZCode → Settings → Plugin Management → Discover → `+` → 添加本地目录
   `D:\WORK_VSCODE\Vibe-coding\Ocdup\plugins`（含 `marketplace.json`）
2. 安装并启用 `ocdup-zcode`（会话自动重载）
3. 验证：新会话里输入 `/ocdup-status`；或说「帮我烧录固件」看 skill 是否触发

## 与其他副本的关系

| 副本 | 作用域 |
|------|--------|
| 本插件 `skills/ocdup-flash/` | ZCode（仓库版本化，真源） |
| DSH 侧 | 暂无 skill，仅 `cordis.patch.yml` 的 MCP 注册 |
| `docs/design-plan.md` | 设计依据（工具行为与实测记录） |

改 skill 内容：改本插件副本 → `marketplace.json` 与 `plugin.json` 同步 bump
version → 在 ZCode 里 Refresh marketplace + 更新插件。
