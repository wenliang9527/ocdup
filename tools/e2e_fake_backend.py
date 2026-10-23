"""Ocdup 端到端验证：假 TCL-RPC 后端 + 真实 MCP stdio 会话。

验证内容（无需真实硬件）：
  1. tools/list 把 connect/status/flash_info/flash_write/read_memory/show_config 都注册了
  2. connect 探测到已在监听的后端（不重复拉起），并查回 version/targets
  3. read_memory 发出的 TCL 命令与返回文本
  4. flash_write 走 program + 独立 verify_image（读回判据）+ reset run，权限闸放行合法路径
  5. flash_write 被 flash_allowed_paths / read_only 拦截，且文案给出解除指引
  6. read_memory 超出 memory_read_max_bytes 被拦截
  7. show_config(reload=true) 能重读 ocd.json
  8. 无 openocd 可执行文件 / 后端进程起不来时，connect 给出可操作报错
  9. dump_memory 落盘闸：白名单内放行落盘，白名单外 / 超 dump_max_bytes 被拦截
  10. backend_log：非 ocdup 启动的后端如实说明「无捕获输出」
   11. connect 后后 best-effort 自动识别芯片 + flash 布局；target 失配时回报失配行
   12. set_target：list / 未知名 / 切换（假后端是外部实例 → 警告 + 配置已换）/ 重复切换空转 / read_only 拦截
   13. halt 后 best-effort 回读核心寄存器（pc/lr/sp/xpsr；reset halt 同样带回）
   14. disconnect: external listener -> "cannot stop it"; read_only -> BLOCKED (panel button path)
   15. run_command 内容分类（v0.4.12）：只读 mdb 透传；program -> BLOCKED 并重定向 flash_write
   16. P1-5 断链重放：假后端断开连接后，只读 read_memory 自动重连并重放一次，业务无感
   17. ocdup rtt CLI（v0.4.19）：rtt setup/start/server start 命令序列 + 假 RTT TCP 数据面
       （--lines 2 收到两行即停）+ 收尾 rtt server stop/rtt stop；--info 只报通道清单
   18. ocdup burn CLI（v0.4.20）：verified → exit 0；arm 回读失配 → VERIFY FAILED + exit 1；
       白名单外 → BLOCKED + exit 2（烧录编排与 flash_write 同一核心）
    19. 符号层 L2（v0.4.21）：load_svd → 按名读（大小写不敏感 + 位域解码 + reset 比对）→
        cluster 寄存器（PWM.CCR 用 mdh）→ mww 写 → read-only access 拒写 → 未知名建议
   20. ocdup burn exit 3（v0.4.24）：死端口 + 无 OCD_BIN → 拉后端必败，BackendException → exit 3
   21. ocdup verify CLI（v0.4.26）：verified → exit 0；arm 回读失配 → VERIFY FAILED + exit 1；
       缺文件 → exit 2（比对判据与 verify_image 工具同一核心）
    22. ocdup rtt 死数据端口 + 用法错误（v0.4.27）：arm 后 rtt server start 成功但数据端口连不上
        → ERROR 可操作文案 + exit 1（此前未处理 SocketException 崩进程，退出码 3762504530）；未知选项 → exit 2
    23. SVD 默认镜像（v0.4.27）：load_svd() 不传 file → 回落 ocd.json 的 svd_path 并解析成功
    24. P0-3 边界定案（v0.4.28）：allow_exec=false 时 halt BLOCKED 而 disconnect 照常（后端生命周期
        不进 allow_exec；外部实例如实回报「杀不动」，且该结论不附 halted 提示）；停机前的 targets
        探测与「仅真停掉自拉后端才附 halted 提示」的组装规则由单元层钉死
    25. CRC 算法起不来（v0.4.29，真机 2026-10-13 抓原文）：verify_image 回
        `target not halted` / `timed out while waiting for target halted` → VERIFY INCONCLUSIVE
        + halt 指引；**绝不**说成「固件不匹配」（模型据此会无谓重烧）
"""
import json
import os
import re
import shutil
import socket
import struct
import subprocess
import sys
import tempfile
import threading
import time

DELIM = b"\x1a"
HOST = "127.0.0.1"
STATE = {"drop_armed": False}  # P1-5 断链模拟：arm 后，下一条指定读命令被假后端直接断开
TARGET_HALTED = False  # P0-3（v0.4.28）：halt/resume/reset 驱动的目标执行态，targets 照实回报
PORT = 29171
REPO_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DLL = os.environ.get("OCDUP_DLL") or os.path.join(REPO_ROOT, "src", "Ocdup", "bin", "Debug", "net8.0", "ocdup.dll")

# ── 假 RTT 数据面（v0.4.19 链路 17）：rtt server start <port> <ch> 命令到达时在本机
# 起一个迷你 TCP 监听，连接建立即吐两行样例数据（CLI 以 --lines 2 收满即停）。──
RTT_SERVERS = {}


def start_rtt_server(port):
    if port in RTT_SERVERS:
        return
    srv = socket.socket()
    srv.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    srv.bind((HOST, port))
    srv.listen(2)
    RTT_SERVERS[port] = srv
    threading.Thread(target=serve_rtt, args=(srv,), daemon=True).start()


