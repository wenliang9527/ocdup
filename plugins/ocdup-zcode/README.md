# ocdup-zcode — Ocdup 烧录工具链 ZCode 插件

把 Ocdup 对 agent 的**知识层 + 可视化面板**打包成 ZCode 插件。装好后 ZCode 会话
自动获得烧录操作的流程纪律、两个快捷命令，以及一个 MCP App 调试面板。

## 组件

| 组件 | 内容 |
|------|------|
| skill | `ocdup-flash`：标准烧录流程（connect → flash_info → flash_write → 读回验证，失败 halt→读→resume）、verify_image 独立校验、read_memory format=json、地址/宽度与相对路径惯例、权限闸纪律（BLOCKED → show_config → 改 ocd.json → reload，含 allow_exec 等五个写类开关）、探针 auto 识别边界（DAPLink 必走 cmsis-dap.cfg 的 A/B 实测定论）、端口纪律（6666 单实例，DSH/ZCode 共享后端）、backend_log 异步日志、烧录后串口验证（Ocdup × acccom 会话层协同三步） |
| command | `/ocdup-status`：后端状态 + 权限生效值（不启动任何进程） |
| command | `/ocdup-flash`：引导式烧录（连探针 → 确认布局 → 烧录 → 读向量表） |
| command | `/flash-verify`：烧录后串口验证（记 acccom 水位 → 烧录/上电 → 发指定帧 → traffic_log 复盘；方案 A 会话层协同） |
| MCP App 面板 | `panel` 工具打开的调试面板（**直接开在侧栏**）：连接状态（后端/目标核/芯片识别，含断开释放探针、target 失配一键切换）、Flash 扇区图（真实扇区几何 + 烧录/擦除精确高亮 + 独立校验）、寄存器查看器（SVD 位域解码 + 名称/地址过滤，路径与选中外设持久化）、操作时间线（可复制导出）、可选自动刷新（默认关闭，避免给 ocdup 流量日志刷噪音）|

## MCP 架构：代理服务器 + 用户级 ocdup

插件清单里声明的 `ocdup-panel` 是**代理服务器**（`panel/server/server.mjs`）：

- UI（沙箱 iframe）只 `callServerTool` 本插件声明的服务器上的 `ocdup_*` 工具；
- 代理**自行拉起用户级 ocdup** 直连转发：MCP 的 `tools/call` 只有 client→server
  方向，宿主 client 不受理 server 侧回发的调用（实测回 "Method not found"），
  因此代理在首次转发时按 `~/.zcode/cli/config.json` → `mcp.servers.ocdup` 的
  启动参数（command/args/env）经 `StdioClientTransport` 拉起 `publish\ocdup.exe`
  子进程直连——惰性单飞（开面板不拉进程，首次调用才连；并发共享一条连接；
  失败不缓存、下次重试）；
- 该实例与宿主拉的 ocdup 同为 OpenOCD 门面，经 127.0.0.1:6666 TCL 共享同一后端
  （6666 单实例端口纪律，DSH/ZCode 共享），不重复拉 OpenOCD；
- `ocdup_*` 工具带 `_meta.ui.visibility: ["app"]`——只在面板内可见，不占模型上下文；
- 烧录等长操作的进度通知从 ocdup 桥接回面板（`onprogress` ↔ `mcpReq.notify`）；
- 面板 HTML 离线打包为单文件（`panel/dist/panel.html`，≤4 MiB，无 CDN 引用），
  由代理作为 MCP App 资源（`ui://ocdup-zcode/panel.html`）下发。

**用户级 `ocdup` 服务器仍然是数据面的唯一真源**（与 DSH 共享）；插件不重复声明
`ocdup` 本体，装/卸插件不影响已有 MCP 配置。前置条件不变：先按仓库根 `README.md`
的「在 ZCode 中注册」把 `ocdup` 加进 `mcp.servers` 并重启 ZCode；配置缺失/
`enabled: false`/子进程启动失败都会以可行动的 ERROR 文本显示在面板上，不空白。

## 面板构建（改 UI / 代理后才需要）

```bash
cd plugins/ocdup-zcode/panel
npm install        # 依赖：@modelcontextprotocol/client + ext-apps + server（版本锁在 package-lock）
npm run typecheck  # tsc --noEmit
npm test           # 47 用例（解析器 13 / 代理 e2e 14 / UI 冒烟 20）；node --test，离线；e2e 含真实 spawn 子进程链路
npm run build      # esbuild 内联为单文件 dist/panel.html（校验 ≤4 MiB + 无外部引用）
```

