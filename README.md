# DSH Harness 控制器 (DshController)

> Windows 桌面小应用：一键启动 / 重启 / 停止 / 打开 **DeepSeek Harness** 的 Web 后端，
> **Windows 与 WSL2 实例在左侧边栏两个独立页面中分别管理**，支持互相隔离的多实例、
> **按实例指定 harness 版本**（默认跟随当前环境主实例版本）。
> WinUI 3 原生界面（侧边栏导航 + Mica 材质），DeepSeek Harness 同款设计语言与鲸鱼图标。

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
- [harness 指定版本（v0.5.0）](#harness-指定版本v050)
- [多实例与隔离](#多实例与隔离)
- [WSL2 实例（v0.4.0）](#wsl2-实例-v040)
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
- **启动失败详细报告**：失败时由核心层自动生成 Markdown 诊断报告——
  **控制台报错原文（控制台转录）+ 启动诊断表（npx/dsh 路径、发行版信息、真实命令行）
  + 实例信息（名称/ID/运行环境/端口/DSH_HOME/harness 版本）+ 生成时间**，
  再附环境 / dsh 解析轨迹 / 配置 / 端口状态 / 子进程输出 / 排障建议；
  文件名含实例 ID 与时间戳，保存目录可自定义（界面提示条可一键打开）
- **状态可视化**：已停止 / 启动中 / 运行中（含"外部进程"识别），显示 PID、界面地址、
  运行环境徽章与生效的 harness 版本
- **实时日志**：后端 stdout/stderr 按 UTF-8 批量渲染（100ms 合并，不卡 UI），
  自动捕获 dsh 打印的 `dsh web: http://...` 公告 URL；全局控制台按 `[WIN·名称]` /
  `[WSL·名称]` 前缀区分实例，提供"打开报告目录"快捷按钮
- **外部实例检测**：若后端由其他方式启动（例如你手动在终端运行过 `dsh web`），
  应用会识别为"外部进程"，停止/重启前弹窗确认，绝不误杀
- **多实例管理（v0.3.0）**：一个控制器管理多个互相隔离的 DeepSeek Harness 实例——
  每实例独立 `DSH_HOME`（数据/会话/凭据/插件全隔离）+ 独立端口 + 独立 workspace；
  支持新建/克隆（空白/标准/完整三档，含插件依赖路径重写）/删除实例；
  CLI 支持 `--instance <id> start|stop|restart|status` 定向操作
- **WSL2 实例管理（v0.4.0）**：WSL 实例经 `wsl.exe` 在发行版内拉起 dsh（Linux 原生
  node/dsh），与 Windows 环境完全隔离（独立 Linux `DSH_HOME`、默认 Linux 原生工作区）；
  可在实例设置中配置 Windows 路径工作区以**按需共享** win 文件（经 `/mnt/c`）；
  停止时按策略**智能关闭**（发行版内无其他 harness 实例 → 终止发行版 →
  无其他发行版运行 → `wsl --shutdown` 释放 VM）
- **Win/WSL 双界面（v0.5.0）**：Windows 实例与 WSL 实例分别在两个独立标签页中管理，
  各持独立实例列表 / 状态 / 操作 / 设置，不再混用同一个"实例选项"；
  全局设置移入独立页面后，改为 **v0.5.1 侧边栏导航**（见下）
- **侧边栏界面（v0.5.1）**：主窗口改为 NavigationView 左侧边栏——
  Windows 实例 / WSL 实例 / 全局设置三个页面，实例面板常驻不销毁，
  切换页面不中断状态轮询；控制台为底部可收起的共享坞；默认窗口
  1180×800（最小 960×620），任何窗口尺寸下内容不再互相遮挡
- **harness 指定版本（v0.5.0）**：每实例可指定任意 harness 版本（经 `npx` 拉取
  `@deepseek-ai/dsh@<版本>` 启动，指定版本时无需全局安装 dsh）；默认**跟随当前环境
  主实例版本**（自动检测 Windows 全局 dsh / WSL 发行版内 dsh 的版本）；
  支持从 npm registry 拉取版本列表下拉选择，也可手工输入
- **WinUI 3 原生体验**：Mica 材质、圆角卡片、明暗主题跟随系统（可手动三态切换）、
  深度还原 DeepSeek Harness web 的简约设计（品牌蓝 `#3964FE`）
- **同款图标**：应用图标取自 dsh-web-frontend 官方 favicon 鲸鱼；任务栏/exe 使用白底黑色鲸鱼，小尺寸也清晰可见
- **配置持久化**：主机、端口、工作目录、harness 版本、行为选项、报告目录、主题保存在 `instances.json`

## 界面速览

```
┌───────────────────────────────────────────────────────────────────────┐
│ 🐋 DSH HARNESS 控制器                                    [◐] – □ ✕     │ ← Mica + 鲸鱼图标
│ ┌────────────┬──────────────────────────────────────────────────────┐ │
│ │ 🖥 Windows  │ 实例 [主实例 · :3080 ▾]      [＋新建][⧉克隆][🗑删除]    │ │ ← v0.5.1 侧边栏
│ │ 实例 · 1 个 │ 共 1 个 Windows 实例 · 本机直接运行 · harness v0.1.0  │ │
│ │ 🐧 WSL      │ ⛔ 启动失败：子进程早退（主实例）     [打开报告]        │ │ ← 失败提示条
│ │ 实例 · 1 个 │ ┌──────────────────────────────────────────────────┐ │ │
│ │ ────────   │ │ ● 运行中 · 本程序启动 [WINDOWS] (harness v0.1.0)   │ │ │
│ │ ⚙ 全局设置  │ │ http://127.0.0.1:3080/ [复制]           进程 PID   │ │ │ ← 状态卡
│ │            │ │ DSH_HOME: C:\…\instances\default           18672   │ │ │
│ │            │ │ 启动方式: 本机已安装的 dsh（跟随当前环境主实例版本） │ │ │
│ │            │ └──────────────────────────────────────────────────┘ │ │
│ │            │ [▶ 启动] [⟳ 重启] [⏹ 停止] [🌐 打开界面]               │ │
│ │            │ ▾ 实例设置 · 主实例（default）…                       │ │
│ │            │ ─────────────────────────────────────────────────   │ │
│ │            │ 🖥 控制台 [隐藏] [滚动：开] [复制日志] [清空] [打开报告目录]│ │ ← 可收起坞
│ │            │ ┌──────────────────────────────────────────────────┐ │ │
│ │            │ │ 21:30:01 [WIN·主实例] 已启动 dsh web（PID 18672） │ │ │
│ │            │ └──────────────────────────────────────────────────┘ │ │
│ └────────────┴──────────────────────────────────────────────────────┘ │
│ Windows 实例 1 个 · WSL 实例 1 个 · WIN harness v0.1.0 …             │ ← 状态栏
└───────────────────────────────────────────────────────────────────────┘
```

## 快速开始

### 方式一：直接使用发布包

1. 下载 [Releases](https://github.com/DreamRift/dsh-harness-controller/releases)
   中的 `DshController-0.4.0-win-x64.zip`，解压到任意目录；
2. 运行 `DshController.exe`（前置条件：Win10 17763+ 与 [.NET 6 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/6.0)；
   `-Portable` 全自包含包则无任何前置）；
3. 点 **▶ 启动后端**，浏览器会自动打开 Harness 界面；
4. 需要重启后端时点 **⟳ 重启后端**（浏览器不会弹新窗口）；
   需要关闭后端时点 **⏹ 停止**。

> 前置条件：已通过 npm 全局安装 DeepSeek Harness 的 CLI（`npm i -g @deepseek-ai/dsh`），
> 程序会自动在 `%APPDATA%\npm\dsh.cmd`、`PATH`、`node + @deepseek-ai/dsh` 中查找。
> 使用 **WSL2 实例**时，前置条件为已安装 WSL2 发行版，并在发行版内通过 npm 安装过 dsh
> （`npm install -g @deepseek-ai/dsh`），详见 [WSL2 实例](#wsl2-实例-v040)。

### 方式二：从源码构建

```powershell
git clone https://github.com/DreamRift/dsh-harness-controller.git
cd dsh-harness-controller
powershell -ExecutionPolicy Bypass -File build.ps1
.\publish-fixed\DshController.exe
```

## 配置说明

v0.3.0 起配置为 `instances.json`（v0.2.0 及以前的 `launcher.json` 首次启动自动迁移为
默认实例 `default`，原文件备份为 `launcher.json.v1.bak`；全新环境自动预置 default 实例，
行为与旧版一致）。关闭窗口时自动保存：

```jsonc
{
  "version": 2,
  "settings": {                          // 全局设置
    "dshCommand": "",                    // 手动指定 dsh 命令完整路径（一般无需设置）
    "errorReportDir": "",                // 错误报告目录；空 = 我的文档\DshController\error-reports
    "theme": "system",                   // 界面主题：system / light / dark
    "homeRoot": ""                       // 新实例 DSH_HOME 根；空 = %LOCALAPPDATA%\DshController\instances
  },
  "instances": [
    {
      "id": "default",                   // 实例 ID（字母/数字/_/-）
      "name": "主实例",
      "home": "",                        // DSH_HOME；空 = 不注入（使用默认 ~/.dsh）
      "host": "127.0.0.1",
      "port": 3080,
      "trustedHosts": [],                // 额外 --trusted-host（可重复）
      "workspace": "C:\\Users\\<你>\\Documents",
      "harnessVersion": "",              // v0.5.0：空 = 跟随当前环境主实例版本；非空 = 指定版本（npx 拉取该版本）
      "autoOpenBrowser": true,
      "stopOnExit": true
    }
  ]
}
```

| 实例字段 | 默认值 | 说明 |
|---|---|---|
| `home` | 空 | 实例数据目录（DSH_HOME）；**空 = 不注入**，使用默认 `~/.dsh`（兼容旧行为）；非空 = 完全隔离的独立实例 |
| `host` / `port` | `127.0.0.1` / `3080` | 后端监听地址（对应 dsh 的 `--host` / `--port`） |
| `trustedHosts` | 空 | 额外浏览器信任来源（对应 dsh 的 `--trusted-host`） |
| `workspace` | 我的文档 | 实例工作目录（运行目录即默认 workspace 根目录）；WSL 实例可为 Linux 路径（`~/` 或 `/` 开头） |
| `harnessVersion` | 空 | harness 指定版本（v0.5.0）：空 = 跟随当前环境主实例版本；如 `0.1.0-rc.7` = 经 npx 拉取该版本启动 |
| `autoOpenBrowser` | `true` | 启动就绪后自动打开浏览器（**重启路径永不自动打开**） |
| `stopOnExit` | `true` | 退出程序时是否停止由本程序启动的该实例后端 |
| `runtime` | `windows` | 运行环境：`windows`（本机 cmd 直接拉起）/ `wsl`（WSL2 发行版内运行，v0.4.0） |
| `wslDistro` | 空 | WSL 发行版名称（`runtime=wsl` 时有效，如 `Ubuntu-26.04`） |
| `wslHome` | 空 | WSL 实例的 Linux 侧 `DSH_HOME`（`~` 前缀展开；空 = 发行版内默认 `~/.dsh`） |

WSL 环境设置 `wslShutdownPolicy`（在 **侧边栏 → WSL 实例 → 实例设置 → 停止后关闭** 中配置）：
`smart`（默认，见下方 WSL 章节）| `distroOnly` | `always` | `never`。

> `instances.json` 含本机路径，已被 `.gitignore` 排除，不会提交到仓库。

## harness 指定版本（v0.5.0）

每个实例可以**跟随当前环境主实例版本**（默认），也可以**指定某个 harness 版本**启动：

- **默认（`harnessVersion` 为空）**：直接使用当前环境已安装的 dsh——
  Windows 实例用本机全局 dsh，WSL 实例用发行版内 dsh；界面里显示为
  「跟随当前环境（v当前版本）」；
- **指定版本（如 `harnessVersion: "0.1.0-rc.7"`）**：启动时改为执行
  `npx --yes @deepseek-ai/dsh@0.1.0-rc.7 web ...`（Windows 用本机 npx.cmd，
  WSL 用发行版内原生 npx）；npx 会复用/拉取并缓存该版本，**首次拉取需联网**，
  之后由 npm 缓存加速。指定版本时**不要求已全局安装 dsh**，只要有 node/npm 即可；
- **当前环境版本检测**：Windows 读 npm 全局包 `package.json`（回退执行
  `dsh --version`）；WSL 在发行版内执行 `dsh --version`（node 兜底）。
  实例设置的"检测当前版本"按钮可随时重新探测；状态卡徽标与页脚实时显示生效版本
  （「当前环境」或「指定版本」）；
- **版本列表**：实例设置的"拉取版本列表"按钮经 `npm view @deepseek-ai/dsh versions`
  （Windows 侧或发行版内执行）列出已发布版本，直接下拉选择；离线时仍可手工输入；
- **输入校验**：版本号统一规范化（可写 `v0.1.0-rc.7`，自动去 `v`），
  非法格式不会写入配置，控制台给出提示；
- **新建实例默认值**：新建/克隆对话框默认「跟随当前环境（v当前版本）」，
  下拉同时提供"指定为当前环境版本"与已发布版本列表；克隆现有实例默认继承源实例设置；
- 缺少 npx（或发行版内缺少 npx）而实例指定了版本时，启动会失败并生成详细报告，
  提示安装 Node.js/npm 或把版本改回「跟随当前环境」。

## 多实例与隔离

每个实例 = **独立的 `$DSH_HOME` + 独立端口 + 独立 workspace**，数据（会话/存储/技能/
凭据/设置）、插件装配与补丁、浏览器状态（不同端口 = 不同 origin）天然互不可见；
模块依赖（`profiles/node_modules`）为自动维护的 junction，只读共享、磁盘成本≈0。

- **新建实例**：向导填写名称/端口/工作目录 → 空目录首次启动时由 dsh 自动初始化
  （`initProfile`），无需手工步骤；
- **克隆实例**：三档复制（Blank 空目录 / Standard 配置与技能 / Full 完整复制），
  自动排除 node_modules 与运行时文件，`file:`/`link:` 插件依赖自动重写指向新 HOME；
- **删除实例**：确认后停止进程、移除注册与 HOME 目录；
- **实例锁**：`<home>\.dsh-instance.lock` 记录 PID，防止同一 HOME 被重复拉起；
- **CLI 定向操作**：`DshController.exe --instance <id> start|stop|restart|status`
  （v0.5.0 起 `start` 会等到后端就绪或失败才返回：就绪退出码 0，失败退出码 1 并打印报告路径）、
  `--check`（含实例清单）、`--spawn-test --home <dir>`（验证 DSH_HOME 注入与自动初始化）。

## WSL2 实例（v0.4.0）

除 Windows 本机实例外，控制器可把同一套实例管理能力延伸到 **WSL2 发行版内**。
v0.5.0 起 Windows 与 WSL 实例分别在 **"Windows 实例" / "WSL 实例" 两个页面**中管理
（v0.5.1 起为左侧边栏导航），各持独立实例列表、状态卡、操作按钮与实例设置，
启动 / 重启 / 停止 / 打开界面 / 日志 / 设置均按各自环境选中实例操作，
win 与 wsl 实例互不影响。

### 运行环境与隔离

- **运行时**：WSL 实例经 `WSLENV=DSH_HOME/u` 把 DSH_HOME 传入发行版，再经
  `wsl.exe -d <发行版> --exec bash /tmp/dshwsl-<port>.sh` 拉起 dsh web；
  输出经 wsl.exe UTF-8 中继，复用现有日志管道 / 就绪探测 / 状态机；
- **完全隔离**：WSL 实例使用独立的 Linux 侧 `DSH_HOME`（ext4 原生，默认 `~/dsh-instances/...`），
  与 Windows 侧 `~/.dsh` 互不读写；工作区默认也是 Linux 原生目录（如 `~/dsh-workspaces/...`）
- **按需共享 Windows 工作区**：把实例的 `workspace` 配成 Windows 路径（如 `C:\Users\...\deepseek Harness`），
  WSL 实例才会经 `/mnt/c` 访问 win 文件——默认隔离，需求出现时再共享
- **浏览器访问**：Windows 浏览器直接访问 `http://127.0.0.1:<port>/`（WSL2 默认
  localhost 转发；dsh 禁止 `--host 0.0.0.0`，WSL 内绑定 127.0.0.1 即可）
- **首次启动自动同步凭据**：若发行版内 DSH_HOME 尚无 `settings.yaml`/`.credentials.yaml`
  且 Windows 侧 `~/.dsh` 存在，自动复制过去（含 `chmod 600`，满足 dsh 凭据插件 owner-only 要求）

### 停止与关闭策略（wslShutdownPolicy，v0.5.0 可在 WSL 界面直接设置）

| 策略 | 行为 |
|---|---|
| `smart`（默认） | 停止 harness → 发行版内已无其他 harness 实例才 `wsl -t` 终止发行版 → 无其他发行版运行再 `wsl --shutdown` 立即释放 VM |
| `always` | 停止后无条件 `wsl --shutdown`（会连带终止其他发行版，如 Docker Desktop 的发行版） |
| `distroOnly` | 只 `wsl -t` 终止本发行版，VM 由系统约 60 秒空闲后自动关闭 |
| `never` | 发行版与 VM 都不动，只停止该实例进程（在发行版里还跑着别的活时最安全） |

停止实现：杀 wsl.exe 宿主 → 发行版内按 pidfile 校验（防 PID 复用误杀）+ 进程组
`TERM→KILL` 升级 → 策略收尾。同发行版内多实例按
`(@deepseek-ai/dsh|dsh).*--port <端口>` 精确匹配，互不影响，也不会误伤命令行里
恰好含"port N"的无关进程。

### 前置条件

```text
# WSL2 发行版已安装（如 wsl --install -d Ubuntu-24.04），并在其内部：
npm install -g @deepseek-ai/dsh      # 用 WSL 里的 npm 安装（勿用 Windows 侧 shim）
```

> 注意：WSL 里的 dsh 必须是从发行版内 `npm install -g` 安装的 Linux 原生版本。
> 控制器会跳过 `/mnt` 路径下的 Windows shim（Windows 为 Linux 环境准备的 shim 在
> Linux 下路径语义混乱，会解析出 `C:\...` 之类的错误路径）。

## 错误报告

以下场景会自动生成详细的 Markdown 诊断报告（v0.5.0 起由**核心层直接生成**，不依赖界面状态），
控制台同步打印报告路径，面板顶部的提示条（InfoBar）提供
"打开报告 / 打开报告目录 / 复制报告路径"：

| 触发场景 | 报告内容侧重 |
|---|---|
| dsh 命令未找到 | 4 级查找路径逐条结果 |
| npx 未找到（实例指定了版本时） | 指定版本信息 + npx 安装指引 |
| WSL 未安装 / 发行版不存在 / 用户未初始化 | 已安装发行版列表、发行版用户、Linux `$HOME` |
| WSL 内 dsh / npx 未找到 | 发行版内解析结果 + 安装指引 |
| 工作区不可访问 | 工作区在发行版内的解析结果 |
| 进程启动异常 | 工作目录 / 真实命令行 / Win32 错误 |
| 子进程早退 | 退出码 + 控制台转录 + 子进程输出转录 |
| 就绪超时（180s） | 全程输出转录 |
| 端口无法释放（停止失败） | 停止操作日志、监听 PID（文件名 `stopfail_`） |
| 程序崩溃 | 异常全文 + 环境上下文 |

每份报告都包含：

1. **具体的报错信息**——失败类型 + 摘要 + **控制台转录**（该实例最近 200 行控制台输出，
   即界面里看到的报错原文）+ 子进程输出转录（dsh/npm 的原始 stdout/stderr）；
2. **启动诊断表**——失败前已确认的事实：运行环境、npx/dsh 路径、发行版列表与用户、
   Linux `$HOME`、DSH_HOME 注入值、工作区解析结果、**真实命令行**；
3. **实例信息**——名称 / ID / 运行环境 / 主机端口 / DSH_HOME / harness 版本 / 工作目录；
4. **生成时间**（含时区）与应用版本、可直接解析的 `json` 配置快照、分类排障建议。

文件名形如 `DshController-fail_<实例ID>_20260820_214850.md`（时间 = 本地时间）。

报告目录默认 `我的文档\DshController\error-reports`，可在 **全局设置 → 报告目录** 中
指定任意目录（支持中文路径，不存在会自动创建）；目录不可写时自动回退到 exe 旁的
`reports\`，并在控制台提示。全局设置里的"打开"按钮与控制台右上角的"打开报告目录"
按钮都可一键定位。

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
实例指定了 harness 版本时改为：`npx --yes @deepseek-ai/dsh@<版本> web --host ... --port ...`
（WSL 实例在发行版内执行同语义脚本）。

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
- **WSL 实例（v0.4.0）**：启动 = 唤醒发行版 → 解析用户 / DSH_HOME / 原生 dsh →
  生成启动脚本（`export PATH` + pidfile + `exec dsh web`）经 `/mnt/c` 拷入发行版
  `/tmp` → `wsl.exe --exec bash` 拉起（主要是规避 wsl.exe 引号转义坑）→ 复用
  Windows 侧 TCP 就绪探测（WSL2 localhost 转发）；
  停止 = 杀 wsl.exe 宿主 → 发行版内 pidfile 校验 + 进程组 `TERM→KILL` →
  smart/always/distroOnly 策略收尾（见 [WSL2 实例](#wsl2-实例-v040)）；
- **稳定性**：显式状态机（Stopped/Starting/Running/Stopping/Restarting）串行化
  全部生命周期操作；全局异常钩子生成崩溃报告。

## 项目结构

```
dsh-harness-controller/
├── DshController.csproj       # WinUI 3 工程配置
├── app.manifest               # DPI (PerMonitorV2) 等声明
├── Program.cs                 # 自定义入口：CLI 自检在 WinUI 引导前执行
├── App.xaml / App.xaml.cs     # 应用入口 + 全局异常兜底
├── MainWindow.xaml / .cs      # 主窗口（侧边栏导航 + 页面切换 + 共享控制台坞）
├── InstancePanel.xaml / .cs   # 单环境实例面板（Windows / WSL 各一份实例）
├── Styles/DshTheme.xaml       # DSH 设计令牌 → Light/Dark 资源字典
├── Core/                      # 纯逻辑层（无 UI 依赖）
│   ├── Config.cs              # launcher.json（System.Text.Json + 旧值净化迁移）
│   ├── DshResolver.cs         # dsh/node 4 级解析 + 缓存 + 轨迹
│   ├── PortTools.cs           # 端口探测 / netstat / 进程树终止（全异步）
│   ├── BackendManager.cs      # 后端状态机 + 输出 Channel + 重启抑制浏览器（含 WSL 分支）
│   ├── WslTools.cs            # WSL2 互操作（wsl.exe 调用/UTF-16LE 解码/路径转换/文件上传）
│   ├── WslLaunch.cs           # WSL 启动脚本生成 + 发行版内停止 + 智能关闭
│   ├── InstanceDef.cs         # 实例定义（runtime/wslDistro/wslHome 等字段）
│   ├── InstanceManager.cs     # N 个实例的路由 + HOME 锁
│   ├── HarnessVersion.cs      # harness 版本探测 / npx 解析 / 版本列表拉取
│   ├── ErrorReporter.cs       # 失败/崩溃 Markdown 报告（控制台转录 + 启动诊断）
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
`(NOT FOUND)`，请确认已执行 `npm i -g @deepseek-ai/dsh`，或在 `instances.json`
全局设置 `settings.dshCommand` 中填写 `dsh.cmd` 的完整路径。

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

**Q：为什么变成了一个大文件夹而不是单个 exe？**
WinUI 3 + 自包含 Windows App SDK 的固有代价（约 120 MB）。用
`build.ps1 -Portable` 构建可连 .NET 运行时一并自包含，拷贝即用。

**Q：可以在沙箱/受限环境运行自检吗？**
可以，自检命令支持 `--noredirect`，在禁止管道重定向的环境中也能运行。

## 许可证

[MIT](LICENSE) © 2026 [DreamRift](https://github.com/DreamRift)

DeepSeek Harness 及其 `@deepseek-ai/dsh` 属 DeepSeek AI 所有，遵循其自身许可证。
应用图标取自 `dsh-web-frontend` 的官方 favicon（鲸鱼）。
