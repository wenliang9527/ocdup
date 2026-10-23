/**
 * ocdup 面板代理服务器（ocdup-zcode 插件的 MCP App 后端）。
 *
 * 职责只有两个：
 * 1. 把本插件声明的 `ocdup-panel` server 上的 `ocdup_*` 工具转发给用户级配置的
 *    ocdup server（config.json → mcp.servers.ocdup → publish\ocdup.exe）。
 *    转发**不走宿主**：MCP 的 tools/call 只有 client→server 方向，宿主 client 不受理
 *    server 侧回发的 tools/call（实测回 "Method not found"）。因此代理在首次转发时
 *    按 config.json 的启动参数自行拉起 ocdup.exe（StdioClientTransport 子进程）直连。
 *    该实例与宿主拉的 ocdup 同为 OpenOCD 门面，经 127.0.0.1:6666 TCL 共享同一后端
 *    （端口纪律见 ocdup-flash skill：6666 单实例，DSH/ZCode 共享），不会重复烧录门面。
 * 2. 把离线打包好的单文件面板 HTML（dist/panel.html，≤4MiB）作为 MCP App 资源下发。
 *
 * 结构：
 * - createPanelProxy(upstream, htmlProvider?) 纯工厂——upstream 只要求实现
 *   Protocol.request 形态（InMemory 自测传假 ocdup 的宿主 client）；
 * - createLazyUpstream(create) 惰性单飞包装——首次转发才建连接，并发共享，失败可重试；
 * - readOcdupLaunchConfig() 从 ZCode config.json 读 ocdup 启动参数；
 * - connectUserOcdup() 拉起子进程并完成 MCP 握手，返回 Client 作转发上游；
 * - stdio 入口 = lazyUpstream(connectUserOcdup) + StdioServerTransport。
 *
 * 依赖：ZCode config.json 配好 mcp.servers.ocdup。转发失败时工具回结构化错误，
 * 面板显示指引而不是空白。
 */
import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/server";
import {
  registerAppTool,
  registerAppResource,
  RESOURCE_MIME_TYPE,
} from "@modelcontextprotocol/ext-apps/server";
import { readFile } from "node:fs/promises";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";
import { homedir } from "node:os";
import process from "node:process";

export const OCDUP_SERVER_NAME = "ocdup"; // 用户级 config.json → mcp.servers.ocdup
export const RESOURCE_URI = "ui://ocdup-zcode/panel.html";
export const SURFACE_ID = "ocdup-panel"; // 与 plugin.json ui.surfaces[0].id 一致
export const PANEL_TOOL = "panel"; // 打开面板的锚点工具（model 点它即渲染面板）
export const PROXY_VERSION = "0.3.0";
const MAX_PANEL_BYTES = 4 * 1024 * 1024;
const CALL_TIMEOUT_MS = 10 * 60_000; // 烧录大镜像可能很慢，与宿主默认超时解耦

const here = dirname(fileURLToPath(import.meta.url));
// 源码态 server.mjs 在 server/ 下，产物态与 dist/ 同级——两处都找
const HTML_CANDIDATES = [join(here, "dist", "panel.html"), join(here, "..", "dist", "panel.html")];

async function loadPanelHtml() {
  for (const p of HTML_CANDIDATES) {
    try {
      const html = await readFile(p, "utf8");
      if (Buffer.byteLength(html, "utf8") > MAX_PANEL_BYTES) {
        return `<!doctype html><meta charset="utf-8"><body style="font-family:sans-serif;padding:16px">面板资源超过 4 MiB 上限（构建产物异常），请重新构建。</body>`;
      }
      return html;
    } catch {
      /* try next */
    }
  }
  return `<!doctype html><meta charset="utf-8"><body style="font-family:sans-serif;padding:16px;color:#c0392b">未找到面板资源 dist/panel.html —— 插件安装不完整，请在 panel/ 目录执行 pnpm build 后重装插件。</body>`;
}

