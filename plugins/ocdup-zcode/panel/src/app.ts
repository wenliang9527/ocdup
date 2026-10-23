/**
 * Ocdup 调试面板 UI。
 *
 * 四个区块：连接状态 / Flash 扇区图 / 寄存器查看器 / 操作时间线。
 * 所有硬件访问都走 app.callServerTool 到本插件声明的代理服务器（ocdup_* 工具），
 * 由代理转发给用户级 ocdup server。文本解析在 parsers.ts。
 *
 * 交互纪律（与 ocdup 工具语义对齐）：
 * - 目标核 running 时「读寄存器」按钮置灰并提示需先 halt；
 * - 擦除 / 烧录 / 写寄存器 / 复位 一律二次确认；
 * - 上次选中的外设 + 标签页存 widget state，关闭重开恢复。
 */
import {
  App,
  applyDocumentTheme,
  applyHostFonts,
  applyHostStyleVariables,
} from "@modelcontextprotocol/ext-apps";
import { EmptyResultSchema } from "@modelcontextprotocol/core";
import {
  parseStatus,
  parseChipIdentity,
  parseFlashBanks,
  parseSectorGeometry,
  sectorsInBank,
  parseFlashOpResult,
  parseCoreRegs,
  parseSvdList,
  parseSvdRegisterList,
  parseDecodedRegister,
  firstErrorLine,
  anyHalted,
  type BackendStatus,
  type ChipIdentity,
  type FlashBank,
  type FlashOpResult,
  type SectorGeometry,
  type DecodedRegister,
  type SvdRegisterRow,
  type SvdPeripheral,
} from "./parsers";
import { stringsFor, type T } from "./i18n";

// ── 工具名常量（与代理 server.mjs 的转发白名单一一对应）──
const TOOLS = {
  status: "ocdup_status",
  connect: "ocdup_connect",
  identify: "ocdup_identify_chip",
  flashInfo: "ocdup_flash_info",
  flashWrite: "ocdup_flash_write",
  flashErase: "ocdup_flash_erase_sector",
  halt: "ocdup_halt",
  resume: "ocdup_resume",
  reset: "ocdup_reset",
  disconnect: "ocdup_disconnect",
  setTarget: "ocdup_set_target",
  verifyImage: "ocdup_verify_image",
  runCommand: "ocdup_run_command",
  loadSvd: "ocdup_load_svd",
  readReg: "ocdup_read_peripheral_register",
  writeReg: "ocdup_write_peripheral_register",
} as const;

// ── 面板状态 ──
interface PanelState {
  tab: "status" | "flash" | "regs" | "log";
  peripheral: string; // 上次选中的外设
  status: BackendStatus | null;
  identity: ChipIdentity | null;
  banks: FlashBank[];
  geometry: SectorGeometry | null; // flash info 报告的扇区几何（拿不到就退回等分示意）
  flashOp: FlashOpResult | null;
  flashNote: string | null;
  flashRaw: string | null; // 最近一次 flash 操作的原文（详情区）
  flashKind: "write" | "erase" | "verify"; // 结论条用词随操作类型变
  statusNote: string | null; // 连接/刷新失败的红条（连接状态页）
  presets: string[]; // target_presets 清单（set_target 可选项）
  halted: boolean;
  coreRegs: Partial<Record<"pc" | "lr" | "sp" | "xpsr", string>>;
  // SVD
  svdLoaded: boolean;
  svdFile: string;
  svdHeader: string;
  peripherals: SvdPeripheral[];
  registers: SvdRegisterRow[];
  regRead: DecodedRegister | null;
  regError: string | null;
  regFilter: string; // 寄存器名/地址过滤（纯前端，不触发读取）
  // 时间线
  log: LogEntry[];
  logCopied: boolean;
  pollEnabled: boolean; // 自动刷新开关（默认关：避免给后端 traffic log 刷噪音）
  busy: string | null;
}

interface LogEntry {
  t: number;
  op: string;
  detail?: string;
  ok: boolean | null; // null = 进行中
  verdict?: string;
  text?: string; // 完整原文（详情区）
}

const state: PanelState = {
  tab: "status",
  peripheral: "",
  status: null,
  identity: null,
  banks: [],
  geometry: null,
  flashOp: null,
  flashNote: null,
  flashRaw: null,
  flashKind: "write",
  statusNote: null,
  presets: [],
  halted: false,
  coreRegs: {},
  svdLoaded: false,
  svdFile: "",
  svdHeader: "",
  peripherals: [],
  registers: [],
  regRead: null,
  regError: null,
  regFilter: "",
  log: [],
  logCopied: false,
  pollEnabled: false,
  busy: null,
};

let app: App;
let t: T;
interface WidgetState {
  tab?: string;
  peripheral?: string;
  svdFile?: string;
}

let widgetState: WidgetState = {};
let confirmResolve: ((v: boolean) => void) | null = null;

// ── 工具调用封装：带时间线记录 + 错误归一 ──
/**
 * opts.record=false  → 不写时间线（identify/flash_info 这类伴生调用）
 * opts.silent=true   → 连 busy 指示与中途重绘都跳过（轮询用：绝不能冲掉用户输入）
 */
async function call(
  tool: string,
  args: Record<string, unknown> = {},
  opts: { record?: boolean; silent?: boolean } = {},
): Promise<string> {
  const record = opts.record ?? true;
  const silent = opts.silent ?? false;
  const entry: LogEntry = { t: Date.now(), op: label(tool, args), ok: null };
  if (record) {
    state.log.push(entry);
    if (!silent) render();
  }
  if (!silent) {
    state.busy = tool;
    render();
  }
  try {
    const res = await app.callServerTool(
      { name: tool, arguments: args },
      tool === TOOLS.flashWrite || tool === TOOLS.flashErase
        ? { onprogress: (p) => flashProgress(p, entry) }
        : undefined,
    );
    const text = (res.content ?? [])
      .map((c) => (c.type === "text" ? c.text : JSON.stringify(c)))
      .join("\n");
    entry.ok = !res.isError;
    entry.text = text;
    entry.verdict = shortVerdict(text);
    return text;
  } catch (err) {
    const text = `ERROR: ${String((err as Error)?.message ?? err)}`;
    entry.ok = false;
    entry.text = text;
    entry.verdict = t.errorTitle;
    return text;
  } finally {
    if (!silent) {
      state.busy = null;
      if (record) render();
    }
  }
}

