# atopenocd-py32:带 PY32F0 flash 驱动的 OpenOCD 构建记录(v0.4.4)

> 目标:给 Artery fork(ArteryTek/openocd,即 atopenocd 的上游源码)移植 PY32F0
> flash 驱动,让 Ocdup 的后端**一个二进制同时支持 AT32 烧录与 PY32 烧录**。
> 本机产物:`D:\ARMGCCTool\atopenocd-py32\bin\openocd.exe`(2026-10-07 构建,
> gcc 16.2 + jimtcl master + puyaf0x 驱动);配套 DLL:`libhidapi-0.dll`、
> `libusb-1.0.dll`、`zlib1.dll`(与本文件同目录记录于 LOCAL-MACHINE.md)。

## 一、组成

| 件 | 来源 | 说明 |
|---|---|---|
| 基座源码 | github.com/ArteryTek/openocd(shallow clone) | atopenocd 的官方上游,含全部 AT32 驱动与 scripts |
| PY32 驱动 | github.com/l0ud/openocd-puya 的 `src/flash/nor/puyaf0x.c` | 实验性 PY32F0 flash 驱动,寄存器级操作 |
| 移植点 | drivers.c(extern + 数组)、`src/flash/nor/Makefile.am`(NOR_DRIVERS) | 共三处,见仓库 cfgs 头注释 |

驱动要点(来自实测):PY32F0 flash 寄存器与 STM32F0 同源但偏移 +4
(KEYR@0x40022008/SR@0x4002200C/CR@0x40022010);页 128B;编程模型 =
CR.PG 后整页字写、**最后一字前置 PGSTRT**;probe 用 0x40015800 读回
rev_id==0x6000 识别 Puya 家族(PY32 无标准 DBGMCU DEV_ID 编码)。

## 二、构建环境(本机)

- **MSYS2**:`D:\ARMGCCTool\atopenocd-build\msys64`(20260927 base)。
  本机 MSYS2 进程**出站网络被拦**(防火墙拦 msys-2.0.dll 系进程)——所有包用
  Git Bash `curl` 从 TUNA 镜像手动下载 + `pacman -U` 离线安装(若网络正常可
  直接 `pacman -Sy`)。包清单:`make autoconf automake libtool pkg-config texinfo
  mingw-w64-x86_64-gcc mingw-w64-x86_64-libusb mingw-w64-x86_64-hidapi`。
- pacman 镜像:`etc/pacman.d/mirrorlist.*` 指向 TUNA;离线装时先把
  `mingw64.db`/`msys.db` 用 curl 放进 `var/lib/pacman/sync/`,再
  `pacman -Sp --needed <包...>` 生成 URL 清单逐个下载,最后 `pacan -U *.zst`。
  该一次性构建环境里 `SigLevel = Never`(省 PGP 初始化)。

## 三、⚠️ jimtcl 必须换 master(本次最大坑)

fork 钉的 jimtcl(a77ef1a,2021 年代)在 **gcc ≥13/16 下解释器有内存级 bug**:

- `source [<命令替换>]` 形态**静默不执行**(→ `-f`/`script` 加载 cfg 全部失效,
  cfg 里的行要么整文件不跑、要么随机丢参数如 `dap create` 变 `dap crea`);
- 症状与优化级别无关(O2/O0 都有,只是表现不同),**不是我们移植代码引入**
  (基线树同样复现)。

解法:把 `jimtcl/` 换成 **jimtcl master**(codeload tarball),并打 5 个小补丁:

1. `jimtcl/configure.ac`:新建 dummy(`AC_INIT([jimtcl],[0.83])`)——
   openocd 的 configure 只查这个文件存在;
2. openocd `configure.ac`:`jimtcl_config_options` 去掉 `--disable-install-jim`
   (master 已无该选项);master 仍支持 `--with-ext=json`;
3. `src/helper/log.h`、`configuration.h`、`jim-nvp.c` 补 `#include <stdio.h>`
   (新 jim.h 不再捎带 stdio;gcc16 把隐式声明当错误);
