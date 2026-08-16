# DshController v0.2.0 重构优化方案

> 制定日期：2026-08-16 ｜ 依据：docs/RESEARCH-NOTES.md（调研事实）
> 状态：**实施完成**——P1–P8 已勾选，GitHub Release 发布待执行；测试记录见 docs/TEST-RESULTS.md

---

## 1. 背景与目标

### 1.1 用户需求（7 条原始诉求）

| # | 需求 | 方案落点 |
|---|---|---|
| R1 | 重构优化应用 | §4 架构重构（单文件→分层多文件） |
| R2 | 增强稳定性、优化代码效率 | §2.2 缺陷清单 D1–D16 → §4.3 并发模型重设计 |
| R3 | 启动失败时生成详细报错文档，位置可自定义 | §5.1 错误报告系统 |
| R4 | 重启功能：只重启 harness 后端，不拉起新浏览器界面 | §5.2 重启语义（suppressBrowser 硬性覆盖） |
| R5 | UI 更美观，DeepSeek Harness 同款简约风格 | §5.3 设计系统（真实令牌映射，非近似色） |
| R6 | 尽可能使用 WinUI 3 | §3 已验证真 WinUI 3 全链路可行，直接采用 |
| R7 | 应用图标 = DeepSeek Harness 同款 | ✅ 已完成（§5.4） |

### 1.2 非目标（明确不做）

- 不引入 MVVM 框架（Prism 等）——code-behind 足够，保持轻量
- 不改 `dsh` 本体行为、不解析其内部协议（维持黑盒：进程 + 端口 + stdout）
- 不做 MSIX 打包/签名（保持 unpackaged 免安装）
- 不做多语言资源化（保持中文 UI，与 v0.1.0 一致）
- 不提交 git（改动留在工作区，由用户审阅后自行提交）

---

## 2. 现状分析：v0.1.0 缺陷清单

> 行号基于当前工作区 `DshController.cs`（1244 行）。每条注明：问题 → 影响 → 修复策略。

### 2.1 稳定性缺陷

| ID | 位置 | 问题 | 影响 | v0.2.0 修复 |
|---|---|---|---|---|
| D1 | Config.Load L109-130 | 手写 JSON 转义/反转义，历史上曾把反斜杠翻倍污染 launcher.json，现靠正则收敛补救 | 根因未除，任何新字段都要手写解析 | 改用 `System.Text.Json`（.NET 内置）序列化；Load 时对旧污染文件一次性净化迁移 |
| D2 | StopBackend L1069-1116 | `EnsurePortFree` 同步执行：Kill(3s)+taskkill(10s)+WaitForPort(15s) 全在 **UI 线程** | 停止时界面最长冻结 ~28s | 全链路 async/await |
| D3 | RefreshStateUi/ApplyState | 外部后端运行时每秒 `FindListenerPid`（起 netstat 子进程，UI 线程同步等 3s） | 每秒一次 UI 冻结 + 进程风暴 | 异步 + 结果缓存 3s；PID 查询移出刷新热路径 |
| D4 | OnChildOutput L1134-1145 | `_announcedUrl` 后台线程写、UI 线程读，无同步 | 理论可见性问题 | 状态统一收敛到 UI 线程（DispatcherQueue） |
| D5 | WaitReadyLoop vs StopBackend | 启动等待期间可点停止，但 `_starting` 期间 Stop 按钮被禁用（见 D9），停止与就绪回调存在竞态 | 停在就绪瞬间可能仍触发自动开浏览器 | BackendManager 状态机串行化所有转移 |
| D6 | OnChildExited L1147-1163 | Exited 回调（线程池）与 UI 关闭竞态；`Ui()` 先查 `_closing` 再 BeginInvoke 存在 TOCTOU | 关窗瞬间可能抛"已释放"异常 | DispatcherQueue.TryEnqueue（自带 disposed 安全语义）+ 管道在窗口关闭时先取消 |
| D7 | KillTree L391-397 | `Process.GetProcessById` 成功即判存活，PID 复用时误判 | 极低概率误报"未停止" | 带时间窗的重试验证 + 端口探测为准 |
| D8 | 全局 | 未处理异常仅写 `crash.log`（纯堆栈字符串），无上下文 | 崩溃难排查 | §5.1 崩溃报告（同错误报告模板） |
| D9 | StartBackendAsync L970-1041 | `await Task.Run(WaitReadyLoop)` 在 try 内 → `finally` 要等 180s 就绪循环结束才清 `_starting` | **整个启动等待期（最长 3 分钟）停止按钮被禁用**，无法中途取消 | 就绪等待独立化 + 可取消（CancellationToken），按钮状态由状态机驱动 |
| D10 | OnFormClosing L1194-1211 | 退出路径 `TryReadSettings()` 失败会弹"端口无效"警告框阻止收尾 | 关窗被弹窗打断 | 退出时静默降级：仅保存有效字段，不弹窗 |

