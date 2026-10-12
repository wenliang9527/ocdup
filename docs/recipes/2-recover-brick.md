# 配方 2：救板（连不上 / 睡死 / 烧坏固件）

> 目标：板子「变砖」——固件跑飞、进了深睡、或上次烧录半途而废——把它救回来。
> 按顺序试，每一步只放宽一点点。

## 1. 先看通道还在不在

```
mcp__ocdup__status
```

- 有版本 + target 列表 → 通道是好的，跳到第 4 步；
- `nothing is listening` → 后端没跑，走第 2 步；
- 有监听但 TCL 查询失败 → openocd 半死，`mcp__ocdup__disconnect` 后重 `connect`。

## 2. 重拉后端（配置层排障）

```
ocdup doctor          # 先确认 OCD_BIN / OCD_SCRIPTS / 探针都在
mcp__ocdup__connect
```

启动失败时错误文本自带 `--- recent openocd output ---`（最后 200 行）——
大多数答案就在里面：探针被占用（LIBUSB_ERROR）、interface cfg 不匹配、
target cfg 断言崩溃（芯片不对号）。

## 3. 深睡/睡死板：复位姿势

睡死（deepsleep/pre-sleep）的板子 SWD 会掉线。板级 cfg 需要：

```
reset_config srst_only srst_nogate connect_assert_srst
```

参考 `cfgs/target_at32f423_key.cfg`（头部注释就是当时的排障记录）。
没有 srst 布线的板子：按住板上的 RESET/BOOT0 再 `connect`（在复位释放前
抢进 SWD），或者直接断电重插后立刻 connect。

## 4. 执行控制救场

```
mcp__ocdup__halt          # 冻住跑飞的固件（顺带回读 PC/LR/SP/xPSR——跑飞在哪一步定位）
mcp__ocdup__reset mode="halt"    # 复位并停在入口
```

`halt` 返回的核心寄存器现场：PC 看跑飞位置、LR 看调用来源、xPSR 看是否在
HardFault。之后 `reset mode="run"` 放它重新跑。

## 5. 固件真坏了：整片重烧

```
mcp__ocdup__flash_erase_sector(bank=0, first=0, last=-1)   # 需在 ocd.json 开 allow_flash_erase
mcp__ocdup__flash_write(file="build/fw.hex")
```

擦不掉 = flash 驱动没起来（target_cfg 不对号，回第 2 步）；烧完好后
`verify_image` 确认。**选项保护**：allow_flash_erase 默认 false 是故意的——
救板时显式打开，平时关着。

## 底线

- 任何时候 `backend_log` 都能看 openocd 自己的日志（TCL-RPC 响应里看不到的
  `Info:`/`Error:`/target 事件都在那）。
- 探针被别的软件占着：`disconnect` 释放自己拉起的实例；外部实例杀不动会
  如实说明——去把那个软件停掉。若此前 halt 过，disconnect 的结论会提示目标
  处于 halted（后端停了没人 resume；要保持运行，先 `resume` / `reset run` 再断开）。
