"""Ocdup 端到端验证：假 TCL-RPC 后端 + 真实 MCP stdio 会话。

验证内容（无需真实硬件）：
  1. tools/list 把 connect/status/flash_info/flash_write/read_memory/show_config 都注册了
  2. connect 探测到已在监听的后端（不重复拉起），并查回 version/targets
  3. read_memory 发出的 TCL 命令与返回文本
  4. flash_write 走 program + reset run，权限闸放行合法路径
  5. flash_write 被 flash_allowed_paths / read_only 拦截，且文案给出解除指引
  6. read_memory 超出 memory_read_max_bytes 被拦截
  7. show_config(reload=true) 能重读 ocd.json
  8. 无 openocd 可执行文件 / 后端进程起不来时，connect 给出可操作报错
  9. dump_memory 落盘闸：白名单内放行落盘，白名单外 / 超 dump_max_bytes 被拦截
  10. backend_log：非 ocdup 启动的后端如实说明「无捕获输出」
"""
import json
import os
import re
import shutil
import socket
import subprocess
import sys
import tempfile
import threading
import time

DELIM = b"\x1a"
HOST = "127.0.0.1"
PORT = 29171
REPO_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DLL = os.environ.get("OCDUP_DLL") or os.path.join(REPO_ROOT, "src", "Ocdup", "bin", "Debug", "net8.0", "ocdup.dll")


def handle_command(cmd):
    c = cmd.strip()
    if c.startswith("ocd_version") or c == "version":
        return "Open On-Chip Debugger 0.12.0 (fake backend for Ocdup e2e)"
    if c == "targets":
        return ("    TargetName         Type       Endian TapName            State\n"
                "--  ------------------ ---------- ------ ------------------ ------------\n"
                " 0* at32f435zmt7       cortex_m   little at32f435zmt7.cpu   running")
    if c.startswith("flash banks"):
        return "0 : at32f435zmt7.flash (at32f435xx) at 0x08000000, size 0x00100000, buswidth 2, chipwidth 4"
    if c.startswith("program"):
        return ("** Programming Started **\n"
                "info : fake program command: " + c + "\n"
                "** Programming Finished **\n"
                "** Verified OK **")
    if c.startswith("reset"):
        return ""
    if c.startswith("dump_image"):
        m = re.match(r"dump_image \{(.*)\} (0x[0-9a-fA-F]+) (\d+)$", c)
        if m:
            path, addr, length = m.group(1), int(m.group(2), 16), int(m.group(3))
            data = bytes(((addr + i) & 0xFF) for i in range(min(length, 1024 * 1024)))
            with open(path, "wb") as f:
                f.write(data)
            return ""
        return "Error: fake backend could not parse dump_image"
    if c.startswith("verify_image"):
        m = re.match(r"verify_image \{(.+?)\}( 0x[0-9a-fA-F]+)?$", c)
        if m:
            path = m.group(1)
            size = os.path.getsize(path) if os.path.exists(path) else 4096
            return "verified %d bytes in 0.01s" % size
        return "Error: fake backend could not parse verify_image"
    if c.startswith("mdw"):
        parts = c.split()
        addr = int(parts[1], 16)
        count = int(parts[2]) if len(parts) > 2 else 1
        lines = []
        for i in range(0, count, 4):
            vals = " ".join("%08x" % ((addr + (i + k) * 4) & 0xFFFFFFFF) for k in range(min(4, count - i)))
            lines.append("0x%08x: %s " % (addr + i * 4, vals))
        return "\n".join(lines)
    return "echo: " + c


