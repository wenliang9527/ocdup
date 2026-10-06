'use strict'
// ============================================================
//  dsh-ocdup-panel — Host 半区行为测试(无框架,纯 node)
//
//  运行(在插件目录下):
//    node test.js core     # 增量/半行/切片/批量裁剪/轮转/清空/方法守卫/方向归一化
//    node test.js window   # 首次挂载只读尾部窗口(大日志不全量读入)
//    node test.js api      # Δt/compact/导出/help(需要 MAX_ENTRIES≥40)
//    node test.js client   # client.js 注册结构冒烟 + 纯函数契约(stub React,不起浏览器)
//
//  用环境变量把插件参数压小,便于快速触发边界:
//    DSH_OCDUP_PANEL_LOG / DSH_OCDUP_PANEL_MAX_ENTRIES / DSH_OCDUP_PANEL_FIRST_TAIL
//  说明:插件在 require 时读取这些常量,所以必须在 require 之前设置 env。
// ============================================================

const fs = require('fs')
const path = require('path')
const os = require('os')

const mode = process.argv[2] || 'core'
const tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'dsh-op-'))
const logFile = process.env.DSH_OCDUP_PANEL_LOG

if (!logFile && mode !== 'client') { console.error('DSH_OCDUP_PANEL_LOG must be set'); process.exit(1) }
if (logFile) {
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
  }

  if (mode === 'client') {
    // T12 client.js 注册结构冒烟:stub 模块环境,验证两条注册分支不跑浏览器
    const ReactStub = {
      createElement: (type, props, ...children) => ({ type, props, children }),
      memo: (f) => f,
      useState: (v) => [typeof v === 'function' ? v() : v, () => {}],
      useEffect: () => {},
      useRef: (v) => ({ current: v }),
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
    ok(Array.isArray(mod.inject) && mod.inject.includes('slots') && mod.inject.includes('sidebarRightTabs')
      && mod.inject.includes('sidebarRight'), 'T12 inject 声明含 slots + rightbar 服务', mod.inject)

    // 纯函数契约(_test 导出)
    const hi = mod._test.highlight('hello world hello', 'hello')
    ok(Array.isArray(hi) && hi.length === 3 && hi[0].type === 'mark' && hi[1] === ' world ' && hi[2].type === 'mark',
      'T13 highlight 命中包裹为 mark', hi.map((x) => (typeof x === 'string' ? x : x.type)))
    ok(mod._test.highlight('abc', 'zz').length === 1 && mod._test.highlight('abc', 'zz')[0] === 'abc', 'T13 highlight 无命中原样返回')
    ok(mod._test.highlight('ABC', 'a').length === 2, 'T13 highlight 大小写不敏感')
    ok(mod._test.fmtDt(null) === '—' && mod._test.fmtDt(500) === '500ms' && mod._test.fmtDt(1500) === '1.50s', 'T13 fmtDt 三档格式')
    ok(mod._test.hueOf('127.0.0.1:6666') === mod._test.hueOf('127.0.0.1:6666') && Number.isInteger(mod._test.hueOf('127.0.0.1:6666')), 'T13 hueOf 稳定')

    // T14 匹配器/正则高亮/徽章共享态
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
    const hr = mod._test.highlightRe('ab', /x*/)
    ok(Array.isArray(hr) && hr.length >= 2, 'T14 正则高亮零长匹配不死循环', hr.map((x) => (typeof x === 'string' ? x : x.type)))
    const hr2 = mod._test.highlightRe('a1b2', /\d/)
    ok(hr2.length === 4 && hr2[1].type === 'mark' && hr2[1].children[0] === '1'
      && hr2[3].children[0] === '2', 'T14 正则高亮包裹数字命中(非 g 正则不死循环)', hr2.map((x) => (typeof x === 'string' ? x : x.children)))

    const bs = mod._test.badgeStore
    const events = []
    const un = bs.subscribe(() => events.push(bs.unread()))
    bs.markSeen(10)
    bs.update(15)
    ok(events.length === 2 && events[0] === false && events[1] === true, 'T14 badgeStore markSeen→update 依次通知', events)
    bs.markSeen(15)
    ok(events.length === 3 && events[2] === false && bs.unread() === false, 'T14 markSeen 推进已读水位并清零', events)
    bs.update(15)
    ok(events.length === 3, 'T14 seq 不前进不重复通知')
    un()
    bs.update(20)
    ok(events.length === 3, 'T14 退订后不再通知')

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

    // T24 面板整体渲染冒烟(stub React 下真跑一遍组件体):改工具栏/链路读数条
    // 时最容易出的是渲染期异常——这里至少保证空数据 + 有条目两条路径都不炸,
    // 且改版后的控件语言还在(chip 过滤 / 内嵌正则 / 无旧分隔线)
    const flatten = (el, out) => {
      out = out || []
      if (el === null || el === undefined || el === false) return out
      if (Array.isArray(el)) { for (const x of el) flatten(x, out); return out }
      if (typeof el !== 'object') { out.push(el); return out }
      // 函数组件就地展开(stub React 不调度,这里手动走一层)
      if (typeof el.type === 'function') return flatten(el.type(Object.assign({}, el.props, { children: el.children })), out)
      out.push(el)
      flatten(el.children, out)
      return out
    }

    // 兜底分支:无 rightbar 服务 → main 槽
    const calls = []
    const registered = {}
    const slotsApi = {
      inject: (name, fn) => { calls.push(['inject', name]); fn() },
      register: (spec, comp) => { calls.push(['register', spec.name, spec.key || spec.id]); registered[spec.name + '|' + (spec.key || spec.id)] = comp },
    }
    const ctxNoRight = { get: (n) => (n === 'slots' ? slotsApi : undefined), effect: (fn) => fn() }
    mod.apply(ctxNoRight)
    ok(calls.some((c) => c[0] === 'register' && c[1] === 'main'), 'T12 兜底分支注册 main 槽')
    ok(!calls.some((c) => c[1] === 'sidebar.right.pane.tab'), 'T12 兜底分支不注册右栏 tab 体')
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

    // 右栏分支:tab 类型 + pane.tab 体 + 图标转发 openTab
    calls.length = 0
    const tabDefs = []
    const opened = []
    const ctxRight = {
      get: (n) => {
        if (n === 'slots') return slotsApi
        if (n === 'sidebarRightTabs') return { register: (d) => tabDefs.push(d) }
        if (n === 'sidebarRight') return { openTab: (k) => opened.push(k) }
        return undefined
      },
      effect: (fn) => fn(),
    }
    mod.apply(ctxRight)
    ok(tabDefs.length === 1 && tabDefs[0].id === 'dsh-ocdup-panel' && tabDefs[0].kind === 'ocdup-log'
      && tabDefs[0].keepMounted === true, 'T12 右栏 tab 类型注册(id/kind/keepMounted)', tabDefs[0])
    ok(Array.isArray(tabDefs[0].guide) && tabDefs[0].guide.length === 1
      && tabDefs[0].guide[0].kind === 'ocdup-log' && typeof tabDefs[0].guide[0].icon === 'function',
    'T12 guide 入口卡声明', tabDefs[0].guide)
    ok(calls.some((c) => c[0] === 'register' && c[1] === 'sidebar.right.pane.tab' && c[2] === 'dsh-ocdup-panel'),
      'T12 pane.tab 体以插件 id 为 key 注册')
    ok(!calls.some((c) => c[0] === 'register' && c[1] === 'main'), 'T12 右栏分支不再注册整屏 main')

    // T24 面板整体渲染冒烟(stub React 下真跑一遍组件体):改工具栏/链路读数条时
    // 最容易出的是渲染期异常——这里至少保证空数据路径不炸,且改版后的控件语言还在
    const pane = registered['sidebar.right.pane.tab|dsh-ocdup-panel']
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
    ok(texts.indexOf('全部目标') < 0 && texts.indexOf('链路') < 0,
      'T24 空数据:目标下拉/链路读数条都不占位')

    // 图标点击 → openTab(kind)
    ok(Array.isArray(calls.find((c) => c[0] === 'inject' && c[1] === 'sidebar.panellist')), 'T12 侧边栏图标已注册')
    const outer = registered['sidebar.panellist|ocdup-log']({})
    const iconEl = outer.type(outer.props)
    ok(iconEl && typeof iconEl.props.onClick === 'function', 'T12 图标渲染为可点击元素')
    iconEl.props.onClick({})
    ok(opened.length === 1 && opened[0] === 'ocdup-log', 'T12 图标点击转发 openTab(kind)', opened)

    delete globalThis.window
    delete globalThis.document
    delete globalThis.localStorage
  }

  dispose()
  if (typeof dispose2 === 'function') dispose2()
  console.log('\nALL PASS (' + checks + ' checks, mode=' + mode + ')')
  fs.rmSync(tmp, { recursive: true, force: true })
})().catch((e) => { console.error(e.message); process.exit(1) })