function label(tool: string, args: Record<string, unknown>): string {
  const name = tool.replace(/^ocdup_/, "");
  const keys = ["file", "address", "name", "value", "mode", "peripheral", "first", "last", "bank", "timeoutMs"];
  const parts: string[] = [];
  for (const k of keys) {
    if (args[k] !== undefined && args[k] !== "" && args[k] !== null) {
      parts.push(`${k}=${String(args[k])}`);
      if (parts.length >= 3) break;
    }
  }
  return parts.length ? `${name}(${parts.join(", ")})` : name;
}

function shortVerdict(text: string): string {
  const v = parseFlashOpResult(text);
  const map: Record<string, string> = {
    verified: t.verdictVerified,
    verify_failed: t.verdictVerifyFailed,
    write_failed: t.verdictWriteFailed,
    inconclusive: t.verdictInconclusive,
    unverified: t.verdictUnverified,
    erased: t.verdictErased,
  };
  if (map[v.verdict]) return map[v.verdict];
  const err = firstErrorLine(text);
  if (err) return err;
  return t.ok;
}

function flashProgress(p: { progress?: number; total?: number; message?: string }, entry: LogEntry) {
  if (p.message) {
    entry.detail = p.message;
    render();
  }
}

// ── 二次确认 ──
function confirmDialog(msg: string, detail?: string): Promise<boolean> {
  return new Promise((resolve) => {
    confirmResolve = resolve;
    const box = document.getElementById("confirm-box")!;
    (box.querySelector(".confirm-msg") as HTMLElement).textContent = msg;
    const d = box.querySelector(".confirm-detail") as HTMLElement;
    d.textContent = detail ?? "";
    d.style.display = detail ? "block" : "none";
    box.style.display = "flex";
    document.getElementById("confirm-ok")!.focus();
  });
}

function settleConfirm(v: boolean) {
  const box = document.getElementById("confirm-box")!;
  box.style.display = "none";
  confirmResolve?.(v);
  confirmResolve = null;
}

// ── widget state ──

async function saveState() {
  const caps = app.getHostCapabilities?.()?.experimental ?? {};
  if (!(caps as Record<string, unknown>)["zcode/widgetState"]) return;
  try {
    await app.request(
      {
        method: "ui/set-widget-state",
        params: { widgetState: { tab: state.tab, peripheral: state.peripheral, svdFile: state.svdFile } },
      },
      EmptyResultSchema,
    );
  } catch {
    /* 状态保存失败不打断操作 */
  }
}

// ── 各区块动作 ──
async function refreshAll() {
  const statusText = await call(TOOLS.status);
  const st = parseStatus(statusText);
  state.status = st;
  state.halted = anyHalted(st.targets);
  // 目标恢复运行后 core registers 快照就过期了；halted 时保留上次读到的现场
  if (!state.halted) state.coreRegs = {};
  state.statusNote = st.blocked ? firstErrorLine(statusText) : null;
  if (st.running) {
    const idText = await call(TOOLS.identify, {}, { record: false });
    if (!/^(ERROR|BLOCKED):/m.test(idText)) {
      state.identity = parseChipIdentity(idText);
    }
    const banksText = await call(TOOLS.flashInfo, {}, { record: false });
    if (!/^(ERROR|BLOCKED):/m.test(banksText)) {
      state.banks = parseFlashBanks(banksText);
      await refreshGeometry();
    }
  } else {
    state.identity = null;
    state.banks = [];
    state.geometry = null;
  }
  render();
}

async function doConnect() {
  // 「连接」必须真的调 connect（会按 interface_cfg/target_cfg 拉起 OpenOCD）；
  // v0.4.2 之前这里错绑到 refreshAll，后端没起来时点它永远连不上。
  const text = await call(TOOLS.connect);
  state.statusNote = /^(ERROR|BLOCKED):/m.test(text) ? firstErrorLine(text) : null;
  state.flashNote = state.statusNote;
  if (state.statusNote) {
    render();
    return;
  }
  await refreshAll();
}

async function doHalt() {
  if (!(await confirmDialog(t.confirmHalt))) return;
  const text = await call(TOOLS.halt);
  if (!/^ERROR:/m.test(text)) {
    state.halted = true;
    state.coreRegs = parseCoreRegs(text);
    if (state.status) state.status.targets = state.status.targets.map((r) => ({ ...r, state: "halted" }));
  }
  render();
}

async function doResume() {
  if (!(await confirmDialog(t.confirmResume))) return;
  const text = await call(TOOLS.resume);
  if (!/^ERROR:/m.test(text) && state.status) {
    state.halted = false;
    state.status.targets = state.status.targets.map((r) => ({ ...r, state: "running" }));
  }
  render();
}

async function doResetRun() {
  if (!(await confirmDialog(t.confirmReset))) return;
  const text = await call(TOOLS.reset, { mode: "run" });
  if (!/^ERROR:/m.test(text) && state.status) {
    state.halted = false;
    state.status.targets = state.status.targets.map((r) => ({ ...r, state: "running" }));
  }
  render();
}

async function doFlashWrite() {
  const file = (document.getElementById("burn-file") as HTMLInputElement).value.trim();
  const address = (document.getElementById("burn-address") as HTMLInputElement).value.trim();
  const verify = (document.getElementById("burn-verify") as HTMLInputElement).checked;
  const reset = (document.getElementById("burn-reset") as HTMLInputElement).checked;
  if (!file) return;
  if (!(await confirmDialog(t.burnConfirm, file))) return;
  const args: Record<string, unknown> = { file, verify, reset };
  if (address) args.address = address;
  const text = await call(TOOLS.flashWrite, args);
  const r = parseFlashOpResult(text);
  state.flashOp = r;
  state.flashRaw = text;
  state.flashKind = "write";
  state.flashNote = firstErrorLine(text);
  render();
}