### 2.2 效率问题

| ID | 位置 | 问题 | 修复 |
|---|---|---|---|
| E1 | AppendLog L1213-1226 | 每行日志一次 BeginInvoke + SelectionStart/ScrollToCaret | dsh 启动输出密集时 UI 消息洪泛 | 后台 `Channel<string>` 聚合，UI 侧 100ms 批量刷新；环形缓冲 2000 行 |
| E2 | Probe L298-311 | 同步 TcpClient + WaitOne(1200) 阻塞式探测 | `ConnectAsync` + `CancellationToken` 超时，完全不占线程 |
| E3 | FindListenerPid | 每次调用新建 Process + 新 Regex；netstat 全表输出读全量 | 静态编译 Regex + 结果缓存；异步读取保持 |
| E4 | DescribeDsh | 每次状态刷新（1s）重新全盘解析 dsh 路径（多次 File.Exists + PATH 扫描） | 解析结果缓存，配置变更时才失效 |
| E5 | MakeIcon L866-890 / SetToolTip L1238-1243 | `Icon.FromHandle` 不释放 GDI；每个 ToolTip new 不 Dispose | 改用内嵌 .ico 资源一次加载；WinUI ToolTip 走 XAML 资源树自动管理 |

### 2.3 功能缺口

| ID | 缺口 | 对应需求 |
|---|---|---|
| F1 | 启动失败只有一行日志，无诊断产物、无历史 | R3 |
| F2 | 无重启功能（要手动 停止→启动 两步，且启动还会自动开浏览器开新标签页） | R4 |
| F3 | UI 为默认 WinForms 观感（GroupBox/灰色按钮），与 DSH web 风格割裂；无明暗主题 | R5 |
| F4 | 自绘蓝色圆圈图标与 DSH 品牌不符 | R7 |

---

## 3. 技术选型（已验证）

**选型：真 WinUI 3**（`Microsoft.WindowsAppSDK 1.5.240607001`），理由与依据：

1. 用户明确"尽可能 WinUI 3"；本机已实测 restore/build/run/publish 全通过（RESEARCH-NOTES §3）。
2. WinUI 3 直接提供：Mica 材质背景、明暗主题自动跟随系统、现代控件（Expander/Switch/InfoBar）、
   圆角与悬停动效——与 DSH web 的简约气质天然契合，无需手绘模仿。
3. 免安装部署：`WindowsPackageType=None` + `WindowsAppSDKSelfContained=true`，目标机零前置
   （仅需 Win10 17763+，且装 .NET 6 Desktop Runtime；`-Portable` 选项可全自包含免运行时）。
4. 回退方案（仅当后续执行遇阻）：WPF + 手写 DSH 主题。当前无迹象需要回退。

**体积代价说明**：36 KB → ~120 MB（publish 目录，常规框架依赖 .NET）。这是自包含 WASDK 的固有成本，
build.ps1 输出 zip 便于分发；README 注明。

---

## 4. 新架构设计

### 4.1 项目结构

```
DshController/
├── DshController.csproj        # WinUI3 配置（§3）；ApplicationIcon=Assets/app.ico
├── app.manifest                # dpiAware/PerMonitorV2, longPathAware
├── Program.cs                  # 自定义入口（GenerateProgramFile=false）：
│                               #   CLI 参数 → Cli.Run；否则 Application.Start + 全局异常钩子
├── App.xaml / App.xaml.cs      # 资源字典合并、主题注入、崩溃兜底
├── MainWindow.xaml / .cs       # 唯一窗口：布局 + 交互（code-behind，无 MVVM）
├── Styles/
│   └── DshTheme.xaml           # §5.3 设计令牌 → XAML 资源（Light/Dark 两套 + 控件样式）
├── Core/                       # 纯逻辑层，无 UI 依赖（可单测）
│   ├── Config.cs               # launcher.json：System.Text.Json 读写 + 旧文件净化迁移
│   ├── DshResolver.cs          # dsh/node 解析（沿用 4 级回退）+ 结果缓存
│   ├── PortTools.cs            # ProbeAsync/FindListenerPidAsync(缓存)/KillTreeAsync/EnsurePortFreeAsync/WaitForPortAsync
│   ├── BackendManager.cs       # 状态机 + 进程生命周期 + 输出捕获（Channel）
│   ├── ErrorReporter.cs        # §5.1 Markdown 报告生成/落盘/兜底
│   └── Cli.cs                  # --check/--spawn-test/--spawn-test-node/--version 移植（AttachConsole）
├── Assets/
│   ├── app.ico / whale.svg     # 已生成（§5.4）
│   └── icon-*.png              # 中间产物（保留 256 用于文档，其余构建时随用）
├── test-server.js              # 保留不动（--spawn-test-node 依赖）
├── build.ps1                   # 重写：dotnet publish（-Clean/-Portable/-Debug）
└── docs/                       # 本方案 + 调研笔记
```

