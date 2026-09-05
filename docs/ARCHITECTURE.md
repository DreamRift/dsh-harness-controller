# DshController 架构说明（v2.0）

> 本文描述重构 2.0 之后的结构与约束。改动前先读这一页；决策的来龙去脉见 `docs/adr/`。

## 1. 分层与工程

```
DshController.slnx
├─ src/DshController.Core         net10.0     纯逻辑，零 UI 依赖
├─ src/DshController.ViewModels   net10.0     视图模型，只依赖 Core 与 MVVM Toolkit
├─ src/DshController.App          net10.0-windows  WinUI 3，产出 DshController.exe
└─ tests/DshController.Tests      net10.0     离线单测（引用 Core + ViewModels）
```

**依赖方向**（机检强制，见 `tools/check-conventions.ps1`）：

```
App ──► ViewModels ──► Core
Tests ──► ViewModels / Core
Core ──► 不依赖任何 UI（连 Microsoft.UI.Dispatching 都不行，改用 IUiDispatcher）
```

判断新代码该放哪一层：
- 会起进程、读文件、发网络请求、解析数据 → **Core**
- 决定"界面显示什么、按钮能不能点、文案怎么写" → **ViewModels**
- 只有 XAML、控件事件、对话框宿主 → **App**

## 2. 数据流：实例档案是界面唯一数据源

```
实例清单(instances.json) ─┐
                          ├─► ArchiveService ──► 档案文件 archives/<id>.json
采集器(Collectors) ───────┘        ▲                    │
   liveness / harness /            │                    ▼
   plugins / usage /               │            ArchiveHub(App) ──► 视图模型 ──► 界面
   home / wslEnv                   │
              RefreshScheduler ────┘（按各分面 TTL、可见优先、并发预算）
```

规则：
1. **界面不自己扫盘/探测**，只问档案要现成数据（永远秒回）；需要更新时由调度器或手动刷新触发采集。
2. **每个分面各自计时**：`RefreshPolicy` 给出 TTL，互不牵连（版本 24h、插件 6h、用量 30min/6h…）。
3. **失败不覆盖成功数据**：Failed/Skipped 只更新状态与错误；Ok/Empty 才写数据。
4. **删除实例不销毁档案**：只写 `retiredAt`，历史永久可查（用量、插件、代际定义）。
5. **单飞**：同一 (实例, 分面) 并发只跑一次采集。

新增一类实例信息 = 新增一个采集器 + 一个 DTO + 一条 TTL 默认值 + 一组离线测试 + Gallery 状态，
不需要改档案模型、仓库、调度器或界面取数路径。

## 3. 落盘位置（全部由 `AppPaths` 单源提供）

| 内容 | 位置 |
|---|---|
| 实例清单 | `%LOCALAPPDATA%\DshController\instances.json` |
| 实例档案 | `…\DshController\archives\<id>.json` |
| 插件市场缓存 / 市场安装记录 | `…\DshController\plugin-cache` / `plugin-records` |
| 新建实例的 DSH_HOME 根 | `…\DshController\instances\` |
| 失败报告 | 我的文档\DshController\error-reports（可配置） |

exe 旁放 `portable.marker` 即切换为便携模式（状态跟着 exe 走）。首次运行会把 exe 旁的旧
`instances.json` 复制进用户级目录（只复制不删除）。

## 4. 线程与生命周期

- Core 通过 `IUiDispatcher` 回调 UI；CLI/测试用 `InlineUiDispatcher` 就地执行。
- 全应用只有两个常驻循环：`RefreshScheduler`（1s 评估）与日志批量泵（100ms）。
  面板不再各自持有轮询定时器。
- 采集全部可取消，WSL 类采集串行（`wsl.exe` 往返昂贵）。

## 5. 界面结构

- `MainWindow` = 外壳：导航、主题、共享控制台、页脚、关闭清理。
- 页面：Windows/WSL 实例、插件市场、插件管理、用量统计、应用设置、设计台（--dev）。
- 新页面一律 **View + ViewModel + x:Bind**（样板见 `Views/UsageView` 与 `UsageViewModel`）。
- 共享服务：`DialogService`（确认/提示/自定义对话框）、`PluginOpsService`（插件命令编排）、
  `ArchiveHub`（界面访问档案的唯一入口，实现 `IArchiveFacade`）。
- 实例页因体量拆成 8 个 partial 文件（列表/接线/版本/状态/报告/新建/扫描/设置），单文件 ≤400 行。

## 6. 质量门禁

`build.ps1` 在构建前依次执行：
1. `tools/check-conventions.ps1`：文件 ≤400 行、Core 禁 UI 引用、裸 catch 必须写理由、
   async void 限事件处理器、App 层禁同步阻塞；例外登记在 `tools/conventions-allow.txt`，**只减不增**。
2. `dotnet test`：离线断言（当前 153 条，< 0.3s）。

需要真实环境的验证走 CLI：`--check` / `--selftest-core` / `--selftest-plugins` /
`--catalog-check` / `--archive-check` / `--import-usage`。

## 7. UI 迭代方式（重要）

WinUI 3 的 XAML 热重载依赖 Visual Studio，命令行构建下没有。因此：
- 改样式 → 看 **设计台**（`--dev` 启动，侧边栏"设计台（dev）"）：所有令牌、控件样式、
  状态分支用假数据摆在一页，不必制造真实运行条件；
- 改逻辑 → 写 **视图模型单测**（毫秒级）；
- 两者都过了再跑 GUI 冒烟。Debug 构建约 15 秒。