async function doFlashErase() {
  const bank = Number((document.getElementById("erase-bank") as HTMLInputElement).value || "0");
  const first = Number((document.getElementById("erase-first") as HTMLInputElement).value || "0");
  const last = Number((document.getElementById("erase-last") as HTMLInputElement).value || "-1");
  if (!(await confirmDialog(t.eraseConfirm, `bank ${bank}, sectors ${first}..${last === -1 ? "last" : last}`))) return;
  const text = await call(TOOLS.flashErase, { bank, first, last });
  const r = parseFlashOpResult(text);
  state.flashOp = r;
  state.flashRaw = text;
  state.flashKind = "erase";
  state.flashNote = firstErrorLine(text);
  render();
}

async function refreshBanks() {
  const text = await call(TOOLS.flashInfo, {}, { record: false });
  if (/^(ERROR|BLOCKED):/m.test(text)) {
    state.flashNote = firstErrorLine(text);
  } else {
    state.flashNote = null;
    state.banks = parseFlashBanks(text);
    await refreshGeometry();
  }
  render();
}

/**
 * 读扇区几何：`flash info <bank>` 是唯一的来源（flash_info 只给容量）。
 * 拿不到（只读会话 / allow_raw_command=false / 驱动不报）就退回等分示意，不报错。
 */
async function refreshGeometry() {
  if (!state.banks.length) {
    state.geometry = null;
    return;
  }
  const text = await call(TOOLS.runCommand, { command: `flash info ${state.banks[0].index}` }, { record: false });
  if (/^(ERROR|BLOCKED):/m.test(text)) {
    state.geometry = null;
    return;
  }
  state.geometry = parseSectorGeometry(text);
}

/** verify_image：只读比对（不写 flash），结果进时间线 + flash 结论条 */
async function doVerifyImage() {
  const file = (document.getElementById("verify-file") as HTMLInputElement)?.value.trim() ?? "";
  const address = (document.getElementById("verify-address") as HTMLInputElement)?.value.trim() ?? "";
  if (!file) {
    state.flashNote = t.verifyNeedFile;
    render();
    return;
  }
  const args: Record<string, unknown> = { file };
  if (address) args.address = address;
  const text = await call(TOOLS.verifyImage, args);
  const r = parseFlashOpResult(text);
  // verify_image 成功时文本是 "verified: ... (N bytes)"；失败给 VERIFY FAILED
  state.flashOp = r.verdict === "other" ? { verdict: "verified", bytes: r.bytes, path: r.path, loadAddress: r.loadAddress } : r;
  state.flashRaw = text;
  state.flashKind = "verify";
  state.flashNote = firstErrorLine(text);
  render();
}

/** disconnect：停掉 ocdup 自拉的后端、释放探针（交给 DSH 等） */
async function doDisconnect() {
  if (!(await confirmDialog(t.confirmDisconnect))) return;
  const text = await call(TOOLS.disconnect);
  state.statusNote = /^(ERROR|BLOCKED):/m.test(text) ? firstErrorLine(text) : null;
  await refreshAll();
}

/** set_target：按 target_presets 一键切换并重启后端（失配时用） */
async function doSetTarget() {
  const input = document.getElementById("target-name") as HTMLInputElement | null;
  const name = (input?.value.trim() || state.identity?.family || "").trim();
  if (!name) return;
  if (!(await confirmDialog(t.setTargetConfirm.replace("{name}", name)))) return;
  const text = await call(TOOLS.setTarget, { name });
  state.statusNote = /^(ERROR|BLOCKED):/m.test(text) ? firstErrorLine(text) : null;
  if (!state.statusNote) {
    // 切换会重启后端：等它起来再刷一次状态
    await new Promise((r) => setTimeout(r, 800));
  }
  await refreshAll();
}

/** 拉取 target_presets 清单（set_target list），把可选项填进下拉 */
async function loadTargetPresets() {
  const text = await call(TOOLS.setTarget, { name: "list" }, { record: false });
  if (/^(ERROR|BLOCKED):/m.test(text)) return;
  const names: string[] = [];
  for (const line of text.split("\n")) {
    const m = line.trim().match(/^([A-Za-z0-9_.-]+)\s*(->|:)\s*\S+/);
    if (m) names.push(m[1]);
  }
  state.presets = names;
  render();
}

async function loadSvd(file?: string, keepPeripheral = true) {
  const args: Record<string, unknown> = {};
  const fileInput = (document.getElementById("svd-file") as HTMLInputElement | null);
  const path = (file ?? fileInput?.value?.trim() ?? "") || state.svdFile || "";
  if (path) args.file = path;
  const text = await call(TOOLS.loadSvd, args);
  if (/^ERROR:/m.test(text)) {
    state.svdLoaded = false;
    state.svdHeader = "";
    state.peripherals = [];
    state.flashNote = firstErrorLine(text);
    render();
    return;
  }
  // 记住成功加载过的路径：关面板重开时自动重载，不用再手输
  if (path) state.svdFile = path;
  const parsed = parseSvdList(text);
  state.svdLoaded = true;
  state.svdHeader = parsed.header;
  state.peripherals = parsed.peripherals;
  const keep = keepPeripheral && parsed.peripherals.some((p) => p.name === state.peripheral);
  if (!keep) state.peripheral = parsed.peripherals[0]?.name ?? "";
  await selectPeripheral(state.peripheral);
  await saveState();
  render();
}

async function selectPeripheral(name: string) {
  state.peripheral = name;
  state.regRead = null;
  state.regError = null;
  if (!name) {
    state.registers = [];
    return;
  }
  const text = await call(TOOLS.loadSvd, { peripheral: name }, { record: false });
  if (/^ERROR:/m.test(text)) {
    state.registers = [];
    state.regError = firstErrorLine(text);
    return;
  }
  state.registers = parseSvdRegisterList(text).registers;
}

async function readRegister(name: string) {
  state.regRead = null;
  state.regError = null;
  const text = await call(TOOLS.readReg, { name });
  if (/^ERROR:/m.test(text)) {
    state.regError = firstErrorLine(text) ?? t.regReadFailed;
    render();
    return;
  }
  state.regRead = parseDecodedRegister(name, text);
  if (!state.regRead) state.regError = text.split("\n")[0] ?? "";
  render();
}