### 4.2 模块职责与依赖方向

```
Program ──► Cli（无窗口模式）
   │
   ▼
MainWindow ──► BackendManager ──► DshResolver / PortTools / Config
   │                │
   │                └──► ErrorReporter（失败上下文 → Markdown）
   └──► Config / ErrorReporter（目录设置、报告打开）
```

- `BackendManager`：唯一持有 `Process`；对 UI 只暴露事件（Log/StateChanged）与
  `StartAsync/StopAsync/RestartAsync/ProbeNowAsync`。
- UI 不直接碰进程/端口 API（旧版 MainForm 直连 Backend 静态类是耦合根源）。

### 4.3 并发模型（R2 的核心修复）

```
                ┌─ DispatcherQueueTimer(1s) ─► ProbeAsync ─┐
                │   （SemaphoreSlim=1 防重入；窗口隐藏时暂停） │
stdout/stderr ──┤                                            ▼
   │           │                                    StateChanged(UI线程)
   ▼           │
Channel<string> ─► UI 聚合泵（100ms 批量 Append + 环形 2000 行）      ← 修 E1
   │
   └─ Process.Exited ─► 收敛到状态机（CancellationTokenSource 同步取消等待循环）← 修 D5/D6/D9
```

状态机（枚举驱动按钮可用性，杜绝散落的 `_starting` 布尔组合）：

```
Stopped ──Start──► Starting ──probe OK──► Running
   ▲                  ││                    ││
   │                  │└─失败/超时/早退─► Stopped(+报告 §5.1)
   └────Stop(杀树+等端口释放)◄────┘│
                      │              └─Restart(外部需确认)─► Stopping ──► Starting(noBrowser) ──► Running
```

**Restart 不变量（R4 核心承诺）**：`StartOptions.SuppressAutoOpen=true` 在 Restart 路径硬编码，
无论 `autoOpenBrowser` 配置为何，重启过程绝不调用浏览器；完成后日志提示"浏览器页面刷新即可重连"。

---

## 5. 功能设计

### 5.1 启动失败错误报告（R3）

**触发点**（6 类，全部走同一模板）：
1. dsh 解析失败（附完整搜索路径清单）
2. `Process.Start` 抛异常（Win32Exception 等，附工作目录/命令行）
3. 进程早退（附退出码 + 已捕获输出）
4. 就绪超时 180s（附全程输出转录）
5. 停止后端口无法释放
6. 全局未处理异常（UI/后台线程，App 层钩子）

**配置**：`launcher.json` 新增 `errorReportDir`，UI 设置区含目录框 + 浏览按钮；
默认 `%USERPROFILE%\Documents\DshController\error-reports`；写入失败兜底 exe 目录。

**报告模板**（`DshController-fail_20260816_213005.md`，崩溃为 `-crash_`）：

```
# DSH 后端启动失败报告
> 生成时间 / 应用版本 / 失败类型 / 摘要（一句话）
## 1 环境        OS/.NET/PATH 中 node/npm/DSH_HOME 存在性/磁盘剩余
## 2 dsh 解析    4 级回退逐条结果（最终命令或全失败清单）
## 3 本次配置    host/port/workspace/dshCommand/autoOpen…（脱敏后全文）
## 4 端口与进程  探测结果/监听 PID/是否外部实例
## 5 输出转录    子进程 stdout/stderr（时间戳，环形缓冲全量）
## 6 异常详情    ToString() 全文 + InnerException 链
## 7 排障建议    按失败类型映射（如"未找到 dsh → npm i -g @deepseek-ai/dsh"）
```

**交互**：写入成功 → 日志区高亮路径 + `ContentDialog`（打开报告 / 打开目录 / 关闭）。

