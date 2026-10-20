# dsh-ocdup-panel — Ocdup 调试实时面板  v1.5.0

DeepSeek Harness 的常驻插件:右侧停靠栏的 OCDUP 调试 tab,实时显示
**AI 通过 Ocdup MCP 工具与 OpenOCD 的全部交互**(TCL 命令、响应、后端生命周期事件),
并能手动断开/恢复后端把探针让给别的 agent 软件。
数据层与 [dsh-serial-panel](../../..) 同构——唯一真源是共享 JSONL,UI 可插拔。

## 数据链路

```
ocdup(src/Ocdup,OpenOCD MCP 服务器)
  → TrafficLog 追加 %LOCALAPPDATA%\Ocdup\mcp-traffic.jsonl(逐条落盘,2MB 轮转 .1;
    轮转时面板自动把 .1 尾部接进环,历史不白屏——见 v1.3.6)
  → 本插件 Host 半区尾随增量(默认 400ms 轮询)
  → 同源私有路由 GET /api/ocdup-log?since=<seq>
  → 浏览器面板自适应增量拉取渲染(有数据 700ms / 空闲 2.5s)
```

## 面板内容

| 列 | 说明 |
|---|---|
| 时间 | 记录时刻(详情区含完整日期) |
| 方向 | `cmd`(琥珀,TCL 命令)/ `rsp`(绿,响应)/ `sys`(蓝,后端生命周期)/ `err`(红,通道错误) |
| 色点 | 目标 `host:port` 稳定色相,可点击 = 过滤该目标 |
| tool | 发起的 MCP 工具名(`flash_write` / `read_memory` / `backend`…) |
| Δt | 距上一条的间隔(烧录/读内存耗时一目了然) |
| text | 命令或响应文本(详情区全文 + 一键复制) |

工具栏:暂停 / 跟随(翻历史自动挂起+新数据浮出)/ 方向通道 chip + 目标过滤 / 正则搜索 /
清空(两段确认)/ 导出 JSONL;左侧图标带「有新流量」圆点;快捷键
空格=暂停、`/`=搜索、Esc=取消选中、↑/↓=可见行间移动选中(循环)、Enter=复制选中行全文。

## v1.2.0 新增(插件优化,仅动本目录,C# 服务端零改)

| 能力 | 说明 |
|---|---|
| 调用配对视图 | 工具栏「流水 / 调用」切换:cmd↔rsp/err 按 tool 流式配对(控制通道串行化,无歧义),期间的 openocd `log` 行归入该调用——一次 `flash_write` = 一行,详情区看 cmd / openocd 日志 / rsp 三段;无法配对的行原样保留不丢数据 |
| 烧录进度条 | 解析 openocd 日志(`erased sector` / `wrote N bytes` / `verified N bytes` / `** Verified OK **`)→ 工具栏进度条:擦除/写入/校验分段,完成度未知时段流光动画,完成给「✓ N 字节 · 校验 OK · Δt」;Verified OK 走 rsp 回到也算(双通道) |
| 会话分组 | `backend` 生命周期(sys)事件作分隔条,标注本会话条数,点击折叠/展开 |
| 工具 chips | 流量中出现过的 MCP 工具按频次成 chip,点选过滤(再点取消);`✕ err N` 一键只看错误 |
| 关键行高亮 | 烧录/校验成功行(绿底 ✓)、err 行(红底 ✕)整行标记 |
| 界面改版 | 方向色条、cmd/rsp 底纹交替、stats 五色占比条、pill 徽章、空态/详情区卡片化 |

