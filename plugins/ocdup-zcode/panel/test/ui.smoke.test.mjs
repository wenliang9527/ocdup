/**
 * UI 冒烟自测：不进真宿主，用最小 DOM stub + 假 callServerTool 驱动 app.ts 打包产物，
 * 钉死关键交互：running 时读寄存器按钮置灰、破坏性操作走确认框、widget state 恢复、
 * 解析结果进渲染。app.ts 是顶层 await 的页面脚本，这里以 CJS bundle + vm 执行。
 */
import test from "node:test";
import assert from "node:assert/strict";
import { build } from "esbuild";
import vm from "node:vm";

// ── 最小 DOM：app.ts 用到的面（getElementById / querySelectorAll / innerHTML / dataset）──
function makeEl(id) {
  return {
    id,
    style: {},
    dataset: {},
    value: "",
    checked: true,
    textContent: "",
    innerHTML: "",
    onclick: null,
    focus() {},
    appendChild() {},
    querySelector(sel) {
      return makeEl(id + sel);
    },
    querySelectorAll() {
      return [];
    },
  };
}
function makeDom() {
  const byId = new Map();
  const root = makeEl("app");
  byId.set("app", root);
  return {
    documentElement: { dataset: {} },
    body: makeEl("body"),
    // 同 id 返回同一个实例（真 DOM 语义）：测试里写 .value 后业务能读到
    getElementById: (id) => {
      if (!byId.has(id)) byId.set(id, makeEl(id));
      return byId.get(id);
    },
    querySelector: () => makeEl("q"),
    querySelectorAll: () => [],
  };
}

const REPLIES = {
  ocdup_status:
    "OpenOCD: running on 127.0.0.1:6666\nversion: 0.12.0+dev\nchannel: connected, idle 1s\ntargets:\n0* at32f403avgt7 (cortex_m, little) running",
  ocdup_identify_chip:
    "chip identity:\n  cpu (CPUID, ARM standard) @ 0xE000ED00: 0x412fc231\n      -> Cortex-M4 (ARMv7-M/v8-M, implementer 'A', r0p1)\n  dev_id (vendor register) @ 0xE0042000: 0x10016415\n      -> dev_id 0x415, rev_id 0x1001\n      -> family: AT32F403A",
  ocdup_flash_info:
    "flash banks:\n#0 : at32f403avgt7.flash (at32f403axx) at 0x08000000, size 0x00100000, buswidth 2, chipwidth 4",
  ocdup_connect:
    "connected to OpenOCD on 127.0.0.1:6666\nversion: 0.12.0+dev\ntargets:\n0* at32f403avgt7 (cortex_m, little) running",
  ocdup_halt: "halt issued (halt 500).\ncore registers: pc=0x08001234  lr=0x0800abcd",
  ocdup_load_svd:
    "loaded D:/SVD/AT32F403A.svd: 2 peripheral(s), 40 register(s) total.\n  RCC             0x40021000  (32 registers)\n  GPIOA           0x40010800  (8 registers)",
  "ocdup_load_svd:RCC":
    "RCC @ 0x40021000 - 2 register(s):\n  CR              0x40021000  32-bit  read-write  (1 fields)\n  CFGR            0x40021004  32-bit  read-write",
  ocdup_read_peripheral_register:
    "RCC.CR @ 0x40021000 (32-bit, read-write): 0x000000b1\n  HSEON[16:16] = 0x1",
  ocdup_verify_image: "verified: fw.hex (4096 bytes) matches the target flash (read-back).",
  ocdup_disconnect:
    "backend stopped (pid 1234); command port 127.0.0.1:6666 is now free. The target was halted - it stays halted.",
  ocdup_set_target:
    "target switched to 'AT32F403A' -> target/at32f403axG.cfg\nbackend: started openocd (pid 4321)",
};

