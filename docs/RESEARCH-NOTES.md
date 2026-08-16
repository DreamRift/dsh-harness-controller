# DshController v0.2.0 调研笔记（RESEARCH-NOTES）

> 调研日期：2026-08-16。本文是重构方案（REFACTOR-PLAN.md）的事实依据，
> 记录环境探测结果、可行性验证数据与设计令牌的提取过程。

## 1. 现有代码盘点

| 项 | 现状（v0.1.0） |
|---|---|
| 语言/UI | C# 5 兼容 WinForms，单文件 `DshController.cs`（1244 行） |
| 编译 | `build.ps1` → .NET Framework 4.x `csc.exe`，产物单 exe 约 36 KB |
| 类构成 | Program / Config / DshCommand / DshResolver / Backend / Native / Cli / MainForm / ControlExtensions |
| CLI 自检 | `--check`、`--spawn-test`、`--spawn-test-node`、`--version`、`--noredirect`（AttachConsole 输出） |
| git | 仓库在 main 分支，HEAD=`ddfecab`，工作区有未提交的稳定性补丁（IsChildAlive、_childPid 快照、启动异常后不残留 Process 对象——重构时保留这些修复意图） |
| 运行时产物 | launcher.json（本机为干净 6 字段版）/ cli.log / crash.log |

## 2. 环境探测结果（本机）

| 检查项 | 结果 |
|---|---|
| .NET SDK | `dotnet 6.0.136`（`C:\Program Files\dotnet`），无额外 workload |
| NuGet | 可联网还原（含 Microsoft.WindowsAppSDK） |
| csc(.NET Framework) | `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe` 存在（v0.1.0 用） |
| Chrome | `C:\Program Files\Google\Chrome\Application\chrome.exe`（用于图标栅格化） |
| ImageMagick/Inkscape | 无 → 图标用 headless Chrome `--default-background-color=00000000` 栅格化 |
| dsh 安装 | `%APPDATA%\npm\node_modules\@deepseek-ai\dsh`（npm 全局，dsh.cmd shim 存在） |
| 本机后端 | **127.0.0.1:3080 正在运行（HTTP 200）**——测试时必须避开此端口，用 3081+ |

## 3. WinUI 3 可行性验证（已实测通过）

最小探针工程（见 `%TEMP%\nuget-probe\probe-winui`）：

- csproj 关键配置：`net6.0-windows10.0.19041.0` / `TargetPlatformMinVersion 10.0.17763.0` /
  `UseWinUI=true` / `WindowsPackageType=None`（免打包）/ `WindowsAppSDKSelfContained=true`（免装运行时）/
  `Platforms=x64` / 包引用 `Microsoft.WindowsAppSDK 1.5.240607001` + `Microsoft.Windows.SDK.BuildTools 10.0.22621.756`
- `dotnet restore` ✅（首次 25s）
- `dotnet build -p:Platform=x64` ✅ 0 警告 0 错误（12.6s，含 XAML 编译）
- 运行探针 exe ✅（进程存活，自包含 WASDK 正常初始化）
- `dotnet publish -c Release -r win-x64 --self-contained false` ✅ 输出 **179 MB**（WASDK 自包含的代价；.NET 6 桌面运行时需目标机已装）

结论：**真 WinUI 3（非 WinForms 模仿）在本机全链路可行**。版本策略：锁定已验证的
WASDK 1.5.240607001，不在本次重构中追新（稳定性优先）。

## 4. DeepSeek Harness 设计令牌（从 dsh-web-frontend 真实 CSS 提取）

来源：`%APPDATA%\npm\node_modules\@deepseek-ai\dsh\node_modules\@deepseek-ai\dsh-web-frontend\dist\assets\index-*.css`

| 令牌 | 值 | 用途 |
|---|---|---|
| `--dsw-alias-brand-primary` | `#3964FE`（fallback 值） | 品牌蓝：主按钮/强调/spinner |
| `--dsw-static-deepseek-450` | `rgb(86,134,254)` | 亮色变体（进行中状态） |
| `--dsw-static-neutral-bluish-00` | `rgb(255,255,255)` | 浅色模式卡片底 |
| `--dsw-static-neutral-bluish-50` | `rgb(249,250,251)` = `#F9FAFB` | 浅色模式页面底 |
| `--dsw-static-neutral-bluish-950` | `rgb(21,21,23)` = `#151517` | 深色模式页面底 |
| `--dsw-static-neutral-bluish-1000` | `rgb(15,17,21)` = `#0F1115` | 主文字（浅色模式） |
| `--dsw-static-neutral-bluish-500` | `rgb(151,157,166)` | 中性图标/占位 |
| label-secondary / tertiary | `#61666B` / `#81858C` | 次级/三级文字 |
| 边框 | `rgb(0 0 0 / 10%)`（浅）；`rgb(255 255 255 / 10%)`（深） | 分隔线/描边 |
| 字体 | `-apple-system, "Segoe UI", "PingFang SC", "Microsoft YaHei"...`；代码 `Consolas/JetBrains Mono` | WinUI 用 Segoe UI 即天然一致 |
| 动效 | `cubic-bezier(.4,0,.2,1)`，快 0.1s / 常规 0.2s | 过渡节奏 |

视觉基调（boot 屏实测）：白卡片居中 + 16px/600/0.08em 字距 wordmark + 品牌蓝 spinner——极简、
大量留白、细边框、圆角卡片。

## 5. 图标调研与产物

- Harness 官方图标 = `dsh-web-frontend/dist/favicon.svg`（鲸鱼，viewBox 0 0 50 50，
  浅色模式 `#000` 填充、深色模式 `#fff`，`manifest.webmanifest` 名称 "DeepSeek Harness"）。
- 已生成（`Assets/`）：
  - `whale.svg`：官方源文件副本（去除 media query）
  - `app.ico`：9 尺寸 PNG-in-ICO 容器（已验证容器结构），构图 = `#3964FE` 品牌蓝圆角瓷砖 +
    白色鲸鱼居中 76%，保证任务栏/小尺寸图标不是白色色块
- 工具链：Node 生成逐尺寸 HTML → headless Chrome `--screenshot` + `--default-background-color=00000000` →
  PowerShell BinaryWriter 组装 ICO。图标通过 exe 提取与像素比例复核。

## 6. 旧代码缺陷索引（重构对照清单）

详见 REFACTOR-PLAN.md §2（D1–D16，带行号）。

## 7. 测试约束

- **禁止动 3080**（用户后端在跑）：CLI spawn 测试默认 `cfg.Port+1=3081`，显式 `--port` 时也用 ≥3081。
- 旧 exe `DshController.exe` 在仓库根目录，被 `.gitignore` 的 `*.exe` 排除；新产物进 `publish/`。