async function writeRegister(name: string, row: SvdRegisterRow) {
  const input = document.getElementById(`reg-val-${name}`) as HTMLInputElement;
  const value = input?.value.trim() ?? "";
  if (!value) return;
  if (!(await confirmDialog(t.writeConfirm.replace("{reg}", name).replace("{addr}", row.address).replace("{val}", value)))) return;
  const text = await call(TOOLS.writeReg, { name, value });
  if (!/^ERROR:/m.test(text)) await readRegister(name);
  render();
}

// ── 渲染 ──
const root = () => document.getElementById("app")!;

function render() {
  document.documentElement.dataset.theme = app?.getHostContext?.()?.theme ?? "light";
  root().innerHTML = `
    ${renderTabs()}
    <div class="body">
      ${state.tab === "status" ? renderStatus() : ""}
      ${state.tab === "flash" ? renderFlash() : ""}
      ${state.tab === "regs" ? renderRegs() : ""}
      ${state.tab === "log" ? renderLog() : ""}
    </div>
    ${renderConfirm()}
    ${renderBusy()}
  `;
  wire();
}

function renderTabs() {
  const tab = (id: typeof state.tab, label: string) =>
    `<button class="tab ${state.tab === id ? "active" : ""}" data-tab="${id}">${esc(label)}</button>`;
  const coreDot = state.halted
    ? `<span class="dot halted"></span>`
    : `<span class="dot running"></span>`;
  return `<div class="tabs">
    ${tab("status", t.tabStatus)}${tab("flash", t.tabFlash)}${tab("regs", t.tabRegs)}${tab("log", t.tabLog)}
    <span class="spacer"></span>
    ${coreDot}
    <label class="check small" title="${esc(t.pollHint)}">
      <input type="checkbox" id="chk-poll" ${state.pollEnabled ? "checked" : ""}> ${esc(t.pollLabel)}
    </label>
    <button class="mini" id="btn-refresh">${esc(t.btnRefresh)}</button>
  </div>`;
}

function renderStatus() {
  const st = state.status;
  const regs = Object.entries(state.coreRegs);
  return `
  <section class="card">
    <h3>${esc(t.backend)}</h3>
    <div class="row">
      <span class="badge ${st?.blocked ? "warn" : st?.running ? "ok" : "err"}">${esc(
        st?.blocked ? t.backendBlocked : st?.running ? t.backendRunning : st?.hint ? t.backendDown : t.backendChecking,
      )}</span>
      ${st?.address ? `<span class="muted">${esc(st.address)}</span>` : ""}
      ${st?.channel ? `<span class="muted">${esc(st.channel)}</span>` : ""}
    </div>
    <div class="row wrap">
      <button id="btn-connect">${esc(t.btnConnect)}</button>
      <button id="btn-halt">${esc(t.btnHalt)}</button>
      <button id="btn-resume">${esc(t.btnResume)}</button>
      <button id="btn-reset">${esc(t.btnResetRun)}</button>
      <button id="btn-disconnect" class="danger">${esc(t.btnDisconnect)}</button>
    </div>
    ${st?.hint ? `<p class="hint">${esc(st.hint)}</p>` : ""}
    ${state.statusNote ? `<p class="hint warn">${esc(state.statusNote)}</p>` : ""}
    ${st?.version ? `<p class="muted">${esc(t.version)}: ${esc(st.version)}</p>` : ""}
  </section>

  <section class="card">
    <h3>${esc(t.core)}</h3>
    ${renderTargets()}
    ${regs.length ? `<p class="muted mono">${regs.map(([k, v]) => `${k}=${v}`).join("  ")}</p>` : ""}
    ${state.halted ? "" : `<p class="hint">${esc(t.needHaltFirst)}</p>`}
  </section>

  <section class="card">
    <h3>${esc(t.chip)}</h3>
    ${renderIdentity()}
  </section>`;
}

function renderTargets() {
  const rows = state.status?.targets ?? [];
  if (!rows.length) return `<p class="muted">—</p>`;
  return `<table class="grid"><tbody>${rows
    .map((r) => {
      const stateClass = r.state === "halted" ? "warn" : r.state === "running" ? "ok" : "muted";
      return `<tr><td class="mono">${esc(r.name)}</td><td>${esc(r.type)}</td>
        <td><span class="badge ${stateClass}">${esc(r.state || t.unknown)}</span></td></tr>`;
    })
    .join("")}</tbody></table>`;
}

function renderIdentity() {
  const id = state.identity;
  if (!id) return `<p class="muted">—</p>`;  const chips: string[] = [];
  if (id.family) chips.push(id.family);
  if (id.model) chips.push(id.model);
  if (!chips.length) chips.push(t.chipUnknown);
  const rows: Array<[string, string]> = [];
  if (id.cpuidRaw) rows.push([t.cpuid, `${id.cpuidRaw}${id.cpuidDesc ? ` — ${id.cpuidDesc}` : ""}`]);
  if (id.devId) rows.push([t.devId, `${id.devId}${id.revId ? ` (rev ${id.revId})` : ""}`]);
  if (id.family) rows.push([t.family, id.family]);
  if (id.model) rows.push(["型号", `${id.model}（${t.modelFromCfg}）`]);
  return `
    <p class="chip-line">${chips.map((c) => `<span class="chip">${esc(c)}</span>`).join(" ")}</p>
    ${rows.length ? `<table class="grid"><tbody>${rows.map(([k, v]) => `<tr><td class="muted">${esc(k)}</td><td class="mono">${esc(v)}</td></tr>`).join("")}</tbody></table>` : ""}
    ${id.mismatch ? `<p class="hint warn">${esc(t.targetMismatch)}</p>${renderSetTarget()}` : ""}
    ${id.readFailures.length ? `<details><summary class="muted">${esc(t.details)}</summary><pre class="raw">${esc(id.readFailures.join("\n"))}</pre></details>` : ""}`;
}

/** target 失配时给一键切换入口（预设名来自 ocd.json 的 target_presets） */
function renderSetTarget() {
  const suggestions = state.presets.length
    ? `<datalist id="target-presets">${state.presets.map((n) => `<option value="${esc(n)}"></option>`).join("")}</datalist>`
    : "";
  return `
    <div class="card" style="margin-top:8px;padding:8px 10px">
      <p class="muted small" style="margin:0 0 6px">${esc(t.setTargetTitle)}</p>
      <div class="row wrap">
        <input id="target-name" list="target-presets" placeholder="${esc(t.setTargetPlaceholder)}"
               value="${esc(state.identity?.family ?? "")}">
        ${suggestions}
        <button id="btn-set-target" class="primary">${esc(t.setTargetGo)}</button>
        <button id="btn-presets" class="link">${esc(t.btnRefresh)}</button>
      </div>
    </div>`;
}