4. `src/helper/command.c` 三个包装:`cmd->isproc` →
   `(cmd->flags & JIM_CMD_ISPROC)`(成员变成 flags 位);
5. `src/openocd.c` `jim_expr_command`:删掉 `currentScriptObj` 内省
   (该内部 API 已移除),恒 LOG_WARNING(上游 openocd 主线同款改法)。

链接报 `inflate/deflate` 未定义 → configure 时加 **`LIBS=-lz`**(jimtcl master
的 zlib 扩展),zlib 用 MSYS2 自带。

## 四、构建步骤

```sh
cd /d/ARMGCCTool/atopenocd-build/openocd
./bootstrap nosubmodule                     # jimtcl 需先按上节就位
mkdir -p build && cd build
../configure --enable-cmsis-dap --enable-st-link --disable-jlink \
    --disable-doxygen --disable-werror \
    --prefix=/d/ARMGCCTool/atopenocd-build/install LIBS=-lz
make -j8                                    # 产物: build/src/openocd.exe
```

部署:exe + 上面三个 DLL 拷到 `D:\ARMGCCTool\atopenocd-py32\bin\`;
scripts 沿用旧 atopenocd 的(OCD_SCRIPTS 不变)。客户端 env `OCD_BIN`
指向新 exe(ZCode `config.json` 的 `mcp.servers.ocdup.env` 与 DSH 两个
`cordis.patch.yml` 均已改,重启客户端生效)。

## 五、验证状态(2026-10-07)

- ✅ 新二进制:CMSIS-DAP **v2**(WinUSB bulk,MI_03 接口)连接 DAPLink 成功
  —— 旧版二进制走的是 HID/libusb0 路径;
- ✅ 带中文注释的 target cfg 加载执行正常(jimtcl master 修复);
- ✅ `flash bank ... puyaf0x` 注册、`flash probe` 命令通路正常;
- ✅ **PY32F002A 烧录闭环实测通过(2026-10-07)**:整片 20KB dump → 擦+写回
  (2.3s)→ openocd verify OK → 独立回读 sha256 与备份逐字节一致;页擦单独验证
  (159 页读回全 0xFF)。测试法=先 dump 后写回,不破坏现有固件;
- ⚠️ 已知怪癖:擦/写期间 openocd 报 "Failed to write memory at 0x..." ——Puya
  对 flash AHB 写返回虚假错误(操作实际已生效,README.py32 记录同款现象),
  以 verify_image/回读为准;ocdup flash_write 会因此报失败,须补 verify_image;
- ✅ **AT32 烧录回归实测通过(2026-10-07,F403A 开发板)**:整 bank1 512KB dump →
  flash_write(mass erase + 同镜像写回 + verify OK + reset run)→ 独立回读 sha256
  与备份逐字节一致;F423 走同一 at32f4xx 驱动,attach 路径另见下条;
- ✅ 顺带抓到并修掉一个 cfg 坑:`reset_config connect_assert_srst` **不含**
  RESET_HAS_SRST(缺 srst_only 位),拉复位时报 "BUG: can't assert SRST"、
  deepsleep 芯片救不活——cfgs/target_at32f423_key.cfg 已补
  `srst_only srst_nogate`,clean attach 验证通过(commit 813350e);
- 📖 序列依据:PY32F002A RM V1.4 §4.2.3(本目录 py32f002a_rm.pdf)+ Puya SVD
  (DFP 1.2.6)双重印证寄存器映射与 PGSTRT 编程模型;注意 PY32 家族寄存器布局
  存在差异(2025 fork 用 F1 式偏移在 PY32F030 上验证,与 F002A SVD 不同)。
- ⚠️ 已知坑:本机这根 DAPLink 的 CMSIS-DAP 接口(MI_03)必须保持 **WinUSB**
  驱动绑定;若被 Zadig 改成别的或驱动丢失,v2 路径会 `unable to find a matching
  CMSIS-DAP device`。另外探针被别的 openocd 进程占用时同样报这个错,先杀进程。
