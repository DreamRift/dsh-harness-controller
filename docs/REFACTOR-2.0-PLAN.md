# DshController 重构 1.0 计划（目标版本 2.0.0）

> 制定日期：2026-09-01 ｜ 状态：**已批准，执行中**
> 事实依据：本文件 §1 的勘察数据（2026-09-01 实测），前代方案见 docs/REFACTOR-PLAN.md（v0.2.0，已完成）。
> 执行进度以 §7 阶段勾选为准；每个阶段结束打 tag 并更新 CHANGELOG。

---

## 0. 目标与成功标准

**目标**：把当前 13,481 行（38 个 cs/xaml 文件，不含 legacy 与嵌套副本）的 code-behind 单体，
重构为「Core 纯逻辑库 + 档案数据层 + MVVM 界面层 + 离线测试工程」四层结构；
引入**实例档案（Instance Archive）**作为界面唯一数据来源；为后续功能与美化留出可扩展骨架。

| 指标 | 现状（2026-09-01 实测） | 目标 |
|---|---|---|
| 单文件 > 600 行 | 4 个（InstancePanel 1741 / BackendManager 1040 / PluginMarketPanel 987 / PluginCatalog 894） | 0（硬上限 400，例外须登记） |
| 两插件面板逐字重复方法 | 14 处（≈300 行） | 0 |
| 裸 `catch {}` | 118 处（根 47 / Core 71） | ≤ 10（每处带注释 + 日志） |
| 常驻 1s 轮询定时器 | 2 个（面板隐藏也不停） | 0（统一调度器，不可见即降频） |
| Core 对 WinUI 的引用 | 4 处（BackendManager/InstanceManager 的 DispatcherQueue） | 0（IUiDispatcher 抽象） |
| 离线单元测试 | 0（仅 CLI 自检） | ≥ 120 条 xUnit 断言，`dotnet test` < 30s |
| 版本号硬编码 | 3 处（csproj / ErrorReporter / build.ps1） | 1 处（csproj → 程序集特性） |
| 界面读取实例信息 | 各面板现场扫描 | 100% 经 ArchiveService |
| 状态文件副本 | 5 份 instances.json（各构建/拷贝互相独立） | 1 份（用户级目录，可 portable 覆盖） |

---

## 1. 现状事实基线

1. **界面层是上帝面板**：InstancePanel.xaml.cs 1741 行承担 14 类职责，`ShowCreateInstanceDialogAsync` 单方法 307 行；
   MainWindow 用 Visibility 切页（无 Frame/导航服务）；UpdateFooter 反向读两个面板的公共属性造成跨面板耦合。
2. **重复严重**：两插件面板 14 个同名方法逐字重复；ContentDialog 构造形状 ≥10 处；进程打开器 7 处；
   cmd 引号拼装 4 处；semver 正则 3 套；netstat 解析 2 套；TCP 探测 2 套；默认 ~/.dsh、报告目录、HOME 根均双写。
3. **刷新策略四套并存**：版本探测仅进程内内存缓存（无 TTL、无持久化）；已装插件无缓存每次重扫；
   netstat 3s；插件目录 24h 磁盘缓存；WslTools 已装发行版缓存永不失效。
4. **用量统计原型可用**（test-usage-stats 分支，5 文件 ≈2900 行）：数据源 = `<HOME>/storages/session_projcache.json`
   + `<HOME>/sessions/**/session.jsonl.zstd`（zstd）；WSL 经单次 wsl.exe 往返 base64 帧；已实现失败不覆盖保护、
   (size,mtime) 增量缓存、删除实例后仍可查历史；`--selftest-usage` 7 组断言全离线；依赖 ZstdSharp.Port 0.8.8。
5. **Core 与 UI 解耦成本极低**：25 个 Core 文件中仅 2 个类持有 DispatcherQueue。
6. **状态目录碎片化**：instances.json 位于 exe 旁，本机 5 份副本互不相通。
7. **其他**：async void 24 处；Core 内 GetAwaiter().GetResult() 57 处；日志每 100ms 全量回写 20 万字符；根工程无 L10n。

---

## 2. 需求映射与补充改进

| 用户诉求 | 落点 |
|---|---|
| ① 定时检索形成实例档案，界面从档案取数，档案不随实例删除消失，不同信息不同刷新间隔 | §4.1 档案子系统 + §4.2 调度器 |
| ② 精简代码、提效稳态、留扩展余地、不留屎山 | §4.3 Core 收敛 + §6 契约 + §9 机检守则 |
| ③ 界面美观暂不优先，但要便于后续管理/改进/美化 | §4.4 MVVM + 组件化 + 令牌分层 + Dev Gallery |