function renderFlash() {
  const banks = state.banks;
  const op = state.flashOp;
  const verdictKey = op
    ? { verified: "verified", verify_failed: "verify_failed", write_failed: "write_failed", inconclusive: "inconclusive", unverified: "unverified", erased: "erased", other: "other" }[op.verdict]
    : null;
  const verdictText =
    verdictKey && verdictKey !== "other"
      ? (t as unknown as Record<string, string>)[
          state.flashKind === "verify" && verdictKey === "verified" ? "verdictVerifyOk" : `verdict${cap(verdictKey)}`
        ]
      : null;
  return `
  <section class="card">
    <h3>${esc(t.flashTitle)}</h3>
    ${banks.length ? renderSectors(banks) : `<p class="muted">${esc(t.flashNone)}</p>`}
    ${op && verdictText ? `<p class="verdict ${op.verdict === "verified" || op.verdict === "erased" ? "ok" : op.verdict === "inconclusive" ? "warn" : "err"}">${esc(verdictText)}</p>` : ""}
    ${op?.bytes ? `<p class="muted">${op.bytes.toLocaleString()} ${esc(t.bytes)}${op.loadAddress ? ` @ ${esc(op.loadAddress)}` : ""}</p>` : ""}
    ${state.flashNote ? `<p class="hint warn">${esc(state.flashNote)}</p>` : ""}
    <div class="row wrap">
      <button id="btn-burn">${esc(t.btnBurn)}</button>
      <button id="btn-erase" class="danger">${esc(t.btnErase)}</button>
      <button id="btn-verify">${esc(t.btnVerify)}</button>
      <button id="btn-flashinfo">${esc(t.btnFlashInfo)}</button>
    </div>
    ${state.flashRaw ? `<details><summary class="muted">${esc(t.details)}</summary><pre class="raw">${esc(state.flashRaw)}</pre></details>` : ""}
  </section>

  <section class="card" id="verify-form" style="display:none">
    <h3>${esc(t.verifyTitle)}</h3>
    <label>${esc(t.verifyFile)}<input id="verify-file" placeholder="build/fw.hex"></label>
    <label>${esc(t.verifyAddress)}<input id="verify-address" placeholder="0x08000000"></label>
    <div class="row"><button id="verify-go" class="primary">${esc(t.verifyGo)}</button>
    <button class="link" data-close="verify-form">${esc(t.cancel)}</button></div>
  </section>

  <section class="card" id="burn-form" style="display:none">
    <h3>${esc(t.burnTitle)}</h3>
    <label>${esc(t.burnFile)}<input id="burn-file" placeholder="build/fw.hex"></label>
    <label>${esc(t.burnAddress)}<input id="burn-address" placeholder="0x08000000"></label>
    <label class="check"><input type="checkbox" id="burn-verify" checked> ${esc(t.burnVerify)}</label>
    <label class="check"><input type="checkbox" id="burn-reset" checked> ${esc(t.burnReset)}</label>
    <div class="row"><button id="burn-go" class="primary">${esc(t.burnGo)}</button>
    <button class="link" data-close="burn-form">${esc(t.cancel)}</button></div>
  </section>

  <section class="card" id="erase-form" style="display:none">
    <h3>${esc(t.eraseTitle)}</h3>
    <div class="row">
      <label>${esc(t.eraseBank)}<input id="erase-bank" value="0" size="4"></label>
      <label>${esc(t.eraseFirst)}<input id="erase-first" value="0" size="4"></label>
      <label>${esc(t.eraseLast)}<input id="erase-last" value="-1" size="4"></label>
    </div>
    <p class="hint warn">${esc(t.eraseOffHint)}</p>
    <div class="row"><button id="erase-go" class="danger">${esc(t.eraseGo)}</button>
    <button class="link" data-close="erase-form">${esc(t.cancel)}</button></div>
  </section>`;
}

/**
 * 扇区图。两种模式：
 * - 有 geometry（`flash info <bank>` 报过 sector size）：每个块 = 一个真实扇区，
 *   按扇区数等比压缩到最多 64 块；擦除操作按 [first..last] 精确点亮对应扇区；
 * - 无 geometry：退回按容量等分的示意块（旧行为），只按 loadAddress 整 bank 高亮。
 */
