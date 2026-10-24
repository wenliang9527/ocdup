# ocdup 第四轮优化方案（2026-10-24）

依据：第三轮审计 `docs/audit-2026-10-24.md` 的 A-9..A-16 遗留 + 本方案设计期的两项补充发现（R4-1/R4-2）。
第三轮终态（本方案的新基线）：build 0 警告、单测 398/398、e2e 46 段、面板四套件 234 项
（core 23 / window 5 / api 37 / client 169）、工具描述 24,900 B。

## 设计期补充发现

**R4-1 插件内容已变更但版本未 bump**：第三轮批次 2 修改了
`plugins/ocdup-zcode/skills/ocdup-flash/SKILL.md`（watch/doctor 退出码同步），但
`plugins/marketplace.json` 与 `plugins/ocdup-zcode/.zcode-plugin/plugin.json` 均仍为 0.4.6。
更新链按版本号判断，内容变更不升版 → 原生 CLI（marketplace update → plugins update）拿不到新 SKILL.md。

**R4-2 面板 test.js 默认只跑 core**：`node test.js` 无参时 mode=core（23 项），
四套件需显式 `node test.js core|window|api|client`（CI 是显式四步，本地快验易误判"全绿"）。

## 批次 A：v0.4.33 —— 零契约风险清偿（直接执行）

> **执行记录（2026-10-24）**：已全部落地并过五道门禁（build 0 警告 / 单测 405 / e2e 47 段 /
> 面板四套件 234 / 预算 24,900 ≤ 25,500）。设计期 A-16 证据路径有误，实际版本漂移在
> **ZCode 插件面板**（`plugins/ocdup-zcode/panel/`），DSH 面板（`tools/dsh-ocdup-panel/`，
> 1.5.x）一直维护良好——已按下表「实际做法」执行。

| 项 | 内容 | 证据/位置 | 实际做法（v0.4.33） |
| --- | --- | --- | --- |
| R4-1 | 插件两处版本 0.4.6 → 0.4.7 | `plugins/marketplace.json`、`.zcode-plugin/plugin.json` | 已 bump（原生 CLI 更新后核对已装副本=人工验收） |
| A-11 | TryParseHex 拒绝带符号 hex（`+5`/`-5` 不再悄悄变 0xFF…FB） | `Parsing.cs` TryParseHex | 已修；Parsing Rejects Theory +4 行（`-5`/`+5`/`0x-10`/`-0x10`） |
| A-13 | OpenOcdLauncher 把 host/transport 拼进 `-c` TCL 串前转义 | `OpenOcdLauncher.cs:141,148` | 已修（`Tcl.Quote` 大括号字面量）；LauncherArgumentsTests 断言更新 + 注入值用例 |
| A-14 | DoctorCli OCD_BIN 探测 Process 补 Dispose | `DoctorCli.cs:162-186` | 已修（finally Dispose）；行为回归 |
| A-15 | ConfigStore 读-改-写（ApplyRuntimePatch/ApplyTargetSwitch）收敛到单锁 | `ConfigStore.cs` | 已修（`_writeLock` 包读-改-写，Reload 同锁）；并发补丁不丢更新测试 ×2 |
| A-16 | 面板版本源定案：**ZCode 插件面板** package.json(0.1.0) / server.mjs PROXY_VERSION(0.3.0) 不同源；DSH 面板（1.5.x）无此问题 | `plugins/ocdup-zcode/panel/package.json`、`panel/server/server.mjs:42` | 定案：package.json 为单一源（0.1.0→0.3.0，握手值不变），PROXY_VERSION 改为派生读取——漂移在结构上不可能；`npm test` 47 过 |
| R4-2 | test.js 无参默认跑全四套件（保留单模式参数） | `test.js:20` | 已改（无参=spawnSync 逐模式子进程，exit 码聚合）；四套件本尊验证 |
| e2e | 补 A-3 场景：非对齐 read/write_variable 的命令形态（对齐 mdw + mwb 链）在假后端钉死 | `tools/e2e_fake_backend.py` | 已加（夹具增 gUnaligned@0x20000012；断言 `mdw 0x20000010 2` + 四条 mwb + 摘字节值 0x00142000） |
| CI | 第五道门禁：measure_tools.py 加 `--budget`（建议 25,500 B = 现 24,900 + 余量），ci.yml 加一步 | `tools/measure_tools.py`、`.github/workflows/ci.yml` | 已加（`--budget 25500`，超预算 exit 1）；本地实测 PASS |

另：DSH 面板 package.json 1.5.1 → 1.5.2（test.js 行为变更随包版本记录）。

## 批次 B：v0.4.34 —— 契约变更类（**停，等确认再动**）

> **执行记录（2026-10-24）**：用户已批准，全部落地并过五道门禁（build 0 警告 / 单测 414 /
> e2e 50 段 / 面板四套件 234 / 预算 24,570 ≤ 25,500，且 **≤ 第三轮终值 24,900**）。
> 实际词表：flash-write 五态（OK/VERIFY-FAILED/WRITE-FAILED/INCONCLUSIVE/UNVERIFIED）、
> verify 三态（OK/FAILED/INCONCLUSIVE）、dump 两态（OK/PARTIAL）；e2e 钉死全部七种形态
> + BLOCKED 不带标记的反向断言；插件 bump 0.4.8（SKILL.md 同步标记语法）。

**A-10 失败结论结构化标记**（对外输出契约变更，第三轮已停案）：

- 设计原则 additive：人读文本一字不动，输出**末行**追加机器可读行；
  拟 `OCDUP-RESULT: flash-write=FAILED` / `verify=INCONCLUSIVE` / `dump=OK(errors)`。
- 范围：MCP 侧 flash_write / verify_image / dump_memory（CLI 已有退出码族，不动）。
- 同步：README / SKILL.md / recipes 引用处跟进；e2e 断言末行。
- 消费方：两个面板据此上色/判红——列为后续项，不在本批强做。

**可选：描述瘦身二期**（schema 参数 title 层，预计再 −5%~8%）：title 同为模型可见面，
与 A-10 同批审批。

## 条件批 T：真机验证（等硬件，不阻塞 A/B）

1. **A-9 定案**：真机抓 `reg` 原始输出 → 若确有 10 位 hex 形态（`pc (/32): 0x080002ab62`），
   修 `Parsing.cs` 解析器（先定语义：截断还是拼接错误）并补单测 + 真机边界注释；
   若为注释笔误，只改注释。
2. 真机复核第三轮修复：A-1 watch --halt Ctrl+C 后目标确为 running；
   A-2 traffic 日志 tool 字段五工具各归其名。

## 明确不做（理由在案）

- **A-12 NTFS junction/symlink 白名单解析**：模型侧无创建链接的手段，攻击面仅限
  人写配置侧；ResolveLinkTarget 递归 + 环路防护的复杂度高于收益。维持「记录在案」。
- **ocdup 自更新 / publish.cmd 自动升级**：现有「退客户端 → publish.cmd → 重启」
  是确定性流程；自更新机制反而新增攻击面与失败模式。

## 验收（第四轮收口时）

- 五道门禁全绿（build 0 警告 / 单测 / e2e / 面板四套件 / 字节预算）
- 工具描述 ≤ 24,900 B（不得高于第三轮终值）
- 提交格式 v0.4.33+ 递增；csproj 与插件版本线各批同步
- 文档：README 版本史 + 三条版本线核对清单（csproj / marketplace+plugin / PROXY_VERSION）
- 真机批单独出报告段（A-9 定案证据：原始输出贴档）