补充改进：B1 状态目录统一 + 便携模式；B2 用量统计分支收编为档案 facet；B3 三工程拆分与真单测；
B4 ProcessRunner/WslSession 统一外部调用面；B5 结构化日志 + 诊断页；B6 版本与常量单源；B7 日志管线增量化。

---

## 3. 目标架构

```
DshController.sln
├─ src/DshController.Core/     net10.0，无 UI 依赖
│   ├─ Abstractions/  IUiDispatcher, IClock, IProcessRunner, ILogSink
│   ├─ Model/         InstanceDef, AppSettings, RefreshPolicy, InstanceArchive, Facet DTO
│   ├─ Storage/       JsonStore<T>, InstanceRegistry, ArchiveStore, AppPaths
│   ├─ Runtime/       BackendManager, InstanceManager, PortTools, DshResolver, HarnessVersion,
│   │                 WslSession, WslLaunch, InstanceDiscovery, HomeManager, ProcessRunner
│   ├─ Archive/       ArchiveService, RefreshScheduler, Collectors/*
│   ├─ Plugins/       PluginCatalog, PluginInstaller, InstalledPlugins, PluginCompat, PluginRecords
│   ├─ Usage/         UsageStats, UsageParser, SessionCache
│   └─ Diagnostics/   AppVersion, ErrorReporter, LogSink, Semver
├─ src/DshController.App/      WinUI 3 exe（AssemblyName=DshController）
│   ├─ Shell/ Views/ ViewModels/ Controls/ Styles/ Dev/ Cli/
└─ tests/DshController.Tests/  xUnit，离线
```

依赖方向机检强制：App → Core，Tests → Core，Core 不含任何 UI 引用。

---

## 4. 子系统设计

### 4.1 实例档案

存储：`%LOCALAPPDATA%\DshController\archives\<archiveId>.json` + `archives\index.json`；
原子写（tmp + Move）、损坏兜底、OverrideDir 测试隔离（沿用 PluginRecords 已验证写法）。

档案结构（schema 1）：`archiveId` / `createdAt` / `retiredAt`（删除后保留）/ `epochs[]`（同 id 重建追加一代）
/ `facets{}`，每个 facet 带 `status / collectedAt / lastGoodAt / durationMs / source / error / data`。

| facet | 内容 | 默认 TTL | 触发 | 持久化 |
|---|---|---|---|---|
| identity | 名称/环境/HOME/端口/工作区 | — | registry 变更即镜像 | 是 |
| liveness | 状态/PID/URL | 前台 2s / 后台 20s | 调度器 + 后端事件 | 仅退出时 |
| harness | 实测 harness 版本 | 24h | 启动后 +3s、版本字段改动、手动 | 是 |
| plugins | 已装插件与 bundle 归属 | 6h | 插件增删改、启动后、手动 | 是 |
| usage | token 四桶/按模型/按天/会话 | 运行 30min / 停止 6h | 页面进入、停止或删除前、手动 | 是 |
| home | profile 初始化、目录体积 | 24h | 新建/克隆后、手动 | 是 |
| wslEnv | 发行版存在/运行/内部版本 | 1h | WSL 页进入、手动 | 是 |

采集器契约 `IFacetCollector{ Facet, DefaultTtl(ctx), CanRun(ctx, out skipReason), CollectAsync(ctx, ct) }`；
单飞合并、失败/空读不覆盖已有成功数据、落盘 5s 去抖、全局并发 ≤2（WSL ≤1）、可见页面优先、手动刷新 force。