function renderSectors(banks: FlashBank[]): string {
  const total = banks.reduce((s, b) => s + b.size, 0);
  const op = state.flashOp;
  const g = state.geometry;
  const opAddr = op?.loadAddress ? parseInt(op.loadAddress, 16) : null;
  const erase = op?.verdict === "erased" ? op.erased : undefined;
  const MAX_BLOCKS = 64;

  return (
    banks
      .map((b) => {
        const pct = total > 0 ? ((b.size / total) * 100).toFixed(1) : "0";
        const sectorCount = g ? sectorsInBank(g, b.size) : 0;
        const blocks = g && sectorCount > 0 ? Math.min(MAX_BLOCKS, sectorCount) : b.size > 0 ? 16 : 1;
        const perBlock = b.size > 0 ? b.size / blocks : 0;

        const blocksHtml =
          b.size > 0
            ? Array.from({ length: blocks }, (_, i) => {
                const start = b.base + Math.round(i * perBlock);
                const end = b.base + Math.round((i + 1) * perBlock) - 1;
                let hit = false;
                let why = "";
                if (erase && erase.bank === b.index) {
                  // 扇区号 → 块号（多扇区压成一块时，整块范围与擦除范围相交就点亮）
                  const firstSector = erase.first;
                  const lastSector =
                    erase.lastText === "last" || erase.lastText === "-1" || /end/i.test(erase.lastText)
                      ? sectorCount - 1
                      : Number(erase.lastText);
                  const blockStartSector = Math.floor((i * (sectorCount || blocks)) / blocks);
                  const blockEndSector = Math.floor(((i + 1) * (sectorCount || blocks)) / blocks) - 1;
                  hit = blockEndSector >= firstSector && blockStartSector <= lastSector;
                  why = `sector ${firstSector}..${lastSector}`;
                } else if (opAddr !== null && !erase) {
                  // 烧录：按 loadAddress + 已写字节数估算覆盖范围
                  const len = op?.bytes ?? 0;
                  const opStart = opAddr;
                  const opEnd = len > 0 ? opAddr + len - 1 : opAddr;
                  hit = opEnd >= start && opStart <= end;
                  why = len > 0 ? `0x${opStart.toString(16)}..0x${opEnd.toString(16)}` : `0x${opStart.toString(16)}`;
                }
                const label = g
                  ? `sector ${Math.floor((i * sectorCount) / blocks)}..${Math.max(0, Math.floor(((i + 1) * sectorCount) / blocks) - 1)} @ 0x${start.toString(16)}`
                  : `0x${start.toString(16)}`;
                return `<span class="sblk ${hit ? "hit" : ""}" title="${esc(`${label}${why && hit ? ` — ${why}` : ""}`)}"></span>`;
              }).join("")
            : `<span class="sblk err" title="size 0"></span>`;

        const geoNote = g && sectorCount > 0 ? `${sectorCount} × ${formatBytes(g.sectorSize)}` : "size 0";
        return `
      <div class="bank">
        <div class="bank-head"><span class="mono">${esc(t.bank)} ${b.index}</span>
          <span class="muted mono">${esc(b.driver)}</span></div>
        <div class="sector-row" style="flex:0 0 ${pct}%">${blocksHtml}</div>
        <div class="muted mono small">0x${b.base.toString(16).toUpperCase()}${
          b.size ? ` · ${formatBytes(b.size)}${g ? ` · ${geoNote}` : ""}` : " · size 0"
        }</div>
      </div>`;
      })
      .join("") + `<p class="muted small">${esc(g ? t.highlightNoteReal : t.highlightNote)}</p>`
  );
}

function renderRegs() {
  if (!state.svdLoaded) {
    return `
    <section class="card">
      <h3>SVD</h3>
      <p class="muted">${esc(t.regsNoSvd)}</p>
      <label>${esc(t.regsFile)}<input id="svd-file" placeholder="AT32F403.svd" value="${esc(state.svdFile)}"></label>
      <div class="row"><button id="svd-load" class="primary">${esc(t.regsLoad)}</button></div>
    </section>`;
  }
  const allRegs = state.registers;
  const regs = allRegs;
  const read = state.regRead;
  const running = !state.halted;
  return `
  <section class="card">
    <h3>SVD</h3>
    <div class="row wrap">
      <select id="svd-pick">${state.peripherals
        .map((p) => `<option value="${esc(p.name)}" ${p.name === state.peripheral ? "selected" : ""}>${esc(p.name)} (${p.registerCount})</option>`)
        .join("")}</select>
      <button id="svd-reload">${esc(t.regsReload)}</button>
      ${state.svdHeader ? `<span class="muted small">${esc(state.svdHeader)}</span>` : ""}
    </div>
  </section>
  <section class="card">
    <h3>${esc(state.peripheral)}</h3>
    ${running ? `<p class="hint warn">${esc(t.regsRunning)}</p>` : ""}
    ${state.regError ? `<p class="hint warn">${esc(state.regError)}</p>` : ""}
    <div class="row">
      <input id="reg-filter" placeholder="${esc(t.regsFilterHint)}" value="${esc(state.regFilter)}">
      <span class="muted small" id="reg-count"></span>
    </div>
    <p class="muted" id="reg-filter-empty" style="display:none">${esc(t.regsFilterEmpty)}</p>
    ${regs.length ? `<table class="grid regs"><thead><tr><th>${esc(t.regsPick)}</th><th>${esc("addr")}</th><th>${esc("access")}</th><th>${esc("value")}</th><th></th></tr></thead><tbody>
      ${regs.map((r) => {
        const full = `${state.peripheral}.${r.name}`;
        const isRead = read && read.fullName === full;
        const writable = r.access !== "read-only";
        return `<tr data-reg-row data-search="${esc(`${r.name} ${r.address}`.toLowerCase())}">
          <td class="mono">${esc(r.name)}</td>
          <td class="muted mono">${esc(r.address)}</td>
          <td class="muted">${esc(r.access)}</td>
          <td class="mono val">${isRead ? esc(read.rawValue) : ""}</td>
          <td class="actions">
            <button class="mini read-btn" data-reg="${esc(full)}" ${running ? "disabled" : ""}>${esc(t.btnReadReg)}</button>
            <input id="reg-val-${esc(full)}" placeholder="0x" size="8" ${writable ? "" : "disabled"}>
            <button class="mini danger write-btn" data-reg="${esc(full)}" data-addr="${esc(r.address)}" ${running || !writable ? "disabled" : ""} ${writable ? "" : `title="${esc(t.writeAccessRO)}"`}>${esc(t.btnWriteReg)}</button>
          </td>
        </tr>${isRead && read.fields.length ? `<tr class="fields" data-reg-row data-search="${esc(`${r.name} ${r.address}`.toLowerCase())}"><td colspan="5">
          <div class="field-row">${read.fields.map((f) => `<span class="field"><b>${esc(f.name)}</b> ${esc(f.bits)} = <span class="mono">${esc(f.value)}</span></span>`).join("")}</div>
          ${read.resetNote ? `<p class="muted small">${esc(read.resetNote)}</p>` : ""}
        </td></tr>` : ""}`;
      }).join("")}
    </tbody></table>` : `<p class="muted">${esc(t.regsEmpty)}</p>`}
  </section>`;
}

/** 按输入框内容显隐寄存器行（纯前端，不重渲染，焦点不丢） */
function applyRegFilter() {
  const input = document.getElementById("reg-filter") as HTMLInputElement | null;
  if (!input) return;
  const q = input.value.trim().toLowerCase();
  state.regFilter = input.value;
  const rows = document.querySelectorAll<HTMLElement>("[data-reg-row]");
  let shown = 0;
  for (const row of rows) {
    const hit = !q || (row.dataset.search ?? "").includes(q);
    row.style.display = hit ? "" : "none";
    if (hit) shown += 1;
  }
  const total = document.querySelectorAll('tr[data-reg-row][data-search]').length;
  const count = document.getElementById("reg-count");
  if (count) count.textContent = q ? `${shown}/${rows.length}` : "";
  const empty = document.getElementById("reg-filter-empty");
  if (empty) empty.style.display = q && shown === 0 ? "block" : "none";
  void total;
}

