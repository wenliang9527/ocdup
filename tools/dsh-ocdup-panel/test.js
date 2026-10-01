'use strict'
// ============================================================
//  dsh-ocdup-panel — Host 半区行为测试(无框架,纯 node)
//
//  运行(在插件目录下):
//    node test.js core     # 增量/半行/切片/批量裁剪/轮转/清空/方法守卫/方向归一化
//    node test.js window   # 首次挂载只读尾部窗口(大日志不全量读入)
//    node test.js api      # Δt/compact/导出/help(需要 MAX_ENTRIES≥40)
//    node test.js client   # client.js 注册结构冒烟(stub React,不起浏览器)
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
if (logFile) fs.mkdirSync(path.dirname(logFile), { recursive: true })

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
      && /@container op-panel/.test(injectedStyles.text), 'T12 一次性样式表注入(含容器查询列降级)', injectedStyles.text)

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
