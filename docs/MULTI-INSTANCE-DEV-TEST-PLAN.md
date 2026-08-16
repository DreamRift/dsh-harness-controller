# DshController v0.3.0 多实例功能 —— 开发与测试计划

> 编制日期：2026-08-16
> 关联文档：《多开隔离-调研与实施方案.md》（`C:\Users\<user>\Documents\AI\deepseek Harness\多开隔离-调研与实施方案.md`，下称"方案文档"）
> 代码基线：DshController v0.2.0（WinUI 3 / .NET 6 / `net6.0-windows10.0.19041.0`，Release 未发布）
> 本文所有"文件:行"引用均基于当前工作区代码，改动前请以实际代码为准。

---

## 0. 目标与范围

### 0.1 功能目标

在 DshController 中实现 **多实例管理**：可创建/克隆/编辑/删除多个 DSH 实例（每个实例 = 独立 `$DSH_HOME` + 端口 + workspace），并在 UI 与 CLI 中**对指定实例操作**（启动 / 重启 / 停止 / 打开界面 / 查看状态与日志），实例之间完全隔离。

### 0.2 本期范围（P0/P1）

| 级别 | 内容 |
|---|---|
| P0（必须） | instances.json 数据模型与 v1 迁移；DSH_HOME 注入；实例选择器 + 对指定实例启动/重启/停止/打开界面/日志；实例 CRUD；双实例并行 |
| P1（应当） | 创建向导（空白/标准克隆）、端口分配器、实例文件锁、CLI `--instance` 定向操作、`--selftest-core` 多实例用例、错误报告带实例标识 |
| P2（可选，本期不做） | 托盘驻留、批量操作 UI、配额/用量监控、链接克隆、实例备份 zip |

### 0.3 设计前提（已源码验证，详见方案文档 §4）

1. `DSH_HOME` 环境变量 = 官方隔离入口（`dsh-home-paths`：显式配置 > `$DSH_HOME` > `~/.dsh`）。
2. `web` profile 首次启动自动 `initProfile`，`profiles/node_modules` 为自动维护的 junction fallback → 新 HOME 免 pnpm、磁盘成本≈0。
3. web 参数：`--host`（仅 127.0.0.1）/ `--port`（0=OS 分配）/ `--trusted-host`（可重复）。
4. **BackendManager 已按 `Config` 参数化**（`StartAsync(cfg, …)`/`StopAsync(cfg, …)`/`RestartAsync(cfg)`）——多实例化的核心工作量在"数据模型 + 注入 + UI 路由"，状态机本身几乎不动。

---

## 1. 现状代码盘点与改造点

| 文件 | 现状（关键成员） | 多实例改造点 |
|---|---|---|
| `Core/Config.cs` | 单例配置；`FilePath` 固定 `launcher.json`；`SanitizePath` 净化 | 新增 `Home`（DSH_HOME 路径）、`TrustedHosts` 字段（默认空串=不注入，向后兼容）；`Config` 保留为"实例配置"；全局设置拆出 `AppSettings` |
| `Core/BackendManager.cs` | 状态机 `Stopped/Starting/Running/Stopping/Restarting`；`StartCoreAsync` L212-231 组装 `ProcessStartInfo`；事件已实例级 | ① `StartCoreAsync` L222-231：`psi.EnvironmentVariables["DSH_HOME"] = cfg.Home`（非空时）；命令追加 `--trusted-host`（cfg.TrustedHosts 非空时）；② 其余逻辑（就绪循环/停止/端口探测）已按 cfg 参数化，**不改** |
| `Core/PortTools.cs` | 纯静态；`ProbeAsync`/`FindListenerPidAsync`（3s 缓存，按端口）/`KillTreeAsync`/`EnsurePortFreeAsync` | **不改**，多实例不同端口天然不冲突 |
| `Core/DshResolver.cs` | 4 级解析 + 缓存（键=cfg.DshCommand） | **不改**，与 HOME 无关，全局共享 |
| `Core/Cli.cs` | `--check` / `--spawn-test [--port]` / `--spawn-test-node` / `--selftest-core` / `--version` | ① `--check` 增加实例清单输出（每实例：id/端口/状态/PID）；② `--spawn-test` 增加 `--home <dir>`（验证 DSH_HOME 注入）；③ 新增 `--instance <id> start|stop|restart|status`（定向操作，无 GUI） |
| `Core/CoreSelfTest.cs` | 6 组无头自检（[5] 迁移/[1] 失败注入/[2] 启动/[3] 重启/[4] 停止/[6] 外部实例），端口 3185+，`Check()` 断言 | 新增 [7]-[12] 组（§5.2），沿用 `Check`/PASS/FAIL/退出码模式 |
| `Core/ErrorReporter.cs` | `StartFailureContext`（含 Config）；报告 Markdown 含"本次配置(launcher.json)"节 | 报告增加实例 id/name/DSH_HOME 节；`AppVersion` → "0.3.0" |
| `MainWindow.xaml.cs` | 单 `_backend`；`ProbeTickAsync` 轮询单实例；按钮直接操作 `_cfg` | `_backend` → `InstanceManager`；轮询/事件/日志/关闭清理按"选中实例"路由（§4.4） |
| `MainWindow.xaml` | 状态卡 + 4 操作按钮 + 设置 Expander + 日志 | 顶部加**实例选择器**；操作行作用于选中实例；加"新建/克隆/编辑/删除"管理行；设置拆"实例设置/全局设置"（§4.5） |
| `Program.cs` / `App.xaml.cs` | `Config.Load()` → `new MainWindow(cfg)` | 改为 `InstanceRegistry.Load()` → `new MainWindow(registry)`；崩溃快照携带 registry 摘要 |

