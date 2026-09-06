# DshController 开发文档（v2.0）

> **给谁看**：接手这个项目的人，或新开一轮对话的 AI 助手。
> 先读 §1 三条铁律与 §2 结构，再按 §5 的对照表决定"我要改的东西应该放在哪"。
>
> **文档地图**（按作用分层，一处事实只记一份）：
>
> | 文档 | 作用 |
> |---|---|
> | 根 `AGENTS.md` | AI 入口：铁律速览 + 当前进度一行 + 指路 |
> | 本文件 | 开发规则：分层、门禁、落点对照、发布流程 |
> | `docs/ARCHITECTURE.md` + `docs/adr/` | 架构与决策记录 |
> | `docs/TEST-RESULTS.md` | 测试账与开放票（唯一记录处） |
> | `docs/provider-sync-alignment.md` | 供应商同步格式契约 |
>
> 历史计划/审计/报告已删除，细节看 git 历史；测试证据截图不入库（重跑探针现生成）。

---

## 1. 三条铁律

1. **界面不自己取数**。任何"关于实例的信息"都从实例档案（`ArchiveService` / `ArchiveHub`）读；
   要更新就让分面失效或强制采集。页面里不允许出现扫盘、探端口、起进程、拉网络的代码。
2. **门禁不许绕**。`tools/check-conventions.ps1` 在每次构建前跑，违规即失败。
   需要例外就写进 `tools/conventions-allow.txt` 并注明理由——那是**台账**，只减不增，
   评审时一眼能看到欠了什么债。
3. **改动必须有验证**。纯逻辑写 xUnit（毫秒级）；界面状态写视图模型单测；
   真实环境用 CLI 自检；最后 GUI 冒烟。三者都过才算完成。

---

## 2. 项目结构

```
DshController.slnx
├─ src/DshController.Core         net10.0            纯逻辑：进程、端口、WSL、插件、档案、用量
├─ src/DshController.ViewModels   net10.0            视图模型：界面状态与文案，不认识 WinUI
├─ src/DshController.App          net10.0-windows    WinUI 3 外壳：XAML、控件事件、对话框宿主
└─ tests/DshController.Tests      net10.0            离线单测（引用 Core + ViewModels）
```

**依赖方向（机检强制）**：`App → ViewModels → Core`，`Tests → ViewModels/Core`，
**Core 不得出现任何 UI 引用**（连 `Microsoft.UI.Dispatching` 都不行，用 `IUiDispatcher`）。

主要目录：

| 路径 | 内容 |
|---|---|
| `Core/Archive/` | 实例档案：模型、仓库、服务、调度器、`Collectors/`（6 个采集器） |
| `Core/Usage/` | token 用量：解析、扫描、范围查询、原型备份导入 |
| `Core/Storage/` | `AppPaths`（所有落盘位置单源）、`JsonStore`（原子写 + 损坏兜底） |
| `Core/Diagnostics/` | `AppVersion`、`ErrorReporter`、`LogBuffer` |
| `Core/`（根） | `BackendManager`（状态机：壳+Start/Stop/Ready/Wsl 四分部）、`InstanceManager`、`WslTools/WslLaunch`、插件四件套（`PluginCatalog` 壳+Fetch/Parse/Merge/Cache） |
| `ViewModels/` | `IArchiveFacade`（含 IsRunning 档案透出）、`PluginTargetViewModel`、`UsageViewModel`（+`.Drill` 分部：钻取/实例卡）、设置校验与新建计划 |
| `App/Shell/` | `ArchiveHub`（界面访问档案的唯一入口）、`DialogService`、`PluginOpsService`、`UiDispatcher` |
| `App/Views/` | 新架构页面（`UsageView` = MVVM 样板、`GalleryView` = 设计台） |
| `App/Instances/` | 实例页的 8 个 partial 文件（列表/接线/版本/状态/报告/新建/扫描/设置） |
| `App/CommandLine/` | CLI 与集成自检 |

---

## 3. 数据流：档案是唯一数据源

```
instances.json ─┐
                ├─► ArchiveService ──► archives/<id>.json
采集器 ─────────┘        ▲                    │
 liveness harness        │                    ▼
 plugins usage      RefreshScheduler   ArchiveHub → 视图模型 → 界面
 home wslEnv        （TTL/优先级/并发预算）
```

四条不变量（都有测试锁死，别破坏）：

1. **单飞**：同一 (实例, 分面) 并发只跑一次采集；
2. **失败不覆盖**：`Failed/Skipped` 只更新状态与错误，`data` 与 `lastGoodAt` 保留；
   `Ok/Empty` 才写数据（`Empty` 是"确实没有"的有效结论）；
3. **删除不销毁**：实例从清单消失只写 `retiredAt`，档案文件永久保留；
4. **落盘去抖**：变更打脏标记，统一 flush。

分面与默认刷新间隔（用户可在「应用设置 → 数据刷新」改，0 = 只手动）：

