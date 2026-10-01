'use strict'
// ============================================================
//  dsh-ocdup-panel — Host 半区(永久 cordis 插件)  v1.0.0
//
//  职责:
//   1. 尾随 %LOCALAPPDATA%\Ocdup\mcp-traffic.jsonl —— ocdup MCP 服务器的
//      TrafficLog 把 AI 每条 OpenOCD 交互镜像到这个共享 JSONL(逐条落盘,
//      超 2MB 轮转到 .1;面板清空 = 截断,ocdup 侧检测收缩会自动重开)。
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
const ROUTE_EXPORT = '/api/ocdup-log/export'
const ROUTE_HELP = '/api/ocdup-log/help'

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
  '- `GET /api/ocdup-log/export?format=jsonl|csv` — 导出内存环为附件',
  '- direction:`cmd`(TCL 命令)/`rsp`(响应)/`sys`(后端生命周期)/`err`(通道错误)',
  '- tool:发起的 MCP 工具名(flash_write/read_memory/…),`backend` = 生命周期事件',
].join('\n')

module.exports = {
  name: 'dsh-ocdup-panel',
  inject: ['webServer'],
  apply(ctx) {
    const webServer = ctx.get('webServer')
    if (!webServer || typeof webServer.register !== 'function') return

    const file = logPath()
    let offset = 0          // 已读字节偏移
    let remainder = ''      // 半行残留(写入端按行追加,可能读到半行)
    let firstRead = true    // 是否尚未读过(决定能否启用尾部窗口)
    let seq = 0             // 单调递增序号(不随 clear/轮转重置,客户端 since 依赖它)
    let prevTsMs = null     // 上一条记录的毫秒时间戳(Δt 用;坏时间戳不更新基准)
    const entries = []      // { seq, id, tool, dir, text, tag, ts, len, dtMs },seq 递增
    const stats = { cmd: 0, rsp: 0, sys: 0, err: 0, bytes: 0, lastAt: null, exists: false, badLines: 0, logSize: 0, logMtimeMs: null, rotated: false }

    const reset = () => {
      offset = 0
      remainder = ''
      firstRead = true
      prevTsMs = null
      entries.length = 0
      stats.cmd = 0; stats.rsp = 0; stats.sys = 0; stats.err = 0; stats.bytes = 0
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
        seq += 1
        // direction 归一化小写:写入端 cmd/rsp 小写、SYS 大写,统一后客户端只处理一套
        const dir = String(rec.direction || '').toLowerCase() === 'cmd' ? 'cmd'
          : String(rec.direction || '').toLowerCase() === 'rsp' ? 'rsp'
          : String(rec.direction || '').toLowerCase() === 'err' ? 'err'
          : 'sys'
        const len = payloadLen(rec)
        // Δt 相对上一条有效时间戳;乱序(多进程追加)钳到 0,坏时间戳记 null
        const tsMs = Date.parse(rec.timestamp || '')
        const dtMs = Number.isFinite(tsMs) && prevTsMs != null ? Math.max(0, tsMs - prevTsMs) : null
        if (Number.isFinite(tsMs)) prevTsMs = tsMs
        entries.push({
          seq,
          id: rec.id,
          tool: rec.tool || '',
          dir,
          hex: rec.rawHex || '',
          text: rec.text || '',
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

    const tail = () => {
      try {
        let st = null
        try { st = fs.statSync(file) } catch (e) { st = null }
        stats.exists = !!st
        if (st) { stats.logSize = st.size; stats.logMtimeMs = st.mtimeMs }
        else { stats.logSize = 0; stats.logMtimeMs = null }
        try { stats.rotated = fs.existsSync(file + '.1') } catch (e) { stats.rotated = false }
        if (!st) { if (offset !== 0) reset(); return }
        if (st.size < offset) reset() // 面板清空(截断)或 ocdup 轮转成新文件
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
      cmd: stats.cmd, rsp: stats.rsp, sys: stats.sys, err: stats.err,
      bytes: stats.bytes,
      lastAt: stats.lastAt, exists: stats.exists,
      kept: entries.length, lastSeq: seq, logPath: file,
      badLines: stats.badLines, logSize: stats.logSize,
      logMtimeMs: stats.logMtimeMs, rotated: stats.rotated,
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
          jsonResponse(res, 200, { ok: true, stats: view() })
        } catch (error) {
          jsonResponse(res, 500, { ok: false, error: error instanceof Error ? error.message : String(error) })
        }
      },
    }), 'dsh-ocdup-panel: ' + ROUTE_CLEAR)

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

    return () => {
      clearInterval(timer)
      reset()
    }
  },
}