---

## 2. 数据模型与迁移

### 2.1 `instances.json`（新文件，置于 exe 目录，`launcher.json` 退役）

```jsonc
{
  "version": 2,
  "settings": {                              // 全局设置（原 launcher.json 全局字段迁入）
    "dshCommand": "",
    "errorReportDir": "",
    "theme": "system",
    "homeRoot": "C:\\Users\\<user>\\AppData\\Local\\DshController\\instances"
  },
  "instances": [
    {
      "id": "default",                      // 稳定 ID：[A-Za-z0-9_-]，迁移时固定为 "default"
      "name": "主实例",
      "home": "",                           // DSH_HOME；空 = 不注入（默认 ~/.dsh，兼容旧行为）
      "host": "127.0.0.1",
      "port": 3080,
      "trustedHosts": [],
      "workspace": "C:\\Users\\<user>\\Documents",
      "autoOpenBrowser": true,
      "stopOnExit": true,
      "createdAt": "2026-08-16T00:00:00Z",
      "lastStartedAt": null
    },
    {
      "id": "proj-a",
      "name": "项目A · 试验",
      "home": "C:\\Users\\<user>\\AppData\\Local\\DshController\\instances\\proj-a",
      "port": 3081,
      "workspace": "D:\\projects\\a"
    }
  ]
}
```

设计决策：

- **`home` 空串 = 不注入 DSH_HOME**：迁移出的 `default` 实例保持旧行为（用 `~/.dsh`），零行为突变；新建实例默认分配独立 HOME。
- **`homeRoot` 默认 `%LOCALAPPDATA%\DshController\instances`**：不放在 `~/.dsh` 内部，避免主实例的扫描类工具（glob/grep/技能探测）意外扫到子目录；可在全局设置中修改。
- 序列化：沿用 `System.Text.Json` + `JsonStringEnumConverterEx`（theme）；未知字段忽略（向前兼容）。

### 2.2 迁移规则（v1 → v2）

| 步骤 | 动作 |
|---|---|
| 1 | `instances.json` 已存在 → 直接加载，跳过迁移 |
| 2 | `launcher.json` 存在 → 读取 v1 字段，生成 `instances[0]`（id=`default`，home 留空），全局字段进 `settings` |
| 3 | 原文件改名为 `launcher.json.v1.bak`（保留现场，可回滚） |
| 4 | 原子写 `instances.json`（临时文件 + `File.Move(overwrite)`） |
| 5 | 迁移失败/文件损坏 → 回退默认 registry（保留 v0.2.0 "配置损坏不中断启动" 语义），并在日志提示 |

### 2.3 运行时类型（Core 层新增）

