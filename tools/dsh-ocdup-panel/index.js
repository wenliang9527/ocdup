'use strict'
// ============================================================
//  dsh-ocdup-panel — Host 半区(永久 cordis 插件)  v1.4.0
//
//  v1.4.0:对照 ocdup v0.4.30 全项目复查(面板侧 3×P1 + 8×P2,断言 210→234),
//  Host 半区收 P1-3 与 P2-10/11:
//   * P1-3 轮转判定从「只看活动文件收缩」升级为「.1 换了 + 活动文件自上一拍
//     被重写过(mtime)」:轮转后新文件若在 POLL_MS 内就长得比 offset 还大
//     (硬盘/卷起),收缩永远不触发——表现为秒不换部(白屏),又把新文件前
//     offset 字节整段跳过(丢头部);信号用 last-write mtime(NTFS 隧道效应
//     会让改名后同名重建的文件继承旧 birthtime,信号失效);
//   * P2-11 入口归一:text/tool 入口校验并归一成字符串(safeText 兜底序列化,
//     JSON.stringify 对 BigInt 会抛)——非字符串透传曾让客户端
//     e.text.length / e.tool.toLowerCase() 直接炸;
//   * P2-10 consume 容错:JSON.parse('null') 也「成功」,非对象记录按坏行计——
//     此前对 null 取属性抛异常且 offset 已推进,吞掉该批后续所有行。
//
//  v1.3.8:新增 POST /api/ocdup-target(target 失配一键切换)——identify 认出
//  的家族与当前 target_cfg 不符时,面板胶囊从「复制 set_target 命令给会话」
//  升级为经 ctx.tools.execute 直调 mcp__ocdup__set_target(用户实测场景:403
//  板插着、cfg 钉在 423 钥匙板上 → DEV_ID 读 0 → flash probe 断言崩 openocd,
//  表现为「连上就自己断开」;换对 cfg 后双 bank 1MB 全通)。
//
//  v1.3.7:清空路由补重置 rotLoaded——「加载过 .1 → 清空 → 再点加载」不该被
//  already loaded 挡住(环里其实已经空了);其余路由契约不变。
//
//  v1.3.6:轮转不再丢视野 —— 尾随发现活动文件消失/收缩且 .1 刚换过时,先把
//  .1 的尾部窗口接进内存环再继续跟新文件(清空路由自己 reset,.1 没动,不会
//  误判成轮转);新增 POST /api/ocdup-log/rotated 按需补载;stats 增
//  rotSize/rotMtimeMs。
//
//  v1.3.5:纯 Client 侧界面修正(后端段结论压缩 + 链路行 2×2 网格),数据层与
//  路由契约无变化;仅版本号随插件整体 bump。
//
//  v1.3.4:新增 POST /api/ocdup-backend(断开/恢复后端)——inject 增加 'tools',
//  经 ctx.tools.execute 调 mcp__ocdup__disconnect / mcp__ocdup__connect
//  (MCP 工具在 Host 侧就是普通工具;10s 超时 + AbortController)。
//  纯增量:数据层无变化,尾随/路由契约不变。
//
//  v1.3.3:修「侧边栏图标点了没反应」——注册契约对齐宿主 0.2.0-rc.2
//  (panellist id 与 main key 严格同值、inject 只留 slots)。纯 Client 侧,
//  数据层无变化,Host 继续哑管道;仅版本号随插件整体 bump。
//
//  v1.3.2:界面精修(按钮/文字/配色:控件不折行、engaged 墨色实底、方向通道
//  chip 取代下拉、工具条单行横滚、链路读数条层级翻转)——纯 Client 侧,
//  数据层无变化,Host 继续哑管道;仅版本号随插件整体 bump。
//
//  v1.3.1:Flash 段报 bank 合计 + 工具栏分组/图例/调用列头条(纯 Client 侧,
//  数据层无变化,Host 继续哑管道);仅版本号随插件整体 bump。
//
//  v1.3.0:配套 ocdup v0.4.0(target 注册表 + set_target 工具):数据层仍无变化
// ——失配行走的是同一条 rsp 文本镜像,Host 继续哑管道;仅版本号随插件整体 bump。
//
//  v1.2.5:芯片段「只信实测」+ 链路三段语义色(纯 Client 侧;数据层无变化,
//  Host 继续哑管道;仅版本号随插件整体 bump)。
//
//  v1.2.4:「台架仪器」界面改版(工具栏两行化/链路规格读数条/列表列头条/
//  控件统一 28px+6px 圆角/tabular 数字)全部在 Client 侧;数据层无变化,
//  Host 继续哑管道;仅版本号随插件整体 bump。
//
//  v1.2.3:界面美化 + 链路三段显示修正全部在 Client 侧;数据层无变化
//  (identify_chip 的新 'model (from target_cfg …)' 行走的是同一条 rsp
//  文本镜像),Host 继续哑管道;仅版本号随插件整体 bump。
//
//  v1.2.2:配套 ocdup v0.3.16(connect 自动芯片识别):数据层无变化,
//  connect 的 'chip identity:' 块由 IdentifyTools.TryIdentifyAsync 镜像,
//  Host 继续哑管道;仅版本号随插件整体 bump。
//
//  v1.2.1:链路状态区同样在 Client 侧派生(deriveLinkInfo 扫原始条目),
//  Host 继续做哑管道;仅版本号随插件整体 bump。
//
//  v1.2.0:数据语义不变(调用配对/烧录进度/会话分组全部在 Client 侧从
//  现有条目派生,Host 保持哑管道);仅版本号随插件整体 bump。
//
//  v1.1.0:direction 新增 log(openocd 异步 stdout/stderr 镜像,ocdup 侧
//  OpenOcdLauncher.PumpAsync 逐行 Record 进 JSONL);stats 相应加 log 计数。
//
//  职责:
//   1. 尾随 %LOCALAPPDATA%\Ocdup\mcp-traffic.jsonl —— ocdup MCP 服务器的
//      TrafficLog 把 AI 每条 OpenOCD 交互镜像到这个共享 JSONL(逐条落盘,
//      超 2MB 轮转到 .1;面板清空 = 截断,ocdup 侧检测收缩会自动重开)。
//      v1.3.6:轮转不再丢视野 —— 活动文件收缩且 .1 刚换过时,先把 .1 的尾部
//      窗口接进内存环再继续跟新文件(清空路由自己 reset,不会误判成轮转);
//      POST /api/ocdup-log/rotated 按需把 .1 尾部补进环(空态「加载」按钮),
//      重启后的历史不会只因轮转就从面板上消失。
//      行结构与 acccom 的 mcp-traffic.jsonl 同一套,本文件几乎是
//      dsh-serial-panel Host 半区的换数据源移植。
//   2. 经 webServer.register 挂同源私有路由 /api/ocdup-log,把增量行喂给
//      浏览器面板(handler(req,res) Node 风格,照抄 dsh-serial-panel)。
//
//  行格式(TrafficRecord, camelCase,与 acccom 同构):
//    { id, tool, timestamp, direction: 'cmd'|'rsp'|'SYS'|'err', rawHex, text, portTag }
//    tool = 发起的 MCP 工具名(flash_write/read_memory/…),'backend' = 生命周期事件
//
//  与串口版的差异:
//   * direction 归一化为小写 cmd/rsp/sys/err(写入端大小写混合,SYS 是事件)
//   * stats 按方向计数(cmd/rsp/sys/err + 总字节),没有 TX/RX 之分
//   * 无 shell 解析诊断(串口版特有);/help 返回内联 markdown(agent 自发现)
//   * 环境变量前缀 DSH_OCDUP_PANEL_*
// ============================================================