构建产物 `panel/dist/panel.html` 随仓库分发（`node_modules/` 已 gitignore），
普通安装不需要本机执行构建。

## 安装（本地市场）

1. ZCode → Settings → Plugin Management → Discover → `+` → 添加本地目录
   `<Ocdup 仓库目录>\plugins`（含 `marketplace.json`，即本仓库的 `plugins/` 目录）
2. 安装并启用 `ocdup-zcode`
3. 验证：新会话里输入 `/ocdup-status`；或让 AI「打开 Ocdup 调试面板」→ 调 `panel` 工具

## 更新（不需要卸载重装）

目录型市场有两层副本，**顺序不能反**：先刷新市场（把仓库内容重新拷进
`marketplaces/<market>/`），再更新插件（从市场存储拷进 `cache/<market>/<plugin>/<version>/`）。
只点「更新插件」而不刷新市场，拿到的仍是上一次的旧副本。

```powershell
# CLI（推荐，等价于界面里的「刷新市场」+「更新插件」）
node <ZCode安装目录>/resources/glm/zcode.cjs plugins marketplace update ocdup-local
node <ZCode安装目录>/resources/glm/zcode.cjs plugins update ocdup-zcode
```

界面等价路径：Plugin Management → 市场右侧 ↻ 刷新 → 插件卡片点「更新」。
之后**重启 ZCode** 生效（更新提示会写明 "Restart zcode to apply"）。

维护纪律：改完面板/知识层 → `npm test` + `npm run build` → 同步 bump
`plugin.json` 与 `marketplace.json` 的版本号 → 上面两条命令 → 重启。

## 面板手动验证清单（真机 / 无板分两档）

有板（AT32F403A + DAPLink）：

1. **工具启动**：会话里调 `panel` → 面板**直接开在侧栏**（`_meta.ui.surface` +
   `preferredDisplayMode:"fullscreen"`），连接状态区几秒内显示「运行中 +
   AT32F403A」，目标核表格有 `at32f403axx.cpu running`；后端未启动时点「连接」
   → 真正拉起 OpenOCD 并自动刷新
2. **主题**：ZCode 切深色/浅色主题 → 面板跟随（`data-theme` 属性切换）
3. **语言**：宿主 locale 为 zh-CN 时文案为中文
4. **Flash 扇区图**：`flash_info` 后 bank 块渲染；让 AI 烧录 `build/fw.hex` →
   时间线出现 flash_write 条目，烧录涉及的地址块高亮，「烧录成功（读回校验通过）」结论条出现
5. **寄存器查看器**：确保 ocd.json 配了 `svd_path` → 寄存器页选 RCC → 读 CR 出位域；
   **目标核 running 时读取按钮置灰并提示「需先 halt」**，点 Halt 后恢复可读
6. **破坏性操作二次确认**：擦除扇区 / 烧录 / 写寄存器 / 复位运行都弹确认框，
   取消后不发起任何调用；写 SVD 只读寄存器会被 ocdup 拒（按钮带提示）
7. **关闭重开状态恢复**：切到「寄存器」标签 + 选中某外设 → 关闭面板 → 重新打开
   → 停在「寄存器」标签且外设选中被还原（widget state）
8. **失败路径**：禁用用户级 `ocdup` server 后重开会话 → 面板操作报
   「ERROR: 调用 ocdup … 失败」指引文案，面板不空白不崩；对运行中的后端执行
   需要 halt 的读 → 错误以红条 + 原文呈现

无板：第 1 条会显示「未运行」+ 连接失败原因；寄存器/Flash 页显示空态文案，
时间线仍记录失败条目——这本身就是失败路径验证的一部分。

## 与其他副本的关系

| 副本 | 作用域 |
|------|--------|
| 本插件 `skills/ocdup-flash/` | ZCode（仓库版本化，真源） |
| DSH 侧 | 暂无 skill，仅 `cordis.patch.yml` 的 MCP 注册；DSH 有独立的 traffic-log 实时面板（`tools/dsh-ocdup-panel`） |
| `docs/design-plan.md` | 设计依据（工具行为与实测记录） |

改 skill / 面板内容：改本插件副本 → `marketplace.json` 与 `plugin.json` 同步 bump
version（改面板后先在 `panel/` 里 `npm run build`）→ 在 ZCode 里 Refresh
marketplace + 更新插件（无效则卸载重装）。