```csharp
// Core/AppSettings.cs          —— 全局设置（settings 节）
public sealed class AppSettings {
    public string DshCommand; public string ErrorReportDir;
    public AppTheme Theme; public string HomeRoot;   // 默认 %LOCALAPPDATA%\DshController\instances
}

// Core/InstanceDef.cs          —— 实例定义（instances[] 元素；字段即 JSON 字段）
public sealed class InstanceDef {
    public string Id; public string Name;
    public string Home; public string Host = "127.0.0.1"; public int Port = 3080;
    public List<string> TrustedHosts = new();
    public string Workspace; public bool AutoOpenBrowser = true;
    public bool StopOnExit = true;
    public DateTime? CreatedAt; public DateTime? LastStartedAt;
    // 运行时便利：ToConfig(AppSettings) → Config（填充 DshCommand/ErrorReportDir）
}

// Core/InstanceRegistry.cs     —— instances.json 读写 + CRUD + 迁移
public sealed class InstanceRegistry {
    public static string FilePath { get; }              // exe 目录 instances.json
    public static InstanceRegistry Load();              // 含 v1 迁移、损坏回退
    public void Save();                                 // 原子写
    public IReadOnlyList<InstanceDef> Instances { get; }
    public InstanceDef Get(string id);                  // 不存在抛 InstanceNotFoundException
    public bool TryGet(string id, out InstanceDef def);
    public void Add(InstanceDef def);                   // 校验：id 格式/唯一、端口 1-65535、host 非空
    public void Update(InstanceDef def);
    public bool Remove(string id);
    public static bool IsValidId(string id);            // ^[A-Za-z0-9_-]{1,64}$
}
```

---

## 3. 核心功能设计

### 3.1 DSH_HOME 注入（BackendManager 最小改动）

`BackendManager.StartCoreAsync` L212-231 现组装：

```csharp
var psi = new ProcessStartInfo { … WorkingDirectory = ws };
if (dsh.Kind == "cmd")
    psi.Arguments = "/d /s /c \"\"" + dsh.Path1 + "\" web --host " + cfg.Host + " --port " + cfg.Port + "\"";
```

改为（约 +6 行）：

```csharp
if (!string.IsNullOrEmpty(cfg.Home))
    psi.EnvironmentVariables["DSH_HOME"] = cfg.Home;
string extra = "";
if (cfg.TrustedHosts != null && cfg.TrustedHosts.Count > 0)
    extra = " --trusted-host " + string.Join(" --trusted-host ", cfg.TrustedHosts.Select(h => "\"" + h + "\""));
// Arguments 尾部拼 extra（cmd 与 node 两种形态一致处理）
```

- `Config` 增加：`[JsonPropertyName("home")] public string Home { get; set; } = "";` 与 `TrustedHosts`。
- **不注入全局 API Key 等变量**（launch-environment 分层：进程环境优先于 `$DSH_HOME/.env`，注入会压过实例自己的 .env，见方案文档 §4.6）。

### 3.2 InstanceManager（Core 层新增）

```csharp
// Core/InstanceManager.cs —— N 个 BackendManager 的集合与路由
public sealed class InstanceManager : IDisposable {
    public InstanceManager(DispatcherQueue dq, InstanceRegistry registry, AppSettings settings);
    public BackendManager For(string id);            // 惰性创建（每实例一个 BackendManager）
    public IReadOnlyDictionary<string, BackendManager> All { get; }
    public InstanceDef Def(string id);
    public async Task<bool> StartAsync(string id);   // 内部：def.ToConfig(settings) → manager.StartAsync
    public async Task<bool> StopAsync(string id, bool killExternal);
    public async Task<bool> RestartAsync(string id);
    public bool IsHomeLocked(string id);             // <home>\.dsh-instance.lock 存在且 PID 存活
    public void StopAllOnExit();                     // 逐实例 stopOnExit=true 且本程序启动的
    public void DisposeAll();
}
```