class FakeBackend(threading.Thread):
    """假 TCL-RPC 后端：按 0x1a 分帧，逐条响应，并记录收到的命令。"""

    def __init__(self, port):
        super().__init__(daemon=True)
        self.port = port
        self.commands = []
        self.lock = threading.Lock()
        self.srv = socket.socket()
        self.srv.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        self.srv.bind((HOST, port))
        self.srv.listen(4)

    def run(self):
        while True:
            try:
                conn, _ = self.srv.accept()
            except OSError:
                return
            threading.Thread(target=self.serve, args=(conn,), daemon=True).start()

    def serve(self, conn):
        buf = b""
        with conn:
            while True:
                try:
                    data = conn.recv(4096)
                except OSError:
                    return
                if not data:
                    return
                buf += data
                while DELIM in buf:
                    raw, buf = buf.split(DELIM, 1)
                    inner = raw.decode("utf-8", "replace")
                    if inner.startswith("capture { "):
                        inner = inner[len("capture { "):]
                    if inner.endswith(" }"):
                        inner = inner[:-2]
                    with self.lock:
                        self.commands.append(inner)
                    try:
                        conn.sendall(handle_command(inner).encode("utf-8") + DELIM)
                    except OSError:
                        return


class Mcp:
    """极简 MCP stdio 客户端（够用即可：initialize / tools/list / tools/call）。"""

    def __init__(self, dll, cwd, env):
        self.proc = subprocess.Popen(
            ["dotnet", dll], cwd=cwd, env=env,
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
            text=True, encoding="utf-8", bufsize=1)
        self.next_id = 2

    def _read_until(self, want_id):
        while True:
            line = self.proc.stdout.readline()
            if not line:
                raise RuntimeError("ocdup closed stdout unexpectedly")
            msg = json.loads(line)
            if msg.get("id") == want_id:
                return msg

    def _send(self, method, params, mid):
        msg = {"jsonrpc": "2.0", "id": mid, "method": method, "params": params}
        self.proc.stdin.write(json.dumps(msg) + "\n")
        self.proc.stdin.flush()
        return self._read_until(mid)

    def initialize(self):
        r = self._send("initialize", {"protocolVersion": "2024-11-05", "capabilities": {},
                                      "clientInfo": {"name": "e2e", "version": "1"}}, 1)
        self.proc.stdin.write(json.dumps({"jsonrpc": "2.0", "method": "notifications/initialized"}) + "\n")
        self.proc.stdin.flush()
        return r

    def tools(self):
        r = self._send("tools/list", {}, self.next_id)
        self.next_id += 1
        return sorted(t["name"] for t in r["result"]["tools"])

    def call(self, name, args=None):
        r = self._send("tools/call", {"name": name, "arguments": args or {}}, self.next_id)
        self.next_id += 1
        if "error" in r:
            return "<MCP error: %s>" % r["error"]
        return "\n".join(c.get("text", "") for c in r["result"].get("content", []))

    def close(self):
        try:
            self.proc.kill()
        except Exception:
            pass


def write_config(work, **permissions):
    cfg = {
        "interface_cfg": "interface/cmsis-dap.cfg",
        "target_cfg": "target/at32f435xx.cfg",
        "transport": "swd",
        "port": PORT,
        "command_timeout_seconds": 30,
        "permissions": {
            "read_only": False,
            "allow_flash": True,
            "flash_allowed_paths": ["build/"],
            "flash_max_bytes": 1048576,
            "memory_read_max_bytes": 4096,
        },
    }
    cfg["permissions"].update(permissions)
    with open(os.path.join(work, "ocd.json"), "w", encoding="utf-8") as f:
        json.dump(cfg, f, indent=2)


def banner(title):
    print("\n" + "=" * 4 + " " + title + " " + "=" * 4)