const fs = require('fs')
const path = require('path')

const ROUTE = '/api/ocdup-log'
const ROUTE_CLEAR = '/api/ocdup-log/clear'
const ROUTE_ROTATED = '/api/ocdup-log/rotated'
const ROUTE_EXPORT = '/api/ocdup-log/export'
const ROUTE_HELP = '/api/ocdup-log/help'
const ROUTE_BACKEND = '/api/ocdup-backend'
const ROUTE_TARGET = '/api/ocdup-target' // v1.3.8:失配胶囊一键切 target_cfg
const ROUTE_MCP = '/api/ocdup-log/mcp'   // v1.4.0:MCP 启停开关(GET 读状态 / POST 切换)

// host 层 MCP 条目 id(住在 profile patch 里,插件面板/本开关共同管理;不属于本插件)
const MCP_ENTRY_ID = 'include:mcp-ocdup'
// 面板动作 → ocdup MCP 工具名(serverName=ocdup → mcp__ocdup__<tool>)
const BACKEND_ACTION_TOOL = { disconnect: 'mcp__ocdup__disconnect', connect: 'mcp__ocdup__connect' }
const TARGET_TOOL = 'mcp__ocdup__set_target' // v1.3.8
const BACKEND_CALL_TIMEOUT_MS = 10000 // stdio 往返 + 杀 openocd + 等端口释放(≤2s)

/** 正整数环境变量读取(非法值回落默认)。 */
function intEnv(name, fallback) {
  const raw = Number(process.env[name])
  return Number.isFinite(raw) && raw > 0 ? Math.floor(raw) : fallback
}

const MAX_ENTRIES = intEnv('DSH_OCDUP_PANEL_MAX_ENTRIES', 5000)           // 内存环上限
const POLL_MS = intEnv('DSH_OCDUP_PANEL_POLL_MS', 400)                    // 尾随轮询间隔
const FIRST_TAIL_BYTES = intEnv('DSH_OCDUP_PANEL_FIRST_TAIL', 256 * 1024) // 首次只读尾部窗口
const MAX_LINE_CHARS = intEnv('DSH_OCDUP_PANEL_MAX_LINE', 1024 * 1024)    // 单行保护
const DEFAULT_FIRST = intEnv('DSH_OCDUP_PANEL_FIRST_LIMIT', 300)          // 首屏返回条数
// 裁剪滞后量:超过 MAX_ENTRIES+TRIM_SLACK 才裁一次,把搬移成本摊薄。
const TRIM_SLACK = MAX_ENTRIES >= 256 ? Math.min(64, MAX_ENTRIES >> 4) : 0

