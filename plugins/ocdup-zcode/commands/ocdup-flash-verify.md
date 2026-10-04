---
description: 烧录后串口验证（Ocdup × acccom 会话层协同）：记 acccom 水位 → 烧录 → 让板子跑 → 发指定帧 → 读回本次全部收发
---

按 ocdup-flash skill 的「烧录后串口验证」流程执行。参数（$ARGUMENTS）依次为：
`<镜像路径> [验证帧hex] [COM口]`；验证帧缺省时先问用户要。

1. **记水位**：调用 `mcp__acccom__traffic_log`（limit=1），把返回的 `lastSeq` 明确告诉
   用户（"水位 S=<n>，之后的收发都会被读回复盘"）；
2. **烧录**：按 ocdup-flash 标准流程（connect → flash_info → flash_write，默认
   verify + reset run）；烧完如需用户重新上电/复位，停在这里等确认再继续；
3. **发帧**：`mcp__acccom__open_port`（用参数给的 COM 口，缺省问用户）→
   `mcp__acccom__send_and_wait` 发验证帧（应答超时就报告，不要盲目重发）；
4. **读回**：`mcp__acccom__traffic_log`（sinceSeq=S, limit=200, compact=true），按时间线
   整理本次全部收发（方向 TX/RX、相邻条目时序差、载荷 HEX 优先），结尾给一句协议层
   观察（应答是否配对、有无异常帧）；**用完 close_port**；
5. **纪律**：acccom MCP 未挂载时如实说明、只完成烧录；COM 独占（被占先报告，别硬开）；
   COM9 系统保留不可用；hex 输入宽松、`byteLength` 是真实字节数——细节以 acccom-serial
   skill 为准，不要凭记忆猜。

$ARGUMENTS