| 分面 | 内容 | 默认 TTL |
|---|---|---|
| `identity` | 名称/环境/端口/HOME | 清单变更即镜像 |
| `liveness` | 运行状态/PID/URL | 前台 2s / 后台 20s |
| `harness` | 实测 harness 版本 | 24h |
| `plugins` | 已装插件与 bundle 归属 | 6h |
| `usage` | token 四桶/按模型/按天/会话 | 运行 30min / 停止 6h |
| `home` | HOME 是否初始化、profile、体积 | 24h |
| `wslEnv` | 发行版是否装/跑、Linux HOME | 1h |

---

## 4. 开发回路

```powershell
# 完整门禁 + 构建（最常用）
powershell -ExecutionPolicy Bypass -File build.ps1 -Debug

# 只跑离线单测（秒级，改逻辑时的主回路）
dotnet test tests\DshController.Tests\DshController.Tests.csproj

# 只跑约定机检
powershell -ExecutionPolicy Bypass -File tools\check-conventions.ps1

# 改样式：--dev 启动后侧边栏出现「设计台」，所有令牌/控件/状态一页可见
src\DshController.App\bin\x64\Debug\net10.0-windows10.0.19041.0\DshController.exe --dev

# 真实环境验证（需要本机装了 dsh）
DshController.exe --check              # dsh 解析 + 端口 + 实例清单
DshController.exe --archive-check      # 各实例各分面的新鲜度/耗时/错误
DshController.exe --selftest-core      # 启停/重启/报告/迁移/克隆（会起真实进程）
DshController.exe --selftest-plugins   # 插件市场核心（全离线）
```

**端口纪律**：任何动态测试只用 ≥3185 端口；3080 是用户在跑的后端，只读探测。

---

## 5. "我要做 X，应该改哪里"

| 需求 | 落点 | 必须一起交付 |
|---|---|---|
| 新增一类实例信息（例如"磁盘剩余空间"） | `Core/Archive/Collectors/XxxCollector.cs` + DTO + `FacetNames` 常量 + `RefreshPolicy` 默认 TTL | 离线单测（用 `FakeCollector` 模式）+ 设计台加一个状态 + `--archive-check` 能看到 |
| 新增页面 | `App/Views/XxxView.xaml(.cs)` + `ViewModels/XxxViewModel.cs`（照抄 `UsageView`）+ MainWindow 导航项 | 视图模型单测（用 `FakeArchiveFacade`）；code-behind 只允许"注入 + 生命周期转发" |
| 改界面样式 | `App/Styles/DshTheme.xaml` | 在设计台里逐项目检（浅色/深色都看） |
| 改后端命令行为 | `Core/BackendManager.cs` / `WslLaunch.cs` | `--selftest-core` 不回归；必要时补 CLI 用例 |
| 加设置项 | `Core/AppSettings.cs`（或 `RefreshPolicy`）+ MainWindow 设置页读写 | 缺字段要能取默认值（向前兼容）；补一条单测 |
| 加落盘文件 | 一律经 `AppPaths` 定路径 + `JsonStore` 读写 | 不允许再出现 `Path.Combine(AppContext.BaseDirectory, ...)` |
| 加对话框 | `DialogService`（不要再 new ContentDialog） | — |
| 插件命令编排 | `PluginOpsService` | 成功后必须让 `plugins` 分面失效 |

**反例（会被机检拦下，也别绕过去）**：在页面里直接 `await InstalledPlugins.ReadWindowsAsync(...)`、
新写一个 `catch { }`、在 App 层 `.GetAwaiter().GetResult()`、把新文件写成 500 行。

---

## 6. 质量门禁

`tools/check-conventions.ps1` 的规则：

| 规则 | 内容 |
|---|---|
| R1 | 单文件 ≤400 行 |
| R2 | `Core` 禁止 `using Microsoft.UI` / `Windows.UI` |
| R3 | 禁止裸 `catch {}`（必须有日志或 `// 理由:` 注释） |
| R4 | `async void` 仅限事件处理器（`*_Click` / `*_Changed` …） |
| R5 | App 层禁止 `.GetAwaiter().GetResult()` / `Task.Wait()` |

例外写在 `tools/conventions-allow.txt`（格式 `<规则ID> <相对路径> # 理由`）。
**规则是"只减不增"**：新代码不许加例外；碰到某个老文件就顺手把它的例外清掉。

当前欠账（2026-09-03）：**R3 裸 catch 已全面清零**（103 处：具体理由注释/日志承接，台账 R3 行=0）；
`BackendManager`/`PluginCatalog`/`PluginMarketPanel` 已拆为 ≤400 行 partial；
R1 例外剩 5 条（MainWindow/HomeManager/InstanceDiscovery/Cli/CoreSelfTest，437~571 行，
用户裁决留台账另开工单）。**新代码仍适用：下次触碰顺手清。**

---

## 7. 发布流程（dev → 正式版）

