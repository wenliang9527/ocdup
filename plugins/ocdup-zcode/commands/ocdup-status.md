---
description: 查看 Ocdup/OpenOCD 后端状态与权限生效值（不启动任何进程）
---

依次调用 Ocdup MCP 工具 `mcp__ocdup__status` 与 `mcp__ocdup__show_config`，然后：

1. 报告后端是否运行、OpenOCD 版本、target 列表，以及输出里的 `backend:` 行
   （探针自动识别结果，若有）；
2. 报告关键权限的生效值与来源：read_only / allow_flash / allow_flash_erase /
   allow_exec / allow_raw_command / flash_allowed_paths / flash_max_bytes /
   memory_read_max_bytes / dump_allowed_paths / dump_max_bytes；
3. 若后端未运行：提示「插好探针后调用 connect 拉起」——status 只探测，永远
   不主动启动后端；
4. 提醒：6666 端口同一时间只能有一个 OpenOCD 后端，DSH 与 ZCode 共用同一
   后端，不要两边同时烧录。

$ARGUMENTS
