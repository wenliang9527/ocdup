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

| 项 | 内容 | 证据/位置 | 测试 |
| --- | --- | --- | --- |
| R4-1 | 插件两处版本 0.4.6 → 0.4.7 | `plugins/marketplace.json`、`.zcode-plugin/plugin.json` | 原生 CLI 更新后核对已装副本（人工验收）+ README 插件版本史 |
| A-11 | TryParseHex 拒绝带符号 hex（`+5`/`-5` 不再悄悄变 0xFF…FB） | `Parsing.cs` TryParseHex | Parsing Theory（正号/负号/边界值） |
| A-13 | OpenOcdLauncher 把 host/transport 拼进 `-c` TCL 串前转义 | `OpenOcdLauncher.cs:141,148` | BuildArguments 断言引号形态 |
| A-14 | DoctorCli OCD_BIN 探测 Process 补 Dispose | `DoctorCli.cs:162-186` | 行为回归（输出不变） |
| A-15 | ConfigStore 读-改-写（ApplyRuntimePatch/ApplyTargetSwitch）收敛到单锁 | `ConfigStore.cs` | 并发 patch 交错小压测 |
| A-16 | 面板版本源定案：package.json(0.1.0) / PROXY_VERSION(0.3.0) / README 面板史(1.4.x) 三处语义定一案并同源 | `tools/dsh-ocdup-panel/package.json`、`server.js` | test.js client 套件加一致性断言 |
| R4-2 | test.js 无参默认跑全四套件（保留单模式参数） | `test.js:20` | 四套件本尊 |
| e2e | 补 A-3 场景：非对齐 read/write_variable 的命令形态（对齐 mdw + mwb 链）在假后端钉死 | `tools/e2e_fake_backend.py` | 新场景 |
| CI | 第五道门禁：measure_tools.py 加 `--budget`（建议 25,500 B = 现 24,900 + 余量），ci.yml 加一步 | `tools/measure_tools.py`、`.github/workflows/ci.yml` | CI 自身 |

## 批次 B：v0.4.34 —— 契约变更类（**停，等确认再动**）

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