/** 时间线导出为纯文本（沙箱里用 clipboard API，失败退回下载） */
async function copyLog() {
  const lines = state.log.map((e) => {
    const t0 = new Date(e.t).toISOString();
    const status = e.ok === null ? "PENDING" : e.ok ? "OK" : "FAIL";
    const head = `[${t0}] ${status} ${e.op}${e.verdict ? ` — ${e.verdict}` : ""}`;
    return e.text ? `${head}\n${e.text.split("\n").map((l) => `    ${l}`).join("\n")}` : head;
  });
  const text = lines.join("\n");
  try {
    await navigator.clipboard.writeText(text);
    state.logCopied = true;
    render();
    setTimeout(() => {
      state.logCopied = false;
      render();
    }, 1500);
  } catch {
    state.flashNote = text.slice(0, 200);
    render();
  }
}

function renderLog() {
  const entries = [...state.log].reverse();
  return `
  <section class="card">
    <div class="row"><h3>${esc(t.tabLog)}</h3><span class="spacer"></span>
      <button class="mini" id="log-copy" ${entries.length ? "" : "disabled"}>${esc(state.logCopied ? t.copied : t.logCopy)}</button>
      <button class="mini" id="log-clear">${esc(t.logClear)}</button></div>
    ${entries.length ? `<table class="grid log"><tbody>${entries
      .map((e, i) => {
        const cls = e.ok === null ? "pending" : e.ok ? "ok" : "err";
        const time = new Date(e.t).toLocaleTimeString();
        return `<tr class="log-row ${cls}" data-i="${state.log.length - 1 - i}">
          <td class="muted mono small">${esc(time)}</td>
          <td class="mono">${esc(e.op)}</td>
          <td>${e.verdict ? `<span class="badge ${cls}">${esc(e.verdict)}</span>` : `<span class="badge pending">${esc(t.busy)}</span>`}</td>
        </tr>${e.text ? `<tr class="log-detail" data-i="${state.log.length - 1 - i}" style="display:none"><td colspan="3"><pre class="raw">${esc(e.text)}</pre></td></tr>` : ""}`;
      })
      .join("")}</tbody></table>` : `<p class="muted">${esc(t.logEmpty)}</p>`}
  </section>`;
}

function renderConfirm() {
  return `<div id="confirm-box" class="modal" style="display:none">
    <div class="modal-box">
      <p class="confirm-msg"></p>
      <pre class="confirm-detail mono"></pre>
      <div class="row end">
        <button id="confirm-ok" class="danger">${esc(t.confirm)}</button>
        <button id="confirm-cancel">${esc(t.cancel)}</button>
      </div>
    </div>
  </div>`;
}

function renderBusy() {
  if (!state.busy) return "";
  return `<div class="busy"><span class="spinner"></span>${esc(t.busy)}</div>`;
}

// ── 事件接线 ──
function wire() {
  const q = <T extends HTMLElement>(sel: string) => document.querySelector<T>(sel);
  for (const btn of document.querySelectorAll<HTMLButtonElement>("[data-tab]")) {
    btn.onclick = async () => {
      state.tab = btn.dataset.tab as typeof state.tab;
      await saveState();
      render();
    };
  }
  const bind = (sel: string, fn: () => void) => {
    const el = q<HTMLButtonElement>(sel);
    if (el) el.onclick = fn;
  };
  bind("#btn-refresh", () => void refreshAll());
  const pollBox = q<HTMLInputElement>("#chk-poll");
  if (pollBox) {
    pollBox.onchange = () => {
      state.pollEnabled = pollBox.checked;
      if (state.pollEnabled) startPolling();
      else stopPolling();
      render();
    };
  }
  bind("#btn-connect", () => void doConnect());
  bind("#btn-halt", () => void doHalt());
  bind("#btn-resume", () => void doResume());
  bind("#btn-reset", () => void doResetRun());
  bind("#btn-disconnect", () => void doDisconnect());
  bind("#btn-burn", () => toggle("#burn-form"));
  bind("#btn-erase", () => toggle("#erase-form"));
  bind("#btn-verify", () => toggle("#verify-form"));
  bind("#btn-flashinfo", () => void refreshBanks());
  bind("#burn-go", () => void doFlashWrite());
  bind("#erase-go", () => void doFlashErase());
  bind("#verify-go", () => void doVerifyImage());
  bind("#btn-set-target", () => void doSetTarget());
  bind("#btn-presets", () => void loadTargetPresets());
  bind("#svd-load", () => void loadSvd());
  bind("#svd-reload", () => void loadSvd(undefined, false));
  // 过滤是纯前端：只显隐行，不重渲染（否则输入焦点每次都会丢）
  const filter = q<HTMLInputElement>("#reg-filter");
  if (filter) {
    filter.oninput = () => applyRegFilter();
    applyRegFilter();
  }
  bind("#log-clear", () => {
    void (async () => {
      if (await confirmDialog(t.logConfirmClear)) {
        state.log = [];
        render();
      }
    })();
  });
  bind("#log-copy", () => void copyLog());
  bind("#confirm-ok", () => settleConfirm(true));
  bind("#confirm-cancel", () => settleConfirm(false));

  for (const el of document.querySelectorAll<HTMLElement>("[data-close]")) {
    el.onclick = () => {
      document.getElementById(el.dataset.close!)!.style.display = "none";
    };
  }

  const pick = q<HTMLSelectElement>("#svd-pick");
  if (pick)
    pick.onchange = async () => {
      await selectPeripheral(pick.value);
      await saveState();
      render();
    };

  for (const btn of document.querySelectorAll<HTMLButtonElement>(".read-btn")) {
    btn.onclick = () => void readRegister(btn.dataset.reg!);
  }
  for (const btn of document.querySelectorAll<HTMLButtonElement>(".write-btn")) {
    btn.onclick = async () => {
      const reg = btn.dataset.reg!;
      const row = state.registers.find((r) => `${state.peripheral}.${r.name}` === reg);
      if (row) await writeRegister(reg, row);
    };
  }

  for (const row of document.querySelectorAll<HTMLTableRowElement>(".log-row")) {
    row.onclick = () => {
      const detail = document.querySelector<HTMLTableRowElement>(`.log-detail[data-i="${row.dataset.i}"]`);
      if (detail) detail.style.display = detail.style.display === "none" ? "table-row" : "none";
    };
  }
}