def serve_rtt(srv):
    while True:
        try:
            conn, _ = srv.accept()
        except OSError:
            return
        try:
            conn.sendall(b"hello from fake rtt line 1\nhello from fake rtt line 2\n")
        except OSError:
            pass
        # 保持连接打开：CLI 达到 --lines 上限后自己断开并走收尾序列


def handle_command(cmd):
    global TARGET_HALTED
    c = cmd.strip()
    if c.startswith("ocd_version") or c == "version":
        return "Open On-Chip Debugger 0.12.0 (fake backend for Ocdup e2e)"
    if c == "targets":
        state = "halted" if TARGET_HALTED else "running"
        return ("    TargetName         Type       Endian TapName            State\n"
                "--  ------------------ ---------- ------ ------------------ ------------\n"
                " 0* at32f435zmt7       cortex_m   little at32f435zmt7.cpu   " + state)
    if c.startswith("flash banks"):
        return "0 : at32f435zmt7.flash (at32f435xx) at 0x08000000, size 0x00100000, buswidth 2, chipwidth 4"
    if c.startswith("program"):
        return ("** Programming Started **\n"
                "info : fake program command: " + c + "\n"
                "** Programming Finished **\n"
                "** Verified OK **")
    if c.startswith("reset"):
        TARGET_HALTED = "halt" in c  # reset run → running；reset halt → halted
        return ""
    if c == "halt" or c.startswith("halt "):
        TARGET_HALTED = True
        return "halted"
    if c.startswith("resume"):
        TARGET_HALTED = False
        return ""
    if c.startswith("reg "):
        # `reg <name>`（halt 后核心寄存器回读）：按 atopenocd 实测形态响应（冒号 + 位宽注，
        # 寄存器名大小写敏感——xPSR 大写才认），未知名回真实错误文本
        regs = {"pc": "pc (/32): 0x080002ad", "lr": "lr (/32): 0x080001ed",
                "sp": "sp (/32): 0x20019d28", "xPSR": "xPSR (/32): 0x01000000"}
        name = c.split()[1]
        return regs.get(name, "register %s not found in current target" % name)
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
            # v0.4.29 链路 25：arm 后回真机抓到的 CRC 算法失败原文（两种形态）
            crc = STATE.get("crc_fail")
            if crc:
                STATE["crc_fail"] = None
                return crc
            # v0.4.20 链路 18：arm 后下一次 verify_image 回读回不符（burn 的 exit 1 路径）
            if STATE.get("fail_verify"):
                STATE["fail_verify"] = False
                return "Error: contents differ at 0x08000040 (fake verify mismatch)"
            path = m.group(1)
            size = os.path.getsize(path) if os.path.exists(path) else 4096
            return "verified %d bytes in 0.01s" % size
        return "Error: fake backend could not parse verify_image"
    if c.startswith("mww") or c.startswith("mwh") or c.startswith("mwb"):
        return ""
    if c.startswith("flash erase_sector"):
        return "erased sectors 0..last on bank 0"
    if c.startswith("rtt server start"):
        m = re.match(r"rtt server start (\d+) (\d+)$", c)
        if m:
            # v0.4.27 链路：__e2e_no_rtt_server__ 之后，命令「成功」但不真开数据端口
            # （模拟 rtt server 没起来/端口被占——数据面连不上的回归场景）。
            if STATE.get("no_rtt_server"):
                STATE["no_rtt_server"] = False
                return ""
            start_rtt_server(int(m.group(1)))
            return ""
        return "Error: fake backend could not parse rtt server start"
    if c.startswith("rtt server stop"):
        return ""
    if c.startswith("rtt setup"):
        return ""
    if c == "rtt start":
        return ""
    if c == "rtt channels":
        return ("Channels: up=4, down=1\n"
                "Up-channels:\n"
                "0: TERMINAL 1024 0\n"
                "1: LOG 256 0\n"
                "2: BIN 512 1\n"
                "3: IDLE 64 0\n"
                "Down-channels:\n"
                "0: INPUT 128 0")
    if c == "rtt stop":
        return ""
    # ── v0.4.30 断点/观察点（命令面按 2026-10-19 真机核定：atopenocd-py32）──
    if c == "bp":
        return ""  # 裸 bp = 列断点，空列表无输出
    if c.startswith("bp "):
        # 真机：length 必填（缺了只回 usage 文本）；运行态软断点被拒、硬件断点可设
        if " hw" not in c and TARGET_HALTED is False:
            return "target at32f403axx.cpu is not halted (add breakpoint)\ncan't add breakpoint: target running"
        return "breakpoint set"
    if c.startswith("rbp "):
        return ""  # rbp <addr> / rbp all 都静默成功
    if c == "wp":
        # 裸 wp = 列观察点，空列表无输出；__e2e_arm_wp_list__ 之后回真机形态列表
        # （remove_watchpoint 全清的数据源——fork 没有 rwp all 形态）
        if STATE.get("wp_list"):
            listing = STATE["wp_list"]
            STATE["wp_list"] = None
            return listing
        return ""
    if c.startswith("wp "):
        # 真机：length 必填（wp <addr> <len> <access> [value]），运行态一律被拒
        if TARGET_HALTED is False:
            return "target at32f403axx.cpu is not halted (add watchpoint)\ncan't add write watchpoint, target running"
        return ""
    if c.startswith("rwp "):
        return ""  # rwp 只认地址（'rwp all' → Invalid command argument）
    if c.startswith("mdw") or c.startswith("mdh") or c.startswith("mdb"):
        parts = c.split()
        addr = int(parts[1], 16)
        count = int(parts[2]) if len(parts) > 2 else 1
        width = 8 if c.startswith("mdb") else (16 if c.startswith("mdh") else 32)
        fmt = {8: "%02x", 16: "%04x", 32: "%08x"}[width]
        mask = (1 << width) - 1
        step = width // 8
        per_line = 32 // width
        lines = []
        for i in range(0, count, per_line):
            vals = " ".join(fmt % ((addr + (i + k) * step) & mask) for k in range(min(per_line, count - i)))
            lines.append("0x%08x: %s " % (addr + i * step, vals))
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
                    # P1-5 断链模拟：arm 后，下一条指定的读命令不回包、直接断开连接——
                    # ocdup 侧应 IOException → 判定只读 op → 自动重连重放一次。
                    if inner == "__e2e_arm_drop__":
                        STATE["drop_armed"] = True
                        try:
                            conn.sendall(b"armed" + DELIM)
                        except OSError:
                            return
                        continue
                    if inner == "__e2e_arm_fail_verify__":
                        STATE["fail_verify"] = True
                        try:
                            conn.sendall(b"armed" + DELIM)
                        except OSError:
                            return
                        continue
                    # v0.4.29：arm 后下一条 verify_image 回真机的 CRC 算法失败原文
                    if inner in ("__e2e_arm_crc_running__", "__e2e_arm_crc_halted__"):
                        STATE["crc_fail"] = ("target not halted\nerror executing cortex_m crc algorithm"
                                             if inner.endswith("running__")
                                             else "timed out while waiting for target halted\n"
                                                  "error executing cortex_m crc algorithm")
                        try:
                            conn.sendall(b"armed" + DELIM)
                        except OSError:
                            return
                        continue
                    # v0.4.27：arm 后，下一条 rtt server start 只回成功、不开数据端口
                    if inner == "__e2e_no_rtt_server__":
                        STATE["no_rtt_server"] = True
                        try:
                            conn.sendall(b"armed" + DELIM)
                        except OSError:
                            return
                        continue
                    # v0.4.30 真机核定：arm 后下一条裸 wp 回真机形态的观察点列表
                    # （remove_watchpoint 无地址 = 先列再逐个 rwp 的数据源）
                    if inner == "__e2e_arm_wp_list__":
                        STATE["wp_list"] = ("address: 0x20000000, len: 0x00000004, r/w/a: 1, value: 0x00000000, mask: 0xffffffff\n"
                                            "address: 0x20000100, len: 0x00000002, r/w/a: 2, value: 0x00000000, mask: 0xffffffff")
                        try:
                            conn.sendall(b"armed" + DELIM)
                        except OSError:
                            return
                        continue
                    if STATE.get("drop_armed") and inner == "mdw 0x20000000 1":
                        STATE["drop_armed"] = False
                        return
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