// UI 可调用的 ocdup 工具（转发白名单）。schema 用宽松 zod——ocdup 端的校验才是真闸，
// 这里只负责透传参数并保证 tools/list 自描述。
const hexStr = z.string().describe("hex 字符串，如 0x08000000");
export const FORWARDED_TOOLS = [
  { name: "ocdup_status", target: "status", description: "Ocdup 后端状态：OpenOCD 是否在运行、版本、目标核 running/halted。只读。", schema: z.object({}) },
  { name: "ocdup_connect", target: "connect", description: "连接 OpenOCD 后端（未运行时自动拉起），返回版本、目标状态、芯片识别与 flash 布局。", schema: z.object({}) },
  { name: "ocdup_identify_chip", target: "identify_chip", description: "识别芯片：CPUID + DEV_ID + 家族。只读。", schema: z.object({ dbgmcuAddress: hexStr.optional() }) },
  { name: "ocdup_flash_info", target: "flash_info", description: "列出 flash banks（驱动、基址、大小）。只读。", schema: z.object({}) },
  { name: "ocdup_flash_write", target: "flash_write", description: "烧录镜像到 flash（写 + 读回校验 + 可选复位运行）。UI 侧已二次确认。", schema: z.object({ file: z.string(), address: hexStr.optional(), verify: z.boolean().default(true), reset: z.boolean().default(true) }) },
  { name: "ocdup_flash_erase_sector", target: "flash_erase_sector", description: "擦除 flash 扇区（破坏性）。UI 侧已二次确认。", schema: z.object({ bank: z.number().default(0), first: z.number().default(0), last: z.number().default(-1) }) },
  { name: "ocdup_verify_image", target: "verify_image", description: "只读比对：把镜像与目标 flash 逐字节校验，不写任何东西。", schema: z.object({ file: z.string(), address: hexStr.optional() }) },
  { name: "ocdup_disconnect", target: "disconnect", description: "停掉 ocdup 自拉的 OpenOCD 并释放探针（之后硬件工具都需 reconnect）。UI 侧已二次确认。", schema: z.object({}) },
  { name: "ocdup_set_target", target: "set_target", description: "按 ocd.json 的 target_presets 切换 target_cfg 并重启后端；传 name='list' 列出预设。", schema: z.object({ name: z.string().describe("预设名（大小写不敏感）或 'list'") }) },
  { name: "ocdup_halt", target: "halt", description: "暂停目标 CPU（读写寄存器/内存前需要）。", schema: z.object({ timeoutMs: z.number().optional() }) },  { name: "ocdup_resume", target: "resume", description: "恢复目标 CPU 运行。", schema: z.object({ address: hexStr.optional() }) },
  { name: "ocdup_reset", target: "reset", description: "复位目标（run=跑固件 / halt=停住 / init）。", schema: z.object({ mode: z.enum(["run", "halt", "init"]).default("run") }) },
  { name: "ocdup_load_svd", target: "load_svd", description: "加载 CMSIS-SVD 并列出外设/寄存器清单。PC 侧读文件，不碰目标。", schema: z.object({ file: z.string().optional(), peripheral: z.string().optional() }) },
  { name: "ocdup_read_peripheral_register", target: "read_peripheral_register", description: "按 SVD 名读外设寄存器并解码位域。只读。", schema: z.object({ name: z.string().describe("PERIPH.REG，如 RCC.CR") }) },
  { name: "ocdup_write_peripheral_register", target: "write_peripheral_register", description: "按 SVD 名写外设寄存器（UI 侧已二次确认）。", schema: z.object({ name: z.string(), value: hexStr }) },
  // 只用于读扇区几何（"flash info <bank>"），面板不接受任意命令输入
  { name: "ocdup_run_command", target: "run_command", description: "只读 TCL 命令（面板仅用于 flash info 查扇区几何）。", schema: z.object({ command: z.string() }) },
];

// ── 用户级 ocdup 的发现与直连 ──

/** ZCode config.json 候选路径：OCDUP_ZCODE_CONFIG 可覆盖（测试用），默认按用户主目录推断。 */
function zcodeConfigCandidates() {
  const override = process.env.OCDUP_ZCODE_CONFIG;
  if (override) return [override];
  return [join(homedir(), ".zcode", "cli", "config.json")];
}

