'use strict'
// ============================================================
//  dsh-ocdup-panel — Client 半区(浏览器面板,永久 cordis 插件)  v1.4.0
//
//  v1.4.0:对照 ocdup v0.4.30 全项目复查(面板侧 3×P1 + 8×P2,断言 210→234),
//  Client 半区收 P1-1/P1-2 与 P2-4~9:
//   * P1-1 默认(流水)视图也有烧录进度条与「烧录中」保护:配对结果单独提
//     paired memo,flashCall 两种视图都从它取(以前流水视图的 basis 是原始行、
//     没有 kind=call 项 → lastFlashCall 置 null,进度条不渲染;断开后端 /
//     切换 target 两处「烧录中」二次确认也不触发);「仍是最新动态」提为纯函数
//     flashStillLatest(调用视图比对象同一性/流水视图比应答行 seq);
//   * P1-2 后端芯片段不再显示陈旧「已连接」:启动失败(did not become
//     ready)/端口无监听(nothing is listening)/TCL-RPC 通道打不开/心跳丢失
//     (heartbeat lost)统一归一成 stopped;仅当外部实例 still running 才当
//     running——以前只认成功连上的 SYS 行,循环跳过失败行扫到最早的
//     started openocd,断线或换错 cfg 后芯片一直显示已连接;
//   * P2-4 探针段只从非 ERROR 开头的 SYS 行解析(启动失败文案里也嵌
//     probeNote,deriveLinkInfo 死等解析 → connect 失败后「返回」卡显示一个
//     像活着的链路);
//   * P2-5 kick/tick 排新定时器前先 clearTimeout——收尾竞态曾各自多挂一条
//     无主线程链;
//   * P2-6 target/backend 两个 POST 加客户端 AbortController 15s——Host 的
//     10s abort 只在宿主 tools.execute 传 signal 时才收住,按钮曾卡「切换中」;
//   * P2-7 匹配主题截到 ROW_PAYLOAD_MAX 与行显示同源(命行落在截断尾部时
//     高亮脱);matcherHit 拆细粒度标签(空 tag 时视图返回 '');
//   * P2-8 芯片回退标签 chipFallbackLabel:只剩 dev_id 时显示 dev <id>,
//     不再显示蹩脚的「已连接」;
//   * P2-9 gap 行服从 port 过滤(port === 'ALL' 才插)。
//
//  v1.3.8:失配胶囊从「复制命令」升级为「一键切换」(用户实测:403 板插着、
//  cfg 钉在 423 钥匙板上 → connect 每次崩 openocd,「连上就自己断开」):
//   * SwitchTargetBtn 点击 → POST /api/ocdup-target → Host 经 ctx.tools.execute
//     直调 mcp__ocdup__set_target(v1.3.0 时面板还不能调 MCP,只能复制命令让
//     用户/Agent 粘回会话);切换=重启 ocdup 自拉的 openocd
//   * 切换成功后父组件顺手 connect 一次:链路行当场刷新,胶囊随新 identify
//     (无失配行)自动消失——否则 set_target 重启后端后「后端」卡显示已连接、
//     链路行却还是旧报告,再点「后端」反而变成断开
//   * 烧录进行中(flashCall open)要二次确认(3s 窗口):重启后端会打断 AI 的
//     烧录,v1.3.5 同款教训
//
//  v1.3.7:空态也要能控后端(用户截图问「这个界面没有控制后端的开关嘛?」——
//  重启后没流量,链路行整行不渲染,「后端」断开/恢复开关跟着消失):
//   * 链路行**常驻**:不再要求 probe/chip/flash 至少有一项。一条链路读数都
//     没有时只留「后端」一张卡(探针/芯片/Flash 占位卡说的是「日志里缺这项」,
//     空态是「还没有日志」,摆三张未识别只是添乱)
//   * 修两个自家 bug:① 清空只清 entries 不清 rotNote——「已加载 .1 尾部
//     1470 条」在条目被清掉后继续说谎(用户就是这么看到自相矛盾的空态);
//     ② Host 清空不重置 rotLoaded——加载过→清空→再点「加载」被 already
//     loaded 挡住,而环里其实已经空了
//
//  v1.3.6:轮转的历史不再从面板上消失(用户截图:重启后面板全空,footer 却写着
//  「轮转文件已生成」——2MB 历史一行都看不到):
//   * Host:活动文件收缩且 .1 刚换过 → 判定为 ocdup 轮转(不是清空),先把 .1
//     尾部窗口接进内存环再继续跟新文件——正在看的面板不白屏;清空路由自己
//     reset,.1 没动,不会误判。stats 新增 rotSize/rotMtimeMs
//   * 新增 POST /api/ocdup-log/rotated:按需把 .1 尾部补进环(空态「加载」
//     按钮),同一份 .1 重复加载返回 note=already loaded
//   * 空态:有 .1 时显示「更早的 x 已轮转(时间)」+ 加载按钮,加载结果以一次
//     性备注反馈;footer 轮转列改显 .1 大小与时间(原来只说「已生成」)
//
//  v1.3.5:后端段反馈压缩 + 链路行 2×2(用户截图:一整句英文结论铺在卡片外,
//  把链路行撑爆;窄停留下「后端」还独自占一行):
//   * 卡片只显示一个词(backendVerdict:已断开/未运行/未释放/已连接/失败),
//     完整文案进 tooltip + 流水镜像(断开动作本来就会留下 cmd/rsp/SYS 行)
//   * 状态机补「was not running」→ stopped(旧逻辑跳过该行、回退读到更早的
//     started openocd,把「没跑」显示成「已连接」);没有 backend 行时显示「未知」
//   * 「后端」段调到段首(这一行唯一能动手的开关,前置=最易点),段名带
//     .op-seg-label:≤560px 隐藏;补中性左色条(不抢三条数据通道色)
//   * 链路行改 .op-linkrow class 承载布局(内联 style 会压过容器查询):
//     ≤620px 换 2×2 网格、刻度标签横跨两列——flex 贪心打包会排出「4+1」
//     这种一行挤满、一行只剩一张卡的不均衡两行
//
//  v1.3.4:右侧停靠回归 + 「后端」断开/恢复开关:
//   * 入口改版:左侧 panellist 图标整体移除(用户:没有使用体验),改注册右栏
//     tab(ctx.sidebarRightTabs.register + sidebar.right.pane.tab body,key=同一个
//     id,照 ui-sidebar-terminal 模板;guide 卡不需要 commandId)。宿主没有右栏
//     服务时退回 main 全局面板(v1.3.3 形态),两条路都通
//   * 链路行新增第四段「后端」:状态从 backend SYS 行派生(backendStateOf),
//     点击断开/恢复 —— 断开把探针让给别的 agent 软件(MCP 门面不断),动作经
//     Host 路由 POST /api/ocdup-backend → ctx.tools.execute → mcp__ocdup__disconnect
//     / mcp__ocdup__connect(ocdup v0.4.6 新增 disconnect 工具)。烧录进行中
//     断开要二次确认(3s 窗口,危险色);动作结果以一次性反馈显示,并镜像进流水
//   * 芯片卡的 grow 改 class(.op-link-grow),≤560px 收回自然宽——窄停留下
//     四张卡不再被撑成 3 行
//   * 随左侧图标一起移除:PanelIcon 组件与 badgeStore(未读徽章)死代码
//
//  v1.3.3:修「侧边栏图标点了没反应」(宿主 0.2.0-rc.2 注册契约):
//   根因:新版 ui-sidebar 的 PanelRow 自己绑 onClick → ctx.layout.selectPanel(id),
//   而 selectPanel 只认「main keyed slot 里同一个 id」的注册项,缺 key 直接抛错
//   并保持原面板;同时侧栏图标已不归右栏 tab 管(右栏是 chat/browser/document 那类
//   session 级内容)。旧版把 body 注册在 sidebar.right.pane.tab(key=插件 id)、点击
//   却调 selectPanel(另一个 id),两个 rightbar 服务又都在 → 永远不进兜底分支,
//   main 槽一个都没注册 → selectPanel 抛错 → 静默无反应。
//   修法:无条件按「全局面板」注册——panellist id 与 main key 严格同值,
//   inject 只声明 slots;PanelIcon 的 onClick 退为老宿主兜底(传 panelId)。
//   顺带:openTab('ocdup-log') 那个参数本来就是错的(body key 是插件 id),已删除。
//
//  v1.3.2:界面精修(按钮/文字/配色,frontend-design 定向,仍只动 Client 皮相):
//   * 修崩坏:所有控件 nowrap + flex:0 0 auto——旧版窄面板把「暂停/跟随」挤成
//     两行、上排图例被裁掉半截;工具栏两行都允许整组换行
//   * 配色收敛:engaged 态统一「墨色实底」(label-primary 底 + bg-layer 字,
//     深浅主题自动反转),替掉品牌色浅底——DSH 主题里 bg-layer-4 缺席,旧版
//     会露出 #eef1fe 蓝底配墨色描边的撞色;颜色只留给四条数据通道
//     (cmd 琥珀/rsp 绿/sys 蓝/err 红/log 石板灰),选中行底色也改品牌色 mix
//   * 方向过滤从原生下拉变「通道 chip」:cmd 69 / rsp 78 … 带色方点 + 计数,
//     点中即过滤、再点取消——与工具 chip 同一套交互语言,顺带去掉与占比表
//     重复的图例、把 'SYS' 统一成 'sys'
//   * 工具 chip 条单行横滚(右缘渐隐 + 滚轮转横向):旧版 11 枚 chip 换行成
//     3~4 行,窄面板吃掉半个屏幕;清空/导出/计数挪到这条带右侧,第二行只留
//     「筛什么」(方向 chip + 搜索)
//   * 搜索框内嵌正则开关(.*)取代行里孤零零的按钮;目标下拉只有一个目标且
//     未过滤时不占位;计数文案改单调式「320 条 · 渲染 300」
//   * 链路读数条层级翻转:值(=型号/容量/探针名)做主读数,段名退成弱标签,
//     段色只留左色条 + 图标;芯片补充读数(内核/dev)在窄面板整段隐藏,不再
//     半词截断;型号未识别时芯片图标转警示色
//   * 传输控件改 SVG(暂停/继续/跟随到底),不再依赖 ⏸/▶ 的字形;详情区段名
//     与通道色对齐(log 用石板灰而非琥珀),标题行去 '·' 串、去重复日期
//
//  v1.3.1:精修 + Flash 总容量:
//   * Flash 段报 bank 容量**合计**(parseFlashInfo.total)——双 bank 芯片
//     (at32f403axG:512K+512K=1024K)一片 flash 多大一眼可见;各 bank 明细进 tooltip
//   * 工具栏下排分组(方向/目标/正则 | 搜索 | 清空/导出 | 计数),组间发丝线,
//     窄屏整组换行不再冲散单个控件
//   * stats 计数改「图例」:每项方向色小方点 + 标签 + 数(与流水行方向色同一套
//     语言),替掉 '·' 串 meta
//   * 调用视图补列头条(时间/状态/调用/Δt/摘要),两个视图都有通道表
//   * 链路卡片左侧加 2px 段色条(仪器通道条的语言),不再只靠图标着色
//
//  v1.3.0:配套 ocdup v0.4.0(target 注册表 + set_target):
//   * 链路行新增「⚡ 切换 <家族>」胶囊:identify/connect 的 rsp 里出现
//     'target mismatch: identified X, current target_cfg: Y -> use set_target X'
//     时显示——点了复制 mcp__ocdup__set_target X 给会话(面板不能调 MCP,
//     这是它和硬件之间唯一的交互形式:复制提问,不直连)
//   * ocdup 侧新增 target_presets/chip_id_map 配置 + set_target 工具(杀自拉
//     后端→运行时切 target→重启),MCP 才是唯一碰硬件的层;面板依旧纯镜像
//
//  v1.2.5:芯片段「只信实测」+ 链路三段上色:
//   * target_cfg 文件名推定的 model 不再上芯片卡——用户把 target_cfg 钉错时
//     那就是误导(实测插的根本不是那片子);卡片只报实测:family(chip_id_map
//     命中 DEV_ID)→ 型号;否则 CPUID 内核 + 有效 dev_id + 琥珀「型号未识别」。
//     model 退到 tooltip,明确标注「仅配置线索,非实测」
//   * 链路三段各一个语义色(和流水方向色同一套仪器语言):探针=sys 蓝
//     #2563eb / 芯片=rsp 绿 #059669 / Flash=cmd 琥珀 #d97706;图标+标签着
//     色,值留主文本色;hover 改中性加深,不让品牌色和段色打架
//
//  v1.2.4:界面改版「台架仪器」(frontend-design 定向,仍只动 Client 皮相):
//   * 工具栏两行化:上排=连接与视图(状态胶囊/流水|调用/暂停/跟随),
//     下排=过滤与操作(方向/目标/正则/搜索/清空/导出+计数),行间发丝线——
//     单行 wrap 会把控件冲散,固定两行分组稳定可扫读
//   * 链路行=「规格读数条」:三枚方角(4px)发丝线读数卡,仪器图标
//     (信号弧/芯片/叠层);芯片卡 flex:1,T20 起卡片只报实测(family 命中
//     chip_id_map → 型号;否则内核 + dev_id + 「型号未识别」)
//   * 列表加列头条(时间/方向/tool/Δt/文本,sticky),复用容器查询的
//     列隐藏类,窄屏同步降级——像逻辑分析仪的通道表
//   * 控件统一 28px 高 + 6px 圆角,数据(时间/len/Δt/计数)全 tabular
//   * 明确避开模板痕迹:奶油底/陶土色、编号 01/02、全大写 eyebrow、
//     meta 串「·」、按钮「→」一律不用;阴影只留给新数据浮钮
//
//  v1.2.3:界面美化 + 链路三段「说人话」:
//   * 工具栏:统一 28px 控件高度/圆角/hover 浮起/focus 环;开关态改品牌色
//     浅底(不再整块填黑);「确认清空?」变危险红;统计做成状态胶囊;搜索框
//     内嵌放大镜图标
//   * 链路行改「小卡片」(图标+标签+值,hover 描品牌边),缺段占位同款卡片
//   * 探针段不再显示 'cmsis-dap.cfg' 这种文件名:按接口脚本映射成硬件名
//     (cmsis-dap→CMSIS-DAP、jlink→J-Link、stlink→ST-Link…),脚本名进 tooltip
//   * 芯片段优先显示型号:identify_chip 新行 'model (from target_cfg …): XXX'
//     (后端从钉死的 target_cfg 文件名推定);都认不出 → 'Cortex-Mx · 型号未识别'
//   * dev 0x000 这类无效读(读回了地址本身)不再显示;Flash 段只写容量
//     ('1024 KB'),bank/基址/驱动名收进 tooltip
//
//  v1.2.2:配套 ocdup connect 的自动芯片识别(v0.3.16):
//   * 芯片来源放宽:identify_chip 的 rsp,或 connect 自动识别镜像的
//     'chip identity:' 块(tool 可能记成 connect)——按文本形态认
//   * 探针兼容钉死 interface_cfg:'(interface: <cfg>)' → 固定配置模式,
//     显示接口脚本名(物理型号未自动识别是 auto 专属)
//   * 链路行缺段时给「未识别 · 复制提问」占位(面板不能给 AI 发消息)
//
//  v1.2.1:链路状态区——从历史流量解析 探针(backend SYS 的 probe
//  auto-detected)/ 芯片(identify_chip 的 rsp:CPUID 解码 + dev_id +
//  family)/ Flash 布局(flash_info 的 rsp:bank 基址 + 大小),常驻工具栏,
//  点各段跳到来源。仍是纯派生,不碰硬件、不改数据契约。
//
//  v1.2.0:功能增强 + 界面改版(机制沿用 v1.1.0:自适应轮询/跟随挂起/
//  徽章/正则/导出/快捷键/keepMounted 不变):
//   * 调用配对:cmd↔rsp/err 按 tool 流式配对(控制通道串行,无歧义)——
//     「流水/调用」双视图;调用期间与随后的 openocd log 行归入该调用
//   * 烧录进度:解析 log 行(erased sector / wrote N bytes / verified)
//     → 工具栏进度条(擦除/写入/校验分段,未完成段流光动画)
//   * 会话分组:sys(backend)生命周期事件作分隔条,可折叠
//   * 工具 chips 快捷过滤(按频次)+ err 一键过滤;↑/↓ 移动选中、Enter 复制
//   * 关键行高亮(Verified OK / verified N bytes / err);行视觉/工具栏/
//     空态/详情区改版(方向色条、cmd/rsp 底纹、stats 占比条、卡片化)
//
//  v1.1.0:新增 log 方向(石板灰,openocd 异步日志镜像);方向过滤下拉与
//  stats 行同步。机制与 v1.0.0 相同。
//
//  - sidebar.panellist 注册全局面板图标(id=ocdup-log)
//  - sidebarRightTabs/main 以插件 id 注册面板本体(dsh-serial-panel 同款双分支)
//  - 数据:同源 fetch /api/ocdup-log?since=<seq> 增量拉取(Host 见 index.js)
//
//  与 dsh-serial-panel client 的差异:
//   * direction 四色:cmd(琥珀=写命令)/rsp(绿=响应)/sys(蓝=后端事件)/err(红)
//   * 无 HEX 切换(ocdup 流量是文本命令/响应,没有原始字节帧)
//   * tool 列前置加宽(读内存/烧录的命令文本以 tool 为轴过滤最有用)
//   * 图标为芯片纹样;空态引导 mcp__ocdup__connect
//   * 其余机制(自适应轮询/跟随挂起/徽章/正则/导出/快捷键/keepMounted)
//     原样保留自 v1.6.0 的 dsh-serial-panel
//
//  闭包环境:React 走 require,禁 JSX/import;网络属于本包同源路由。
// ============================================================