- 事件路由：`BackendManager` 的事件已带 sender（manager 本身），UI 用 `For(id)` 反查或按选中 id 订阅；**BackendManager 事件签名不改**。
- **实例文件锁**：启动前在 `<home>\.dsh-instance.lock` 写 PID（`FileShare.None`）；启动失败/停止后删除。锁存在且 PID 存活 → 拒绝二次启动（防同 HOME 双进程，桌面版 README 警告的正是这个）。
- 空 `home` 的实例（如迁移出的 `default`）不做锁（兼容旧行为，无法锁定 `~/.dsh` 所有使用者——旧行为即信任用户）。

### 3.3 PortAllocator（Core 层新增）

```csharp
// Core/PortAllocator.cs —— 端口分配与冲突检测
public sealed class PortAllocator {
    public PortAllocator(Func<int, Task<bool>> probe);          // 注入 PortTools.ProbeAsync
    public int Suggest(int preferred, IEnumerable<int> taken);  // preferred 起向上找空闲（≤3099），0=OS 分配
    public async Task<bool> IsFreeAsync(int port);              // 探测 + 在册实例端口排除
}
```

- 规则：3080 段保留给 `default`/用户主实例；新实例默认 `preferred = 3081 + n`；被占用时提示并在 UI 显示候选；用户确认后才写配置。
- 删除实例时释放端口记录（不强制回收，端口本身无状态）。

### 3.4 HomeManager（Core 层新增）

```csharp
// Core/HomeManager.cs —— 创建/克隆/删除/健康检查
public sealed class HomeManager {
    public string NewHomeRoot(string homeRoot, string id);      // homeRoot\id（规范化、去重）
    public void CreateBlank(string home);                       // mkdir（initProfile 由首次启动完成）
    public void Clone(string srcHome, string dstHome, CloneLevel level);
    public bool HealthCheck(string home, out string detail);    // profiles/<web>/cordis.yml 等存在性
    public bool Delete(string home, bool keepBackup, out string backupPath); // 可选 zip 备份
}
public enum CloneLevel { Blank, Standard, Full }               // 方案文档 §5.2
```