def main():
    work = os.path.join(tempfile.gettempdir(), "ocdup_e2e")
    shutil.rmtree(work, ignore_errors=True)
    os.makedirs(os.path.join(work, "build"))
    os.makedirs(os.path.join(work, "elsewhere"))
    for rel in (os.path.join("build", "fw.bin"), os.path.join("elsewhere", "there.bin")):
        with open(os.path.join(work, rel), "wb") as f:
            f.write(b"\x00" * 4096)
    write_config(work)

    backend = FakeBackend(PORT)
    backend.start()
    time.sleep(0.3)

    env = dict(os.environ)
    env["OCD_CONFIG"] = os.path.join(work, "ocd.json")
    env.pop("OCD_BIN", None)
    env.pop("OCD_SCRIPTS", None)

    mcp = Mcp(DLL, work, env)
    try:
        mcp.initialize()

        banner("tools/list")
        print(mcp.tools())

        banner("show_config")
        print(mcp.call("show_config"))

        banner("status")
        print(mcp.call("status"))

        banner("connect")
        print(mcp.call("connect"))

        banner("backend_log (fake backend was started outside ocdup -> no captured output)")
        print(mcp.call("backend_log"))

        banner("flash_info")
        print(mcp.call("flash_info"))

        banner("read_memory 0x20000000 x8 (32-bit)")
        print(mcp.call("read_memory", {"address": "0x20000000", "count": 8}))

        banner("read_memory format=json (structured values)")
        print(mcp.call("read_memory", {"address": "0x20000000", "count": 8, "format": "json"}))

        banner("verify_image build/fw.bin @0x08000000 (read-only compare)")
        print(mcp.call("verify_image", {"file": "build/fw.bin", "address": "0x08000000"}))

        banner("dump_memory 0x08000000 x4096 -> dumps/flash-head.bin")
        print(mcp.call("dump_memory", {"file": "dumps/flash-head.bin", "address": "0x08000000", "length": 4096}))
        dumped = os.path.join(work, "dumps", "flash-head.bin")
        print("  file on disk:", os.path.exists(dumped), os.path.getsize(dumped) if os.path.exists(dumped) else "-")

        banner("dump_memory elsewhere/evil.bin (outside dump_allowed_paths)")
        print(mcp.call("dump_memory", {"file": "elsewhere/evil.bin", "address": "0x08000000", "length": 4096}))

        banner("dump_memory length=99999999 (over dump_max_bytes)")
        print(mcp.call("dump_memory", {"file": "dumps/too-big.bin", "address": "0x08000000", "length": 99999999}))

        banner("flash_write build/fw.bin @0x08000000 (allowed)")
        print(mcp.call("flash_write", {"file": "build/fw.bin", "address": "0x08000000"}))

        banner("flash_write elsewhere/there.bin (outside flash_allowed_paths)")
        print(mcp.call("flash_write", {"file": "elsewhere/there.bin", "address": "0x08000000"}))

        banner("read_memory count=4000 (over memory_read_max_bytes)")
        print(mcp.call("read_memory", {"address": "0x20000000", "count": 4000}))

        banner("read_memory bad address")
        print(mcp.call("read_memory", {"address": "hello"}))

        banner("ocd.json -> read_only=true; show_config(reload) then flash_write")
        write_config(work, read_only=True)
        print(mcp.call("show_config", {"reload": True}))
        print(mcp.call("flash_write", {"file": "build/fw.bin", "address": "0x08000000"}))

        banner("backend commands observed")
        for c in backend.commands:
            print(" -", c)
    finally:
        mcp.close()

    banner("connect on a dead port with no openocd on PATH")
    write_config(work, read_only=False)
    cfg_path = os.path.join(work, "ocd.json")
    with open(cfg_path, encoding="utf-8") as f:
        cfg = json.load(f)
    cfg["port"] = 29172
    with open(cfg_path, "w", encoding="utf-8") as f:
        json.dump(cfg, f, indent=2)

    mcp2 = Mcp(DLL, work, env)
    try:
        mcp2.initialize()
        print(mcp2.call("connect"))
    finally:
        mcp2.close()

    banner("connect when the backend binary is not openocd (OCD_BIN=python.exe)")
    env2 = dict(env)
    env2["OCD_BIN"] = shutil.which("python") or sys.executable
    mcp3 = Mcp(DLL, work, env2)
    try:
        mcp3.initialize()
        print(mcp3.call("connect"))
    finally:
        mcp3.close()


if __name__ == "__main__":
    main()