### 5.2 重启功能（R4）

| 当前状态 | 重启按钮行为 |
|---|---|
| Running(本程序启动) | 立即：Stop（杀树+等端口释放）→ Start(**SuppressAutoOpen=true**) |
| Running(外部进程) | 确认对话框（显示外部 PID）→ 同上 |
| Starting/Stopping | 按钮禁用 |
| Stopped | 按钮禁用（此时用"启动"） |

日志全程记录"⟳ 重启：停止后端…端口已释放…重新启动（不打开浏览器）…"。

### 5.3 UI 设计系统（R5，令牌真值见 RESEARCH-NOTES §4）

**资源字典** `Styles/DshTheme.xaml`（Light/Dark 各一套，运行时按主题切换 + 头部手动覆盖）：

| 令牌 | Light | Dark |
|---|---|---|
| BgBase | `#F9FAFB` | `#151517` |
| BgCard | `#FFFFFF` | `#1E1E22`（950 提亮一档） |
| LabelPrimary | `#0F1115` | `#F0F1F3` |
| LabelSecondary | `#61666B` | `#9BA0A8` |
| LabelTertiary | `#81858C` | `#6F737A` |
| Border | `#0F1115 @ 10%` | `#FFFFFF @ 10%` |
| BrandPrimary | `#3964FE` | `#3964FE`（深色微调亮 `#4D74FF` hover） |
| StateRun/StateStarting/StateStop | `#2FB380` / `#E8A33D` / `#81858C` | 同 |

**布局**（Mica 背景 + 自绘标题栏留白，宽 660 默认）：

```
┌──────────────────────────────────────────────────────┐
│ 🐋 DSH Harness 控制器                     [主题▾] – □ ✕│
│ ┌──────────────────────────────────────────────────┐ │
│ │ ● 运行中 · 本程序启动                     PID 18672│ │ ← 状态卡片（圆点+文字）
│ │ http://127.0.0.1:3080/                     [复制]│ │   + 地址可点击
│ └──────────────────────────────────────────────────┘ │
│  [▶ 启动后端]  [⟳ 重启后端]  [⏹ 停止]  [🌐 打开界面]   │ ← 主操作行
│ ▸ 设置                                       Expander│
│ │   主机 [127.0.0.1]   端口 [3080]                   │
│ │   工作目录 [..............................] [浏览] │
│ │   错误报告目录 [..........................] [浏览] │
│ │   ── 自动打开浏览器(启动时)      [开/关]            │ ← Switch
│ │   ── 退出时停止本程序启动的后端    [开/关]           │
│ ▸ 后端日志                            [清空] [自动滚动]│
│ ┌──────────────────────────────────────────────────┐ │
│ │ 21:30:01  已启动 dsh web（PID 18672）  Consolas    │ │
│ └──────────────────────────────────────────────────┘ │
│  dsh: %APPDATA%\npm\dsh.cmd · v0.2.0                 │ ← 页脚（tertiary 色）
└──────────────────────────────────────────────────────┘
```

- 主按钮 = 品牌蓝填充白字（8px 圆角），其余 = Card 底描边幽灵样式；
  禁用态 40% 透明度；悬停过渡 0.1s（DSH `--ds-ease-in-out` 节奏）。
- 主题切换：跟随系统（默认）/ 浅色 / 深色，三态循环按钮，持久化到 `theme` 字段。
- 窗口图标 `AppWindow.SetIcon(Assets/app.ico)`；exe/任务栏图标走 csproj `ApplicationIcon`。

### 5.4 应用图标（R7）✅ 已完成

`Assets/app.ico`（9 尺寸 PNG-in-ICO）+ `whale.svg` 源件，构图使用品牌蓝 `#3964FE`
圆角瓷砖 + 白色鲸鱼；任务栏小尺寸不再显示为白色色块。构建后从 exe 提取图标复核像素。

---

## 6. 兼容与迁移

| 项 | 策略 |
|---|---|
| launcher.json | 字段只增不改（+`errorReportDir`/`theme`）；读取容忍未知字段；首次加载检测到多重反斜杠污染即净化重存（修 D1 根因收尾） |
| CLI | `--check/--spawn-test/--spawn-test-node/--noredirect/--version` 行为与退出码语义不变，仍写 `cli.log` |
| test-server.js / LICENSE / .gitignore 语义 | 保留；.gitignore 追加 `bin/ obj/ publish/ *.user` |
| 旧 DshController.cs | 移入 `legacy/` 留档（不删除，便于 diff 审阅）；根目录旧 exe 清理 |
| 版本 | 0.1.0 → **0.2.0**（破坏性变化：产物从单 exe 变为目录） |