def build_mini_elf():
    """最小合法 ELF32 LE（.symtab + .strtab）：list/read/write_variable 的 e2e 夹具。
    符号：gCounter(0x20000004,4 OBJECT) / s_local(0x20000010,1 OBJECT) / doThing(FUNC,应被排除)。"""
    strtab = b"\x00gCounter\x00s_local\x00doThing\x00"
    symtab = (struct.pack("<IIIBBH", 0, 0, 0, 0, 0, 0)
              + struct.pack("<IIIBBH", 1, 0x20000004, 4, 1, 0, 1)
              + struct.pack("<IIIBBH", 10, 0x20000010, 1, 1, 0, 1)
              + struct.pack("<IIIBBH", 18, 0x08000100, 88, 2, 0, 1))
    ehsize, shentsize, shnum = 52, 40, 4
    sym_off, str_off, shoff = ehsize, ehsize + len(symtab), ehsize + len(symtab) + len(strtab)

    def shdr(typ, off, size, link=0, entsize=0):
        return struct.pack("<IIIIIIIIII", 0, typ, 0, 0, off, size, link, 0, 4, entsize)

    eh = b"\x7fELF\x01\x01\x01\x00" + b"\x00" * 8
    eh += struct.pack("<HHIIIIIHHHHHH", 2, 40, 1, 0, 0, shoff, 0, ehsize, 0, 0, shentsize, shnum, 0)
    return eh + symtab + strtab + shdr(0, 0, 0) + shdr(2, sym_off, len(symtab), 2, 16) \
        + shdr(3, str_off, len(strtab)) + shdr(1, 0, 0)


