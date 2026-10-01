---
description: 烧录固件到目标板（标准流程：connect → flash_info → flash_write → 读回验证）
---

按 ocdup-flash skill 的标准烧录流程执行：

1. `connect` 连接后端，把 `backend:` 行（探针识别结果）报给用户；
2. `flash_info` 确认 flash bank 基址与大小；
3. 烧录参数指定的镜像：参数为空时先向用户要文件路径（.elf/.hex/.bin）；
   **`.bin` 必须给 `address`**（未给时向用户确认，AT32 常见 0x08000000）；
4. 保持默认 verify=true、reset=true；成功后用 `read_memory address=0x08000000
   count=16 width=32 format=json` 读向量表（json 免解析），把结果给用户确认
   程序已在跑；需要独立复核时用 `verify_image` 再比一次（只读，不写）；
   运行态读失败 → `halt` → 重读 → `resume`；
5. 遇到 `BLOCKED:` 按 skill 的权限纪律处理（show_config → 改 ocd.json →
   reload），不要绕过；失败时按 skill 的故障升级路径排查，不要盲目重烧。

$ARGUMENTS
