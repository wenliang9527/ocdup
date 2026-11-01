# 配方 4：量产治具烧录（ocdup burn + doctor 预检 + exit code 分流）

> 目标：产线治具/CI 上非交互烧录，按退出码自动分流 PASS/FAIL。
> 与 flash_write MCP 工具同一编排核心（权限闸 + program → 独立读回 → reset 门控），
> 判据零漂移。

## 治具脚本骨架（bash）

```bash
#!/usr/bin/env bash
set -u
export OCD_CONFIG=/fixture/ocd.json           # 治具自己的配置（钉死 cfg，别用 auto；路径不能带空格）

ocdup doctor > precheck.log 2>&1 || { echo "ENV FAIL"; exit 2; }   # 环境预检，FAIL 即退出码 1

ocdup burn --file fixture/fw.bin --address 0x08000000
rc=$?
case $rc in
  0) echo "PASS";;                     # 写入且读回一致（reset run 已执行）
  1) echo "FLASH FAIL";;               # 读回不符 / 写失败——板子返修
  2) echo "SETUP FAIL";;               # 用法/配置/权限（BLOCKED）——治具配置错了
  3) echo "LINK FAIL";;                # 后端/通道故障——探针或 USB 接触问题
esac
```

## 要点

- **判据**：读回校验（verify_image）是唯一权威判据。某些驱动写相吐虚假错误
  （PY32 的假 AHB 错误），文本嗅探不可靠——`burn` 的退出码只认读回。
  若在 MCP 侧编排（不走 CLI）：`flash_write` / `verify_image` / `dump_memory`
  的输出末行有机器可读结论 `OCDUP-RESULT: <op>=<verdict>`（v0.4.34），
  与 CLI 退出码同判据、同词表。
- **不省 verify**：`--no-verify` 存在但产线别用；省下的几秒换不来批次风险。
- **不响应 Ctrl+C**：写相中断 = 半截固件。急停 = 拔治具电源/探针，下一块板
  重新烧会先擦。
- **配置钉死**：治具 ocd.json 显式写 `interface_cfg`（不用 auto）、`read_only: false`、
  `flash_allowed_paths` 只留治具固件目录、`allow_flash_erase: true`（烧前整片擦
  用 flash_write 的自动擦即可，通常不需要单独 erase）。
- **逐板记录**：把 stdout 存档（报告里有字节数、读回结论、reset 结果），
  `ocd.json` + 固件版本号一起入批次记录。

## 批次开始前的自检

金板自检 = 烧一次金板镜像过产线流程 + 抽一块板用 `ocdup verify` 复核（只读比对，
与 verify_image 工具同一判据，exit code 分流）：

```bash
ocdup burn --file fixture/golden.bin --address 0x08000000   # 金板镜像过一遍产线流程
ocdup verify --file fixture/golden.bin --address 0x08000000 # 抽检复核,exit 0=一致
```
