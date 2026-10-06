# dsh-ocdup-panel — Ocdup 调试实时面板  v1.3.2

DeepSeek Harness 的常驻插件:在侧边栏加一个 🎛 面板入口,右侧停靠栏实时显示
**AI 通过 Ocdup MCP 工具与 OpenOCD 的全部交互**(TCL 命令、响应、后端生命周期事件)。
数据层与 [dsh-serial-panel](../../..) 同构——唯一真源是共享 JSONL,UI 可插拔。

## 数据链路

```
ocdup(src/Ocdup,OpenOCD MCP 服务器)
  → TrafficLog 追加 %LOCALAPPDATA%\Ocdup\mcp-traffic.jsonl(逐条落盘,2MB 轮转 .1)
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
- api:Δt 乱序钳 0、坏时间戳、码点截断、jsonl/csv 导出、help 路由
   client:stub React 冒烟(双注册分支、高亮、正则、徽章共享态、面板整体渲染)
  烧录进度解析(erased/wrote/verified 各形态)、调用配对(ok/err/open/孤立)、
  调用耗时、会话分隔与折叠、工具清单排序、关键行标记、
  链路状态解析(探针三要素/芯片四字段/flash bank/fmtSize/三项各取最近)、
   探针硬件名映射(probeLabel/friendlyProbeName)、芯片段标签(只信实测:
   family→型号/否则内核+dev+未识别/cfg model 不上卡)、Flash 段容量(fmtKb)、
   内核名缩写(coreShort)、target 失配行解析(parseTargetMismatch)、
   flash 容量合计(total,双 bank 1024K 实况)、方向图例(dirLegend)、
   计数文案(counterLabel)、芯片读数分层(chipLabel head/tail)
   ——client 模式 116 项,四模式合计 158 项断言。

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
```

## 安装 / 卸载

- 安装:`deploy.cmd`(+`web`)+ 两个 profile 的 `cordis.patch.yml` insert 块,重启 DSH。
- 卸载:删除上述部署目录 + patch.yml 里的 insert 块,重启 DSH。
