# DSH Harness 控制器 (DshController)

> Windows 桌面小应用：一键启动 / 暂停 / 打开 **DeepSeek Harness** 的 Web 后端。
> 零第三方依赖，仅需 Windows 自带的 .NET Framework 4.x。

![License](https://img.shields.io/github/license/DreamRift/dsh-harness-controller)
![Platform](https://img.shields.io/badge/platform-Windows-blue)
![.NET](https://img.shields.io/badge/.NET-Framework%204.x-512BD4)

---

## 目录

- [简介](#简介)
- [功能特性](#功能特性)
- [界面速览](#界面速览)
- [快速开始](#快速开始)
- [配置说明](#配置说明)
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
| **▶ 启动后端** | 隐藏启动 `dsh web`，后端就绪后**自动拉起浏览器**打开 Harness 界面 |
| **⏸ 暂停/停止** | 结束 `dsh web` 的完整进程树，并确认端口已释放 |
| **🌐 打开界面** | 独立的“一键打开浏览器界面”按钮 |

> 说明：DeepSeek Harness 本身没有内置的“暂停后端”命令（`dsh` CLI 只提供
> `--host` / `--port` / `--trusted-host` 等启动参数），因此“暂停”实现为停止后端进程。
> 会话数据保留在 `$DSH_HOME`（默认 `%USERPROFILE%\.dsh`），重新启动后端即可继续，
> 无需担心数据丢失。

## 功能特性

- **一键启停**：启动、停止后端进程树（cmd → node → worker 多层子进程都能清理干净；
  若 dsh 派生脱离的监听进程，会通过 `netstat` 定位并一并结束，确保端口彻底释放）
- **自动开浏览器**：后端就绪后自动打开默认浏览器；也可随时用独立按钮重新打开
- **状态可视化**：已停止 / 启动中 / 运行中（含“外部进程”识别），显示 PID 与界面地址
- **实时日志**：后端 stdout/stderr 按 UTF-8 实时显示，自动捕获 dsh 打印的
  `dsh web: http://...` 公告 URL
- **外部实例检测**：若后端由其他方式启动（例如你手动在终端运行过 `dsh web`），
  应用会识别为“外部进程”，停止前弹窗确认，绝不误杀
- **配置持久化**：主机、端口、工作目录、行为选项保存在 `launcher.json`，随开随用
- **零依赖**：单文件 exe（约 36 KB），仅需 Windows 自带 .NET Framework 4.x

## 界面速览

```
┌──────────────────────────────────────────────────────────────┐
│ 后端控制                                                     │
│   状态：● 运行中（本程序启动）     进程 PID：18672            │
│   界面地址：http://127.0.0.1:3080/                           │
│   ┌──────────────┐ ┌──────────────┐ ┌──────────────┐        │
│   │ ▶ 启动后端   │ │ ⏸ 暂停/停止 │ │ 🌐 打开界面 │        │
│   └──────────────┘ └──────────────┘ └──────────────┘        │
├──────────────────────────────────────────────────────────────┤
│ 设置（保存在 launcher.json）                                  │
│   主机：127.0.0.1      端口：3080                             │
│   工作目录：[C:\Users\...\Documents]  [浏览…]                │
│   ☑ 启动后自动打开浏览器界面   ☑ 退出时停止由本程序启动的后端 │
├──────────────────────────────────────────────────────────────┤
│ 后端输出日志                                                  │
│   10:30:01  已启动 dsh web（PID 18672，工作目录: ...）        │
│   10:30:02        dsh web: http://127.0.0.1:3080             │
│   10:30:02  后端已就绪: http://127.0.0.1:3080/               │
└──────────────────────────────────────────────────────────────┘
```

## 快速开始

### 方式一：直接下载编译好的程序

1. 下载 [Releases](https://github.com/DreamRift/dsh-harness-controller/releases)
   中的 `DshController.exe`；
2. 双击运行；
3. 点 **▶ 启动后端**，浏览器会自动打开 Harness 界面；
4. 需要关闭后端时点 **⏸ 暂停/停止**；随时可用 **🌐 打开界面** 回到页面。

> 前置条件：已通过 npm 全局安装 DeepSeek Harness 的 CLI（`npm i -g @deepseek-ai/dsh`），
> 程序会自动在 `%APPDATA%\npm\dsh.cmd`、`PATH`、`node + @deepseek-ai/dsh` 中查找。

### 方式二：从源码构建

```powershell
git clone https://github.com/DreamRift/dsh-harness-controller.git
cd dsh-harness-controller
powershell -ExecutionPolicy Bypass -File build.ps1
.\DshController.exe
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
  "stopOnExit": true
}
```

| 字段 | 默认值 | 说明 |
|---|---|---|
| `host` | `127.0.0.1` | 后端监听主机（对应 dsh 的 `--host`） |
| `port` | `3080` | 后端监听端口（对应 dsh 的 `--port`） |
| `workspace` | `我的文档` | 后端的工作目录；**运行目录即默认 workspace 根目录**（dsh 文档约定） |
| `dshCommand` | 空 | 手动指定 dsh 启动命令的完整路径（一般无需设置） |
| `autoOpenBrowser` | `true` | 启动就绪后是否自动打开浏览器 |
| `stopOnExit` | `true` | 关闭程序时是否停止由本程序启动的后端 |

> `launcher.json` 含本机路径，已被 `.gitignore` 排除，不会提交到仓库。

## 构建方法

无需 Visual Studio，仅需 Windows 自带的 .NET Framework 4.x 与 PowerShell：

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1
```

脚本会自动定位 `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`，
编译产物为 `DshController.exe`（约 36 KB，零外部依赖）。

## 部署自检

程序内置了无界面自检模式，适合发布前/排障时使用：

```powershell
DshController.exe --check                        # 打印 dsh 解析结果与端口状态
DshController.exe --spawn-test --port 3137       # 真实启动/停止一个 dsh web 实例（不开浏览器）
DshController.exe --spawn-test-node --port 3137  # 仅验证进程管线（微型 node 服务，不涉及 dsh）
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
| 默认地址 | `127.0.0.1:3080`（dsh-web-app 的部署默认值） |
| 工作目录 | dsh 文档：“运行命令时所在的目录将作为默认 workspace 根目录” |
| 暂停后端 | 停止 `dsh web` 进程树（Harness 无内置 pause 命令，会话数据保留于 `$DSH_HOME`） |
| 界面地址 | `http://127.0.0.1:3080/` |

启动后端时实际执行的命令（在工作目录下）：

```
cmd /s /c ""<npm 全局目录>\dsh.cmd" web --host 127.0.0.1 --port 3080"
```

找不到 npm shim 时自动回退到：`node <dsh 包>\lib\bin.js web --host ... --port ...`。

## 工作原理

- **命令解析**：依次查找 ① `launcher.json` 指定路径 → ② `%APPDATA%\npm\dsh.cmd`
  → ③ `PATH` 中的 `dsh` → ④ `node + @deepseek-ai/dsh/lib/bin.js`；
- **启动**：`ProcessStartInfo` 隐藏窗口 + 重定向 stdout/stderr（UTF-8），
  输出实时显示在日志区；
- **就绪检测**：TCP 握手探测（不依赖 HTTP 语义与系统代理），1.2 秒超时，
  每 800ms 轮询，最长等待 180 秒；
- **停止**：先 `.NET Process.Kill()` 强杀主进程，再 `taskkill /PID <pid> /T /F`
  清理整棵进程树；若停止后端口仍被监听（dsh 可能派生脱离的 node 进程），
  通过 `netstat -ano` 定位真正的监听进程并一并结束，确保端口彻底释放；
- **外部实例**：`netstat -ano` 解析 `LISTENING` 行（异步读取，带超时保护）得到 PID，
  停止外部实例前弹窗确认；
- **日志捕获**：正则提取输出中的 `http://...` 作为公告 URL 展示。

## 项目结构

```
dsh-harness-controller/
├── DshController.cs      # 全部源码（C# WinForms，单文件，C# 5 兼容）
├── build.ps1             # 编译脚本（csc.exe，无需 Visual Studio）
├── test-server.js        # 自检辅助（--spawn-test-node 使用的微型 HTTP 服务）
├── README.md             # 本文档
├── CHANGELOG.md          # 版本变更记录
├── LICENSE               # MIT 许可证
└── .gitignore            # 排除运行时产物与本机配置
```

## 常见问题 (FAQ)

**Q：点击“启动后端”没反应？**
先运行 `DshController.exe --check` 查看 `dsh command` 是否被找到；若显示
`(NOT FOUND)`，请确认已执行 `npm i -g @deepseek-ai/dsh`，或在 `launcher.json`
的 `dshCommand` 中填写 `dsh.cmd` 的完整路径。

**Q：提示“后端已在运行”是什么意思？**
端口探测发现已有实例在监听（可能是你手动启动的，或上次未关闭干净）。
应用不会重复启动，会直接打开浏览器；需要停止时点“暂停/停止”，程序会先弹窗确认。

**Q：“暂停”后我的会话/文件会丢吗？**
不会。会话与数据保存在 `$DSH_HOME`（默认 `%USERPROFILE%\.dsh`），
再次启动后端即可恢复。

**Q：更换端口后浏览器打开的是旧地址？**
`launcher.json` 中修改 `port` 后关闭窗口保存；之后启动与打开界面都会使用新端口。

**Q：可以在沙箱/受限环境运行自检吗？**
可以，自检命令支持 `--noredirect`，在禁止管道重定向的环境中也能运行。

## 许可证

[MIT](LICENSE) © 2026 [DreamRift](https://github.com/DreamRift)

DeepSeek Harness 及其 `@deepseek-ai/dsh` 属 DeepSeek AI 所有，遵循其自身许可证。
