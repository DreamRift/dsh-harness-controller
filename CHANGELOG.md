# Changelog

本项目遵循 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/) 风格，
版本号遵循 [Semantic Versioning](https://semver.org/lang/zh-CN/)。

## [0.4.0] - 开发中（待发布）

> **WSL2 实例管理**：控制器现在可切换管理 Windows 与 WSL2 两种运行环境的 DeepSeek Harness 实例，
> WSL 实例与 Windows 完全隔离（独立 Linux DSH_HOME、默认 Linux 工作区），仅按需共享 Windows 工作区，
> 停止时按策略智能关闭发行版/VM。核心逻辑经独立验证版（DshWslCtrl）实测后移植。

### 新增

- **WSL 运行环境**：实例支持 `runtime: wsl`，经 `wsl.exe -d <发行版> --exec bash` 拉起 dsh web；DSH_HOME 经 WSLENV 传入 WSL，输出经 wsl.exe UTF-8 中继复用现有日志管道与就绪探测
- **发行版内启动/停止**：启动脚本（pidfile + exec）写入发行版 /tmp；停止按 pidfile 校验 + 进程组 TERM→KILL 升级，同发行版多实例互不影响
- **智能关闭（wslShutdownPolicy）**：发行版内无其他 harness 实例 → `wsl -t` 终止发行版；`smart` 策略下无其他发行版运行才 `wsl --shutdown` 释放 VM
- **WSL 实例界面支持**：实例下拉显示 `[WIN]`/`[WSL 发行版]` 标识；实例设置新增运行环境选择、WSL 发行版、WSL DSH_HOME；新建实例对话框支持选择 WSL2 运行环境；状态卡显示 WSL 发行版与 Linux HOME
- **凭据自动同步**：WSL 实例首次启动时，若发行版内无凭据且 Windows 侧 ~/.dsh 存在，自动复制 settings.yaml/.credentials.yaml（chmod 600）

### 新增文件

- `Core/WslTools.cs`：WSL 互操作层（wsl.exe 调用、UTF-16LE 解码、发行版管理、路径转换、/mnt/c 文件上传）
- `Core/WslLaunch.cs`：WSL 启动脚本生成、发行版内停止、智能关闭

### 修复

- **工作区源码缺失恢复**：此前工作区误删的 `Core/`、`Styles/`、`Assets/`、`docs/` 等文件恢复为 HEAD 版本；`AppSettings` 补回 v0.3.1 新增的 `newInstanceWorkspace` 字段

### 版本号统一

- csproj / ErrorReporter / CHANGELOG 三处版本号统一为 0.4.0

## [0.3.1] - 开发中（待发布）

> **实例选择器可用性**：将实例选择 ComboBox 从标题栏移入内容区，修复下拉打不开问题；
> **新建默认值优化**：继承当前选中实例的设置作为新建对话框默认值；
> **Expander Save/Cancel**：为"实例设置"/"全局设置"添加独立保存与取消按钮。

### 新增

- **实例设置 / 全局设置独立保存**：两个设置 Expander 均新增"保存"/"取消"按钮，保存后自动收起；取消则放弃未保存修改并恢复原值
- **全局设置新增"新建实例默认工作区"**：设置新建/克隆实例对话框中工作目录的默认值；留空则回退为继承当前选中实例的工作目录

### 修复

- **实例选择下拉打不开（严重）**：`ExtendsContentIntoTitleBar + SetTitleBar` 下标题栏内的 ComboBox 下拉箭头点击被拖拽命中测试拦截，导致下拉列表无法弹出——选择器移入内容区顶部保证所有交互正常（WinUI 3 已知问题，见 docs/FIX-MULTI-INSTANCE-SWITCH.md 补充说明）
- **新建实例工作目录保持"我的文档"**：改为按"全局新建实例默认工作区 → 当前选中实例工作目录 → 我的文档"三级回退；用户修改主实例工作目录后，新建实例默认沿用同一路径
- **诊断日志增强**：切换/刷新操作全程记录诊断信息

## [0.3.0] - 开发中（未发布）

> **多实例功能**：一个控制器管理 N 个互相隔离的 DeepSeek Harness 实例。
每个实例 = 独立 `$DSH_HOME`（数据/会话/凭据/插件全隔离）+ 独立端口 + 独立 workspace。

### 新增

- **多实例数据模型**：`launcher.json` → `instances.json`（`version 2`，`settings` 全局 +
  `instances[]` 实例清单）；旧配置首次启动自动迁移为 `default` 实例（原文件备份为
  `launcher.json.v1.bak`），全新环境兜底预置 default 实例——v0.2.0 单实例行为零突变
- **实例隔离**：启动时注入 `DSH_HOME` 环境变量（每个实例独立数据目录），支持
  `--trusted-host` 追加参数；`web` profile 首次启动自动初始化，模块依赖 junction 自动共享
- **对指定实例操作（UI）**：标题栏实例选择器；状态卡 / 启动 / 重启（不拉浏览器）/
  停止 / 打开界面 / 日志 全部按选中实例路由；DSH_HOME 显示；按钮使能状态机矩阵
- **实例管理**：新建实例（向导：名称/端口/工作目录）、克隆实例（空白 / 克隆 ~/.dsh /
  克隆现有实例 × Blank/Standard/Full 三档，含 file:/link: 依赖路径重写与 node_modules 排除）、
  删除实例（确认后停止 + 移除注册与 HOME 目录）
- **对指定实例操作（CLI）**：`--instance <id> start|stop|restart|status`；
  `--check` 输出实例清单；`--spawn-test --home <dir>` 验证 DSH_HOME 注入与自动初始化
- **实例 HOME 文件锁**：`<home>\.dsh-instance.lock` 记录 PID，防止同一 HOME 被重复拉起
- **端口分配器**：3080-3099 用户段内自动推荐空闲端口，冲突检测
- **错误报告**：新增"实例信息"节（实例 ID / DSH_HOME），配置节改为 instances.json 视角
- **自检扩展**：`--selftest-core` 新增 [7]-[13] 组（迁移 / DSH_HOME 注入 / 双实例并行 /
  数据隔离 / 实例锁 / 端口分配 / 克隆），既有真实启动用例改用临时 HOME（不污染 ~/.dsh）

### 变更

- 配置文件名：`launcher.json` → `instances.json`（自动迁移，无手工操作）
- `AppVersion` → `0.3.0`

### 修复

- 多实例下事件串台风险：UI 事件按"选中实例的 manager"过滤
- CLI `--check` 迁移后主配置读取（改为实例清单首个实例）

## [0.2.0] - 开发中（未发布）

> **当前状态**：代码实现完成，构建通过；`--check`、`--spawn-test-node`、
> `--spawn-test`、`--selftest-core`（Debug 与 Release 各 30 项）已全部验证通过；
> GUI 已启动并确认外部实例状态、按钮、日志与明暗主题切换；Release 发布目录已生成，
> GitHub Release 发布待执行。

### 重构

- **技术栈迁移：WinForms（单文件 C# 5）→ WinUI 3 / Windows App SDK 1.5**（需求"尽可能使用 WinUI 3"）：
  - `.NET 6` + `net6.0-windows10.0.19041.0`，x64，unpackaged（免安装、免打包），
    Windows App SDK 运行时自包含（目标机零前置，仅需 Win10 17763+ 与 .NET 6 Desktop Runtime）
  - 单文件 1244 行拆分为 `Core/`（纯逻辑）+ XAML UI（code-behind），
    UI 不再直接接触进程/端口 API
- **稳定性重构**（v0.1.0 已知缺陷 D1–D10 逐项修复，详见 docs/REFACTOR-PLAN.md §2）：
  - `launcher.json` 读写改用 `System.Text.Json`，根除 v0.1.0 手写转义导致的
    反斜杠翻倍污染；历史污染值加载时自动净化迁移
  - 端口探测、netstat、进程树终止全部异步化——停止后端不再冻结 UI（v0.1.0 最长 ~28s）
  - 外部后端运行时不再每秒同步起 netstat 子进程（3s 结果缓存）
  - `BackendManager` 显式状态机（Stopped/Starting/Running/Stopping/Restarting），
    启动等待期随时可"停止"（v0.1.0 启动中 180s 内按钮全禁用）
  - 关闭窗口改用 AppWindow.Closing 拦截 + 重关模式，确保退出时进程树清理完成后窗口才销毁；
    关闭时静默保存设置，不再弹"端口无效"警告
- **效率优化**：子进程输出走 `Channel` + 100ms 批量泵（替代每行一次 BeginInvoke），
  日志环形缓冲 2000 行，dsh 路径解析结果缓存

### 新增

- **启动失败详细报告**：6 类失败（dsh 未找到 / 启动异常 / 子进程早退 / 就绪超时 /
  端口无法释放 / 全局崩溃）自动生成 Markdown 报告（环境、dsh 解析轨迹、配置、
  端口状态、输出转录、异常详情、排障建议），保存目录可在设置中自定义
  （默认 `我的文档\DshController\error-reports`），失败后弹窗可一键打开报告/目录
- **重启后端**：⟳ 按钮一键"停止 → 重新启动"，**全程不打开浏览器**
  （重启路径硬编码抑制自动打开，浏览器旧页面刷新即可重连）；
  外部实例重启前弹窗确认
- **DeepSeek Harness 同款图标**：应用/任务栏/窗口图标取自 dsh-web-frontend 官方
  favicon 鲸鱼（`Assets/app.ico`，16–256px 九尺寸）；任务栏使用白底黑色鲸鱼，
  并放大鲸鱼避免小尺寸下看起来只是白色块；标题栏鲸鱼随明暗主题自动换色
- **DSH 简约风格 UI**：设计令牌（品牌蓝 `#3964FE`、底色 `#F9FAFB`/`#151517`、
  三级文字色、10% 边框）从 dsh-web-frontend 真实 CSS 提取；Mica 材质背景、
  状态卡片、圆角按钮、明暗主题跟随系统 + 手动三态切换并持久化
- 设置面板 Expander 化，新增"错误报告目录"行；状态卡新增 URL 一键复制
- 日志区新增自动滚动开关；日志批量渲染

### 变更

- 就绪超时（180s）行为变更：清理无响应子进程（v0.1.0 会留下僵尸进程）
- 产物形态变更：单 exe（36 KB）→ `publish-fixed/` 目录（约 120 MB，自包含 WASDK；
  `build.ps1 -Portable` 可连 .NET 一并自包含）
- 构建依赖变更：.NET Framework csc → dotnet SDK 6.0+（`build.ps1` 已适配）

### 修复

- `--check` 等自检命令在 Windows App SDK 清单合并（mt.exe）下的启动问题
  （requestedExecutionLevel 的 UIAccess 属性导致 SxS 激活失败）
- 外部后端直接点击"重启"并确认后无动作：UI 通过端口探测显示 Running，但
  `BackendManager` 内部状态仍为 Stopped，旧逻辑会提前返回；现在允许停止外部实例
  并由本程序重新启动后端（重启仍不打开浏览器）

## [0.1.0] - 2026-08-14

### 新增

- Windows 桌面控制面板（C# WinForms，零第三方依赖）：
  - ▶ **启动后端**：隐藏启动 `dsh web`，就绪后自动打开浏览器
  - ⏸ **暂停/停止**：停止 `dsh web` 进程树并确保端口释放
  - 🌐 **打开界面**：一键在默认浏览器打开 Harness 界面
- 后端状态实时显示（已停止 / 启动中 / 运行中，含外部进程识别）
- 后端 stdout/stderr 实时日志，自动捕获 `dsh web:` 公告 URL
- 配置持久化（`launcher.json`）：主机、端口、工作目录、自动开浏览器、退出时停止
- 外部实例检测：通过 `netstat` 定位监听进程，停止前弹窗确认
- 无界面自检命令：`--check`、`--spawn-test`、`--spawn-test-node`、`--version`
- `build.ps1` 一键编译脚本（仅需 Windows 自带 .NET Framework 4.x 与 csc.exe）