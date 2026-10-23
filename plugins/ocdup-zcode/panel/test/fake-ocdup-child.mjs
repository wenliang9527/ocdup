/**
 * 假用户级 ocdup 子进程（真实 stdio MCP 服务器，供 e2e spawn 链路测试用）。
 * 提供 status（读 OCDUP_FAKE_TOKEN 验证 config.env 透传）与 env_echo 两个工具。
 */
import { McpServer } from "@modelcontextprotocol/server";
import { StdioServerTransport } from "@modelcontextprotocol/server/stdio";
import { z } from "zod";

const server = new McpServer({ name: "ocdup-fake-child", version: "0.4.30" }, { capabilities: { tools: {} } });
server.registerTool("status", { inputSchema: z.object({}) }, async () => ({
  content: [{ type: "text", text: `OpenOCD: running on 127.0.0.1:6666 (fake child, token=${process.env.OCDUP_FAKE_TOKEN ?? ""})` }],
}));
server.registerTool("env_echo", { inputSchema: z.object({}) }, async () => ({
  content: [{ type: "text", text: process.env.OCDUP_FAKE_TOKEN ?? "" }],
}));
await server.connect(new StdioServerTransport());
