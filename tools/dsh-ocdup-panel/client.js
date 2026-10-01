'use strict'
// ============================================================
//  dsh-ocdup-panel — Client 半区(浏览器面板,永久 cordis 插件)  v1.0.0
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
    const LS = 'dsh-ocdup-panel/prefs/v1'

    const POLL_ACTIVE_MS = 700   // 刚拿到数据:贴近写入端节奏
    const POLL_IDLE_MS = 2500    // 空闲退避
    const POLL_HIDDEN_MS = 2000  // 面板不可见
    const POLL_PAUSED_MS = 1500  // 用户暂停
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

    const DIR_COLOR = { cmd: '#d97706', rsp: '#059669', sys: '#2563eb', err: '#ef4444' }
    const DIR_LABEL = { cmd: 'cmd', rsp: 'rsp', sys: 'SYS', err: 'err' }
    const MARK_STYLE = { background: 'rgba(245, 158, 11, 0.35)', color: 'inherit', borderRadius: 2, padding: '0 1px' }

    // ---- 流量徽章共享态:图标轮询推进 lastSeq,面板可见时标记已读 ----
    const badgeStore = {
      lastSeq: 0,
      lastSeenSeq: 0,
      subs: new Set(),
      update(seq) { if (seq > this.lastSeq) { this.lastSeq = seq; this.notify() } },
      markSeen(seq) { if (seq > this.lastSeenSeq) { this.lastSeenSeq = seq; this.notify() } },
      unread() { return this.lastSeq > this.lastSeenSeq },
      notify() { for (const fn of this.subs) { try { fn() } catch (e) { /* 订阅者异常不扩散 */ } } },
      subscribe(fn) { this.subs.add(fn); return () => this.subs.delete(fn) },
    }

    // 搜索匹配器:正则开关开启且表达式合法 → RegExp;否则小写子串(空查询 → null)
    const compileMatcher = (query, useRegex) => {
      const q = String(query || '').trim()
      if (!q) return null
      if (useRegex) {
        try { return new RegExp(q, 'i') } catch (e) { return q.toLowerCase() } // 非法正则回退子串
      }
      return q.toLowerCase()
    }

    const matcherHit = (matcher, e) => (matcher instanceof RegExp
      ? matcher.test(e.text || '') || matcher.test(e.tool || '') || matcher.test(e.tag || '')
      : (e.text && e.text.toLowerCase().indexOf(matcher) >= 0)
        || (e.tool && e.tool.toLowerCase().indexOf(matcher) >= 0)
        || (e.tag && e.tag.toLowerCase().indexOf(matcher) >= 0))

    // ---- 一次性样式:hover/窄宽度降级(inline style 写不了 :hover 与媒体查询) ----
    const injectStyles = () => {
      try {
        if (document.getElementById('dsh-ocdup-panel-styles')) return
        const el = document.createElement('style')
        el.id = 'dsh-ocdup-panel-styles'
        el.textContent = [
          '.op-row { background: transparent; }',
          '.op-row:hover { background: var(--dsw-alias-bg-layer-3, #f5f5f5); }',
          '.op-row.op-sel { background: var(--dsw-alias-bg-layer-3, #f0f0f0); }',
          // 容器查询跟随面板实际宽度(停靠栏拖拽时列降级实时生效)
          '@container op-panel (max-width: 640px) { .op-tag { display: none; } }',
          '@container op-panel (max-width: 560px) { .op-len { display: none; } }',
          '@container op-panel (max-width: 520px) { .op-stats { display: none; } }',
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

    // 当前过滤视图 → JSONL 下载(Blob 本地生成;gap 标记不是数据,剔除)
    const downloadJsonl = (entries, name) => {
      try {
        const lines = entries.filter((e) => !e.kind).map((e) => JSON.stringify(e))
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
    const STATS_KEYS = ['cmd', 'rsp', 'sys', 'err', 'bytes', 'lastAt', 'exists', 'kept', 'lastSeq',
      'badLines', 'logSize', 'logMtimeMs', 'rotated']
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

    // ---- 组件:行条目(memo:引用未变的行不重渲染) ----
    const Row = React.memo(function Row({ e, selected, onSelect, matcher, onPort }) {
      const payload = e.text.length > ROW_PAYLOAD_MAX ? e.text.slice(0, ROW_PAYLOAD_MAX) + ' …' : e.text
      const payloadChildren = !matcher ? payload
        : matcher instanceof RegExp ? highlightRe(payload, matcher)
        : highlight(payload, matcher)
      return h('div', {
        className: 'op-row' + (selected ? ' op-sel' : ''),
        onClick: () => onSelect(e),
        style: {
          display: 'flex', alignItems: 'baseline', gap: 8,
          minWidth: 0, width: '100%', boxSizing: 'border-box',
          padding: '2px 10px', cursor: 'pointer',
          borderBottom: '1px solid var(--dsw-alias-border-l2, #f0f0f0)',
          fontSize: 12, lineHeight: 1.6,
        },
      },
        h('span', { style: { flex: '0 0 84px', color: 'var(--dsw-alias-label-tertiary, #999)', fontVariantNumeric: 'tabular-nums' } }, shortTime(e.ts)),
        h('span', {
          title: e.dir,
          style: {
            flex: '0 0 34px', textAlign: 'center', borderRadius: 4, fontSize: 11,
            fontWeight: 700, color: '#fff', background: DIR_COLOR[e.dir] || '#888', padding: '0 4px',
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
        h('span', {
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

    // ---- 组件:Ocdup 实时面板(右侧停靠栏 tab 体 / 旧版整屏 main 面板) ----
    const EMPTY_TAB_INFO = { tab: null }
    const OcdupPanel = (props) => {
      const prefs0 = React.useMemo(loadPrefs, [])
      const useTabInfoSafe = props && typeof props.useTabInfo === 'function' ? props.useTabInfo : () => EMPTY_TAB_INFO
      const tabInfo = useTabInfoSafe()
      const tabVisible = !(tabInfo && tabInfo.tab && tabInfo.tab.visible === false)
      const tabVisibleRef = React.useRef(tabVisible)
      React.useEffect(() => { tabVisibleRef.current = tabVisible }, [tabVisible])
      const [entries, setEntries] = React.useState([])
      const [stats, setStats] = React.useState(null)
      const [connected, setConnected] = React.useState(false)
      const [paused, setPaused] = React.useState(false)
      const [dir, setDir] = React.useState(prefs0.dir || 'ALL')
      const [port, setPort] = React.useState(prefs0.port || 'ALL')
      const [follow, setFollow] = React.useState(prefs0.follow !== false)
      const [useRegex, setUseRegex] = React.useState(!!prefs0.useRegex)
      const [autoSuspended, setAutoSuspended] = React.useState(false)
      const [newCount, setNewCount] = React.useState(0)
      const [confirmClear, setConfirmClear] = React.useState(false)
      const [search, setSearch] = React.useState('')
      const [selected, setSelected] = React.useState(null)
      const lastSeqRef = React.useRef(0)
      const pausedRef = React.useRef(paused)
      const listRef = React.useRef(null)
      const searchRef = React.useRef(null)
      const rootRef = React.useRef(null)
      const confirmTimerRef = React.useRef(null)
      const inFlightRef = React.useRef(false)
      const autoSuspendedRef = React.useRef(false)

      React.useEffect(() => { pausedRef.current = paused }, [paused])
      React.useEffect(() => { autoSuspendedRef.current = autoSuspended }, [autoSuspended])
      React.useEffect(() => { savePrefs({ dir, follow, port, useRegex }) }, [dir, follow, port, useRegex])
      React.useEffect(() => () => { if (confirmTimerRef.current) clearTimeout(confirmTimerRef.current) }, [])

      // ---- 自适应增量轮询(单条 setTimeout 链,避免固定间隔空转) ----
      React.useEffect(() => {
        let alive = true
        let timer = null

        const schedule = (delay) => {
          if (!alive) return
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
            const r = await fetch(ROUTE + '?since=' + lastSeqRef.current, { cache: 'no-store' })
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

        const onVis = () => {
          if (document.visibilityState !== 'visible' || !alive) return
          if (timer !== null) { clearTimeout(timer); timer = null }
          tick()
        }
        document.addEventListener('visibilitychange', onVis)
        tick()

        return () => {
          alive = false
          if (timer !== null) clearTimeout(timer)
          document.removeEventListener('visibilitychange', onVis)
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
            setEntries([]); setSelected(null)
            if (j.stats) { setStats(j.stats); lastSeqRef.current = j.stats.lastSeq || 0 }
          }
        } catch (e) { /* 忽略 */ }
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

      // ---- 过滤结果缓存:只在数据/方向/目标/匹配器变化时重算 ----
      const matcher = React.useMemo(() => compileMatcher(search, useRegex), [search, useRegex])
      const visible = React.useMemo(() => {
        const out = []
        for (let i = 0; i < entries.length; i++) {
          const e = entries[i]
          if (e.kind === 'gap') {
            if (dir === 'ALL' && !matcher) out.push(e)
            continue
          }
          if (dir !== 'ALL' && e.dir !== dir) continue
          if (port !== 'ALL' && e.tag !== port) continue
          if (matcher && !matcherHit(matcher, e)) continue
          out.push(e)
        }
        return out
      }, [entries, dir, port, matcher])

      const visibleCount = visible.length
      const rendered = visibleCount > MAX_RENDER_ROWS ? visible.slice(visibleCount - MAX_RENDER_ROWS) : visible
      const counterText = (rendered.length < visibleCount ? '渲染 ' + rendered.length + ' / ' : '')
        + visibleCount + ' / ' + entries.length + ' 条'

      const btn = (label, onClick, active, title) => h('button', {
        type: 'button', title: title || '', onClick,
        style: {
          appearance: 'none', cursor: 'pointer', fontSize: 12, padding: '4px 10px',
          borderRadius: 8, border: '1px solid var(--dsw-alias-border-l2, #ddd)',
          background: active ? 'var(--dsw-alias-label-primary, #222)' : 'var(--dsw-alias-bg-layer-3, #fff)',
          color: active ? 'var(--dsw-alias-bg-layer-3, #fff)' : 'var(--dsw-alias-label-primary, #222)',
          font: 'inherit',
        },
      }, label)

      const toolbar = h('div', {
        style: {
          display: 'flex', alignItems: 'center', gap: 8, flexWrap: 'wrap',
          padding: '10px 12px', borderBottom: '1px solid var(--dsw-alias-border-l2, #eee)',
        },
      },
        h('span', {
          title: !connected ? 'Host 未响应' : (stats && stats.exists === false) ? '已连接,等待日志文件出现' : '已连接 Host 尾随',
          style: {
            width: 8, height: 8, borderRadius: 999, flex: '0 0 auto',
            background: !connected
              ? 'var(--dsw-alias-state-error-primary, #ef4444)'
              : (stats && stats.exists === false)
                ? 'var(--dsw-alias-state-warn-primary, #f59e0b)'
                : 'var(--dsw-alias-state-success-primary, #10b981)',
          },
        }),
        h('span', { className: 'op-stats', style: { fontSize: 12, color: 'var(--dsw-alias-label-secondary, #666)', fontVariantNumeric: 'tabular-nums' } },
          'cmd ' + ((stats && stats.cmd) || 0) + ' · rsp ' + ((stats && stats.rsp) || 0)
          + ' · SYS ' + ((stats && stats.sys) || 0)
          + (stats && stats.err ? ' · err ' + stats.err : '')),
        btn(paused ? '▶ 继续' : '⏸ 暂停', () => setPaused(!paused), paused, '暂停后数据在 Host 侧继续累积,恢复后拉齐'),
        btn(follow && autoSuspended ? '跟随·挂起' : '跟随', () => setFollow(!follow), effectiveFollow,
          autoSuspended ? '翻看历史中已自动挂起,回到底部或再点恢复' : '自动滚动到最新'),
        h('select', {
          value: dir, onChange: (e) => setDir(e.target.value),
          style: { fontSize: 12, padding: '3px 6px', borderRadius: 8, border: '1px solid var(--dsw-alias-border-l2, #ddd)', background: 'var(--dsw-alias-bg-layer-3, #fff)', color: 'var(--dsw-alias-label-primary, #222)' },
        },
          h('option', { value: 'ALL' }, '全部'),
          h('option', { value: 'cmd' }, 'cmd'),
          h('option', { value: 'rsp' }, 'rsp'),
          h('option', { value: 'sys' }, 'SYS'),
          h('option', { value: 'err' }, 'err'),
        ),
        h('select', {
          value: port, onChange: (e) => setPort(e.target.value), title: '按目标 (host:port) 过滤',
          style: { fontSize: 12, padding: '3px 6px', borderRadius: 8, border: '1px solid var(--dsw-alias-border-l2, #ddd)', background: 'var(--dsw-alias-bg-layer-3, #fff)', color: 'var(--dsw-alias-label-primary, #222)' },
        },
          h('option', { value: 'ALL' }, '全部目标'),
          portOptions.map((t) => h('option', { key: t, value: t }, t)),
        ),
        btn('.*', () => setUseRegex(!useRegex), useRegex, '正则模式(非法表达式自动回退子串匹配)'),
        h('input', {
          ref: searchRef,
          value: search,
          placeholder: useRegex ? '正则过滤 text/tool/target…' : '过滤 text/tool/target…',
          onChange: (e) => setSearch(e.target.value),
          style: { flex: '1 1 140px', minWidth: 120, height: 28, fontSize: 12, padding: '0 10px', borderRadius: 8, border: '1px solid var(--dsw-alias-border-l2, #ddd)', background: 'var(--dsw-alias-bg-layer-3, #fff)', color: 'var(--dsw-alias-label-primary, #222)', boxSizing: 'border-box' },
        }),
        btn(confirmClear ? '确认清空?' : '清空', clear, confirmClear, '截断共享 mcp-traffic.jsonl(再点一次生效)'),
        btn('导出', () => downloadJsonl(visible), false, '下载当前过滤视图为 JSONL(整个内存环用 Host /export 路由)'),
        h('span', { style: { fontSize: 11, color: 'var(--dsw-alias-label-tertiary, #999)' } }, counterText),
      )

      // 空态:API 地址(agent 可直接 curl)+ 链路状态
      const apiFull = (typeof location !== 'undefined' && location.origin ? location.origin : '') + ROUTE
      const emptyBox = h('div', { style: { padding: 24, textAlign: 'center', color: 'var(--dsw-alias-label-tertiary, #999)', fontSize: 13 } },
        h('div', null,
          stats && stats.exists
            ? '日志已连接,等待 AI 调用 ocdup 工具…(让 Agent 调 mcp__ocdup__connect 试试)'
            : '尚未发现 ' + ((stats && stats.logPath) || 'mcp-traffic.jsonl') + ' — 首次 AI 硬件操作后自动出现'),
        h('div', { style: { marginTop: 6, fontSize: 11, display: 'inline-flex', alignItems: 'center', gap: 6 } },
          'API:',
          h('span', { style: { fontFamily: 'Consolas, Menlo, monospace' } }, apiFull),
          h(CopyBtn, { text: apiFull, label: '复制' })),
        stats && stats.exists && typeof stats.logSize === 'number'
          ? h('div', { style: { marginTop: 4, fontSize: 11 } },
              '日志 ' + fmtBytes(stats.logSize) + 'B · 坏行 ' + (stats.badLines || 0)
              + ' · 轮转文件' + (stats.rotated ? '已生成' : '未生成'))
          : null,
      )

      const list = h('div', {
        ref: listRef, onScroll,
        style: { flex: 1, minHeight: 0, width: '100%', overflowX: 'hidden', overflowY: 'auto' },
      },
        visibleCount === 0
          ? emptyBox
          : rendered.map((e) => (e.kind === 'gap'
              ? h(GapRow, { key: 'gap-' + e.seq, count: e.count })
              : h(Row, {
                  key: e.seq, e,
                  matcher,
                  onPort: (tag) => setPort(port === tag ? 'ALL' : tag),
                  selected: selected && selected.seq === e.seq, onSelect: setSelected,
                }))),
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

      const detail = selected ? h('div', {
        style: {
          flex: '0 0 auto', maxHeight: 180, overflow: 'auto', borderTop: '1px solid var(--dsw-alias-border-l2, #eee)',
          padding: '8px 12px', fontSize: 12, background: 'var(--dsw-alias-bg-layer-3, #fafafa)',
        },
      },
        h('div', { style: { marginBottom: 4, display: 'flex', alignItems: 'center', gap: 8, color: 'var(--dsw-alias-label-secondary, #666)' } },
          h('span', { style: { flex: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' } },
            '#' + selected.seq + ' · ' + shortTime(selected.ts)
              + (typeof selected.ts === 'string' && selected.ts.length >= 10 ? ' · ' + selected.ts.slice(0, 10) : '')
              + ' · ' + (DIR_LABEL[selected.dir] || selected.dir) + ' · '
              + selected.tool + (selected.tag ? ' · ' + selected.tag : '') + ' · ' + selected.len + 'B'
              + (selected.dtMs != null ? ' · Δ' + fmtDt(selected.dtMs) : '')),
          selected.text ? h(CopyBtn, { text: selected.text, label: '复制' }) : null,
        ),
        h('div', { style: { fontFamily: 'Consolas, Menlo, monospace', whiteSpace: 'pre-wrap', wordBreak: 'break-all', color: 'var(--dsw-alias-label-primary, #222)' } },
          selected.text || '(空)'),
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
      }
      const onMouseDown = (e) => {
        if (isFormField(e.target) || e.target.tagName === 'BUTTON') return
        if (rootRef.current) rootRef.current.focus()
      }

      // 面板可见且未暂停未挂起 = 正在实时看 → 标记已读
      React.useEffect(() => {
        if (tabVisible && !paused && !autoSuspended && stats) badgeStore.markSeen(stats.lastSeq || 0)
      }, [tabVisible, paused, autoSuspended, stats])

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
        toolbar, listWrap, detail,
      )
    }

    // ---- 组件:侧边栏面板入口(点击打开面板;优先右栏 tab,兜底整屏) ----
    const PanelIcon = (props) => {
      const [unread, setUnread] = React.useState(badgeStore.unread())
      React.useEffect(() => badgeStore.subscribe(() => setUnread(badgeStore.unread())), [])
      React.useEffect(() => {
        let alive = true
        let timer = null
        const tick = async () => {
          timer = null
          if (!alive) return
          if (document.visibilityState === 'hidden') { timer = setTimeout(tick, 8000); return }
          try {
            const r = await fetch(ROUTE + '?stats=1', { cache: 'no-store' })
            const j = await r.json()
            if (alive && j && j.ok && j.stats) badgeStore.update(j.stats.lastSeq || 0)
          } catch (e) { /* Host 未起:静默 */ }
          if (alive) timer = setTimeout(tick, 5000)
        }
        tick()
        return () => { alive = false; if (timer !== null) clearTimeout(timer) }
      }, [])
      const onClick = (e) => {
        if (props && typeof props.onOpen === 'function') { props.onOpen(e); return }
        if (props && typeof props.onClick === 'function') { props.onClick(e); return }
        try {
          const layout = ctxRef && ctxRef.get && ctxRef.get('layout')
          if (layout && typeof layout.selectPanel === 'function') layout.selectPanel('ocdup-log')
        } catch (e2) { /* layout 服务缺席时静默 */ }
      }
      return h('span', {
        title: props && props.title ? props.title : 'OCDUP 调试面板',
        onClick,
        style: { position: 'relative', display: 'inline-flex', alignItems: 'center', justifyContent: 'center', cursor: 'pointer', lineHeight: 1 },
      },
        h(ChipGlyph, { size: 16 }),
        unread ? h('span', {
          title: '有新的 ocdup 流量',
          style: {
            position: 'absolute', top: -3, right: -5, width: 7, height: 7,
            borderRadius: 999,
            background: 'var(--dsw-alias-state-error-primary, #ef4444)',
            border: '1px solid var(--dsw-alias-bg-layer-2, #fff)',
          },
        }) : null,
      )
    }

    return {
      inject: ['slots', 'sidebarRight', 'sidebarRightTabs'],
      // 纯函数仅供 test.js client 模式断言,生产逻辑不依赖
      _test: { highlight, highlightRe, compileMatcher, matcherHit, hueOf, fmtDt, sameStats, badgeStore },
      apply: function (ctx) {
        const slots = ctx.get('slots')
        if (slots === undefined) return
        ctxRef = ctx

        const rightTabs = ctx.get('sidebarRightTabs')
        const right = ctx.get('sidebarRight')
        const hasRightbar = !!(rightTabs && typeof rightTabs.register === 'function'
          && right && typeof right.openTab === 'function')

        if (hasRightbar) {
          // 主路径:右侧停靠栏 tab —— 对话始终可见;keepMounted 让轮询跨切换/收起存活
          ctx.effect(() => rightTabs.register({
            id: 'dsh-ocdup-panel',
            kind: 'ocdup-log',
            title: () => 'OCDUP 调试',
            keepMounted: true,
            guide: [{
              id: 'ocdup-log',
              kind: 'ocdup-log',
              title: () => 'OCDUP 调试',
              description: () => 'OpenOCD 命令/响应实时面板',
              icon: ChipGlyph,
            }],
          }), 'dsh-ocdup-panel: tab type')

          ctx.effect(() => slots.inject('sidebar.right.pane.tab', () => slots.register(
            { name: 'sidebar.right.pane.tab', key: 'dsh-ocdup-panel' },
            (props) => h(OcdupPanel, props),
          )), 'dsh-ocdup-panel: pane body')

          // 左侧图标 = 打开右侧 tab(无会话时 openTab 会抛,吞掉即可)
          slots.inject('sidebar.panellist', () => slots.register(
            { name: 'sidebar.panellist', id: 'ocdup-log', order: 81, label: () => 'OCDUP 调试' },
            (props) => h(PanelIcon, {
              ...props,
              title: '打开 OCDUP 调试面板(右侧停靠,对话保留)',
              onOpen: () => { try { right.openTab('ocdup-log') } catch (e) { /* 无会话/面板未挂 */ } },
            }),
          ))
        } else {
          // 兜底:DSH 缺 rightbar 服务(版本差异)→ 整屏 main 模式
          slots.inject('sidebar.panellist', () => slots.register(
            { name: 'sidebar.panellist', id: 'ocdup-log', order: 81, label: () => 'OCDUP 调试' },
            (props) => h(PanelIcon, props),
          ))

          slots.inject('main', () => slots.register(
            { name: 'main', key: 'ocdup-log' },
            (props) => h(OcdupPanel, props),
          ))
        }
      },
    }
  },
})
