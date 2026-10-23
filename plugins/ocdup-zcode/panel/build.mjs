/**
 * 离线打包：esbuild 把 panel.html + app.ts + parsers.ts + i18n.ts + SDK 内联成
 * 单文件 dist/panel.html（≤4MiB，无外部引用），由 server.mjs 作为 MCP App 资源下发。
 */
import { build } from "esbuild";
import { readFile, writeFile, mkdir } from "node:fs/promises";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));
const OUT_DIR = join(here, "dist");
const OUT_FILE = join(OUT_DIR, "panel.html");
const MAX_BYTES = 4 * 1024 * 1024;

const result = await build({
  entryPoints: [join(here, "src", "app.ts")],
  bundle: true,
  format: "esm",
  target: ["es2022", "chrome120", "safari17"],
  minify: true,
  write: false,
  legalComments: "none",
  outfile: "panel.bundle.js",
  define: { "process.env.NODE_ENV": '"production"' },
});

const js = result.outputFiles[0].text;
let html = await readFile(join(here, "src", "panel.html"), "utf8");

// 用打包产物替换 <script type="module" src>，内联进单个 HTML。
// 替换串含 $ 序列（minify 后的正则字面量等），必须用函数式 replace 避免被当成替换模式。
html = html.replace(
  /\s*<script type="module" src="\.\/app\.ts"><\/script>\s*/,
  () => `\n    <script type="module">\n${js}\n    </script>\n  `,
);

if (/<script[^>]+src="\.\/app\.ts"/.test(html)) {
  console.error("[build] 未能内联脚本入口：panel.html 的 script 标签形态与预期不符");
  process.exit(1);
}
if (/src=["']https?:|href=["']https?:|@import|url\(["']?https?:/.test(html)) {
  console.error("[build] 面板资源出现外部引用（CDN/远程字体）——必须完全离线");
  process.exit(1);
}

await mkdir(OUT_DIR, { recursive: true });
await writeFile(OUT_FILE, html, "utf8");

const bytes = Buffer.byteLength(html, "utf8");
if (bytes > MAX_BYTES) {
  console.error(`[build] dist/panel.html ${bytes} bytes 超过 4 MiB 上限`);
  process.exit(1);
}
console.log(`[build] dist/panel.html ${bytes.toLocaleString()} bytes (limit ${MAX_BYTES.toLocaleString()})`);