- **克隆排除**：`profiles/node_modules`、`profiles/*/node_modules`（junction，由 heal 自动重建）、`.dsh-instance.lock`、`backend.pid` 等运行时文件。
- **依赖路径重写（关键）**：克隆后检查 `profiles/web/package.json` 的 `dependencies`，凡 `file:`/`link:` 指向**旧 HOME 的 packages/** → 复制对应包到新 HOME 并改写路径；指向外部目录（如 `Harness插件\xxx\src`）→ 保留原样并在 UI 标注"共享代码"（方案文档 §5.1.5）。

### 3.5 CLI 扩展（Core/Cli.cs）

| 命令 | 行为 | 退出码 |
|---|---|---|
| `--check` | 原输出 + 实例清单节：每实例 `id / port / home / UP/DOWN / pid` | 2=任一实例 dsh 解析失败 |
| `--spawn-test [--port N] [--home <dir>] [--noredirect]` | 带 `--home` 时验证 DSH_HOME 注入链路：启动后断言 `<home>/profiles/web` 自动初始化 | 0=通过 |
| `--instance <id> start\|stop\|restart\|status [--no-browser]` | 定向操作指定实例（无 GUI）；status 输出 JSON 或文本 | 1=实例不存在/操作失败 |
| `--selftest-core [--port N]` | 原有 6 组 + 新增 [7]-[12]（§5.2） | 0=全绿 |

### 3.6 错误报告扩展（Core/ErrorReporter.cs）

- `StartFailureContext` 增加 `string InstanceId; string InstanceHome;`。
- 报告新增节：`## 实例信息`（id/name/DSH_HOME/克隆来源）与 `## 本次配置（instances.json）`（替代 launcher.json 节）。
- 报告文件名不变（避免破坏既有查看习惯），内容含实例标识即可。

---

## 4. UI 设计（MainWindow）

### 4.1 布局变化（MainWindow.xaml）

```
┌────────────────────────────────────────────────────────┐
│ 🐋 DSH HARNESS 控制器           实例: [项目A ▾]  [◐]   │ ← 标题栏新增实例选择 ComboBox
├────────────────────────────────────────────────────────┤
│ ┌─ 状态卡（选中实例）───────────────────────────────┐ │
│ │ ● 运行中 · 本程序启动      http://127.0.0.1:3081/ │ │
│ │ DSH_HOME: …\instances\proj-a      进程 PID 12345 │ │
│ └──────────────────────────────────────────────────┘ │
│ [▶ 启动] [⟳ 重启] [⏹ 停止] [🌐 打开界面]             │ ← 全部作用于“选中实例”
│ [＋ 新建实例] [⧉ 克隆实例] [✎ 编辑] [🗑 删除]         │ ← 实例管理行（新）
│ ▸ 实例设置（选中实例）                                │
│   主机/端口/工作目录/DSH_HOME/trusted-hosts/行为开关   │
│ ▸ 全局设置                                           │
│   dsh 命令/报告目录/实例目录(homeRoot)/主题            │
│ ▸ 后端日志（选中实例，切换实例时刷新）                 │
└────────────────────────────────────────────────────────┘
```

### 4.2 交互流程（对指定实例操作，code-behind 直接实现）

| 场景 | 流程 |
|---|---|
| 切换实例 | ComboBox 选中 → `_selectedId` 更新 → 状态卡/日志/设置面板全部切换到该实例（日志先 `RecentOutput(200)` 回填再挂实时流） |
| 启动 | `TryReadSettings`（校验选中实例的端口/workspace）→ 保存 → `_instanceMgr.StartAsync(id)`；StartFailed → 报告弹窗（带实例标识） |
| 重启 | 外部实例确认弹窗（现有逻辑按选中实例）→ `RestartAsync(id)`（R4：不拉浏览器） |
| 停止 | 外部进程确认（现有逻辑）→ `StopAsync(id, killExternal)` |
| 新建实例 | 向导 ContentDialog：名称 → 端口（PortAllocator 推荐）→ workspace 浏览 → DSH_HOME 目录（默认 `homeRoot\id`，可改）→ 克隆来源（无/克隆现有/克隆 ~/.dsh）+ 档位 → 创建后自动选中 |
| 删除实例 | 确认弹窗（显示 home 路径）→ 停止该实例 → 可选保留备份 → 删除 registry 项与 HOME |
| 关闭窗口 | 逐实例执行 `stopOnExit && IsMine` 的停止（串行，总超时 15s）→ 保存 registry |

### 4.3 状态轮询

- `ProbeTickAsync` 改为只探测**选中实例**（1s，沿用 `_probeGate` 防重入）；未选中实例状态由事件驱动（StateChanged/Exited）+ 列表卡片上的懒刷新（可选）。
- 忙态（Starting/Stopping/Restarting）跳过探测逻辑不变。

### 4.4 按钮使能矩阵（选中实例）

| 状态 | 启动 | 重启 | 停止 | 打开界面 |
|---|---|---|---|---|
| Stopped | ✅ | ❌ | ❌ | ✅ |
| Starting | ❌ | ❌ | ✅（可取消） | ✅ |
| Running(本程序) | ❌ | ✅ | ✅ | ✅ |
| Running(外部) | ❌ | ✅（确认后） | ✅（确认后） | ✅ |
| Stopping/Restarting | ❌ | ❌ | ❌ | ✅ |

---

## 5. 开发计划（里程碑）

> 估时按"个人开发 + 每里程碑跑通自检"计；总工期约 5–6 个工作日。

### M1：数据层与迁移（1 天）

| # | 任务 | 文件 | 验收 |
|---|---|---|---|
| 1.1 | `Config` 增加 `Home`/`TrustedHosts`；新增 `AppSettings`/`InstanceDef`/`InstanceRegistry` | `Config.cs`、`AppSettings.cs`、`InstanceDef.cs`、`InstanceRegistry.cs` | 单测/自检 [7] 绿 |
| 1.2 | v1→v2 迁移（含 `launcher.json.v1.bak`、原子写、损坏回退） | `InstanceRegistry.cs` | 自检 [7] 覆盖三态：旧文件存在/新文件存在/损坏 |
| 1.3 | `Cli --check` 实例清单输出 | `Cli.cs` | 手工跑 `--check` 输出正确 |
| 1.4 | `Program/App` 改用 registry 启动（先兼容单实例 UI 不崩） | `Program.cs`、`App.xaml.cs` | GUI 正常启动 |

### M2：生命周期与注入（1–1.5 天）

| # | 任务 | 文件 | 验收 |
|---|---|---|---|
| 2.1 | BackendManager DSH_HOME/trusted-host 注入 | `BackendManager.cs` | 自检 [8] 绿（进程环境含 DSH_HOME、profiles/web 自动初始化） |
| 2.2 | `InstanceManager`（集合/路由/锁/StopAllOnExit） | `InstanceManager.cs` | 自检 [9][11] 绿 |
| 2.3 | CLI `--instance <id> start/stop/restart/status` | `Cli.cs` | 手工对临时实例全链路操作成功 |
| 2.4 | `--spawn-test --home` 扩展 | `Cli.cs` | 自检命令跑通 |

### M3：UI 多实例（1–1.5 天）

| # | 任务 | 文件 | 验收 |
|---|---|---|---|
| 3.1 | 实例选择器 + 状态卡/操作行/日志/设置全部按选中实例路由 | `MainWindow.xaml(.cs)` | GUI 手工清单（§6.4）A 组绿 |
| 3.2 | 实例管理行（新建/克隆/编辑/删除）+ 向导对话框 | `MainWindow.xaml(.cs)`、`CreateInstanceDialog`（新） | 向导创建空白实例可启动；删除只删目标 |
| 3.3 | 实例设置/全局设置拆分布局 | `MainWindow.xaml` | 设置读写正确、迁移后默认实例设置回填 |
| 3.4 | 错误报告实例标识 | `ErrorReporter.cs`、`MainWindow.xaml.cs` | 失败报告含实例节 |

### M4：克隆、端口分配器与收尾（1 天）

| # | 任务 | 文件 | 验收 |
|---|---|---|---|
| 4.1 | `PortAllocator` 接入向导与启动前校验 | `PortAllocator.cs`、`MainWindow.xaml.cs` | 自检 [12] 绿；冲突时 UI 提示 |
| 4.2 | `HomeManager` 克隆（三档 + 依赖路径重写 + 排除 junction） | `HomeManager.cs` | 自检 [13] 绿；标准克隆实例首次启动成功 |
| 4.3 | CHANGELOG/README 更新（多实例章节、`--instance` 文档） | `CHANGELOG.md`、`README.md` | 文档与行为一致 |
| 4.4 | `build.ps1` Release 构建 + 全量自检 + GUI 回归 | `build.ps1` | §6 全部通过，产出 `publish-fixed/` |

---

## 6. 测试计划

### 6.1 测试策略

| 层次 | 手段 | 覆盖 |
|---|---|---|
| 单元/无头自检 | `--selftest-core`（发布二进制内，无 GUI） | 迁移、注入、并行、锁、端口、克隆 —— 全部自动化 |
| CLI 集成 | `--spawn-test --home`、`--instance` | 真实 dsh 进程链路 |
| GUI 手工 | 清单驱动（§6.4） | 选择器交互、按钮使能、日志切换、向导 |
| 回归 | 既有 6 组自检 + 单实例 GUI 行为 | v0.2.0 行为不回归 |

**端口纪律（沿用 RESEARCH-NOTES §7）**：
- 用户段：3080（本机主实例）、3081+（用户新实例）——**测试一律不碰**；
- 自检段：3185 起（既有），新增用例用 **3195–3210**；
- 临时 HOME 根：`%TEMP%\dsh-mi-test\homes\<id>`，每次自检前整体删除重建。

### 6.2 自动化自检用例（CoreSelfTest 扩展，沿用 [N] 组风格）

| 组 | 用例 | 断言要点 |
|---|---|---|
| **[7] 注册表与迁移** | 7.1 v1→v2 迁移 | 旧 launcher.json 生成 default 实例；settings 回填；`.v1.bak` 存在 |
| | 7.2 新文件直接加载 | instances.json 合法时内容原样 |
| | 7.3 损坏回退 | 坏 JSON → 默认 registry，不抛异常 |
| | 7.4 ID 校验 | 非法 id（空/含 `/`/超长）拒绝；重复 id 拒绝 |
| **[8] DSH_HOME 注入** | 8.1 环境注入 | 启动 `--home` 实例后，断言进程树中 dsh 的 `DSH_HOME` 生效（通过 `<home>/profiles/web/cordis.yml` 自动生成证明） |
| | 8.2 自动初始化 | 首次启动后 `<home>/profiles/web/package.json`（bundles=[base,web-app]）与 `profiles/node_modules` junction 存在 |
| | 8.3 空 home 不注入 | 用 env 探针脚本（`test-env.cmd` 打印 `%DSH_HOME%` 后 `exit 0`，经 `cfg.DshCommand` 注入，**不真实启动 dsh**）→ 断言输出行 `DSH_HOME=`（空），且未创建任何 DSH_HOME 目录 |
| **[9] 双实例并行** | 9.1 同时 Running | A(homeA, 3196) 与 B(homeB, 3197) 同时就绪 |
| | 9.2 定向停止 | 停 A → A 端口释放、B 仍 Running 且 B 端口仍监听 |
| | 9.3 进程隔离 | 停 A 后 A 的进程树消失，B 的 PID 不变 |
| **[10] 数据隔离** | 10.1 存储隔离 | 在 A 的 `storages/` 写入独特标记文件 `mi-marker-<guid>` → 断言 B 的 `storages/` 无此文件；B 启动产生的会话文件也不出现在 A（目录快照对比） |
| **[11] 实例锁** | 11.1 同 HOME 二次启动拒绝 | 锁文件存在且 PID 存活 → `StartAsync` 返回 false 并日志提示 |
| | 11.2 停止后解锁 | 停止后锁删除，可再次启动 |
| **[12] 端口分配器** | 12.1 冲突检测 | 占用 3198 → `Suggest(3198,…)` 返回 3199+ |
| | 12.2 在册端口排除 | 已注册实例的端口不出现在候选 |
| **[13] 克隆** | 13.1 标准克隆 | 夹具源 HOME → 标准克隆 → 新 HOME 启动成功；`profiles/node_modules` 未复制（junction 重建） |
| | 13.2 依赖路径重写 | 夹具源 package.json 含 `file:..\packages\xxx.tgz` → 克隆后路径指向新 HOME 且包存在 |

> 自检夹具：`HomeManager.CreateBlank` + 手写 `profiles/web/package.json` 即可充当克隆源（无需真实 dsh 插件），避免测试依赖本机插件。

### 6.3 CLI/集成测试

| 用例 | 命令 | 通过标准 |
|---|---|---|
| I-1 注入链路 | `DshController.exe --spawn-test --home %TEMP%\dsh-mi-test\homes\cli --port 3201` | 退出码 0；日志含 "DSH_HOME" 生效证据；目录自动初始化 |
| I-2 定向启动/停止 | `--instance <临时id> start` → `status` → `stop` | status 依次 UP/DOWN；stop 后端口释放 |
| I-3 定向重启 | `--instance <临时id> restart` | PID 变化、无浏览器拉起（CLI 永不拉浏览器） |
| I-4 不存在实例 | `--instance nope status` | 退出码 1 + 明确错误文本 |
| I-5 真实双开回归 | 手工：实例 A(3081)/B(3082) 各开浏览器 → 两边各建会话 → 互不可见 | 方案文档 Phase 0 验收项全过 |

### 6.4 GUI 手工测试清单

**A 组（多实例核心）**
- [ ] A-1 升级迁移：放置 v1 launcher.json → 启动 GUI → 默认实例出现且设置回填，原行为不变
- [ ] A-2 新建空白实例（向导）→ 自动选中 → 启动 → 就绪 → 浏览器打开正确端口
- [ ] A-3 切换实例：日志区切换到目标实例历史输出；状态卡/PID/URL 同步
- [ ] A-4 对指定实例重启：外部实例确认弹窗只针对选中实例；重启不拉浏览器
- [ ] A-5 对指定实例停止：另一实例不受影响
- [ ] A-6 双实例同时运行 → 关闭主窗口 → stopOnExit=true 的实例被停止，false 的保留
- [ ] A-7 按钮使能矩阵（§4.4）逐格核对

**B 组（管理）**
- [ ] B-1 克隆现有实例（标准档）→ 新实例启动成功、配置继承、会话不继承
- [ ] B-2 删除实例（运行中）→ 确认 → 进程停止、HOME 移除、列表消失
- [ ] B-3 端口冲突：手动把新实例端口设为已占用 → 启动前提示，不接受错误端口
- [ ] B-4 实例设置编辑（端口/workspace/home/trusted-hosts）→ 保存 → 重启后生效

**C 组（回归）**
- [ ] C-1 单实例（default）全流程：启动/重启/停止/打开界面/日志滚动/清空
- [ ] C-2 主题三态、设置持久化、报告目录自定义
- [ ] C-3 失败注入（改 dshCommand 为坏路径）→ 报告生成且含实例信息节
- [ ] C-4 `--check`/`--selftest-core`/`--version` 在发布目录可运行

### 6.5 回归保障

- `--selftest-core` 既有 [1]-[6] 组**必须全绿**（改动 BackendManager 时重点盯 [2][3][4] 的启动/重启/停止）。
- `--spawn-test-node` 与 `--spawn-test`（无 `--home`）行为不变。
- 迁移后首次启动 GUI 的默认实例 = 旧 v0.2.0 行为（home 空、端口 3080）。

### 6.6 缺陷等级

| 级别 | 定义 | 门槛 |
|---|---|---|
| P0 | 单实例回归损坏、双实例串数据、误杀外部进程 | 发布前必须清零 |
| P1 | 向导/克隆/锁的边界缺陷（如克隆路径重写漏项） | 发布前必须清零或记录 workaround |
| P2 | 体验类（布局、提示文案、列表刷新时机） | 记录，不阻塞 |

---

## 7. 完成定义（DoD）与发布检查单

- [ ] `--selftest-core` 全绿（含新增 [7]-[13] 组，输出 "N passed, 0 failed"）
- [ ] `--spawn-test --home`、`--instance` 四种子命令实测通过
- [ ] GUI 手工清单 A/B/C 组全过
- [ ] 迁移路径实测：本机 launcher.json → instances.json，default 实例可启动
- [ ] 双实例（3081/3082）并行运行 ≥30 分钟，会话/设置/日志互不可见
- [ ] 失败报告含实例标识；崩溃报告路径不回归
- [ ] CHANGELOG v0.3.0 条目 + README 多实例章节 + `--instance` 用法
- [ ] `build.ps1` Release 构建通过，发布目录自检命令可运行
- [ ] `launcher.json` 退役说明写入 README（旧文件自动迁移，可手动删除）

---

## 8. 风险与对策（开发期）

| 风险 | 影响 | 对策 |
|---|---|---|
| BackendManager 改动引发单实例回归 | P0 | M2 改动控制在 L212-231 注入段；每步跑 [1]-[6] 自检 |
| 克隆后 junction/file: 依赖悬空 | 新实例启动失败 | 自检 [13.2] 强制覆盖；HomeManager.HealthCheck 提供"修复"入口 |
| 迁移丢失用户配置 | 数据损失 | 保留 `.v1.bak`；迁移代码只读旧文件、原子写新文件 |
| 多实例日志/事件串台 | 状态显示错乱 | 事件路由以实例 id 为键；UI 层单测（自检）断言事件隔离 |
| 测试误碰 3080/用户目录 | 影响用户工作 | 端口纪律 + 测试 HOME 全在 `%TEMP%\dsh-mi-test`；自检禁止默认 home 空启动真实 dsh（8.3 用测试 cwd） |
| 向导创建重名/非法 id | 配置损坏 | InstanceRegistry.Add 强校验 + UI 即时提示 |

---

## 9. 参考资料

- 方案文档：《多开隔离-调研与实施方案.md》（§4 机制、§5.1 插件隔离、§5.2 克隆档位、§6 架构、§7 阶段）
- 代码基线：`DshController/Core/*.cs`、`MainWindow.xaml(.cs)`、`Program.cs`、`App.xaml.cs`
- 测试基线：`docs/TEST-RESULTS.md`、`docs/RESEARCH-NOTES.md`（端口纪律）
- 事故教训：`C:\Users\<user>\Documents\AI\deepseek Harness\报错\2026-08-16-DSH启动失败-白名单patch互踩.md`（插件隔离边界，克隆时注意）

---

*计划结束。开发按 M1→M4 顺序推进；每个里程碑结束跑一次 §6 对应自检再进入下一步。*