def write_config(work, _extras=None, **permissions):
    cfg = {
        "interface_cfg": "interface/cmsis-dap.cfg",
        "target_cfg": "target/at32f435xx.cfg",
        "transport": "swd",
        "port": PORT,
        "command_timeout_seconds": 30,
        "elf_path": "fw.elf",
        "chip_id_map": {"0x000": "AT32F403A"},
        "target_presets": {
            "AT32F403A": "target/at32f403axx.cfg",
            "AT32F435": "target/at32f435xx.cfg",
        },
        "permissions": {
            "read_only": False,
            "allow_flash": True,
            "allow_raw_command": True,  # 代码默认 false（v0.4.12 收紧）；e2e 显式开以测逃生舱透传
            "allow_set_permissions": True,
            "flash_allowed_paths": ["build/"],
            "flash_max_bytes": 1048576,
            "memory_read_max_bytes": 4096,
        },
    }
    cfg["permissions"].update(permissions)
    if _extras:
        cfg.update(_extras)  # 顶层额外键（svd_path 等），测试默认镜像解析用
    with open(os.path.join(work, "ocd.json"), "w", encoding="utf-8") as f:
        json.dump(cfg, f, indent=2)


def banner(title):
    print("\n" + "=" * 4 + " " + title + " " + "=" * 4)


