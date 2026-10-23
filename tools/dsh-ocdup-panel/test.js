'use strict'
// ============================================================
//  dsh-ocdup-panel — Host 半区行为测试(无框架,纯 node)
//
//  运行(在插件目录下):
//    node test.js core     # 增量/半行/切片/批量裁剪/轮转/清空/方法守卫/方向归一化
//    node test.js window   # 首次挂载只读尾部窗口(大日志不全量读入)
//    node test.js api      # Δt/compact/导出/help/轮转续接+rotated路由(需要 MAX_ENTRIES≥40)
//    node test.js client   # client.js 注册结构冒烟 + 纯函数契约 + 渲染冒烟(stub React,不起浏览器)
//
//  各模式需要的插件常量(如 MAX_ENTRIES/FIRST_TAIL)由 test.js 按 mode 自行设置,
//  日志默认落临时目录——上面那行零配置可跑;手动设 env 可覆盖,压别的边界用。
//  说明:插件在 require 时读取这些常量,所以 env 必须在 require 之前设置。
// ============================================================

const fs = require('fs')
const path = require('path')
const os = require('os')

const mode = process.argv[2] || 'core'

// 各模式对插件常量的要求不同(插件 require 时读 env,所以必须现在就设)。
// 已显式设置的值不动——想压别的边界时环境变量仍然优先。
//   core:   MAX_ENTRIES=5   → T4 批量裁剪(写 12 条裁到 5)
//   window: FIRST_TAIL=600  → T8 首次挂载只读 600B 尾部窗口(大日志不全量读入)
//   api:    MAX_ENTRIES=40  → T4/T9 Δt 统计需要 ≥40 条环
const MODE_ENV = {
  core: { DSH_OCDUP_PANEL_MAX_ENTRIES: '5' },
  window: { DSH_OCDUP_PANEL_FIRST_TAIL: '600' },
  api: { DSH_OCDUP_PANEL_MAX_ENTRIES: '40' },
}
for (const [k, v] of Object.entries(MODE_ENV[mode] || {})) {
  if (!process.env[k]) {
    process.env[k] = v
    console.log(`  (env ${k}=${v} for mode ${mode})`)
  }
}

const tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'dsh-op-'))
// 未显式指定时兜底用临时目录日志——README 的一行命令就能直接跑,也绝不碰真实流量日志。
const logFile = process.env.DSH_OCDUP_PANEL_LOG || path.join(tmp, 'traffic.jsonl')
if (mode !== 'client') console.log(`  (log file: ${logFile})`)
if (!process.env.DSH_OCDUP_PANEL_LOG) process.env.DSH_OCDUP_PANEL_LOG = logFile
{
  fs.mkdirSync(path.dirname(logFile), { recursive: true })
  // 清掉上次运行残留:否则 apply 时会先读入旧内容、offset 停在旧文件末尾,
  // 随后 writeFileSync 的新内容只被"增量"读到一半(T1 丢行的根因)。
  fs.rmSync(logFile, { force: true })
  fs.rmSync(logFile + '.1', { force: true })
}

const line = (id, dir, text, tag, ts, tool) => JSON.stringify({
  id, tool: tool || 'read_memory', timestamp: ts || '2026-10-01T10:00:00.123+08:00',
  direction: dir,
  rawHex: '', text, portTag: tag || '',
})

const mod = require(path.join(__dirname, 'index.js'))
const routes = {}
const ctx = {
  get: (name) => (name === 'webServer' ? { register: (r) => { routes[r.path] = r.handler } } : undefined),
  effect: (fn) => fn(),
}
const dispose = mod.apply(ctx)
let dispose2 = null

const call = (route, req) => new Promise((resolve) => {
  const res = { writeHead() {}, end: (t) => resolve(JSON.parse(t)) }
  routes[route](req, res)
})
// export/help 路由返回非 JSON 附件,需要捕获状态码/响应头/原始 body
const callRaw = (route, req) => new Promise((resolve) => {
  const cap = { status: 0, headers: {}, body: '' }
  const res = {
    writeHead: (s, h2) => { cap.status = s; cap.headers = h2 || {} },
    end: (t) => { cap.body = t; resolve(cap) },
  }
  routes[route](req, res)
})
const sleep = (ms) => new Promise((r) => setTimeout(r, ms))
let checks = 0
const ok = (cond, label, extra) => {
  checks += 1
  if (!cond) throw new Error('FAIL: ' + label + (extra ? '  -> ' + JSON.stringify(extra) : ''))
  console.log('  ok  ' + label)
}

