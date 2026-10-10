# 配方 1：新板首次烧录闭环

> 目标：一块（可能陌生的）板子，从插上探针到固件运行 + 读回验证，一条龙走完。
> 全程 MCP 工具；每一步的"看什么"都写明了。

## 0. 环境预检（换机/第一次用时）

```
ocdup doctor
```

五项全 [OK]（后端端口 [WARN] 属冷启动正常）再往下走；哪项 [FAIL] 按提示修。

## 1. 连接（自动拉后端 + 认芯片）

```
mcp__ocdup__connect
```

看三样东西：
- `backend:` 行 —— auto 模式认出的探针（kind → 接口脚本）；
- `chip identity:` —— CPUID 内核 + chip_id_map 认出的家族；
- **`target mismatch` 行** —— 认出的家族与 ocd.json 的 target_cfg 不符时会出现，
  此时先 `mcp__ocdup__set_target <家族名>`（自动重启后端切 cfg），再 `flash_info`。

## 2. 确认 flash 布局

```
mcp__ocdup__flash_info
```

确认 bank 基址（Cortex-M 通常是 0x08000000）与容量。auto-size 配置（FLASH_SIZE=0）
必须 probe 过才有真实容量——本工具已自动逐 bank probe；容量报 0 说明 SWD/供电有问题，
先别烧。

## 3. 烧录（读回校验是唯一判据）

```
mcp__ocdup__flash_write(file="build/fw.hex")
# 裸 .bin 必须显式给地址：
mcp__ocdup__flash_write(file="build/fw.bin", address="0x08000000")
```

结论三段式，只有 `programmed + verified` 才会 reset run：
- `VERIFY FAILED` —— 写声称成功但读回不符，最危险，查 target_cfg 是否配错芯片；
- `WRITE FAILED` —— 写步骤已报错，查探针连接/供电/容量。

## 4. 运行态验证

```
mcp__ocdup__read_memory(address="0x08000000", count=4)
mcp__ocdup__verify_image(file="build/fw.hex")
```

向量表第 0/1 字应是初始 SP / Reset_Handler 地址（对照 .map 文件）；verify OK
= 板上固件与镜像逐字节一致。

## 常见坑

| 症状 | 原因与解法 |
|---|---|
| 「连上就自己断开」 | target_cfg 与实际板子不配（DEV_ID 读 0 → 驱动断言崩溃）。用 set_target 切对 cfg |
| flash 容量报 0 | probe 没到：查 SWD 排线/供电；auto-size cfg 靠 probe 拿真实容量 |
| 深睡板连不上 | 板级 cfg 需要 `connect_assert_srst`（见 cfgs/ 里的钥匙板 cfg 注释） |
