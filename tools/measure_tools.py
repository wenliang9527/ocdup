"""一次性度量:列出 MCP tools 并统计 JSON 字节数。

用法:
  python tools/measure_tools.py               # 打印工具数/总字节/逐工具明细
  python tools/measure_tools.py --budget 25500  # 门禁模式:总字节超预算则 exit 1(CI 第五道门禁)
"""
import json, subprocess, sys, os

DLL = os.path.join(os.path.dirname(__file__), "..", "src", "Ocdup", "bin", "Debug", "net8.0", "Ocdup.dll")

budget = None
args = sys.argv[1:]
if "--budget" in args:
    i = args.index("--budget")
    if i + 1 >= len(args):
        print("usage: measure_tools.py [--budget N]"); sys.exit(2)
    budget = int(args[i + 1])

proc = subprocess.Popen(
    ["dotnet", os.path.abspath(DLL)],
    stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
    cwd=os.path.join(os.path.dirname(__file__), ".."),
)

def send(obj):
    proc.stdin.write((json.dumps(obj) + "\n").encode())
    proc.stdin.flush()

def recv():
    while True:
        line = proc.stdout.readline()
        if not line:
            raise RuntimeError("server exited")
        line = line.strip()
        if line.startswith(b"{"):
            return json.loads(line)

send({"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {
    "protocolVersion": "2024-11-05",
    "capabilities": {}, "clientInfo": {"name": "measure", "version": "0"}}})
recv()
send({"jsonrpc": "2.0", "method": "notifications/initialized"})
send({"jsonrpc": "2.0", "id": 2, "method": "tools/list"})
resp = recv()

tools = resp["result"]["tools"]
total = sum(len(json.dumps(t, ensure_ascii=False).encode()) for t in tools)
per_tool = sorted(((len(json.dumps(t, ensure_ascii=False).encode()), t["name"]) for t in tools), reverse=True)
print(f"tool count: {len(tools)}")
print(f"total bytes (name+description+inputSchema): {total}")
for size, name in per_tool:
    print(f"  {size:6d}  {name}")
proc.kill()

if budget is not None:
    if total > budget:
        print(f"FAIL: {total} B exceeds the {budget} B description budget - slim before adding words")
        sys.exit(1)
    print(f"PASS: {total} B <= budget {budget} B")