;(async () => {
  if (mode === 'core') {
    const max = Number(process.env.DSH_OCDUP_PANEL_MAX_ENTRIES)

    // T1 首次读取 + 方向归一化(SYS 大写 → sys)
    fs.writeFileSync(logFile, [
      line(1, 'cmd', 'mdw 0x20000000 16'),
      line(2, 'rsp', '0x20000000: 11111111'),
      line(3, 'SYS', 'started openocd (pid 123)'),
    ].join('\n') + '\n')
    await sleep(600)
    let r = await call('/api/ocdup-log', { method: 'GET', url: '/' })
    console.log('T1 initial:', r.ok, 'entries=' + r.entries.length, 'lastSeq=' + r.stats.lastSeq)
    ok(r.ok && r.entries.length === 3, 'T1 首次读取返回全部行')
    ok(r.stats.lastSeq === 3 && r.stats.cmd === 1 && r.stats.rsp === 1 && r.stats.sys === 1 && r.stats.err === 0,
      'T1 按方向统计正确', r.stats)
    ok(r.entries[0].dir === 'cmd' && r.entries[1].dir === 'rsp' && r.entries[2].dir === 'sys',
      'T1 direction 归一化为小写', r.entries.map((e) => e.dir))
    ok(r.entries[0].dtMs === null && r.entries[2].dtMs === 0, 'T1 Δt:首条 null,同毫秒钳 0', r.entries.map((e) => e.dtMs))
    ok(r.entries[2].tool === 'read_memory', 'T1 tool 字段透传')

    // T2 半行拼接(写入端分批写)
    fs.appendFileSync(logFile, line(4, 'cmd', 'dump_image {C:\\dumps\\f.bin}'))
    await sleep(600)
    fs.appendFileSync(logFile, '\n' + line(5, 'err', 'control channel failed', '127.0.0.1:29171') + '\n')
    await sleep(600)
    r = await call('/api/ocdup-log', { method: 'GET', url: '/?since=3' })
    ok(r.entries.length === 2 && r.entries[0].seq === 4 && r.entries[0].text.indexOf('dump_image') === 0,
      'T2 半行残留拼接正确', r.entries)
    ok(r.entries[1].dir === 'err' && r.entries[1].tag === '127.0.0.1:29171', 'T2 err 方向 + 目标 tag 透传')

    // T3 since 切片(直接算下标)
    r = await call('/api/ocdup-log', { method: 'GET', url: '/?since=4' })
    ok(r.entries.length === 1 && r.entries[0].seq === 5, 'T3 since 精确切片', r.entries.map((e) => e.seq))
    r = await call('/api/ocdup-log', { method: 'GET', url: '/?since=5' })
    ok(r.entries.length === 0, 'T3 已到末尾返回空')

    // T4 批量裁剪(MAX_ENTRIES=5,写入到 12 条)
    for (let i = 6; i <= 12; i++) fs.appendFileSync(logFile, line(i, i % 2 ? 'cmd' : 'rsp', 'x' + i) + '\n')
    await sleep(700)
    r = await call('/api/ocdup-log', { method: 'GET', url: '/?since=12' })
    ok(r.stats.kept === max, 'T4 内存环裁剪到上限 ' + max, { kept: r.stats.kept })
    ok(r.stats.lastSeq === 12, 'T4 序号继续单调(不被裁剪重置)', r.stats.lastSeq)
    r = await call('/api/ocdup-log', { method: 'GET', url: '/?since=1' })
    ok(r.entries.length === max && r.entries[0].seq === 8, 'T4 早于窗口的 since 退化为保留窗口', { n: r.entries.length, first: r.entries[0] && r.entries[0].seq })

    // T5 轮转/清空 → 文件变小,重置偏移
    fs.writeFileSync(logFile, line(99, 'SYS', 'fresh') + '\n')
    await sleep(600)
    r = await call('/api/ocdup-log', { method: 'GET', url: '/?since=0' })
    ok(r.entries.length === 1 && r.entries[0].text === 'fresh', 'T5 文件变小后重新同步', r.entries)

    // T6 清空路由 + 方法守卫
    r = await call('/api/ocdup-log/clear', { method: 'POST', url: '/' })
    ok(r.ok && fs.statSync(logFile).size === 0, 'T6 POST clear 截断文件')
    r = await call('/api/ocdup-log', { method: 'POST', url: '/' })
    ok(r.ok === false && /GET only/.test(r.error), 'T6 GET 路由拒绝 POST')
    r = await call('/api/ocdup-log/clear', { method: 'GET', url: '/' })
    ok(r.ok === false && /POST only/.test(r.error), 'T6 clear 路由拒绝 GET')

    // T7 坏行不致命
    fs.appendFileSync(logFile, '{not json\n' + line(100, 'rsp', 'after-bad') + '\n')
    await sleep(600)
    r = await call('/api/ocdup-log', { method: 'GET', url: '/?since=0' })
    ok(r.ok && r.entries.length === 1 && r.entries[0].text === 'after-bad', 'T7 坏行跳过,后续行照常解析', r.entries)
    ok(r.stats.badLines === 1, 'T7 坏行计数入 stats', r.stats.badLines)

    // T7b log 方向归一化(openocd 异步日志镜像,面板 v1.1.0)
    // 注意:先走 clear 截断(生产中唯一的全量重写路径),否则大文件全量重写
    // 会被当作增量、从旧 offset 中段读入(真实运行中不存在这种写入形态)。
    await call('/api/ocdup-log/clear', { method: 'POST', url: '/' })
    await sleep(500)
    fs.writeFileSync(logFile, [
      line(200, 'cmd', 'mww 0x20000000 1'),
      line(201, 'log', 'Info : flash erase sectors 0 3'),
    ].join('\n') + '\n')
    await sleep(600)
    r = await call('/api/ocdup-log', { method: 'GET', url: '/?since=0' })
    ok(r.entries.length === 2 && r.entries[1].dir === 'log', 'T7b log 方向归一化为小写', r.entries.map((e) => e.dir))
    ok(r.stats.log === 1 && r.stats.cmd === 1, 'T7b log 计数入 stats', { log: r.stats.log, cmd: r.stats.cmd })

    // T7c v1.4.0：字面量 null 行 + 非字符串字段。JSON.parse('null') 也「成功」,
    // 此前 rec.direction 对 null 取属性抛异常,异常逃出 consume 时 offset 已推进,
    // 该批后续行永远不再被读(静默丢数据);text/tool 非字符串原样透传会让客户端
    // e.text.length / e.tool.toLowerCase() 直接崩。
    await call('/api/ocdup-log/clear', { method: 'POST', url: '/' })
    await sleep(500)
    fs.writeFileSync(logFile, [
      'null',
      line(300, 'cmd', 'after-null-1'),
      JSON.stringify({ id: 301, tool: { nested: true }, timestamp: '2026-10-01T10:00:00.000+08:00', direction: 'rsp', text: 12345, portTag: '' }),
      line(302, 'rsp', 'after-null-2'),
    ].join('\n') + '\n')
    await sleep(600)
    r = await call('/api/ocdup-log', { method: 'GET', url: '/?since=0' })
    const t7cTexts = r.entries.map((e) => e.text)
    ok(r.entries.length === 3 && t7cTexts.indexOf('after-null-1') >= 0 && t7cTexts.indexOf('after-null-2') >= 0,
      'T7c null 行按坏行计,不吞后续行(曾因逃逸异常静默丢整批)', t7cTexts)
    ok(r.stats.badLines === 1, 'T7c null 行计入 badLines', r.stats.badLines)
    const t7cObj = r.entries.find((e) => e.id === 301)
    ok(t7cObj && t7cObj.text === '12345' && typeof t7cObj.text === 'string'
      && typeof t7cObj.tool === 'string' && t7cObj.tool.indexOf('nested') >= 0,
      'T7c 非字符串 text/tool 入口归一成字符串(客户端不再崩)', t7cObj && { text: t7cObj.text, tool: t7cObj.tool })
  }

  if (mode === 'window') {
    // T8 首次挂载只读尾部窗口:先造一个大日志,窗口压到 600B(ocdup 行 ~130B,约 4-5 行)
    const lines = []
    for (let i = 1; i <= 60; i++) lines.push(line(i, i % 2 ? 'cmd' : 'rsp', 'payload-' + i))
    fs.writeFileSync(logFile, lines.join('\n') + '\n')
    const size = fs.statSync(logFile).size

    const fresh = require(path.join(__dirname, 'index.js'))
    const r2 = {}
    const ctx2 = {
      get: (n) => (n === 'webServer' ? { register: (r) => { r2[r.path] = r.handler } } : undefined),
      effect: (fn) => fn(),
    }
    dispose2 = fresh.apply(ctx2) // tail() 在 apply 内同步跑一次
    const res = await new Promise((resolve) => {
      const rr = { writeHead() {}, end: (t) => resolve(JSON.parse(t)) }
      r2['/api/ocdup-log']({ method: 'GET', url: '/' }, rr)
    })

    console.log('T8 file=' + size + 'B, parsed=' + res.entries.length + ', firstId=' + (res.entries[0] && res.entries[0].id) + ', lastId=' + (res.entries[res.entries.length - 1] && res.entries[res.entries.length - 1].id))
    ok(size > 3000, 'T8 前置条件:日志远大于窗口', size)
    ok(res.entries.length >= 3 && res.entries.length < 20, 'T8 只解析窗口内的少量行', res.entries.length)
    ok(res.entries[res.entries.length - 1].id === 60, 'T8 最后一行完整解析')
    ok(res.entries[0].id > 1 && res.entries[0].id < 60, 'T8 首行来自窗口(证明未全量读入)', res.entries[0].id)
    ok(res.entries.every((e) => typeof e.text === 'string' && e.text.indexOf('payload-') === 0), 'T8 无半行造成的脏数据')
  }

  if (mode === 'api') {
    const envMax = Number(process.env.DSH_OCDUP_PANEL_MAX_ENTRIES || 0)
    if (envMax > 0 && envMax < 40) {
      throw new Error('api 模式需要 MAX_ENTRIES≥40(或移除 DSH_OCDUP_PANEL_MAX_ENTRIES 后运行)')
    }

    // T9 Δt / 链路诊断字段
    fs.writeFileSync(logFile, [
      line(1, 'cmd', 'dt1', '', '2026-10-01T10:00:01.000+08:00'),
      line(2, 'rsp', 'dt2', '', '2026-10-01T10:00:02.250+08:00'),
      line(3, 'SYS', 'dt3', '', '2026-10-01T10:00:02.000+08:00'),
      line(4, 'cmd', 'badts', '', 'not-a-date'),
      line(5, 'rsp', '😀😀', '', '2026-10-01T10:00:03.000+08:00'),
      line(6, 'rsp', 'x'.repeat(600), '127.0.0.1:6666', '2026-10-01T10:00:03.100+08:00', 'flash_write'),
      line(7, 'rsp', 'y'.repeat(5000), '', '2026-10-01T10:00:03.200+08:00', 'dump_memory'),
      line(8, 'err', 'a,b"c\nd', '', '2026-10-01T10:00:03.300+08:00'),
    ].join('\n') + '\n')
    await sleep(600)
    let r = await call('/api/ocdup-log', { method: 'GET', url: '/?since=0' })
    const byId = {}
    for (const e of r.entries) byId[e.id] = e
    ok(byId[1] && byId[1].dtMs === null, 'T9 首条 Δt 为 null', byId[1] && byId[1].dtMs)
    ok(byId[2] && byId[2].dtMs === 1250, 'T9 Δt 正常计算', byId[2] && byId[2].dtMs)
    ok(byId[3] && byId[3].dtMs === 0, 'T9 乱序时间戳钳 0', byId[3] && byId[3].dtMs)
    ok(byId[4] && byId[4].dtMs === null, 'T9 坏时间戳记 null(基准不推进)', byId[4] && byId[4].dtMs)
    ok(byId[8] && byId[8].dir === 'err', 'T9 err 方向归一化', byId[8] && byId[8].dir)
    ok(typeof r.stats.logSize === 'number' && r.stats.logSize > 0
      && typeof r.stats.logMtimeMs === 'number' && typeof r.stats.rotated === 'boolean'
      && r.stats.badLines === 0, 'T9 链路诊断字段齐备', r.stats)

    // stats=1 心律模式:零条目仅统计
    const rh = await call('/api/ocdup-log', { method: 'GET', url: '/?stats=1' })
    ok(rh.ok && rh.entries.length === 0 && rh.stats.lastSeq === 8 && rh.stats.rsp === 4,
      'T9 stats=1 返回零条目仅统计', rh.stats)

    // T10 compact 模式
    r = await call('/api/ocdup-log', { method: 'GET', url: '/?since=0&compact=1' })
    const c = {}
    for (const e of r.entries) c[e.seq] = e
    ok(!('text' in c[1]) && typeof c[1].payload === 'string' && c[1].tool === 'read_memory',
      'T10 去掉 text 只留 payload + tool', Object.keys(c[1] || {}))
    r = await call('/api/ocdup-log', { method: 'GET', url: '/?since=0&compact=1&max=1' })
    ok(r.entries.find((e) => e.seq === 5).payload === '😀', 'T10 截断按码点(代理对不劈开)', r.entries.find((e) => e.seq === 5))
    ok(c[6].payload.length === 512 && c[6].tool === 'flash_write', 'T10 默认 max=512', c[6] && c[6].payload.length)
    r = await call('/api/ocdup-log', { method: 'GET', url: '/?since=0&compact=1&max=999999' })
    ok(r.entries.find((e) => e.seq === 7).payload.length === 4096, 'T10 max 上限 4096', r.entries.find((e) => e.seq === 7).payload.length)

    // T11 导出 jsonl/csv + 方法守卫
    const rJsonl = await callRaw('/api/ocdup-log/export', { method: 'GET', url: '/api/ocdup-log/export?format=jsonl' })
    const jsonlLines = rJsonl.body.split('\n').filter(Boolean)
    ok(rJsonl.status === 200
      && /jsonl/.test(rJsonl.headers['content-type'] || '')
      && /ocdup-log-/.test(rJsonl.headers['content-disposition'] || '')
      && jsonlLines.length === r.entries.length
      && typeof JSON.parse(jsonlLines[0]).seq === 'number', 'T11 jsonl 导出逐行可解析', { n: jsonlLines.length, h: rJsonl.headers })
    const rCsv = await callRaw('/api/ocdup-log/export', { method: 'GET', url: '/api/ocdup-log/export?format=csv' })
    ok(rCsv.status === 200
      && /csv/.test(rCsv.headers['content-type'] || '')
      && rCsv.body.charCodeAt(0) === 0xFEFF
      && rCsv.body.slice(1).startsWith('seq,ts,dir,tool,tag,len,dtMs,text'), 'T11 csv 表头与 BOM', rCsv.body.slice(0, 60))
    ok(rCsv.body.indexOf('"a,b""c\nd"') >= 0, 'T11 csv 逗号/引号/换行转义', JSON.stringify(rCsv.body.slice(-60)))
    const r405 = await call('/api/ocdup-log/export', { method: 'POST', url: '/' })
    ok(r405.ok === false && /GET only/.test(r405.error), 'T11 export 拒绝 POST')

    // T11b help 路由:返回内联 markdown(agent 自发现)
    const rHelp = await callRaw('/api/ocdup-log/help', { method: 'GET', url: '/api/ocdup-log/help' })
    ok(rHelp.status === 200
      && /markdown/.test(rHelp.headers['content-type'] || '')
      && /api\/ocdup-log/.test(rHelp.body), 'T11b help 路由返回接口说明', { s: rHelp.status })
    const rHelp405 = await call('/api/ocdup-log/help', { method: 'POST', url: '/' })
    ok(rHelp405.ok === false && /GET only/.test(rHelp405.error), 'T11b help 拒绝 POST')

    // T12 v1.3.6 轮转续接(用户截图:重启后历史全空,footer 却写「轮转文件已生成」):
    //   活动文件消失/收缩 且 .1 刚换过 → ocdup 轮转,把 .1 尾部接进环(不白屏);
    //   .1 没动 → 面板清空/外部截断,照旧全清,旧历史不回流
    const rotLines = []
    for (let i = 1; i <= 6; i++) rotLines.push(line(300 + i, i % 2 ? 'cmd' : 'rsp', 'rot-' + i))
    fs.writeFileSync(logFile, rotLines.join('\n') + '\n')
    await sleep(600)
    let rRot = await call('/api/ocdup-log', { method: 'GET', url: '/?since=0' })
    ok(rRot.entries.length === 6, 'T12 前置:轮转前 6 条在环里', rRot.entries.length)

    // 模拟 ocdup 轮转:活动文件整体移成 .1(mtime 保留),宿主该判定轮转并救援尾部
    fs.renameSync(logFile, logFile + '.1')
    await sleep(600)
    fs.writeFileSync(logFile, line(400, 'SYS', 'after-rotate') + '\n')
    await sleep(600)
    rRot = await call('/api/ocdup-log', { method: 'GET', url: '/?since=0' })
    const rotTexts = rRot.entries.map((e) => e.text)
    ok(rRot.stats.rotated === true && rRot.stats.rotSize > 0 && typeof rRot.stats.rotMtimeMs === 'number',
      'T12 stats 暴露 .1 的大小/时间', { rotated: rRot.stats.rotated, rotSize: rRot.stats.rotSize })
    ok(rotTexts.indexOf('rot-6') >= 0 && rotTexts.indexOf('rot-1') >= 0,
      'T12 轮转后 .1 尾部被接进环(看着的人不白屏)', rotTexts)
    ok(rotTexts.indexOf('after-rotate') > rotTexts.indexOf('rot-6'),
      'T12 新周期条目接在 .1 尾部之后(时序正确)', rotTexts)

    // .1 没变时的收缩 = 清空/外部截断:全清,不把 .1 又捞回来
    fs.writeFileSync(logFile, line(401, 'rsp', 'post-clear') + '\n')
    await sleep(600)
    rRot = await call('/api/ocdup-log', { method: 'GET', url: '/?since=0' })
    ok(rRot.entries.length === 1 && rRot.entries[0].text === 'post-clear',
      'T12 .1 未变时的收缩按清空处理(旧历史不回流)', rRot.entries.map((e) => e.text))

    // /rotated 路由:自动续接已记住这份 .1 → 再点「加载」不二次灌入(防翻倍)
    rRot = await call('/api/ocdup-log/rotated', { method: 'POST', url: '/' })
    ok(rRot.ok && rRot.note === 'already loaded' && rRot.entries.length === 0,
      'T12 自动续接过的 .1 再点加载返回 already loaded(不翻倍)', { note: rRot.note })

    // 直接换 .1 内容(活动文件不动 → 不触发自动续接)→ /rotated 真加载
    fs.writeFileSync(logFile + '.1', [
      line(500, 'cmd', 'man-1'), line(501, 'rsp', 'man-2'), line(502, 'log', 'man-3'),
    ].join('\n') + '\n')
    await sleep(600)
    rRot = await call('/api/ocdup-log/rotated', { method: 'POST', url: '/' })
    ok(rRot.ok && rRot.note === 'loaded' && rRot.entries.length === 3,
      'T12 新 .1 经 POST /rotated 补进环(3 条)', { note: rRot.note, n: rRot.entries.length })
    ok(rRot.stats.cmd === 1 && rRot.stats.rsp === 2 && rRot.stats.log === 1,
      'T12 /rotated 后 stats 计入补入的条目', { cmd: rRot.stats.cmd, rsp: rRot.stats.rsp, log: rRot.stats.log })
    rRot = await call('/api/ocdup-log/rotated', { method: 'POST', url: '/' })
    ok(rRot.ok && rRot.note === 'already loaded' && rRot.entries.length === 0,
      'T12 同一份 .1 重复加载返回 already loaded', { note: rRot.note })
    rRot = await call('/api/ocdup-log/rotated', { method: 'GET', url: '/' })
    ok(rRot.ok === false && /POST only/.test(rRot.error), 'T12 /rotated 拒绝 GET')

    // v1.3.7:清空后同一份 .1 还能再加载(clear 重置 rotLoaded)。不清的话
    // 「加载过 → 清空 → 再点加载」会被 already loaded 挡住,而环里其实已空
    rRot = await call('/api/ocdup-log/clear', { method: 'POST', url: '/' })
    ok(rRot.ok && rRot.stats.kept === 0, 'T12 clear 路由重置内存环', { kept: rRot.stats.kept })
    await sleep(500)
    rRot = await call('/api/ocdup-log/rotated', { method: 'POST', url: '/' })
    ok(rRot.ok && rRot.note === 'loaded' && rRot.entries.length === 3,
      'T12 清空后同一份 .1 仍可再加载(rotLoaded 已重置)', { note: rRot.note, n: rRot.entries.length })

    // T12c v1.4.0(放最后:会改写 .1 内容):轮转后新文件一拍内长过旧 offset——
    // 只靠「收缩」判定会两侧丢行:不救 .1 尾部(白屏),又把新文件的前 offset
    // 字节整段跳过(头部丢失)。修法:判定补「.1 换了 + 活动文件被重写过」
    // (mtime 信号;NTFS 隧道效应下 birthtime 不可靠,实测过)→ 救 .1 + 从头读。
    await call('/api/ocdup-log/clear', { method: 'POST', url: '/' })
    await sleep(500)
    const preLines = []
    for (let i = 1; i <= 5; i++) preLines.push(line(600 + i, 'cmd', 'pre-' + i))
    fs.writeFileSync(logFile, preLines.join('\n') + '\n')
    await sleep(600)
    let rT12 = await call('/api/ocdup-log', { method: 'GET', url: '/?since=0' })
    const oldOffset = fs.statSync(logFile).size
    ok(rT12.entries.length === 5 && oldOffset > 100, 'T12c 前置:轮转前 5 条在环里', { n: rT12.entries.length, oldOffset })
    // 同一拍内:整体移成 .1 + 新文件首行就写到远超旧 offset(旧代码从这里开始丢行)
    fs.renameSync(logFile, logFile + '.1')
    const bigHead = 'x'.repeat(oldOffset + 500)
    fs.writeFileSync(logFile, line(700, 'cmd', bigHead) + '\n' + line(701, 'rsp', 'post-big') + '\n')
    await sleep(600)
    rT12 = await call('/api/ocdup-log', { method: 'GET', url: '/?since=0' })
    const tT = rT12.entries.map((e) => e.text)
    ok(tT.indexOf('pre-1') >= 0 && tT.indexOf('pre-5') >= 0,
      'T12c 新文件变大也救 .1 尾部(不白屏)', tT.slice(0, 8))
    ok(tT.indexOf(bigHead) >= 0 && tT.indexOf('post-big') >= 0,
      'T12c 新文件头部不跳(从 offset 中段起读曾把首行整段吞掉)', { hasBig: tT.indexOf(bigHead) >= 0, hasPost: tT.indexOf('post-big') >= 0 })

    // T12b v1.3.8 POST /api/ocdup-target(失配胶囊一键切换):经 ctx.tools.execute
    // 调 mcp__ocdup__set_target——MCP 工具在 Host 侧就是普通工具,v1.3.4 的后端
    // 路由同款路径。fresh 实例 + tools 桩,不碰真实探针。
    const fresh3 = require(path.join(__dirname, 'index.js'))
    const r3 = {}
    const toolCalls = []
    const ctx3 = {
      get: (n) => (n === 'webServer' ? { register: (r) => { r3[r.path] = r.handler } }
        : n === 'tools' ? {
          execute: async (a) => {
            toolCalls.push(a)
            return toolCalls.length === 1
              ? { isError: false, value: "target switched to 'AT32F403A' -> target/at32f403axG.cfg" }
              : { isError: true, value: 'BLOCKED: read_only session' }
          },
        } : undefined),
      effect: (fn) => fn(),
    }
    dispose2 = fresh3.apply(ctx3)
    const call3 = (route, method, bodyObj) => new Promise((resolve) => {
      const res = { writeHead() {}, end: (t) => resolve(JSON.parse(t)) }
      const req = {
        method, url: '/',
        on: (ev, fn) => {
          if (ev === 'data' && bodyObj !== undefined) fn(Buffer.from(JSON.stringify(bodyObj)))
          if (ev === 'end') fn()
        },
      }
      r3[route](req, res)
    })
    let rT = await call3('/api/ocdup-target', 'GET')
    ok(rT.ok === false && /POST only/.test(rT.error), 'T12b /api/ocdup-target 拒绝 GET')
    rT = await call3('/api/ocdup-target', 'POST')
    ok(rT.ok === false && /preset/.test(rT.error), 'T12b 缺 preset → 400', rT.error)
    rT = await call3('/api/ocdup-target', 'POST', { preset: 'AT32F403A' })
    ok(rT.ok && rT.action === 'set_target' && rT.preset === 'AT32F403A'
      && /at32f403axG/.test(rT.text || ''),
      'T12b 正常路径:ok + 回显 preset + 工具返回文本', rT)
    ok(toolCalls.length === 1 && toolCalls[0].name === 'mcp__ocdup__set_target'
      && toolCalls[0].arguments && toolCalls[0].arguments.name === 'AT32F403A',
      'T12b 经 tools.execute 调 mcp__ocdup__set_target {name:preset}', toolCalls)
    rT = await call3('/api/ocdup-target', 'POST', { preset: 'AT32F403A' })
    ok(rT.ok === false && /BLOCKED/.test(rT.error || ''),
      'T12b 工具 isError(如只读会话被拦)→ 502 + 原文错误', rT)
  }

  if (mode === 'client') {
    // T12 client.js 注册结构冒烟:stub 模块环境,验证两条注册分支不跑浏览器
    // v1.3.6:useState 支持按调用次序覆盖初始值(T26 用位置 1 注入 stats)。
    // v1.3.7:升级成带 hook 作用域的迷你 React——每个函数组件一份 hooks,
    // 主组件作用域跨重渲染保留,于是「点按钮 → setState → 重渲染 → 看树」
    // 能真跑(用户那个「条目被清空、加载备注还在」的自相矛盾画面就是这么抓的)。
    const scopeStack = []
    const newScope = () => ({ hooks: [], idx: 0, overrides: [] })
    const ReactStub = {
      createElement: (type, props, ...children) => ({ type, props, children }),
      memo: (f) => f,
      useState: (v) => {
        const s = scopeStack[scopeStack.length - 1]
        const i = s.idx++
        if (!(i in s.hooks)) {
          s.hooks[i] = (i < s.overrides.length && s.overrides[i] !== undefined)
            ? s.overrides[i]
            : (typeof v === 'function' ? v() : v)
        }
        const setter = (arg) => {
          s.hooks[i] = typeof arg === 'function' ? arg(s.hooks[i]) : arg
        }
        return [s.hooks[i], setter]
      },
      useEffect: () => {},
      useRef: (v) => {
        const s = scopeStack[scopeStack.length - 1]
        const i = s.idx++
        if (!(i in s.hooks)) s.hooks[i] = { current: v }
        return s.hooks[i]
      },
      useMemo: (f) => f(),
    }
    let loaded = null
    globalThis.window = { __ModuleLoader__: { load: (m) => { loaded = m } } }
    const injectedStyles = { text: '' }
    globalThis.document = {
      getElementById: () => null,
      createElement: () => ({ textContent: '' }),
      head: { appendChild: (el) => { injectedStyles.text = el.textContent } },
    }
    globalThis.localStorage = { getItem: () => null, setItem: () => {} }
    require(path.join(__dirname, 'client.js'))
    ok(loaded && typeof loaded.factory === 'function', 'T12 模块经 __ModuleLoader__.load 注册')
    const mod = loaded.factory(() => ReactStub)
    ok(Array.isArray(mod.inject) && mod.inject.includes('slots'), 'T12 inject 声明 slots',
      mod.inject)

    // 纯函数契约(_test 导出)
    const hi = mod._test.highlight('hello world hello', 'hello')
    ok(Array.isArray(hi) && hi.length === 3 && hi[0].type === 'mark' && hi[1] === ' world ' && hi[2].type === 'mark',
      'T13 highlight 命中包裹为 mark', hi.map((x) => (typeof x === 'string' ? x : x.type)))
    ok(mod._test.highlight('abc', 'zz').length === 1 && mod._test.highlight('abc', 'zz')[0] === 'abc', 'T13 highlight 无命中原样返回')
    ok(mod._test.highlight('ABC', 'a').length === 2, 'T13 highlight 大小写不敏感')
    ok(mod._test.fmtDt(null) === '—' && mod._test.fmtDt(500) === '500ms' && mod._test.fmtDt(1500) === '1.50s', 'T13 fmtDt 三档格式')
    ok(mod._test.hueOf('127.0.0.1:6666') === mod._test.hueOf('127.0.0.1:6666') && Number.isInteger(mod._test.hueOf('127.0.0.1:6666')), 'T13 hueOf 稳定')

    // T14 匹配器/正则高亮
    const cm = mod._test.compileMatcher
    ok(cm('ABC', false) === 'abc', 'T14 子串模式小写化', cm('ABC', false))
    const re = cm('^mdw', true)
    ok(re instanceof RegExp && re.source === '^mdw' && re.ignoreCase, 'T14 正则模式返回 RegExp', re)
    ok(cm('((', true) === '((', 'T14 非法正则回退子串', cm('((', true))
    ok(cm('  ', true) === null, 'T14 空查询返回 null')
    const hitRe = mod._test.matcherHit(cm('mdw', true), { text: 'mdw 0x20000000', tool: 'read_memory', tag: '' })
    ok(hitRe === true, 'T14 matcherHit 正则命中 text 字段', hitRe)
    const hitTool = mod._test.matcherHit(cm('flash_write', false), { text: 'program x', tool: 'flash_write', tag: '' })
    ok(hitTool === true, 'T14 matcherHit 子串命中 tool 字段', hitTool)
    // v1.4.0：text 的匹配主题与行显示同源(截到 ROW_PAYLOAD_MAX)——命中落在截断
    // 尾部时不再「行保留但高亮丢」;顺带把用户正则的回溯面从整条 4000 字符收窄
    const longText = 'a'.repeat(4000) + 'NEEDLE' + 'b'.repeat(300)
    ok(mod._test.matcherHit(cm('NEEDLE', false), { text: longText, tool: 't', tag: '' }) === false,
      'T14 命中落在显示截断尾部之外 → 不命中(与行显示一致)', 'len=' + longText.length)
    ok(mod._test.matcherHit(cm('NEEDLE', false), { text: 'NEEDLE at head', tool: 't', tag: '' }) === true,
      'T14 截断窗口内的命中照常')
    const hitNonString = mod._test.matcherHit(cm('x', false), { text: 12345, tool: 't', tag: '' })
    ok(hitNonString === false, 'T14 非字符串 text 不炸(入口已归一,这里双保险)', hitNonString)
    const hr = mod._test.highlightRe('ab', /x*/)
    ok(Array.isArray(hr) && hr.length >= 2, 'T14 正则高亮零长匹配不死循环', hr.map((x) => (typeof x === 'string' ? x : x.type)))
    const hr2 = mod._test.highlightRe('a1b2', /\d/)
    ok(hr2.length === 4 && hr2[1].type === 'mark' && hr2[1].children[0] === '1'
      && hr2[3].children[0] === '2', 'T14 正则高亮包裹数字命中(非 g 正则不死循环)', hr2.map((x) => (typeof x === 'string' ? x : x.children)))

    // v1.3.4:左侧图标与未读徽章已随入口改版移除,badgeStore 及其断言一并删除

    // T15 烧录进度解析(openocd 0.11/0.12 与 atopenocd 实测文案)
    const pfp = mod._test.parseFlashProgress
    ok(pfp('Info : erased sector 3 3').type === 'erase' && pfp('Info : erased sector 3 3').count === 1
      && pfp('Info : erased sector 3 3').last === 3, 'T15 erased sector N N 单扇区', pfp('Info : erased sector 3 3'))
    ok(pfp('Info : erased sectors 0 3').count === 4 && pfp('Info : erased sectors 0 3').last === 3,
      'T15 erased sectors first last 区间计数', pfp('Info : erased sectors 0 3'))
    ok(pfp('erased 25 sectors').count === 25 && pfp('erased 25 sectors').last === -1,
      'T15 "N sectors erased" 汇总形态', pfp('erased 25 sectors'))
    ok(pfp('erased sectors 0..last on bank 0').type === 'erase',
      'T15 flash_erase_sector 汇总行识别为擦除', pfp('erased sectors 0..last on bank 0'))
    ok(pfp('wrote 139077 bytes from file D:/fw.hex in 1.234s').bytes === 139077,
      'T15 wrote N bytes 提取字节数', pfp('wrote 139077 bytes from file D:/fw.hex in 1.234s'))
    ok(pfp('verified 28288 bytes in 0.01s').bytes === 28288,
      'T15 verified N bytes 提取字节数', pfp('verified 28288 bytes in 0.01s'))
    ok(pfp('** Verified OK **').type === 'verify' && pfp('** Verified OK **').bytes === 0,
      'T15 ** Verified OK ** 识别为校验确认', pfp('** Verified OK **'))
    ok(pfp('nonsense line') === null && pfp('') === null && pfp(null) === null,
      'T15 无关行返回 null(静默跳过)')

    const fpg = mod._test.flashProgressOf
    ok(fpg([]).phase === 'idle', 'T15 空日志 phase=idle', fpg([]))
    const eraseLogs = ['Info : erased sector 0 3', 'Info : erased sector 4 7']
    ok(fpg(eraseLogs).phase === 'erase' && fpg(eraseLogs).sectors === 8,
      'T15 擦除段累计扇区数', fpg(eraseLogs))
    ok(fpg(eraseLogs.concat(['wrote 139077 bytes from file x'])).phase === 'write'
      && fpg(eraseLogs.concat(['wrote 139077 bytes from file x'])).bytes === 139077,
      'T15 写完后 phase=write 且带字节数')
    ok(fpg(['verified 28288 bytes in 0.01s']).phase === 'verify'
      && fpg(['verified 28288 bytes in 0.01s']).verified === 28288,
      'T15 verify_image 形态 phase=verify')
    ok(fpg(['** Verified OK **']).ok === true && fpg(['** Verified OK **']).phase === 'done',
      'T15 Verified OK 直接 phase=done', fpg(['** Verified OK **']))
    // rsp 也算进聚合:有的 openocd 版本把 Verified OK 带回 capture 响应
    const fpc = mod._test.flashProgressOfCall
    const fakeCall = { logs: ['wrote 139077 bytes from file x'], rsp: '** Programming Finished **\n** Verified OK **' }
    ok(fpc(fakeCall).ok === true && fpc(fakeCall).phase === 'done' && fpc(fakeCall).bytes === 139077,
      'T15 调用级进度把 rsp 计入(logs+rsp 双通道)', fpc(fakeCall))
    ok(mod._test.isFlashTool('flash_write') && mod._test.isFlashTool('flash_erase_sector')
      && !mod._test.isFlashTool('read_memory') && !mod._test.isFlashTool(undefined),
      'T15 isFlashTool 只认烧录两类')

    // T16 调用配对(控制通道串行 → 同 tool 的 cmd 后必跟 rsp/err)
    const pc = mod._test.pairCalls
    const mk = (seq, dir, tool, text) => ({ seq, dir, tool, text, ts: '2026-10-06T14:00:0' + (seq % 10) + '.000+08:00', tag: '', len: 1, dtMs: null })
    const paired = pc([
      mk(1, 'cmd', 'flash_write', 'program {fw.hex}'),
      mk(2, 'log', 'backend', 'Info : erased sector 0 3'),
      mk(3, 'rsp', 'flash_write', '** Verified OK **'),
    ])
    ok(paired.length === 1 && paired[0].kind === 'call' && paired[0].state === 'ok'
      && paired[0].logs.length === 1 && paired[0].cmd === 'program {fw.hex}' && paired[0].rsp.indexOf('Verified') >= 0,
      'T16 cmd→log→rsp 配对成一次调用(log 归入调用)', paired)
    const pairedErr = pc([mk(1, 'cmd', 'read_variable', 'mdw 0x20000010 1'), mk(2, 'err', 'read_variable', 'read timed out')])
    ok(pairedErr.length === 1 && pairedErr[0].state === 'err' && pairedErr[0].rsp === 'read timed out',
      'T16 err 应答配对为失败调用', pairedErr)
    const pairedOpen = pc([mk(1, 'cmd', 'flash_write', 'a'), mk(2, 'cmd', 'read_memory', 'b'), mk(3, 'rsp', 'read_memory', 'c')])
    ok(pairedOpen.length === 2 && pairedOpen[0].state === 'open' && pairedOpen[1].state === 'ok',
      'T16 未应答调用按 open 先结算,不吞下一条', pairedOpen.map((c) => c.state))
    const orphan = pc([mk(1, 'rsp', 'read_memory', 'orphan')])
    ok(orphan.length === 1 && orphan[0].kind === undefined,
      'T16 孤立 rsp 原样保留(不丢数据)', orphan)
    ok(pc([]).length === 0 && pc([{ kind: 'gap', seq: 9, count: 3 }]).length === 1,
      'T16 空流/gap 标记直通', pc([]))
    const dt = mod._test.callDtMs
    ok(dt({ ts: '2026-10-06T14:00:01.000+08:00', rspTs: '2026-10-06T14:00:04.250+08:00' }) === 3250,
      'T16 调用耗时 = rsp - cmd', dt({ ts: '2026-10-06T14:00:01.000+08:00', rspTs: '2026-10-06T14:00:04.250+08:00' }))
    ok(dt({ ts: 'x', rspTs: 'y' }) === null && dt({ ts: 'x' }) === null,
      'T16 坏/缺时间戳耗时记 null')

    // T17 会话分隔 / 折叠 / 工具清单 / 最近烧录调用
    const sep = mk(1, 'sys', 'backend', 'started openocd (pid 1)')
    ok(mod._test.isSessionSep(sep) && !mod._test.isSessionSep(mk(2, 'sys', 'connect', 'x'))
      && !mod._test.isSessionSep(mk(3, 'cmd', 'backend', 'y')) && !mod._test.isSessionSep(null),
      'T17 会话分隔只认 backend 的 sys 事件')
    const flow = [sep, mk(2, 'cmd', 'flash_write', 'a'), mk(3, 'rsp', 'flash_write', 'b'),
      mk(4, 'sys', 'backend', 'reused'), mk(5, 'rsp', 'read_memory', 'c')]
    const runs2 = mod._test.sessionRuns(flow)
    ok(runs2.get(1) === 2 && runs2.get(4) === 1, 'T17 会话 run 计数(到下一个分隔条前)', [runs2.get(1), runs2.get(4)])
    const collapsed = mod._test.applyCollapse(flow, new Set([1]))
    ok(collapsed.length === 3 && collapsed[0].seq === 1 && collapsed[1].seq === 4,
      'T17 折叠会话:分隔条保留、其余条目隐藏', collapsed.map((e) => e.seq))
    ok(mod._test.applyCollapse(flow, new Set()).length === 5, 'T17 空折叠集合原样返回')
    const to = mod._test.toolOptions(flow.concat([{ kind: 'gap', seq: 99, count: 1 }, mk(6, 'cmd', '', 'no-tool')]))
    ok(to.map((t) => t.name).join(',') === 'backend,flash_write,read_memory',
      'T17 工具清单按频次降序,跳过 gap/无 tool', to.map((t) => t.name + ':' + t.n))
    const callsFlow = mod._test.pairCalls(flow.concat([mk(6, 'cmd', 'flash_erase_sector', 'd'), mk(7, 'rsp', 'flash_erase_sector', 'e')]))
    ok(mod._test.lastFlashCall(callsFlow).tool === 'flash_erase_sector'
      && mod._test.lastFlashCall([{ kind: 'call', tool: 'read_memory' }]) === null
      && mod._test.lastFlashCall([]) === null,
      'T17 最近一次烧录类调用(非烧录流 → null)', mod._test.lastFlashCall(callsFlow).tool)

    // T17b v1.4.0 flashStillLatest:烧录进度「仍是最新动态」两种视图一套语义
    const fsl = mod._test.flashStillLatest
    const openCall = { kind: 'call', tool: 'flash_write', state: 'open', seq: 1 }
    const doneCall = { kind: 'call', tool: 'flash_write', state: 'ok', seq: 1, rspSeq: 2 }
    const staleCall = { kind: 'call', tool: 'flash_write', state: 'ok', seq: 1, rspSeq: 2, cmd: 'a', rsp: '', logs: [] }
    ok(fsl(null, []) === false, 'T17b 无烧录调用 → 不显示', fsl(null, []))
    ok(fsl(openCall, [openCall]) === true, 'T17b 进行中的烧录恒显示(两种视图)')
    ok(fsl(doneCall, [doneCall]) === true,
      'T17b 调用视图:末项就是该调用(对象同一性)→ 完成后仍显示')
    ok(fsl(doneCall, [mk(2, 'rsp', 'flash_write', 'b')]) === true,
      'T17b 流水视图:末行是该次烧录的应答行(seq 相等)→ 默认视图也有进度条')
    ok(fsl(staleCall, [mk(2, 'rsp', 'flash_write', 'b'), mk(3, 'cmd', 'read_memory', 'c')]) === false,
      'T17b 之后来了新调用 → 不再当最新动态')

    // T18 关键行标记(驱动行高亮)
    const rm = mod._test.rowMark
    ok(rm({ dir: 'rsp', text: '** Programming Finished **\n** Verified OK **' }) === 'ok',
      'T18 rsp 带 Verified OK → ok 标记', rm({ dir: 'rsp', text: '** Verified OK **' }))
    ok(rm({ dir: 'rsp', text: 'verified 28288 bytes in 0.01s' }) === 'ok',
      'T18 rsp 带 verified N bytes → ok 标记')
    ok(rm({ dir: 'err', text: 'control channel failed' }) === 'err',
      'T18 err 方向 → err 标记')
    ok(rm({ dir: 'log', text: 'Error: unable to find a matching CMSIS-DAP device' }) === 'err',
      'T18 log 行 Error: 开头 → err 标记')
    ok(rm({ dir: 'rsp', text: '0x08000000: 20019d28 080002ad' }) === null
      && rm({ kind: 'gap' }) === null && rm(null) === null,
      'T18 普通行/gap/空值无标记')

    // T19 链路状态解析(v1.2.1/v1.2.2):探针 / 芯片 / Flash
    const ppn = mod._test.parseProbeNote
    // 真实形态:整条文案被括号包着——cfg token 后紧跟 ')' 也不能吞进去
    const p1 = ppn('started openocd (pid 4711); command port 127.0.0.1:6666 is up (probe auto-detected: CMSIS-DAP -> interface/cmsis-dap.cfg (evidence: 0D28:0204))')
    ok(p1 && p1.kind === 'CMSIS-DAP' && p1.cfg === 'interface/cmsis-dap.cfg' && p1.evidence === '0D28:0204' && p1.pinned === false,
      'T19 探针识别文案三要素(kind/cfg/evidence,括号不吞)', p1)
    const p2 = ppn('started openocd; command port 6666 is up (probe auto-detected: ST-Link -> interface/stlink.cfg)')
    ok(p2 && p2.kind === 'ST-Link' && p2.cfg === 'interface/stlink.cfg' && p2.evidence === '',
      'T19 无 evidence 且右括号紧贴 cfg 也解析', p2)
    // 钉死 interface_cfg(v1.2.2):'…is up (interface: interface/cmsis-dap.cfg)'
    const p3 = ppn('started openocd; command port 6666 is up (interface: interface/cmsis-dap.cfg)')
    ok(p3 && p3.pinned === true && p3.kind === '固定配置' && p3.cfg === 'interface/cmsis-dap.cfg' && p3.evidence === '',
      'T19 固定配置模式(知道脚本名,未识别物理型号)', p3)
    ok(ppn('using the OpenOCD listener already present on 127.0.0.1:6666') === null
      && ppn('') === null && ppn(null) === null,
      'T19 复用后端/空文本返回 null(不显示探针)')
    const bn = mod._test.baseName
    ok(bn('interface/cmsis-dap.cfg') === 'cmsis-dap.cfg' && bn('D:\\ocd\\scripts\\interface\\stlink.cfg') === 'stlink.cfg'
      && bn('plain') === 'plain' && bn('') === '',
      'T19 baseName 两种分隔符 + 无分隔符', [bn('interface/cmsis-dap.cfg'), bn('a\\b.cfg')])

    const chipRsp = [
      'chip identity:',
      '  cpu (CPUID, ARM standard) @ 0xE000ED00: 0x410fc241',
      '    -> Cortex-M4 r0p1',
      '  dev_id (vendor register) @ 0xE0042000: 0x10006413',
      '    -> dev_id 0x413, rev_id 0x1000',
      '    -> family: AT32F403A',
      'next: flash_info cross-checks the probed flash size',
    ].join('\n')
    const ci = mod._test.parseChipIdentity(chipRsp)
    ok(ci && ci.core === 'Cortex-M4 r0p1' && ci.devId === '0x413' && ci.revId === '0x1000' && ci.family === 'AT32F403A',
      'T19 identify_chip 报告四字段(core/dev_id/rev_id/family)', ci)
    const ci2 = mod._test.parseChipIdentity('chip identity:\n  cpu (CPUID, ARM standard) @ 0xE000ED00: 0x411fc241\n    -> Cortex-M4 r0p0\n    -> family: unknown - add it to "chip_id_map" in ocd.json (key = DEV_ID hex)')
    ok(ci2 && ci2.core === 'Cortex-M4 r0p0' && /unknown/i.test(ci2.family),
      'T19 未配 chip_id_map 时 family=unknown 仍解析', ci2)
    ok(mod._test.parseChipIdentity('') === null && mod._test.parseChipIdentity('garbage') === null,
      'T19 非芯片报告返回 null')

    const fiRsp = '0 : at32f435zmt7.flash (at32f435xx) at 0x08000000, size 0x00100000, buswidth 2, chipwidth 4\n'
      + '1 : at32f435zmt7.flash (at32f435xx) at 0x1fffc000, size 0x004000, buswidth 2, chipwidth 4'
    const fi = mod._test.parseFlashInfo(fiRsp)
    ok(fi && fi.banks.length === 2 && fi.first.bank === 0 && fi.first.base === '0x08000000' && fi.first.size === 0x100000,
      'T19 flash_info 双 bank 解析(首 bank + 总数)', fi && fi.banks.map((b) => b.bank))
    ok(fi && fi.total === 0x104000,
      'T19 flash_info total = 各 bank 之和(v1.3.1 卡片报合计,不是首 bank)', fi && fi.total)
    const fiDual = mod._test.parseFlashInfo('0 : at32f403axx.flash (at32f403axx) at 0x08000000, size 0x00080000, buswidth 0, chipwidth 0\n'
      + '1 : at32f403axx.flash (at32f403axx) at 0x08080000, size 0x00080000, buswidth 0, chipwidth 0')
    ok(fiDual && fiDual.total === 0x100000 && mod._test.fmtKb(fiDual.total) === '1024 KB',
      'T19 双 bank 各 512K → total 1024K(at32f403axG 实况)', fiDual && mod._test.fmtKb(fiDual.total))
    ok(mod._test.parseFlashInfo('no banks here') === null && mod._test.parseFlashInfo('') === null,
      'T19 非 flash_info 文本返回 null')

    const fs2 = mod._test.fmtSize
    ok(fs2(0x100000) === '1 MB' && fs2(0x40000) === '256 KB' && fs2(512) === '512 B' && fs2(0) === '0 B'
      && fs2(null) === '' && fs2(1536 * 1024) === '1.5 MB',
      'T19 fmtSize 三档(整 MB 不带小数)', [fs2(0x100000), fs2(0x40000), fs2(512), fs2(1536 * 1024)])

    const mkLink = (seq, dir, tool, text) => ({ seq, dir, tool, text, ts: '2026-10-06T15:00:00.000+08:00', tag: '', len: 1, dtMs: null })
    const linkFlow = [
      mkLink(1, 'sys', 'backend', 'probe auto-detected: AT-Link -> interface/atlink_dap_v2.cfg (evidence: 0x0B6E:0001)'),
      mkLink(2, 'rsp', 'flash_info', '0 : at32f403a.flash (at32f403axx) at 0x08000000, size 0x00100000, buswidth 2, chipwidth 4'),
      mkLink(3, 'rsp', 'identify_chip', 'chip identity:\n  cpu (CPUID, ARM standard) @ 0xE000ED00: 0x410fc241\n    -> Cortex-M4 r0p1\n    -> dev_id 0x410\n    -> family: AT32F403A'),
      mkLink(4, 'rsp', 'identify_chip', 'chip identity:\n  cpu (CPUID, ARM standard) @ 0xE000ED00: 0x412fc24\n    -> Cortex-M4 r0p0\n    -> dev_id 0x456\n    -> family: AT32F403A'),
      { kind: 'gap', seq: 9, count: 2 },
    ]
    const li = mod._test.deriveLinkInfo(linkFlow)
    ok(li.probe && li.probe.kind === 'AT-Link' && li.flash && li.flash.first.size === 0x100000
      && li.chip && li.chip.devId === '0x456' && li.chip.core === 'Cortex-M4 r0p0',
      'T19 deriveLinkInfo 三项齐(芯片取最近一条)', { probe: li.probe && li.probe.kind, chip: li.chip && li.chip.devId, flash: li.flash && li.flash.first.size })
    const liEmpty = mod._test.deriveLinkInfo([])
    ok(liEmpty.probe === null && liEmpty.chip === null && liEmpty.flash === null,
      'T19 空流三项皆 null')
    ok(mod._test.deriveLinkInfo([mkLink(1, 'rsp', 'identify_chip', 'garbage')]).chip === null,
      'T19 解析失败不写坏状态')

    // v1.2.2:钉死 interface_cfg 的探针形态;connect 自动识别的芯片镜像(tool=connect 也认)
    const liPinned = mod._test.deriveLinkInfo([
      mkLink(1, 'sys', 'backend', 'started openocd (pid 1234); command port 127.0.0.1:6666 is up (interface: interface/cmsis-dap.cfg)'),
      mkLink(2, 'rsp', 'connect', 'connected to OpenOCD on 127.0.0.1:6666\ntargets:\n0* py32f002a.cpu (cortex_m, little) running\nchip identity:\n  cpu (CPUID, ARM standard) @ 0xE000ED00: 0x410cc601\n    -> Cortex-M0+ r0p1\n  dev_id (vendor register) @ 0xE0042000: 0x1000abcd\n    -> dev_id 0xbc0, rev_id 0x1000\n    -> family: unknown - add it to "chip_id_map" in ocd.json (key = DEV_ID hex)'),
    ])
    ok(liPinned.probe && liPinned.probe.pinned === true && liPinned.probe.cfg === 'interface/cmsis-dap.cfg',
      'T19 钉死模式:探针段显示接口脚本', liPinned.probe)
    ok(liPinned.chip && liPinned.chip.core === 'Cortex-M0+ r0p1' && liPinned.chip.devId === '0xbc0' && liPinned.flash === null,
      'T19 connect 自动识别的芯片镜像(tool=connect 的 rsp 也解析)', liPinned.chip)
    ok(liPinned.chip.family.indexOf('unknown') === 0, 'T19 family=unknown 原样带出(ocd.json 未配 chip_id_map)')

    // v1.4.0：启动失败文案里也嵌 probeNote(BuildFailureMessage 带上次选了哪个
    // 接口脚本)——照解析会让链路行「探针」段在 connect 失败后显示一个像活着的链路
    const liFail = mod._test.deriveLinkInfo([
      mkLink(1, 'sys', 'backend', 'ERROR: D:/atopenocd/bin/openocd.exe did not become ready: unable to find a matching CMSIS-DAP device\ninterface: interface/cmsis-dap.cfg\n--- recent openocd output ---\nError: unable to find a matching CMSIS-DAP device'),
    ])
    ok(liFail.probe === null,
      'T19b 失败 SYS 行不解析探针(链路行不假装活着)', liFail.probe)
    const liFailAfterOk = mod._test.deriveLinkInfo([
      mkLink(1, 'sys', 'backend', 'started openocd (pid 1) (probe auto-detected: AT-Link -> interface/atlink_dap_v2.cfg (evidence: 0x0B6E:0001))'),
      mkLink(2, 'sys', 'backend', 'ERROR: openocd did not become ready: the command port did not come up\nprobe auto-detected: AT-Link -> interface/atlink_dap_v2.cfg (evidence: 0x0B6E:0001)'),
    ])
    ok(liFailAfterOk.probe && liFailAfterOk.probe.kind === 'AT-Link',
      'T19b 失败行被跳过,探针段保留上一次成功识别的结果', liFailAfterOk.probe)

    // v1.4.0：芯片段回退标签——只剩 dev_id(CPUID 读失败)时显示 dev id,不再写死「已连接」
    const cfl = mod._test.chipFallbackLabel
    ok(cfl({ devId: '0xbc0', core: '' }) === 'dev 0xbc0'
      && cfl({ devId: '0x0', core: '' }) === '已连接'
      && cfl({ devId: '0x000', core: '' }) === '已连接'
      && cfl(null) === '已连接' && cfl({ core: '' }) === '已连接',
      'T19b 芯片卡回退:有效 dev_id → dev id;假 dev/空 → 已连接', [cfl({ devId: '0xbc0' }), cfl(null)])

    // v1.2.3:探针硬件名 / 芯片型号 / Flash KB / 无效 dev 隐藏
    const fpn = mod._test.friendlyProbeName
    ok(fpn('interface/cmsis-dap.cfg') === 'CMSIS-DAP'
      && fpn('cmsis_dap.cfg') === 'CMSIS-DAP'
      && fpn('D:\\ocd\\scripts\\interface\\jlink.cfg') === 'J-Link'
      && fpn('interface/stlink-v2-1.cfg') === 'ST-Link'
      && fpn('interface/atlink_dap_v2.cfg') === 'AT-Link'
      && fpn('interface/busblaster.cfg') === 'Busblaster'
      && fpn('') === '' && fpn(null) === '',
      'T20 接口脚本名 → 烧录器类型名(探针段不再是文件名)', [fpn('interface/cmsis-dap.cfg'), fpn('interface/jlink.cfg'), fpn('interface/stlink-v2-1.cfg'), fpn('interface/atlink_dap_v2.cfg')])
    const pl = mod._test.probeLabel
    ok(pl({ pinned: true, cfg: 'interface/cmsis-dap.cfg' }) === 'CMSIS-DAP'
      && pl({ pinned: false, kind: 'CMSIS-DAP/DAPLink', cfg: 'interface/cmsis-dap.cfg' }) === 'CMSIS-DAP/DAPLink'
      && pl({ pinned: true, cfg: 'interface/atlink_dap_v2.cfg' }) === 'AT-Link'
      && pl({ pinned: true, cfg: '' }) === ''
      && pl(null) === '',
      'T20 probeLabel:钉死模式映射脚本名,auto 模式用认出的 kind')

    const chipModelRsp = [
      'chip identity:',
      '  cpu (CPUID, ARM standard) @ 0xE000ED00: 0x410cc601',
      '    -> Cortex-M0+ r0p1',
      '  dev_id (vendor register) @ 0xE0042000: 0xe0042000',
      '    -> dev_id 0x000, rev_id 0x0000',
      '    -> family: unknown - add it to "chip_id_map" in ocd.json (key = DEV_ID hex)',
      '  model (from target_cfg target_py32f002a_lock.cfg): PY32F002A',
      'next: flash_info cross-checks the probed flash size',
    ].join('\n')
    const ci3 = mod._test.parseChipIdentity(chipModelRsp)
    ok(ci3 && ci3.model === 'PY32F002A' && ci3.modelFrom === 'target_py32f002a_lock.cfg',
      'T20 identify_chip 新行:model(from target_cfg)两字段', ci3 && [ci3.model, ci3.modelFrom])
    const cl = mod._test.chipLabel
    const labModel = cl(ci3)
    ok(labModel && labModel.text === 'Cortex-M0+ r0p1' && labModel.identified === false,
      'T20 芯片段:cfg 推定的 model 不上卡(配错就是误导),只报实测内核', labModel)
    const clUnknown = cl({ core: 'Cortex-M0+ r0p1', family: 'unknown - add it to "chip_id_map" in ocd.json (key = DEV_ID hex)', devId: '0x000', revId: '0x0000' })
    ok(clUnknown && clUnknown.identified === false && clUnknown.text === 'Cortex-M0+ r0p1',
      'T20 芯片段:认不出时 identified=false(界面补「型号未识别」)', clUnknown)
    ok(mod._test.isBogusDev('0x000') === true && mod._test.isBogusDev('0x410') === false
      && mod._test.isBogusDev('') === true && mod._test.isBogusDev('0xfff') === false,
      'T20 dev 0x000 判无效(寄存器不存在时读回了地址本身)')
    const tipUnknown = mod._test.chipTip({ core: 'Cortex-M0+ r0p1', family: 'unknown - add it to "chip_id_map" in ocd.json (key = DEV_ID hex)', devId: '0x000', revId: '0x0000' })
    ok(!/DEV_ID: /.test(mod._test.chipTip(ci3)) && /target_cfg/.test(mod._test.chipTip(ci3))
      && /chip_id_map/.test(mod._test.chipTip(ci3))
      && !/DEV_ID: /.test(tipUnknown) && /chip_id_map/.test(tipUnknown),
      'T20 芯片 tooltip:无效 dev 不显示,model 标注「仅配置线索」+ 填 chip_id_map 指引', mod._test.chipTip(ci3))

    // v1.2.4:内核名缩写(CPUID 解码串太长,卡片只留「Cortex-M4 r0p1」)
    const cs = mod._test.coreShort
    ok(cs("Cortex-M4 (ARMv7-M/v8-M, implementer 'A', r0p1)") === 'Cortex-M4 r0p1'
      && cs('Cortex-M0+ r0p1') === 'Cortex-M0+ r0p1'
      && cs("Cortex-M (partno 0xed)") === 'Cortex-M'
      && cs('') === '' && cs(null) === '',
      'T21 coreShort:括号里的架构细节进 tooltip,卡片留短名', [cs("Cortex-M4 (ARMv7-M/v8-M, implementer 'A', r0p1)"), cs("Cortex-M (partno 0xed)")])
    const clLong = cl({ core: "Cortex-M4 (ARMv7-M/v8-M, implementer 'A', r0p1)", devId: '0x344', revId: '0x7005', family: 'unknown - add it to "chip_id_map" in ocd.json (key = DEV_ID hex)', model: 'AT32F403A', modelFrom: 'target_at32f403a.cfg' })
    ok(clLong && clLong.text === 'Cortex-M4 r0p1 · dev 0x344' && clLong.identified === false,
      'T21 芯片段:实测内核 + 有效 dev_id;cfg 型号不出现在卡片', clLong)
    const clFam = cl({ core: 'Cortex-M4 (ARMv7-M/v8-M, implementer ' + "'A'" + ', r0p1)', devId: '0x344', revId: '0x7005', family: 'STM32F334', model: 'AT32F403A' })
    ok(clFam && clFam.text === 'STM32F334 · Cortex-M4 r0p1' && clFam.identified === true,
      'T21 芯片段:chip_id_map 命中时 family 才是卡片主值', clFam)

    const fk = mod._test.fmtKb
    ok(fk(0x100000) === '1024 KB' && fk(0x40000) === '256 KB' && fk(0) === '待探测' && fk(null) === '' && fk(1536) === '2 KB',
      'T20 fmtKb:Flash 段只写容量(size 0 = 未探测,不显示误导的 0 KB)', [fk(0x100000), fk(0x40000), fk(0)])

    // v1.2.3 端到端:截图同款流量(钉死探针 + dev 无效 + model 推定 + flash_info)
    const li123 = mod._test.deriveLinkInfo([
      mkLink(1, 'sys', 'backend', 'started openocd (pid 275); command port 127.0.0.1:6666 is up (interface: interface/cmsis-dap.cfg)'),
      mkLink(2, 'rsp', 'flash_info', '0 : py32f002a.flash (py32f002axx) at 0x08000000, size 0x00100000, buswidth 2, chipwidth 4'),
      mkLink(3, 'rsp', 'connect', 'connected to OpenOCD on 127.0.0.1:6666\nchip identity:\n  cpu (CPUID, ARM standard) @ 0xE000ED00: 0xe0042000\n    -> Cortex-M (partno 0xed)\n  dev_id (vendor register) @ 0xE0042000: 0xe0042000\n    -> dev_id 0x000, rev_id 0x0000\n    -> family: unknown - add it to "chip_id_map" in ocd.json (key = DEV_ID hex)\n  model (from target_cfg target_py32f002a_lock.cfg): PY32F002A'),
    ])
    ok(mod._test.probeLabel(li123.probe) === 'CMSIS-DAP'
      && li123.chip && li123.chip.model === 'PY32F002A'
      && mod._test.fmtKb(li123.flash.first.size) === '1024 KB',
      'T20 deriveLinkInfo 端到端:探针类型名 + 型号推定 + Flash KB',
      { probe: mod._test.probeLabel(li123.probe), chip: li123.chip && li123.chip.model, flash: mod._test.fmtKb(li123.flash.first.size) })

    // v1.3.0:target 失配行解析(ocdup v0.4.0 的 identify/connect rsp 末行)
    const mm = mod._test.parseTargetMismatch
    const mmText = 'chip identity:\n'
      + '  cpu (CPUID, ARM standard) @ 0xE000ED00: 0x410fc241\n'
      + '    -> Cortex-M4 (ARMv7-M/v8-M, implementer ' + "'A'" + ', r0p1)\n'
      + '  dev_id (vendor register) @ 0xE0042000: 0x70050344\n'
      + '    -> dev_id 0x344, rev_id 0x7005\n'
      + '    -> family: AT32F403A\n'
      + '  target mismatch: identified AT32F403A, current target_cfg: D:/work/Ocdup/target_py32f002a_lock.cfg -> use set_target AT32F403A\n'
      + 'next: flash_info cross-checks the probed flash size'
    const m1 = mm(mmText)
    ok(m1 && m1.family === 'AT32F403A' && m1.action === 'AT32F403A'
      && m1.current === 'D:/work/Ocdup/target_py32f002a_lock.cfg',
      'T22 失配行三要素(家族/当前 target/建议动作)', m1)
    ok(mm('chip identity:\n    -> family: unknown - add it to "chip_id_map" in ocd.json') === null
      && mm('') === null && mm(null) === null,
      'T22 无失配行 → null(不显示胶囊)')
    const liMm = mod._test.deriveLinkInfo([
      mkLink(1, 'sys', 'backend', 'started openocd; command port 6666 is up (interface: interface/cmsis-dap.cfg)'),
      mkLink(2, 'rsp', 'connect', 'connected to OpenOCD on 127.0.0.1:6666\nchip identity:\n    -> family: AT32F403A\n  target mismatch: identified AT32F403A, current target_cfg: target_py32f002a_lock.cfg -> use set_target AT32F403A'),
    ])
    ok(liMm.mismatch && liMm.mismatch.action === 'AT32F403A' && liMm.chip && liMm.chip.family === 'AT32F403A',
      'T22 deriveLinkInfo 同时解析失配与芯片(tool=connect 的 rsp 也认)', liMm.mismatch)
    // 最近一次优先:新流量里没有失配行 → 不显示(即使历史上有)
    ok(mod._test.deriveLinkInfo([mkLink(1, 'rsp', 'connect', mmText), mkLink(2, 'rsp', 'identify_chip', 'chip identity:\n    -> family: AT32F403A')]).mismatch === null,
      'T22 失配胶囊按最近一次判定(新 identify 无失配则不显示)')

    // T23 v1.3.2 界面层:方向图例(=方向过滤器)/ 计数文案 / 芯片读数分层
    const dl = mod._test.dirLegend
    const legend = dl({ cmd: 69, rsp: 78, sys: 14, log: 159, err: 0 })
    ok(legend.length === 4 && legend.map((d) => d.key).join(',') === 'cmd,rsp,sys,log'
      && legend[0].label === 'cmd' && legend[0].color === '#d97706' && legend[0].n === 69
      && legend[2].label === 'sys',
      'T23 方向图例:有量的方向按固定顺序成 chip(err=0 不出,sys 不再大写)', legend.map((d) => d.label + d.n))
    ok(dl(null).length === 0 && dl({ cmd: 0, rsp: 0, sys: 0, log: 0, err: 0 }, 'ALL').length === 0,
      'T23 无流量/ALL 不产生 chip')
    const legendActive = dl({ cmd: 0, rsp: 0, sys: 0, log: 0, err: 0 }, 'sys')
    ok(legendActive.length === 1 && legendActive[0].key === 'sys' && legendActive[0].n === 0,
      'T23 当前生效的方向即使归零也留着(否则取消不掉该过滤)', legendActive)

    const cl2 = mod._test.counterLabel
    ok(cl2(300, 320, 320) === '320 条 · 渲染 300', 'T23 计数:未过滤但渲染截断', cl2(300, 320, 320))
    ok(cl2(120, 120, 320) === '120 / 320 条' && cl2(5000, 5000, 5000) === '5000 条',
      'T23 计数:过滤后无截断 / 全量单调', [cl2(120, 120, 320), cl2(5000, 5000, 5000)])

    const clHead = mod._test.chipLabel({ core: "Cortex-M4 (ARMv7-M/v8-M, implementer 'A', r0p1)", devId: '0x344', family: 'AT32F403A' })
    ok(clHead.head === 'AT32F403A' && clHead.tail === 'Cortex-M4 r0p1' && clHead.identified === true
      && clHead.text === 'AT32F403A · Cortex-M4 r0p1',
      'T23 芯片读数分层:head=型号(主读数)/tail=内核(窄面板整段隐藏)', clHead)
    const clHead2 = mod._test.chipLabel({ core: 'Cortex-M4 r0p1', devId: '0x344', family: 'unknown - add it to "chip_id_map"' })
    ok(clHead2.head === 'Cortex-M4 r0p1' && clHead2.tail === 'dev 0x344' && clHead2.identified === false,
      'T23 型号未识别:head=内核 / tail=dev id(界面改用警示色图标)', clHead2)

    // v1.3.4 后端状态派生(链路行第四段「后端」断开/恢复):backend SYS 行 → stopped/running
    const bs2 = mod._test.backendStateOf
    const mkBackend = (seq, text) => ({ seq, dir: 'sys', tool: 'backend', text, ts: '2026-10-08T12:00:00.000+08:00', tag: '', len: 1, dtMs: null })
    ok(bs2([mkBackend(1, 'started openocd (pid 4711); command port 127.0.0.1:6666 is up')]) === 'running',
      'T23 后端状态:started openocd → running')
    ok(bs2([mkBackend(1, 'using the OpenOCD listener already present on 127.0.0.1:6666')]) === 'running',
      'T23 后端状态:复用已有监听 → running')
    ok(bs2([mkBackend(1, 'started openocd'), mkBackend(2, 'backend stopped - the probe behind 127.0.0.1:6666 is released')]) === 'stopped',
      'T23 后端状态:disconnect 的 SYS 行 → stopped(取最近一条)')
    ok(bs2([mkBackend(1, 'started openocd'), mkBackend(2, 'backend was not running (nothing on 127.0.0.1:6666) - nothing to disconnect.')]) === 'stopped',
      'T23 后端状态:「本来就没跑」也是 stopped(不能回退读成已连接)')
    ok(bs2([mkBackend(1, 'backend stopped - released'), mkBackend(2, 'started openocd (pid 99)')]) === 'running',
      'T23 后端状态:重新 connect 后又回 running')
    ok(bs2([]) === null && bs2([mkBackend(1, 'unrelated chatter')]) === null,
      'T23 后端状态:没有 backend 行 → null(界面显示「未知」)')

    // v1.4.0:失败类 SYS 行也是断开态——此前只认成功形态,拔线/换错 cfg 后再调
    // 硬件工具,循环会跳过失败行扫到更早的「started openocd」→ 卡片显示陈旧「已连接」
    ok(bs2([mkBackend(1, 'started openocd (pid 1)'), mkBackend(2, 'ERROR: D:/atopenocd/bin/openocd.exe did not become ready: unable to find a matching CMSIS-DAP device')]) === 'stopped',
      'T23b 启动失败(did not become ready)→ stopped(不再显示陈旧已连接)')
    ok(bs2([mkBackend(1, 'started openocd'), mkBackend(2, 'ERROR: nothing is listening on 127.0.0.1:6666. Call connect to start the backend')]) === 'stopped',
      'T23b 端口无监听(nothing is listening)→ stopped')
    ok(bs2([mkBackend(1, 'started openocd'), mkBackend(2, 'ERROR: cannot open the OpenOCD TCL-RPC channel on 127.0.0.1:6666: connection refused')]) === 'stopped',
      'T23b TCL-RPC 通道打不开 → stopped')
    ok(bs2([mkBackend(1, 'started openocd'), mkBackend(2, 'heartbeat lost (no TCL-RPC response within 60s) - the backend appears gone')]) === 'stopped',
      'T23b 心跳丢失 → stopped(后端没了,下次调用自动重连)')
    ok(bs2([mkBackend(1, 'backend stopped'), mkBackend(2, 'backend on 127.0.0.1:6666 is still running and ocdup cannot stop it: that listener was started outside ocdup')]) === 'running',
      'T23b 外部实例杀不动(still running)→ running(监听确实还活着)')
    ok(bs2([mkBackend(1, 'ERROR: did not become ready'), mkBackend(2, 'started openocd (pid 9); command port 127.0.0.1:6666 is up')]) === 'running',
      'T23b 失败后又成功启动 → 回到 running(取最近一条)')

    // v1.3.5 结论压缩:卡片只放一个词,完整文案进 tooltip / 流水镜像
    const bv = mod._test.backendVerdict
    ok(bv('disconnect', true, 'backend stopped - the probe behind 127.0.0.1:6666 is released for other software.').label === '已断开'
      && bv('disconnect', true, 'backend stopped').tone === 'ok',
      'T23 断开结论:真断了 → 已断开', bv('disconnect', true, 'backend stopped'))
    ok(bv('disconnect', true, 'backend was not running (nothing on 127.0.0.1:6666) - nothing to disconnect.').label === '未运行'
      && bv('disconnect', true, 'backend was not running').tone === 'warn',
      'T23 断开结论:本来就没跑 → 未运行(警示色,不是「已断开」)')
    ok(bv('disconnect', true, 'backend on 127.0.0.1:6666 is still running and ocdup cannot stop it').label === '未释放'
      && bv('disconnect', true, 'still running').tone === 'warn',
      'T23 断开结论:外部实例杀不动 → 未释放(不能说成已断开)')
    ok(bv('connect', true, 'connected to OpenOCD on 127.0.0.1:6666\nversion: ...').label === '已连接',
      'T23 恢复结论:connect 成功 → 已连接')
    ok(bv('disconnect', false, '失败:the host tools service is unavailable').label === '失败'
      && bv('disconnect', false, 'x').tone === 'err',
      'T23 路由/工具调用失败 → 失败(危险色)')

    // T24 面板整体渲染冒烟(stub React 下真跑一遍组件体):改工具栏/链路读数条
    // 时最容易出的是渲染期异常——这里至少保证空数据 + 有条目两条路径都不炸,
    // 且改版后的控件语言还在(chip 过滤 / 内嵌正则 / 无旧分隔线)
    // 树序列化:函数组件就地展开(v1.3.7 起每个函数组件拿一份自己的 hook
    // 作用域,子组件重渲染不污染主组件的 hooks)
    const flatten = (el, out) => {
      out = out || []
      if (el === null || el === undefined || el === false) return out
      if (Array.isArray(el)) { for (const x of el) flatten(x, out); return out }
      if (typeof el !== 'object') { out.push(el); return out }
      // 函数组件就地展开(stub React 不调度,这里手动走一层)
      if (typeof el.type === 'function') {
        scopeStack.push(newScope())
        const expanded = el.type(Object.assign({}, el.props, { children: el.children }))
        scopeStack.pop()
        return flatten(expanded, out)
      }
      out.push(el)
      flatten(el.children, out)
      return out
    }
    const btnTexts = (n) => flatten(n).filter((x) => typeof x === 'string').join('')

    // T25 注册契约(v1.3.4:右侧停靠 tab,左侧图标移除):
    //   * ctx.sidebarRightTabs.register({ id, kind, multiple, title, keepMounted,
    //     guide }) 声明 tab 类型(照 ui-sidebar-terminal 模板,guide 不需要 commandId)
    //   * body 注册在 sidebar.right.pane.tab,**key = 同一个 id**
    //   * 左侧 sidebar.panellist 一个都不注册(用户不要左侧入口)
    //   * 宿主没有右栏服务时退回 main 全局面板(v1.3.3 形态,id 同值)
    const calls = []
    const registered = {}
    const tabDefs = []
    const slotsApi = {
      inject: (name, fn) => { calls.push(['inject', name]); fn() },
      register: (spec, comp) => { calls.push(['register', spec.name, spec.key || spec.id]); registered[spec.name + '|' + (spec.key || spec.id)] = comp },
    }
    const ctxRight = {
      get: (n) => (n === 'slots' ? slotsApi
        : n === 'sidebarRightTabs' ? { register: (d) => tabDefs.push(d) }
          : n === 'sidebarRight' ? { openTab: () => {} }
            : undefined),
      effect: (fn) => fn(),
    }
    mod.apply(ctxRight)
    const TAB_ID = 'dsh-ocdup-panel'
    ok(Array.isArray(mod.inject) && mod.inject.includes('slots')
      && mod.inject.includes('sidebarRight') && mod.inject.includes('sidebarRightTabs'),
      'T25 inject 声明 slots + 两个右栏服务', mod.inject)
    ok(tabDefs.length === 1 && tabDefs[0].id === TAB_ID && tabDefs[0].kind === 'ocdup-log'
      && tabDefs[0].multiple === false && tabDefs[0].keepMounted === true,
      'T25 右栏 tab 类型(id/kind/单例/keepMounted)', tabDefs[0])
    ok(Array.isArray(tabDefs[0].guide) && tabDefs[0].guide.length === 1
      && tabDefs[0].guide[0].id === 'new' && typeof tabDefs[0].guide[0].title === 'function'
      && typeof tabDefs[0].guide[0].icon === 'function',
      'T25 guide 入口卡(无 commandId,terminal 同款)', tabDefs[0].guide)
    ok(!!registered['sidebar.right.pane.tab|' + TAB_ID],
      'T25 pane.tab body 的 key 与 tab id 同值(openTab 认 kind,body 认 id)', Object.keys(registered))
    ok(!calls.some((c) => c[1] === 'sidebar.panellist'), 'T25 左侧图标不再注册(用户不要左侧入口)')
    ok(!calls.some((c) => c[0] === 'register' && c[1] === 'main'), 'T25 右栏分支不注册 main')

    // 兜底:宿主没有右栏服务 → main 全局面板(key 与图标 id 同值)
    const pane = registered['sidebar.right.pane.tab|dsh-ocdup-panel']   // 先取引用,下面要清空重放
    calls.length = 0
    for (const k of Object.keys(registered)) delete registered[k]
    mod.apply({ get: (n) => (n === 'slots' ? slotsApi : undefined), effect: (fn) => fn() })
    ok(!!registered['main|ocdup-log'], 'T25 无右栏服务时退回 main 全局面板(id 同值)', Object.keys(registered))
    ok(!calls.some((c) => c[1] === 'sidebar.right.pane.tab'), 'T25 兜底分支不注册右栏 body')

    ok(/\.op-link-grow\b/.test(injectedStyles.text) && /560px/.test(injectedStyles.text),
      'T12 窄面板收回芯片卡 grow(链路行不被撑成 3 行)', injectedStyles.text)
    ok(/op-row/.test(injectedStyles.text) && /640px/.test(injectedStyles.text)
      && /@container op-panel/.test(injectedStyles.text)
      && /op-dir-rsp/.test(injectedStyles.text) && /op-mark-ok/.test(injectedStyles.text)
      && /op-flow/.test(injectedStyles.text) && /op-session/.test(injectedStyles.text),
      'T12 一次性样式表注入(容器查询列降级 + rsp 底纹/关键行/进度流光/会话条)', injectedStyles.text)
    ok(/\.op-colhead\b/.test(injectedStyles.text) && /position: sticky/.test(injectedStyles.text),
      'T12 列头条样式(sticky 通道表)', injectedStyles.text)
    // v1.3.2 新皮相:过滤 chip(墨色 engaged)/单行工具条(横滚 + 渐隐)/搜索框内嵌
    // 正则开关/窄面板隐藏的补充读数——旧的 .op-gsep 分隔线已随布局改版删除
    ok(/\.op-chip\b/.test(injectedStyles.text) && /\.op-strip\b/.test(injectedStyles.text)
      && /\.op-affix\b/.test(injectedStyles.text) && /scrollbar-width: none/.test(injectedStyles.text),
      'T12 chip/单行工具条/正则开关样式', injectedStyles.text)
    ok(/\.op-chip-core\b/.test(injectedStyles.text) && /620px/.test(injectedStyles.text),
      'T12 窄面板隐藏链路补充读数(.op-chip-core)', injectedStyles.text)
    ok(/white-space: nowrap/.test(injectedStyles.text) && /var\(--dsw-alias-label-primary, #222\)/.test(injectedStyles.text),
      'T12 控件不折行 + engaged 墨色实底(不再用蓝底)', injectedStyles.text)
    ok(!/\.op-gsep\b/.test(injectedStyles.text) && !/\.op-stats-text\b/.test(injectedStyles.text),
      'T12 旧版分隔线/胶囊内计数样式已移除', injectedStyles.text)
    ok(/\.op-switch\b/.test(injectedStyles.text),
      'T12 失配切换胶囊样式(警示色)', injectedStyles.text)

    // T24 面板整体渲染冒烟(stub React 下真跑一遍组件体):改工具栏/链路读数条时
    // 最容易出的是渲染期异常——这里至少保证空数据路径不炸,且改版后的控件语言还在
    // T24 面板整体渲染冒烟:链路行「后端」段(断开/恢复)也要在树里
    ok(typeof pane === 'function', 'T24 右栏 pane 体可取到')
    let tree = null
    let threw = ''
    try { tree = pane({}) } catch (e) { threw = e.message }
    const nodes = flatten(tree)
    const texts = nodes.filter((n) => typeof n === 'string')
    const classes = nodes.map((n) => (n && n.props && n.props.className) || '').join(' ')
    ok(!threw, 'T24 空数据渲染不抛异常', threw)
    ok(texts.indexOf('流水') >= 0 && texts.indexOf('调用') >= 0 && texts.indexOf('暂停') >= 0 && texts.indexOf('跟随') >= 0,
      'T24 工具栏控件在位(流水/调用/暂停/跟随)', texts.slice(0, 12))
    const input = nodes.find((n) => n && n.type === 'input' && n.props && n.props.className === 'op-search')
    ok(input && input.props.placeholder === '过滤 命令 / 响应',
      'T24 搜索框占位文案(工具过滤交给 chip 条)', input && input.props.placeholder)
    ok(classes.indexOf('op-affix') >= 0 && classes.indexOf('op-btn') >= 0,
      'T24 内嵌正则开关 + 动作按钮在树里')
    ok(texts.indexOf('全部目标') < 0,
      'T24 空数据:目标下拉不占位')
    // v1.3.7:链路行常驻——空数据也有「后端」开关(用户:这个界面没有控制后端的开关嘛?)
    ok(texts.indexOf('链路') >= 0 && texts.indexOf('后端') >= 0 && texts.indexOf('未知') >= 0,
      'T24 空数据链路行常驻:「后端」开关在(状态未知)', texts.slice(0, 10))
    ok(texts.indexOf('探针') < 0 && texts.indexOf('芯片') < 0 && texts.indexOf('Flash') < 0,
      'T24 空数据不摆三张链路占位卡')

    // v1.3.4:左侧图标已移除(用户不要),入口改为右栏 guide 卡。
    // 带数据渲染一遍,确认「后端」段(断开/恢复)在树里且带状态点。
    const paneWithData = (() => {
      let captured = null
      const slots3 = { inject: (n, fn) => fn(), register: (spec, comp) => { if (spec.name === 'sidebar.right.pane.tab') captured = comp } }
      mod.apply({ get: (n) => (n === 'slots' ? slots3 : n === 'sidebarRightTabs' ? { register: () => {} } : n === 'sidebarRight' ? { openTab: () => {} } : undefined), effect: (fn) => fn() })
      return captured
    })()
    ok(typeof paneWithData === 'function', 'T24 右栏 pane 体可重复取到(重复 apply 不炸)')

    // T26 v1.3.6 轮转空态(用户截图:重启后面板全空,footer 却写着「轮转文件
    // 已生成」——2MB 历史一行都看不到):有 .1 时给大小/时间 + 「加载」入口,
    // 没有 .1 时入口不出现;点击走 POST /api/ocdup-log/rotated
    // v1.3.7:主组件 hook 作用域跨重渲染保留 → 能「点按钮→重渲染→看树」
    // 覆盖数组按主组件 hook 次序寻址(useRef 也占位):
    //   idx0 = tabVisibleRef, idx1 = entries, idx2 = stats
    const mainScope = newScope()
    const PanelComp = pane({}).type
    let lastNodes = []
    const renderNodes = (overrides) => {
      mainScope.hooks = []
      mainScope.idx = 0
      mainScope.overrides = overrides || []
      scopeStack.push(mainScope)
      const tree = PanelComp({})
      scopeStack.pop()
      lastNodes = flatten(tree)
      return lastNodes
    }
    const rerender = () => {
      mainScope.idx = 0 // hooks 保留、位号归零:否则第二次跑会从尾继续数,全部重新初始化
      scopeStack.push(mainScope)
      const tree = PanelComp({})
      scopeStack.pop()
      lastNodes = flatten(tree)
      return lastNodes
    }
    const findBtn = (nodes, text) => nodes.find((n) => n && n.type === 'button'
      && typeof (n.props && n.props.onClick) === 'function' && btnTexts(n) === text)
    const rotStats = {
      cmd: 0, rsp: 0, sys: 0, err: 0, log: 0, bytes: 0, lastAt: null, exists: true,
      kept: 0, lastSeq: 0, badLines: 0, logSize: 0, logMtimeMs: 1, rotated: true,
      rotSize: 2097171, rotMtimeMs: Date.parse('2026-10-07T07:22:32+08:00'),
    }
    const nodes26 = renderNodes([undefined, undefined, rotStats])
    const texts26 = nodes26.filter((n) => typeof n === 'string')
    ok(texts26.some((t) => t.indexOf('已轮转到 .1') >= 0), 'T26 空态提示 .1 的存在',
      texts26.filter((t) => t.indexOf('轮转') >= 0))
    ok(texts26.indexOf('加载 .1 尾部') >= 0, 'T26 「加载 .1 尾部」按钮在空态里')
    ok(texts26.some((t) => t.indexOf('2.0 MB') >= 0 && t.indexOf('10/7') >= 0),
      'T26 提示带 .1 体积与最后写入时间', texts26.filter((t) => t.indexOf('2.0 MB') >= 0))
    const btn26 = findBtn(nodes26, '加载 .1 尾部')
    ok(!!btn26, 'T26 加载按钮可被定位(带 onClick)')
    const fetchCalls = []
    globalThis.fetch = (url, opts) => {
      fetchCalls.push({ url: String(url), method: opts && opts.method })
      return Promise.resolve({
        json: () => Promise.resolve({
          ok: true, note: 'loaded',
          entries: [{ seq: 1, dir: 'cmd', tool: 'read_memory', text: 'rot-1', tag: '', len: 1, dtMs: null, ts: 'x' }],
          stats: rotStats,
        }),
      })
    }
    btn26.props.onClick()
    await sleep(30)
    ok(fetchCalls.length === 1 && fetchCalls[0].url === '/api/ocdup-log/rotated' && fetchCalls[0].method === 'POST',
      'T26 点击加载 → POST /api/ocdup-log/rotated', fetchCalls)
    delete globalThis.fetch

    const texts26b = renderNodes([undefined, undefined, Object.assign({}, rotStats, { rotated: false, rotSize: 0, rotMtimeMs: null })])
      .filter((n) => typeof n === 'string')
    ok(texts26b.indexOf('加载 .1 尾部') < 0 && texts26b.some((t) => t.indexOf('已轮转到 .1') < 0),
      'T26 没有 .1 时空态不出现加载入口')
    ok(texts26b.some((t) => t.indexOf('.1 无') >= 0), 'T26 footer 轮转列无 .1 时显示「.1 无」',
      texts26b.filter((t) => t.indexOf('.1') >= 0))

    const fst = mod._test.fmtStamp
    ok(/^\d{1,2}\/\d{1,2} \d{2}:\d{2}$/.test(fst(Date.parse('2026-10-07T07:22:32+08:00'))),
      'T26 fmtStamp:M/D HH:mm 形状(本地时区无关)', fst(Date.parse('2026-10-07T07:22:32+08:00')))
    ok(fst(null) === '' && fst(undefined) === '' && fst('x') === '' && fst(NaN) === '',
      'T26 fmtStamp:非时间输入回空串')

    // T27 v1.3.7 空态的后端开关(用户截图问「这个界面没有控制后端的开关嘛?」):
    // 链路行常驻,没流量时也有「后端」卡;点它真去 POST /api/ocdup-backend,
    // 结论回填卡片(没信息时先断开——本来就没跑会如实说「未运行」)
    renderNodes([undefined, undefined, null])
    const backendBtn = findBtn(lastNodes, '后端未知')
    ok(!!backendBtn, 'T27 空态有「后端」开关卡(值=未知)', btnTexts(lastNodes.find((n) => n && n.type === 'button' && btnTexts(n).indexOf('后端') >= 0) || {}))
    const beCalls = []
    globalThis.fetch = (url, opts) => {
      beCalls.push({ url: String(url), method: opts && opts.method, body: opts && opts.body })
      return Promise.resolve({
        json: () => Promise.resolve({ ok: true, text: 'backend was not running (nothing on 127.0.0.1:6666) - nothing to disconnect.' }),
      })
    }
    backendBtn.props.onClick()
    await sleep(30)
    ok(beCalls.length === 1 && beCalls[0].url === '/api/ocdup-backend' && beCalls[0].method === 'POST'
      && JSON.parse(beCalls[0].body).action === 'disconnect',
      'T27 空态点「后端」→ POST /api/ocdup-backend(无信息时先断开)', beCalls)
    rerender()
    const texts27 = lastNodes.filter((n) => typeof n === 'string')
    ok(texts27.indexOf('未运行') >= 0, 'T27 断开结论回填到卡片(本来就没跑 → 未运行)',
      texts27.filter((t) => t === '未运行' || t === '已断开' || t === '已连接'))
    delete globalThis.fetch

    // T28 v1.3.7 端到端复现用户那个自相矛盾画面:加载 .1 → 条目进列表;
    // 再两段式清空 → 条目和「已加载 N 条」备注一起消失(旧版备注留着说谎)
    renderNodes([undefined, undefined, rotStats])
    const loadBtn = findBtn(lastNodes, '加载 .1 尾部')
    ok(!!loadBtn, 'T28 加载按钮在空态里')
    const inc = []
    for (let i = 1; i <= 1470; i++) {
      inc.push({ seq: i, dir: i % 2 ? 'cmd' : 'rsp', tool: 'read_memory', text: 'x' + i, tag: '127.0.0.1:6666', len: 1, dtMs: null, ts: '2026-10-07T07:00:0' + (i % 10) + '.000+08:00' })
    }
    globalThis.fetch = () => Promise.resolve({
      json: () => Promise.resolve({ ok: true, note: 'loaded', entries: inc, stats: Object.assign({}, rotStats, { kept: 1470, lastSeq: 1470 }) }),
    })
    loadBtn.props.onClick()
    await sleep(50)
    rerender()
    const texts28 = lastNodes.filter((n) => typeof n === 'string')
    ok(texts28.indexOf('已加载 .1 尾部 1470 条') >= 0, 'T28 加载后备注显示条数')
    ok(texts28.indexOf('日志已连接,等待 AI 调用 ocdup 工具…') < 0 && texts28.indexOf('1470 条 · 渲染 300') >= 0,
      'T28 加载后空态让位给列表(计数 1470 条 · 渲染 300)——用户截图里缺的就是这一步',
      texts28.filter((t) => t.indexOf('条') >= 0))
    // 两段式清空:第一击进确认态,第二击真清
    globalThis.fetch = () => Promise.resolve({ json: () => Promise.resolve({ ok: true, stats: rotStats }) })
    findBtn(lastNodes, '清空').props.onClick()
    await sleep(10)
    rerender()
    const confirmBtn = findBtn(lastNodes, '确认清空')
    ok(!!confirmBtn, 'T28 清空第一击进入确认态(按钮变「确认清空」)')
    confirmBtn.props.onClick()
    await sleep(30)
    rerender()
    const texts28b = lastNodes.filter((n) => typeof n === 'string')
    ok(texts28b.indexOf('已加载 .1 尾部 1470 条') < 0,
      'T28 清空后「已加载 N 条」备注一起清(旧版条目没了备注还在,自相矛盾)')
    ok(texts28b.indexOf('日志已连接,等待 AI 调用 ocdup 工具…') >= 0,
      'T28 清空后回到空态')
    ok(texts28b.indexOf('后端') >= 0, 'T28 清空后「后端」开关还在(链路行常驻)')
    delete globalThis.fetch

    // T29 v1.3.8 失配胶囊一键切换(用户实测:403 板插着、cfg 钉在 423 钥匙板上
    // → connect 每次崩 openocd,表现为「连上就自己断开」;换对 cfg 后双 bank
    // 1MB 全通)。胶囊从「复制 set_target 命令」升级为直调,成功后自动重连。
    // 注意:胶囊是子组件,harness 的子作用域每次渲染都是新的(只有主组件跨重渲染
    // 保留 hooks),所以这里断言 fetch 契约(路由/body/链式),不靠 rerender 看翻词。
    const mm29 = 'connected to OpenOCD on 127.0.0.1:6666\nchip identity:\n'
      + '  cpu (CPUID, ARM standard) @ 0xE000ED00: 0x410fc241\n'
      + '    -> Cortex-M4 (ARMv7-M/v8-M, implementer ' + "'A'" + ', r0p1)\n'
      + '  dev_id (vendor register) @ 0xE0042000: 0x70050344\n'
      + '    -> dev_id 0x344, rev_id 0x7005\n'
      + '    -> family: AT32F403A\n'
      + '  target mismatch: identified AT32F403A, current target_cfg: D:/work/Ocdup/cfgs/target_at32f423_key.cfg -> use set_target AT32F403A'
    const entries29 = [
      { seq: 1, dir: 'sys', tool: 'backend', text: 'started D:\\ARMGCCTool\\atopenocd-py32\\bin\\openocd.exe (pid 12140); command port 127.0.0.1:6666 is up (interface: interface/cmsis-dap.cfg)', tag: '127.0.0.1:6666', len: 131, dtMs: null, ts: '2026-10-08T14:43:45.154+08:00' },
      { seq: 2, dir: 'rsp', tool: 'connect', text: mm29, tag: '127.0.0.1:6666', len: mm29.length, dtMs: 35, ts: '2026-10-08T14:43:45.189+08:00' },
    ]
    const stats29 = {
      cmd: 0, rsp: 1, sys: 1, err: 0, log: 0, bytes: 0, lastAt: null, exists: true,
      kept: 2, lastSeq: 2, badLines: 0, logSize: 0, logMtimeMs: 1, rotated: false, rotSize: 0, rotMtimeMs: null,
    }
    renderNodes([undefined, entries29, stats29])
    const swBtn = findBtn(lastNodes, '切换 AT32F403A')
    ok(!!swBtn, 'T29 失配胶囊在链路行里(文案仍是 切换 <家族>)',
      lastNodes.filter((n) => n && n.type === 'button').map((n) => btnTexts(n)))
    const tgCalls = []
    globalThis.fetch = (url, opts) => {
      tgCalls.push({ url: String(url), method: opts && opts.method, body: opts && opts.body, signal: !!(opts && opts.signal) })
      return Promise.resolve({
        json: () => Promise.resolve({ ok: true, text: "target switched to 'AT32F403A' -> target/at32f403axG.cfg" }),
      })
    }
    swBtn.props.onClick()
    await sleep(30)
    ok(tgCalls.length === 2
      && tgCalls[0].url === '/api/ocdup-target' && tgCalls[0].method === 'POST'
      && JSON.parse(tgCalls[0].body).preset === 'AT32F403A'
      && tgCalls[1].url === '/api/ocdup-backend' && tgCalls[1].method === 'POST'
      && JSON.parse(tgCalls[1].body).action === 'connect',
      'T29 点胶囊 → POST /api/ocdup-target {preset} → 成功后自动 connect 刷新链路行', tgCalls)
    // v1.4.0：两个 POST 都带 AbortController 的 signal——Host 侧 10s abort 只在
    // 宿主 tools.execute 认 signal 时收得住,不认时按钮会永久卡在「切换中…」
    ok(tgCalls.every((c) => c.signal),
      'T29b target/backend 的 POST 都带客户端超时信号', tgCalls.map((c) => c.signal))
    // 失败路径(如只读会话被 ocdup 拦成 BLOCKED):只发一次 target 请求,不自动重连
    const tgCalls2 = []
    globalThis.fetch = (url, opts) => {
      tgCalls2.push({ url: String(url), method: opts && opts.method, body: opts && opts.body })
      return Promise.resolve({ json: () => Promise.resolve({ ok: false, error: 'BLOCKED: read_only session' }) })
    }
    swBtn.props.onClick()
    await sleep(30)
    ok(tgCalls2.length === 1 && tgCalls2[0].url === '/api/ocdup-target',
      'T29 切换失败不自动重连(只发一次 target 请求)', tgCalls2)
    delete globalThis.fetch

    // T30 v1.3.9 轮询自愈:fetchWithTimeout 正常/超时两条路径。
    // 背景(用户实测):一个挂起的轮询请求把整条链永久楔住(inFlightRef 永远 true,
    // 之后每一拍都被挡在 fetch 之外,连 visibilitychange 都救不回来),视图冻死
    // 3 分钟——现在 10s abort 落回 catch/finally,下一拍自动恢复。
    const fwt = mod._test.fetchWithTimeout
    ok(typeof fwt === 'function' && mod._test.POLL_FETCH_TIMEOUT_MS === 10000,
      'T30 fetchWithTimeout 已导出且超时为 10s', mod._test.POLL_FETCH_TIMEOUT_MS)
    let sawSignal = false
    globalThis.fetch = (url, opts) => { sawSignal = !!(opts && opts.signal); return Promise.resolve({ ok: true }) }
    const r30 = await fwt('/api/ocdup-log?since=1', { cache: 'no-store' }, 1000)
    ok(r30.ok && sawSignal, 'T30 正常路径:透传 AbortSignal 并原样解析')
    globalThis.fetch = (url, opts) => new Promise((resolve, reject) => {
      opts.signal.addEventListener('abort', () => reject(new Error('aborted by timeout')))
    })
    let threw30 = ''
    try { await fwt('/api/ocdup-log?since=1', {}, 30) } catch (e) { threw30 = e && e.message ? e.message : String(e) }
    ok(/abort/i.test(threw30), 'T30 超时路径:挂起的 fetch 在 ms 后 abort 抛出(轮询链能落回 catch)', threw30)
    delete globalThis.fetch

    delete globalThis.window
    delete globalThis.document
    delete globalThis.localStorage
  }

  dispose()
  if (typeof dispose2 === 'function') dispose2()
  console.log('\nALL PASS (' + checks + ' checks, mode=' + mode + ')')
  fs.rmSync(tmp, { recursive: true, force: true })
})().catch((e) => { console.error(e.stack || e.message); process.exit(1) })
