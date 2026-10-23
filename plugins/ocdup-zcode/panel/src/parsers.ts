/**
 * Ocdup 面板文本解析器：把 ocdup 工具返回的人读文本还原成结构化数据。
 * 每个解析器对应一份 ocdup 输出格式（BackendTools/IdentifyTools/FlashTools/ExecTools/SvdFile），
 * 字段对不上时全部可空——面板永远显示原文兜底，不因解析失败丢信息。
 */

/**
 * ocdup 在 Windows 用 StringBuilder.AppendLine 输出 CRLF；面板侧一律先归一成 LF，
 * 否则 `/targets:\n/` 这类跨行正则会静默失配（v0.4.2 修：目标核卡片恒为 —）。
 */
export function normalizeNewlines(text: string | undefined | null): string {
  return (text ?? "").replace(/\r\n?/g, "\n");
}

export interface TargetRow {
  idx: string;
  name: string;
  type: string;
  endian: string;
  state: string; // running / halted / unknown…
}

/** status/connect 的 targets 摘要行：`0* at32f435zmt7 (cortex_m, little) running`；解析不出再退回原始表格行 */
export function parseTargets(text: string): TargetRow[] {
  const rows: TargetRow[] = [];
  for (const rawLine of normalizeNewlines(text).split("\n")) {
    const line = rawLine.trim();
    if (!line || line.startsWith("--") || line.includes("TargetName")) continue;
    let m = line.match(/^(\d+\*?)\s+(\S+)\s+\((\S+),\s*(\S+)\)\s+(\w+)$/);
    if (m) {
      rows.push({ idx: m[1], name: m[2], type: m[3], endian: m[4], state: m[5].toLowerCase() });
      continue;
    }
    // 原始表格行兜底：` 0* at32f435zmt7  cortex_m little at32f435zmt7.cpu running`
    const tokens = line.split(/\s+/);
    if (tokens.length >= 6 && /^\d+\*?$/.test(tokens[0])) {
      rows.push({
        idx: tokens[0],
        name: tokens[1],
        type: tokens[2],
        endian: tokens[3],
        state: (tokens[tokens.length - 1] ?? "").toLowerCase(),
      });
    }
  }
  return rows;
}

export function anyHalted(rows: TargetRow[]): boolean {
  return rows.some((r) => r.state === "halted");
}

export interface ChipIdentity {
  available: boolean;
  cpuidRaw?: string; // 0x412fc231
  cpuidDesc?: string; // Cortex-M4 (ARMv7-M/v8-M, implementer 'A', r0p1)
  devIdRaw?: string; // 0x10016415
  devId?: string; // 415
  revId?: string; // 1001
  family?: string; // AT32F403A
  model?: string; // 由 target_cfg 推定
  modelCfg?: string;
  mismatch?: string; // target mismatch 提示原文
  readFailures: string[]; // 读失败行原文
}

/**
 * identify_chip / connect 的 `chip identity:` 块。行格式（SvbTools/IdentifyTools 钉死）：
 *   chip identity:
 *     cpu (CPUID, ARM standard) @ 0xE000ED00: 0x412fc231
 *         -> Cortex-M4 (ARMv7-M/v8-M, implementer 'A', r0p1)
 *     dev_id (vendor register) @ 0xE0042000: 0x10016415
 *         -> dev_id 0x415, rev_id 0x1001
 *         -> family: AT32F403A
 *   model (from target_cfg target_at32f403axx.cfg): AT32F403A
 *   target mismatch: identified AT32F403A, ...
 */