---

## 7. 构建与发布

`build.ps1` 重写（PowerShell 5 兼容、ASCII 注释风格延续）：

```
powershell -ExecutionPolicy Bypass -File build.ps1            # Release publish → publish\
powershell ... -File build.ps1 -Debug                         # Debug 构建（快速迭代）
powershell ... -File build.ps1 -Clean                         # 清 bin/obj/publish
powershell ... -File build.ps1 -Portable                      # .NET 亦自包含（免运行时，~250MB）
```

自动步骤：`dotnet restore` → `dotnet publish -p:Platform=x64` → 打 zip → 打印自检命令提示。
脚本首检 dotnet SDK ≥6.0 与 NuGet 连通性，失败给出可读指引。

---

## 8. 测试计划

| # | 场景 | 方法 | 通过标准 |
|---|---|---|---|
| T1 | CLI 自检 | `--version` / `--check` / `--spawn-test-node --port 3081` | 退出码 0，cli.log 完整 |
| T2 | 真实 dsh 管线 | `--spawn-test --port 3081`（**避开 3080**） | 启动→就绪→杀树→端口释放全绿 |
| T3 | 失败报告 | ① dshCommand 指向不存在路径 ② 占死端口后启动 ③ 超时（dshCommand=node 空转脚本） | 3 类均生成 Markdown，章节齐全，目录遵循配置 |
| T4 | 重启语义 | GUI 手动 + 日志断言 | 全程无浏览器进程拉起；后端 PID 更新；autoOpenBrowser=true 时亦然 |
| T5 | UI 目检 | 运行后 PowerShell 截屏（浅/深主题、各状态） | 与 §5.3 线框一致；图标/鲸鱼正确渲染 |
| T6 | 回归 | 外部后端识别（用 3080 现存实例只读探测）/ 退出清理 / 配置往返保存 | 不错杀外部进程；配置无转义污染 |
| T7 | 稳定性 soak | spawn-test 连跑 3 次 | 无句柄泄漏迹象（进程数回落正常） |

---

## 9. 风险与缓解

| 风险 | 概率 | 缓解 |
|---|---|---|
| WASDK 自包含在个别老 Win10 上的兼容问题 | 低 | README 注明 17763+；保留 v0.1.0 legacy 路径作应急 |
| 体积膨胀引发用户不满 | 中 | 计划内说明 + `-Portable` 说明 + zip 分发 |
| XAML 编译在 CI/其他机器的差异 | 低 | build.ps1 锁 SDK 版本提示；本机已验证 |
| netstat/taskkill 被策略限制 | 低 | 原有超时保护保留；失败纳入错误报告 |
| 图标小尺寸(16px)细节糊 | 中 | T5 目检；不满意则 16/20/48 单独手调比例重生成 |
| 测试误伤用户正在跑的 3080 后端 | — | **硬约束**：一切动态测试仅用 ≥3081 端口；3080 只读探测 |

---

## 10. 执行步骤（批准后按序执行，完成勾选）

- [x] P1 骨架：csproj/app.manifest/Program/App/MainWindow 空窗口 + 主题资源字典 + 图标接入，`dotnet build` 通过
- [x] P2 Core：Config（JSON+迁移）、DshResolver（+缓存）、PortTools（全异步）
- [x] P3 Core：BackendManager 状态机 + 输出 Channel + Restart(SuppressAutoOpen)
- [x] P4 Core：ErrorReporter（6 触发点接线 + 兜底）+ Cli 移植
- [x] P5 UI：MainWindow 完整布局（状态卡/操作行/Expander 设置/日志/页脚）+ 明暗主题切换
- [x] P6 文档与构建：build.ps1、README、CHANGELOG(0.2.0)、.gitignore、legacy/ 归档
- [x] P7 测试：T1–T7 全量执行并记录结果到 docs/TEST-RESULTS.md
- [x] P8 收尾：清理中间产物（多余 icon PNG）、核对完成标准

## 11. 完成标准

1. §1.1 七条需求全部落地且经 §8 对应测试验证；
2. 旧缺陷 D1–D10、E1–E5 在新代码中不复存在（逐条可指出对应修复位置）；
3. `build.ps1` 一键产出可双击运行的 `publish\DshController.exe`（含新图标）；
4. launcher.json 旧格式无缝读取，无破坏；
5. 全程未影响用户 3080 端口的运行中后端。