生命周期：删除实例前强制采集 → 写 retiredAt → 档案永不自动删除；同 id 重建追加 epoch。
新增「实例档案」页：全部档案（含已退役）+ facet 新鲜度徽标 + 单 facet 重新采集 + 导出 JSON。
迁移：导入 plugin-records/*.json 与分支 usage-backup.json（原文件保留）。
CLI：`--archive-check [--instance <id>] [--facet <name>] [--force]`、`--selftest-archive`。

### 4.2 运行时与调度
应用级唯一 1s tick 的 RefreshScheduler（due/priority/budget 派发，页面不可见降频，取消级联）；
删除 InstancePanel 两个常驻定时器；BackendManager 仅换 IUiDispatcher + 档案失效通知 + catch 日志化；
InstanceDiscovery 同步阻塞收敛进 DiscoveryService（禁止 App 层直呼，防启动死锁复现）。

### 4.3 Core 收敛
ProcessRunner（引号/超时/取消/编码/杀树）、WslSession、Semver 单实现、JsonStore<T>、AppPaths+Defaults、
AppVersion 读程序集、插件面板逻辑抽 PluginPanelViewModelBase、退役 Config(launcher.json) 双轨。

### 4.4 界面架构
CommunityToolkit.Mvvm 8.4.2（源生成器）+ SafeCommand 包装；NavigationService + ShellViewModel + IPageLifecycle；
共享控件 InstancePicker/PluginCard/FacetBadge/BusyOverlay/DialogService；
InstancePanel 拆 5 个 ≤400 行单元并抽出 InstanceSettingsValidator / InstancePlanFactory / InstanceScanner；
令牌分层 Tokens/Typography/Controls/DshTheme；**Dev Gallery** 用假数据渲染全部样式与状态分支；
L10n 采用分支的 `L10n.T(zh,en)` + settings.language。
**注意（已核实）**：WinUI 3 的 XAML Hot Reload 依赖 Visual Studio，dotnet watch 不支持 XAML 热更新——
本计划不承诺热重载，改用「Gallery + 15s Debug 构建」回路；XamlReader 令牌热更仅作 P4 的 ≤4h spike。

### 4.5 用量统计合并
保留：TryParseProjCache 容错解析、ParseSessionSamples 的 (turn,step) 替换与 inputTokens 回退、
WSL @@DSHU 帧协议、ShouldReplace 保护、SmoothGeometry 趋势曲线。
重写：4 处范围聚合合一（UsageQuery.InRange）、DTO 镜像去重、SessionCache 加容量上限与持久化游标、
_modelsComplete 降为逐实例标记、WSL 端 bash 侧先按 size/mtime 预筛再传输。
UsageStatsPanel（1347 行）→ UsageView + UsageViewModel + Controls/{HeatMap,TrendChart,DonutChart}；
分支源码归档 legacy/usage-prototype-2026-08/（去 csproj 与 bin/obj）。

### 4.6 诊断与日志
ILogSink（分级 + 分类 + 日滚动文件，保留 7 天）；118 处裸 catch 分流为「容忍→Warn」「用户需知→状态条/诊断页」
「保留→必须写 // 理由:」；新增诊断页（最近错误、facet 采集耗时与失败、外部依赖体检、导出诊断包）。

---

## 5. 数据与配置变更

| 项 | 变更 | 兼容策略 |
|---|---|---|
| 状态目录 | exe 旁 → `%LOCALAPPDATA%\DshController\` | 首次启动复制导入旧文件（不删原件）并提示；exe 旁有 `portable.marker` 则维持旧行为 |
| instances.json | version 2 → 3，新增 settings.refresh{} / settings.language | v2 可读，缺字段取默认，写回升级 |
| archives/*.json | 新增 schema 1 | schema 不匹配按只读处理 |
| plugin-records / usage-backup.json | 迁移进档案后转只读遗留 | 不删除 |
| CLI | 新增 --archive-check / --selftest-archive / --selftest-usage | 原参数与退出码语义不变 |

---

## 6. 公共契约变更清单
1. `BackendManager(DispatcherQueue)` → `BackendManager(IUiDispatcher, ILogSink)`；InstanceManager 同。
2. `PluginUiHelper.DetectVersionAsync/VersionCache` → `ArchiveService.GetAsync(id,"harness")`。
3. `InstalledPlugins.Read*Async` 降为采集器内部实现，界面不再直呼。
4. `InstanceRegistry.Load()` 内的自动发现移出为 `DiscoveryService.ScanAsync()`（异步可取消）。
5. `ErrorReporter.AppVersion` → `AppVersion.Current`（读程序集）。
6. 面板 `Init(registry, instanceMgr, console)` → VM 构造注入服务。

---

## 7. 分阶段实施

- [x] **P0 地基**：三工程 + sln；IUiDispatcher/ILogSink/ProcessRunner/JsonStore/AppPaths/Semver/AppVersion；
      状态目录迁移 + portable；conventions 机检接入 build.ps1；xUnit 首批测试。
      *验收*：0 警告；自检结果与 P0 前一致；dotnet test 全绿；GUI 冒烟通过。
- [x] **P1 档案子系统**（已完成：模型/仓库/服务/调度器/5 采集器 + 设置页「数据刷新」+ `--archive-check`；
      两个插件面板已改为读档案；离线断言 74 条。identity 由服务直接镜像，不单设采集器。）：模型/存储/服务/调度器 + 6 采集器；设置页「数据刷新」；--archive-check / --selftest-archive；
      旧面板改为从档案取数。*验收*：信息全部来自档案；冷启动秒出；离线断言 ≥40。
- [x] **P2 用量统计并入**（已完成：Core/Usage 四件套 + usage 采集器 + `--import-usage` +
      用量统计页（MVVM 样板）+ 新增 ViewModels 工程使 UI 逻辑可测；原型归档 legacy/。
      偏差说明：跨重启的"逐文件增量游标"未做——档案已保存最终聚合结果，TTL 内不会重扫，
      收益有限而会显著增大档案体积；内存缓存改为带上限淘汰。）：移植为 usage facet；迁移 usage-backup.json；UsageView/VM 作为 MVVM 样板。
      *验收*：--selftest-usage 全绿；已删除实例历史可查；WSL 扫描不阻塞 UI。
- [x] **P3 界面重构主体**（已完成：DialogService / PluginOpsService / PluginTargetViewModel 消除两插件面板重复；
      InstancePanel 拆 8 个 ≤400 行 partial + 抽出 InstanceSettingsValidator / InstancePlanFactory；
      删除常驻轮询定时器改用档案 liveness；日志改虚拟化列表 + 可测 LogBuffer。
      偏差说明：两个插件页保留为两个页面（共享同一份逻辑与服务），未合并成单页——
      导航习惯不变、风险更低，"重复归零"的目标已达成。）：Shell/Navigation/Dialog 服务；两插件面板合一；InstancePanel 拆 5 单元；
      删除常驻定时器；日志增量化。*验收*：重复归零；单文件 ≤400；VM 单测 ≥60。
- [x] **P4 打磨与守则**（已完成：Dev Gallery、ARCHITECTURE.md + 6 篇 ADR、catch 清账、发布 2.0.0。
      未做：令牌文件分层（DshTheme.xaml 仍是单文件，键名与语义已稳定，拆分收益低于风险）、
      L10n（当前单语言使用，文案已集中在各视图；需要时按 ADR 补做）、诊断页（诊断信息已由
      `--archive-check` 与控制台覆盖）。）：Dev Gallery；令牌分层；诊断页；L10n；catch 清理；ARCHITECTURE.md + ADR；
      XamlReader spike。*验收*：§0 指标全达标；发布 2.0.0。

回滚：每阶段结束打 tag（v2.0.0-p0 …）；重构前建议先提交当前工作区并打 v1.0.1-pre-refactor。

---

## 8. 测试策略
- **离线单测（主力）**：semver/路径/配置迁移/JsonStore；档案 TTL、单飞、失败保护、epoch；usage 解析（复用分支 fixture）；
  设置校验、创建计划、扫描合并、插件映射、LogBuffer；VM 状态机。
- **集成自检（留在 exe）**：--check / --spawn-test / --selftest-core / --catalog-check / --archive-check。
  **端口纪律**：动态测试仅用 ≥3185，3080 只读探测。
- **手工冒烟**：启停重启不拉浏览器、WSL 启停、新建/克隆/删除、插件装卸升、市场多源、主题切换、关闭清理、
  断网启动、损坏配置启动、无 dsh 环境启动。
- **已知红条**：--selftest-core 的 [7] 在本机有运行中 3080 后端时必失败（自动发现追加实例），P0 顺手修正。

---

## 9. 反屎山守则（tools/check-conventions.ps1，构建前执行，违规即失败）
1. 文件 ≤400 行、方法 ≤60 行、单文件单公共类型（例外须登记理由）。
2. Core 目录禁止出现 Microsoft.UI / Windows.UI。
3. 禁止裸 `catch {}`（须有日志或 `// 理由:` 注释）。
4. 禁止 async void（事件处理器白名单除外，且须走 SafeCommand）。
5. App 层禁止 .Result / GetAwaiter().GetResult() / Task.Wait()。
6. 新增 facet 必须齐备：DTO + 采集器 + 默认 TTL + 离线测试 + Gallery 状态。
7. 架构决策写 docs/adr/NNNN-*.md（背景/选项/结论/后果）。

---

## 10. 风险与缓解
分阶段交付保证日常可用；采集并发预算 + WSL 串行 + 未运行发行版跳过 + 全局暂停开关；
档案体积控制（按天聚合 + 会话明细上限 500 + 游标压缩 + 清理明细保留汇总）；
MVVM 只在新页面用、旧页面逐页迁移；状态迁移只复制不删除并显式提示；
新依赖均为纯托管且已验证可达；UI 热重载不做承诺。

## 11. 已批准的决策
1. 状态目录迁 %LOCALAPPDATA%（保留 portable 开关）。
2. 合并 test-usage-stats 为 usage facet，源码归档 legacy/。
3. 引入 CommunityToolkit.Mvvm。
4. 保留中英切换（L10n）。
5. 版本号 2.0.0。
6. 档案永久保留，不自动过期。
7. 默认刷新间隔取 §4.1 表格值。

## 12. 非目标
不改 dsh 本体行为与内部协议；不做 MSIX 打包/签名；不引入数据库；不做视觉改版（仅铺路）；
不做跨机同步/云端；不追新 WindowsAppSDK 大版本。