function logPath() {
  const override = (process.env.DSH_OCDUP_PANEL_LOG || '').trim()
  if (override) return override
  const base = (process.env.LOCALAPPDATA || '').trim()
  return path.join(base, 'Ocdup', 'mcp-traffic.jsonl')
}

function jsonResponse(res, status, body) {
  res.writeHead(status, {
    'content-type': 'application/json; charset=utf-8',
    'cache-control': 'no-store',
  })
  res.end(JSON.stringify(body))
}

/** 码点安全截断:普通 slice 可能劈开代理对,JSON 序列化会产出孤立代理。 */
function truncateCodePoints(s, max) {
  const pts = Array.from(s)
  return pts.length > max ? pts.slice(0, max).join('') : s
}

const CSV_COLUMNS = ['seq', 'ts', 'dir', 'tool', 'tag', 'len', 'dtMs', 'text']

function csvCell(v) {
  const s = v == null ? '' : String(v)
  return /[",\r\n]/.test(s) ? '"' + s.replace(/"/g, '""') + '"' : s
}

function toCsv(entries) {
  const rows = entries.map((e) => [
    e.seq, e.ts, e.dir, e.tool, e.tag, e.len, e.dtMs == null ? '' : e.dtMs, e.text,
  ].map(csvCell).join(','))
  // BOM 前缀:让 Excel 识别 UTF-8
  return '\uFEFF' + [CSV_COLUMNS.join(',')].concat(rows).join('\r\n') + (rows.length ? '\r\n' : '')
}

const HELP_MD = [
  '# dsh-ocdup-panel API',
  '',
  'ocdup(OpenOCD MCP 服务器)的流量镜像面板,数据源 `%LOCALAPPDATA%\\Ocdup\\mcp-traffic.jsonl`。',
  '',
  '- `GET /api/ocdup-log?since=<seq>` — 增量条目 + stats;省略 since 返回最近 ' + 300 + ' 条',
  '- `GET /api/ocdup-log?stats=1` — 零条目仅统计(徽章/存活检查)',
  '- `GET /api/ocdup-log?since=<seq>&compact=1&max=<n>` — 单字段 payload 截断(≤4096)',
  '- `POST /api/ocdup-log/clear` — 截断共享日志并重置内存环',
  '- `POST /api/ocdup-log/rotated` — 把 .1(轮转备份)尾部窗口补进内存环并返回新增条目;同一份 .1 重复加载返回空 + note(轮转时的自动续接走同一条读取路径)',
  '- `POST /api/ocdup-backend` body `{action:"disconnect"|"connect"}` — 面板「后端」段按钮:经 Host `ctx.tools.execute` 调 `mcp__ocdup__disconnect` / `mcp__ocdup__connect`(断开=停掉 ocdup 自拉的 openocd 释放探针,MCP 门面不断;只读会话会被 ocdup 拦成 BLOCKED)',
  '- `POST /api/ocdup-target` body `{preset:"AT32F403A"}` — 面板失配胶囊的一键切换:经 Host `ctx.tools.execute` 调 `mcp__ocdup__set_target`(切换=用新 target_cfg 重启 ocdup 自拉的 openocd;preset 名见 ocd.json 的 `target_presets`,传 `"list"` 可列出;只读会话被拦成 BLOCKED)',
  '- `GET /api/ocdup-log/export?format=jsonl|csv` — 导出内存环为附件',
  '- direction:`cmd`(TCL 命令)/`rsp`(响应)/`sys`(后端生命周期)/`err`(通道错误)',
  '- tool:发起的 MCP 工具名(flash_write/read_memory/…),`backend` = 生命周期事件',
].join('\n')

/** 读取 POST 的 JSON body(空体/坏 JSON → 空对象,由调用方校验字段)。 */
function readJsonBody(req) {
  return new Promise((resolve, reject) => {
    const chunks = []
    let size = 0
    req.on('data', (c) => {
      size += c.length
      if (size > 64 * 1024) { reject(new Error('body too large')); req.destroy(); return }
      chunks.push(c)
    })
    req.on('end', () => {
      const raw = Buffer.concat(chunks).toString('utf8').trim()
      if (!raw) { resolve({}); return }
      try { resolve(JSON.parse(raw)) } catch (e) { resolve({}) }
    })
    req.on('error', reject)
  })
}

module.exports = {
  name: 'dsh-ocdup-panel',
  inject: ['webServer', 'tools'],
  apply(ctx) {
    const webServer = ctx.get('webServer')
    if (!webServer || typeof webServer.register !== 'function') return
    // v0.4.6:断开/恢复探针的通道。MCP 工具在 Host 侧就是普通工具(mcp__ocdup__*,
    // dsh-mcp-client 注册在 ctx.tools 上),所以面板按钮不必「复制提问让 AI 调」,
    // 直接走 ctx.tools.execute —— 宿主文档明确这种直接调用只是不受 agent 提醒跟踪。
    const tools = ctx.get('tools')

    // v1.4.0:MCP 启停开关。状态实时读 loader 里对应条目的 disabled(不存偏好,
    // 重启后、外部改动后都永远说真话);切换走 pluginManager.setPluginEnabled
    // ——它改 profile patch 的 disabled 并热 reload,无需重启 DSH。
    const pluginManager = ctx.get('pluginManager')
    const loaderOf = () => ctx.get('loader') || ctx.loader || null
    const mcpState = () => {
      const loader = loaderOf()
      const entry = loader && typeof loader.entries === 'function'
        ? loader.entries().find((e) => e.id === MCP_ENTRY_ID)
        : null
      if (!entry) return { available: false, enabled: null }
      return { available: true, enabled: !entry.disabled }
    }

    const file = logPath()
    let offset = 0          // 已读字节偏移
    let remainder = ''      // 半行残留(写入端按行追加,可能读到半行)
    let firstRead = true    // 是否尚未读过(决定能否启用尾部窗口)
    let seq = 0             // 单调递增序号(不随 clear/轮转重置,客户端 since 依赖它)
    let prevTsMs = null     // 上一条记录的毫秒时间戳(Δt 用;坏时间戳不更新基准)
    let rotSeen = null      // 上一轮轮询时 .1 的 {size,mtimeMs}(变了=刚发生过轮转)
    let rotLoaded = null    // 已经补进环的那份 .1(同一份不重复加载)
    let activeMtimeMs = null // 上一轮轮询时活动文件的 last-write 时间(变了=文件被重写过,v1.4.0)
    const entries = []      // { seq, id, tool, dir, text, tag, ts, len, dtMs },seq 递增
    const stats = { cmd: 0, rsp: 0, sys: 0, err: 0, log: 0, bytes: 0, lastAt: null, exists: false, badLines: 0, logSize: 0, logMtimeMs: null, rotated: false, rotSize: 0, rotMtimeMs: null }

    const reset = () => {
      offset = 0
      remainder = ''
      firstRead = true
      prevTsMs = null
      entries.length = 0
      stats.cmd = 0; stats.rsp = 0; stats.sys = 0; stats.err = 0; stats.log = 0; stats.bytes = 0
      stats.lastAt = null
      stats.badLines = 0
    }

    const payloadLen = (rec) => {
      if (rec.rawHex) {
        const n = String(rec.rawHex).replace(/[^0-9a-fA-F]/g, '').length
        if (n > 0) return Math.floor(n / 2)
      }
      return typeof rec.text === 'string' ? Buffer.byteLength(rec.text, 'utf8') : 0
    }

    // v1.4.0：非字符串字段的安全序列化(JSON.stringify 对 BigInt 会抛,兜底 String())
    const safeText = (v) => {
      try {
        const s = JSON.stringify(v)
        return typeof s === 'string' ? s : String(v)
      } catch (e) {
        try { return String(v) } catch (e2) { return '[unserializable]' }
      }
    }

    const consume = (text) => {
      const data = remainder + text
      const lines = data.split('\n')
      remainder = lines.pop() || ''
      // 写入端若长时间不换行(异常/超大帧),丢弃残留而不是无限增长
      if (remainder.length > MAX_LINE_CHARS) { stats.badLines += 1; remainder = '' }
      for (const line of lines) {
        const s = line.trim()
        if (!s) continue
        let rec
        try { rec = JSON.parse(s) } catch (e) { stats.badLines += 1; continue } // 坏行跳过,不致命
        // v1.4.0：JSON.parse('null') 也「成功」——非对象记录按坏行计。此前
        // rec.direction 对 null 取属性会抛,异常逃出 consume 时 offset 已推进,
        // 该批剩余行永远不再被读(静默丢数据)。
        if (rec === null || typeof rec !== 'object' || Array.isArray(rec)) { stats.badLines += 1; continue }
        seq += 1
        // direction 归一化小写:cmd/rsp/log 小写、SYS 大写,统一后客户端只处理一套
        const low = String(rec.direction || '').toLowerCase()
        const dir = low === 'cmd' ? 'cmd'
          : low === 'rsp' ? 'rsp'
          : low === 'err' ? 'err'
          : low === 'log' ? 'log'
          : 'sys'
        const len = payloadLen(rec)
        // Δt 相对上一条有效时间戳;乱序(多进程追加)钳到 0,坏时间戳记 null
        const tsMs = Date.parse(rec.timestamp || '')
        const dtMs = Number.isFinite(tsMs) && prevTsMs != null ? Math.max(0, tsMs - prevTsMs) : null
        if (Number.isFinite(tsMs)) prevTsMs = tsMs
        // v1.4.0：text/tool 入口归一成字符串——面板自己声明了字段约定(全 string),
        // 非字符串原样透传会让客户端 e.text.length / e.tool.toLowerCase() 直接崩
        const toolText = typeof rec.tool === 'string' ? rec.tool : (rec.tool == null ? '' : safeText(rec.tool))
        const bodyText = typeof rec.text === 'string' ? rec.text : (rec.text == null ? '' : safeText(rec.text))
        entries.push({
          seq,
          id: rec.id,
          tool: toolText,
          dir,
          hex: rec.rawHex || '',
          text: bodyText,
          tag: rec.portTag || '',
          ts: rec.timestamp || '',
          len,
          dtMs,
        })
        stats[dir] += 1
        stats.bytes += len
        stats.lastAt = rec.timestamp || new Date().toISOString()
      }
      // 批量裁剪:一次 splice 顶掉逐行 shift 的搬移成本
      if (entries.length > MAX_ENTRIES + TRIM_SLACK) {
        entries.splice(0, entries.length - MAX_ENTRIES)
      }
    }

    /**
     * v1.3.6:读 .1 的尾部窗口并消费进内存环,返回新增条目(供轮转救援与
     * /rotated 路由共用)。丢窗口首部的半行——与首次挂载同一套语义。
     */
    const ingestRotated = (rot) => {
      const mark = entries.length
      try {
        const from = Math.max(0, rot.size - FIRST_TAIL_BYTES)
        const fd = fs.openSync(file + '.1', 'r')
        try {
          const buf = Buffer.allocUnsafe(rot.size - from)
          const read = fs.readSync(fd, buf, 0, buf.length, from)
          let text = buf.toString('utf8', 0, read)
          // 只有从中段开窗才需要丢半行(from=0 时窗口起点就是行边界,丢了会
          // 把第一条完整行当半行干掉);整个窗口都是半行则整段丢
          if (from > 0) {
            const nl = text.indexOf('\n')
            text = nl >= 0 ? text.slice(nl + 1) : ''
          }
          if (text) consume(text)
        } finally {
          try { fs.closeSync(fd) } catch (e) { /* ignore */ }
        }
      } catch (e) { /* .1 读不了(刚删/权限):环保持原样,下一轮再看 */ }
      return entries.slice(mark)
    }

    // 轮转救援:重置环 → 接进 .1 尾部 → 记住这份 .1(否则用户再点空态「加载」
    // 按钮会把同一份 .1 二次灌进环,条目翻倍)
    const salvageRotated = (rot) => {
      reset()
      ingestRotated(rot)
      rotLoaded = rot
    }

    const tail = () => {
      try {
        let st = null
        try { st = fs.statSync(file) } catch (e) { st = null }
        stats.exists = !!st
        if (st) { stats.logSize = st.size; stats.logMtimeMs = st.mtimeMs }
        else { stats.logSize = 0; stats.logMtimeMs = null }
        let rot = null
        try {
          const rst = fs.statSync(file + '.1')
          rot = { size: rst.size, mtimeMs: rst.mtimeMs }
        } catch (e) { rot = null }
        stats.rotated = !!rot
        stats.rotSize = rot ? rot.size : 0
        stats.rotMtimeMs = rot ? rot.mtimeMs : null
        // 轮转判定:v1.4.0 从「只看收缩」升级为「.1 换了 + 活动文件自上一拍被重写过」。
        // 只看收缩不够:轮转后新文件若在一拍内长过旧 offset(慢盘/挂起/大 POLL_MS
        // 完全可达),收缩判定不触发 → 既不救 .1 尾部,又把新文件的前 offset 字节
        // 整段跳过,两侧静默丢行。信号用 mtime 不用 birthtime——Windows NTFS 的
        // 隧道效应会让「改名后同名重建」的文件继承旧创建时间(实测 birthtime 与旧
        // 文件一致,信号失效);last-write 时间不受隧道影响。
        // 反向保护:只换 .1 内容而活动文件没动(外部工具/测试)→ activeRewritten
        // 为假 → 不误判轮转,/rotated 路由照旧手动加载。
        const rotChanged = !!rot && (!rotSeen || rot.size !== rotSeen.size || rot.mtimeMs !== rotSeen.mtimeMs)
        rotSeen = rot
        if (!st) {
          if (offset !== 0) {
            if (rotChanged) salvageRotated(rot)
            else reset()
          }
          return
        }
        const activeRewritten = activeMtimeMs != null && st.mtimeMs !== activeMtimeMs
        activeMtimeMs = st.mtimeMs
        if (rotChanged && activeRewritten) {
          // ocdup 轮转:救 .1 尾部(salvageRotated 内置 reset → offset=0)+ 新文件
          // 从头读(firstRead=false 关掉尾部窗口,头部不补就又断档)
          salvageRotated(rot)
          firstRead = false
        }
        else if (st.size < offset) {
          reset() // 面板清空(截断)或外部截断:.1 没动,旧历史不回流
        }
        if (st.size === offset) return

        let from = offset
        let dropPartial = false
        // 首次挂载遇到大日志:只读尾部窗口,并丢掉窗口首部的半行
        if (firstRead && st.size - from > FIRST_TAIL_BYTES) {
          from = st.size - FIRST_TAIL_BYTES
          dropPartial = true
        }

        const fd = fs.openSync(file, 'r')
        try {
          const buf = Buffer.allocUnsafe(st.size - from)
          const read = fs.readSync(fd, buf, 0, buf.length, from)
          offset = st.size
          firstRead = false
          let text = buf.toString('utf8', 0, read)
          if (dropPartial) {
            const nl = text.indexOf('\n')
            text = nl >= 0 ? text.slice(nl + 1) : '' // 本窗口还没写满一行:整段丢弃
          }
          if (text) consume(text)
        } finally {
          try { fs.closeSync(fd) } catch (e) { /* ignore */ }
        }
      } catch (e) { /* 尾随失败不致命,下轮重试 */ }
    }

    tail()
    const timer = setInterval(tail, POLL_MS)

    const view = () => ({
      cmd: stats.cmd, rsp: stats.rsp, sys: stats.sys, err: stats.err, log: stats.log,
      bytes: stats.bytes,
      lastAt: stats.lastAt, exists: stats.exists,
      kept: entries.length, lastSeq: seq, logPath: file,
      badLines: stats.badLines, logSize: stats.logSize,
      logMtimeMs: stats.logMtimeMs, rotated: stats.rotated,
      rotSize: stats.rotSize, rotMtimeMs: stats.rotMtimeMs,
      mcp: mcpState(),
    })

    /**
     * 按 since 取增量。entries 的 seq 严格递增,起点可直接算下标,无需全表
     * filter;since 早于保留窗口时退化为「最近 DEFAULT_FIRST 条」。
     */
    const sliceSince = (since) => {
      if (entries.length === 0) return []
      const firstSeq = entries[0].seq
      if (!Number.isFinite(since) || since < firstSeq) return entries.slice(-DEFAULT_FIRST)
      const start = since - firstSeq + 1
      if (start >= entries.length) return []
      return start <= 0 ? entries.slice() : entries.slice(start)
    }

    // ---- 同源私有路由:GET 增量,POST 清空 ----
    ctx.effect(() => webServer.register({
      kind: 'exact',
      path: ROUTE,
      async handler(req, res) {
        try {
          if (req.method !== 'GET') {
            jsonResponse(res, 405, { ok: false, error: 'GET only' })
            return
          }
          const url = new URL(req.url, 'http://local')
          const sinceRaw = url.searchParams.get('since')
          const since = Number(sinceRaw)
          // stats=1 心律模式:零条目仅统计(图标徽章轮询/agent 存活检查用)
          const statsOnly = url.searchParams.get('stats') === '1'
          let out = statsOnly
            ? []
            : (sinceRaw === null ? entries.slice(-DEFAULT_FIRST) : sliceSince(since))
          // 紧凑模式(agent 友好):单字段按码点截断
          if (!statsOnly && url.searchParams.get('compact') === '1') {
            const rawMax = Number(url.searchParams.get('max'))
            const max = Number.isFinite(rawMax) && rawMax > 0 ? Math.min(Math.floor(rawMax), 4096) : 512
            out = out.map((e) => ({
              seq: e.seq, dir: e.dir, tool: e.tool, tag: e.tag, len: e.len, dtMs: e.dtMs,
              payload: truncateCodePoints(e.text || '', max),
            }))
          }
          jsonResponse(res, 200, { ok: true, entries: out, stats: view() })
        } catch (error) {
          jsonResponse(res, 500, { ok: false, error: error instanceof Error ? error.message : String(error) })
        }
      },
    }), 'dsh-ocdup-panel: ' + ROUTE)

    ctx.effect(() => webServer.register({
      kind: 'exact',
      path: ROUTE_CLEAR,
      async handler(req, res) {
        try {
          if (req.method !== 'POST') {
            jsonResponse(res, 405, { ok: false, error: 'POST only' })
            return
          }
          // 截断共享日志(ocdup 侧检测收缩会自动重开);目录不存在则先建
          try {
            const dir = path.dirname(file)
            if (!fs.existsSync(dir)) fs.mkdirSync(dir, { recursive: true })
            fs.writeFileSync(file, '')
          } catch (e) { /* 文件尚不存在时无需截断 */ }
          reset()
          // 清空后仍允许重新加载同一份 .1:否则「加载」按钮会被 already loaded
          // 挡住,而环里其实已经空了(加载过→清空→再点,按钮没反应)
          rotLoaded = null
          jsonResponse(res, 200, { ok: true, stats: view() })
        } catch (error) {
          jsonResponse(res, 500, { ok: false, error: error instanceof Error ? error.message : String(error) })
        }
      },
    }), 'dsh-ocdup-panel: ' + ROUTE_CLEAR)

    // v1.3.6:按需把 .1(轮转备份)尾部窗口补进内存环。空态「加载」按钮走这里;
    // 轮转时的自动续接(见 tail)走同一条 ingestRotated 读取路径,不复制逻辑。
    ctx.effect(() => webServer.register({
      kind: 'exact',
      path: ROUTE_ROTATED,
      async handler(req, res) {
        try {
          if (req.method !== 'POST') {
            jsonResponse(res, 405, { ok: false, error: 'POST only' })
            return
          }
          let rot = null
          try {
            const rst = fs.statSync(file + '.1')
            rot = { size: rst.size, mtimeMs: rst.mtimeMs }
          } catch (e) { rot = null }
          if (!rot || rot.size <= 0) {
            jsonResponse(res, 200, { ok: true, entries: [], note: 'no rotated file', stats: view() })
            return
          }
          if (rotLoaded && rotLoaded.size === rot.size && rotLoaded.mtimeMs === rot.mtimeMs) {
            jsonResponse(res, 200, { ok: true, entries: [], note: 'already loaded', stats: view() })
            return
          }
          const added = ingestRotated(rot)
          rotLoaded = rot
          jsonResponse(res, 200, { ok: true, entries: added, note: 'loaded', stats: view() })
        } catch (error) {
          jsonResponse(res, 500, { ok: false, error: error instanceof Error ? error.message : String(error) })
        }
      },
    }), 'dsh-ocdup-panel: ' + ROUTE_ROTATED)

    // 断开/恢复后端(面板「后端」段的按钮):POST {action:'disconnect'|'connect'}
    // → ctx.tools.execute 调 mcp__ocdup__<disconnect|connect>。动作本身会被
    // ocdup 的 TrafficLog 镜像成 cmd/rsp + backend SYS 行,面板流水里可见可追溯。
    let callSeq = 0
    const callOcpTool = async (name, args) => {
      if (!tools || typeof tools.execute !== 'function') {
        return { ok: false, error: 'the host tools service is unavailable in this DSH build' }
      }
      const ctl = new AbortController()
      const timer = setTimeout(() => ctl.abort(), BACKEND_CALL_TIMEOUT_MS)
      try {
        const result = await tools.execute({
          callId: 'ocdup-panel-' + (++callSeq),
          name,
          arguments: args || {},
          signal: ctl.signal,
        })
        // ToolExecutionResult: { isError, value, content } —— content 是 ContentBlock[]
        const blocks = Array.isArray(result && result.content) ? result.content : []
        const text = (typeof result && result.value === 'string' ? result.value : '')
          || blocks.map((b) => (b && typeof b.text === 'string' ? b.text : '')).filter(Boolean).join('\n')
          || (typeof result && result.value != null ? JSON.stringify(result.value) : '')
        return result && result.isError
          ? { ok: false, error: text || 'the tool call failed' }
          : { ok: true, text }
      } catch (error) {
        const aborted = error && error.name === 'AbortError'
        return {
          ok: false,
          error: aborted
            ? `timed out after ${BACKEND_CALL_TIMEOUT_MS / 1000}s calling ${name} (the MCP server may be busy)`
            : (error instanceof Error ? error.message : String(error)),
        }
      } finally {
        clearTimeout(timer)
      }
    }

    ctx.effect(() => webServer.register({
      kind: 'exact',
      path: ROUTE_BACKEND,
      async handler(req, res) {
        try {
          if (req.method !== 'POST') {
            jsonResponse(res, 405, { ok: false, error: 'POST only' })
            return
          }
          const body = await readJsonBody(req)
          const action = body && typeof body.action === 'string' ? body.action : ''
          const name = BACKEND_ACTION_TOOL[action]
          if (!name) {
            jsonResponse(res, 400, { ok: false, error: "action must be 'disconnect' or 'connect'" })
            return
          }
          const out = await callOcpTool(name, {})
          jsonResponse(res, out.ok ? 200 : 502, Object.assign({ ok: out.ok, action }, out.ok ? { text: out.text } : { error: out.error }))
        } catch (error) {
          jsonResponse(res, 500, { ok: false, error: error instanceof Error ? error.message : String(error) })
        }
      },
    }), 'dsh-ocdup-panel: ' + ROUTE_BACKEND)

    // v1.3.8:一键切换 target_cfg(target 失配胶囊):POST {preset:'AT32F403A'}
    // → ctx.tools.execute 调 mcp__ocdup__set_target。切换 = 用新 target_cfg
    // 重启 ocdup 自拉的 openocd(preset 名见 ocd.json 的 target_presets,传
    // 'list' 可列出);只读会话被 ocdup 拦成 BLOCKED。胶囊切换成功后 client
    // 会顺手 connect 一次刷新链路行,所以这条路由只负责切换本身,不链式调用。
    ctx.effect(() => webServer.register({
      kind: 'exact',
      path: ROUTE_TARGET,
      async handler(req, res) {
        try {
          if (req.method !== 'POST') {
            jsonResponse(res, 405, { ok: false, error: 'POST only' })
            return
          }
          const body = await readJsonBody(req)
          const preset = body && typeof body.preset === 'string' ? body.preset.trim() : ''
          if (!preset) {
            jsonResponse(res, 400, { ok: false, error: "preset must be a non-empty string (see ocd.json target_presets)" })
            return
          }
          const out = await callOcpTool(TARGET_TOOL, { name: preset })
          jsonResponse(res, out.ok ? 200 : 502, Object.assign({ ok: out.ok, action: 'set_target', preset }, out.ok ? { text: out.text } : { error: out.error }))
        } catch (error) {
          jsonResponse(res, 500, { ok: false, error: error instanceof Error ? error.message : String(error) })
        }
      },
    }), 'dsh-ocdup-panel: ' + ROUTE_TARGET)

    // 导出 Host 内存环(≤MAX_ENTRIES 条,非完整磁盘日志)为 jsonl/csv 附件
    ctx.effect(() => webServer.register({
      kind: 'exact',
      path: ROUTE_EXPORT,
      async handler(req, res) {
        try {
          if (req.method !== 'GET') {
            jsonResponse(res, 405, { ok: false, error: 'GET only' })
            return
          }
          const url = new URL(req.url, 'http://local')
          const format = url.searchParams.get('format') === 'csv' ? 'csv' : 'jsonl'
          const stamp = new Date().toISOString().replace(/[:.]/g, '-')
          const body = format === 'csv'
            ? toCsv(entries)
            : entries.map((e) => JSON.stringify(e)).join('\n') + (entries.length ? '\n' : '')
          res.writeHead(200, {
            'content-type': format === 'csv' ? 'text/csv; charset=utf-8' : 'application/jsonl; charset=utf-8',
            'content-disposition': 'attachment; filename="ocdup-log-' + stamp + '.' + format + '"',
            'cache-control': 'no-store',
          })
          res.end(body)
        } catch (error) {
          jsonResponse(res, 500, { ok: false, error: error instanceof Error ? error.message : String(error) })
        }
      },
    }), 'dsh-ocdup-panel: ' + ROUTE_EXPORT)

    // agent 自发现:内联 markdown(接口速见),首次读取后缓存
    let helpBody = null
    ctx.effect(() => webServer.register({
      kind: 'exact',
      path: ROUTE_HELP,
      async handler(req, res) {
        try {
          if (req.method !== 'GET') {
            jsonResponse(res, 405, { ok: false, error: 'GET only' })
            return
          }
          if (helpBody === null) {
            try { helpBody = fs.readFileSync(path.join(__dirname, 'AGENTS.md'), 'utf8') } catch (e) { helpBody = HELP_MD }
          }
          res.writeHead(200, {
            'content-type': 'text/markdown; charset=utf-8',
            'cache-control': 'no-store',
          })
          res.end(helpBody)
        } catch (error) {
          jsonResponse(res, 500, { ok: false, error: error instanceof Error ? error.message : String(error) })
        }
      },
    }), 'dsh-ocdup-panel: ' + ROUTE_HELP)

    // v1.4.0:MCP 启停开关。GET 返回当前状态(随主路由 view() 一并给面板轮询);
    // POST {enabled:bool} 切条目。注意:切换只影响「新会话」的工具注册——
    // 已开会话的工具列表是快照,不会变(UI 需提示,别让用户以为立刻生效)。
    ctx.effect(() => webServer.register({
      kind: 'exact',
      path: ROUTE_MCP,
      async handler(req, res) {
        try {
          if (req.method === 'GET') {
            jsonResponse(res, 200, { ok: true, mcp: mcpState() })
            return
          }
          if (req.method !== 'POST') {
            jsonResponse(res, 405, { ok: false, error: 'GET or POST only' })
            return
          }
          const current = mcpState()
          if (!current.available) {
            jsonResponse(res, 404, { ok: false, error: 'mcp entry not found: ' + MCP_ENTRY_ID })
            return
          }
          if (!pluginManager || typeof pluginManager.setPluginEnabled !== 'function') {
            jsonResponse(res, 501, { ok: false, error: 'pluginManager service unavailable' })
            return
          }
          const body = await readJsonBody(req)
          const enabled = !!(body && body.enabled === true)
          if (current.enabled === enabled) {
            jsonResponse(res, 200, { ok: true, mcp: current, changed: false })
            return
          }
          await pluginManager.setPluginEnabled(MCP_ENTRY_ID, enabled)
          jsonResponse(res, 200, { ok: true, mcp: mcpState(), changed: true })
        } catch (error) {
          jsonResponse(res, 500, { ok: false, error: error instanceof Error ? error.message : String(error) })
        }
      },
    }), 'dsh-ocdup-panel: ' + ROUTE_MCP)

    return () => {
      clearInterval(timer)
      reset()
    }
  },
}