export function parseChipIdentity(text: string): ChipIdentity {
  const id: ChipIdentity = { available: false, readFailures: [] };
  if (!text) return id;
  for (const rawLine of normalizeNewlines(text).split("\n")) {
    const line = rawLine.trim();
    let m = line.match(/^cpu \(CPUID, ARM standard\) @ (\S+):\s*(0x[0-9a-fA-F]+)$/);
    if (m) {
      id.available = true;
      id.cpuidRaw = m[2];
      continue;
    }
    m = line.match(/^->\s*((?:Cortex-M|ARMv\S*).*)$/);
    if (m && id.cpuidRaw && !id.cpuidDesc) {
      id.cpuidDesc = m[1];
      continue;
    }
    m = line.match(/^dev_id \(vendor register\) @ (\S+):\s*(0x[0-9a-fA-F]+)$/);
    if (m) {
      id.available = true;
      id.devIdRaw = m[2];
      continue;
    }
    m = line.match(/^->\s*dev_id\s+(0x[0-9a-fA-F]+),\s*rev_id\s+(0x[0-9a-fA-F]+)$/);
    if (m) {
      id.devId = m[1];
      id.revId = m[2];
      continue;
    }
    m = line.match(/^->\s*family:\s*(.+)$/);
    if (m) {
      id.family = m[1].trim();
      continue;
    }
    m = line.match(/^model \(from target_cfg (.+?)\):\s*(.+)$/);
    if (m) {
      id.model = m[2].trim();
      id.modelCfg = m[1].trim();
      continue;
    }
    if (line.startsWith("target mismatch:")) {
      id.mismatch = line;
      continue;
    }
    if (/\(no parsable response\)|\(read failed\)/.test(line)) {
      id.readFailures.push(line);
    }
  }
  return id;
}

export interface FlashBank {
  index: number;
  name: string; // at32f435zmt7.flash
  driver: string; // at32f435xx
  base: number; // 0x08000000
  size: number; // bytes；0 = 未探测到
}

/**
 * flash_info 的 `flash banks:` 行（openocd 原文）：
 *   #0 : at32f435zmt7.flash (at32f435xx) at 0x08000000, size 0x00100000, buswidth 2, chipwidth 4
 */