window.__ModuleLoader__.load({
  id: 'dsh-ocdup-panel',
  factory: function (require) {
    const React = require('react')
    const h = React.createElement

    const ROUTE = '/api/ocdup-log'
    const ROUTE_CLEAR = '/api/ocdup-log/clear'
    const ROUTE_ROTATED = '/api/ocdup-log/rotated'
    const ROUTE_BACKEND = '/api/ocdup-backend'
    const ROUTE_TARGET = '/api/ocdup-target' // v1.3.8:失配胶囊一键切 target_cfg
    const ROUTE_MCP = '/api/ocdup-log/mcp'   // v1.4.0:MCP 启停开关
    const LS = 'dsh-ocdup-panel/prefs/v1'

    const POLL_ACTIVE_MS = 700   // 刚拿到数据:贴近写入端节奏
    const POLL_IDLE_MS = 2500    // 空闲退避
    const POLL_HIDDEN_MS = 2000  // 面板不可见
    const POLL_PAUSED_MS = 1500  // 用户暂停
    // v1.3.9:单次轮询 fetch 的超时。没有它,一个挂起的请求会把整条轮询链永久
    // 楔住(inFlightRef 永远 true,之后每一拍都被 `if (inFlightRef.current)`
    // 挡在 fetch 之外,连 visibilitychange 的立即刷新也救不回来)——用户实测
    // 视图冻死 3 分钟。abort 后照旧落进 catch/finally,下一拍自动恢复。
    const POLL_FETCH_TIMEOUT_MS = 10000
    const MAX_ROWS_KEPT = 5000   // 与 Host MAX_ENTRIES 对齐
    const MAX_RENDER_ROWS = 300  // 单次实际挂载的行数
    const ROW_PAYLOAD_MAX = 4000 // 行内 payload 显示上限(详情区不受限)

    // ---- 偏好(localStorage,带版本号) ----
    const loadPrefs = () => {
      const d = { dir: 'ALL', follow: true }
      try { return Object.assign(d, JSON.parse(localStorage.getItem(LS) || '{}')) } catch (e) { return d }
    }
    const savePrefs = (p) => { try { localStorage.setItem(LS, JSON.stringify(p)) } catch (e) {} }

    const shortTime = (ts) => (typeof ts === 'string' && ts.length >= 23 ? ts.slice(11, 23) : ts || '')
    const fmtBytes = (n) => (n >= 1024 ? (n / 1024).toFixed(1) + 'K' : String(n | 0))
    // v1.3.6:轮转文件(.1)的最后修改时间,本地时间短格式 M/D HH:mm
    const fmtStamp = (ms) => {
      if (ms == null || !Number.isFinite(ms)) return ''
      const d = new Date(ms)
      const p = (n) => (n < 10 ? '0' + n : '' + n)
      return (d.getMonth() + 1) + '/' + d.getDate() + ' ' + p(d.getHours()) + ':' + p(d.getMinutes())
    }

    const DIR_COLOR = { cmd: '#d97706', rsp: '#059669', sys: '#2563eb', err: '#ef4444', log: '#64748b' }
    // v1.3.2:统一小写(sys 不再大写)——大写混排是排版噪声,通道身份由颜色承担
    const DIR_LABEL = { cmd: 'cmd', rsp: 'rsp', sys: 'sys', err: 'err', log: 'log' }
    const DIR_ORDER = ['cmd', 'rsp', 'sys', 'log', 'err']

    // v1.3.2:方向图例 = 方向过滤器(工具栏第二行)。有量的方向才成 chip——
    // 空方向没有可过滤的东西,列出来只会稀释注意力;但当前生效的方向即使归零
    // 也要留着(否则用户没法取消它)
    const dirLegend = (stats, active) => {
      const out = DIR_ORDER
        .map((k) => ({ key: k, label: DIR_LABEL[k] || k, color: DIR_COLOR[k], n: (stats && stats[k]) || 0 }))
        .filter((d) => d.n > 0)
      if (active && active !== 'ALL' && !out.some((d) => d.key === active)) {
        out.push({ key: active, label: DIR_LABEL[active] || active, color: DIR_COLOR[active], n: (stats && stats[active]) || 0 })
        out.sort((a, b) => DIR_ORDER.indexOf(a.key) - DIR_ORDER.indexOf(b.key))
      }
      return out
    }

    // v1.3.2:计数文案单调化——旧版「渲染 300 / 320 / 320 条」两个斜杠读不出层级
    const counterLabel = (rendered, visible, total) => {
      const base = visible < total ? visible + ' / ' + total + ' 条' : total + ' 条'
      return rendered < visible ? base + ' · 渲染 ' + rendered : base
    }

    // v1.3.4 后端(openocd)状态:从 backend SYS 行派生——ocdup 的 AcquireAsync 写
    // 「started openocd …」/「using the listener already present …」,RestartAsync 写
    // 「backend restarted」,StopAsync(disconnect)写「backend stopped …」或
    // 「backend was not running …」(本来就没跑,同样是断开态)。
    // 面板不碰硬件,这里只是把「AI 已经做过的事」镜像成一眼状态。
    // v1.4.0：失败类 SYS 行也归一成断开态——启动失败(did not become ready)、
    // 端口无监听(nothing is listening)、TCL-RPC 通道打不开、心跳丢失(heartbeat
    // lost)都是「后端没了」。此前只认成功形态:拔线/换错 cfg 后再调任意硬件工具,
    // 循环会跳过失败行扫到更早的「started openocd」→ 卡片继续显示陈旧的「已连接」。
    // 「still running … cannot stop it」(外部实例)是唯一反例:监听确实还活着 → running。
    const backendStateOf = (entries) => {
      for (let i = entries.length - 1; i >= 0; i--) {
        const e = entries[i]
        if (!e || e.kind === 'gap' || e.tool !== 'backend') continue
        const t = e.text || ''
        if (/backend stopped|was not running/i.test(t)) return 'stopped'
        if (/^ERROR:|did not become ready|nothing is listening|cannot open the .*TCL-RPC|heartbeat lost/i.test(t)) return 'stopped'
        if (/started openocd|already present|restarted|still running/i.test(t)) return 'running'
      }
      return null
    }

    // v1.3.5 断开/恢复的结论压缩成一个词:卡片只放得下一个词,完整文案进 tooltip
    // (流水里同样有镜像)。纯函数,可单测。
    const backendVerdict = (action, ok, text) => {
      const t = String(text || '')
      if (!ok) return { label: '失败', tone: 'err' }
      if (action === 'connect') return { label: '已连接', tone: 'ok' }
      if (/was not running/i.test(t)) return { label: '未运行', tone: 'warn' }
      if (/still running/i.test(t)) return { label: '未释放', tone: 'warn' }
      return { label: '已断开', tone: 'ok' }
    }
    // 链路三段各一个语义色(v1.2.5):探针=连接/后端(sys 蓝)、芯片=寄存器读回
    // (rsp 绿)、Flash=擦写(cmd 琥珀)——与流水行方向色同一套仪器语言
    const LINK_TONE = { probe: '#2563eb', chip: '#059669', flash: '#d97706' }
    // 语义色调(走 DSH token,深浅主题自适应)
    const TONE_OK = 'var(--dsw-alias-state-success-primary, #10b981)'
    const TONE_WARN = 'var(--dsw-alias-state-warn-primary, #f59e0b)'
    const TONE_ERR = 'var(--dsw-alias-state-error-primary, #ef4444)'
    const MARK_STYLE = { background: 'rgba(245, 158, 11, 0.35)', color: 'inherit', borderRadius: 2, padding: '0 1px' }

    // 搜索匹配器:正则开关开启且表达式合法 → RegExp;否则小写子串(空查询 → null)
    const compileMatcher = (query, useRegex) => {
      const q = String(query || '').trim()
      if (!q) return null
      if (useRegex) {
        try { return new RegExp(q, 'i') } catch (e) { return q.toLowerCase() } // 非法正则回退子串
      }
      return q.toLowerCase()
    }

    // v1.4.0：text 的匹配主题与行显示同源(截到 ROW_PAYLOAD_MAX)——命中落在截断
    // 尾部时不再「行保留但高亮丢」;顺带把用户正则的回溯面从整条 4000 字符
    // (调用视图还拼 cmd+rsp+logs)收窄到同一上限
    const matchSubject = (e) => {
      const t = typeof e.text === 'string' ? e.text : ''
      return t.length > ROW_PAYLOAD_MAX ? t.slice(0, ROW_PAYLOAD_MAX) : t
    }

    const matcherHit = (matcher, e) => {
      const subject = matchSubject(e)
      // 末位 Boolean():e.tag 为空时 `e.tag && …` 会短路成 ''——调用方写
      // `=== false` 就炸(此前只有真值断言没暴露)。契约收紧为恒布尔。
      return matcher instanceof RegExp
        ? matcher.test(subject) || matcher.test(e.tool || '') || matcher.test(e.tag || '')
        : Boolean((subject.toLowerCase().indexOf(matcher) >= 0)
          || (e.tool && e.tool.toLowerCase().indexOf(matcher) >= 0)
          || (e.tag && e.tag.toLowerCase().indexOf(matcher) >= 0))
    }

    // ---- 一次性样式:底纹/hover/选中/关键行/进度流光/窄宽度降级 ----
    // (inline style 写不了 :hover、animation 与容器查询,统一在这里注入)
    const injectStyles = () => {
      try {
        if (document.getElementById('dsh-ocdup-panel-styles')) return
        const el = document.createElement('style')
        el.id = 'dsh-ocdup-panel-styles'
        el.textContent = [
          // 行:rsp 浅底纹与 cmd 区分(请求-应答节奏),hover/选中渐进
          '.op-row { background: transparent; transition: background-color .12s ease, filter .12s ease; }',
          '.op-row.op-dir-rsp { background: var(--dsw-alias-bg-layer-3, #fafafa); }',
          '.op-row:hover { background: var(--dsw-alias-bg-layer-3, #f0f0f0); }',
          // 关键行高亮( Verified OK / err ):标记优先于 hover 底纹
          '.op-row.op-mark-ok { background: rgba(16, 185, 129, 0.13); }',
          '.op-row.op-mark-err { background: rgba(239, 68, 68, 0.13); }',
          '.op-row.op-mark-ok:hover, .op-row.op-mark-err:hover { filter: brightness(1.05); }',
          // 选中态压在标记之上(同时命中时选中可见);品牌色 mix 随主题走,不再硬编码蓝
          '.op-row.op-sel { background: color-mix(in srgb, var(--dsw-alias-brand-primary, #4f6bed) 12%, transparent); box-shadow: inset 2px 0 0 var(--dsw-alias-brand-primary, #4f6bed); }',
          // 会话分隔条 hover 反馈
          '.op-session { transition: background-color .12s ease; }',
          '.op-session:hover { background: var(--dsw-alias-bg-layer-3, #efefef); }',
          // 工具 chips / 视图切换按钮
          '.op-chip { transition: background-color .12s ease, color .12s ease, border-color .12s ease; }',
          '.op-chip:hover { border-color: var(--dsw-alias-border-l3, #c9c9c9); }',
          // v1.3.2 控件皮相(统一语言):
          //   * 所有控件 nowrap —— 旧版窄面板把「暂停/跟随」挤成两行,是这张面板
          //     最刺眼的一处崩坏
          //   * engaged 态一律「墨色实底」:浅色主题=黑底白字,深色主题自动反转。
          //     旧版用品牌色浅底,而 DSH 主题里 bg-layer-4 缺席 → 露出 #eef1fe
          //     蓝底配墨色描边,和宿主近黑品牌色撞车。颜色只留给数据通道
          '.op-btn { border: 1px solid var(--dsw-alias-border-l2, #d9d9d9); background: var(--dsw-alias-bg-layer-3, #fff); color: var(--dsw-alias-label-secondary, #555); white-space: nowrap; transition: background-color .12s ease, border-color .12s ease, color .12s ease, box-shadow .12s ease; }',
          '.op-btn:hover { border-color: var(--dsw-alias-border-l3, #bdbdbd); color: var(--dsw-alias-label-primary, #222); }',
          '.op-btn:active { box-shadow: inset 0 1px 3px rgba(0,0,0,.10); }',
          '.op-btn:focus-visible { outline: 2px solid var(--dsw-alias-brand-primary, #4f6bed); outline-offset: 1px; }',
          '.op-btn[data-on="1"] { border-color: var(--dsw-alias-label-primary, #222); background: var(--dsw-alias-label-primary, #222); color: var(--dsw-alias-bg-layer-2, #fff); font-weight: 600; }',
          '.op-btn[data-tone="warn"] { border-color: var(--dsw-alias-state-warn-primary, #f59e0b); background: rgba(245,158,11,.10); color: var(--dsw-alias-state-warn-primary, #b45309); }',
          '.op-btn[data-tone="danger"] { border-color: var(--dsw-alias-state-error-primary, #ef4444); background: rgba(239,68,68,.10); color: var(--dsw-alias-state-error-primary, #dc2626); font-weight: 600; }',
          // MCP 开关(v1.5.1):开态实底绿白字 / 关态中性灰平底 + ●/○ 形状 cue。
          // 淡染在 26px 按钮上区分度不够,扫一眼必须认出状态。tone 规则在 data-on
          // 之后书写,同特异性按源序覆盖,ok/off 再各加一个属性选择器确保压过 data-on
          '.op-btn[data-tone="ok"][data-on="1"] { border-color: var(--dsw-alias-state-success-primary, #22c55e); background: var(--dsw-alias-state-success-primary, #16a34a); color: #fff; font-weight: 700; }',
          '.op-btn[data-tone="off"] { border-color: var(--dsw-alias-border-l2, #d9d9d9); background: color-mix(in srgb, var(--dsw-alias-label-primary, #222) 10%, transparent); color: var(--dsw-alias-label-tertiary, #999); font-weight: 600; }',
          // 分段控件(流水/调用):选中段墨色填充,压在被选相邻段之上
          '.op-seg { position: relative; white-space: nowrap; border: 1px solid var(--dsw-alias-border-l2, #d9d9d9); background: var(--dsw-alias-bg-layer-3, #fff); color: var(--dsw-alias-label-secondary, #555); transition: background-color .12s ease, border-color .12s ease, color .12s ease; }',
          '.op-seg:first-child { border-radius: 6px 0 0 6px; }',
          '.op-seg:last-child { border-radius: 0 6px 6px 0; }',
          '.op-seg:hover { border-color: var(--dsw-alias-border-l3, #bdbdbd); color: var(--dsw-alias-label-primary, #222); }',
          '.op-seg:focus-visible { outline: 2px solid var(--dsw-alias-brand-primary, #4f6bed); outline-offset: 1px; z-index: 2; }',
          '.op-seg[data-on="1"] { z-index: 1; background: var(--dsw-alias-label-primary, #222); border-color: var(--dsw-alias-label-primary, #222); color: var(--dsw-alias-bg-layer-2, #fff); font-weight: 600; }',
          // 下拉:与按钮同一副皮相(容器自带 chevron,不用 UA 箭头)
          '.op-select { appearance: none; cursor: pointer; border: 1px solid var(--dsw-alias-border-l2, #d9d9d9); background: var(--dsw-alias-bg-layer-3, #fff); color: var(--dsw-alias-label-secondary, #555); white-space: nowrap; transition: border-color .12s ease, color .12s ease; }',
          '.op-select:hover { border-color: var(--dsw-alias-border-l3, #bdbdbd); color: var(--dsw-alias-label-primary, #222); }',
          '.op-select:focus-visible { outline: 2px solid var(--dsw-alias-brand-primary, #4f6bed); outline-offset: 1px; }',
          // 搜索框 + 内嵌正则开关
          '.op-search { border: 1px solid var(--dsw-alias-border-l2, #d9d9d9); background: var(--dsw-alias-bg-layer-3, #fff); color: var(--dsw-alias-label-primary, #222); transition: border-color .12s ease, box-shadow .12s ease; }',
          '.op-search::placeholder { color: var(--dsw-alias-label-tertiary, #999); }',
          '.op-search:hover { border-color: var(--dsw-alias-border-l3, #bdbdbd); }',
          '.op-search:focus { border-color: var(--dsw-alias-brand-primary, #4f6bed); box-shadow: 0 0 0 2px color-mix(in srgb, var(--dsw-alias-brand-primary, #4f6bed) 16%, transparent); outline: none; }',
          '.op-affix { border: 1px solid transparent; background: transparent; color: var(--dsw-alias-label-tertiary, #767676); transition: background-color .12s ease, color .12s ease; }',
          '.op-affix[data-on="0"]:hover { background: var(--dsw-alias-bg-layer-4, #f2f3f5); color: var(--dsw-alias-label-primary, #222); }',
          '.op-affix[data-on="1"] { background: var(--dsw-alias-label-primary, #222); color: var(--dsw-alias-bg-layer-2, #fff); }',
          '.op-affix:focus-visible { outline: 2px solid var(--dsw-alias-brand-primary, #4f6bed); outline-offset: 1px; }',
          // 过滤 chip:方向/工具/只看失败 共用一种语言;engaged 墨色实底
          '.op-chip { white-space: nowrap; transition: background-color .12s ease, color .12s ease, border-color .12s ease; }',
          '.op-chip[data-on="0"]:hover { border-color: var(--dsw-alias-border-l3, #c9c9c9); color: var(--dsw-alias-label-primary, #222); }',
          // 工具条单行横滚(旧版 11 枚 chip 撑成 3~4 行,吃掉半个屏幕)
          '.op-strip { scrollbar-width: none; -ms-overflow-style: none; mask-image: linear-gradient(to right, #000 calc(100% - 16px), transparent); -webkit-mask-image: linear-gradient(to right, #000 calc(100% - 16px), transparent); }',
          '.op-strip::-webkit-scrollbar { display: none; }',
          // 统计胶囊(信号灯 + 占比表)
          '.op-stat { transition: border-color .12s ease; }',
          '.op-stat:hover { border-color: var(--dsw-alias-border-l3, #c9c9c9); }',
          // 链路读数卡:段色只在左色条与图标,hover 走中性加深
          '.op-link { border: 1px solid var(--dsw-alias-border-l2, #e6e6e6); background: var(--dsw-alias-bg-layer-3, #fff); color: var(--dsw-alias-label-secondary, #666); transition: background-color .12s ease, border-color .12s ease; }',
          // 芯片卡在宽面板吃掉余量(身份是主读数);窄面板收回自然宽,否则它一「长胖」
          // 就把 Flash/后端 挤到下一行,链路行从 2 行变 3 行
          '.op-link-grow { flex: 1 1 auto; min-width: 0; }',
          '.op-link:hover { border-color: var(--dsw-alias-border-l3, #bdbdbd); background: var(--dsw-alias-bg-layer-4, #f5f5f5); }',
          '.op-link:focus-visible { outline: 2px solid var(--dsw-alias-brand-primary, #4f6bed); outline-offset: 1px; }',
          // target 失配胶囊(v1.3.0):警示色实底感,链路行里唯一「要你操作」的东西
          '.op-switch { border: 1px solid var(--dsw-alias-state-warn-primary, #f59e0b); background: rgba(245,158,11,.10); color: var(--dsw-alias-state-warn-primary, #b45309); }',
          '.op-switch:hover { border-color: var(--dsw-alias-state-warn-primary, #d97706); background: rgba(245,158,11,.18); color: var(--dsw-alias-state-warn-primary, #92400e); }',
          '.op-link-miss { border-style: dashed; }',
          '.op-link-miss:hover { border-color: var(--dsw-alias-state-warn-primary, #f59e0b); background: rgba(245,158,11,.08); }',
          // 烧录进度条:完成度未知(擦除/写入中)用流光动画
          '@keyframes op-flow { from { background-position: 0 0; } to { background-position: 24px 0; } }',
          '.op-flow { background-image: linear-gradient(90deg, rgba(255,255,255,.30) 25%, transparent 25%, transparent 50%, rgba(255,255,255,.30) 50%, rgba(255,255,255,.30) 75%, transparent 75%); background-size: 24px 24px; animation: op-flow .8s linear infinite; }',
          // 容器查询跟随面板实际宽度(停靠栏拖拽时列降级实时生效)
          '@container op-panel (max-width: 640px) { .op-tag { display: none; } }',
          '@container op-panel (max-width: 620px) { .op-chip-core { display: none; } }',
          '@container op-panel (max-width: 560px) { .op-link-grow { flex: 0 1 auto; } .op-seg-label { display: none; } }',
          '@container op-panel (max-width: 560px) { .op-len { display: none; } }',
          '@container op-panel (max-width: 520px) { .op-dt { display: none; } }',
          '@container op-panel (max-width: 460px) { .op-tool { display: none; } }',
          // 窄停靠:链路行换 2×2 网格(刻度标签横跨两列)——flex 贪心打包会排出
          // 「4+1」这种一行挤满、一行只剩一张卡的不均衡两行;
          // v1.3.7:整行只有一张卡时(空态,只剩「后端」开关)让它吃满整行——
          // 半张卡配右半空白看着像渲染坏了,而它那时是这一行唯一的控件
          '.op-linkrow { display: flex; align-items: center; gap: 6px; flex-wrap: wrap; }',
          '@container op-panel (max-width: 620px) { .op-linkrow { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 6px 8px; align-items: center; } .op-linkrow > .op-tick { grid-column: 1 / -1; } .op-linkrow > .op-link:only-of-type { grid-column: 1 / -1; } }',
          '.op-colhead { position: sticky; top: 0; z-index: 2; display: flex; align-items: baseline; gap: 8px; padding: 5px 10px 4px; border-bottom: 1px solid var(--dsw-alias-border-l2, #e8e8e8); background: var(--dsw-alias-bg-layer-2, #fff); font-size: 10px; line-height: 1.4; color: var(--dsw-alias-label-tertiary, #999); font-variant-numeric: tabular-nums; user-select: none; }',
          '@media (max-width: 640px) { .op-tag { display: none; } }',
          '@media (max-width: 560px) { .op-len { display: none; } }',
        ].join('\n')
        document.head.appendChild(el)
      } catch (e) { /* 注入失败仅降级 hover 与窄宽度适配 */ }
    }
    injectStyles()

    const fmtDt = (ms) => (ms == null ? '—' : ms < 1000 ? ms + 'ms' : (ms / 1000).toFixed(2) + 's')

    // 搜索命中高亮:大小写不敏感,返回字符串/mark 混合的 children 数组
    const highlight = (text, q) => {
      if (!q) return [text]
      const s = String(text)
      const lower = s.toLowerCase()
      const needle = q.toLowerCase()
      if (!needle) return [s]
      const out = []
      let i = 0
      for (;;) {
        const hit = lower.indexOf(needle, i)
        if (hit < 0) { if (i < s.length) out.push(s.slice(i)); break }
        if (hit > i) out.push(s.slice(i, hit))
        out.push(h('mark', { key: hit, style: MARK_STYLE }, s.slice(hit, hit + needle.length)))
        i = hit + needle.length
      }
      return out.length ? out : [s]
    }

    // 正则版高亮:exec 循环需要 g 标志,内部克隆加 g;零长匹配 lastIndex 前进 1 位防空转
    const highlightRe = (text, re) => {
      const s = String(text)
      const g = re.flags.indexOf('g') >= 0 ? re : new RegExp(re.source, re.flags + 'g')
      g.lastIndex = 0
      const out = []
      let i = 0
      let m
      let guard = 0
      while ((m = g.exec(s)) !== null && guard++ < 1000) {
        if (m.index > i) out.push(s.slice(i, m.index))
        out.push(h('mark', { key: i + ':' + m.index, style: MARK_STYLE }, m[0]))
        i = m.index + (m[0].length || 1)
        if (!m[0].length) g.lastIndex = i
      }
      if (i < s.length) out.push(s.slice(i))
      return out.length ? out : [s]
    }

    // portTag(host:port)→ 稳定色相:多目标板流量交错时一眼区分
    const hueOf = (tag) => {
      let hsh = 0
      for (let i = 0; i < tag.length; i++) hsh = (hsh * 31 + tag.charCodeAt(i)) >>> 0
      return hsh % 360
    }

    // ---- 烧录进度:解析 openocd 日志行 ----
    // 实测形态(atopenocd 0.11 / openocd 0.11-0.12):
    //   'erased sector 3 3' / 'Info : erased sectors 0 3'(首扇区 尾扇区)
    //   'erased 25 sectors' / 'erased sectors 0..last on bank 0'(erase_sector 汇总行)
    //   'wrote 139077 bytes from file …' / 'verified 28288 bytes in 0.01s'
    //   '** Verified OK **'。识别不了返回 null,调用方静默跳过。
    const parseFlashProgress = (text) => {
      const s = String(text || '')
      let m = s.match(/erased\s+sectors?\s+(\d+)(?:\s+(\d+))?/i)
      if (m) {
        const first = Number(m[1])
        const last = m[2] != null ? Number(m[2]) : first
        return { type: 'erase', count: Math.max(1, last - first + 1), last }
      }
      m = s.match(/erased\s+(\d+)\s+sectors?/i)
      if (m) return { type: 'erase', count: Number(m[1]), last: -1 }
      m = s.match(/(\d+)\s+sectors?\s+erased/i)
      if (m) return { type: 'erase', count: Number(m[1]), last: -1 }
      if (/erased sectors 0\.\.last/i.test(s)) return { type: 'erase', count: 1, last: -1 }
      m = s.match(/wrote\s+(\d+)\s+bytes/i)
      if (m) return { type: 'write', bytes: Number(m[1]) }
      m = s.match(/verified\s+(\d+)\s+bytes/i)
      if (m) return { type: 'verify', bytes: Number(m[1]) }
      if (/\*\*\s*Verified OK\s*\*\*/i.test(s)) return { type: 'verify', bytes: 0 }
      return null
    }

    // ---- v1.5.3(A-10):OCDUP-RESULT 机器结论行 ----
    // ocdup v0.4.34 起 flash_write / verify_image / dump_memory 的响应末行追加
    //   OCDUP-RESULT: <op>=<verdict>
    // 人读文本一字不动,因此「wrote N bytes」这类进度行在 VERIFY FAILED 时同样存在——
    // 只按字节数判绿会把校验失败画成绿色 ✓(v1.4.0 起的实测缺陷)。标签存在时它是权威。
    const RESULT_TAG_RE = /OCDUP-RESULT:\s*([a-z][a-z-]*)\s*=\s*([A-Z][A-Z-]*)/g

    // 一条文本里出现的全部结论标签(同步扫描,先重置 lastIndex 防跨调用残留)
    const parseResultTags = (text) => {
      const s = String(text || '')
      const out = []
      RESULT_TAG_RE.lastIndex = 0
      let m
      while ((m = RESULT_TAG_RE.exec(s)) !== null) out.push({ op: m[1], verdict: m[2] })
      return out
    }

    // 同一次调用只认最后一个标签(响应末行),前面的历史行不参与判定
    const lastResultTag = (text) => {
      const tags = parseResultTags(text)
      return tags.length ? tags[tags.length - 1] : null
    }

    // 结论 → 视觉档位。只有 OK 是绿;INCONCLUSIVE/PARTIAL/UNVERIFIED 是「没验成/待验」
    // 而不是失败(与 ocdup 措辞一致,否则会怂恿用户无谓重烧),给琥珀;FAILED 家族给红。
    const verdictToneOf = (verdict) => {
      const v = String(verdict || '').toUpperCase()
      if (v === 'OK') return { tone: TONE_OK, mark: 'ok' }
      if (v === 'INCONCLUSIVE' || v === 'PARTIAL' || v === 'UNVERIFIED') return { tone: TONE_WARN, mark: 'warn' }
      return { tone: TONE_ERR, mark: 'err' }
    }

    // 一次烧录类调用的聚合进度(logs = 归属该调用的 log 文本):
    // { phase:'idle'|'erase'|'write'|'verify'|'done', sectors, bytes, verified, ok, tag }
    // 擦除段无总量(openocd 不预告总扇区数),UI 以流光条表示「进行中、完成度未知」。
    // v1.5.3:出现结论标签时 ok 只由标签决定——字节数再大也不算通过。
    const flashProgressOf = (logs) => {
      const out = { phase: 'idle', sectors: 0, bytes: 0, verified: 0, ok: false, tag: null }
      for (const t of logs || []) {
        const tag = lastResultTag(t)
        if (tag) out.tag = tag
        const p = parseFlashProgress(t)
        if (!p) continue
        if (p.type === 'erase') { out.sectors += p.count; if (out.phase === 'idle') out.phase = 'erase' }
        else if (p.type === 'write') { out.bytes = p.bytes; out.phase = 'write' }
        else if (p.type === 'verify') {
          if (p.bytes > 0) out.verified = p.bytes
          else out.ok = true
          out.phase = out.ok ? 'done' : 'verify'
        }
      }
      if (out.tag) out.ok = out.tag.verdict === 'OK'
      if (out.ok) out.phase = 'done'
      return out
    }

    // 调用级进度:logs + rsp 一起喂——openocd 的 '** Verified OK **' 有的版本走
    // stdout(进 log),有的被 capture 带回(进 rsp),两处都看才能稳定判「校验通过」。
    const flashProgressOfCall = (call) => flashProgressOf((call.logs || []).concat(call.rsp || ''))

    const FLASH_TOOLS = { flash_write: 1, flash_erase_sector: 1 }
    const isFlashTool = (t) => !!FLASH_TOOLS[t]

    // ---- 调用配对:cmd↔rsp/err 流式配对 ----
    // BackendSession 逐条 Record(op/cmd → rsp/err)且控制通道串行化,因此同 tool 的
    // cmd 之后必跟其 rsp/err;期间的 log 行(烧录擦写进度就在这条链上)归入该调用。
    // 无法配对的行(CMD 无应答、孤立 err)原样保留,绝不丢数据。
    const pairCalls = (entries) => {
      const out = []
      let open = null
      for (const e of entries) {
        if (e.kind === 'gap') { out.push(e); continue }
        if (e.dir === 'cmd') {
          if (open) out.push(open) // 上一条调用没等到应答(异常路径):先结算
          open = { kind: 'call', tool: e.tool, tag: e.tag, state: 'open', seq: e.seq, ts: e.ts, cmd: e.text, rsp: '', logs: [] }
          continue
        }
        if (e.dir === 'rsp' || e.dir === 'err') {
          if (open && e.tool === open.tool) {
            open.rsp = e.text
            open.state = e.dir === 'rsp' ? 'ok' : 'err'
            open.rspSeq = e.seq
            open.rspTs = e.ts
            out.push(open)
            open = null
          } else {
            out.push(e) // 孤立响应(err 先于 cmd 落盘等异常序列):原样成行
          }
          continue
        }
        if (open && e.dir === 'log') { open.logs.push(e.text); continue }
        out.push(e) // sys 事件 / 调用之外的 log:原样
      }
      if (open) out.push(open)
      return out
    }

    // 调用耗时 = 应答时间戳 - 命令时间戳(坏时间戳返回 null)
    const callDtMs = (call) => {
      if (!call || !call.rspTs) return null
      const a = Date.parse(call.ts)
      const b = Date.parse(call.rspTs)
      return Number.isFinite(a) && Number.isFinite(b) ? Math.max(0, b - a) : null
    }

    // 会话分隔:backend 生命周期(sys)事件——connect 拉起/复用/失败等
    const isSessionSep = (e) => !!(e && e.kind !== 'gap' && e.dir === 'sys' && e.tool === 'backend')

    // 每个会话分隔条之后(到下一个分隔条前)的条目数 → Map<sepSeq, count>
    const sessionRuns = (items) => {
      const m = new Map()
      let cur = null
      for (const it of items) {
        if (isSessionSep(it)) { cur = it.seq; m.set(cur, 0); continue }
        if (cur != null) m.set(cur, m.get(cur) + 1)
      }
      return m
    }

    // 折叠命中的会话:分隔条保留(点它展开),其余条目隐藏
    const applyCollapse = (items, collapsed) => {
      if (!collapsed || collapsed.size === 0) return items
      const out = []
      let hide = false
      for (const it of items) {
        if (isSessionSep(it)) { hide = collapsed.has(it.seq); out.push(it); continue }
        if (!hide) out.push(it)
      }
      return out
    }

    // 工具清单:按出现频次降序(同频按字典序),用于快捷过滤 chips
    const toolOptions = (entries) => {
      const counts = new Map()
      for (const e of entries) {
        if (e.kind === 'gap' || !e.tool) continue
        counts.set(e.tool, (counts.get(e.tool) || 0) + 1)
      }
      return Array.from(counts.entries())
        .sort((a, b) => (b[1] - a[1]) || a[0].localeCompare(b[0]))
        .map(([name, n]) => ({ name, n }))
    }

    // 关键行标记:烧录/校验成功(ok,绿)/ 存疑(warn,琥珀)/ 错误(err,红),驱动行视觉高亮
    // v1.5.3:带 OCDUP-RESULT 的行由标签定性——`programmed but verify FAILED` 这类
    // 响应里同样有 "verified/differ" 字样,纯正则会把失败行点亮成绿色。
    const rowMark = (e) => {
      if (!e || e.kind === 'gap') return null
      const t = e.text || ''
      if (e.dir === 'err') return 'err'
      if (e.dir === 'rsp') {
        const tag = lastResultTag(t)
        if (tag) return verdictToneOf(tag.verdict).mark
        if (/\*\*\s*Verified OK\s*\*\*/i.test(t) || /verified\s+\d+\s+bytes/i.test(t)) return 'ok'
      }
      if (e.dir === 'log' && /^(info\s*:\s*)?error\b/i.test(t.trim())) return 'err'
      return null
    }

    // 最近一次烧录类调用(工具栏进度条数据源):从后往前找 flash_write/flash_erase_sector
    const lastFlashCall = (items) => {
      for (let i = items.length - 1; i >= 0; i--) {
        const it = items[i]
        if (it && it.kind === 'call' && isFlashTool(it.tool)) return it
      }
      return null
    }

    // v1.4.0：烧录进度「仍是最新动态」判定(纯函数,可单测):调用中恒显示;
    // 已结束时——调用视图 basis 末项就是该调用对象本身(对象同一性),流水视图
    // basis 是原始行、末行是该次烧录的应答行(seq 与 rspSeq 相等)。两种视图
 // 一套语义,默认的流水视图从此也有进度条与「烧录中」二次确认。
    const flashStillLatest = (call, items) => {
      if (!call) return false
      if (call.state === 'open') return true
      const last = items[items.length - 1]
      if (!last) return true
      return last === call || (call.rspSeq != null && last.seq === call.rspSeq)
    }

    // ---- 链路状态解析(v1.2.1):探针 / 芯片 / Flash 布局 ----
    // 数据源全是现有流量,不碰硬件:connect 的 backend SYS(探针自动识别)、
    // identify_chip 的 rsp(CPUID/dev_id/family)、flash_info 的 rsp(bank 行)。
    const fmtSize = (bytes) => {
      if (bytes == null || !Number.isFinite(bytes)) return ''
      if (bytes >= 1024 * 1024) return (bytes % 1048576 === 0 ? (bytes / 1048576).toFixed(0) : (bytes / 1048576).toFixed(1)) + ' MB'
      if (bytes >= 1024) return Math.round(bytes / 1024) + ' KB'
      return bytes + ' B'
    }

    // 路径取文件名(interface/cmsis-dap.cfg → cmsis-dap.cfg)
    const baseName = (p) => {
      const s = String(p || '')
      const i = Math.max(s.lastIndexOf('/'), s.lastIndexOf('\\'))
      return i >= 0 ? s.slice(i + 1) : s
    }

    // OpenOCDLauncher 的后端说明文案,两种形态:
    //   auto 识别(interface_cfg="auto"):'…(probe auto-detected: CMSIS-DAP -> interface/cmsis-dap.cfg (evidence: 0D28:0204))'
    //   钉死脚本:'…(interface: interface/cmsis-dap.cfg)'——知道用的哪个接口脚本,物理型号未识别
    //   注意 cfg  token 后可能紧跟右括号(文案整体被括号包着),捕获时排除 ')'。
    const parseProbeNote = (text) => {
      const s = String(text || '')
      const m = s.match(/probe auto-detected:\s*(.+?)\s*->\s*([^\s)]+)(?:\s*\(evidence:\s*([^)]*)\))?/i)
      if (m) return { kind: m[1], cfg: m[2], evidence: (m[3] || '').trim(), pinned: false }
      const f = s.match(/interface:\s*([^\s)]+)/i)
      if (f) return { kind: '固定配置', cfg: f[1], evidence: '', pinned: true }
      return null
    }

    // identify_chip 的报告(逐行形态):
    //   chip identity:
    //     cpu (CPUID, ARM standard) @ 0xE000ED00: 0x410fc241
    //       -> Cortex-M4 r0p1
    //     dev_id (vendor register) @ 0xE0042000: 0x…
    //       -> dev_id 0x410, rev_id 0x0000
    //       -> family: AT32F403A(未配 chip_id_map 时是 unknown + 提示)
    //     model (from target_cfg target_py32f002a_lock.cfg): PY32F002A  (v1.2.3)
    const parseChipIdentity = (text) => {
      const lines = String(text || '').split('\n')
      const out = { core: '', devId: '', revId: '', family: '', model: '', modelFrom: '' }
      for (let i = 0; i < lines.length; i++) {
        const l = lines[i]
        if (/cpu \(CPUID/i.test(l)) {
          const g = (lines[i + 1] || '').match(/->\s*(.+)/)
          if (g) out.core = g[1].trim()
        }
        let m = l.match(/dev_id\s+(0x[0-9a-fA-F]+)/i)
        if (m) out.devId = m[1].toLowerCase()
        m = l.match(/rev_id\s+(0x[0-9a-fA-F]+)/i)
        if (m) out.revId = m[1].toLowerCase()
        m = l.match(/family:\s*(.+)/i)
        if (m) out.family = m[1].trim()
        // v1.2.3:后端从钉死的 target_cfg 文件名推定的型号(非硬件读)
        m = l.match(/model\s*\(from target_cfg\s+([^)]+)\):\s*(\S+)/i)
        if (m) { out.modelFrom = m[1].trim(); out.model = m[2].trim() }
      }
      return (out.core || out.devId || out.family || out.model) ? out : null
    }

    // 探针硬件名:接口脚本名(cmsis-dap.cfg)对用户没意义,按脚本映射成烧录器类型
    const PROBE_NAME_MAP = {
      'cmsis-dap': 'CMSIS-DAP', 'cmsis_dap': 'CMSIS-DAP',
      'jlink': 'J-Link', 'stlink': 'ST-Link', 'st-link': 'ST-Link',
      'atlink': 'AT-Link', 'daplink': 'DAPLink',
      'ulink': 'ULINK', 'xds110': 'XDS110', 'ftdi': 'FTDI',
      'buspirate': 'Bus Pirate', 'remote-bitbang': 'Remote Bitbang',
    }
    const PROBE_ACRONYMS = new Set(['dap', 'usb', 'hid', 'swd', 'jtag', 'ftdi', 'oc', 'gpio', 'mpsse', 'st', 'jlink', 'ulink', 'xds', 'arm', 'cmsis', 'sp', 'i2c', 'spi'])
    const friendlyProbeName = (cfg) => {
      const raw = String(cfg || '').trim().replace(/\\/g, '/')
      if (!raw) return ''
      const i = raw.lastIndexOf('/')
      let base = i >= 0 ? raw.slice(i + 1) : raw
      base = base.replace(/\.cfg$/i, '')
      const key = base.toLowerCase()
      if (!key) return ''
      if (PROBE_NAME_MAP[key]) return PROBE_NAME_MAP[key]
      // 同一驱动的变体脚本名(stlink-v2-1 / cmsis-dap+swj?)也归到大类
      if (key.indexOf('cmsis') === 0) return 'CMSIS-DAP'
      if (/^(at-?link|daplink)/.test(key)) return /daplink/.test(key) ? 'DAPLink' : 'AT-Link'
      if (/^st-?link/.test(key)) return 'ST-Link'
      if (/^j-?link/.test(key)) return 'J-Link'
      // 未知脚本:美化原名(USB-DAP 这类缩写自动大写)
      return key.split(/[-_]+/).filter(Boolean)
        .map((t) => (PROBE_ACRONYMS.has(t) ? t.toUpperCase() : t.charAt(0).toUpperCase() + t.slice(1)))
        .join('-')
    }

    // 探针段显示值:auto 模式用后端认出的 kind(带证据),钉死模式按脚本名映射
    const probeLabel = (p) => !p ? '' : (p.pinned ? (friendlyProbeName(p.cfg) || p.cfg) : (p.kind || friendlyProbeName(p.cfg) || p.cfg))

    // dev 0x000 是无效读(寄存器不存在时读回了地址本身):不当真数据展示
    const isBogusDev = (dev) => {
      const v = parseInt(String(dev || ''), 16)
      return !Number.isFinite(v) || (v & 0xfff) === 0
    }

    // 内核名缩写:CPUID 解码串("Cortex-M4 (ARMv7-M/v8-M, implementer 'A', r0p1)")
    // 对卡片太长了——卡片只显示 "Cortex-M4 r0p1",架构细节在 tooltip 里看全文
    const coreShort = (core) => {
      const s = String(core || '').trim()
      if (!s) return ''
      const main = s.split('(')[0].trim()
      const rev = (s.match(/r\d+p\d+/i) || [''])[0]
      return rev && main.indexOf(rev) < 0 ? main + ' ' + rev : main
    }

    // 芯片段显示(v1.2.5 起只信实测):
    //   * family 命中 chip_id_map(DEV_ID 实测) → 型号 + 内核,identified=true
    //   * 否则只报 CPUID 解码出的内核 + 有效 DEV_ID,identified=false → 界面补
    //     「型号未识别」;target_cfg 文件名推定的 model 不再上卡(用户配错 cfg 时
    //     那就是误导),只留 tooltip 当线索——实测认不出时,型号唯一可靠来源是
    //     chip_id_map(键 = 实测 DEV_ID)
    const chipLabel = (c) => {
      if (!c) return null
      const fam = (c.family && !/^unknown/i.test(c.family)) ? c.family : ''
      const core = coreShort(c.core)
      // v1.3.2:head/tail 供读数卡分层排版——head=主读数(型号或内核),
      // tail=补充(内核/dev id),窄面板把 tail 整段藏掉,不做半词截断
      if (fam) return { text: fam + (core ? ' · ' + core : ''), identified: true, head: fam, tail: core }
      if (!core) return null
      const dev = (c.devId && !isBogusDev(c.devId)) ? c.devId : ''
      return { text: core + (dev ? ' · dev ' + dev : ''), identified: false, head: core, tail: dev ? 'dev ' + dev : '' }
    }

    // v1.4.0：芯片段没有完整读数时的回退标签(纯函数,可单测):只剩 dev_id
    // (CPUID 读失败但 DEV_ID 读到)时显示 dev id——此前写死 '已连接',看着像
    // 链路状态而不是芯片读数
    const chipFallbackLabel = (chip) =>
      (chip && chip.devId && !isBogusDev(chip.devId)) ? 'dev ' + chip.devId : '已连接'

    // 芯片段 tooltip:实测数据为主,cfg 线索(model)明确标注「非实测」
    const chipTip = (c) => {
      if (!c) return ''
      const ident = !!(c.family && !/^unknown/i.test(c.family))
      const lines = [ident ? 'identify_chip / connect 自动识别' : '⚠ 型号未识别:实测 DEV_ID 没命中 ocd.json 的 chip_id_map']
      if (c.core) lines.push('内核(实测 CPUID): ' + c.core)
      if (c.devId && !isBogusDev(c.devId)) lines.push('DEV_ID(实测): ' + c.devId + (c.revId ? '   rev ' + c.revId : ''))
      if (c.family) lines.push('family(chip_id_map): ' + c.family)
      if (c.model) lines.push('ocd.json target_cfg 推定: ' + c.model + '(仅配置线索,非实测;与实测不符时以实测为准)')
      if (!ident)
        lines.push('型号唯一可靠来源:ocd.json 的 chip_id_map(键 = 实测 DEV_ID)')
      return lines.join('\n') + '\n点击:调用视图只看 identify_chip'
    }

    // Flash 段只写容量(用户要的「多少 k」):bank/基址/驱动名在 tooltip。
    // size 0 = auto-size 配置还没 probe 出来(或没挂 flash 驱动):不显示 "0 KB" 那种
    // 误导人的数,显示「待探测」。
    const fmtKb = (bytes) => (bytes == null || !Number.isFinite(bytes) ? '' : bytes === 0 ? '待探测' : Math.round(bytes / 1024) + ' KB')

    // v0.4.0 target 失配行(identify_chip/connect 的 rsp 末尾):
    //   '  target mismatch: identified AT32F403A, current target_cfg: D:/.../target_py32f002a_lock.cfg -> use set_target AT32F403A'
    // 面板据此给「一键切换」胶囊(点了复制 MCP 调用给会话——面板不能调 MCP)
    const parseTargetMismatch = (text) => {
      const s = String(text || '')
      if (!/target mismatch/i.test(s)) return null
      const m = s.match(/target mismatch:\s*identified\s+(.+?),\s*current target_cfg:\s*(.+?)\s*->\s*use\s+set_target\s+(\S+)/i)
      if (!m) return { family: '', current: '', action: '' }
      return { family: m[1].trim(), current: m[2].trim(), action: m[3].trim() }
    }

    // flash_info 的 bank 行:
    //   '0 : at32f435zmt7.flash (at32f435xx) at 0x08000000, size 0x00100000, buswidth 2, chipwidth 4'
    // total = 各 bank 之和:双 bank 芯片(at32f403axG:512K+512K=1024K)卡片报总数,
    // 「这片 flash 多大」一眼可见;各 bank 明细进 tooltip
    const parseFlashInfo = (text) => {
      const s = String(text || '')
      const re = /(\d+)\s*:\s*(\S+)\s*\(([^)]*)\)\s*at\s+(0x[0-9a-fA-F]+),\s*size\s+(0x[0-9a-fA-F]+)/g
      const banks = []
      let m
      while ((m = re.exec(s)) !== null) {
        banks.push({ bank: Number(m[1]), name: m[2], driver: m[3], base: m[4].toLowerCase(), size: parseInt(m[5], 16) })
      }
      if (!banks.length) return null
      return { banks, first: banks[0], total: banks.reduce((a, b) => a + b.size, 0) }
    }

    // 从原始条目流反推最近一次链路状态(从新往旧扫,三项齐就停;view 无关)
    const deriveLinkInfo = (entries) => {
      const out = { probe: null, chip: null, flash: null, mismatch: null }
      for (let i = entries.length - 1; i >= 0; i--) {
        const e = entries[i]
        if (e.kind === 'gap') continue
        if (!out.probe && e.dir === 'sys' && e.tool === 'backend' && !/^ERROR/i.test(e.text || '')) {
          // v1.4.0：只从非失败行解析探针——启动失败文案里也嵌 probeNote
          // (BuildFailureMessage 带上次选了哪个接口脚本),照解析会让链路行的
          // 「探针」段在 connect 失败后显示一个像是活着的链路
          const p = parseProbeNote(e.text)
          if (p) out.probe = p
        }
        // 芯片来源两种:identify_chip 工具的 rsp,或 connect 自动识别镜像的
        // "chip identity:" 块(tool 可能记成 connect)——按文本形态认,不按工具名
        // 失配行与芯片块是同一份报告的末行:跟着这份报告走(换了 target 后新的
        // identify 没失配行,胶囊就消失,不会赖在历史流量上)
        if (!out.chip && e.dir === 'rsp' && (e.tool === 'identify_chip' || /chip identity:/.test(e.text || ''))) {
          const c = parseChipIdentity(e.text)
          if (c) {
            out.chip = c
            const mm = parseTargetMismatch(e.text)
            out.mismatch = (mm && mm.action) ? mm : null
          }
        }
        if (!out.flash && e.dir === 'rsp' && e.tool === 'flash_info') {
          const f = parseFlashInfo(e.text)
          if (f) out.flash = f
        }
        if (out.probe && out.chip && out.flash && out.mismatch) break
      }
      return out
    }

    const copyText = async (s) => {
      try {
        await navigator.clipboard.writeText(s)
        return true
      } catch (e) {
        try {
          const ta = document.createElement('textarea')
          ta.value = s
          ta.style.position = 'fixed'
          ta.style.opacity = '0'
          document.body.appendChild(ta)
          ta.select()
          const done = document.execCommand('copy')
          document.body.removeChild(ta)
          return done
        } catch (e2) { return false }
      }
    }

    // v1.3.9:带超时的 fetch(轮询/刷新用)。超时即 abort,promise 以 AbortError
    // 回落,调用方的 catch/finally 能把状态机复位——轮询链因此可自愈。
    const fetchWithTimeout = (url, opts, ms) => {
      const ctl = new AbortController()
      const to = setTimeout(() => ctl.abort(), ms)
      return fetch(url, Object.assign({}, opts, { signal: ctl.signal }))
        .finally(() => clearTimeout(to))
    }

    // 当前过滤视图 → JSONL 下载(Blob 本地生成;gap 标记不是数据,剔除;
    // 调用项(kind=call)归一化成 {cmd,rsp,logs} 记录后再序列化)
    const exportForDownload = (items) => items
      .filter((e) => e.kind !== 'gap')
      .map((e) => (e.kind === 'call'
        ? { seq: e.seq, ts: e.ts, dir: 'call', tool: e.tool, state: e.state, dtMs: callDtMs(e), cmd: e.cmd, rsp: e.rsp, logs: e.logs }
        : e))
    const downloadJsonl = (entries, name) => {
      try {
        const lines = entries.map((e) => JSON.stringify(e))
        const blob = new Blob([lines.join('\n') + (lines.length ? '\n' : '')], { type: 'application/x-ndjson' })
        const url = URL.createObjectURL(blob)
        const a = document.createElement('a')
        a.href = url
        a.download = (name || 'ocdup-log-view-') + new Date().toISOString().replace(/[:.]/g, '-') + '.jsonl'
        document.body.appendChild(a)
        a.click()
        document.body.removeChild(a)
        setTimeout(() => URL.revokeObjectURL(url), 1000)
        return true
      } catch (e) { return false }
    }

    // ---- 组件:复制按钮(成功后短暂显示「已复制」) ----
    const CopyBtn = function CopyBtn({ text, label }) {
      const [done, setDone] = React.useState(false)
      const timerRef = React.useRef(null)
      React.useEffect(() => () => { if (timerRef.current) clearTimeout(timerRef.current) }, [])
      const onCopy = async () => {
        if (!(await copyText(text))) return
        setDone(true)
        if (timerRef.current) clearTimeout(timerRef.current)
        timerRef.current = setTimeout(() => setDone(false), 1500)
      }
      return h('button', {
        type: 'button', onClick: onCopy,
        style: {
          appearance: 'none', cursor: 'pointer', fontSize: 11, padding: '2px 8px',
          borderRadius: 6, border: '1px solid var(--dsw-alias-border-l2, #ddd)',
          background: done ? 'var(--dsw-alias-state-success-primary, #10b981)' : 'var(--dsw-alias-bg-layer-3, #fff)',
          color: done ? 'var(--dsw-alias-bg-layer-1, #fff)' : 'var(--dsw-alias-label-secondary, #666)', font: 'inherit',
        },
      }, done ? '已复制' : (label || '复制'))
    }

    // stats 浅比较:Host 每轮都回一个全新对象,不比较就会每轮触发重渲染
    const STATS_KEYS = ['cmd', 'rsp', 'sys', 'err', 'log', 'bytes', 'lastAt', 'exists', 'kept', 'lastSeq',
      'badLines', 'logSize', 'logMtimeMs', 'rotated', 'rotSize', 'rotMtimeMs']
    const sameStats = (a, b) => {
      if (!a || !b) return false
      for (let i = 0; i < STATS_KEYS.length; i++) {
        const k = STATS_KEYS[i]
        if (a[k] !== b[k]) return false
      }
      return true
    }

    let ctxRef = null // apply 时捕获,供图标兜底切换面板用

    // ---- 组件:芯片图标(侧边栏入口;接受 {size, className}) ----
    const ChipGlyph = ({ size, className }) => h('svg', {
      width: size || 16, height: size || 16, viewBox: '0 0 16 16', className,
      fill: 'none', stroke: 'currentColor', strokeWidth: 1.5,
      strokeLinecap: 'round', strokeLinejoin: 'round', style: { display: 'block' },
    },
      h('rect', { x: 4.5, y: 4.5, width: 7, height: 7, rx: 1 }),
      h('path', { d: 'M6.5 1.5v3M9.5 1.5v3M6.5 11.5v3M9.5 11.5v3M1.5 6.5h3M1.5 9.5h3M11.5 6.5h3M11.5 9.5h3' }),
    )

    // ---- 组件:链路段小图标(v1.2.3):探针(信号弧)/ Flash(叠层) ----
    const ProbeGlyph = ({ size }) => h('svg', {
      width: size || 12, height: size || 12, viewBox: '0 0 16 16',
      fill: 'none', stroke: 'currentColor', strokeWidth: 1.5,
      strokeLinecap: 'round', strokeLinejoin: 'round', style: { display: 'block' },
    },
      h('path', { d: 'M2.2 6.4a8.2 8.2 0 0 1 11.6 0' }),
      h('path', { d: 'M4.7 9.2a4.7 4.7 0 0 1 6.6 0' }),
      h('circle', { cx: 8, cy: 12.2, r: 1.3 }),
    )
    const FlashGlyph = ({ size }) => h('svg', {
      width: size || 12, height: size || 12, viewBox: '0 0 16 16',
      fill: 'none', stroke: 'currentColor', strokeWidth: 1.5,
      strokeLinecap: 'round', strokeLinejoin: 'round', style: { display: 'block' },
    },
      h('path', { d: 'M8 1.9 14.1 5 8 8.1 1.9 5z' }),
      h('path', { d: 'M1.9 8 8 11.1 14.1 8' }),
      h('path', { d: 'M1.9 11 8 14.1 14.1 11' }),
    )

    // ---- 组件:传输控制小图标(v1.3.2):暂停/继续/跟随到底 ----
    // (旧版用 ⏸/▶ 字符,和字体渲染绑死;SVG 在任何主题下都同一副形状)
    const iconBox = { display: 'block', flex: '0 0 auto' }
    const PauseGlyph = () => h('svg', { width: 10, height: 10, viewBox: '0 0 10 10', fill: 'currentColor', 'aria-hidden': true, style: iconBox },
      h('rect', { x: 1.6, y: 1.2, width: 2.6, height: 7.6, rx: 1 }),
      h('rect', { x: 5.8, y: 1.2, width: 2.6, height: 7.6, rx: 1 }))
    const PlayGlyph = () => h('svg', { width: 10, height: 10, viewBox: '0 0 10 10', fill: 'currentColor', 'aria-hidden': true, style: iconBox },
      h('path', { d: 'M2.2 1.3 8.6 5 2.2 8.7z' }))
    const TailGlyph = () => h('svg', {
      width: 11, height: 11, viewBox: '0 0 12 12', fill: 'none', stroke: 'currentColor',
      strokeWidth: 1.4, strokeLinecap: 'round', strokeLinejoin: 'round', 'aria-hidden': true, style: iconBox,
    },
      h('path', { d: 'M6 1.6v6.2' }),
      h('path', { d: 'M3.4 5.2 6 7.8l2.6-2.6' }),
      h('path', { d: 'M2 10.4h8' }))
    const ChevronGlyph = () => h('svg', {
      width: 10, height: 10, viewBox: '0 0 10 10', fill: 'none', stroke: 'currentColor',
      strokeWidth: 1.4, strokeLinecap: 'round', strokeLinejoin: 'round', 'aria-hidden': true, style: iconBox,
    }, h('path', { d: 'M2.4 3.9 5 6.5l2.6-2.6' }))
    // 电源/插头(后端段图标):圆弧 + 竖线,断没断开一眼可辨
    const PowerGlyph = () => h('svg', {
      width: 12, height: 12, viewBox: '0 0 16 16', fill: 'none', stroke: 'currentColor',
      strokeWidth: 1.5, strokeLinecap: 'round', strokeLinejoin: 'round', 'aria-hidden': true, style: iconBox,
    },
      h('path', { d: 'M8 1.8v5.4' }),
      h('path', { d: 'M4.3 3.9a4.6 4.6 0 1 0 7.4 0' }))

    // ---- 组件:链路占位项(v1.2.2:缺哪项就提示哪项) ----
    // 面板不能给 AI 发消息:点一下把提问复制到剪贴板,用户粘贴到会话即可。
    const LINK_PROMPT = '连一下板子,看看当前连的是什么烧录器、什么芯片(mcp__ocdup__identify_chip)、flash 布局(mcp__ocdup__flash_info)'
    const LinkPromptBtn = ({ label, tone }) => {
      const [done, setDone] = React.useState(false)
      const timerRef = React.useRef(null)
      React.useEffect(() => () => { if (timerRef.current) clearTimeout(timerRef.current) }, [])
      return h('button', {
        type: 'button', className: 'op-chip op-link op-link-miss',
        title: '链路行还缺这项信息:点此复制提问,粘贴到会话让 Agent 调 identify_chip / flash_info',
        onClick: async () => {
          if (!(await copyText(LINK_PROMPT))) return
          setDone(true)
          if (timerRef.current) clearTimeout(timerRef.current)
          timerRef.current = setTimeout(() => setDone(false), 1500)
        },
        style: {
          appearance: 'none', cursor: 'pointer', fontSize: 11,
          display: 'inline-flex', alignItems: 'center', gap: 5, maxWidth: '100%',
          height: 26, padding: '0 8px', borderRadius: 4, background: 'transparent',
          color: tone || 'var(--dsw-alias-label-tertiary, #999)', font: 'inherit', whiteSpace: 'nowrap',
        },
      }, done ? '已复制' : label + ' 未识别 · 复制提问')
    }

    // ---- 组件:target 失配切换胶囊(v1.3.0 复制 → v1.3.8 直连) ----
    // identify 认出家族与当前 target_cfg 不符时出现。v1.3.8 起点了直接经 Host
    // POST /api/ocdup-target 调 mcp__ocdup__set_target(切换=用新 cfg 重启
    // ocdup 自拉的后端);成功后 onSwitched() 让父组件 connect 一次刷新链路行。
    // 烧录进行中要二次确认:重启后端会打断 AI 的烧录(v1.3.5 同款教训)。
    const SwitchTargetBtn = ({ mismatch, flashOpen, onSwitched }) => {
      const [st, setSt] = React.useState('idle') // idle|confirm|busy|done|err
      const [err, setErr] = React.useState('')
      const timerRef = React.useRef(null)
      React.useEffect(() => () => { if (timerRef.current) clearTimeout(timerRef.current) }, [])
      const flash = (s, ms) => {
        if (timerRef.current) clearTimeout(timerRef.current)
        timerRef.current = setTimeout(() => setSt(s), ms)
      }
      const label = st === 'confirm' ? '确认切换?' : st === 'busy' ? '切换中…'
        : st === 'done' ? '已切换' : st === 'err' ? '失败' : '切换 ' + mismatch.action
      return h('button', {
        type: 'button', className: 'op-chip op-link op-switch',
        title: 'identify 识别为 ' + mismatch.family + ',但当前 target_cfg 是 ' + mismatch.current
          + '\n点击:直接切换 target_cfg 到 ' + mismatch.action + '(set_target 会重启 ocdup 自拉的后端,随后自动重连刷新)'
          + (flashOpen ? '\n⚠ 有烧录正在进行,切换会中断它(需再点一次确认)' : '')
          + (err ? '\n上次失败:' + err : ''),
        onClick: async () => {
          if (st === 'busy') return
          if (flashOpen && st !== 'confirm') {
            setSt('confirm')
            flash('idle', 3000)
            return
          }
          setSt('busy')
          setErr('')
          try {
            // v1.4.0：客户端自己的超时——Host 侧 10s AbortController 只在宿主
            // tools.execute 认 signal 时才收得住;不认时挂起的请求会把按钮
            // 永久卡在「切换中…」。15s = Host 10s + 余量。
            const ac = new AbortController()
            const to = setTimeout(() => ac.abort(), 15000)
            let r
            try {
              r = await fetch(ROUTE_TARGET, {
                method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ preset: mismatch.action }),
                signal: ac.signal,
              })
            } finally { clearTimeout(to) }
            const j = await r.json()
            if (j && j.ok) {
              setSt('done')
              flash('idle', 1500)
              if (onSwitched) onSwitched()
            } else {
              setErr((j && j.error) || ('HTTP ' + r.status))
              setSt('err')
              flash('idle', 6000)
            }
          } catch (e) {
            setErr(e && e.message ? e.message : String(e))
            setSt('err')
            flash('idle', 6000)
          }
        },
        style: {
          appearance: 'none', cursor: 'pointer', fontSize: 11,
          display: 'inline-flex', alignItems: 'center', gap: 5, maxWidth: '100%',
          height: 26, padding: '0 8px', borderRadius: 4,
          font: 'inherit', fontWeight: 600, whiteSpace: 'nowrap',
        },
      }, label)
    }

    // ---- 组件:行条目(memo:引用未变的行不重渲染) ----
    const Row = React.memo(function Row({ e, selected, onSelect, matcher, onPort }) {
      const payload = e.text.length > ROW_PAYLOAD_MAX ? e.text.slice(0, ROW_PAYLOAD_MAX) + ' …' : e.text
      const payloadChildren = !matcher ? payload
        : matcher instanceof RegExp ? highlightRe(payload, matcher)
        : highlight(payload, matcher)
      const mark = rowMark(e)
      return h('div', {
        className: 'op-row op-dir-' + e.dir + (selected ? ' op-sel' : '') + (mark ? ' op-mark-' + mark : ''),
        onClick: () => onSelect(e),
        style: {
          display: 'flex', alignItems: 'baseline', gap: 8,
          minWidth: 0, width: '100%', boxSizing: 'border-box',
          padding: '3px 10px', cursor: 'pointer',
          borderBottom: '1px solid var(--dsw-alias-border-l2, #f0f0f0)',
          fontSize: 12, lineHeight: 1.6,
        },
      },
        // 方向色条:整列扫描时最快定位 cmd/rsp/err/log
        h('span', {
          style: {
            flex: '0 0 3px', alignSelf: 'stretch', borderRadius: 2, marginRight: 3,
            background: DIR_COLOR[e.dir] || '#888',
          },
        }),
        h('span', { style: { flex: '0 0 84px', color: 'var(--dsw-alias-label-tertiary, #999)', fontVariantNumeric: 'tabular-nums' } }, shortTime(e.ts)),
        h('span', {
          title: e.dir,
          style: {
            flex: '0 0 34px', textAlign: 'center', borderRadius: 4, fontSize: 10, letterSpacing: '.04em',
            fontWeight: 700, color: '#fff', background: DIR_COLOR[e.dir] || '#888', padding: '1px 4px',
          },
        }, DIR_LABEL[e.dir] || e.dir),
        e.tag ? h('span', {
          title: '按目标 ' + e.tag + ' 过滤(再点切回全部)',
          onClick: (ev) => {
            ev.stopPropagation()
            if (typeof onPort === 'function') onPort(e.tag)
          },
          style: {
            flex: '0 0 8px', height: 8, borderRadius: 999, alignSelf: 'center',
            cursor: 'pointer',
            background: 'hsl(' + hueOf(e.tag) + ', 55%, 45%)',
          },
        }) : null,
        h('span', { className: 'op-tool', style: { flex: '0 0 110px', overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', color: 'var(--dsw-alias-label-secondary, #666)' } }, e.tool),
        h('span', { className: 'op-len', style: { flex: '0 0 44px', textAlign: 'right', color: 'var(--dsw-alias-label-tertiary, #999)', fontVariantNumeric: 'tabular-nums' } }, e.len),
        h('span', {
          className: 'op-dt', title: '距上一条',
          style: { flex: '0 0 48px', textAlign: 'right', fontSize: 11, color: 'var(--dsw-alias-label-tertiary, #999)', fontVariantNumeric: 'tabular-nums' },
        }, fmtDt(e.dtMs)),
        mark ? h('span', {
          title: mark === 'ok' ? '校验通过' : '错误',
          style: {
            flex: '0 0 auto', fontWeight: 700, fontSize: 11,
            color: mark === 'ok' ? 'var(--dsw-alias-state-success-primary, #10b981)' : 'var(--dsw-alias-state-error-primary, #ef4444)',
          },
        }, mark === 'ok' ? '✓' : '✕') : null,
        h('span', {
          title: e.text,
          style: {
            flex: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
            fontFamily: 'Consolas, Menlo, monospace', color: 'var(--dsw-alias-label-primary, #222)',
          },
        }, payloadChildren),
      )
    })

    // ---- 组件:断层标记(增量 seq 跳变 / Host 环滚出) ----
    const GapRow = React.memo(function GapRow({ count }) {
      return h('div', {
        style: {
          padding: '3px 10px', textAlign: 'center', fontSize: 11,
          color: 'var(--dsw-alias-label-tertiary, #999)',
          background: 'var(--dsw-alias-bg-layer-3, #fafafa)',
          borderBottom: '1px solid var(--dsw-alias-border-l2, #f0f0f0)',
        },
      }, '… 已省略 ' + count + ' 条(超出 Host 保留窗口)…')
    })

    // ---- 组件:会话分隔条(backend 生命周期;点它折叠/展开本会话) ----
    const SessionRow = React.memo(function SessionRow({ e, collapsed, count, onToggle }) {
      return h('div', {
        className: 'op-session',
        onClick: onToggle,
        title: collapsed ? '展开本会话(' + count + ' 条)' : '折叠本会话(' + count + ' 条)',
        style: {
          display: 'flex', alignItems: 'center', gap: 8,
          padding: '5px 10px', cursor: 'pointer', userSelect: 'none',
          background: 'var(--dsw-alias-bg-layer-3, #f5f5f5)',
          borderTop: '1px solid var(--dsw-alias-border-l2, #e8e8e8)',
          borderBottom: '1px solid var(--dsw-alias-border-l2, #e8e8e8)',
          fontSize: 11, color: 'var(--dsw-alias-label-secondary, #666)',
        },
      },
        h('span', { style: { flex: '0 0 12px', textAlign: 'center', color: 'var(--dsw-alias-label-tertiary, #999)' } }, collapsed ? '▸' : '▾'),
        h('span', { style: { flex: '0 0 auto', fontWeight: 700, color: 'var(--dsw-alias-label-primary, #222)' } }, '会话'),
        h('span', {
          style: {
            flex: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
            fontFamily: 'Consolas, Menlo, monospace',
          },
        }, shortTime(e.ts) + '  ' + e.text),
        h('span', { style: { flex: '0 0 auto', color: 'var(--dsw-alias-label-tertiary, #999)', fontVariantNumeric: 'tabular-nums' } },
          (collapsed ? '已折叠 · ' : '') + count + ' 条'),
      )
    })

    // ---- 组件:调用行(配对视图:一次 MCP 工具调用 = 一行) ----
    const CALL_GLYPH = {
      ok: { g: '✓', color: TONE_OK, title: '完成' },
      err: { g: '✕', color: TONE_ERR, title: '失败' },
      open: { g: '⏳', color: TONE_WARN, title: '进行中' },
    }
    const CallRow = React.memo(function CallRow({ call, selected, onSelect, matcher }) {
      const st = CALL_GLYPH[call.state] || CALL_GLYPH.open
      const dt = callDtMs(call)
      const summary = call.cmd || ''
      const oneLine = summary.length > ROW_PAYLOAD_MAX ? summary.slice(0, ROW_PAYLOAD_MAX) + ' …' : summary
      const flash = isFlashTool(call.tool) ? flashProgressOfCall(call) : null
      return h('div', {
        className: 'op-row op-dir-call' + (selected ? ' op-sel' : ''),
        onClick: () => onSelect(call),
        style: {
          display: 'flex', alignItems: 'baseline', gap: 8,
          minWidth: 0, width: '100%', boxSizing: 'border-box',
          padding: '3px 10px', cursor: 'pointer',
          borderBottom: '1px solid var(--dsw-alias-border-l2, #f0f0f0)',
          fontSize: 12, lineHeight: 1.6,
        },
      },
        h('span', { style: { flex: '0 0 3px', alignSelf: 'stretch', borderRadius: 2, marginRight: 3, background: st.color } }),
        h('span', { style: { flex: '0 0 84px', color: 'var(--dsw-alias-label-tertiary, #999)', fontVariantNumeric: 'tabular-nums' } }, shortTime(call.ts)),
        h('span', {
          title: st.title,
          style: {
            flex: '0 0 34px', textAlign: 'center', borderRadius: 4, fontSize: 11,
            fontWeight: 700, color: st.color,
            background: call.state === 'open' ? 'transparent' : 'color-mix(in srgb, currentColor 14%, transparent)',
            border: '1px solid ' + (call.state === 'open' ? st.color : 'transparent'),
            padding: '0 4px',
          },
        }, st.g),
        h('span', {
          className: 'op-tool',
          style: {
            flex: '0 0 110px', overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
            color: 'var(--dsw-alias-label-primary, #222)', fontWeight: 600,
          },
        }, call.tool),
        h('span', {
          className: 'op-dt', title: '调用耗时',
          style: { flex: '0 0 48px', textAlign: 'right', fontSize: 11, color: 'var(--dsw-alias-label-tertiary, #999)', fontVariantNumeric: 'tabular-nums' },
        }, fmtDt(dt)),
        h('span', {
          style: {
            flex: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
            fontFamily: 'Consolas, Menlo, monospace', color: 'var(--dsw-alias-label-secondary, #666)',
          },
        }, matcher
          ? (matcher instanceof RegExp ? highlightRe(oneLine, matcher) : highlight(oneLine, matcher))
          : oneLine),
        call.logs.length ? h('span', {
          title: '这次调用还带 ' + call.logs.length + ' 条 openocd 日志(详情区查看)',
          style: {
            flex: '0 0 auto', fontSize: 10, padding: '0 6px', borderRadius: 999,
            color: 'var(--dsw-alias-label-tertiary, #767676)', border: '1px solid var(--dsw-alias-border-l2, #e0e0e0)',
          },
        }, call.logs.length + ' 条日志') : null,
        flash && flash.sectors ? h('span', {
          title: '擦除 ' + flash.sectors + ' 扇区',
          style: { flex: '0 0 auto', fontSize: 10, color: 'var(--dsw-alias-label-tertiary, #999)' },
        }, flash.sectors + ' 扇区') : null,
      )
    })

    // ---- 组件:Ocdup 实时面板(右侧停靠栏 tab 体 / 旧版整屏 main 面板) ----
    const EMPTY_TAB_INFO = { tab: null }
    const OcdupPanel = (props) => {
      const prefs0 = React.useMemo(loadPrefs, [])
      const useTabInfoSafe = props && typeof props.useTabInfo === 'function' ? props.useTabInfo : () => EMPTY_TAB_INFO
      const tabInfo = useTabInfoSafe()
      const tabVisible = !(tabInfo && tabInfo.tab && tabInfo.tab.visible === false)
      const tabVisibleRef = React.useRef(tabVisible)
      React.useEffect(() => {
        tabVisibleRef.current = tabVisible
        // v1.3.9:切回本标签立即补一拍——后台这些拍可能整段没收到(挂起/节流),
        // 回来时不该还看着旧状态;kick 会清掉排队中的定时器直接 tick。
        if (tabVisible && kickRef.current) kickRef.current()
      }, [tabVisible])
      const [entries, setEntries] = React.useState([])
      const [stats, setStats] = React.useState(null)
      const [connected, setConnected] = React.useState(false)
      const [paused, setPaused] = React.useState(false)
      const [dir, setDir] = React.useState(prefs0.dir || 'ALL')
      const [port, setPort] = React.useState(prefs0.port || 'ALL')
      const [follow, setFollow] = React.useState(prefs0.follow !== false)
      const [useRegex, setUseRegex] = React.useState(!!prefs0.useRegex)
      const [view, setView] = React.useState(prefs0.view === 'calls' ? 'calls' : 'stream')
      const [tool, setTool] = React.useState('ALL')
      const [collapsed, setCollapsed] = React.useState(() => new Set())
      const [autoSuspended, setAutoSuspended] = React.useState(false)
      const [newCount, setNewCount] = React.useState(0)
      const [confirmClear, setConfirmClear] = React.useState(false)
      // v1.3.4 后端段:busy / 一次性反馈 / 烧录中的二次确认
      const [backendBusy, setBackendBusy] = React.useState(false)
      const [backendMsg, setBackendMsg] = React.useState(null)
      const [confirmBackend, setConfirmBackend] = React.useState(false)
      const backendTimerRef = React.useRef(null)
      const [search, setSearch] = React.useState('')
      const [selected, setSelected] = React.useState(null)
      // v1.3.6:加载轮转文件(.1)的一次性备注(成功条数 / 已加载过 / 失败原因)
      const [rotNote, setRotNote] = React.useState(null)
      const rotTimerRef = React.useRef(null)
      const lastSeqRef = React.useRef(0)
      const pausedRef = React.useRef(paused)
      const listRef = React.useRef(null)
      const searchRef = React.useRef(null)
      const rootRef = React.useRef(null)
      const confirmTimerRef = React.useRef(null)
      const inFlightRef = React.useRef(false)
      const autoSuspendedRef = React.useRef(false)
      // v1.3.9:轮询 effect 暴露的「立即补一拍」——切回本标签/窗口聚焦时调用
      const kickRef = React.useRef(null)

      React.useEffect(() => { pausedRef.current = paused }, [paused])
      React.useEffect(() => { autoSuspendedRef.current = autoSuspended }, [autoSuspended])
      React.useEffect(() => { savePrefs({ dir, follow, port, useRegex, view }) }, [dir, follow, port, useRegex, view])
      React.useEffect(() => () => {
        if (confirmTimerRef.current) clearTimeout(confirmTimerRef.current)
        if (backendTimerRef.current) clearTimeout(backendTimerRef.current)
        if (rotTimerRef.current) clearTimeout(rotTimerRef.current)
      }, [])

      // ---- 自适应增量轮询(单条 setTimeout 链,避免固定间隔空转) ----
      // v1.3.9 两个自愈补丁(用户实测:视图冻死 3 分钟不刷新):
      //   ① fetch 走 fetchWithTimeout(10s abort)——挂起的请求不再把链永久楔住;
      //   ② kick():切回本标签 / 窗口重新聚焦 / visibilitychange 都立即补一拍,
      //      不等下一拍(后台拍可能整段没收到,回来时不该还看旧状态)。
      React.useEffect(() => {
        let alive = true
        let timer = null

        const schedule = (delay) => {
          if (!alive) return
          // v1.4.0：排新定时器前清掉旧的——kick 与在飞 tick 的收尾竞态曾各自
          // 排一条,无主的那条fire 后多跑一次冗余轮询(每竞态一次,累积)
          if (timer !== null) { clearTimeout(timer); timer = null }
          timer = setTimeout(tick, delay)
        }

        const tick = async () => {
          timer = null
          if (!alive) return
          if (pausedRef.current) { schedule(POLL_PAUSED_MS); return }
          if (document.visibilityState === 'hidden') { schedule(POLL_HIDDEN_MS); return }
          if (!tabVisibleRef.current) { schedule(POLL_HIDDEN_MS); return }
          if (inFlightRef.current) { schedule(POLL_ACTIVE_MS); return }

          let got = false
          inFlightRef.current = true
          try {
            const r = await fetchWithTimeout(ROUTE + '?since=' + lastSeqRef.current, { cache: 'no-store' }, POLL_FETCH_TIMEOUT_MS)
            const j = await r.json()
            if (!alive) return
            setConnected(!!(j && j.ok))
            if (j && j.ok) {
              if (j.stats) setStats((prev) => (sameStats(prev, j.stats) ? prev : j.stats))
              const inc = j.entries || []
              if (inc.length) {
                got = true
                const prevLast = lastSeqRef.current
                const firstSeq = inc[0].seq
                lastSeqRef.current = inc[inc.length - 1].seq
                if (autoSuspendedRef.current) setNewCount((c) => c + inc.length)
                setEntries((prev) => {
                  let next
                  if (firstSeq <= prevLast) {
                    next = inc.slice() // Host 重启(seq 归零):整体替换
                  } else if (firstSeq > prevLast + 1) {
                    next = prev.concat([{ kind: 'gap', seq: firstSeq, count: firstSeq - prevLast - 1 }], inc)
                  } else {
                    next = prev.concat(inc)
                  }
                  return next.length > MAX_ROWS_KEPT ? next.slice(next.length - MAX_ROWS_KEPT) : next
                })
              }
            }
          } catch (e) { if (alive) setConnected(false) } finally {
            inFlightRef.current = false
          }
          schedule(got ? POLL_ACTIVE_MS : POLL_IDLE_MS)
        }

        // 立即补一拍:清掉排队中的定时器直接 tick(tick 自己尊重 hidden/paused/inFlight)
        const kick = () => {
          if (!alive) return
          if (timer !== null) { clearTimeout(timer); timer = null }
          tick()
        }
        const onVis = () => {
          if (document.visibilityState !== 'visible' || !alive) return
          kick()
        }
        const onFocus = () => { if (alive) kick() }
        document.addEventListener('visibilitychange', onVis)
        window.addEventListener('focus', onFocus)
        kickRef.current = kick
        tick()

        return () => {
          alive = false
          kickRef.current = null
          if (timer !== null) clearTimeout(timer)
          document.removeEventListener('visibilitychange', onVis)
          window.removeEventListener('focus', onFocus)
        }
      }, [])

      // ---- 跟随滚动:用户向上翻历史时自动挂起,回到底部自动恢复 ----
      const effectiveFollow = follow && !autoSuspended
      React.useEffect(() => {
        const el = listRef.current
        if (el && effectiveFollow) el.scrollTop = el.scrollHeight
      }, [entries, effectiveFollow])

      const onScroll = () => {
        const el = listRef.current
        if (!el) return
        const atBottom = el.scrollTop + el.clientHeight >= el.scrollHeight - 24
        autoSuspendedRef.current = !atBottom
        setAutoSuspended(!atBottom)
        if (atBottom) setNewCount(0)
      }

      const jumpToLatest = () => {
        const el = listRef.current
        if (el) el.scrollTop = el.scrollHeight
        setNewCount(0)
      }

      // ---- 清空(两段式确认) ----
      const clear = async () => {
        if (!confirmClear) {
          setConfirmClear(true)
          if (confirmTimerRef.current) clearTimeout(confirmTimerRef.current)
          confirmTimerRef.current = setTimeout(() => setConfirmClear(false), 3000)
          return
        }
        if (confirmTimerRef.current) clearTimeout(confirmTimerRef.current)
        setConfirmClear(false)
        try {
          const r = await fetch(ROUTE_CLEAR, { method: 'POST' })
          const j = await r.json()
          if (j && j.ok) {
            // rotNote 一起清:它说的是「已加载 N 条」,条目没了这句话就是谎话
            setEntries([]); setSelected(null); setCollapsed(new Set()); setRotNote(null)
            if (j.stats) { setStats(j.stats); lastSeqRef.current = j.stats.lastSeq || 0 }
          }
        } catch (e) { /* 忽略 */ }
      }

      // ---- v1.4.0:MCP 启停开关(状态来自 Host view().mcp,随主路由轮询一并刷新) ----
      const mcp = stats && stats.mcp ? stats.mcp : null
      const mcpAvailable = !!(mcp && mcp.available)
      const mcpOn = !!(mcp && mcp.enabled)
      const toggleMcp = async () => {
        if (!mcpAvailable) return
        const next = !mcpOn
        try {
          const r = await fetch(ROUTE_MCP, {
            method: 'POST', cache: 'no-store',
            headers: { 'content-type': 'application/json' },
            body: JSON.stringify({ enabled: next }),
          })
          const j = await r.json()
          if (j && j.ok && j.mcp) {
            setStats((prev) => (prev ? Object.assign({}, prev, { mcp: j.mcp }) : prev))
          }
        } catch (e) { /* 忽略:下一轮轮询会纠正显示 */ }
      }

      // ---- v1.3.6:加载轮转文件 .1 的尾部窗口(空态按钮;Host 同路径也被轮转
      // 自动续接复用)。合并规则与轮询增量一致:seq 断档插 gap 标记、归零整体替换
      const showRotNote = (text) => {
        setRotNote(text)
        if (rotTimerRef.current) clearTimeout(rotTimerRef.current)
        rotTimerRef.current = setTimeout(() => setRotNote(null), 6000)
      }
      const loadRotated = async () => {
        try {
          const r = await fetch(ROUTE_ROTATED, { method: 'POST', cache: 'no-store' })
          const j = await r.json()
          if (j && j.ok) {
            const inc = j.entries || []
            if (inc.length) {
              const prevLast = lastSeqRef.current
              const firstSeq = inc[0].seq
              lastSeqRef.current = inc[inc.length - 1].seq
              setEntries((prev) => {
                let next
                if (firstSeq <= prevLast) next = inc.slice()
                else if (firstSeq > prevLast + 1) {
                  next = prev.concat([{ kind: 'gap', seq: firstSeq, count: firstSeq - prevLast - 1 }], inc)
                } else next = prev.concat(inc)
                return next.length > MAX_ROWS_KEPT ? next.slice(next.length - MAX_ROWS_KEPT) : next
              })
              showRotNote('已加载 .1 尾部 ' + inc.length + ' 条')
            } else {
              showRotNote(j.note === 'already loaded' ? '这份 .1 已经加载过了' : (j.note || '.1 没有可加载的内容'))
            }
          } else {
            showRotNote('加载失败:' + ((j && j.error) || 'unknown'))
          }
        } catch (e) { showRotNote('加载失败:' + (e && e.message ? e.message : String(e))) }
      }

      // ---- 目标(host:port)清单(用于下拉;选中项被裁出窗口时保留该选项防值失效) ----
      const ports = React.useMemo(() => {
        const out = []
        for (let i = 0; i < entries.length; i++) {
          const t = entries[i].tag
          if (t && out.indexOf(t) < 0) out.push(t)
        }
        return out
      }, [entries])
      const portOptions = port !== 'ALL' && ports.indexOf(port) < 0 ? ports.concat(port) : ports

      // ---- 过滤结果缓存:只在数据/视图/方向/目标/工具/匹配器变化时重算 ----
      const matcher = React.useMemo(() => compileMatcher(search, useRegex), [search, useRegex])

      // 视图基准:流水 = 原始条目;调用 = 配对后的调用项(call/原样条目/gap)
      // v1.4.0：配对结果单独提一个 memo——烧录进度与「烧录中」二次确认两种视图
      // 都要用(此前 flashCall 只从 basis 取,默认的流水视图里没有 kind=call 项,
      // 进度条与两处确认保护整个不出现)。
      const paired = React.useMemo(() => pairCalls(entries), [entries])
      const basis = React.useMemo(() => (view === 'calls' ? paired : entries), [paired, entries, view])

      const visible = React.useMemo(() => {
        const out = []
        for (let i = 0; i < basis.length; i++) {
          const it = basis[i]
          if (it.kind === 'gap') {
            // v1.4.0：gap 行也服从 port 过滤——按 host:port 筛的时候不该插一条
            // 与当前目标无关的「已省略 N 条」
            if (dir === 'ALL' && !matcher && tool === 'ALL' && port === 'ALL') out.push(it)
            continue
          }
          if (view === 'stream') {
            if (dir !== 'ALL' && it.dir !== dir) continue
          } else if (dir === 'err') {
            // 调用视图的方向过滤只剩语义明确的「只看失败」
            if (it.kind === 'call' ? it.state !== 'err' : it.dir !== 'err') continue
          }
          if (tool !== 'ALL' && (it.tool || '') !== tool) continue
          if (port !== 'ALL' && (it.tag || '') !== port) continue
          if (matcher) {
            if (it.kind === 'call') {
              // v1.4.0：haystack 截到 ROW_PAYLOAD_MAX——与行显示的截断同源,
              // 命中落在截断尾部时不再「行被保留但高亮丢失」;同时把用户正则的
              // 回溯面从 cmd+rsp+logs 拼起来的 ~12KB 收到与单行同一上限
              const hay = ((it.cmd || '') + '\n' + (it.rsp || '') + '\n' + (it.logs || []).join('\n'))
                .slice(0, ROW_PAYLOAD_MAX)
              const hit = matcher instanceof RegExp ? matcher.test(hay) : hay.toLowerCase().indexOf(matcher) >= 0
              if (!hit) continue
            } else if (!matcherHit(matcher, it)) continue
          }
          out.push(it)
        }
        return out
      }, [basis, view, dir, port, tool, matcher])

      // 会话折叠:先按折叠集合剔除(分隔条保留),再数每个会话的条数
      const runs = React.useMemo(() => sessionRuns(visible), [visible])
      const shown = React.useMemo(() => applyCollapse(visible, collapsed), [visible, collapsed])
      const visibleCount = shown.length
      const rendered = visibleCount > MAX_RENDER_ROWS ? shown.slice(visibleCount - MAX_RENDER_ROWS) : shown

      // 工具 chips:频次降序(最多 10 个,其余进「其他」不计);
      // backend 是生命周期事件的 tool,不出现在 chips(sys 方向过滤已覆盖)
      const chips = React.useMemo(() => toolOptions(view === 'calls' ? basis : entries).filter((t) => t.name !== 'backend'), [basis, view, entries])
      const chipList = chips.length > 10 ? chips.slice(0, 10) : chips

      // 烧录进度:最近一次 flash_write/flash_erase_sector 调用(仅在其仍处于
      // 「最新动态」时显示——进行中,或它之后还没有别的新调用)
      // v1.4.0：flashCall 从 paired 取(两种视图都有);「仍是最新动态」用纯函数
      // flashStillLatest 判(调用视图对象同一性 / 流水视图应答行 seq 相等)
      const flashCall = React.useMemo(() => lastFlashCall(paired), [paired])
      const flashProg = flashCall ? flashProgressOfCall(flashCall) : null
      const flashShow = !!flashStillLatest(flashCall, basis)
      const errCount = (stats && stats.err) || 0

      // v1.3.2:统一控件皮相(尺寸/圆角/focus 环走 CSS 类,行内只留布局)。
      // 所有控件 nowrap + flex:0 0 auto——窄面板下宁可整组换行,也不折字
      // (旧版「暂停/跟随」被挤成两行,是这张面板最刺眼的崩坏)。
      // active → data-on=1 墨色实底;tone: warn(跟随挂起)/ danger(确认清空)
      const btn = (label, onClick, active, title, tone, icon) => h('button', {
        type: 'button', title: title || '', onClick, className: 'op-btn',
        'data-on': active ? '1' : '0', 'data-tone': tone || '',
        style: {
          appearance: 'none', cursor: 'pointer', fontSize: 12, height: 26, flex: '0 0 auto',
          display: 'inline-flex', alignItems: 'center', gap: 5,
          padding: '0 9px', borderRadius: 6, font: 'inherit', whiteSpace: 'nowrap',
        },
      }, icon || null, label)

      // ---- 工具栏构件:stats 占比条 / 分段按钮 / chips / 烧录进度条 ----
      const statsBar = (st) => {
        if (!st) return null
        const segs = [
          ['cmd', st.cmd, DIR_COLOR.cmd], ['rsp', st.rsp, DIR_COLOR.rsp],
          ['sys', st.sys, DIR_COLOR.sys], ['log', st.log, DIR_COLOR.log], ['err', st.err, DIR_COLOR.err],
        ].filter((s) => s[1] > 0)
        const total = segs.reduce((a, s) => a + s[1], 0)
        if (total <= 0) return null
        return h('span', {
          title: segs.map((s) => (DIR_LABEL[s[0]] || s[0]) + ' ' + s[1]).join('   '),
          style: {
            display: 'inline-flex', width: 64, height: 6, borderRadius: 3, overflow: 'hidden', flex: '0 0 auto',
            background: 'var(--dsw-alias-border-l2, #eee)',
          },
        }, segs.map((s) => h('span', { key: s[0], style: { width: (s[1] / total * 100) + '%', background: s[2], height: '100%' } })))
      }

      // 分段按钮(视图切换:流水/调用)——选中段墨色填充
      const seg = (label, active, onClick, title, first) => h('button', {
        type: 'button', title, onClick, className: 'op-seg',
        'data-on': active ? '1' : '0',
        style: {
          appearance: 'none', cursor: 'pointer', fontSize: 12, height: 26, flex: '0 0 auto',
          padding: '0 11px', marginLeft: first ? 0 : -1,
          display: 'inline-flex', alignItems: 'center', font: 'inherit', whiteSpace: 'nowrap',
        },
      }, label)

      // 过滤 chip:方向 / 工具 / 只看失败 共用同一种语言(带可选通道色方点);
      // engaged 一律墨色实底——两处 chipp 的交互也因此完全一致(点中=过滤,再点=取消)
      const chipBtn = (label, active, onClick, count, color) => h('button', {
        type: 'button', className: 'op-chip', 'data-on': active ? '1' : '0',
        title: active ? '点击取消该过滤' : '点击只显示 ' + label, onClick,
        style: {
          appearance: 'none', cursor: 'pointer', fontSize: 11, height: 24, flex: '0 0 auto',
          display: 'inline-flex', alignItems: 'center', gap: 5,
          padding: '0 9px', borderRadius: 999, fontVariantNumeric: 'tabular-nums',
          border: '1px solid ' + (active ? 'transparent' : 'var(--dsw-alias-border-l2, #e0e0e0)'),
          background: active ? 'var(--dsw-alias-label-primary, #222)' : 'transparent',
          color: active ? 'var(--dsw-alias-bg-layer-2, #fff)' : 'var(--dsw-alias-label-secondary, #666)',
          font: 'inherit', whiteSpace: 'nowrap',
        },
      },
        color ? h('span', { style: { width: 6, height: 6, borderRadius: 2, flex: '0 0 auto', background: color } }) : null,
        label,
        count != null && count !== ''
          ? h('span', { style: { color: active ? 'inherit' : 'var(--dsw-alias-label-tertiary, #767676)' } }, String(count))
          : null)

      // 小刻度标签(分组名,弱化到不抢数据)
      const tick = (text, cls) => h('span', {
        className: cls || undefined,
        style: { flex: '0 0 auto', fontSize: 11, color: 'var(--dsw-alias-label-tertiary, #767676)' },
      }, text)

      // 烧录进度条文字:进行中(擦除/写入/校验分段)→ 结果(✓ 字节+校验 / ! 存疑 / ✕ 失败)
      // v1.5.3:结论行(OCDUP-RESULT)优先。此前 `if (prog.bytes)` 把「写了 N 字节」当成功,
      // 于是 VERIFY-FAILED / INCONCLUSIVE 也显示绿色 ✓ N 字节——校验失败被画成通过。
      const flashBarText = (call, prog) => {
        const dt = callDtMs(call)
        const tail = dt != null ? ' · ' + fmtDt(dt) : ''
        if (call.state === 'err') return { text: '✕ 失败' + tail, tone: TONE_ERR, active: false }
        if (call.state === 'open') {
          const label = prog.phase === 'erase' ? '擦除中… 已擦 ' + prog.sectors + ' 扇区'
            : prog.phase === 'write' ? '写入中' + (prog.bytes ? ' ' + prog.bytes + ' 字节' : '') + '…'
            : prog.phase === 'verify' ? '校验中…'
            : '进行中…'
          return { text: label, tone: TONE_WARN, active: true }
        }
        if (prog.tag) {
          const t = verdictToneOf(prog.tag.verdict)
          const glyph = t.mark === 'ok' ? '✓' : t.mark === 'warn' ? '!' : '✕'
          const size = prog.bytes ? ' ' + prog.bytes + ' 字节' : ''
          const verb = prog.tag.op === 'verify' ? '校验' : prog.tag.op === 'dump' ? '导出' : '烧录'
          return {
            text: glyph + size + ' · ' + verb + ' ' + prog.tag.verdict + tail,
            tone: t.tone, active: false,
          }
        }
        if (prog.ok) return { text: '✓ ' + prog.bytes + ' 字节 · 校验 OK' + tail, tone: TONE_OK, active: false }
        // 无结论标签的老版本后端:只有擦除/写入字节而没有校验确认 → 不能说「通过」
        if (prog.bytes) return { text: '· ' + prog.bytes + ' 字节' + (prog.sectors ? ' · 擦 ' + prog.sectors + ' 扇区' : '') + ' · 未校验' + tail, tone: TONE_WARN, active: false }
        return { text: '· 完成' + tail, tone: TONE_WARN, active: false }
      }

      const toggleView = (v) => {
        setView(v)
        // 调用视图的方向过滤只剩 ALL/err(其它方向没有「调用」语义)
        if (v === 'calls' && dir !== 'ALL' && dir !== 'err') setDir('ALL')
      }

      // v1.3.2:工具栏仍是两行(上排「看什么」/下排「怎么筛」),但去掉行间发丝线,
      // 靠间距分组——四条横线会把面板切成碎块。
      //  上排:信号灯+占比表 ··· 流水|调用 ⏸暂停 跟随 (目标)
      //  下排:方向通道 chip(cmd/rsp/sys/log/err,点中即过滤) ··· 搜索 .* 清空 导出
      const toolbarBar = {
        display: 'flex', alignItems: 'center', gap: 6,
        padding: '0 12px', minWidth: 0,
      }
      const legend = dirLegend(stats, dir)
      const counterText = counterLabel(rendered.length, visibleCount, entries.length)
      const counterTip = '当前视图条数 / 输入条目总数'
        + (rendered.length < visibleCount ? '(列表最多挂载 ' + MAX_RENDER_ROWS + ' 行)' : '')
      // 目标下拉:只有一个目标且没在过滤时不占位——没有可选的东西就不放控件
      const showPort = portOptions.length > 1 || port !== 'ALL'
      const portSelect = showPort ? h('span', {
        style: { position: 'relative', display: 'inline-flex', alignItems: 'center', flex: '0 0 auto' },
      },
        h('select', {
          value: port, onChange: (e) => setPort(e.target.value), className: 'op-select',
          title: '按目标 (host:port) 过滤',
          style: { fontSize: 12, height: 26, padding: '0 22px 0 9px', borderRadius: 6, font: 'inherit', maxWidth: 134, textOverflow: 'ellipsis' },
        },
          h('option', { value: 'ALL' }, '全部目标'),
          portOptions.map((t) => h('option', { key: t, value: t }, t)),
        ),
        h('span', {
          style: {
            position: 'absolute', right: 7, display: 'inline-flex', pointerEvents: 'none',
            color: 'var(--dsw-alias-label-tertiary, #767676)',
          },
        }, h(ChevronGlyph)),
      ) : null
      const searchField = h('span', {
        style: { position: 'relative', display: 'inline-flex', alignItems: 'center', flex: '1 1 160px', minWidth: 120 },
      },
        h('svg', {
          width: 12, height: 12, viewBox: '0 0 16 16', 'aria-hidden': true,
          style: { position: 'absolute', left: 9, top: '50%', transform: 'translateY(-50%)', color: 'var(--dsw-alias-label-tertiary, #999)', pointerEvents: 'none' },
          fill: 'none', stroke: 'currentColor', strokeWidth: 1.6, strokeLinecap: 'round',
        },
          h('circle', { cx: 7, cy: 7, r: 4.2 }),
          h('path', { d: 'M10.2 10.2 13.6 13.6' })),
        h('input', {
          ref: searchRef, className: 'op-search',
          value: search,
          // 工具过滤有专用 chip 条,搜索框只讲命令/响应,占位文案短到不被裁
          placeholder: view === 'calls' ? '过滤 调用 / 日志 / 响应' : '过滤 命令 / 响应',
          onChange: (e) => setSearch(e.target.value),
          style: { width: '100%', height: 26, fontSize: 12, padding: '0 30px 0 26px', borderRadius: 6, boxSizing: 'border-box', font: 'inherit' },
        }),
        // 正则开关做进输入框右端(旧版是浮在行里的一个孤零零 '.*' 按钮)
        h('button', {
          type: 'button', className: 'op-chip op-affix', 'data-on': useRegex ? '1' : '0',
          title: '正则模式:按正则匹配命令/响应/工具名(非法表达式自动回退子串)',
          onClick: () => setUseRegex(!useRegex),
          style: {
            position: 'absolute', right: 4, height: 18, padding: '0 5px', borderRadius: 4,
            appearance: 'none', cursor: 'pointer', fontSize: 10, lineHeight: 1,
            border: '1px solid var(--dsw-alias-border-l2, #e0e0e0)',
            fontFamily: 'Consolas, Menlo, monospace',
            display: 'inline-flex', alignItems: 'center',
          },
        }, '.*'),
      )
      const toolbar = h('div', {
        style: {
          borderBottom: '1px solid var(--dsw-alias-border-l2, #e6e6e6)',
          paddingTop: 6, paddingBottom: 8,
        },
      },
        h('div', { style: { ...toolbarBar, flexWrap: 'wrap', rowGap: 6, paddingBottom: 5 } },
          h('span', {
            className: 'op-stat',
            title: (!connected ? 'Host 未响应' : (stats && stats.exists === false) ? '已连接,等待日志文件出现' : '已连接 Host 尾随')
              + (legend.length ? '\n' + legend.map((d) => d.label + ' ' + d.n).join('   ') : ''),
            style: {
              display: 'inline-flex', alignItems: 'center', gap: 8, height: 26,
              padding: '0 10px', borderRadius: 999, flex: '0 0 auto',
              border: '1px solid var(--dsw-alias-border-l2, #e6e6e6)',
              background: 'var(--dsw-alias-bg-layer-3, #fff)',
              fontSize: 12, color: 'var(--dsw-alias-label-secondary, #666)',
              fontVariantNumeric: 'tabular-nums',
            },
          },
            h('span', {
              style: {
                width: 7, height: 7, borderRadius: 999, flex: '0 0 auto',
                background: !connected
                  ? TONE_ERR
                  : (stats && stats.exists === false)
                    ? TONE_WARN
                    : TONE_OK,
              },
            }),
            statsBar(stats),
          ),
          h('span', { style: { flex: '1 1 auto', minWidth: 0 } }),
          h('span', { style: { display: 'inline-flex', flex: '0 0 auto' } },
            seg('流水', view === 'stream', () => toggleView('stream'), '流水视图:每条 TCL 命令/响应一行', true),
            seg('调用', view === 'calls', () => toggleView('calls'), '调用视图:cmd↔rsp 配对成行,log 归入其调用'),
          ),
          btn(paused ? '继续' : '暂停', () => setPaused(!paused), paused,
            '暂停后数据在 Host 侧继续累积,恢复后拉齐', '', paused ? h(PlayGlyph) : h(PauseGlyph)),
          btn(follow && autoSuspended ? '已挂起' : '跟随', () => setFollow(!follow), effectiveFollow,
            follow && autoSuspended ? '翻看历史中已自动挂起,回到底部或再点恢复' : '自动滚动到最新',
            follow && autoSuspended ? 'warn' : '', h(TailGlyph)),
          btn(mcpOn ? '● MCP 开' : '○ MCP 关', toggleMcp, mcpOn,
            mcpAvailable
              ? (mcpOn
                ? '停用 ocdup MCP:新会话不再注册 mcp__ocdup__* 工具(省 token);当前会话工具列表不变'
                : '启用 ocdup MCP:新会话才会注册 mcp__ocdup__* 工具;热生效,无需重启 DSH')
              : 'MCP 开关不可用(pluginManager 服务缺失)',
            mcpAvailable ? (mcpOn ? 'ok' : 'off') : ''),
          portSelect,
        ),
        h('div', { style: { ...toolbarBar, flexWrap: 'wrap', rowGap: 6 } },
          h('span', { style: { display: 'inline-flex', alignItems: 'center', gap: 5, flexWrap: 'wrap', flex: '0 1 auto', minWidth: 0 } },
            // 流水 = 方向通道 chip(点中即过滤,再点取消);调用视图只剩「只看失败」
            // 有意义,无失败时这里就不放东西(空着比写「暂无失败」安静)
            view === 'stream'
              ? legend.map((d) => chipBtn(d.label, dir === d.key, () => setDir(dir === d.key ? 'ALL' : d.key), d.n, d.color))
              : (errCount > 0
                  ? chipBtn('只看失败', dir === 'err', () => setDir(dir === 'err' ? 'ALL' : 'err'), errCount, TONE_ERR)
                  : null),
          ),
          // flex-basis 必须给具体值:输入框 width:100% 时 basis:auto 会算成整行宽,
          // 换行判定于是把它单甩到第二行(实测 333+548 > 548)
          h('span', { style: { display: 'inline-flex', alignItems: 'center', gap: 6, flex: '1 1 180px', minWidth: 130, marginLeft: 'auto' } },
            searchField,
          ),
        ),
      )

      // 工具 chips 行(频次降序):单行横滚 + 右缘渐隐——旧版 11 枚 chip 换行成
      // 3~4 行,吃掉半个屏幕;滚轮在这条带上转成横向滚动。
      // 清空/导出也挪到这一行:第二行留给「筛什么」(方向 chip + 搜索),不再挤
      const stripWheel = (e) => {
        const el = e.currentTarget
        if (el && el.scrollWidth > el.clientWidth) el.scrollLeft += e.deltaY
      }
      const chipsRow = entries.length > 0 ? h('div', {
        style: {
          display: 'flex', alignItems: 'center', gap: 8, minWidth: 0,
          padding: '0 12px 8px', borderBottom: '1px solid var(--dsw-alias-border-l2, #e6e6e6)',
        },
      },
        tick('工具'),
        h('div', {
          className: 'op-strip', onWheel: stripWheel,
          style: {
            flex: '1 1 auto', minWidth: 0, display: 'flex', alignItems: 'center', gap: 5,
            overflowX: 'auto', overflowY: 'hidden', paddingRight: 18, paddingBottom: 2,
          },
        },
          chipBtn('全部', tool === 'ALL', () => setTool('ALL'), chips.reduce((a, c) => a + c.n, 0)),
          chipList.map((c) => chipBtn(c.name, tool === c.name, () => setTool(tool === c.name ? 'ALL' : c.name), c.n)),
        ),
        btn(confirmClear ? '确认清空' : '清空', clear, confirmClear, '截断共享 mcp-traffic.jsonl(再点一次生效)', confirmClear ? 'danger' : ''),
        btn('导出', () => downloadJsonl(exportForDownload(shown)), false, '下载当前过滤视图为 JSONL(整个内存环用 Host /export 路由)'),
        h('span', {
          title: counterTip,
          style: {
            flex: '0 0 auto', fontSize: 11, fontVariantNumeric: 'tabular-nums',
            color: 'var(--dsw-alias-label-tertiary, #767676)',
          },
        }, counterText),
      ) : null

      // 烧录进度条:最近一次 flash_* 调用仍是最新动态时显示
      const flashBar = flashShow ? h('div', {
        style: {
          display: 'flex', alignItems: 'center', gap: 8, padding: '5px 12px',
          borderBottom: '1px solid var(--dsw-alias-border-l2, #eee)', fontSize: 11,
          color: 'var(--dsw-alias-label-secondary, #666)',
        },
      },
        h('span', { style: { flex: '0 0 auto', fontWeight: 700, color: 'var(--dsw-alias-label-primary, #222)' } }, flashCall.tool),
        (() => {
          const fb = flashBarText(flashCall, flashProg)
          return h('span', { style: { display: 'inline-flex', alignItems: 'center', gap: 6, flex: 1, minWidth: 0 } },
            h('span', {
              style: {
                flex: 1, minWidth: 40, maxWidth: 220, height: 4, borderRadius: 2,
                background: 'var(--dsw-alias-border-l2, #eee)', overflow: 'hidden',
              },
            },
              h('span', {
                className: fb.active ? 'op-flow' : '',
                style: { display: 'block', width: '100%', height: '100%', background: fb.tone },
              })),
            h('span', { style: { flex: '0 1 auto', whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' } }, fb.text),
          )
        })(),
      ) : null

      // 链路状态区(v1.2.4 起):「规格读数条」——探针/芯片/Flash 三枚方角读数卡,
      // 从历史流量解析后常驻;点各段跳到来源;缺的段给「复制提问」占位卡。
      // v1.3.2 层级翻转:读数(值是主文本、加粗、tabular)才是主角,段名(探针/芯片/
      // Flash)退成弱标签,段色只留在左色条 + 图标——旧版把段名做成彩色粗体、
      // 值做成灰字,读起来是「探针 探针 CMSIS-DAP」这种本末倒置
      const link = React.useMemo(() => deriveLinkInfo(entries), [entries])
      const linkSeg = (icon, label, value, title, onClick, grow, tone, labelClass) => h('button', {
        type: 'button', className: 'op-link' + (grow ? ' op-link-grow' : ''), onClick, title,
        style: {
          appearance: 'none', cursor: 'pointer', fontSize: 11,
          display: 'inline-flex', alignItems: 'center', gap: 6, maxWidth: '100%', minWidth: 0,
          height: 26, padding: '0 8px', borderRadius: 4, font: 'inherit', whiteSpace: 'nowrap',
          border: '1px solid var(--dsw-alias-border-l2, #e6e6e6)',
          borderLeft: tone ? '2px solid ' + tone : undefined,
          background: 'var(--dsw-alias-bg-layer-3, #fff)',
          color: 'var(--dsw-alias-label-secondary, #666)',
          ...(grow ? {} : { flex: '0 0 auto' }),
        },
      },
        icon
          ? h('span', { style: { display: 'inline-flex', flex: '0 0 auto', color: tone || 'var(--dsw-alias-label-tertiary, #999)' } }, icon)
          : null,
        h('span', { style: { flex: '0 0 auto', color: 'var(--dsw-alias-label-tertiary, #767676)' }, className: labelClass || undefined }, label),
        h('span', {
          style: {
            minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis',
            display: 'inline-flex', alignItems: 'center', gap: 4,
            color: 'var(--dsw-alias-label-primary, #222)', fontWeight: 600,
            fontVariantNumeric: 'tabular-nums',
          },
        }, value))
      const chipSeg = link.chip ? chipLabel(link.chip) : null

      // ---- v1.3.4/v1.3.5 后端段:断开 / 恢复 ----
      // 状态机:stopped / running / null(没信息)。烧录进行中必须先确认:
      // 断开会中断 AI 的烧录。v1.3.5:结论只显示一个词(backendVerdict),
      // 完整文案进 tooltip + 流水镜像——卡片里铺长句会把链路行撑爆。
      const backendState = backendStateOf(entries)
      const backendRunning = backendState !== 'stopped'
      const backendAct = async (action) => {
        setBackendBusy(true)
        try {
          // v1.4.0：同 SwitchTargetBtn——客户端超时兜底(Host 的 abort 不一定被认)
          const ac = new AbortController()
          const to = setTimeout(() => ac.abort(), 15000)
          let r
          try {
            r = await fetch(ROUTE_BACKEND, {
              method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ action }),
              signal: ac.signal,
            })
          } finally { clearTimeout(to) }
          const j = await r.json()
          const ok = !!(j && j.ok)
          const text = ok ? (j.text || '') : ('失败:' + ((j && j.error) || ('HTTP ' + r.status)))
          setBackendMsg({ action, ok, text })
        } catch (e) {
          setBackendMsg({ action, ok: false, text: '失败:' + (e && e.message ? e.message : String(e)) })
        } finally {
          setBackendBusy(false)
          if (backendTimerRef.current) clearTimeout(backendTimerRef.current)
          backendTimerRef.current = setTimeout(() => setBackendMsg(null), 6000)
        }
      }
      const onBackendClick = () => {
        if (backendBusy) return
        if (!backendRunning) { backendAct('connect'); return }
        // 断开:烧录进行中要二次确认(3s 窗口),其余一键
        if (flashCall && flashCall.state === 'open' && !confirmBackend) {
          setConfirmBackend(true)
          if (backendTimerRef.current) clearTimeout(backendTimerRef.current)
          backendTimerRef.current = setTimeout(() => setConfirmBackend(false), 3000)
          return
        }
        setConfirmBackend(false)
        backendAct('disconnect')
      }
      const VERDICT_TONE = { ok: TONE_OK, warn: TONE_WARN, err: TONE_ERR }
      const verdict = backendMsg ? backendVerdict(backendMsg.action, backendMsg.ok, backendMsg.text) : null
      const backendValue = verdict
        ? [
          h('span', { key: 'd', style: { width: 6, height: 6, borderRadius: 999, flex: '0 0 auto', background: VERDICT_TONE[verdict.tone] } }),
          h('span', { key: 't', style: { color: VERDICT_TONE[verdict.tone], fontWeight: 700 } }, verdict.label),
        ]
        : confirmBackend
          ? [h('span', { key: 'c', style: { color: TONE_ERR, fontWeight: 700 } }, '确认断开?')]
          : [
            h('span', {
              key: 'd',
              style: {
                width: 6, height: 6, borderRadius: 999, flex: '0 0 auto',
                background: backendState === null
                  ? 'var(--dsw-alias-label-tertiary, #767676)'
                  : backendRunning ? TONE_OK : 'var(--dsw-alias-label-tertiary, #767676)',
              },
            }),
            h('span', { key: 't' }, backendBusy ? '处理中…' : backendState === null ? '未知' : backendRunning ? '已连接' : '已断开'),
          ]
      const backendTip = (verdict ? '上一步:' + backendMsg.text + '\n\n' : '')
        + (backendRunning
          ? '后端 openocd(命令端口由 ocd.json 决定)\n点击:断开并释放探针——别的 agent 软件(Claude Code / ZCode…)就能用\n'
            + 'MCP 门面不断,之后 connect 或任何硬件调用会自动拉回来'
            + (flashCall && flashCall.state === 'open' ? '\n⚠ 有烧录正在进行,断开会中断它(需再点一次确认)' : '')
          : backendState === null
            ? '还没有从流水里读到过后端事件(连接/断开都会留一行)\n点击:断开(本来就没跑时会如实说)或恢复'
            : '后端已断开(探针已释放)\n点击:重新 connect 拉回后端')
      // 中性左色条:前三段是数据通道色,这一段是面板控的开关,不抢通道色。
      // 段名带 .op-seg-label:≤560px 隐藏(窄停留下四张卡靠它省 28px 排进两行)
      const backendSeg = linkSeg(h(PowerGlyph), '后端', backendValue, backendTip,
        onBackendClick, false, confirmBackend ? TONE_ERR : 'var(--dsw-alias-border-l3, #cacaca)', 'op-seg-label')

      // v1.3.7:链路行**常驻**——不再要求 probe/chip/flash 至少有一项。空态
      // (重启后还没流量、或刚清空)时这一行只剩「后端」开关,而那恰恰是最该在
      // 手边的时候:探针要不要让给别的 agent 软件,不必等 AI 先跑一轮。
      const linkRow = h('div', {
        // 布局(display/gap/align/换行)全在 .op-linkrow class 里:内联 style 会压过
        // 容器查询的 grid 覆盖(窄停靠 2×2),这里只留与布局无关的皮相
        className: 'op-linkrow',
        style: {
          padding: '8px 12px', borderBottom: '1px solid var(--dsw-alias-border-l2, #e6e6e6)',
        },
      },
        tick('链路', 'op-tick'),
        // 「后端」排在第一:它是这一行唯一能动手的开关(前置=最易点),后面三张
        // 是「板子上有什么」的数据读数;窄停留下也正好两行排下(实测 340px:
        // 后端104+探针160 一行 / 芯片157+Flash138 一行)
        backendSeg,
        // 一条链路读数都没有时(重启后还没流量 / 刚清空)只留「后端」开关:
        // 三张占位卡说的是「日志里缺这项」,而空态是「还没有日志」——摆三张
        // 「未识别 · 复制提问」只是添乱,后端开关才是这时候真有用的东西
        (link.probe || link.chip || link.flash || link.mismatch)
          ? [
            link.probe ? linkSeg(h(ProbeGlyph), '探针', probeLabel(link.probe),
              (link.probe.pinned
                ? 'interface: ' + link.probe.cfg + '(固定配置,未自动识别物理型号——auto 模式才会认 USB 探针)'
                : 'probe auto-detected: ' + link.probe.kind + ' -> ' + link.probe.cfg
                + (link.probe.evidence ? '(evidence: ' + link.probe.evidence + ')' : ''))
              + '\n点击:搜索 ' + (link.probe.pinned ? 'interface:' : 'probe auto-detected') + ' 定位这条记录',
              () => setSearch(link.probe.pinned ? 'interface:' : 'probe auto-detected'), false, LINK_TONE.probe)
              : h(LinkPromptBtn, { label: '探针', tone: LINK_TONE.probe }),
            link.chip ? linkSeg(h(ChipGlyph, { size: 12 }), '芯片',
              chipSeg
                ? [
                  h('span', { key: 'h' }, chipSeg.head),
                  // 补充读数(内核 / dev id)与「型号未识别」都在窄面板整段隐藏:
                  // 半词截断比少一行更难读
                  chipSeg.tail ? h('span', {
                    key: 't', className: 'op-chip-core',
                    style: { fontWeight: 400, color: 'var(--dsw-alias-label-tertiary, #767676)' },
                  }, '· ' + chipSeg.tail) : null,
                  chipSeg.identified ? null : h('span', {
                    key: 'u', className: 'op-chip-core',
                    style: { fontWeight: 400, color: TONE_WARN },
                  }, '型号未识别'),
                ]
                : chipFallbackLabel(link.chip),
              chipTip(link.chip),
              // 型号未识别时图标转警示色:窄面板看不到文字也能一眼看出「这片只认到内核」
              () => { setView('calls'); setTool('identify_chip') }, true,
              chipSeg && chipSeg.identified === false ? TONE_WARN : LINK_TONE.chip)
              : h(LinkPromptBtn, { label: '芯片', tone: LINK_TONE.chip }),
            link.flash ? linkSeg(h(FlashGlyph), 'Flash', fmtKb(link.flash.total),
              'flash_info:' + link.flash.banks.map((b) => '\nbank ' + b.bank + ' @ ' + b.base + ' size ' + fmtSize(b.size)).join('')
              + (link.flash.banks.length > 1 ? '\n合计: ' + fmtSize(link.flash.total) : '')
              + '\n点击:调用视图只看 flash_info',
              () => { setView('calls'); setTool('flash_info') }, false, LINK_TONE.flash)
              : h(LinkPromptBtn, { label: 'Flash', tone: LINK_TONE.flash }),
            // v1.3.0:target 失配 → 切换胶囊;v1.3.8 起点了直接切(见组件注释)
            link.mismatch ? h(SwitchTargetBtn, {
              mismatch: link.mismatch,
              flashOpen: !!(flashCall && flashCall.state === 'open'),
              onSwitched: () => backendAct('connect'),
            }) : null,
          ]
          : null,
      )

      // 空态:API 地址(agent 可直接 curl)+ 链路状态;
      // 「有条目但被过滤空」单独一支(排除折叠导致的情况)
      const apiFull = (typeof location !== 'undefined' && location.origin ? location.origin : '') + ROUTE
      const emptyBox = (entries.length > 0 && visibleCount === 0)
        ? h('div', { style: { padding: 24, textAlign: 'center', color: 'var(--dsw-alias-label-tertiary, #999)', fontSize: 13 } },
            h('div', null, '当前过滤无匹配条目'),
            h('div', { style: { marginTop: 4, fontSize: 11 } },
              '调整方向 / 工具 / 搜索条件,展开被折叠的会话,或点工具 chips 的「全部」切回'))
        : h('div', {
            style: {
              padding: '28px 24px', textAlign: 'center',
              display: 'flex', flexDirection: 'column', alignItems: 'center', gap: 10,
            },
          },
            h('div', { style: { opacity: 0.45 } }, h(ChipGlyph, { size: 30 })),
            h('div', { style: { fontSize: 13, color: 'var(--dsw-alias-label-secondary, #666)' } },
              mcpAvailable && !mcpOn
                ? 'ocdup MCP 已停用 — 点工具条「MCP 开」启用;启用后新会话才会注册 mcp__ocdup__* 工具'
                : stats && stats.exists
                  ? '日志已连接,等待 AI 调用 ocdup 工具…'
                  : '尚未发现 ' + ((stats && stats.logPath) || 'mcp-traffic.jsonl') + ' — 首次 AI 硬件操作后自动出现'),
            stats && stats.exists
              ? h('div', { style: { fontSize: 11, color: 'var(--dsw-alias-label-tertiary, #999)' } },
                  '让 Agent 调 mcp__ocdup__connect 试试(连接探针后就有流量)')
              : null,
            // v1.3.6:有轮转备份 .1 时,空态给一个人能点的入口(大小 + 最后写入
            // 时间)。轮转后的历史不该只因「当前周期没流量」就从面板上消失
            stats && stats.rotated && stats.rotSize > 0
              ? h('div', {
                  style: {
                    fontSize: 11, color: 'var(--dsw-alias-label-tertiary, #999)',
                    display: 'flex', flexDirection: 'column', alignItems: 'center', gap: 6,
                  },
                },
                  h('div', null, '更早的 ' + fmtSize(stats.rotSize) + ' 已轮转到 .1('
                    + fmtStamp(stats.rotMtimeMs) + ')— 当前周期还没有新流量'),
                  h('button', {
                    type: 'button', onClick: loadRotated,
                    title: '把 .1 的尾部窗口补进面板(Host: POST /api/ocdup-log/rotated);同一份 .1 只会加载一次',
                    style: {
                      appearance: 'none', cursor: 'pointer', fontSize: 11, padding: '3px 10px',
                      borderRadius: 6, border: '1px solid var(--dsw-alias-border-l2, #ddd)',
                      background: 'var(--dsw-alias-bg-layer-3, #fff)',
                      color: 'var(--dsw-alias-label-secondary, #666)', font: 'inherit',
                    },
                  }, '加载 .1 尾部'),
                )
              : null,
            h('div', { style: { fontSize: 11, display: 'inline-flex', alignItems: 'center', gap: 6, padding: '3px 10px', borderRadius: 999, border: '1px solid var(--dsw-alias-border-l2, #eee)', color: 'var(--dsw-alias-label-tertiary, #999)' } },
              'API:',
              h('span', { style: { fontFamily: 'Consolas, Menlo, monospace' } }, apiFull),
              h(CopyBtn, { text: apiFull, label: '复制' })),
            stats && stats.exists && typeof stats.logSize === 'number'
              ? h('div', { style: { fontSize: 11, color: 'var(--dsw-alias-label-tertiary, #999)' } },
                  '日志 ' + fmtBytes(stats.logSize) + 'B · 坏行 ' + (stats.badLines || 0)
                  + ' · .1 ' + (stats.rotated ? fmtSize(stats.rotSize) + ' ' + fmtStamp(stats.rotMtimeMs) : '无'))
              : null,
          )

          // v1.3.1 列头条(两个视图都有):通道表——列宽与各自行类型严格对齐,
      // .op-tool/.op-len/.op-dt/.op-tag 复用同一套容器查询隐藏类,窄屏同步降级
      const colHead = (view === 'stream' && visibleCount > 0) ? h('div', { className: 'op-colhead' },
        h('span', { style: { flex: '0 0 3px', marginRight: 3 } }),
        h('span', { style: { flex: '0 0 84px' } }, '时间'),
        h('span', { style: { flex: '0 0 34px', textAlign: 'center' } }, '方向'),
        h('span', { className: 'op-tag', style: { flex: '0 0 8px' } }),
        h('span', { className: 'op-tool', style: { flex: '0 0 110px' } }, 'tool'),
        h('span', { className: 'op-len', style: { flex: '0 0 44px', textAlign: 'right' } }, 'B'),
        h('span', { className: 'op-dt', style: { flex: '0 0 48px', textAlign: 'right' } }, 'Δt'),
        h('span', { style: { flex: 1, minWidth: 0 } }, '命令 / 响应'),
      ) : (view === 'calls' && visibleCount > 0) ? h('div', { className: 'op-colhead' },
        // 调用视图列头:列宽与 CallRow 对齐(3px 状态条/时间/状态/调用/Δt/摘要)
        h('span', { style: { flex: '0 0 3px', marginRight: 3 } }),
        h('span', { style: { flex: '0 0 84px' } }, '时间'),
        h('span', { style: { flex: '0 0 34px', textAlign: 'center' } }, '状态'),
        h('span', { className: 'op-tool', style: { flex: '0 0 110px' } }, '调用'),
        h('span', { className: 'op-dt', style: { flex: '0 0 48px', textAlign: 'right' } }, 'Δt'),
        h('span', { style: { flex: 1, minWidth: 0 } }, '命令 / openocd 日志 / 响应'),
      ) : null

      const list = h('div', {
        ref: listRef, onScroll,
        style: { flex: 1, minHeight: 0, width: '100%', overflowX: 'hidden', overflowY: 'auto' },
      },
        colHead,
        visibleCount === 0
          ? emptyBox
          : rendered.map((it) => {
              if (it.kind === 'gap') return h(GapRow, { key: 'gap-' + it.seq, count: it.count })
              if (isSessionSep(it)) {
                return h(SessionRow, {
                  key: 's-' + it.seq, e: it,
                  collapsed: collapsed.has(it.seq),
                  count: runs.get(it.seq) || 0,
                  onToggle: () => setCollapsed((prev) => {
                    const next = new Set(prev)
                    if (next.has(it.seq)) next.delete(it.seq)
                    else next.add(it.seq)
                    return next
                  }),
                })
              }
              if (it.kind === 'call') {
                return h(CallRow, {
                  key: 'c-' + it.seq, call: it, matcher,
                  selected: !!(selected && selected.seq === it.seq), onSelect: setSelected,
                })
              }
              return h(Row, {
                key: it.seq, e: it, matcher,
                onPort: (tag) => setPort(port === tag ? 'ALL' : tag),
                selected: !!(selected && selected.seq === it.seq), onSelect: setSelected,
              })
            }),
      )

      // 跟随挂起期间有新数据 → 右下角浮出提示
      const listWrap = h('div', {
        style: { flex: 1, minHeight: 0, position: 'relative', display: 'flex', flexDirection: 'column', background: 'var(--dsw-alias-bg-layer-2, #fff)' },
      },
        list,
        (autoSuspended && newCount > 0) ? h('button', {
          type: 'button', onClick: jumpToLatest, title: '跳到最新并恢复跟随',
          style: {
            position: 'absolute', bottom: 14, right: 14, zIndex: 5,
            appearance: 'none', cursor: 'pointer', border: 'none', borderRadius: 999,
            padding: '6px 12px', fontSize: 12, fontWeight: 600, font: 'inherit',
            color: 'var(--dsw-alias-bg-layer-1, #fff)',
            background: 'var(--dsw-alias-brand-primary, #4f6bed)',
            boxShadow: '0 4px 12px rgba(0, 0, 0, 0.18)',
          },
        }, '↓ ' + newCount + ' 条新数据') : null,
      )

      // ---- 详情区:普通条目 = 全文;调用项 = cmd / openocd 日志 / rsp 三段 ----
      // v1.3.2:段名用小号字重标签(旧版给日志段上琥珀色,和流水的 log=石板灰
      // 通道色打架);标题行不用 '·' 串,靠间距分组、弱化次要字段
      const detailLabel = (text, tone) => h('div', {
        style: {
          fontSize: 10, fontWeight: 600, marginBottom: 3,
          color: tone || 'var(--dsw-alias-label-tertiary, #767676)',
        },
      }, text)
      const detailBody = (text, mark) => h('div', {
        style: {
          fontFamily: 'Consolas, Menlo, monospace', whiteSpace: 'pre-wrap', wordBreak: 'break-all',
          color: mark === 'err' ? TONE_ERR : mark === 'ok' ? TONE_OK : 'var(--dsw-alias-label-primary, #222)',
        },
      }, text || '(空)')
      const metaDim = { color: 'var(--dsw-alias-label-tertiary, #767676)', flex: '0 0 auto' }

      const detail = selected ? h('div', {
        style: {
          flex: '0 0 auto', maxHeight: 220, overflow: 'auto', borderTop: '1px solid var(--dsw-alias-border-l2, #eee)',
          padding: '8px 12px', fontSize: 12, background: 'var(--dsw-alias-bg-layer-3, #fafafa)',
        },
      },
        h('div', { style: { marginBottom: 6, display: 'flex', alignItems: 'center', gap: 8, minWidth: 0, color: 'var(--dsw-alias-label-secondary, #666)' } },
          h('span', {
            style: {
              flex: '1 1 auto', minWidth: 0, display: 'inline-flex', alignItems: 'center', gap: 8,
              overflow: 'hidden', whiteSpace: 'nowrap', fontVariantNumeric: 'tabular-nums',
            },
          },
            h('span', { style: { ...metaDim, fontVariantNumeric: 'tabular-nums' } }, '#' + selected.seq),
            selected.kind === 'call'
              ? [
                h('span', { key: 't', style: { fontWeight: 600, color: 'var(--dsw-alias-label-primary, #222)' } }, selected.tool),
                h('span', { key: 's', style: { color: (CALL_GLYPH[selected.state] || CALL_GLYPH.open).color, fontWeight: 600 } },
                  (CALL_GLYPH[selected.state] || CALL_GLYPH.open).g + ' '
                  + (selected.state === 'ok' ? '完成' : selected.state === 'err' ? '失败' : '进行中')),
                typeof selected.ts === 'string' && selected.ts.length >= 23
                  ? h('span', { key: 'ts', style: metaDim }, shortTime(selected.ts))
                  : null,
                callDtMs(selected) != null ? h('span', { key: 'dt', style: metaDim }, 'Δ' + fmtDt(callDtMs(selected))) : null,
                selected.logs.length ? h('span', { key: 'lg', style: metaDim }, selected.logs.length + ' 条日志') : null,
              ]
              : [
                h('span', { key: 't', style: { fontWeight: 600, color: 'var(--dsw-alias-label-primary, #222)' } }, selected.tool || '—'),
                h('span', { key: 'd', style: { color: DIR_COLOR[selected.dir] || 'inherit', fontWeight: 600 } }, DIR_LABEL[selected.dir] || selected.dir),
                typeof selected.ts === 'string' && selected.ts.length >= 23 ? h('span', { key: 'ts', style: metaDim }, shortTime(selected.ts)) : null,
                h('span', { key: 'ln', style: metaDim }, selected.len + ' B'),
                selected.dtMs != null ? h('span', { key: 'dt', style: metaDim }, 'Δ' + fmtDt(selected.dtMs)) : null,
                selected.tag ? h('span', { key: 'tg', style: metaDim }, selected.tag) : null,
              ],
          ),
          selected.kind === 'call'
            ? h(CopyBtn, { text: selected.cmd + (selected.rsp ? '\n' + selected.rsp : ''), label: '复制' })
            : (selected.text ? h(CopyBtn, { text: selected.text, label: '复制' }) : null),
        ),
        selected.kind === 'call'
          ? h('div', { style: { display: 'flex', flexDirection: 'column', gap: 8 } },
              h('div', null, detailLabel('命令'), detailBody(selected.cmd)),
              selected.logs.length ? h('div', null,
                detailLabel('openocd 日志 ' + selected.logs.length + ' 条', DIR_COLOR.log),
                h('div', {
                  style: {
                    fontFamily: 'Consolas, Menlo, monospace', whiteSpace: 'pre-wrap', wordBreak: 'break-all',
                    maxHeight: 120, overflow: 'auto', color: 'var(--dsw-alias-label-secondary, #666)', fontSize: 11,
                  },
                }, selected.logs.join('\n'))) : null,
              h('div', null,
                detailLabel(selected.state === 'err' ? '错误' : '响应', selected.state === 'err' ? TONE_ERR : DIR_COLOR.rsp),
                detailBody(selected.rsp,
                  selected.state === 'err' ? 'err' : rowMark({ dir: 'rsp', text: selected.rsp || '' })),
              ))
          : h('div', { style: { display: 'flex', flexDirection: 'column', gap: 4 } },
              detailBody(selected.text, rowMark(selected))),
      ) : null

      // ---- 快捷键(仅面板持有焦点时) ----
      const isFormField = (t) => {
        const tag = t && t.tagName
        return tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT' || (t && t.isContentEditable)
      }
      const onKeyDown = (e) => {
        if (isFormField(e.target) || e.target.tagName === 'BUTTON') return
        if (e.key === ' ') { e.preventDefault(); setPaused(!pausedRef.current) }
        else if (e.key === '/') { e.preventDefault(); if (searchRef.current) searchRef.current.focus() }
        else if (e.key === 'Escape') setSelected(null)
        else if (e.key === 'ArrowUp' || e.key === 'ArrowDown') {
          if (rendered.length === 0) return
          e.preventDefault()
          const idx = selected ? rendered.findIndex((it) => it.seq === selected.seq) : -1
          let next
          if (e.key === 'ArrowUp') next = idx <= 0 ? rendered.length - 1 : idx - 1
          else next = (idx < 0 || idx >= rendered.length - 1) ? 0 : idx + 1
          setSelected(rendered[next])
        }
        else if (e.key === 'Enter' && selected) {
          e.preventDefault()
          const text = selected.kind === 'call'
            ? selected.cmd + (selected.rsp ? '\n' + selected.rsp : '')
            : (selected.text || '')
          if (text) copyText(text)
        }
      }
      const onMouseDown = (e) => {
        if (isFormField(e.target) || e.target.tagName === 'BUTTON') return
        if (rootRef.current) rootRef.current.focus()
      }

      // v1.3.4:面板可见且未暂停未挂起 = 正在实时看(左图标与未读徽章已随入口改版移除)

      // v1.3.7:加载 .1 的反馈备注独立一行(6s 自清)。放在空态里的话,条目一
      // 加载上来空态整块消失,备注闪一下就没了;独立成行则空态/列表都看得见
      const rotNoteBar = rotNote ? h('div', {
        style: {
          flex: '0 0 auto', fontSize: 11, padding: '4px 12px',
          color: 'var(--dsw-alias-label-tertiary, #767676)',
          borderBottom: '1px solid var(--dsw-alias-border-l2, #e6e6e6)',
          background: 'var(--dsw-alias-bg-layer-3, #fff)',
        },
      }, rotNote) : null

      return h('div', {
        ref: rootRef, tabIndex: -1, onKeyDown, onMouseDown,
        style: {
          outline: 'none',
          width: '100%', maxWidth: '100%', minWidth: 0, overflow: 'hidden',
          containerType: 'inline-size', containerName: 'op-panel',
          height: '100%', boxSizing: 'border-box', display: 'flex', flexDirection: 'column',
          background: 'var(--dsw-alias-bg-layer-2, #fff)',
          color: 'var(--dsw-alias-label-primary, #222)',
        },
      },
        toolbar, linkRow, chipsRow, flashBar, rotNoteBar, listWrap, detail,
      )
    }

    return {
      // v1.3.3:只要有 slots 就够(不再依赖 sidebarRight/sidebarRightTabs——
      // 那两个服务在新宿主仍在,但侧栏图标已经不归它们管了)
      // v1.3.4:右栏 tab 路线回归(用户要右侧停靠),但入口不再押注左侧图标:
      // inject 需要 slots + 两个右栏服务;左侧 panellist 注册整体移除。
      //
      // v1.5.3 实测纠错(原计划「inject 只留 slots」已回退):
      // 本宿主的 `ctx.get(name)` 走 ReflectService.get(name, strict=true),而
      // 服务解析要求该名字**已在 inject 里声明**——未声明的名字在代理层直接
      // 抛 "cannot get property ... without inject"。所以把两个右栏服务从
      // inject 移除并不会让它们变成「可选」,而是让 apply 里那两行 ctx.get()
      // 立刻抛错 → hasRightbar=false → 插件滑进 main 全局面板兜底分支。
      // 浏览器实测:右栏 guide 里有 acccom-serial(files/git/terminal/…),
      // 唯独没有 OCDUP——就是这条。同宿主的 dsh-serial-panel 也保持三项全声明。
      // 想真正做「可选」必须把解析包进 try/catch 或 ctx.inject([...], cb) 的
      // 回调作用域,那是独立改动,不与本次 bugfix 混做。
      inject: ['slots', 'sidebarRight', 'sidebarRightTabs'],
      // 纯函数仅供 test.js client 模式断言,生产逻辑不依赖
      _test: {
        highlight, highlightRe, compileMatcher, matcherHit, hueOf, fmtDt, sameStats,
        parseFlashProgress, flashProgressOf, flashProgressOfCall, pairCalls, callDtMs, isSessionSep,
        parseResultTags, lastResultTag, verdictToneOf,
        sessionRuns, applyCollapse, toolOptions, rowMark, lastFlashCall, flashStillLatest, isFlashTool,
        fmtSize, parseProbeNote, parseChipIdentity, parseFlashInfo, deriveLinkInfo, baseName,
        friendlyProbeName, probeLabel, isBogusDev, chipLabel, chipFallbackLabel, chipTip, fmtKb, coreShort,
        parseTargetMismatch, dirLegend, counterLabel, backendStateOf, backendVerdict,
        fmtStamp, fetchWithTimeout, POLL_FETCH_TIMEOUT_MS,
      },
      apply: function (ctx) {
        const slots = ctx.get('slots')
        if (slots === undefined) return
        ctxRef = ctx

        // v1.3.4:右侧停靠栏 tab(用户要的形态)+ 左侧图标整体移除。
        // 契约(读宿主 app.asar 的 ui-sidebar-terminal / ui-sidebar-browser 确认):
        //   * ctx.sidebarRightTabs.register({ id, kind, multiple, priority, title,
        //     keepMounted, guide:[{id,order,title,description,icon}] }) 声明 tab 类型;
        //     guide 项**不需要** commandId(terminal 就没挂)——右栏 guide 卡点一下即开
        //   * body 注册在 sidebar.right.pane.tab,**key = 上面同一个 id**
        //   * openTab 认的是 **kind**(id 与 kind 可以不同名:浏览器 id=包名/kind='browser')
        // 宿主没有右栏服务(老版本/缺服务)→ 退回 main 全局面板(v1.3.3 形态),
        // 保证任何宿主下都至少有一个入口能把面板开出来。
        const rightTabs = ctx.get('sidebarRightTabs')
        const right = ctx.get('sidebarRight')
        const hasRightbar = !!(rightTabs && typeof rightTabs.register === 'function'
          && right && typeof right.openTab === 'function')

        if (hasRightbar) {
          ctx.effect(() => rightTabs.register({
            id: 'dsh-ocdup-panel',
            kind: 'ocdup-log',
            multiple: false,            // 单例:一个调试面板够用
            // v1.5.3:档位只认 extension / builtin / fallback(宿主 RANKS 表)。
            // 此前写 'plugin' 不在表内 → RANKS['plugin'] === undefined,排名比较得 NaN;
            // 当前靠 openTab(kind) 按 kind 直开、不参与排名才没炸。插件贡献的类型
            // 就是 extension(也是宿主 DEFAULT_BAND),显式写出来免得再退化。
            priority: 'extension',
            title: () => 'OCDUP 调试',
            keepMounted: true,          // 轮询跨切 tab / 收起到浮动 / 切会话都活着
            guide: [{
              id: 'new', order: 30,
              title: () => 'OCDUP 调试',
              description: () => 'OpenOCD 命令/响应实时面板',
              icon: ChipGlyph,
            }],
          }), 'dsh-ocdup-panel: tab type')

          ctx.effect(() => slots.inject('sidebar.right.pane.tab', () => slots.register(
            { name: 'sidebar.right.pane.tab', key: 'dsh-ocdup-panel' },
            (props) => h(OcdupPanel, props),
          )), 'dsh-ocdup-panel: pane body')
        } else {
          // 兜底:宿主没有右栏 tab 服务 → 主视图区全局面板
          slots.inject('main', () => slots.register(
            { name: 'main', key: 'ocdup-log' },
            (props) => h(OcdupPanel, props),
          ))
        }
      },
    }
  },
})
