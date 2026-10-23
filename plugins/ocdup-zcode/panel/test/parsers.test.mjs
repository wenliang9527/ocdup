/**
 * 解析器离线自测：用 ocdup 真实输出形态（来自 C# 测试夹具与 e2e_fake_backend）驱动
 * parsers.ts，node --test 直跑。面板显示原文兜底，但解析错会导致错误的结构化展示，
 * 所以这里把每个已知形态钉死。
 */
import test from "node:test";
import assert from "node:assert/strict";
import {
  parseTargets,
  parseChipIdentity,
  parseFlashBanks,
  parseFlashOpResult,
  parseCoreRegs,
  parseSvdList,
  parseSvdRegisterList,
  parseDecodedRegister,
  parseStatus,
  firstErrorLine,
} from "../src/parsers.ts";

const TARGETS_SUMMARY = "0* at32f435zmt7 (cortex_m, little) running";
const TARGETS_RAW = [
  "    TargetName         Type       Endian TapName            State",
  "--  ------------------ ---------- ------ ------------------ ------------",
  " 0* at32f435zmt7       cortex_m   little at32f435zmt7.cpu   running",
].join("\n");

test("parseTargets: 摘要行与原始表格行都识别", () => {
  const s = parseTargets(TARGETS_SUMMARY);
  assert.equal(s.length, 1);
  assert.equal(s[0].state, "running");
  assert.equal(s[0].name, "at32f435zmt7");

  const raw = parseTargets(TARGETS_RAW);
  assert.equal(raw.length, 1);
  assert.equal(raw[0].state, "running");
  assert.equal(raw[0].type, "cortex_m");
});

test("parseStatus: running 形态", () => {
  const st = parseStatus(
    ["OpenOCD: running on 127.0.0.1:6666", "version: 0.12.0+dev", "channel: connected, idle 3s (heartbeat probes while idle)", "targets:", TARGETS_SUMMARY].join("\n"),
  );
  assert.equal(st.running, true);
  assert.equal(st.address, "127.0.0.1:6666");
  assert.equal(st.version, "0.12.0+dev");
  assert.equal(st.targets[0].state, "running");
  assert.match(st.channel ?? "", /connected/);
});

test("parseStatus: not running 与 BLOCKED 形态", () => {
  const down = parseStatus("OpenOCD: not running - nothing is listening on 127.0.0.1:6666. Call connect to start the backend from interface_cfg/target_cfg.");
  assert.equal(down.running, false);
  assert.match(down.hint ?? "", /not running/);

  const blocked = parseStatus("BLOCKED: 'disconnect' stops the OpenOCD backend, which read_only forbids.");
  assert.equal(blocked.blocked, true);
});

test("CRLF 输出：Windows AppendLine 的 \\r\\n 不影响任何解析器", () => {
  const crlf = (s) => s.replace(/\n/g, "\r\n");
  const st = parseStatus(
    crlf(
      ["OpenOCD: running on 127.0.0.1:6666", "version: 0.12.0+dev", "channel: connected, idle 3s", "targets:", TARGETS_SUMMARY].join("\n"),
    ),
  );
  assert.equal(st.running, true);
  assert.equal(st.targets.length, 1);
  assert.equal(st.targets[0].state, "running");
  assert.doesNotMatch(st.channel ?? "", /\r/);

  assert.equal(parseFlashBanks(crlf("#0 : at32f403axx.bank1 (at32f403axx) at 0x08000000, size 0x00080000")).length, 1);
  assert.equal(parseSvdList(crlf("loaded x.svd: 1 peripheral\n  RCC  0x40021000  (2 registers)")).peripherals.length, 1);
  assert.equal(parseCoreRegs(crlf("core registers: pc=0x08001234  lr=0x0800abcd")).pc, "0x08001234");
  assert.equal(firstErrorLine(crlf("line\nERROR: boom\r\nnext")), "ERROR: boom");
});


test("parseChipIdentity: 完整识别报告（connect 的 chip identity 块）", () => {
  const text = [
    "chip identity:",
    "  cpu (CPUID, ARM standard) @ 0xE000ED00: 0x412fc231",
    "      -> Cortex-M4 (ARMv7-M/v8-M, implementer 'A', r0p1)",
    "  dev_id (vendor register) @ 0xE0042000: 0x10016415",
    "      -> dev_id 0x415, rev_id 0x1001",
    "      -> family: AT32F403A",
    "  model (from target_cfg target_at32f403axx.cfg): AT32F403A",
  ].join("\n");
  const id = parseChipIdentity(text);
  assert.equal(id.available, true);
  assert.equal(id.cpuidRaw, "0x412fc231");
  assert.match(id.cpuidDesc ?? "", /Cortex-M4/);
  assert.equal(id.devId, "0x415");
  assert.equal(id.revId, "0x1001");
  assert.equal(id.family, "AT32F403A");
  assert.equal(id.model, "AT32F403A");
});

