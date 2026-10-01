# dsh-ocdup-panel — Ocdup 调试实时面板  v1.0.0

DeepSeek Harness 的常驻插件:在侧边栏加一个 🎛 面板入口,右侧停靠栏实时显示
**AI 通过 Ocdup MCP 工具与 OpenOCD 的全部交互**(TCL 命令、响应、后端生命周期事件)。
数据层与 [dsh-serial-panel](../../..) 同构——唯一真源是共享 JSONL,UI 可插拔。

## 数据链路

```
ocdup(src/Ocdup,OpenOCD MCP 服务器)
  → TrafficLog 追加 %LOCALAPPDATA%\Ocdup\mcp-traffic.jsonl(逐条落盘,2MB 轮转 .1)
  → 本插件 Host 半区尾随增量(默认 400ms 轮询)
  → 同源私有路由 GET /api/ocdup-log?since=<seq>
  → 浏览器面板自适应增量拉取渲染(有数据 700ms / 空闲 2.5s)
```

## 面板内容

| 列 | 说明 |
|---|---|
| 时间 | 记录时刻(详情区含完整日期) |
| 方向 | `cmd`(琥珀,TCL 命令)/ `rsp`(绿,响应)/ `SYS`(蓝,后端生命周期)/ `err`(红,通道错误) |
| 色点 | 目标 `host:port` 稳定色相,可点击 = 过滤该目标 |
| tool | 发起的 MCP 工具名(`flash_write` / `read_memory` / `backend`…) |
| Δt | 距上一条的间隔(烧录/读内存耗时一目了然) |
| text | 命令或响应文本(详情区全文 + 一键复制) |

工具栏:暂停 / 跟随(翻历史自动挂起+新数据浮出)/ 方向与目标过滤 / 正则搜索 /
清空(两段确认)/ 导出 JSONL;左侧图标带「有新流量」圆点;快捷键
空格=暂停、`/`=搜索、Esc=取消选中。

## 目录职责

**唯一真源 = 本目录(Ocdup 仓库 `tools/dsh-ocdup-panel/`)**,纳入 git 版本管理。
`%USERPROFILE%\.dsh\profiles\` 下的所有副本都只是**部署目标**:

| 路径 | 说明 |
|------|------|
| `~\.dsh\profiles\desktop\dsh-ocdup-panel\` | 部署目标(desktop profile 源目录) |
| `~\.dsh\profiles\desktop\node_modules\dsh-ocdup-panel\` | 部署目标(安装闭包,DSH 实际加载) |
| `~\.dsh\profiles\web\...`(两处) | 浏览器形态 dsh 的部署目标(`deploy.cmd web`) |

改代码只改本目录,然后:

```cmd
deploy.cmd        :: 部署到 desktop profile
deploy.cmd web    :: 追加部署到 web profile
```

再重启 DeepSeek Harness(loader 启动时读取,HMR 默认关闭)。
注册:`cordis.patch.yml` 里的 `- insert: - id: dsh-ocdup-panel`(desktop 与 web 各一条)。

## 测试

Host 半区带一套零依赖行为测试,在插件目录下运行:

```cmd
node test.js core     & node test.js window & node test.js api & node test.js client
```

- core:方向归一化(SYS→sys)/ 半行拼接 / since 切片 / 批量裁剪 / 清空与方法守卫 / 坏行容错
- window:首次挂载只读尾部窗口(大日志不全量读入)
- api:Δt 乱序钳 0、坏时间戳、码点截断、jsonl/csv 导出、help 路由
- client:stub React 冒烟(双注册分支、高亮、正则、徽章共享态)

用环境变量把参数压小是为了快速触发边界(上限 5 条即可验证裁剪)。
`test.js` 只存在于真源目录,不同步到安装闭包。

## 可调环境变量(Host 半区读取)

| 变量 | 默认 | 说明 |
|------|------|------|
| `DSH_OCDUP_PANEL_MAX_ENTRIES` | 5000 | 内存环上限 |
| `DSH_OCDUP_PANEL_POLL_MS` | 400 | 尾随文件轮询间隔 |
| `DSH_OCDUP_PANEL_FIRST_TAIL` | 262144 | 首次只读的尾部字节窗口 |
| `DSH_OCDUP_PANEL_FIRST_LIMIT` | 300 | 首屏/回退返回条数 |
| `DSH_OCDUP_PANEL_MAX_LINE` | 1048576 | 单行残留保护阈值 |
| `DSH_OCDUP_PANEL_LOG` | %LOCALAPPDATA%\Ocdup\mcp-traffic.jsonl | 日志路径覆盖 |

设置方式:`profiles\<profile>\cordis.patch.yml` 里给 dsh-ocdup-panel 条目加
`config` 不生效(插件读的是进程环境),需在启动 DSH 前设置环境变量。

## agent 自发现

```
GET /api/ocdup-log/help          # 接口速见(markdown)
GET /api/ocdup-log?stats=1       # 零载荷存活检查
GET /api/ocdup-log?since=<seq>&compact=1&max=512
```

## 安装 / 卸载

- 安装:`deploy.cmd`(+`web`)+ 两个 profile 的 `cordis.patch.yml` insert 块,重启 DSH。
- 卸载:删除上述部署目录 + patch.yml 里的 insert 块,重启 DSH。