export function parseFlashBanks(text: string): FlashBank[] {
  const banks: FlashBank[] = [];
  for (const rawLine of normalizeNewlines(text).split("\n")) {
    const m = rawLine
      .trim()
      .match(/^#(\d+)\s*:\s*(\S+)\s+\((\S+)\)\s+at\s+(0x[0-9a-fA-F]+),\s*size\s+(0x[0-9a-fA-F]+)/);
    if (!m) continue;
    banks.push({
      index: Number(m[1]),
      name: m[2],
      driver: m[3],
      base: parseInt(m[4], 16),
      size: parseInt(m[5], 16),
    });
  }
  return banks;
}

export type FlashVerdict = "verified" | "verify_failed" | "write_failed" | "inconclusive" | "unverified" | "erased" | "other";

/** `flash info <bank>` 报告的扇区几何（AT32 驱动实测形如：
 *  `main flash size: 0x100000, sector num: 0x100, sector size: 0x800, bank size: 0x80000`） */
export interface SectorGeometry {
  sectorsPerBank: number; // driver 报的 sector num（AT32F403A 实测 = 单 bank 扇区数，与 bank size/sector size 一致）
  sectorSize: number; // 单扇区字节数
  bankSize: number; // 单 bank 字节数
  mainSize: number; // 整片字节数
}

export function parseSectorGeometry(text: string): SectorGeometry | null {
  const m = normalizeNewlines(text).match(
    /main flash size:\s*(0x[0-9a-fA-F]+),\s*sector num:\s*(0x[0-9a-fA-F]+),\s*sector size:\s*(0x[0-9a-fA-F]+),\s*bank size:\s*(0x[0-9a-fA-F]+)/,
  );
  if (!m) return null;
  return {
    mainSize: parseInt(m[1], 16),
    sectorsPerBank: parseInt(m[2], 16),
    sectorSize: parseInt(m[3], 16),
    bankSize: parseInt(m[4], 16),
  };
}

/** 某 bank 的实际扇区数 = bank size / sector size（driver 的 sector num 是整片口径） */
export function sectorsInBank(g: SectorGeometry, bankSize: number): number {
  if (!g.sectorSize) return 0;
  return Math.max(0, Math.round(bankSize / g.sectorSize));
}

export interface FlashOpResult {
  verdict: FlashVerdict;
  bytes?: number; // programmed/verified N bytes
  loadAddress?: string; // 0x08000000
  path?: string;
  erased?: { bank: number; first: number; lastText: string };
}

/** flash_write / flash_erase_sector 结论行（FlashTools 四态 + 擦除回执） */
export function parseFlashOpResult(text: string): FlashOpResult {
  const r: FlashOpResult = { verdict: "other" };
  text = normalizeNewlines(text);
  const bytes = text.match(/(?:programmed|verified):\s*\S+\s*\((\d+) bytes\)/) ?? text.match(/verified:\s*\S+\s*\((\d+) bytes\)/);
  if (bytes) r.bytes = Number(bytes[1]);
  const addr = text.match(/load address:\s*(0x[0-9a-fA-F]+)/);
  if (addr) r.loadAddress = addr[1];
  const path = text.match(/^(?:programmed|verified):\s*(.+?)\s*\((\d+) bytes\)/m);
  if (path) r.path = path[1];
  if (/VERIFY INCONCLUSIVE/.test(text)) r.verdict = "inconclusive";
  else if (/VERIFY FAILED/.test(text)) r.verdict = "verify_failed";
  else if (/WRITE FAILED/.test(text)) r.verdict = "write_failed";
  else if (/^unverified:/m.test(text)) r.verdict = "unverified";
  else if (/^verified:/m.test(text)) r.verdict = "verified";
  else if (/erase issued:\s*bank (\d+), sectors (\d+)\.\.(\S+?)\.?\s*$/.test(text)) {
    const m = text.match(/erase issued:\s*bank (\d+), sectors (\d+)\.\.(\S+?)\.?\s*$/) !;
    r.verdict = "erased";
    r.erased = { bank: Number(m[1]), first: Number(m[2]), lastText: m[3] };
  } else if (/^programmed:/m.test(text)) r.verdict = "unverified";
  return r;
}

export interface CoreRegs {
  pc?: string;
  lr?: string;
  sp?: string;
  xpsr?: string;
}

/** halt 的 `core registers: pc=0x…  lr=0x…` 行 */
export function parseCoreRegs(text: string): CoreRegs {
  const regs: CoreRegs = {};
  const m = normalizeNewlines(text).match(/core registers:\s*(.+)/);
  if (!m) return regs;
  for (const pair of m[1].trim().split(/\s{2,}|\s+/)) {
    const kv = pair.match(/^(pc|lr|sp|xPSR)=(0x[0-9a-fA-F]+)$/i);
    if (kv) {
      const key = kv[1].toLowerCase() === "xpsr" ? "xpsr" : (kv[1].toLowerCase() as keyof CoreRegs);
      regs[key] = kv[2];
    }
  }
  return regs;
}

export interface SvdPeripheral {
  name: string;
  baseAddress: string; // 0x…
  registerCount: number;
}

/** load_svd 无 peripheral 参数时的清单行：`  RCC             0x40021000  (32 registers)` */
export function parseSvdList(text: string): { header: string; peripherals: SvdPeripheral[] } {
  const peripherals: SvdPeripheral[] = [];
  const lines = normalizeNewlines(text).split("\n").map((l) => l.trim());
  const header = lines.find((l) => l.startsWith("loaded ")) ?? "";
  for (const line of lines) {
    const m = line.match(/^(\S+)\s+(0x[0-9a-fA-F]+)\s+\((\d+) registers?\)/);
    if (m) peripherals.push({ name: m[1], baseAddress: m[2], registerCount: Number(m[3]) });
  }
  return { header, peripherals };
}

export interface SvdRegisterRow {
  name: string;
  address: string;
  sizeBits: number;
  access: string;
  fieldCount: number; // -1 = 无位域信息（括号缺失）
}

/** load_svd(file, peripheral) 的寄存器行：`  CR              0x40021000  32-bit  read-write  (3 fields)` */
export function parseSvdRegisterList(text: string): { header: string; registers: SvdRegisterRow[] } {
  const registers: SvdRegisterRow[] = [];
  const lines = normalizeNewlines(text).split("\n").map((l) => l.trim());
  const header = lines.find((l) => /@\s*0x\S+\s*-\s*\d+ register/.test(l)) ?? "";
  for (const line of lines) {
    const m = line.match(/^(\S+)\s+(0x[0-9a-fA-F]+)\s+(\d+)-bit\s+(\S+)(?:\s+\((\d+) fields?\))?$/);
    if (m)
      registers.push({
        name: m[1],
        address: m[2],
        sizeBits: Number(m[3]),
        access: m[4],
        fieldCount: m[5] !== undefined ? Number(m[5]) : -1,
      });
  }
  return { header, registers };
}

export interface DecodedField {
  name: string;
  bits: string; // [3:0]
  value: string; // 0x5
}

export interface DecodedRegister {
  fullName: string; // RCC.CR
  address: string;
  sizeBits: number;
  access: string;
  rawValue: string;
  fields: DecodedField[];
  resetNote?: string;
}

/**
 * read_peripheral_register 的解码报告（SvdFile.DescribeRegister）：
 *   CR @ 0x40021000 (32-bit, read-write): 0x000000b1
 *     HSEON[16] = 0x1
 *     ...
 *   differs from the reset value (0x00000083)
 */
export function parseDecodedRegister(name: string, text: string): DecodedRegister | null {
  const first = normalizeNewlines(text).split("\n")[0]?.trim() ?? "";
  const m = first.match(/^(?:(\S+)\.)?(\S+) @ (0x[0-9a-fA-F]+) \((\d+)-bit, ([\w-]+)\):\s*(0x[0-9a-fA-F]+)$/);
  if (!m) return null;
  const reg: DecodedRegister = {
    fullName: m[1] ? `${m[1]}.${m[2]}` : name,
    address: m[3],
    sizeBits: Number(m[4]),
    access: m[5],
    rawValue: m[6],
    fields: [],
  };
  for (const rawLine of normalizeNewlines(text).split("\n").slice(1)) {
    const line = rawLine.trim();
    const f = line.match(/^(\S+)\[(\d+):(\d+)\]\s*=\s*(0x[0-9a-fA-F]+)$/);
    if (f) {
      reg.fields.push({ name: f[1], bits: `[${f[2]}:${f[3]}]`, value: f[4] });
      continue;
    }
    if (/matches the reset value|differs from the reset value/.test(line)) reg.resetNote = line;
  }
  return reg;
}

export interface BackendStatus {
  running: boolean;
  blocked?: boolean; // read_only BLOCKED 提示
  hint?: string; // not running 时的提示原文
  version?: string;
  address?: string; // host:port
  channel?: string; // channel: connected, idle 3s ...
  targets: TargetRow[];
  raw: string;
}

/** status 工具的三态文本 → 结构化（running / not running / BLOCKED） */
export function parseStatus(text: string): BackendStatus {
  const st: BackendStatus = { running: false, targets: [], raw: normalizeNewlines(text) };
  if (/^BLOCKED:/m.test(st.raw)) {
    st.blocked = true;
    return st;
  }
  const conn = st.raw.match(/OpenOCD: running on (\S+)/);
  if (conn) {
    st.running = true;
    st.address = conn[1];
  } else {
    const notRunning = st.raw.match(/OpenOCD: not running[^\n]*/);
    st.hint = notRunning ? notRunning[0] : st.raw.split("\n")[0] || "";
    return st;
  }
  const version = st.raw.match(/version:\s*(.+)/);
  if (version) st.version = version[1].trim();
  const channel = st.raw.match(/channel: [^\n]+/);
  if (channel) st.channel = channel[0];
  const targetsBlock = st.raw.match(/targets:\n([\s\S]*)/);
  if (targetsBlock) st.targets = parseTargets(targetsBlock[1]);
  return st;
}

/** 工具结果里的 BLOCKED/ERROR 首行（面板红条展示用） */
export function firstErrorLine(text: string): string | null {
  for (const line of normalizeNewlines(text).split("\n")) {
    const t = line.trim();
    if (t.startsWith("BLOCKED:") || t.startsWith("ERROR:")) {
      return t.length > 220 ? t.slice(0, 220) + "…" : t;
    }
  }
  return null;
}
