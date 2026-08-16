# Changelog

本项目遵循 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/) 风格，
版本号遵循 [Semantic Versioning](https://semver.org/lang/zh-CN/)。

## [0.2.0] - 开发中（未发布）

> **当前状态**：代码实现完成，构建通过；`--check`、`--spawn-test-node`、
> `--spawn-test`、`--selftest-core`（Debug 与 Release 各 23 项）已全部验证通过；
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
  - 关闭窗口改用 AppWindow.Closing 拦截+重关模式，确保退出时进程树清理完成后窗口才销毁；
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
  favicon 鲸鱼（`Assets/app.ico`，16–256px 九尺寸）；任务栏使用品牌蓝底白色鲸鱼，
  避免小尺寸下呈现纯白块；标题栏鲸鱼随明暗主题自动换色
- **DSH 简约风格 UI**：设计令牌（品牌蓝 `#3964FE`、底色 `#F9FAFB`/`#151517`、
  三级文字色、10% 边框）从 dsh-web-frontend 真实 CSS 提取；Mica 材质背景、
  状态卡片、圆角按钮、明暗主题跟随系统 + 手动三态切换并持久化
- 设置面板 Expander 化，新增"错误报告目录"行；状态卡新增 URL 一键复制
- 日志区新增自动滚动开关；日志批量渲染

### 变更

- 就绪超时（180s）行为变更：清理无响应子进程（v0.1.0 会留下僵尸进程）
- 产物形态变更：单 exe（36 KB）→ `publish/` 目录（约 120 MB，自包含 WASDK；
  `build.ps1 -Portable` 可连 .NET 一并自包含）
- 构建依赖变更：.NET Framework csc → dotnet SDK 6.0+（`build.ps1` 已适配）

### 修复

- `--check` 等自检命令在 Windows App SDK 清单合并（mt.exe）下的启动问题
  （requestedExecutionLevel 的 UIAccess 属性导致 SxS 激活失败）

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