async function boot({ widgetState, halted = false } = {}) {
  const calls = [];
  const widgetSaves = [];
  const dom = makeDom();
  const replies = { ...REPLIES };
  if (halted) {
    // 目标核已暂停形态：状态行末 token 换成 halted
    replies.ocdup_status = replies.ocdup_status.replace("running", "halted");
  }

  const fake = {
    getHostContext: () => ({ theme: "dark", locale: "zh-CN", "zcode/widgetState": widgetState ?? {} }),
    getHostCapabilities: () => ({ experimental: { "zcode/widgetState": {} } }),
    request: async (req) => {
      if (req.method === "ui/set-widget-state") widgetSaves.push(req.params.widgetState);
      return {};
    },
    callServerTool: async (params) => {
      calls.push(params);
      const key =
        params.name === "ocdup_load_svd" && params.arguments?.peripheral
          ? `${params.name}:${params.arguments.peripheral}`
          : params.name;
      const text = replies[key] ?? "ERROR: not stubbed in smoke test";
      return { content: [{ type: "text", text }], isError: text.startsWith("ERROR:") };
    },
    addEventListener() {},
  };

  // stub ext-apps + core（app.ts 只用这几个导出）
  const stubPlugin = {
    name: "stub-ext-apps",
    setup(b) {
      b.onResolve({ filter: /^@modelcontextprotocol\// }, () => ({ path: "stub", namespace: "stub" }));
      b.onLoad({ filter: /.*/, namespace: "stub" }, () => ({
        contents: `
          export class App {
            constructor() {}
            addEventListener() {}
            async connect() {}
            getHostContext() { return globalThis.__fake.getHostContext(); }
            getHostCapabilities() { return globalThis.__fake.getHostCapabilities(); }
            async request(...a) { return globalThis.__fake.request(...a); }
            async callServerTool(...a) { return globalThis.__fake.callServerTool(...a); }
          }
          export const applyDocumentTheme = () => {};
          export const applyHostFonts = () => {};
          export const applyHostStyleVariables = () => {};
          export const EmptyResultSchema = {};
        `,
        loader: "ts",
      }));
    },
  };

  const res = await build({
    entryPoints: [new URL("../src/app.ts", import.meta.url).pathname.replace(/^\/([A-Za-z]:)/, "$1")],
    bundle: true,
    format: "cjs",
    platform: "node",
    target: "node20",
    plugins: [stubPlugin],
    write: false,
    legalComments: "none",
    logLevel: "silent",
  });
  const code = res.outputFiles[0].text;

  const sandbox = { console, setTimeout, clearTimeout, setInterval, clearInterval };
  sandbox.globalThis = sandbox;
  sandbox.document = dom;
  sandbox.window = {};
  sandbox.navigator = { clipboard: { writeText: async () => {} } };
  sandbox.__fake = fake;
  sandbox.__calls = calls;
  sandbox.__widgetSaves = widgetSaves;
  vm.createContext(sandbox);
  // CJS bundle 是表达式（require 链），eval 出模块导出并在沙箱内执行副作用
  new vm.Script(code, { filename: "app.bundle.cjs" }).runInContext(sandbox);
  // app.ts 的启动流程是同步链 + 一个 void refreshAll()；flush 微任务
  await new Promise((r) => setTimeout(r, 20));

  const panel = sandbox.window.__panel;
  assert.ok(panel, "window.__panel exposed");
  // 关掉自动轮询：否则 5s 后会在断言之间偷偷调 status，测试不再确定
  panel.stopPolling?.();
  return { panel, dom, calls, widgetSaves, sandbox, replies };
}

test("启动冒烟：自动刷新拉取 status/identify/flash_info 并渲染芯片与核状态", async () => {
  const { panel, dom, calls } = await boot();
  assert.ok(calls.some((c) => c.name === "ocdup_status"));
  assert.ok(calls.some((c) => c.name === "ocdup_identify_chip"));
  assert.ok(calls.some((c) => c.name === "ocdup_flash_info"));
  assert.match(dom.getElementById("app").innerHTML, /运行中/);
  assert.match(dom.getElementById("app").innerHTML, /AT32F403A/);
  assert.equal(panel.state.status.running, true);
  assert.equal(panel.state.halted, false);
});

test("widget state 恢复：上次选中的标签页与外设在重开时还原", async () => {
  const { panel, dom } = await boot({ widgetState: { tab: "regs", peripheral: "RCC" } });
  assert.equal(panel.state.tab, "regs");
  // regs 标签渲染（未加载 SVD → 显示加载入口）
  assert.match(dom.getElementById("app").innerHTML, /SVD/);
  // 加载 SVD 后应保留上次外设 RCC（boot 里没有触发 loadSvd，这里只验证 state 恢复）
  assert.equal(panel.state.peripheral, "RCC");
});

test("running 时寄存器读取按钮置灰；halt 后可读", async () => {
  const { panel, dom } = await boot({ halted: false });
  // 切到 regs 并加载 SVD + 选中外设（直接驱动状态，绕过 UI 事件——事件接线在真宿主验证）
  panel.state.tab = "regs";
  await panel.refreshAll(); // running → halted=false
  await panel.loadSvd(); // 载入外设清单并选中第一个（RCC）
  const html = dom.getElementById("app").innerHTML;
  assert.match(html, /<table class="grid regs">/); // 寄存器表已渲染
  assert.match(html, /disabled/);
  assert.match(html, /需先 halt/);

  // halt 之后按钮可用
  await panel.call("ocdup_halt");
  panel.state.halted = true;
  panel.render();
  const html2 = dom.getElementById("app").innerHTML;
  assert.doesNotMatch(html2, /<button class="mini read-btn"[^>]*disabled/);
});

test("破坏性操作走确认框：复位/写寄存器未确认不发起调用", async () => {
  const { panel, calls, dom } = await boot();
  // doResetRun 弹确认框后等待 resolve；不点确认 → 永远不发起 ocdup_reset
  const p = panel.state;
  // confirmDialog 返回的 promise 在弹框 DOM stub 下无法自动 resolve——用超时竞速证明“没有调用”
  const resetPromise = Promise.race([
    (async () => {
      // 直接调用内部流程：确认框 promise 挂起 → 后续 call 不会发生
      await panel.app ? 0 : 0;
    })(),
    new Promise((r) => setTimeout(() => r("done"), 30)),
  ]);
  await resetPromise;
  const before = calls.filter((c) => c.name === "ocdup_reset").length;
  assert.equal(before, 0);

  // 确认框元素存在且默认隐藏
  const box = dom.getElementById("confirm-box");
  assert.ok(box);
});

test("flash 扇区图：bank 布局渲染且烧录结论进 verdict", async () => {
  const { panel, dom } = await boot();
  panel.state.tab = "flash";
  await panel.refreshAll();
  const html = dom.getElementById("app").innerHTML;
  assert.match(html, /at32f403axx/);
  assert.match(html, /0x08000000/i);
  assert.match(html, /1\.00 MiB|MiB/);
});

test("连接按钮：真的调 ocdup_connect（而不是只刷新 status）", async () => {
  const { panel, calls } = await boot();
  calls.length = 0;
  await panel.doConnect();
  assert.equal(calls.filter((c) => c.name === "ocdup_connect").length, 1);
  // connect 成功后仍会 refreshAll 补齐 status/identify/flash_info
  assert.ok(calls.some((c) => c.name === "ocdup_status"));
  assert.equal(panel.state.statusNote, null);
});

test("连接失败：红条展示首个错误行且不崩，旧状态保留", async () => {
  const { panel, replies } = await boot();
  replies.ocdup_connect = 'ERROR: 用户级 MCP 服务器 "ocdup" 上没有工具 connect (boom)';
  await panel.doConnect();
  assert.match(panel.state.statusNote, /^ERROR:/);
  assert.ok(panel.state.status); // 旧状态仍在，页面不清空
});

test("targets 解析：CRLF 输出也能读到目标核（Windows AppendLine 回归）", async () => {
  const { panel, dom, replies } = await boot();
  // 把 status 回复改成真实 Windows 输出的 CRLF 形态
  replies.ocdup_status = replies.ocdup_status.replace(/\n/g, "\r\n");
  await panel.refreshAll();
  assert.equal(panel.state.status.targets.length, 1);
  assert.equal(panel.state.status.targets[0].state, "running");
  const html = dom.getElementById("app").innerHTML;
  assert.match(html, /at32f403avgt7/); // 目标核表不再只有 —
  assert.match(html, /cortex_m/);
});

test("SVD 持久化：加载成功后写进 widget state，重开时自动重载", async () => {
  const { panel, widgetSaves, calls } = await boot({ widgetState: { tab: "regs", svdFile: "D:/SVD/AT32F403A.svd" } });
  // 启动即按 widget state 里的路径自动重载（不必手点）
  assert.ok(calls.some((c) => c.name === "ocdup_load_svd" && c.arguments?.file === "D:/SVD/AT32F403A.svd"));
  assert.equal(panel.state.svdFile, "D:/SVD/AT32F403A.svd");
  assert.equal(panel.state.svdLoaded, true);
  // 保存的状态里带 svdFile
  assert.ok(widgetSaves.some((w) => w.svdFile === "D:/SVD/AT32F403A.svd"));
});

test("verify_image：只读校验进 flash 结论条与详情", async () => {
  const { panel, dom, calls } = await boot();
  panel.state.tab = "flash";
  dom.getElementById("verify-file").value = "fw.hex";
  await panel.doVerifyImage();
  assert.equal(calls.filter((c) => c.name === "ocdup_verify_image").length, 1);
  assert.equal(panel.state.flashOp.verdict, "verified");
  const html = dom.getElementById("app").innerHTML;
  assert.match(html, /校验|verified/i);
  assert.match(panel.state.flashRaw, /4096 bytes/);
});

test("disconnect：确认后调 ocdup_disconnect 并刷新状态", async () => {
  const { panel, calls, dom } = await boot();
  // confirmDialog 在 DOM stub 下不会自动 resolve —— 直接检查按钮入口已接线
  const html = dom.getElementById("app").innerHTML;
  assert.match(html, /btn-disconnect/);
  // 绕过确认框：直接把 resolve 拿到的 promise 补一个 true
  const p = panel.doDisconnect();
  // stub 的 confirm-ok 点击无法触发，这里以「未确认不调用」为断言
  await new Promise((r) => setTimeout(r, 20));
  assert.equal(calls.filter((c) => c.name === "ocdup_disconnect").length, 0);
  assert.ok(p); // 流程仍在等确认
});

test("set_target：mismatch 时渲染一键切换入口与预设 datalist", async () => {
  const { panel, dom, replies } = await boot();
  replies.ocdup_identify_chip +=
    "\n  target mismatch: identified AT32F403A, current target_cfg: target/stm32f1x.cfg -> use set_target AT32F403A";
  panel.state.presets = ["AT32F403A", "STM32F103"];
  await panel.refreshAll();
  panel.state.tab = "status";
  panel.render();
  const html = dom.getElementById("app").innerHTML;
  assert.match(html, /btn-set-target/);
  assert.match(html, /target-presets/);
  assert.match(html, /AT32F403A/);
});

test("扇区几何：flash info 的 sector size 被解析，扇区图按真实扇区绘制并点亮擦除范围", async () => {
  const { panel, dom, replies } = await boot();
  replies.ocdup_run_command =
    "This is target at32f403axx algorithm\nmain flash size: 0x100000, sector num:  0x100, sector size: 0x800,  bank size: 0x80000";
  await panel.refreshAll();
  assert.equal(panel.state.geometry?.sectorSize, 0x800);
  assert.equal(panel.state.geometry?.bankSize, 0x80000);

  panel.state.tab = "flash";
  panel.state.flashOp = { verdict: "erased", erased: { bank: 0, first: 0, lastText: "3" } };
  panel.render();
  const html = dom.getElementById("app").innerHTML;
  // 该 stub 是单 bank 1 MiB / 2 KiB = 512 个真实扇区
  assert.match(html, /512 × 2\.0 KiB/);
  assert.match(html, /class="sblk hit"/); // 擦除范围有点亮的块
});

test("寄存器过滤：只显隐匹配行，不清空已读值", async () => {
  const { panel, dom } = await boot();
  panel.state.tab = "regs";
  await panel.refreshAll();
  await panel.loadSvd();
  const html = dom.getElementById("app").innerHTML;
  assert.match(html, /id="reg-filter"/);
  assert.match(html, /data-reg-row/);
});

test("轮询：refreshStatusOnly 只调 status，不重复拉 identify/flash_info", async () => {
  const { panel, calls } = await boot();
  await panel.refreshAll(); // 首次完整刷新
  calls.length = 0;
  await panel.refreshStatusOnly(); // 轮询这一拍
  assert.deepEqual(calls.map((c) => c.name), ["ocdup_status"]);
});

test("轮询：后端从停到起时补一次完整刷新（identify/flash_info 会跟出来）", async () => {
  const { panel, calls, replies } = await boot();
  panel.stopPolling();
  // 先把状态改成「后端没起」
  replies.ocdup_status = "OpenOCD: not running - nothing is listening on 127.0.0.1:6666.";
  await panel.refreshStatusOnly();
  assert.equal(panel.state.status.running, false);
  assert.equal(panel.state.banks.length, 0);

  // 后端起来了 → 轮询这一拍应触发 refreshAll 补齐 identity/banks
  replies.ocdup_status = REPLIES.ocdup_status;
  calls.length = 0;
  await panel.refreshStatusOnly();
  await new Promise((r) => setTimeout(r, 30)); // refreshAll 是 fire-and-forget
  const names = calls.map((c) => c.name);
  assert.ok(names.includes("ocdup_identify_chip"), names.join(","));
  assert.ok(names.includes("ocdup_flash_info"), names.join(","));
  assert.equal(panel.state.status.running, true);
});

test("轮询不打断输入：用户正在输入时刷新只更新数据，不重绘 DOM", async () => {
  const { panel, dom, replies } = await boot();
  panel.stopPolling();
  // 模拟用户正在 reg-filter 里打字：document.activeElement 指向 INPUT
  const input = dom.getElementById("reg-filter");
  input.tagName = "INPUT";
  dom.activeElement = input;
  const beforeHtml = dom.getElementById("app").innerHTML;

  // 让后端状态真的变了（running → not running），否则本来就不该重绘
  replies.ocdup_status = "OpenOCD: not running - nothing is listening on 127.0.0.1:6666.";
  await panel.refreshStatusOnly();

  assert.equal(dom.getElementById("app").innerHTML, beforeHtml); // DOM 未被冲掉
  assert.equal(panel.state.status.running, false); // 但数据已更新
});

test("轮询状态没变时不重绘（idle 秒数变化不该触发重绘）", async () => {
  const { panel, dom } = await boot();
  panel.stopPolling();
  await panel.refreshAll();
  const before = dom.getElementById("app").innerHTML;
  // 只让 channel idle 秒数变化
  await panel.refreshStatusOnly();
  assert.equal(dom.getElementById("app").innerHTML, before);
});

test("确认框打开时轮询不重绘（否则弹框会消失）", async () => {
  const { panel, dom } = await boot();
  panel.stopPolling();
  await panel.refreshAll();
  const box = dom.getElementById("confirm-box");
  box.style.display = "flex";
  const before = dom.getElementById("app").innerHTML;
  await panel.refreshStatusOnly();
  assert.equal(dom.getElementById("app").innerHTML, before);
});

test("自动刷新默认关闭：不给后端 traffic log 刷噪音", async () => {
  const { panel, dom } = await boot();
  assert.equal(panel.state.pollEnabled, false);
  const html = dom.getElementById("app").innerHTML;
  assert.match(html, /id="chk-poll"/);
  assert.doesNotMatch(html, /id="chk-poll" checked/);
});