function toggle(sel: string) {
  const el = document.getElementById(sel.slice(1))!;
  el.style.display = el.style.display === "none" ? "block" : "none";
}

function esc(s: unknown): string {
  return String(s ?? "")
    .replace(/&/g, "&amp;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;")
    .replace(/"/g, "&quot;");
}

function cap(s: string): string {
  return s.replace(/(^|_)(\w)/g, (_, p, c) => p + c.toUpperCase());
}

function formatBytes(n: number): string {
  if (n >= 1024 * 1024) return `${(n / 1024 / 1024).toFixed(2)} MiB`;
  if (n >= 1024) return `${(n / 1024).toFixed(1)} KiB`;
  return `${n} B`;
}

// ── 启动 ──
app = new App({ name: "ocdup-panel", version: "0.1.0" }, {});
// 处理器必须在 connect 之前注册，否则初始通知（toolresult 等）会丢
app.addEventListener("toolresult", () => render());
app.addEventListener("hostcontextchanged", () => {
  applyTheme();
  t = stringsFor(app.getHostContext?.()?.locale);
  render();
});
void main();

async function main() {
  await app.connect();

  widgetState =
    (app.getHostContext?.() as unknown as { "zcode/widgetState"?: WidgetState })?.["zcode/widgetState"] ?? {};
  state.tab = (["status", "flash", "regs", "log"] as const).includes(widgetState.tab as typeof state.tab)
    ? (widgetState.tab as typeof state.tab)
    : "status";
  state.peripheral = widgetState.peripheral ?? "";
  state.svdFile = widgetState.svdFile ?? "";

  t = stringsFor(app.getHostContext?.()?.locale);
  applyTheme();
  render();

  // 面板打开时若无后端信息则主动查一次（不阻塞渲染）
  if (!state.status) void refreshAll();

  // 上次加载过 SVD 就直接重载（寄存器页不再是「一次性」功能）；
  // 后端可能还没起来，失败只记日志不打扰。
  if (state.svdFile) void loadSvd(state.svdFile).catch(() => {});

  if (state.pollEnabled) startPolling();

  // 暴露给无头自测（window.__panel）
  (window as unknown as { __panel: unknown }).__panel = {
    state,
    app,
    render,
    refreshAll,
    call,
    loadSvd,
    doConnect,
    doDisconnect,
    doSetTarget,
    doVerifyImage,
    loadTargetPresets,
    startPolling,
    stopPolling,
    refreshStatusOnly,
  };
}

// ── 轻量轮询：只刷 status（不碰 identify/flash_info，代价小）──
let pollTimer: ReturnType<typeof setInterval> | null = null;

/** 面板可见时每 5s 刷一次 status；有操作在跑就跳过这一拍 */
function startPolling(intervalMs = 5000) {
  stopPolling();
  pollTimer = setInterval(() => {
    if (state.busy) return;
    void refreshStatusOnly();
  }, intervalMs);
  // 面板切到后台就别再打后端（省探针也无谓的日志噪音）
  document.addEventListener?.("visibilitychange", onVisibilityChange);
}

function onVisibilityChange() {
  if (document.visibilityState === "hidden") stopPolling();
  else if (!pollTimer) startPolling();
}

function stopPolling() {
  if (pollTimer) {
    clearInterval(pollTimer);
    pollTimer = null;
  }
}

async function refreshStatusOnly() {
  const text = await call(TOOLS.status, {}, { record: false, silent: true });
  const st = parseStatus(text);
  const wasRunning = state.status?.running ?? false;
  // 后端从「没起」变成「起来了」才补一次完整刷新，避免每 5s 拉 identify/flash_info
  const becameUp = st.running && !wasRunning;
  const before = statusSignature(state.status, state.halted);
  state.status = st;
  state.halted = anyHalted(st.targets);
  if (!state.halted) state.coreRegs = {};
  state.statusNote = st.blocked ? firstErrorLine(text) : null;
  if (!st.running) {
    state.identity = null;
    state.banks = [];
    state.geometry = null;
  }
  // 轮询绝不能把用户正在打字的输入框 / 打开着的确认框冲掉：render() 会重建 innerHTML。
  // 只有状态真的变了、且没有输入焦点/弹框时才重绘；否则数据静默更新，等下一次真实重绘体现。
  const changed = before !== statusSignature(state.status, state.halted);
  if (changed && !isUserInteracting()) render();
  if (becameUp) void refreshAll();
}

/** 状态签名：忽略 channel 里的 idle 秒数（每拍都在变，不该触发重绘） */
function statusSignature(st: BackendStatus | null, halted: boolean): string {
  if (!st) return "null";
  return [
    st.running,
    st.blocked,
    st.hint ?? "",
    st.version ?? "",
    st.address ?? "",
    halted,
    st.targets.map((r) => `${r.name}:${r.state}`).join(","),
  ].join("|");
}

/** 面板里是否有正在输入的控件或打开着的确认框——有就跳过自动重绘 */
function isUserInteracting(): boolean {
  const box = document.getElementById("confirm-box") as HTMLElement | null;
  // 确认框默认内联 display:none；只有 confirmDialog 显式改成 flex 才算「开着」
  if (box && box.style.display === "flex") return true;
  const active = document.activeElement as HTMLElement | null;
  if (!active) return false;
  const tag = active.tagName?.toUpperCase();
  return tag === "INPUT" || tag === "TEXTAREA" || tag === "SELECT";
}

function applyTheme() {
  const ctx = app.getHostContext?.();
  if (ctx?.theme) applyDocumentTheme(ctx.theme);
  if (ctx?.styles) {
    if (ctx.styles.variables) applyHostStyleVariables(ctx.styles.variables);
    if (ctx.styles.css?.fonts) applyHostFonts(ctx.styles.css.fonts);
  }
}