/**
 * 从 ZCode config.json 读用户级 ocdup 的启动参数（stdio 命令 + args + env）。
 * 任何缺失/非法都抛可行动的错误——最终会以 ERROR 文本显示在面板上。
 */
export async function readOcdupLaunchConfig() {
  const candidates = zcodeConfigCandidates();
  let raw = null;
  let readErr = null;
  for (const p of candidates) {
    try {
      raw = JSON.parse(await readFile(p, "utf8"));
      break;
    } catch (err) {
      if (err?.code !== "ENOENT") {
        readErr = err;
        break;
      }
    }
  }
  if (!raw) {
    const detail = readErr
      ? `读取/解析失败：${readErr.message ?? readErr}`
      : `未找到（找过 ${candidates.join("、")}）`;
    throw new Error(`ZCode config.json ${detail}。确认 ZCode 已安装且配置了 mcp.servers.ocdup。`);
  }
  const entry = raw?.mcp?.servers?.ocdup ?? raw?.mcp?.ocdup;
  if (!entry || typeof entry !== "object") {
    throw new Error(
      `ZCode config.json 里没有 mcp.servers.ocdup 条目。` +
        `按 ocdup 仓库根 README「在 ZCode 中注册」配置后重开会话。`,
    );
  }
  if (entry.enabled === false) {
    throw new Error(`ZCode config.json 的 mcp.servers.ocdup 处于 enabled: false——启用后重开会话。`);
  }
  if (entry.type !== "stdio" || typeof entry.command !== "string" || !entry.command) {
    throw new Error(
      `mcp.servers.ocdup 必须是 type:"stdio" 且带 command（当前 ${JSON.stringify({ type: entry.type, command: entry.command })}）。`,
    );
  }
  return {
    command: entry.command,
    args: Array.isArray(entry.args) ? entry.args : [],
    env: entry.env && typeof entry.env === "object" ? entry.env : {},
  };
}

/**
 * 惰性单飞上游：首次 request 才 create()，并发调用共享同一次连接；
 * create 失败不缓存（下一次调用重试）。
 *
 * v0.4.2 起补「死连接自愈」：连上之后如果 ocdup.exe 被重装/重启（连接断开），
 * 缓存的 Client 会一直报 CONNECTION_CLOSED，面板从此全坏直到重开会话。
 * 因此 request 抛连接类错误时清掉 pending，下一次调用重新拉起子进程。
 */
export function createLazyUpstream(create) {
  let pending = null;
  const acquire = () => {
    if (!pending) {
      pending = Promise.resolve()
        .then(create)
        .catch((err) => {
          pending = null;
          throw err;
        });
    }
    return pending;
  };
  /** 连接已死（而不是工具本身报错）——只有这类错误才值得重建上游 */
  const isDeadConnection = (err) => {
    const msg = String(err?.message ?? err ?? "");
    const code = String(err?.code ?? "");
    return /CONNECTION_CLOSED|Connection closed|EPIPE|ECONNRESET|ENOTCONN|MCP error -32000|transport.*closed|process exited|terminated/i.test(
      msg,
    ) || /CONNECTION_CLOSED|EPIPE|ECONNRESET/.test(code);
  };
  return {
    /** Protocol.request 形态，直接对接 forwardCall */
    request: async (req, opts) => {
      const conn = await acquire();
      try {
        return await conn.request(req, opts);
      } catch (err) {
        if (isDeadConnection(err)) {
          // 丢弃死连接；下一次 request 会重新 create()（失败也不缓存）
          const dead = pending;
          pending = null;
          void dead?.then((c) => c?.close?.()).catch(() => {});
          const wrapped = new Error(`${String(err?.message ?? err)}（上游连接已断开，已自动重置，下次调用会重连）`);
          wrapped.cause = err;
          throw wrapped;
        }
        throw err;
      }
    },
    close: async () => {
      if (!pending) return;
      const c = await pending.then(
        (x) => x,
        () => null,
      );
      pending = null;
      if (c) await c.close?.();
    },
  };
}