1. 在 dev 目录跑完整门禁：`build.ps1 -Debug` → `--selftest-core` / `--selftest-plugins` / `--archive-check` → GUI 冒烟。
2. 更新 `CHANGELOG.md`；版本号**只改** `src/DshController.App/DshController.App.csproj` 的 `<Version>`
   （`AppVersion` 与 build.ps1 的 zip 名都从这里取）。
3. `powershell -ExecutionPolicy Bypass -File build.ps1` → 产出 `publish-fixed\` 与同名 zip。
4. 把 `publish-fixed\` 的内容（**去掉** zip、`instances.json`、`cli.log`、`crash.log`、`reports\`）
   覆盖到项目根的 `应用\`；把干净源码（去掉 `bin` `obj` `publish-fixed` `.git`）覆盖到 `源码\`。
5. 提交并打 tag（`v2.0.0` 这类），`应用\版本说明.md` 与 `源码\版本说明.md` 同步更新。

---

## 8. 路线图与已知欠账

**已完成（2026-09 整改轮）**：裸 catch 清账（R3=0）；大文件拆分
（BackendManager→壳+启动/停止/就绪/WSL；PluginCatalog→壳+拉取/解析/合并/缓存；
PluginMarketPanel→壳+数据源与过滤/安装动作/详情弹窗）。

**近期可做（按价值排序）**

1. **诊断页**：把最近的 Warn/Error、各分面采集耗时与失败原因、外部依赖体检做成一页
   （目前这些信息散在控制台与 `--archive-check`）。前置：`ILogSink` 结构化日志。
2. **剩余 R1 五条拆分**：CoreSelfTest(571)/MainWindow(539)/HomeManager(539)/Cli(534)/
   InstanceDiscovery(437)——用户裁决另开工单处理。
3. **令牌文件分层**：`DshTheme.xaml` 拆成 Tokens / Typography / Controls 三份（键名保持不变）。
4. **L10n**：`L10n.T(zh,en)` 轻量方案（原型分支有实现，见 `legacy/usage-prototype-2026-08/L10n.cs`）。
5. **用量跨重启增量**：目前档案存最终聚合结果、TTL 内不重扫；若会话量继续增长再考虑逐文件游标。
6. **口径归一**：`UsageQuery.Summarize` 内联的逐日归并可与 `MergeDaily` 共用一份实现——本轮红队
   minor 决议不动（核心聚合改动收益<风险，176 测试锁死现状；两口径已有对照单测钉一致）。

**明确不做**：改 dsh 本体行为、MSIX 打包、引入数据库、跨机同步、追新 WindowsAppSDK 大版本。

---

## 9. 踩过的坑（别再踩）

| 坑 | 现象 | 结论 |
|---|---|---|
| XAML 编译器静默失败 | C# 有编译错误时，XamlCompiler 报 `WMC9999 未将对象引用设置到对象的实例`，掩盖真正的错误 | 先看 `error CS`，别被 WMC9999 带跑偏 |
| 嵌套工程副本 | 目录里放另一个 csproj，会被 SDK 默认通配符吸进主工程，报几百个"重复定义" | 副本一律放 `legacy/` 且不带 csproj |
| 单飞竞态 | 采集同步完成时，清理早于登记，完成态 Task 永久留在字典 → 数据再也不刷新 | 先登记占位 `TaskCompletionSource`，再启动采集 |
| 分面名大小写 | `ToLowerInvariant()` 后与常量 `"wslEnv"` 比较，永远不等 → 该分面从不自动刷新 | 一切按名比较先过 `FacetNames.Canonical` |
| PowerShell 脚本编码 | 无 BOM 的 UTF-8 .ps1 被 Windows PowerShell 按 ANSI 读，中文字符串打散成语法错误 | 脚本存 UTF-8 **带 BOM** |
| 解决方案平台 | 解决方案级构建传入 `Platform="Any CPU"`，WindowsAppSDK 自包含只认具体架构 | App csproj 用 `TreatAsLocalProperty="Platform;PlatformTarget"` |
| `[ObservableProperty]` 字段写法 | WinUI 3 下报 MVVMTK0045（非 AOT 兼容） | 用 `public partial` 属性写法（需要 `LangVersion latest`） |
| 状态文件跟着 exe 走 | 每个构建产物一套实例清单，"换目录实例全没了" | 状态一律经 `AppPaths` 落用户级目录 |
| 首次进页面不显示版本 | 探测只挂在下拉框 `SelectionChanged`，默认实例不触发 | 取数走统一加载路径，别挂在 UI 事件上 |

---

## 10. 速查

```powershell
# 状态与数据都在这里（不在 exe 旁边）
%LOCALAPPDATA%\DshController\
    instances.json          实例清单
    archives\<id>.json      实例档案（含已删除实例）
    plugin-cache\ plugin-records\ logs\ instances\

# 便携模式：exe 旁放一个空文件 portable.marker，状态就跟着 exe 走
# 导入原型分支的历史用量：
DshController.exe --import-usage <usage-backup.json 路径>
```