数据契约不变:Host 仍是哑管道(尾随 JSONL + 同源路由),配对/进度/会话/chips
全部在 Client 侧从现有条目派生——`ocdup`(C#)与 `mcp-traffic.jsonl` 零改动。

## v1.2.1 新增:链路状态区

工具栏下方常驻一行「链路」,从历史流量解析最近一次:

| 段 | 数据源 | 显示示例 |
|---|---|---|
| 探针 | `connect` 的 backend SYS(`probe auto-detected: …`) | `探针 CMSIS-DAP`(悬停看接口脚本 + evidence VID:PID) |
| 芯片 | `identify_chip` 的 rsp | `芯片 Cortex-M4 r0p1 · dev 0x413 · AT32F403A` |
| Flash | `flash_info` 的 rsp(bank 行) | `Flash bank 0 0x08000000 · 1 MB`(多 bank 悬停列全) |

- 三项各取**最近一次**成功解析的结果;没做过对应调用就不显示该段;
- 点段跳转到来源:探针→搜索 `probe auto-detected`;芯片/Flash→调用视图按工具过滤;
- 面板本身永远不主动连硬件——这里显示的仍是「AI 已经做过的事」的镜像。

## v1.2.2 配套 ocdup v0.3.16:connect 自动识别芯片

`connect` 成功后 ocdup 会 best-effort 自动读 CPUID + DEV_ID(3 秒短超时,目标不
应答只补一行说明、绝不拖垮连接),把 `chip identity:` 块镜像进流量日志;面板据此
在**每次连接后自动填上芯片段**——不用再等 AI 手动调 identify_chip。

- 芯片来源放宽:`identify_chip` 的 rsp,或 connect 自动识别的块(按文本形态认);
- 探针兼容**钉死 interface_cfg**:启动文案 `(interface: <cfg>)` → 显示接口脚本名
  (物理型号未识别是 auto 专属;auto 模式仍是 `kind + evidence`);
- 链路行缺段时给「未识别 · 复制提问」占位——点一下复制提问粘回会话即可
  (面板不能给 AI 发消息);
- `family: unknown` 时原样显示:在 ocd.json 的 `chip_id_map` 补该 DEV_ID 即显示家族名。

## v1.2.3 界面美化 + 链路三段「说人话」(配套 ocdup v0.3.17)

工具栏与链路行整体改版(只动 Client 半区,Host 仍是哑管道):

| 改动 | 之前 | 之后 |
|---|---|---|
| 探针段 | `cmsis-dap.cfg`(文件名) | `CMSIS-DAP`(按接口脚本映射:cmsis-dap→CMSIS-DAP、jlink→J-Link、stlink→ST-Link、atlink→AT-Link…;脚本名进 tooltip) |
| 芯片段 | `Cortex-M (partno 0xed) … · dev 0x000`(原始寄存器读数) | `PY32F002A · Cortex-M0+`:identify_chip 新增 `model (from target_cfg …): XXX` 行(后端从钉死的 target_cfg 文件名推定,面板标注来源);都认不出 → 型号 + 琥珀色「型号未识别」 |
| dev 0x000 | 照原样显示 | 不显示(寄存器不存在时读回了地址本身,不是有效读数) |
| Flash 段 | `bank 0 0x08000000 · 1 MB` | `1024 KB`(bank/基址/驱动名收进 tooltip) |
| 工具栏 | 各控件高度/圆角不一,开关态整块填黑 | 统一 28px 控件 + hover 浮起 + focus 环;开关态改品牌色浅底;「确认清空?」变危险红;统计做成状态胶囊;搜索框内嵌放大镜 |
| 链路行 | 虚线 pill | 带图标的小卡片(探针=信号 / 芯片=纹样 / Flash=叠层),hover 描品牌边;缺段占位同款卡片 |

C# 服务端只加一处:`IdentifyTools.TryIdentifyAsync` 在报告里追加
`model (from target_cfg <file>): <MODEL>`(`DeriveModelFromTargetCfg` 纯函数,
`target_py32f002a_lock.cfg → PY32F002A`;auto/不像型号 → 不加行),仍全是只读。

## v1.2.4「台架仪器」界面改版(纯 Client 侧,C# 零改)

按 frontend-design 定向:示波器/逻辑分析仪语言——发丝线、tabular 数字、
通道式列头;明确避开奶油底/陶土色、编号 01/02、全大写 eyebrow、meta 串「·」、
按钮「→」这些模板痕迹。

| 改动 | 说明 |
|---|---|
| 工具栏两行化 | 上排=看什么(状态胶囊 / 流水·调用 / 暂停 / 跟随),下排=过滤与操作(方向·目标·正则·搜索·清空·导出 + 计数),行间发丝线;治单行 wrap 在窄侧栏把控件冲散 |
| 链路「规格读数条」 | 三枚方角(4px)发丝线读数卡:仪器图标(信号弧/芯片/叠层)+ 标签 + 值;芯片卡 flex:1——型号是这行最该被读到的数;hover 描品牌边;缺段虚线警示卡 |
| 内核名缩写 | CPUID 解码串("Cortex-M4 (ARMv7-M/v8-M, implementer 'A', r0p1)")太长——卡片显示 `Cortex-M4 r0p1`(coreShort),架构细节进 tooltip |
| 列表列头条 | 流水视图加 sticky 通道表头(时间/方向/tool/B/Δt/命令·响应),列宽与行严格对齐,.op-tool/.op-len/.op-dt/.op-tag 复用容器查询——窄屏同步降级 |
| 控件皮相统一 | 28px 高 + 6px 圆角;数字(时间/len/Δt/计数/chip 计数)全 tabular-nums;工具 chips 22px pill;阴影只留「N 条新数据」浮钮 |

## v1.2.5 芯片段「只信实测」+ 链路三段上色(纯 Client 侧,C# 零改)

| 改动 | 说明 |
|---|---|
| 芯片卡只报实测 | target_cfg 文件名推定的 model **不再上卡**——用户把 target_cfg 钉错时那就是误导(实测插的根本不是那片子)。卡片改为:family 命中 chip_id_map(键=实测 DEV_ID)→ 型号 + 内核;否则 CPUID 内核 + 有效 `dev 0x344` + 琥珀「型号未识别」。model 退到 tooltip,标注「仅配置线索,非实测」 |
| 三段语义色 | 探针=sys 蓝 `#2563eb` / 芯片=rsp 绿 `#059669` / Flash=cmd 琥珀 `#d97706`——与流水行方向色同一套仪器语言;图标+标签着色,值留主文本色,hover 改中性加深(不让品牌色和段色打架);缺段占位卡同色虚线 |

**实测 DEV_ID 怎么变成型号**:把识别出的值填进 ocd.json 的 `chip_id_map`(如 `"chip_id_map": { "0x344": "<丝印/手册确认的家族名>" }`),identify_chip 下趟调用即报 family,卡片立刻显示。

## v1.3.0 配套 ocdup v0.4.0:target 注册表 + 失配切换胶囊(纯 Client 侧)

ocdup 侧(v0.4.0)新增：`target_presets` 注册表(家族名 → openocd target cfg)、新工具
`set_target <家族名>`(校验 → 杀 ocdup **自拉**的 openocd → 运行时切 target_cfg → 重启
后端;read_only 拦截;外部实例占着端口时如实警告)、identify 报告末尾追加失配行、
connect 成功后顺带 best-effort `flash probe 0`+`flash banks`(Flash 段不用等 AI 调)。

面板侧只加一样：**「⚡ 切换 <家族名>」胶囊**——链路行解析到失配行时出现，点击把
`mcp__ocdup__set_target <家族名>` 复制到剪贴板(面板不能调 MCP，这是它和硬件之间
唯一的交互形式：复制提问，不直连)。胶囊跟着**最近一次 identify 报告**走：切完 target
后新的 identify 没有失配行，胶囊即消失。

## v1.3.1 精修(纯 Client 侧)

| 改动 | 说明 |
|---|---|
| Flash 段报**合计** | `parseFlashInfo.total` = 各 bank 容量之和——双 bank 芯片(at32f403axG:bank1 512K + bank2 512K)一片多大一眼可见，各 bank 明细进 tooltip;配合 ocdup v0.4.1 的「逐个 bank probe」(实测 `flash probe 0` 不带动 bank1) |
| 工具栏下排分组 | 方向/目标/正则 | 搜索 | 清空/导出 | 计数,组间发丝线;窄屏整组换行,不再冲散单个控件 |
| stats 图例 | 方向计数改「色点 + 标签 + 数」(与流水行方向色同一套语言),替掉 '·' 串 meta |
| 调用视图列头条 | 两个视图都有吸顶通道表(调用视图:时间/状态/调用/Δt/摘要) |
| 链路卡左色条 | 卡片左侧 2px 段色条(仪器通道条语言),不只靠图标着色 |

## v1.3.2 界面精修:按钮 / 文字 / 配色(纯 Client 侧)

| 改动 | 说明 |
|---|---|
| 修控件折行 | 所有控件 `nowrap + flex:0 0 auto`,工具栏两行都允许**整组**换行——旧版窄面板把「暂停/跟随」挤成上下两行、状态胶囊里的方向计数被裁掉半截 |
| engaged 墨色实底 | 选中/开关态统一 `label-primary 底 + bg-layer 字`(浅色主题=黑底白字,深色主题自动反转),替掉品牌色浅底——DSH 主题里 `bg-layer-4` 缺席,旧版会露出 `#eef1fe` 蓝底配墨色描边的撞色;选中行底色也改品牌色 mix |
| 方向通道 chip | 方向过滤从原生 `<select>` 变 chip(`cmd 69` / `rsp 78` … 带色方点 + 计数,点中即过滤、再点取消),与工具 chip 同一套交互语言;顺带去掉与占比表重复的图例、`SYS` 统一成 `sys` |
| 工具条单行横滚 | 工具 chip 条改单行横滚(右缘渐隐提示还有内容 + 滚轮转横向)——旧版 11 枚 chip 换行成 3~4 行,窄面板吃掉半个屏幕;清空/导出/计数挪到这条带右侧 |
| 搜索框内嵌正则 | `.*` 开关做进输入框右端,不再是一行里孤零零的按钮;占位文案收敛为「过滤 命令 / 响应」(工具过滤交给 chip 条) |
| 目标下拉按需出现 | 只有一个目标且没在过滤时不占位;计数文案改单调式(「320 条 · 渲染 300」,替掉两个斜杠的「渲染 300 / 320 / 320 条」) |
| 链路读数条层级翻转 | **值**(型号/容量/探针名)做主读数(主文本 + 600 字重 + tabular),段名退成弱标签,段色只留左色条 + 图标——旧版是彩色粗体段名 + 灰值,读起来本末倒置;芯片补充读数(内核/dev id)在 ≤620px 整段隐藏(不做半词截断),型号未识别时芯片图标转警示色 |
| 传输控件 SVG | 暂停/继续/跟随到底改内联 SVG,不再依赖 `⏸`/`▶` 的字形;详情区段名与通道色对齐(`log` 用石板灰而非琥珀),标题行去 `·` 串、去重复日期,日志条数统一成「N 条日志」 |

## v1.3.3 修复:侧边栏图标点了没反应(注册契约对齐宿主 0.2.0-rc.2)

**现象**:侧栏「ACCOM 串口」「OCDUP 调试」两个图标点了都没有反应。

**根因**(读宿主 `resources/app.asar` 的 ui-sidebar 源码逐行确认):

| 宿主的契约 | 旧插件 |
|---|---|
| 侧栏 `PanelRow` **自己**绑 `onClick → ctx.layout.selectPanel(id)`,图标只是被渲染进宿主 `<button>` 里的 glyph | `PanelIcon` 自造 `onOpen → right.openTab(...)`,以为点击由自己处理 |
| `selectPanel(id)` **只认 `main` keyed slot 里同一个 id** 的注册项,缺 key 直接抛错并保持原面板 | body 注册在 `sidebar.right.pane.tab`(`key='dsh-ocdup-panel'`),**main 槽一个都没注册**(那分支只在拿不到 rightbar 服务的老宿主才走) |
| 侧栏图标 = **全局面板**启动器;右栏 tab 是 chat/browser/document 这类 session 级内容,不经侧栏图标打开 | 点击传 `selectPanel('ocdup-log')`,而 main 里没有这个 key → 抛错 → 静默无反应 |
| —— | 附带 bug:`openTab('ocdup-log')` 传的是 `kind` 而不是 body 的 key,老宿主路径同样是错的 |

**修法**:无条件按「全局面板」注册——`sidebar.panellist` 的 `id` 与 `main` 的 `key` **严格同值**(`ocdup-log`),`inject` 只声明 `slots`;`PanelIcon.onClick` 退为老宿主兜底(带 `panelId` 调 `layout.selectPanel`)。删除右栏 tab 注册与 `openTab` 调用。

`dsh-serial-panel`(ACCOM)同一个病,同样改法(Xcom 仓库的真源)。

> 副作用(有意为之):面板从「右侧停靠 tab」变成「主视图区整屏面板」;DSH 停靠栏拖拽列宽时容器查询仍生效(≤620px 隐藏芯片补充读数、≤520px 隐藏 Δt 列等)。若要回到右侧停靠,得按新契约注册右栏 tab 类型 + guide 入口(宿主用 `ctx.sidebarRightTabs.register` + `sidebar.right.pane.tab` body,key 与 openTab 参数必须一致),而不是复用旧 `openTab(kind)` 写法。

## v1.3.4 右侧停靠回归 + 「后端」断开/恢复(纯 Client + 一条 Host 路由)

| 改动 | 说明 |
|---|---|
| 入口改右栏 guide 卡 | 左侧 `sidebar.panellist` 图标**整体移除**(全局面板在窄栏里没有使用体验),改注册右栏 tab:`ctx.sidebarRightTabs.register({id,kind:'ocdup-log',multiple:false,priority:'plugin',title,keepMounted:true,guide:[{id,order,title,description,icon}]})` + body 注册在 `sidebar.right.pane.tab`(**key = 同一个 id**;openTab 认 kind、body 认 id)。宿主没有右栏服务时退回 main 全局面板。**代价**:guide 卡只在有会话时可见(与终端/浏览器一致) |
| 链路行第四段「后端」 | 状态从 backend SYS 行派生(`backendStateOf`:`started openocd`/`already present`/`restarted` → running,`backend stopped` → stopped)。点击 = 断开(停掉 ocdup 自拉的 openocd、释放探针,**MCP 门面不断**)或恢复(= `connect`)。烧录进行中先二次确认(3s 窗口,卡片染危险色「确认断开?」);结果以一次性反馈显示在段内,并镜像进流水(cmd/rsp + backend SYS) |
| 断开/恢复怎么落地 | 面板 Host `inject: ['webServer','tools']`,新路由 `POST /api/ocdup-backend {action}` → `ctx.tools.execute({name:'mcp__ocdup__disconnect'|'mcp__ocdup__connect'})`(MCP 工具在 Host 侧就是普通工具,`mcp__<serverName>__<tool>`;10s 超时)。配套 ocdup v0.4.6 新增 `disconnect` 工具:`read_only` 会话会被拦成 BLOCKED(停机=改变后端状态,与 set_target 同例) |
| 窄停靠排版 | 芯片卡 grow 改 class `.op-link-grow`,≤560px 收回自然宽;四张卡在 ~340px 停留下排 2~3 行,文字不截断 |
| 删除的死代码 | `PanelIcon` 组件与 `badgeStore`(未读徽章依赖左侧图标承载) |

> 想手动释放探针时:点「后端 → 已连接」即断开;别的软件用完,AI 调 `mcp__ocdup__connect` 或再点一次即可拉回。`read_only: true` 时断开被 ocdup 拦下(按钮会显示 BLOCKED 文案)。

## v1.3.5 后端段结论压缩 + 链路行 2×2(纯 Client 侧)

用户截图反馈:断开后「后端」卡把一整句英文结论(`backend was not running (nothing on 127.0.0.1:6666) - nothing to disconnect.`)铺在卡片外,把链路行撑爆;窄停留下这张卡还独自占一行。

| 改动 | 说明 |
|---|---|
| 结论一个词 | `backendVerdict(action, ok, text)` 把 rsp 文案压成 `已断开`/`未运行`/`未释放`/`已连接`/`失败`(+语义色圆点);完整文案进卡片 tooltip,且断开动作本来就会镜像成 cmd/rsp + backend SYS 行,流水里可追溯 |
| 状态机补漏 | 「backend was not running」也判为 stopped——旧逻辑跳过该行、回退读到更早的 `started openocd`,把「没跑」显示成「已连接」;没有任何 backend 行时显示「未知」而不是假装已连接 |
| 段序调整 | 「后端」调到段首:它是这一行**唯一能动手的开关**(前置=最易点),后面三张是「板子上有什么」的数据读数 |
| 链路行 2×2 | ≤620px 换网格(`.op-linkrow` class 承载布局,刻度标签横跨两列)——flex 贪心打包会排出「4+1」这种一行挤满、一行只剩一张卡的不均衡两行;布局必须走 class,内联 `display:flex` 会压过容器查询 |
| 窄屏段名 | 「后端」段名带 `.op-seg-label`,≤560px 隐藏(图标 + 圆点 + 状态词自带信息,tooltip 补全) |

> 断开/恢复的完整语义见 v1.3.4 一节;`read_only: true` 时断开被 ocdup 拦成 BLOCKED,卡片会显示「失败」并把文案带在 tooltip 里。

## v1.3.6 轮转的历史不再消失(Host 尾随逻辑 + 空态入口)

用户截图:重启 DSH 后整个面板全空,footer 却写着「轮转文件已生成」——`.1` 里 2MB
(11,765 行)历史一行都看不到。根因有两条,都不是数据丢失:

* ocdup 侧 `TrafficLog` 用 `FileMode.Append` 开文件,**重启从不截断**;2MB 轮转
  (`RotateIfNeededLocked`)是设计内的有界策略,历史完整落在 `.1`
* 面板 Host 把「活动文件变小」一律当清空 `reset()`——轮转发生的瞬间,正在看的
  面板直接白屏;重启后更是只读活动文件,从不读 `.1`

| 改动 | 说明 |
|---|---|
| 轮转自动续接 | 尾随发现活动文件消失/收缩**且 `.1` 刚换过**(size/mtime 变化)时,判定为 ocdup 轮转:先把 `.1` 的尾部窗口(FIRST_TAIL_BYTES,默认 256KB)接进内存环,再继续跟新文件。`.1` 没动 = 面板清空或外部截断,照旧全清——两种收缩因此可区分,旧历史不会在清空后回流 |
| 防重复灌入 | 自动续接会记住这份 `.1`(size+mtime);用户再点空态「加载」对同一份返回 `note=already loaded`,条目不翻倍 |
| `POST /api/ocdup-log/rotated` | 按需把 `.1` 尾部补进环并返回新增条目(空态「加载 .1 尾部」按钮);没有 `.1` 返回 `note=no rotated file`。与自动续接共用同一条 `ingestRotated` 读取路径,不复制逻辑 |
| 空态入口 | 有条目时不算空;全空且 `.1` 存在时显示「更早的 2.0 MB 已轮转到 .1(10/7 07:22)— 当前周期还没有新流量」+ 加载按钮,加载结果以一次性备注反馈(已加载 N 条 / 已经加载过了 / 失败原因) |
| stats 新字段 | `rotSize` / `rotMtimeMs`(随 `rotated` 一起进 `view()`),footer 轮转列从没信息量的「已生成/未生成」改成 `.1 2.0 MB 10/7 07:22` / `.1 无` |
| 窗口半行修复 | `ingestRotated` 只在**从中段开窗**(`from > 0`)时丢首部半行;小文件 `from = 0` 时窗口起点就是行边界,旧写法会把第一条完整行当半行丢掉(测试抓到的真 bug) |

> 轮转 ≠ 丢数据:`.1` 恒为最近一个轮转周期的完整内容(轮转时先删旧 `.1` 再移入),
> 再往前的周期被主动丢弃——这是 ocdup 侧的有界策略,面板只负责让「最近的历史」可见。

## v1.3.7 空态也要能控后端(用户截图问「这个界面没有控制后端的开关嘛?」)

重启后还没流量(或刚清空)时,旧版链路行整行不渲染——「后端」断开/恢复开关跟着消失,
而那恰恰是最该在手边的时候:探针要不要让给别的 agent 软件,不必等 AI 先跑一轮。

| 改动 | 说明 |
|---|---|
| 链路行常驻 | 不再要求 probe/chip/flash 至少有一项。一条链路读数都没有时只留「后端」一张卡;三张占位卡说的是「日志里缺这项」,而空态是「还没有日志」,摆三张「未识别 · 复制提问」只是添乱 |
| 单卡满行 | ≤620px 网格下 `.op-linkrow > .op-link:only-of-type { grid-column: 1 / -1 }`——半张卡配右半空白看着像渲染坏了,而它那时是这一行唯一的控件(Chrome 实测 271.5px → 551px) |
| 空态点它也能用 | 没有 backend 行时状态显示「未知」,点击走 `POST /api/ocdup-backend`(无信息时先断开:本来就没跑会如实回「未运行」),结论以一个词回填卡片 |
| 修:清空后备注说谎 | `clear()` 只清 entries 不清 rotNote——「已加载 .1 尾部 1470 条」在条目被清掉后继续说谎(用户就是这么看到自相矛盾的空态)。现在一起清 |
| 修:清空后不能再加载 | Host 清空路由不重置 `rotLoaded`——「加载过 → 清空 → 再点加载」被 `already loaded` 挡住,而环里其实已经空了。现在清空即重置 |
| 加载备注独立成行 | 原来渲染在空态里,条目一加载上来空态整块消失,备注闪一下就没了;改成列表上方独立一行 + 6s 自清,空态/列表两种状态都看得见 |

> 测试 harness 同步升级成带 hook 作用域的迷你 React(每个函数组件一份 hooks,主组件
> 跨重渲染保留),于是「点按钮 → setState → 重渲染 → 看树」能真跑——T28 就这么端到端
> 复现并钉死用户那个自相矛盾画面。

## v1.3.8 失配胶囊一键切换(用户实测:「怎么连接上后自己断开了?」)

用户现场:AT32F403AVGT7 开发板插着,`ocd.json` 的 target_cfg 却钉在 423 钥匙板
(`target_at32f423_key.cfg`——连复位姿势 `connect_assert_srst` 都是按 423 调的)。
点「后端」连接 → openocd 起得来、SWD 连上、CPUID 读到 Cortex-M4 r0p1(面板显示
「连上了」),但 DEV_ID@0xE0042000 读回 **0x00000000** → at32f4xx 驱动
`flash reg address: 0x0, usd addr: 0x0` → `read at32 flash size failed, 0k flash`
→ **断言 `num_pages > 0` 失败(at32f4xx.c:958),openocd 进程直接 abort** → 控制
通道被迫关闭 → ocdup 只能报 `nothing is listening on 127.0.0.1:6666` → 面板
显示「已断开」。三次重连(14:43:45 / 14:44:01 / 14:45:10)同一条路崩到底。
`set_target AT32F403A`(→ `target/at32f403axG.cfg`,不带 srst 姿势)后 DEV_ID 读回
0x70050344,flash 双 bank 512K+512K=1MB 全通。

| 改动 | 说明 |
|---|---|
| 失配胶囊直连 | v1.3.0 时面板不能调 MCP,胶囊只会把 `mcp__ocdup__set_target X` 复制到剪贴板、要用户/Agent 粘回会话;v1.3.8 起点击 → `POST /api/ocdup-target` → Host 经 `ctx.tools.execute` 直调 `mcp__ocdup__set_target`(切换 = 用新 target_cfg 重启 ocdup 自拉的 openocd;preset 名见 ocd.json 的 `target_presets`) |
| 切换后自动重连 | set_target 重启后端后「后端」卡显示已连接、链路行却还是旧报告,再点「后端」反而变成断开;现在胶囊切换成功后父组件顺手 `connect` 一次——链路行当场刷新,胶囊随新 identify(无失配行)自动消失 |
| 烧录中二次确认 | flash 调用进行时点胶囊要 3s 内再点一次(按钮变「确认切换?」):重启后端会打断 AI 的烧录,v1.3.5 同款教训 |
| 状态机 | 切换中… / 已切换 / 失败(失败原因进 tooltip);只读会话被 ocdup 拦成 BLOCKED 时如实显示失败,且不自动重连 |

> 诊断方法论:用户问「怎么连接上后自己断开了?」——流水日志里三次 connect 全是
> `Assertion failed: num_pages > 0` 后紧跟 `control channel failed`。不是谁点了
> 断开,是 openocd 进程 abort;面板只是如实渲染 ocdup 的 `nothing is listening`。
> 根因不在二进制(.1 归档里零断言记录),在 cfg 与板子不配。

## v1.3.9 轮询链自愈(用户实测:面板视图冻死 3 分钟不刷新)

用户现场:重启 DSH 后打开面板,「后端」卡停在「已断开」不动——而后端其实在跑
(pid 1888,日志 16:06:20 有 `started openocd` 且之后无任何断开)。取证:Host 侧
ring 491 条全收齐(`?since=343` 能返回含 `started (pid 1888)` 的 148 条),客户端
视图却冻在 343 条/16:06:20——**轮询链楔死**。

| 改动 | 说明 |
|---|---|
| fetch 加超时 | 新增 `fetchWithTimeout(url, opts, ms)`(AbortController,10s)。以前一个挂起的请求会让 `inFlightRef` 永远为 true,之后每一拍都被 `if (inFlightRef.current)` 挡在 fetch 之外——连 visibilitychange 的立即刷新也救不回来,视图永久冻住;现在超时 abort 落回 catch/finally,下一拍自动恢复 |
| kick 立即补拍 | 轮询 effect 经 `kickRef` 暴露 `kick()`(清掉排队中的定时器直接 tick);切回本标签(tabVisible false→true)、窗口重新聚焦(`window` focus 事件)、visibilitychange 三处都触发——后台这些拍可能整段没收到,回前台不该还看旧状态 |
| 测试 | T30 覆盖 fetchWithTimeout 正常路径(透传 AbortSignal)与超时路径(挂起的 fetch 在 ms 后 abort 抛出) |

> 教训:单条 setTimeout 链 + 无超时的 fetch = 单点故障;`inFlightRef` 这类互斥位
> 一旦被挂起的请求占住,所有恢复入口(包括用户主动切标签)都会被它挡死。

## v1.5.0 MCP 一键开关(面板内热切换)

工具条新增「MCP 开 / MCP 关」按钮,直接启停本面板对应的 host 层 MCP 条目
(`include:mcp-ocdup`):关闭后**新会话**不再注册 `mcp__ocdup__*` 工具(30 个工具
schema 约占 6.8k token/轮,单笔最大的 token 节省),开启后随时可用。与 v1.3.8 的
target 胶囊同思路:面板动作不必「复制提问让 AI 调」。

- 切换走 `pluginManager.setPluginEnabled`(改 profile patch 的 `disabled` + **热 reload**),
  **无需重启 DSH**;状态持久化在 patch 里,重启后保持。
- 状态实时读 loader,不存偏好——外部改动后面板显示永远说真话。
- **已开会话的工具列表是快照**,切换只对新会话生效(UI 已提示)。
- 停用时空态显示引导文案;`pluginManager` 服务缺失时按钮置灰。
- 新增路由:`GET /api/ocdup-log/mcp`(读状态)/ `POST /api/ocdup-log/mcp` `{enabled:bool}`(切换)。

## v1.4.0 全项目复查面板侧:三个 P1 + 八个边角

对照 ocdup v0.4.30 的全项目优化轮,面板代码(client.js / index.js)做了一轮独立
复查(约 2300 行),修三个用户日常路径上的 P1 与八个边角。全部有测试钉死:
四模式 210 → **234 项断言**(core 23 / window 5 / api 37 / client 169)。

| # | 级别 | 问题 | 修法 |
|---|---|---|---|
| 1 | **P1** | **默认(流水)视图没有烧录进度条与「烧录中」保护**:`flashCall` 只从 `basis` 取,而流水视图的 basis 是原始行(没有 `kind=call` 项)→ `lastFlashCall` 恒 null → 进度条不渲染;连带断开后端 / 切换 target 两处「烧录进行中二次确认」也永不触发。只有手动切到「调用」视图这些才活 | 配对结果单独提 `paired` memo,`flashCall` 两种视图都从它取;「仍是最新动态」提纯为 `flashStillLatest`(调用视图对象同一性 / 流水视图应答行 seq 相等,新函数进 `_test`) |
| 2 | **P1** | **后端卡片显示陈旧的「已连接」**:`backendStateOf` 只认成功形态的 SYS 行;启动失败(`did not become ready`)/端口无监听(`nothing is listening`)/TCL-RPC 通道打不开/心跳丢失(`heartbeat lost`)都不匹配 → 循环跳过失败行扫到更早的 `started openocd`。拔线或换错 cfg 后再调任意硬件工具,卡片继续撒谎(与 v1.3.8「连上就自己断开」同族) | 失败文案归一成 `stopped`;反例「外部实例杀不动」(still running)归 `running`——监听确实还活着 |
| 3 | **P1** | **轮转漏救两侧丢行**:判定只看「活动文件收缩」。轮转后新文件若在一拍内长过旧 offset(慢盘/挂起/大 POLL_MS 完全可达),收缩不触发 → 既不救 .1 尾部(白屏),又把新文件前 offset 字节整段跳过(头部丢) | 判定升级为「.1 换了 + 活动文件自上一拍被重写过(mtime)」→ 救 .1 + 新文件从头读。**信号选型踩坑**:先用 birthtime,实测 NTFS 隧道效应会让「改名后同名重建」的文件继承旧创建时间(信号失效),last-write 时间不受影响才可靠 |
| 4 | P2 | 探针段误认失败文案:`BuildFailureMessage` 里嵌 probeNote,`deriveLinkInfo` 照解析 → connect 失败后「探针」卡显示一个像活着的链路 | 只从非 `ERROR` 开头的 SYS 行解析探针 |
| 5 | P2 | kick 竞态泄漏定时器:`schedule` 排新定时器不清旧的 → 每竞态多一条无主轮询链 | 排新的先 `clearTimeout` |
| 6 | P2 | target/backend 两个 POST 没有客户端超时:Host 的 10s abort 只在宿主 `tools.execute` 认 signal 时收得住,不认时按钮永久卡在「切换中…」 | 两处 fetch 加 AbortController(15s)+ T29b 断言 signal 在场 |
| 7 | P2 | 用户正则对完整 4000 字符(调用视图拼 cmd+rsp+logs ~12KB)跑,灾难性回溯冻结 UI;且命中落在显示截断尾部时「行保留但高亮丢」 | 匹配主题与行显示同源(都截到 `ROW_PAYLOAD_MAX`);顺带把 `matcherHit` 契约收紧为恒布尔(空 tag 时曾返回 `''`) |
| 8 | P2 | 芯片卡只剩 dev_id 时(CPUID 读失败但 DEV_ID 读到)显示写死的「已连接」 | 提纯 `chipFallbackLabel`:有效 dev_id → `dev <id>` |
| 9 | P2 | gap 行不服从 port 过滤:按 host:port 筛时仍插一条「已省略 N 条」 | gap 插入条件补 `port === 'ALL'` |
| 10 | P2 | 字面量 `null` 行让 `consume` 抛异常(`rec.direction` 对 null 取属性),而 `offset` 已推进 → 该批后续行永远丢失(静默丢数据) | 非对象记录按坏行计,consume 不再抛 |
| 11 | P2 | Host 入口不校验 text/tool 类型:非字符串透传会让客户端 `e.text.length` / `e.tool.toLowerCase()` 直接崩 | 入口归一成字符串(`safeText` 兜底序列化) |

## 目录职责

**唯一真源 = 本目录(Ocdup 仓库 `tools/dsh-ocdup-panel/`)**,纳入 git 版本管理。
`%USERPROFILE%\.dsh\profiles\` 下的所有副本都只是**部署目标**:

| 路径 | 说明 |
|------|------|
| `~\.dsh\profiles\desktop\dsh-ocdup-panel\` | 部署目标(desktop profile 源目录) |
| `~\.dsh\profiles\desktop\node_modules\dsh-ocdup-panel\` | 部署目标(安装闭包,DSH 实际加载) |
| `~\.dsh\profiles\web\...`(两处) | 浏览器形态 dsh 的部署目标(`deploy.cmd web`) |

改代码只改本目录,然后:

```cmd
deploy.cmd        :: 部署到 desktop profile
deploy.cmd web    :: 追加部署到 web profile
```

再重启 DeepSeek Harness(loader 启动时读取,HMR 默认关闭)。
注册:`cordis.patch.yml` 里的 `- insert: - id: dsh-ocdup-panel`(desktop 与 web 各一条)。

## 测试

Host 半区带一套零依赖行为测试,在插件目录下运行(每个模式自设所需的环境变量
(如 `MAX_ENTRIES=5/40`、`FIRST_TAIL=600`),日志默认落在临时目录——零配置直接跑,
也绝不碰真实的流量日志;要压别的边界时手动设置这些 env 即可覆盖):

```cmd
node test.js core     & node test.js window & node test.js api & node test.js client
```

- core:方向归一化(SYS→sys)/ 半行拼接 / since 切片 / 批量裁剪 / 清空与方法守卫 / 坏行容错
- window:首次挂载只读尾部窗口(大日志不全量读入)
- api:Δt 乱序钳 0、坏时间戳、码点截断、jsonl/csv 导出、help 路由、
   轮转续接(活动文件收缩 + .1 换过 → 救援尾部;.1 未变 → 按清空处理不回流)、
   `/rotated` 路由(真加载 / already loaded 防翻倍 / 清空后可再加载 / 方法守卫)、
   `/api/ocdup-target`(方法守卫 / 缺 preset 400 / 正常路径回显 preset+文本 /
   tools.execute 参数形态 / 工具 isError→502)
   client:stub React 冒烟(双注册分支、高亮、正则、徽章共享态、面板整体渲染)
  烧录进度解析(erased/wrote/verified 各形态)、调用配对(ok/err/open/孤立)、
  调用耗时、会话分隔与折叠、工具清单排序、关键行标记、
  链路状态解析(探针三要素/芯片四字段/flash bank/fmtSize/三项各取最近)、
   探针硬件名映射(probeLabel/friendlyProbeName)、芯片段标签(只信实测:
   family→型号/否则内核+dev+未识别/cfg model 不上卡)、Flash 段容量(fmtKb)、
   内核名缩写(coreShort)、target 失配行解析(parseTargetMismatch)、
   flash 容量合计(total,双 bank 1024K 实况)、方向图例(dirLegend)、
    计数文案(counterLabel)、芯片读数分层(chipLabel head/tail)、后端状态派生
    (backendStateOf)与结论压缩(backendVerdict)、注册契约(右栏 tab 类型/body key=id/guide 无 commandId/
    左侧不注册 panellist/无右栏服务退回 main)、轮转空态与加载入口(有/无 .1 两支、点击走 POST /rotated、fmtStamp 时间格式)、
   失配胶囊一键切换(点击走 POST /api/ocdup-target {preset} → 成功后自动 connect → 失败不重连)
   ——client 模式 169 项,四模式合计 234 项断言。

v1.4.0 新增覆盖:null 行不吞后续批 + 非字符串 text/tool 归一(core)、轮转后新文件
变大也救 .1 且头部不跳(api,T12c)、flashStillLatest 两视图语义(client,T17b)、
失败 SYS 行归 stopped / 外部实例 still running 归 running(T23b)、探针段不解析
失败文案 + 芯片卡 dev 回退(T19b)、匹配主题与显示同截断 + matcherHit 恒布尔(T14)、
target/backend POST 带 signal(T29b)。

用环境变量把参数压小是为了快速触发边界(上限 5 条即可验证裁剪)。
`test.js` 只存在于真源目录,不同步到安装闭包。

## 可调环境变量(Host 半区读取)

| 变量 | 默认 | 说明 |
|------|------|------|
| `DSH_OCDUP_PANEL_MAX_ENTRIES` | 5000 | 内存环上限 |
| `DSH_OCDUP_PANEL_POLL_MS` | 400 | 尾随文件轮询间隔 |
| `DSH_OCDUP_PANEL_FIRST_TAIL` | 262144 | 首次只读的尾部字节窗口 |
| `DSH_OCDUP_PANEL_FIRST_LIMIT` | 300 | 首屏/回退返回条数 |
| `DSH_OCDUP_PANEL_MAX_LINE` | 1048576 | 单行残留保护阈值 |
| `DSH_OCDUP_PANEL_LOG` | %LOCALAPPDATA%\Ocdup\mcp-traffic.jsonl | 日志路径覆盖 |

设置方式:`profiles\<profile>\cordis.patch.yml` 里给 dsh-ocdup-panel 条目加
`config` 不生效(插件读的是进程环境),需在启动 DSH 前设置环境变量。

## agent 自发现

```
GET /api/ocdup-log/help          # 接口速见(markdown)
GET /api/ocdup-log?stats=1       # 零载荷存活检查
GET /api/ocdup-log?since=<seq>&compact=1&max=512
GET /api/ocdup-log/mcp           # MCP 条目启停状态;POST {enabled:bool} 热切换(见 v1.5.0)
```

## 安装 / 卸载

- 安装:`deploy.cmd`(+`web`)+ 两个 profile 的 `cordis.patch.yml` insert 块,重启 DSH。
- 卸载:删除上述部署目录 + patch.yml 里的 insert 块,重启 DSH。