test("parseChipIdentity: 读失败形态", () => {
  const text = [
    "chip identity:",
    "  cpu (CPUID, ARM standard) @ 0xE000ED00: (no parsable response) raw: target not halted | JTAG-DP STICKY ERROR",
    "  dev_id (vendor register) @ 0xE0042000: (read failed) connection is closed",
  ].join("\n");
  const id = parseChipIdentity(text);
  assert.equal(id.available, false); // 两个读都没拿到数：无芯片信息可显示
  assert.equal(id.cpuidRaw, undefined);
  assert.equal(id.readFailures.length, 2);
});

test("parseFlashBanks: e2e fake 的 bank 行", () => {
  const banks = parseFlashBanks(
    "flash banks:\n#0 : at32f435zmt7.flash (at32f435xx) at 0x08000000, size 0x00100000, buswidth 2, chipwidth 4",
  );
  assert.equal(banks.length, 1);
  assert.equal(banks[0].base, 0x08000000);
  assert.equal(banks[0].size, 0x100000);
  assert.equal(banks[0].driver, "at32f435xx");
});

test("parseFlashOpResult: 烧录 verified 与 INCONCLUSIVE", () => {
  const ok = parseFlashOpResult(
    ["programmed: D:/fw/build/fw.bin (524288 bytes)", "load address: 0x08000000", "verified: D:/fw/build/fw.bin (524288 bytes) matches the target flash (read-back).", "reset run: done"].join("\n"),
  );
  assert.equal(ok.verdict, "verified");
  assert.equal(ok.bytes, 524288);
  assert.equal(ok.loadAddress, "0x08000000");
  assert.equal(ok.path, "D:/fw/build/fw.bin");

  const inc = parseFlashOpResult("VERIFY INCONCLUSIVE: the read-back did not run to completion...\nreason: the backend could not run its CRC algorithm");
  assert.equal(inc.verdict, "inconclusive");

  const fail = parseFlashOpResult("WRITE FAILED: the program step reported an error...");
  assert.equal(fail.verdict, "write_failed");

  const erased = parseFlashOpResult("erase issued: bank 0, sectors 0..last.");
  assert.equal(erased.verdict, "erased");
  assert.deepEqual(erased.erased, { bank: 0, first: 0, lastText: "last" });
});

test("parseCoreRegs: halt 的现场行", () => {
  const regs = parseCoreRegs("halt issued (halt 500).\ncore registers: pc=0x08001234  lr=0x0800abcd  sp=0x20004000  xPSR=0x61000000");
  assert.equal(regs.pc, "0x08001234");
  assert.equal(regs.lr, "0x0800abcd");
  assert.equal(regs.sp, "0x20004000");
  assert.equal(regs.xpsr, "0x61000000");
});

test("parseSvdList: load_svd 清单", () => {
  const { header, peripherals } = parseSvdList(
    ["loaded D:/SVD/AT32F403A.svd: 3 peripheral(s), 60 register(s) total.", "  RCC             0x40021000  (32 registers)", "  GPIOA           0x40010800  (18 registers)", "  ... 1 more"].join("\n"),
  );
  assert.match(header, /loaded/);
  assert.equal(peripherals.length, 2);
  assert.equal(peripherals[0].name, "RCC");
  assert.equal(peripherals[1].baseAddress, "0x40010800");
});

test("parseSvdRegisterList: load_svd(peripheral) 寄存器行", () => {
  const { registers } = parseSvdRegisterList(
    ["RCC @ 0x40021000 - 2 register(s):", "  CR              0x40021000  32-bit  read-write  (3 fields)", "  CFGR            0x40021004  32-bit  read-write"].join("\n"),
  );
  assert.equal(registers.length, 2);
  assert.equal(registers[0].fieldCount, 3);
  assert.equal(registers[1].fieldCount, -1);
});

test("parseDecodedRegister: 解码报告", () => {
  const reg = parseDecodedRegister(
    "RCC.CR",
    ["CR @ 0x40021000 (32-bit, read-write): 0x000000b1", "  HSEON[16:16] = 0x1", "  HSITRIM[6:3] = 0x0", "  differs from the reset value (0x00000083)"].join("\n"),
  );
  assert.ok(reg);
  assert.equal(reg.fullName, "RCC.CR");
  assert.equal(reg.rawValue, "0x000000b1");
  assert.equal(reg.fields.length, 2);
  assert.equal(reg.fields[0].bits, "[16:16]");
  assert.equal(reg.resetNote, "differs from the reset value (0x00000083)");
});

test("firstErrorLine: BLOCKED 与 ERROR", () => {
  assert.match(firstErrorLine("BLOCKED: 'flash_erase_sector' is destructive...") ?? "", /^BLOCKED:/);
  assert.match(firstErrorLine("ERROR: no SVD file is loaded...") ?? "", /^ERROR:/);
  assert.equal(firstErrorLine("flash banks:\n#0 : x"), null);
});