/** 收集子进程 stderr 尾部（诊断启动失败用）。 */
function makeStderrTail(transport, maxChunks = 50) {
  const chunks = [];
  transport.stderr?.on("data", (c) => {
    chunks.push(Buffer.from(c));
    if (chunks.length > maxChunks) chunks.shift();
    process.stderr.write(`[ocdup-panel][ocdup] ${String(c)}`);
  });
  return () => {
    const text = Buffer.concat(chunks).toString("utf8").trim();
    return text ? text.split("\n").slice(-5).join("\n") : "";
  };
}

/**
 * 拉起用户级 ocdup.exe 并完成 MCP initialize 握手，返回 Client（转发上游）。
 * env 传 config 里声明的 OCD_CONFIG/OCD_BIN/OCD_SCRIPTS 等；transport 会并合
 * 默认安全环境（PATH/SYSTEMROOT 等），子进程能正常解析与 spawn OpenOCD。
 */
export async function connectUserOcdup() {
  const cfg = await readOcdupLaunchConfig();
  const { Client } = await import("@modelcontextprotocol/client");
  const { StdioClientTransport } = await import("@modelcontextprotocol/client/stdio");
  const transport = new StdioClientTransport({
    command: cfg.command,
    args: cfg.args,
    env: cfg.env,
    stderr: "pipe",
  });
  const stderrTail = makeStderrTail(transport);
  const client = new Client({ name: "ocdup-panel-proxy", version: PROXY_VERSION });
  try {
    await client.connect(transport); // spawn + initialize 握手
  } catch (err) {
    await client.close().catch(() => {});
    const tail = stderrTail();
    throw new Error(
      `用户级 ocdup 启动/握手失败（${cfg.command}）：${err?.message ?? err}` +
        (tail ? `\n子进程 stderr 末尾：\n${tail}` : ""),
    );
  }
  process.stderr.write(`[ocdup-panel] user-level ocdup connected: ${cfg.command}\n`);
  return client;
}

/**
 * 创建面板代理服务器实例。
 * @param upstream {{ request: (req: {method: string, params: object}, opts?: object) => Promise<object> }}
 *   转发目标：stdio 模式传 createLazyUpstream(connectUserOcdup)；
 *   InMemory 自测传假 ocdup 的宿主 client。只要求实现 Protocol.request 形态。
 * @param htmlProvider {() => Promise<string>} 面板 HTML 提供器（自测可注入替代产物）。
 */
export function createPanelProxy(upstream, htmlProvider = loadPanelHtml) {
  const server = new McpServer(
    { name: "ocdup-panel", version: PROXY_VERSION },
    { capabilities: { tools: {}, resources: {} } },
  );

  // ── 面板 HTML 资源 ──
  registerAppResource(server, "Ocdup 调试面板", RESOURCE_URI, { mimeType: RESOURCE_MIME_TYPE }, async () => {
    const html = await htmlProvider();
    return { contents: [{ uri: RESOURCE_URI, mimeType: RESOURCE_MIME_TYPE, text: html }] };
  });

  // ── 打开面板的锚点工具：宿主看到 _meta.ui.resourceUri 就会渲染面板 ──
  // surface = plugin.json 里声明的 surface id（宿主据此把面板做成单例实例，重复打开复用）；
  // preferredDisplayMode = "fullscreen" 让宿主直接开进侧栏（宿主内部 onOpenSidePane），
  // 而不是先渲染成对话内联卡片再让用户点展开。
  registerAppTool(
    server,
    PANEL_TOOL,
    {
      title: "Ocdup 调试面板",
      description: "打开 Ocdup 调试面板：连接状态、Flash 扇区图、寄存器查看器、操作时间线。只读打开，不做任何操作。",
      inputSchema: z.object({}),
      _meta: { ui: { resourceUri: RESOURCE_URI, surface: SURFACE_ID, preferredDisplayMode: "fullscreen" } },
    },
    async () => ({
      content: [{ type: "text", text: "Ocdup 调试面板已打开（侧栏）。面板内的操作会经代理转发到用户级 ocdup 服务器。" }],
    }),
  );

  // ── ocdup_* 转发工具：visibility=["app"]（仅面板可见，不占模型上下文）──
  for (const spec of FORWARDED_TOOLS) {
    registerAppTool(
      server,
      spec.name,
      {
        title: spec.name,
        description: `[面板专用] ${spec.description}`,
        inputSchema: spec.schema,
        _meta: { ui: { resourceUri: RESOURCE_URI, visibility: ["app"] } },
      },
      async (args, ctx) => {
        const res = await forwardCall(upstream, spec.target, args ?? {}, ctx);
        return { content: [{ type: "text", text: res.text }], isError: res.isError };
      },
    );
  }

  return server;
}

