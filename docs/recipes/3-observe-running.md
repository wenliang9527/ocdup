# 配方 3：观测运行中的固件（watch / rtt / halt 现场）

> 目标：固件跑着的时候看它在干什么——变量曲线、printf 日志、卡死现场。
> 三个入口按「看什么」选，互不冲突。

## 1. 变量曲线：`ocdup watch` CLI

```
ocdup watch --name gCounter --name adc_raw --interval 100
ocdup watch --name gCounter --format csv --interval 50 > curve.csv   # 重定向画曲线
```

- 变量名来自 .elf 符号表（`ocd.json` 的 `elf_path`）；`list_variables` 先看有什么；
- `--halt` 每拍暂停固件读一致快照（过 allow_exec 闸）——值抖动大时用；
- 长时运行特意做成 CLI：不占 MCP 串行通道，MCP 会话照常可用。

## 2. printf / RTT 日志流：`ocdup rtt` CLI

固件里已移植 SEGGER RTT（`_SEGGER_RTT` 符号）时：

```
ocdup rtt --info                              # 先看控制块认到没有、有哪些通道
ocdup rtt --address 0x20000400 --lines 200 > rtt.log
```

- `--address` 查固件 .map 文件的 `_SEGGER_RTT` 符号地址（搜不到时必须给精确值）；
- 原始字节直通 stdout（二进制协议也安全），进度走 stderr；
- 数据面是 openocd 的 rtt server 本机 TCP 端口（默认 19221）。

## 3. 卡死现场：halt + 寄存器

```
mcp__ocdup__halt
```

返回的 `pc / lr / sp / xPSR` 就是案发现场：PC 对照 .map 定位函数，xPSR 低 9 位
看是否在 HardFault/NMI。看完 `mcp__ocdup__resume` 放行。

## 4. 运行态读内存（不halt）

flash（0x08000000）运行态直读没问题（真机验证过）；RAM 变量要「一致快照」
用第 1 步的 `--halt` 或第 3 步先 halt。

## 组合用法

MCP 会话里让 AI 烧录/改权限，本机终端同时开着 `ocdup rtt` 看日志——
`burn`/`rtt`/`watch` 都是独立进程，与 MCP 会话共享 ocd.json 但各开各的后端连接
（探针是独占资源：同一时刻只让一边连着）。