def main():
    work = os.path.join(tempfile.gettempdir(), "ocdup_e2e")
    shutil.rmtree(work, ignore_errors=True)
    os.makedirs(os.path.join(work, "build"))
    os.makedirs(os.path.join(work, "elsewhere"))
    with open(os.path.join(work, "fw.elf"), "wb") as f:
        f.write(build_mini_elf())
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

        banner("disconnect (fake backend is an external listener -> reported, not stopped)")
        print(mcp.call("disconnect", {}))
        print(mcp.call("status", {}))

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

        banner("identify_chip (CPUID + vendor id register; fake backend echoes address as value)")
        print(mcp.call("identify_chip", {}))
        print(mcp.call("identify_chip", {"dbgmcu_address": "hello"}))

        banner("set_target list / unknown name / switch (fake backend is external -> warning)")
        print(mcp.call("set_target", {"name": "list"}))
        print(mcp.call("set_target", {"name": "GD32F303"}))
        print(mcp.call("set_target", {"name": "AT32F403A"}))
        print(mcp.call("set_target", {"name": "at32f403a"}))
        print(mcp.call("show_config"))

        banner("identify_chip again after switch (mismatch gone: preset == running target)")
        print(mcp.call("identify_chip", {}))

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

        banner("write_memory 0x20000000 (allow_memory_write defaults to true)")
        print(mcp.call("write_memory", {"address": "0x20000000", "values": ["0x11111111", "0x22222222"]}))

        banner("flash_erase_sector (allow_flash_erase defaults to false -> BLOCKED)")
        print(mcp.call("flash_erase_sector", {}))

        banner("run_command (escape hatch, echo passthrough; allow_raw_command explicitly on in e2e config)")
        print(mcp.call("run_command", {"command": "mdb 0x40010800 4"}))

        banner("run_command write-class (content policy: program -> BLOCKED, redirect flash_write)")
        print(mcp.call("run_command", {"command": "program build/fw.bin verify"}))

        banner("P1-5: forced connection drop on next read; read-only op replays once after reconnect")
        print(mcp.call("run_command", {"command": "__e2e_arm_drop__"}))
        print(mcp.call("read_memory", {"address": "0x20000000", "count": 1}))

        banner("ocdup rtt CLI (v0.4.19): stream channel 0 via the fake RTT TCP data plane, stop at --lines 2")
        rtt = subprocess.run(
            ["dotnet", DLL, "rtt", "--channel", "0", "--port", "19221", "--lines", "2", "--seconds", "60"],
            cwd=work, env=dict(env), capture_output=True, text=True, encoding="utf-8", timeout=90)
        print("  exit:", rtt.returncode)
        print("  stdout:", rtt.stdout.strip()[:200])
        print("  stderr head:", rtt.stderr.strip().splitlines()[:2] if rtt.stderr.strip() else "(empty)")
        assert rtt.returncode == 0, "rtt CLI should exit 0, got %d (stderr: %s)" % (rtt.returncode, rtt.stderr[-400:])
        assert "hello from fake rtt line 2" in rtt.stdout, "rtt CLI should stream the fake payload to stdout verbatim"
        rtt_cmds = [c for c in backend.commands if c.startswith("rtt")]
        print("  rtt commands:", rtt_cmds)
        assert any(c.startswith("rtt setup 0x20000000 4096") for c in rtt_cmds), "rtt setup with defaults missing"
        assert "rtt start" in rtt_cmds, "rtt start missing"
        assert "rtt server start 19221 0" in rtt_cmds, "rtt server start missing"
        assert "rtt server stop 19221" in rtt_cmds, "rtt server stop (cleanup) missing"
        assert "rtt stop" in rtt_cmds, "rtt stop (cleanup) missing"

        banner("ocdup rtt --info (channel listing only, no data stream)")
        rtt2 = subprocess.run(
            ["dotnet", DLL, "rtt", "--info"],
            cwd=work, env=dict(env), capture_output=True, text=True, encoding="utf-8", timeout=90)
        print("  exit:", rtt2.returncode)
        print("  listing head:", rtt2.stdout.strip().splitlines()[:3])
        assert rtt2.returncode == 0, "rtt --info should exit 0, got %d" % rtt2.returncode
        assert "TERMINAL" in rtt2.stdout and "Down-channels" in rtt2.stdout, "rtt --info should print the channel listing"

        banner("ocdup rtt dead data port (v0.4.27): rtt server accepted but nothing listens on the data port")
        # arm：下一条 rtt server start 只回成功、不真开数据端口（先经 MCP 会话下arm 命令）
        print(mcp.call("run_command", {"command": "__e2e_no_rtt_server__"}))
        rtt3 = subprocess.run(
            ["dotnet", DLL, "rtt", "--port", "19222", "--lines", "1", "--seconds", "30"],
            cwd=work, env=dict(env), capture_output=True, text=True, encoding="utf-8", timeout=90)
        print("  exit:", rtt3.returncode)
        print("  stderr head:", rtt3.stderr.strip().splitlines()[:2])
        # 回归钉死：数据端口连不上曾以未处理 SocketException 崩进程（退出码 3762504530 + 英文堆栈）
        assert rtt3.returncode == 1, "rtt should exit 1 on a dead data port, got %d (stderr: %s)" % (
            rtt3.returncode, rtt3.stderr[-400:])
        assert "cannot use the RTT data port" in rtt3.stderr, "rtt should explain the dead data port on stderr"
        assert "Unhandled exception" not in rtt3.stderr, "rtt must not crash with an unhandled exception"

        banner("ocdup rtt usage error (v0.4.27): exit code family aligned with burn/verify")
        rtt4 = subprocess.run(
            ["dotnet", DLL, "rtt", "--bogus"],
            cwd=work, env=dict(env), capture_output=True, text=True, encoding="utf-8", timeout=60)
        assert rtt4.returncode == 2, "usage errors must exit 2 (burn/verify family), got %d" % rtt4.returncode
        assert "unknown option '--bogus' (ocdup rtt)" in rtt4.stderr, "scanner error should name the command"

        banner("ocdup burn CLI (v0.4.20): same core as flash_write; exit 0 on verified, 1 on verify mismatch, 2 on BLOCKED")
        burn = subprocess.run(
            ["dotnet", DLL, "burn", "--file", "build/fw.bin", "--address", "0x08000000"],
            cwd=work, env=dict(env), capture_output=True, text=True, encoding="utf-8", timeout=90)
        print("  exit:", burn.returncode)
        print("  head:", burn.stdout.strip().splitlines()[-2:] if burn.stdout.strip() else "(no stdout)")
        assert burn.returncode == 0, "burn should exit 0 on verified, got %d (stderr: %s)" % (burn.returncode, burn.stderr[-300:])
        assert "matches the target flash" in burn.stdout, "burn report should carry the verified verdict"

        print(mcp.call("run_command", {"command": "__e2e_arm_fail_verify__"}))
        burn2 = subprocess.run(
            ["dotnet", DLL, "burn", "--file", "build/fw.bin", "--address", "0x08000000"],
            cwd=work, env=dict(env), capture_output=True, text=True, encoding="utf-8", timeout=90)
        print("  armed verify mismatch -> exit:", burn2.returncode)
        assert burn2.returncode == 1, "burn should exit 1 when the read-back does not match, got %d" % burn2.returncode
        assert "VERIFY FAILED" in burn2.stdout, "burn report should carry the VERIFY FAILED verdict"

        burn3 = subprocess.run(
            ["dotnet", DLL, "burn", "--file", "elsewhere/there.bin", "--address", "0x08000000"],
            cwd=work, env=dict(env), capture_output=True, text=True, encoding="utf-8", timeout=90)
        print("  outside whitelist -> exit:", burn3.returncode)
        assert burn3.returncode == 2, "burn should exit 2 on BLOCKED, got %d" % burn3.returncode
        assert "BLOCKED" in burn3.stderr, "burn should print the BLOCKED guidance to stderr"

        banner("ocdup burn exit 3 (v0.4.24): backend/channel failure -> deterministic non-zero")
        # 指向死端口的独立配置 + 无 OCD_BIN：拉后端必败（BackendException → exit 3）
        dead_cfg = {
            "interface_cfg": "interface/cmsis-dap.cfg",
            "target_cfg": "target/at32f435xx.cfg",
            "port": 29179,
            "command_timeout_seconds": 5,
            "permissions": {"read_only": False, "allow_flash": True,
                            "flash_allowed_paths": ["build/"], "flash_max_bytes": 1048576},
        }
        with open(os.path.join(work, "dead.json"), "w", encoding="utf-8") as f:
            json.dump(dead_cfg, f, indent=2)
        env_dead = dict(env)
        env_dead["OCD_CONFIG"] = os.path.join(work, "dead.json")
        burn4 = subprocess.run(
            ["dotnet", DLL, "burn", "--file", "build/fw.bin", "--address", "0x08000000"],
            cwd=work, env=env_dead, capture_output=True, text=True, encoding="utf-8", timeout=90)
        print("  dead port -> exit:", burn4.returncode)
        print("  stderr head:", burn4.stderr.strip().splitlines()[:1] if burn4.stderr.strip() else "(empty)")
        assert burn4.returncode == 3, "burn should exit 3 on backend failure, got %d" % burn4.returncode
        assert "ERROR" in burn4.stderr, "burn should print the actionable backend failure to stderr"

        banner("ocdup verify CLI (v0.4.26): read-only compare, exit 0/1/2")
        v1 = subprocess.run(
            ["dotnet", DLL, "verify", "--file", "build/fw.bin", "--address", "0x08000000"],
            cwd=work, env=dict(env), capture_output=True, text=True, encoding="utf-8", timeout=90)
        print("  exit:", v1.returncode)
        assert v1.returncode == 0, "verify should exit 0 on verified, got %d (stderr: %s)" % (v1.returncode, v1.stderr[-300:])
        assert "matches the target flash" in v1.stdout, "verify report should carry the verified verdict"

        print(mcp.call("run_command", {"command": "__e2e_arm_fail_verify__"}))
        v2 = subprocess.run(
            ["dotnet", DLL, "verify", "--file", "build/fw.bin", "--address", "0x08000000"],
            cwd=work, env=dict(env), capture_output=True, text=True, encoding="utf-8", timeout=90)
        print("  armed mismatch -> exit:", v2.returncode)
        assert v2.returncode == 1, "verify should exit 1 on mismatch, got %d" % v2.returncode
        assert "VERIFY FAILED" in v2.stdout, "verify report should carry the VERIFY FAILED verdict"

        v3 = subprocess.run(
            ["dotnet", DLL, "verify", "--file", "nope.bin", "--address", "0x08000000"],
            cwd=work, env=dict(env), capture_output=True, text=True, encoding="utf-8", timeout=90)
        print("  missing file -> exit:", v3.returncode)
        assert v3.returncode == 2, "verify should exit 2 on missing file, got %d" % v3.returncode
        assert "does not exist" in v3.stderr, "verify should print the path guidance to stderr"

        banner("CRC-algorithm failure (v0.4.29, real-machine 2026-10-13): INCONCLUSIVE, never 'firmware mismatch'")
        for arm, label in (("__e2e_arm_crc_running__", "target not halted"),
                           ("__e2e_arm_crc_halted__", "timed out while waiting for target halted")):
            print(mcp.call("run_command", {"command": arm}))
            crc = mcp.call("verify_image", {"file": "build/fw.bin", "address": "0x08000000"})
            print(crc)
            assert crc.startswith("VERIFY INCONCLUSIVE"), \
                "CRC failure must read as INCONCLUSIVE, got: %s" % crc.splitlines()[0]
            assert "does not match the target flash" not in crc, \
                "CRC failure must NOT claim the firmware differs from the flash"
            assert "halt" in crc, "CRC failure must tell the caller to halt and retry"
            print("  [%s] -> INCONCLUSIVE with halt guidance OK" % label)

        banner("CRC-algorithm failure on the BURN path (v0.4.30): same verdict family as verify_image")
        # v0.4.29 只修了 verify 侧；burn/flash_write 此前把「没验成」误报成 VERIFY FAILED。
        # arm 后 burn 的读回应回 CRC 算法失败原文：exit 1（没验成 ≠ 通过）+ INCONCLUSIVE 措辞。
        print(mcp.call("run_command", {"command": "__e2e_arm_crc_running__"}))
        burn_crc = subprocess.run(
            ["dotnet", DLL, "burn", "--file", "build/fw.bin", "--address", "0x08000000"],
            cwd=work, env=dict(env), capture_output=True, text=True, encoding="utf-8", timeout=90)
        print("  exit:", burn_crc.returncode)
        print("  head:", burn_crc.stdout.strip().splitlines()[-3:] if burn_crc.stdout.strip() else "(no stdout)")
        assert burn_crc.returncode == 1, "burn with an inconclusive read-back must exit 1, got %d" % burn_crc.returncode
        assert "VERIFY INCONCLUSIVE" in burn_crc.stdout, "burn must carry the INCONCLUSIVE verdict"
        assert "does not match the target flash" not in burn_crc.stdout, \
            "burn must NOT claim the firmware differs when the CRC algorithm could not run"
        print("  -> INCONCLUSIVE (not a mismatch) OK")

        banner("SVD layer L2 (v0.4.21): load_svd / read_peripheral_register (decode + reset compare) / write gate")
        MINI_SVD = """<?xml version="1.0"?>
<device>
  <name>TEST32</name>
  <peripherals>
    <peripheral>
      <name>RCC</name>
      <baseAddress>0x40021000</baseAddress>
      <registers>
        <register>
          <name>CR</name>
          <addressOffset>0x00</addressOffset>
          <size>32</size>
          <access>read-write</access>
          <resetValue>0x00000081</resetValue>
          <fields>
            <field><name>HSION</name><bitOffset>0</bitOffset><bitWidth>1</bitWidth></field>
            <field><name>PLLON</name><lsb>24</lsb><msb>24</msb></field>
            <field><name>HPRE</name><bitRange>[7:4]</bitRange></field>
          </fields>
        </register>
        <cluster>
          <name>PWM</name>
          <addressOffset>0x100</addressOffset>
          <register><name>CCR</name><addressOffset>0x0</addressOffset><size>16</size></register>
        </cluster>
      </registers>
    </peripheral>
    <peripheral>
      <name>GPIOA</name>
      <baseAddress>0x40010800</baseAddress>
      <registers>
        <register><name>ODR</name><addressOffset>0x0C</addressOffset><access>read-only</access></register>
      </registers>
    </peripheral>
  </peripherals>
</device>
"""
        with open(os.path.join(work, "test.svd"), "w", encoding="utf-8") as f:
            f.write(MINI_SVD)
        print(mcp.call("load_svd", {"file": "test.svd"}))
        print(mcp.call("load_svd", {"file": "test.svd", "peripheral": "RCC"}))
        r1 = mcp.call("read_peripheral_register", {"name": "rcc.cr"})
        print(r1)
        assert "0x40021000" in r1 and "differs from the reset value" in r1, "CR read should decode and compare reset"
        r2 = mcp.call("read_peripheral_register", {"name": "RCC.PWM.CCR"})
        print(r2)
        assert "0x40021100" in r2, "cluster register should resolve to base+cluster offset"
        assert any(c.startswith("mdh 0x40021100 1") for c in backend.commands), "16-bit register must be read with mdh"
        w1 = mcp.call("write_peripheral_register", {"name": "RCC.CR", "value": "0x1"})
        print(w1)
        assert "wrote RCC.CR" in w1, "write should go through with the memory-write gate open"
        assert any(c.startswith("mww 0x40021000 0x1") for c in backend.commands), "32-bit register must be written with mww"
        w2 = mcp.call("write_peripheral_register", {"name": "GPIOA.ODR", "value": "0x1"})
        print(w2)
        assert "read-only" in w2, "read-only access registers must be refused"
        r3 = mcp.call("read_peripheral_register", {"name": "UART9.CR"})
        print(r3)
        assert "Did you mean" in r3, "unknown peripheral should suggest the closest names"

        banner("halt -> read -> resume (exec control, allow_exec defaults to true)")
        print(mcp.call("halt", {}))
        print(mcp.call("read_memory", {"address": "0x20000000", "count": 2}))
        print(mcp.call("resume", {}))
        print(mcp.call("reset", {"mode": "run"}))
        print(mcp.call("reset", {"mode": "halt"}))

        banner("breakpoints / watchpoints (M-bp, v0.4.30): add/list/remove + gating")
        # 命令面按 2026-10-19 真机核定（AT32F403AVGT7 + atopenocd-py32）：bp/wp 的
        # length 必填恒显式；rwp 只认地址（无 all 形态，全清走「列出逐个删」）
        r = mcp.call("add_breakpoint", {"address": "0x08000100"})
        print(r)
        assert any(c.startswith("bp 0x08000100 2") for c in backend.commands), \
            "add_breakpoint must issue 'bp <addr> <len>' (length is mandatory on the atopenocd fork)"
        print(mcp.call("add_breakpoint", {"address": "0x08000200", "length": 4, "hw": True}))
        assert any(c == "bp 0x08000200 4 hw" for c in backend.commands), "length+hw must both reach the command"
        print(mcp.call("add_breakpoint", {"address": "nope"}))
        print(mcp.call("add_breakpoint", {"address": "0x08000100", "length": 0}))
        print(mcp.call("list_breakpoints", {}))
        assert "bp" in backend.commands, "list_breakpoints must issue bare 'bp'"
        print(mcp.call("remove_breakpoint", {"address": "0x08000100"}))
        assert any(c == "rbp 0x08000100" for c in backend.commands), "remove_breakpoint must issue 'rbp <addr>'"
        print(mcp.call("remove_breakpoint", {}))
        assert "rbp all" in backend.commands, "remove_breakpoint without address must clear all"
        print(mcp.call("add_watchpoint", {"address": "0x20000000", "length": 4, "access": "w"}))
        assert any(c == "wp 0x20000000 4 w" for c in backend.commands), "watchpoint command shape"
        print(mcp.call("add_watchpoint", {"address": "0x20000010", "access": "r"}))
        assert any(c == "wp 0x20000010 4 r" for c in backend.commands), \
            "watchpoint length defaults to 4 and access stays explicit"
        print(mcp.call("add_watchpoint", {"address": "0x20000000", "access": "x"}))
        # 全清观察点：fork 没有 'rwp all'（真机：Invalid command argument）——先 wp 列、
        # 再逐个 rwp。假后端此时回 echo（无地址可解析）→ 如实报告「没有可删的」
        r_wall = mcp.call("remove_watchpoint", {})
        print(r_wall)
        assert "no watchpoints to remove" in r_wall, \
            "an unparseable watchpoint listing must report honestly, not claim success"
        assert "rwp all" not in backend.commands, "no 'rwp all' - the fork rejects it (real-machine finding)"
        # arm 一份真机形态的 wp 列表 → 全清走「列出 + 逐个 rwp」
        print(mcp.call("run_command", {"command": "__e2e_arm_wp_list__"}))
        r_wall2 = mcp.call("remove_watchpoint", {})
        print(r_wall2)
        assert "removed 2 watchpoint(s)" in r_wall2 and "0x20000000" in r_wall2 and "0x20000100" in r_wall2, \
            "remove-all must list then remove each watchpoint by address"
        assert any(c == "rwp 0x20000000" for c in backend.commands) \
            and any(c == "rwp 0x20000100" for c in backend.commands), \
            "each watchpoint is removed by its own address"
        print(mcp.call("remove_watchpoint", {"address": "0x20000000"}))
        assert any(c == "rwp 0x20000000" for c in backend.commands), "remove_watchpoint by address"
        # 闸门：bp/wp 与 halt/resume 同一条 allow_exec（v0.4.30 起裸通道同样分类）
        write_config(work, read_only=False, allow_exec=False)
        print(mcp.call("show_config", {"reload": True}))
        blocked = mcp.call("add_breakpoint", {"address": "0x08000100"})
        print(blocked)
        assert blocked.startswith("BLOCKED:") and "allow_exec" in blocked, "breakpoints must follow allow_exec"
        blocked_wp = mcp.call("add_watchpoint", {"address": "0x20000000"})
        assert blocked_wp.startswith("BLOCKED:") and "allow_exec" in blocked_wp, "watchpoints must follow allow_exec"
        listed = mcp.call("list_breakpoints", {})
        assert not listed.startswith("BLOCKED:"), "list_breakpoints is a pure read - no gate"
        # 裸通道同样分类：allow_raw_command 开着，bp 仍被 allow_exec 拦（RawCommandPolicy v0.4.30）
        raw_bp = mcp.call("run_command", {"command": "bp 0x08000100"})
        print(raw_bp)
        assert raw_bp.startswith("BLOCKED:") and "allow_exec" in raw_bp, "raw 'bp' must be exec-classified too"
        write_config(work, read_only=False)
        print(mcp.call("show_config", {"reload": True}))

        banner("read_memory count=4000 (over memory_read_max_bytes)")
        print(mcp.call("read_memory", {"address": "0x20000000", "count": 4000}))

        banner("read_memory bad address")
        print(mcp.call("read_memory", {"address": "hello"}))

        banner("set_permissions runtime patch (allow_set_permissions=true in e2e config)")
        print(mcp.call("set_permissions", {"readOnly": True}))
        print(mcp.call("flash_write", {"file": "build/fw.bin", "address": "0x08000000"}))
        print(mcp.call("set_permissions", {"readOnly": False}))
        print(mcp.call("show_config"))

        banner("symbol layer L1: list/read/write_variable (mini ELF fixture, elf_path in ocd.json)")
        print(mcp.call("list_variables", {}))
        print(mcp.call("list_variables", {"pattern": "gcount"}))
        print(mcp.call("read_variable", {"name": "gCounter"}))
        print(mcp.call("read_variable", {"name": "missing_var"}))
        print(mcp.call("write_variable", {"name": "gCounter", "value": "0x42"}))

        banner("ocd.json -> read_only=true; show_config(reload) then flash_write")
        write_config(work, read_only=True)
        print(mcp.call("show_config", {"reload": True}))
        print(mcp.call("flash_write", {"file": "build/fw.bin", "address": "0x08000000"}))
        print(mcp.call("write_memory", {"address": "0x20000000", "values": ["0x1"]}))
        print(mcp.call("halt", {}))
        print(mcp.call("set_target", {"name": "AT32F403A"}))
        print(mcp.call("disconnect", {}))

        banner("backend commands observed")
        for c in backend.commands:
            print(" -", c)
    finally:
        mcp.close()

    banner("SVD default image (v0.4.27): load_svd() with no file falls back to \"svd_path\" in ocd.json")
    write_config(work, read_only=False, _extras={"svd_path": "test.svd"})
    mcp4 = Mcp(DLL, work, env)
    try:
        mcp4.initialize()
        print(mcp4.call("load_svd"))
        r4 = mcp4.call("read_peripheral_register", {"name": "RCC.CR"})
        print(r4)
        assert "0x40021000" in r4, "load_svd() without file should load svd_path and resolve RCC.CR"
    finally:
        mcp4.close()

    banner("P0-3 boundary (v0.4.28): allow_exec=false gates halt/resume but NOT disconnect (backend lifecycle)")
    write_config(work, read_only=False, allow_exec=False)
    mcp5 = Mcp(DLL, work, env)
    try:
        mcp5.initialize()
        r5 = mcp5.call("halt", {})
        print(r5)
        assert "BLOCKED" in r5 and "allow_exec" in r5, "halt must stay behind allow_exec"
        d5 = mcp5.call("disconnect", {})
        print(d5)
        assert "BLOCKED" not in d5, "disconnect is backend lifecycle - read_only only, no allow_exec"
        assert "cannot stop it" in d5, "external fake listener: reported, not stopped"
        assert "stays halted" not in d5, "nothing was stopped, so no halted note may be attached"
    finally:
        mcp5.close()

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