/**
 * 经上游（用户级 ocdup 的 Client 连接）调工具，把 CallToolResult 归一成 {text, isError}。
 * 下游（面板宿主）带了 progressToken 时，用 onprogress 订阅 ocdup 的进度
 * （Protocol 会自动在请求上挂 token），再经 ctx.mcpReq.notify 以面板的 token 转发回去。
 */
async function forwardCall(upstream, toolName, args, ctx) {
  try {
    const token = ctx?.mcpReq?._meta?.progressToken;
    const onprogress =
      token !== undefined
        ? (p) => {
            ctx.mcpReq
              .notify({
                method: "notifications/progress",
                params: {
                  progressToken: token,
                  progress: p.progress,
                  ...(p.total !== undefined ? { total: p.total } : {}),
                  ...(p.message !== undefined ? { message: p.message } : {}),
                },
              })
              .catch(() => {});
          }
        : undefined;
    const result = await upstream.request(
      { method: "tools/call", params: { name: toolName, arguments: args } },
      { timeout: CALL_TIMEOUT_MS, resetTimeoutOnProgress: true, signal: ctx?.signal, onprogress },
    );
    const text = (result.content ?? [])
      .map((c) => (c.type === "text" ? c.text : JSON.stringify(c)))
      .join("\n");
    return { text, isError: Boolean(result.isError) };
  } catch (err) {
    const msg = String(err?.message ?? err);
    if (/-32602|Method not found|Unknown tool|not found/i.test(msg)) {
      return {
        text: `ERROR: 用户级 MCP 服务器 "${OCDUP_SERVER_NAME}" 上没有工具 ${toolName}（${msg}）。ocdup 版本过旧或转发白名单与 ocdup 工具名不一致，请更新 publish\\ocdup.exe 后重开会话。`,
        isError: true,
      };
    }
    return {
      text: `ERROR: 调用 ${OCDUP_SERVER_NAME} 的 ${toolName} 失败：${msg}`,
      isError: true,
    };
  }
}

// ── stdio 入口（直接 node server/server.mjs 被 plugin.json 拉起）──
import { pathToFileURL } from "node:url";
const isMain = Boolean(process.argv[1]) && import.meta.url === pathToFileURL(process.argv[1]).href;
if (isMain) {
  const { StdioServerTransport } = await import("@modelcontextprotocol/server/stdio");
  // 上游 = 用户级 ocdup 的惰性连接：面板开而不用不拉子进程，首次转发才拉起，
  // 并发共享一条连接；ocdup.exe 与宿主拉的实例同为门面，共享 6666 后端。
  const upstream = createLazyUpstream(connectUserOcdup);
  const server = createPanelProxy(upstream);
  process.stderr.write(
    `[ocdup-panel] proxy ready: ${FORWARDED_TOOLS.length} tools forwarded -> ${OCDUP_SERVER_NAME} (spawned on first call), resource ${RESOURCE_URI}\n`,
  );
  await server.connect(new StdioServerTransport());
  // 宿主杀掉本进程时尽力断开子进程（stdin EOF 后 ocdup.exe 会自行退出）
  process.on("exit", () => {
    upstream.close().catch(() => {});
  });
}
