/**
 * 代理服务器离线端到端自测：
 * 面板代理（server/server.mjs 的 createPanelProxy） ↔ InMemory ↔ 假用户级 ocdup 服务器。
 * 验证：ocdup_* 白名单出现在 tools/list 且 visibility=["app"]、调用被转发到假 ocdup、
 * ocdup 缺席的工具回结构化错误、panel.html 资源以 MCP App MIME 下发且完全离线、
 * 启动配置解析、惰性单飞上游、进度通知桥接、真实 spawn（node 假 ocdup 子进程）。
 */
import test from "node:test";
import assert from "node:assert/strict";
import { Client } from "@modelcontextprotocol/client";
import { McpServer, InMemoryTransport } from "@modelcontextprotocol/server";
import { z } from "zod";
import {
  createPanelProxy,
  createLazyUpstream,
  readOcdupLaunchConfig,
  RESOURCE_URI,
} from "../server/server.mjs";
import { readFile, writeFile, mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { spawn } from "node:child_process";
import { fileURLToPath } from "node:url";

function fakeOcdup(calls) {
  const server = new McpServer({ name: "ocdup-fake", version: "0.4.30" }, { capabilities: { tools: {} } });
  server.registerTool(
    "status",
    { inputSchema: z.object({}) },
    async () => ({
      content: [{ type: "text", text: "OpenOCD: running on 127.0.0.1:6666\nversion: 0.12.0+dev\nchannel: connected, idle 1s" }],
    }),
  );
  server.registerTool(
    "identify_chip",
    { inputSchema: z.object({ dbgmcuAddress: z.string().optional() }) },
    async (args) => {
      calls.push({ tool: "identify_chip", args });
      return {
        content: [{ type: "text", text: "chip identity:\n  cpu (CPUID, ARM standard) @ 0xE000ED00: 0x412fc231\n      -> Cortex-M4 (ARMv7-M/v8-M, implementer 'A', r0p1)\n      -> dev_id 0x415, rev_id 0x1001\n      -> family: AT32F403A" }],
      };
    },
  );
  server.registerTool(
    "flash_write",
    { inputSchema: z.object({ file: z.string() }) },
    async (args, ctx) => {
      calls.push({ tool: "flash_write", args });
      // 模拟 ocdup 端烧录进度：面板带了 progressToken 就发两步进度
      const token = ctx?.mcpReq?._meta?.progressToken;
      if (token !== undefined) {
        await ctx.mcpReq.notify({ method: "notifications/progress", params: { progressToken: token, progress: 1, total: 2, message: "erasing" } });
        await ctx.mcpReq.notify({ method: "notifications/progress", params: { progressToken: token, progress: 2, total: 2, message: "writing" } });
      }
      return { content: [{ type: "text", text: "programmed: fw.bin (1024 bytes)\nverified: fw.bin (1024 bytes) matches the target flash (read-back)." }] };
    },
  );
  return server;
}

async function harness() {
  const calls = [];
  const fake = fakeOcdup(calls);
  // 真实拓扑等价物：面板代理 → [转发上游] → 用户级 ocdup。代理的 upstream 经
  // createLazyUpstream 包装假 ocdup 的宿主 client，顺带覆盖惰性单飞逻辑。
  const hostClient = new Client({ name: "zcode-host", version: "0.0.0" });
  const proxy = createPanelProxy(
    createLazyUpstream(async () => {
      const [hostT, fakeT] = InMemoryTransport.createLinkedPair();
      await Promise.all([hostClient.connect(hostT), fake.connect(fakeT)]);
      return hostClient;
    }),
  );
  const client = new Client({ name: "panel-test", version: "0.0.0" });
  const [clientT, proxyT] = InMemoryTransport.createLinkedPair();
  await Promise.all([proxy.connect(proxyT), client.connect(clientT)]);
  return {
    client,
    calls,
    stop: async () => {
      await client.close();
      await proxy.close();
      await hostClient.close();
      await fake.close();
    },
  };
}

test("tools/list：白名单 + panel 锚点，转发工具 visibility=['app']", async () => {
  const { client, stop } = await harness();
  try {
    const { tools } = await client.listTools();
    const names = tools.map((t) => t.name);
    for (const n of ["panel", "ocdup_status", "ocdup_connect", "ocdup_flash_write", "ocdup_write_peripheral_register"]) {
      assert.ok(names.includes(n), `missing ${n}`);
    }
    const fw = tools.find((t) => t.name === "ocdup_status");
    assert.equal(fw._meta?.ui?.resourceUri, RESOURCE_URI);
    assert.deepEqual(fw._meta?.ui?.visibility, ["app"]);
    const panel = tools.find((t) => t.name === "panel");
    assert.equal(panel._meta?.ui?.resourceUri, RESOURCE_URI);
    assert.equal(panel._meta?.ui?.visibility, undefined); // 模型可见
    // 侧栏直开：surface 指向插件声明的 surface id；显示模式走 openai/ui（宿主只认这个位置）
    assert.equal(panel._meta?.ui?.surface, "ocdup-panel");
    assert.equal(panel._meta?.["openai/ui"]?.preferredModelDisplayMode, "fullscreen");
    assert.equal(panel._meta?.ui?.preferredDisplayMode, undefined, "写在这里宿主不读，别再放回去");
    // v0.4.2 新增的三个转发工具
    for (const n of ["ocdup_disconnect", "ocdup_set_target", "ocdup_verify_image"]) {
      assert.ok(names.includes(n), `missing ${n}`);
      const spec = tools.find((t) => t.name === n);
      assert.deepEqual(spec._meta?.ui?.visibility, ["app"]);
    }
  } finally {
    await stop();
  }
});

test("转发：经惰性单飞上游打到假 ocdup，文本原样返回", async () => {
  const { client, calls, stop } = await harness();
  try {
    const res = await client.callTool({ name: "ocdup_status", arguments: {} });
    assert.ok(!res.isError);
    assert.match(res.content[0].text, /OpenOCD: running on 127\.0\.0\.1:6666/);

    const id = await client.callTool({ name: "ocdup_identify_chip", arguments: { dbgmcuAddress: "0xE0042000" } });
    assert.match(id.content[0].text, /family: AT32F403A/);
    assert.equal(calls.at(-1).args.dbgmcuAddress, "0xE0042000");

    const fw = await client.callTool({ name: "ocdup_flash_write", arguments: { file: "fw.bin" } });
    assert.match(fw.content[0].text, /verified/);
    assert.equal(calls.at(-1).tool, "flash_write");
  } finally {
    await stop();
  }
});

test("进度桥接：面板宿主带 progressToken 时，ocdup 的进度经代理转发回来", async () => {
  const { client, stop } = await harness();
  try {
    const events = [];
    const res = await client.callTool(
      { name: "ocdup_flash_write", arguments: { file: "fw.bin" } },
      { onprogress: (p) => events.push(p) },
    );
    assert.ok(!res.isError);
    assert.deepEqual(
      events.map((e) => e.message),
      ["erasing", "writing"],
    );
    assert.deepEqual(events.map((e) => e.progress), [1, 2]);
  } finally {
    await stop();
  }
});

test("失败路径：ocdup 没有的工具回结构化错误（isError，不炸会话）", async () => {
  const { client, stop } = await harness();
  try {
    const res = await client.callTool({ name: "ocdup_reset", arguments: { mode: "run" } });
    assert.equal(res.isError, true);
    assert.match(res.content[0].text, /^ERROR:/);
    assert.match(res.content[0].text, /没有工具 reset|ocdup\.exe/);
    // 会话还活着：再调一次 status 正常
    const ok = await client.callTool({ name: "ocdup_status", arguments: {} });
    assert.ok(!ok.isError);
  } finally {
    await stop();
  }
});

test("资源：panel.html 以 MCP App MIME 下发、单文件离线、≤4MiB", async () => {
  const { client, stop } = await harness();
  try {
    const { resources } = await client.listResources();
    const ui = resources.find((r) => r.uri === RESOURCE_URI);
    assert.ok(ui, "panel resource missing");

    const read = await client.readResource({ uri: RESOURCE_URI });
    const html = read.contents[0].text;
    assert.match(String(read.contents[0].mimeType), /mcp-app/);
    // 真实构建产物（dist/panel.html），完全离线
    const dist = await readFile(new URL("../dist/panel.html", import.meta.url), "utf8");
    assert.equal(html, dist);
    assert.ok(!/src=["']https?:|href=["']https?:|@import\s+url\(["']https?:/.test(html), "must be offline");
    assert.ok(Buffer.byteLength(html, "utf8") <= 4 * 1024 * 1024);
  } finally {
    await stop();
  }
});

test("HTML 下发的兜底形态：dist 缺失时给指引页而不是炸", async () => {
  const missing = createPanelProxy({ request: async () => { throw new Error("nope"); } }, async () => {
    throw new Error("file not found");
  });
  // 直接用 htmlProvider 的兜底逻辑：loadPanelHtml 找不到文件时返回指引页。
  // 这里只验证工厂在坏 htmlProvider 下资源读取回错误结构（资源 read 抛错 = MCP 资源错误）。
  const client = new Client({ name: "t", version: "0" });
  const [ct, pt] = InMemoryTransport.createLinkedPair();
  await Promise.all([missing.connect(pt), client.connect(ct)]);
  await assert.rejects(() => client.readResource({ uri: RESOURCE_URI }));
  await client.close();
  await missing.close();
});

// ── readOcdupLaunchConfig：从 config.json 提取 ocdup 启动参数 ──

async function withTempConfig(json, fn) {
  const dir = await mkdtemp(join(tmpdir(), "ocdup-panel-test-"));
  const file = join(dir, "config.json");
  await writeFile(file, typeof json === "string" ? json : JSON.stringify(json), "utf8");
  const prev = process.env.OCDUP_ZCODE_CONFIG;
  process.env.OCDUP_ZCODE_CONFIG = file;
  try {
    return await fn(file, dir);
  } finally {
    if (prev === undefined) delete process.env.OCDUP_ZCODE_CONFIG;
    else process.env.OCDUP_ZCODE_CONFIG = prev;
    await rm(dir, { recursive: true, force: true });
  }
}

test("readOcdupLaunchConfig：正常条目解析出 command/args/env", async () => {
  await withTempConfig(
    { mcp: { servers: { ocdup: { type: "stdio", command: "D:\\x\\ocdup.exe", args: ["--v"], env: { OCD_CONFIG: "D:\\x\\ocd.json" }, enabled: true } } } },
    async () => {
      const cfg = await readOcdupLaunchConfig();
      assert.equal(cfg.command, "D:\\x\\ocdup.exe");
      assert.deepEqual(cfg.args, ["--v"]);
      assert.deepEqual(cfg.env, { OCD_CONFIG: "D:\\x\\ocd.json" });
    },
  );
});

test("readOcdupLaunchConfig：enabled:false / 缺条目 / 坏 JSON 都回可行动错误", async () => {
  await withTempConfig(
    { mcp: { servers: { ocdup: { type: "stdio", command: "x", enabled: false } } } },
    async () => {
      await assert.rejects(() => readOcdupLaunchConfig(), /enabled: false/);
    },
  );
  await withTempConfig({ mcp: { servers: {} } }, async () => {
    await assert.rejects(() => readOcdupLaunchConfig(), /mcp\.servers\.ocdup/);
  });
  await withTempConfig("{ broken", async () => {
    await assert.rejects(() => readOcdupLaunchConfig(), /解析失败/);
  });
  await withTempConfig({ mcp: { servers: { ocdup: { type: "sse", url: "http://x" } } } }, async () => {
    await assert.rejects(() => readOcdupLaunchConfig(), /stdio/);
  });
});

// ── createLazyUpstream：惰性、单飞、失败可重试 ──

test("createLazyUpstream：首次才建连、并发共享、失败不缓存", async () => {
  let created = 0;
  const upstream = createLazyUpstream(async () => {
    created += 1;
    return { request: async () => ({ content: [{ type: "text", text: `conn-${created}` }] }), close: async () => {} };
  });
  // 并发三个请求只建一次连接
  const [a, b, c] = await Promise.all([
    upstream.request({ method: "tools/call", params: {} }),
    upstream.request({ method: "tools/call", params: {} }),
    upstream.request({ method: "tools/call", params: {} }),
  ]);
  assert.equal(created, 1);
  assert.equal(a.content[0].text, "conn-1");
  assert.equal(b.content[0].text, "conn-1");
  assert.equal(c.content[0].text, "conn-1");

  // 失败不缓存：下一个请求重试
  let fail = true;
  const flaky = createLazyUpstream(async () => {
    if (fail) throw new Error("boom");
    return { request: async () => ({ content: [{ type: "text", text: "ok" }] }) };
  });
  await assert.rejects(() => flaky.request({ method: "tools/call", params: {} }), /boom/);
  fail = false;
  const res = await flaky.request({ method: "tools/call", params: {} });
  assert.equal(res.content[0].text, "ok");
});

test("createLazyUpstream：close 关闭已建连接；未建连接时 close 是 no-op", async () => {
  let closed = 0;
  const upstream = createLazyUpstream(async () => ({
    request: async () => ({ content: [] }),
    close: async () => {
      closed += 1;
    },
  }));
  await upstream.close();
  assert.equal(closed, 0); // 从未 create
  await upstream.request({ method: "tools/call", params: {} });
  await upstream.close();
  assert.equal(closed, 1);
});

test("createLazyUpstream：连接死掉后自动重置，下一次请求重新拉起", async () => {
  let created = 0;
  let deadClosed = 0;
  const upstream = createLazyUpstream(async () => {
    created += 1;
    const mine = created;
    return {
      request: async () => {
        // 第一个连接模拟「ocdup.exe 被重装」——每次调用都报连接已关闭
        if (mine === 1) throw new Error("MCP error -32000: Connection closed");
        return { content: [{ type: "text", text: `conn-${mine}` }] };
      },
      close: async () => {
        deadClosed += 1;
      },
    };
  });

  // 第一次：连上了但请求失败（连接已死）→ 错误带上「已自动重置」提示
  await assert.rejects(() => upstream.request({ method: "tools/call", params: {} }), /已自动重置/);
  assert.equal(created, 1);

  // 第二次：因为 pending 已被清掉，会重新 create → 拿到新连接
  const res = await upstream.request({ method: "tools/call", params: {} });
  assert.equal(created, 2);
  assert.equal(res.content[0].text, "conn-2");
  assert.equal(deadClosed, 1); // 死连接被关掉，不泄漏子进程
});

test("createLazyUpstream：工具级错误（非连接问题）不重建连接", async () => {
  let created = 0;
  const upstream = createLazyUpstream(async () => {
    created += 1;
    return {
      request: async () => {
        throw new Error("ERROR: verify failed: read-back mismatch");
      },
      close: async () => {},
    };
  });
  await assert.rejects(() => upstream.request({ method: "tools/call", params: {} }), /read-back mismatch/);
  await assert.rejects(() => upstream.request({ method: "tools/call", params: {} }), /read-back mismatch/);
  assert.equal(created, 1); // 复用同一条连接
});


// ── 真实 spawn 链路：connectUserOcdup 拉一个 node 假 ocdup 子进程（MCP stdio） ──

const __dirname = fileURLToPath(new URL(".", import.meta.url));
const FAKE_OCDUP_CHILD = join(__dirname, "fake-ocdup-child.mjs");

test("connectUserOcdup：按 config 拉起子进程完成握手并转发（真实 spawn，非 InMemory）", async () => {
  await withTempConfig(
    {
      mcp: {
        servers: {
          ocdup: { type: "stdio", command: process.execPath, args: [FAKE_OCDUP_CHILD], env: { OCDUP_FAKE_TOKEN: "t1" }, enabled: true },
        },
      },
    },
    async () => {
      // 直接走 stdio 入口同款组装：lazyUpstream + connectUserOcdup
      const { connectUserOcdup } = await import("../server/server.mjs");
      const upstream = createLazyUpstream(connectUserOcdup);
      try {
        const res = await upstream.request({ method: "tools/call", params: { name: "status", arguments: {} } }, { timeout: 30_000 });
        assert.match(res.content[0].text, /OpenOCD: running/);
        // env 透传：子进程读 OCDUP_FAKE_TOKEN 证明 config.env 生效
        const envRes = await upstream.request({ method: "tools/call", params: { name: "env_echo", arguments: {} } }, { timeout: 30_000 });
        assert.match(envRes.content[0].text, /t1/);
      } finally {
        await upstream.close();
      }
    },
  );
});

test("connectUserOcdup：命令不存在时报含 command 的可行动错误", async () => {
  await withTempConfig(
    { mcp: { servers: { ocdup: { type: "stdio", command: "definitely-not-exist-xyz.exe", args: [], enabled: true } } } },
    async () => {
      const { connectUserOcdup } = await import("../server/server.mjs");
      await assert.rejects(() => connectUserOcdup(), /definitely-not-exist-xyz\.exe/);
    },
  );
});
