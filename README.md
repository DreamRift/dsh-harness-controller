# DSH Harness 控制器 (DshController)

> Windows 桌面小应用：一键启动 / 重启 / 停止 / 打开 **DeepSeek Harness** 的 Web 后端。
> WinUI 3 原生界面，DeepSeek Harness 同款设计语言与鲸鱼图标。

![License](https://img.shields.io/github/license/DreamRift/dsh-harness-controller)
![Platform](https://img.shields.io/badge/platform-Windows%2010%2017763%2B-blue)
![.NET](https://img.shields.io/badge/.NET-6.0-512BD4)
![WinUI](https://img.shields.io/badge/WinUI-3--WASDK%201.5-5C2D91)

---

## 目录

- [简介](#简介)
- [功能特性](#功能特性)
- [界面速览](#界面速览)
- [快速开始](#快速开始)
- [配置说明](#配置说明)
- [错误报告](#错误报告)
- [构建方法](#构建方法)
- [部署自检](#部署自检)
- [与 dsh 文档的对应关系](#与-dsh-文档的对应关系)
- [工作原理](#工作原理)
- [项目结构](#项目结构)
- [常见问题](#常见问题-faq)
- [许可证](#许可证)

---

## 简介

[DeepSeek Harness](https://github.com/deepseek-ai/deepseek-harness) 是一个本地运行的
AI 编码代理框架，其 Web 界面通过 `dsh web` 命令启动（默认监听 `http://127.0.0.1:3080`）。

日常使用中，每次都要打开终端敲命令、记地址、手动关进程。**DshController** 把这一切
收进一个带按钮的小窗口：

| 按钮 | 作用 |
|---|---|
| **▶ 启动后端** | 隐藏启动 `dsh web`，后端就绪后按配置自动打开浏览器界面 |
| **⟳ 重启后端** | 一键"停止 → 重新启动"，**不会打开新的浏览器界面**（旧页面刷新即可重连） |
| **⏹ 停止** | 结束 `dsh web` 的完整进程树，并确认端口已释放 |
| **🌐 打开界面** | 独立的"一键打开浏览器界面"按钮 |

> 说明：DeepSeek Harness 本身没有内置的"暂停后端"命令（`dsh` CLI 只提供
> `--host` / `--port` / `--trusted-host` 等启动参数），因此"暂停"实现为停止后端进程。
> 会话数据保留在 `$DSH_HOME`（默认 `%USERPROFILE%\.dsh`），重新启动后端即可继续，
> 无需担心数据丢失。

## 功能特性

- **一键启停/重启**：启动、重启（不拉浏览器）、停止后端进程树（cmd → node → worker
  多层子进程都能清理干净；若 dsh 派生脱离的监听进程，会通过 `netstat` 定位并一并结束）
- **启动失败详细报告**：失败时自动生成 Markdown 诊断报告（环境 / dsh 解析轨迹 /
  配置 / 端口状态 / 输出转录 / 异常 / 排障建议），保存目录可自定义
- **状态可视化**：已停止 / 启动中 / 运行中（含"外部进程"识别），显示 PID 与界面地址
- **实时日志**：后端 stdout/stderr 按 UTF-8 批量渲染（100ms 合并，不卡 UI），
  自动捕获 dsh 打印的 `dsh web: http://...` 公告 URL
- **外部实例检测**：若后端由其他方式启动（例如你手动在终端运行过 `dsh web`），
  应用会识别为"外部进程"，停止/重启前弹窗确认，绝不误杀
- **WinUI 3 原生体验**：Mica 材质、圆角卡片、明暗主题跟随系统（可手动三态切换）、
  深度还原 DeepSeek Harness web 的简约设计（品牌蓝 `#3964FE`）
- **同款图标**：应用图标取自 dsh-web-frontend 官方 favicon 鲸鱼；任务栏/exe 使用白底黑色鲸鱼，小尺寸也清晰可见
- **配置持久化**：主机、端口、工作目录、行为选项、报告目录、主题保存在 `launcher.json`

## 界面速览

```
┌──────────────────────────────────────────────────────┐
│ 🐋 DSH HARNESS 控制器                    [◐] – □ ✕   │ ← Mica + 鲸鱼图标
│ ┌──────────────────────────────────────────────────┐ │
│ │ ● 运行中 · 本程序启动                进程 PID     │ │ ← 状态卡（明暗主题）
│ │ http://127.0.0.1:3080/            本程序 18672   │ │
│ └──────────────────────────────────────────────────┘ │
│  [▶ 启动后端] [⟳ 重启后端] [⏹ 停止] [🌐 打开界面]     │
│ ▸ 设置                                               │
│   主机 / 端口 / 工作目录 / 错误报告目录 / 行为开关     │
│ ▸ 后端日志                        [滚动：开] [清空]   │
│ ┌──────────────────────────────────────────────────┐ │
│ │ 21:30:01  已启动 dsh web（PID 18672）   Consolas  │ │
│ └──────────────────────────────────────────────────┘ │
│  dsh: %APPDATA%\npm\dsh.cmd · v0.2.0                 │
└──────────────────────────────────────────────────────┘
```

## 快速开始

### 方式一：直接使用发布包

1. 下载 [Releases](https://github.com/DreamRift/dsh-harness-controller/releases)
   中的 `DshController-0.2.0-win-x64.zip`，解压到任意目录；
2. 运行 `DshController.exe`（前置条件：Win10 17763+ 与 [.NET 6 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/6.0)；
   `-Portable` 全自包含包则无任何前置）；
3. 点 **▶ 启动后端**，浏览器会自动打开 Harness 界面；
4. 需要重启后端时点 **⟳ 重启后端**（浏览器不会弹新窗口）；
   需要关闭后端时点 **⏹ 停止**。

> 前置条件：已通过 npm 全局安装 DeepSeek Harness 的 CLI（`npm i -g @deepseek-ai/dsh`），
> 程序会自动在 `%APPDATA%\npm\dsh.cmd`、`PATH`、`node + @deepseek-ai/dsh` 中查找。

### 方式二：从源码构建

```powershell
git clone https://github.com/DreamRift/dsh-harness-controller.git
cd dsh-harness-controller
powershell -ExecutionPolicy Bypass -File build.ps1
.\publish-fixed\DshController.exe
```

## 配置说明

首次运行后，程序目录下会生成 `launcher.json`（关闭窗口时自动保存）：

```json
{
  "host": "127.0.0.1",
  "port": 3080,
  "workspace": "C:\\Users\\<你>\\Documents",
  "dshCommand": "",
  "autoOpenBrowser": true,
  "stopOnExit": true,
  "errorReportDir": "",
  "theme": "system"
}
```

| 字段 | 默认值 | 说明 |
|---|---|---|
| `host` | `127.0.0.1` | 后端监听主机（对应 dsh 的 `--host`） |
| `port` | `3080` | 后端监听端口（对应 dsh 的 `--port`） |
| `workspace` | `我的文档` | 后端的工作目录；**运行目录即默认 workspace 根目录**（dsh 文档约定） |
| `dshCommand` | 空 | 手动指定 dsh 启动命令的完整路径（一般无需设置） |
| `autoOpenBrowser` | `true` | 启动就绪后是否自动打开浏览器（**重启路径永不自动打开**） |
| `stopOnExit` | `true` | 关闭程序时是否停止由本程序启动的后端 |
| `errorReportDir` | 空 | 启动失败/崩溃报告目录；空 = `我的文档\DshController\error-reports` |
| `theme` | `system` | 界面主题：`system`（跟随系统）/ `light` / `dark` |

> `launcher.json` 含本机路径，已被 `.gitignore` 排除，不会提交到仓库。

## 错误报告

以下场景会自动生成详细的 Markdown 诊断报告，并弹窗提供"打开报告 / 打开目录"：

| 触发场景 | 报告内容侧重 |
|---|---|
| dsh 命令未找到 | 4 级查找路径逐条结果 |
| 进程启动异常 | 工作目录 / 命令行 / Win32 错误 |
| 子进程早退 | 退出码 + 输出转录 |
| 就绪超时（180s） | 全程输出转录 |
| 端口无法释放 | 停止操作日志、监听 PID |
| 程序崩溃 | 异常全文 + 环境上下文 |

报告目录默认 `我的文档\DshController\error-reports`，可在 **设置 → 报告目录** 中自定义；
目录不可写时自动回退到 exe 旁的 `reports\`。

## 构建方法

需要 .NET SDK 6.0+（无需 Visual Studio）：

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1             # Release 发布 -> publish-fixed\（含 zip）
powershell -ExecutionPolicy Bypass -File build.ps1 -Debug      # 快速开发构建
powershell -ExecutionPolicy Bypass -File build.ps1 -Portable   # 连 .NET 一并自包含
powershell -ExecutionPolicy Bypass -File build.ps1 -Clean      # 清理构建产物
```

> 体积说明：WinUI 3 + 自包含 Windows App SDK 的发布目录约 120 MB（v0.1.0 单 exe
> 36 KB 的时代一去不返），`build.ps1` 会同时产出 zip 便于分发。首次构建需联网还原
> NuGet 包。

## 部署自检

程序内置了无界面自检模式，适合发布前/排障时使用：

```powershell
DshController.exe --check                        # 打印 dsh 解析结果与端口状态
DshController.exe --spawn-test --port 3137       # 真实启动/停止一个 dsh web 实例（不开浏览器）
DshController.exe --spawn-test-node --port 3137  # 仅验证进程管线（微型 node 服务，不涉及 dsh）
DshController.exe --selftest-core --port 3185     # 核心链路无头自检（启动/重启/停止/报告）
DshController.exe --version                      # 打印版本
```

- `--spawn-test` 会完整走一遍：解析 dsh 命令 → 隐藏启动 → 等待就绪 → 停止进程树 →
  验证端口释放，任何一步失败都会以非零退出码结束；
- 输出同时写入 exe 同目录的 `cli.log`，方便脚本读取；
- 在禁止管道重定向的受限环境（如沙箱）中，可附加 `--noredirect` 运行自检。

## 与 dsh 文档的对应关系

| 本应用功能 | 对应的 dsh 用法（见 `@deepseek-ai/dsh` README） |
|---|---|
| 启动后端 | `dsh web`（即 `dsh --profile web` 的别名），等价执行 `dsh web --host <host> --port <port>` |
| 重启后端 | 停止进程树后重新执行 `dsh web`（不打开浏览器） |
| 默认地址 | `127.0.0.1:3080`（dsh-web-app 的部署默认值） |
| 工作目录 | dsh 文档："运行命令时所在的目录将作为默认 workspace 根目录" |
| 暂停后端 | 停止 `dsh web` 进程树（Harness 无内置 pause 命令，会话数据保留于 `$DSH_HOME`） |
| 界面地址 | `http://127.0.0.1:3080/` |

启动后端时实际执行的命令（在工作目录下）：

```
cmd /s /c ""<npm 全局目录>\dsh.cmd" web --host 127.0.0.1 --port 3080"
```

找不到 npm shim 时自动回退到：`node <dsh 包>\lib\bin.js web --host ... --port ...`。

## 工作原理

- **命令解析**：依次查找 ① `launcher.json` 指定路径 → ② `%APPDATA%\npm\dsh.cmd`
  → ③ `PATH` 中的 `dsh` → ④ `node + @deepseek-ai/dsh/lib/bin.js`（结果缓存）；
- **启动**：`ProcessStartInfo` 隐藏窗口 + 重定向 stdout/stderr（UTF-8），
  输出经 `Channel` 以 100ms 批量渲染到日志区；
- **就绪检测**：TCP 握手探测（不依赖 HTTP 语义与系统代理），1.2 秒超时，
  每 800ms 轮询，最长等待 180 秒（超时清理进程并生成失败报告）；
- **重启**：状态机驱动 停止 → 端口释放确认 → 重新启动；重启路径硬编码抑制
  浏览器自动打开；
- **停止**：优先 `.NET` 的 `Kill(entireProcessTree)`，`taskkill /T /F` 兜底；
  若停止后端口仍被监听，通过 `netstat -ano` 定位真正的监听进程并一并结束；
- **外部实例**：`netstat -ano` 解析 `LISTENING` 行（3s 缓存）得到 PID，
  停止/重启外部实例前弹窗确认；
- **稳定性**：显式状态机（Stopped/Starting/Running/Stopping/Restarting）串行化
  全部生命周期操作；全局异常钩子生成崩溃报告。

## 项目结构

```
dsh-harness-controller/
├── DshController.csproj       # WinUI 3 工程配置
├── app.manifest               # DPI (PerMonitorV2) 等声明
├── Program.cs                 # 自定义入口：CLI 自检在 WinUI 引导前执行
├── App.xaml / App.xaml.cs     # 应用入口 + 全局异常兜底
├── MainWindow.xaml / .cs      # 主窗口（布局 + 交互）
├── Styles/DshTheme.xaml       # DSH 设计令牌 → Light/Dark 资源字典
├── Core/                      # 纯逻辑层（无 UI 依赖）
│   ├── Config.cs              # launcher.json（System.Text.Json + 旧值净化迁移）
│   ├── DshResolver.cs         # dsh/node 4 级解析 + 缓存 + 轨迹
│   ├── PortTools.cs           # 端口探测 / netstat / 进程树终止（全异步）
│   ├── BackendManager.cs      # 后端状态机 + 输出 Channel + 重启抑制浏览器
│   ├── ErrorReporter.cs       # 失败/崩溃 Markdown 报告
│   ├── Cli.cs                 # --check / --spawn-test / --selftest-core 自检
│   ├── CoreSelfTest.cs        # 核心链路无头自检（启动/重启/停止/报告/配置迁移）
│   └── NativeMethods.cs       # Win32 P/Invoke（AttachConsole 等）
├── Assets/                    # app.ico（鲸鱼九尺寸）、whale.svg(-white)
├── legacy/DshController.cs    # v0.1.0 WinForms 源码留档
├── docs/                      # 重构方案、调研笔记、测试记录
├── test-server.js             # 自检辅助（--spawn-test-node）
├── build.ps1                  # 构建/发布脚本
├── README.md / CHANGELOG.md / LICENSE
└── .gitignore
```

## 常见问题 (FAQ)

**Q：点击"启动后端"没反应？**
先运行 `DshController.exe --check` 查看 `dsh command` 是否被找到；若显示
`(NOT FOUND)`，请确认已执行 `npm i -g @deepseek-ai/dsh`，或在 launcher.json
的 `dshCommand` 中填写 `dsh.cmd` 的完整路径。

**Q：启动失败后哪里看详细原因？**
失败弹窗会提供"打开报告"；报告是完整的 Markdown 诊断文档，
默认在 `我的文档\DshController\error-reports`（可在设置中改）。

**Q：重启后端会弹出新浏览器窗口吗？**
不会。重启路径无论 `autoOpenBrowser` 设置如何都不打开浏览器；
原界面刷新一下即可重连新后端。

**Q：提示"后端已在运行"是什么意思？**
端口探测发现已有实例在监听（可能是你手动启动的，或上次未关闭干净）。
应用不会重复启动；需要停止/重启时程序会先弹窗确认。

**Q："暂停"后我的会话/文件会丢吗？**
不会。会话与数据保存在 `$DSH_HOME`（默认 `%USERPROFILE%\.dsh`），
再次启动后端即可恢复。

**Q：为什么 v0.2.0 变成了一个大文件夹而不是单个 exe？**
WinUI 3 + 自包含 Windows App SDK 的固有代价（约 120 MB）。用
`build.ps1 -Portable` 构建可连 .NET 运行时一并自包含，拷贝即用。

**Q：可以在沙箱/受限环境运行自检吗？**
可以，自检命令支持 `--noredirect`，在禁止管道重定向的环境中也能运行。

## 许可证

[MIT](LICENSE) © 2026 [DreamRift](https://github.com/DreamRift)

DeepSeek Harness 及其 `@deepseek-ai/dsh` 属 DeepSeek AI 所有，遵循其自身许可证。
应用图标取自 `dsh-web-frontend` 的官方 favicon（鲸鱼）。
