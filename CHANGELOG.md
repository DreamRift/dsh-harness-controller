# Changelog

本项目遵循 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/) 风格，
版本号遵循 [Semantic Versioning](https://semver.org/lang/zh-CN/)。

## [2.0.0] - 2026-09-05

> 计划全文见 `docs/REFACTOR-2.0-PLAN.md`。目标：实例档案子系统 + 代码精简 + 便于迭代的界面架构。

### P0 地基（已完成）

- **工程拆分**：单工程 → `DshController.slnx` 三工程（`src/DshController.Core` 纯逻辑类库、
  `src/DshController.App` WinUI 3 可执行、`tests/DshController.Tests` 离线单测）。
  产物名与 CLI 参数不变，仍是 `DshController.exe`。
- **Core 与 UI 解耦**：新增 `IUiDispatcher` 抽象取代 Core 里的 `DispatcherQueue` 依赖，
  Core 现为 `net10.0` 纯托管库（零 WinUI 引用），xUnit 可直接引用做毫秒级离线测试。
- **状态目录统一**：`instances.json` 等状态从 exe 旁迁到 `%LOCALAPPDATA%\DshController\`
  （本机此前存在 5 份互不相通的副本）；首次运行自动从 exe 旁导入（只复制不删除），
  `portable.marker` 可切回便携模式；目标为空清单而源非空时会接管并备份，避免"先跑新构建再部署"丢清单。
- **单源化**：新增 `AppPaths`（全部落盘位置）与 `AppVersion`（版本号读程序集），
  消除报告目录、HOME 根、`~/.dsh`、版本号等 6+ 处重复定义；`build.ps1` 的 zip 版本号也从 csproj 解析。
- **质量门禁**：新增 `tools/check-conventions.ps1`（文件行数上限 / Core 禁 UI 引用 / 裸 catch 必须写理由 /
  async void 限事件处理器 / App 层禁同步阻塞），构建前执行、违规即失败，历史欠账登记在
  `tools/conventions-allow.txt` 并随阶段清零；`build.ps1` 现在会先跑机检与离线单测再构建。
- **测试**：首批 36 条离线断言（路径净化、版本解析与规范化、实例配置归一、市场记录读写、
  状态目录与迁移边界），`dotnet test` < 1s。

### P1 实例档案子系统（已完成）

- **实例档案（Instance Archive）**：每个实例一份 `%LOCALAPPDATA%\DshController\archives\<id>.json`，
  界面上关于实例的信息**统一从档案读取**，不再各页面现场扫描。四条不变量：
  单飞（同一实例同一分面并发只采一次）、失败不覆盖成功数据、删除只标记退役（档案永久保留）、落盘去抖。
- **分面（facet）与各自的刷新间隔**：`identity`（清单镜像，零成本）、`liveness`（前台 2s / 后台 20s）、
  `harness`（24h）、`plugins`（6h）、`home`（24h）、`wslEnv`（1h）、`usage`（预留给 P2：运行中 30min / 停止 6h）。
  全部可在「应用设置 → 数据刷新」调整，0 = 只在手动刷新或相关操作后更新；带总开关。
- **统一调度器**：全应用一个 1 秒评估循环取代原来两个面板各自常驻的轮询定时器；
  当前可见实例优先、全局并发上限 2、WSL 类采集串行（wsl.exe 往返昂贵）、全程可取消。
- **事件驱动失效**：实例启动成功 → 版本/HOME/插件/WSL 环境失效；插件装卸升级 → 插件分面失效；
  实例设置变更、实例删除同理。界面在新数据到达前继续显示旧值 + "上次成功于"。
- **插件市场 / 插件管理两页改为读档案**：冷启动即有版本与已装列表（此前每次进入页面都要重扫 HOME、
  每次重启应用都要重探版本）；"刷新"按钮改为强制重采。
- **新增 CLI `--archive-check [--instance <id>] [--facet <名>] [--force]`**：打印各实例各分面的
  状态/新鲜度/耗时/来源/错误，并补采从未采集过的分面；同时列出已退役档案。
- **测试**：档案子系统 38 条新断言（仓库读写与损坏兜底、TTL 三态、失败不覆盖、空结果覆盖、
  跳过记原因、单飞合并、去抖落盘、代际与退役、调度优先级与 WSL 并发预算、策略映射），
  累计 **74 条离线断言**，`dotnet test` < 0.3s。
- **过程中修掉两个真实缺陷**（由新测试发现）：① 单飞字典在"采集同步完成"时清理早于登记，
  导致完成态 Task 永久驻留、后续请求全部拿到陈旧快照；② 刷新间隔按分面名匹配时先 `ToLowerInvariant`
  再比常量，`wslEnv` 永远匹配不上（该分面从不自动刷新）。

### P2 用量统计并入档案（已完成）

- **token 用量成为档案的一个分面**：原型分支（`test-usage-stats`）的统计核心移植进 Core 并拆成
  `Usage/UsageModels|UsageParser|UsageScanner|UsageQuery`；口径与官方 token-meter 一致
  （四分桶、(turn,step) 去重、`uncachedInputTokens` 缺失回退 `inputTokens`、模型名取最近一次 request/header）。
  数据源：`storages/session_projcache.json`（权威总账）+ `sessions/**/session.jsonl.zstd`（按模型/按天）。
- **合并时做掉的三处欠账**：① 原型里"按范围聚合"复制了 4 份 → 收敛为唯一的 `UsageQuery.Summarize`；
  ② 内存/持久化两套 DTO 逐字段镜像 → 合成一套（少一层拷贝也少一处漂移）；
  ③ 会话样本缓存无上限 → 加容量上限与最久未用淘汰。
- **WSL 扫描改两段式**：先一次往返只列出 (size, mtime, path)（输出极小），再只对变化过的文件分批取内容；
  没有变化时第二次往返都不发——原型是每次把整个 sessions 目录 base64 搬一遍。
- **历史用量不丢**：新增 `--import-usage [路径]` 导入原型的 `usage-backup.json`，
  **清单里已不存在的实例照样建档并标记退役**——本机实测导入 2 个已删除实例的历史用量。
  应用启动时也会自动尝试导入一次（幂等，已有更新数据的实例跳过）。
- **用量统计页（MVVM 样板）**：新增侧边栏「用量统计」页 —— KPI（总 token/请求/会话/缓存命中率/活跃天/热门模型）、
  每日柱状图、按模型排行、会话明细，支持"全部实例（含已删除）/单实例"与 7 天/30 天/全部范围切换。
  该页 code-behind 只剩注入与生命周期转发，状态全部经 `x:Bind` 绑定。
- **新增 ViewModels 工程**（`src/DshController.ViewModels`，net10.0 不依赖 WinUI）：视图模型只认
  `IArchiveFacade` 接口，于是 UI 逻辑第一次可以离线单测（范围切换、KPI 文案、空态、刷新命令）。
  这是 P3 拆解现有面板的前置条件。
- **测试**：用量解析/聚合/帧协议/范围查询/视图模型共 34 条新断言，累计 **108 条**，`dotnet test` < 0.2s。
- **原型归档**：`test-usage-stats/`（234 MB，含 bin/obj 与一份完整工程副本）已移除，
  源码留档在 `legacy/usage-prototype-2026-08/`，从此不会再被任何构建吸进去。

### P3 界面重构主体（已完成）

- **两个插件面板的重复代码归零**：目标实例 / profile 归一 / 版本文案 / 已装状态 / 忙态 /
  过期结果丢弃，此前在市场页与管理页**逐字重复 14 个方法**，现在收敛为 ViewModels 层的
  `PluginTargetViewModel`（可离线单测）；确认与提示对话框收敛为 `DialogService`（原 ≥10 处同形状代码）；
  安装/升级/卸载的"跑命令 → 判失败 → 让档案失效 → 提示重启"收敛为 `PluginOpsService`。
  插件管理页 507 → **321 行**，插件市场页 987 → **846 行**。
- **InstancePanel 从 1741 行的上帝面板拆成 8 个 partial 文件**（主文件 340 行，其余
  Wiring 96 / Version 260 / Status 265 / Reports 181 / Create 351 / Scan 315 / Settings 191），
  **全部 ≤400 行**；其中纯逻辑抽到可测的 `InstanceSettingsValidator`（端口边界、主机空值、
  版本号规范化、trusted-hosts 拆分）与 `InstancePlanFactory`（id 生成、工作区/HOME 回退规则）。
  顺带修掉一个体验问题：新实例 id 不再一律带随机后缀（`web-dev` 而不是 `web-dev-a1b2c3`）。
- **删除两个常驻 1 秒轮询定时器**（页面隐藏也照跑）：在线状态改由档案的 `liveness` 分面驱动，
  统一调度器按"可见 2s / 后台 20s"采集，面板只订阅结果；用户操作后仍会立即主动探一次。
- **日志管线增量化**：原来每 100ms 把整段 20 万字符转录重新赋给 TextBox.Text（UI 线程 O(n) 重建），
  现在是虚拟化列表按行追加（O(1)）；缓冲与裁剪抽成可测的 `LogBuffer`。
- **约定机检欠账清零**：`tools/conventions-allow.txt` 中 InstancePanel 的 R1/R3 例外全部删除，
  当前 `check-conventions: PASS`（90 个文件，0 违规）。
- **测试**：插件目标视图模型 11 条 + 实例设置/新建计划 17 条 + LogBuffer 6 条，累计 **153 条**。

### P4 打磨与守则（已完成）

- **设计台（Dev Gallery）**：`--dev` 启动后侧边栏出现「设计台」——全部颜色令牌、排版、按钮/输入/卡片样式、
  实例状态四态、档案新鲜度徽标、空态与忙态，全部用假数据摆在一页。WinUI 3 的 XAML 热重载依赖
  Visual Studio（命令行构建没有），所以改样式的回路定为「改 XAML → 15 秒 Debug 构建 → 看设计台一页」，
  不必再制造真实运行条件（失败态、WSL 未运行…）。
- **架构文档与决策记录**：新增 `docs/ARCHITECTURE.md`（分层与依赖方向、档案数据流、落盘位置表、
  线程模型、界面结构、质量门禁、UI 迭代方式）与 `docs/adr/` 六篇决策记录（工程拆分、状态目录、
  实例档案、用量并入、不承诺热重载、把守则变成构建门禁）。
- **裸 catch 清账（部分）**：本轮重构触碰过的文件全部清干净——实例页 8 个 partial、两个插件面板、
  MainWindow 的日志管线、Core 新增的档案/用量/存储模块；`tools/conventions-allow.txt` 里
  InstancePanel 的 R1/R3 例外整组删除。**仍有 87 处裸 catch 留在未重写的 Core/CLI 老文件里**
  （BackendManager、PluginCatalog、WslTools、Cli 等），它们在例外清单里逐条登记、构建时可见，
  规则是"下次触碰该文件即清理"——不是被忽略，而是被记账。
- **发布 2.0.0**：`build.ps1` 现在先跑约定机检与离线单测再发布；产物
  `publish-fixed\DshController-2.0.0-win-x64.zip`，版本号由 csproj 单源提供。

### 2026-09 整改轮（六项，收口对账见 `docs/closing-report-2026-09.md`）

- **重启绝不拉浏览器**：根因是子进程 `dsh web` 自开窗口、控制器侧抑制拦不住；现对 Windows/WSL
  三条启动分支统一注入 `web --no-open`，重启后只开控制器自己的控制台页（`RestartNoBrowserTests` 7 条）。
- **用量页按定稿方案改版**（D1-A/D2-A/D3-A/D4-A）：日聚合合并（`MergeDaily`/`DayComposition`）、
  钻取行选中联动与清除闭环、范围切换经 `IArchiveFacade.IsRunning` 闭环；`UsageView` 整页重写。
- **全局中度精简**：四页头与面板 11 处静态长文删除，说明改 tooltip 承接。
- **按钮遮挡治理**：10 处缩字 + 抖动消除；主题新增 `BtnInline`/`SettingHint` 令牌；960/1280/1600
  三档宽度走查（见 `docs/audit-button-trim.md`）。
- **设置两区分节卡片化**：应用设置页 2 卡 → 3 卡 13 行，实例设置折叠区 1 卡 → 5 卡 13 行
  （纯 XAML 改版，读写逻辑零改动）。
- **质量欠账清还**：裸 catch 103 处清零（3 处改日志承接、其余写明理由）；`BackendManager`/
  `PluginCatalog`/`PluginMarketPanel` 拆为 ≤400 行 partial；例外台账 R3 25→0、R1 9→5、R4 2→0。
- 离线单测 153 → **176**；`--selftest-core` **89/0**、`--selftest-plugins` **61/0**。

### 2026-09-04 顶栏四页改版轮（档案页 + API 预设页）

- **顶栏四页壳**：`MainWindow.PageHost` / `SplitPageShell` 改版，实例页、插件页左栏、升级治理接续回填。
- **新增档案页**：退役实例列表、元信息一览、用量看板并入（红队 pass）、跨实例总计行、
  档案改名（`AliasStore` 别名支路，改名实时生效）。
- **新增 API 预设页**：`ProviderPresetStore` 持久化、供应商编辑页、同步预览窗（取消零写）、
  写入生效（行级合并 + 原子备份，红队 pass）、新建实例页"同步预设"勾选。
- **供应商同步配置格式对齐**：`ProviderConfigMapper`，字段对照表见 `docs/provider-sync-alignment.md`。
- **验证基建**：`verify/` 收录 12 个 GUI 探针脚本（纯 ASCII、自恢复）；`docs/evidence/` 收录 141 张
  目检截图；`docs/TEST-RESULTS.md` 建立测试账、探针表与开放票去向。
- 离线单测 220 → **275**；约定机检 **142 文件 PASS**；GUI 探针 12/12 全量回归。

### 修复

- **自检 `[7] 迁移生成 default 实例` 的环境依赖红条**：`--selftest-core` 此前在 bin 目录借用真实
  `instances.json` 做 fixture，且 `InstanceRegistry.Load()` 会把本机正在运行的后端自动发现进清单，
  导致"迁移应只生成 1 个实例"的断言随环境漂移。现在自检全程跑在临时状态沙箱里，
  并新增 `InstanceRegistry.Load(discoverRunningInstances:false)` 保证确定性——本机结果由 84/1 变为 **85/0**。

- **插件管理 / 插件市场首次进入不显示实例 harness 版本**：版本探测此前只挂在
  目标实例下拉框的 `SelectionChanged` 上，而首次进入页面时的默认实例是在
  `_loadingInstances` 屏蔽期内选中的（该事件不触发，或因实例 id 未变被跳过），
  信息条一直停在"harness 版本未检测"，必须手动在 Windows / WSL 实例之间切换
  一次才会探测出来。现把探测并入统一的加载流程（`EnsureVersionAsync`，按实例
  id 记录已探测状态），首次进入即显示默认实例版本；插件市场同时补上首次进入的
  已装状态读取（"已安装"标记与兼容性提示不再需要切换实例才出现）。
- 版本探测与 HOME 读取改为并行，WSL 实例的慢探测不再拖住插件列表渲染；
  探测中信息条显示"正在检测 harness 版本…"，"版本未检测"只在真的探测失败时出现；
  两页的「刷新」按钮现在会一并失效版本缓存重新探测。
- **主工程无法构建**：嵌套的独立工程副本 `test-usage-stats\`（自带 csproj）被 SDK
  默认通配符吸进主工程，导致成百上千个"重复定义"编译错误；已按 `legacy\` 同样
  的方式在 csproj 中排除。

## [1.0.0] - 2026-08-29

> 首个正式版本：现代化控制台界面、统一主题系统与更稳定的运行时体验。

### 变更

- 重设计主窗口、实例管理、插件市场、插件管理与应用设置界面。
- 控制台日志改为批量刷新，降低高频输出时的 UI 卡顿。
- 修复插件管理首次进入时默认实例未同步，导致显示“没有实例”的问题。
- 发布版本号统一为 `1.0.0`，发布压缩包命名同步更新。

## [0.6.1] - 开发中（待发布）

> **多来源插件市场 + WSL 已安装实例发现**：修复 v0.6.0 按过时"接口规范"文档
> 解析导致官方源拿不到数据的问题（按线上真实 schema 重写解析器）；市场来源
> 多选 + 合并去重；分类全部中文；WSL 主实例不开机也能被「扫描」发现。

### 修复

- **官方源拿不到数据**（v0.6.0 回归）：线上 plugins.json / market.json 的真实
  结构与早前参考的"接口规范"文档不一致（条目字段为 `npm`/`description:{zh,en}`/
  `full_name` 而非 `pkg`/`desc`/`repo`），解析器对不上真实字段导致列表不可用。
  已按实测 schema 重写为自适应解析（官方快照 / 精选快照 / GitHub 搜索 / 旧规范
  四种形态自动识别），并对真实数据端到端验证

### 新增

- **市场多来源**：内置三个来源可多选（「插件市场 → 数据源」）——官方全量目录
  （2400+，中文简介/npm 包名/官方中文分类）、GitHub 精选快照（600，经 jsDelivr
  CDN 镜像 GitHub 仓库数据，直连 GitHub 不可达也可用）、GitHub 实时搜索
  （api.github.com topic:dsh-plugin，最新但未审核，失败不影响其他来源）；
  自定义源 URL 保留（全局设置「自定义市场源」）。各来源独立缓存、独立回退，
  状态栏显示每个来源的成功/失败/缓存兜底
- **多来源合并去重**：npm 包名 与 owner/repo 双键并组（同一插件只要 npm 相同
  或仓库相同就合并），变体字段取最优（npm 包名优先保留 > 已审核 > 中文简介 >
  star）；卡片显示"N 源"徽标，详情里列出各源条目并可**选择从哪个源的条目安装**
- **GitHub 实时来源的未审核条目**：卡片带"未审核"徽标，安装确认弹窗额外警告
- **WSL 已安装实例发现**：「扫描」在"运行中后端"之外，还会检测**已安装 dsh 但
  未运行**的发行版（主实例不开机也能被发现），弹窗询问后添加为实例（默认
  ~/.dsh，端口自动分配）——修复"打开 WSL 扫描却发现不了默认安装实例"的问题
- **分类全部中文**：分类下拉按数据动态构建，标签取来源官方中文分类
  （plugins.json categories.zh / market.json category_zh）+ 内置映射，
  未知代码原样显示不编造；卡片与详情同步中文
- **CLI 自检**：`--catalog-check [--all]`——联网拉取全部启用来源并解析合并，
  打印各来源状态/条目数/合并结果，用于验证数据源可达性

### 变更

- 版本号 0.6.0 → 0.6.1；`--selftest-plugins` 扩至 61 项断言（新增四形态解析/
  合并去重/分类映射/RepoFromUrl/安装探测解析）

## [0.6.0] - 开发中（待发布）

> **插件市场与插件管理**：侧边栏新增「插件市场」「插件管理」两个页面——
> 从社区目录搜索插件，按 DSH 官方方式（`dsh plugin --profile <p> add`）安装到
> 指定实例；已装插件可升级、卸载。插件随实例 DSH_HOME 落盘，实例间天然隔离。

### 新增

- **插件市场页**：数据源为 awesome-dsh-plugin 社区目录（每日抓取 GitHub
  `dsh-plugin` topic 并人工复核；默认源 `awesome-dsh-plugin.com/plugins.json`，
  全局设置可更换兼容镜像/自建源）。支持中英文关键字搜索、分类过滤、
  热度/更新时间排序、"仅可安装/仅人工复核"开关；目录数据本地缓存 24 小时，
  网络失败自动回退过期缓存兜底
- **支持版本展示**：插件仓库明确声明了支持的 DSH 版本（目录 `minHost` 字段，
  缺失时兜底查 npm registry 包元数据的 peerDependencies/engines 并标注来源）
  时原样展示"支持 DSH ≥ x.y.z"，并与所选实例的 harness 版本比对给出
  兼容 / 低于要求 / 未检测三态提示；仓库未声明则如实显示"未声明"，不做推测
- **官方方式安装**：卡片一键安装严格调用 `dsh plugin --profile <p> add
  <npm包名 | github:owner/repo>`（与 BackendManager 同一套 dsh 解析与
  DSH_HOME 注入；WSL 实例在发行版内执行）；安装前确认弹窗包含目标实例、
  HOME 路径、共享默认 ~/.dsh 的隔离警告与未初始化 HOME 提示；安装输出实时
  打入共享控制台；安装成功后对比实例 HOME 包集合新增市场安装记录，实例
  运行中可一键重启使 bundle 插件生效
- **插件管理页**：读取实例 HOME 的 `profiles/<profile>/package.json`
  （dependencies + dsh.profile.bundles）与 node_modules 版本号，真实展示
  已装插件（bundle 生效标记、来源徽标：市场安装/官方基础包/手动安装/本地链接）；
  升级与卸载走官方 `dsh plugin update/remove` 命令（官方基础包禁止操作），
  危险操作二次确认，卸载同步清除市场记录
- **新增核心层**：`HttpFetch`（首个联网点，单例 HttpClient + 系统代理 +
  独立超时）、`PluginCatalog`（目录拉取/双源回退/缓存/过滤）、`PluginCompat`
  （版本声明提取与 semver 比较）、`PluginInstaller`（官方命令封装，Windows
  spawn + WSL 发行版内执行，目标白名单校验规避 pnpm/cmd 含空格路径拆断坑）、
  `InstalledPlugins`（HOME 黑盒读取）、`PluginRecords`（按实例分文件的市场
  安装记录）
- **CLI 自检**：`--selftest-plugins`——目录解析/过滤排序/版本兼容判定/市场
  记录读写/profile package.json 解析/命令拼装与目标校验，44 项断言全离线运行
- **全局设置**：新增「插件市场源」配置（留空 = 官方默认源）

### 变更

- 版本号 0.5.1 → 0.6.0（csproj / ErrorReporter.AppVersion / build.ps1 三处同步）
- README 新增插件市场与插件管理章节、自检命令与项目结构说明

## [0.5.1] - 开发中（待发布）

> **侧边栏界面改版**：主窗口从 TabView 双标签页改为 NavigationView 左侧边栏
> （Windows 实例 / WSL 实例 / 全局设置 三个页面），修复内容被遮挡/挤压的问题；
> 控制台改为可收起的底部坞，默认窗口尺寸加大并设置最小尺寸。

### 新增

- **手动扫描运行中实例**：实例列表行新增「扫描」按钮——一键扫描本环境中
  "正在运行但未注册"的 harness 实例并加入列表（Windows 经 netstat + 进程
  命令行识别；WSL 进发行版内 pgrep + /proc 解析，对 GUI 启动之后才在
  WSL 终端手动启动的实例同样有效）。发现项不立即落盘，编辑保存后持久化；
  扫描无结果时提示排查方向，空状态提示补充"点「扫描」发现运行中实例"指引，
  README FAQ 新增 WSL 实例发现机制说明（触发时机/发行版回收/默认端口注意）
- **侧边栏导航**：Windows 实例 / WSL 实例 / 全局设置 三个页面（Windows 11 设置
  同款 NavigationView 控件）；实例面板常驻不销毁，切换页面仅改可见性，
  选中实例、状态轮询与版本探测不中断；侧边栏导航项沿用标签页头的实例数/
  运行中统计
- **全局设置独立页面**：由窗口底部 Expander 移入侧边栏页面（卡片布局、
  最大宽度居中），不再挤压实例内容区
- **控制台可收起**："隐藏 / 显示"按钮一键收起日志区为实例页腾出空间，
  收起时页码获得全额高度
- **窗口尺寸**：默认 1180×800（原 920×860，侧边栏布局下加宽减高）；
  设置最小尺寸 960×620 防止极端缩放导致控件重叠；主题按钮按系统标题栏
  实际内边距（TitleBar.RightInset）精确定位，不再机械留白 150px
- 底部全宽状态栏：上细分隔线 + 实例统计 / harness 版本 / 报告目录 / 应用版本

### 修复

- **WSL 实例无法自动发现（v0.5.0 回归）**：发现逻辑此前依赖 Windows 侧
  `wsl.exe` 宿主进程命令行（`-d <发行版> … dshwsl-<port>.sh`），而新版商店版
  WSL（2.x）中 `wsl.exe` 启动后即退出、由 `wslrelay.exe`（命令行仅含
  `--vm-id/--handle`）接管端口转发——宿主命令行不复存在，正则永远匹配不到；
  转发端口的监听进程又是 wslrelay，Windows 分支同样无法识别。现改为
  **发行版内直接探测**：枚举运行中发行版 → `pgrep` harness 进程 →
  `/proc/<pid>/{cmdline,environ,cwd}` 反推端口/DSH_HOME/工作区（单次 bash
  往返完成），不依赖任何 Windows 侧进程信息；旧版 WSL 的宿主命令行扫描
  保留为回退。发行版内探测对"WSL 终端手动启动的实例"同样有效。
  发现门控同步放宽：存在运行中发行版时也触发完整探测（防转发端口因网络
  模式差异不在 netstat 呈现时漏扫）
- **启动死锁（窗口不出现）**：Load() 在 UI 线程被 OnLaunched 同步调用，
  而 Program.Main 为 UI 线程安装了 DispatcherQueueSynchronizationContext——
  在 UI 线程直接对 WSL 探测异步任务 GetResult() 阻塞时，其内部 await 续体
  投递回被阻塞的 UI 队列，形成经典 SyncContext 死锁（进程存活、窗口永不
  出现、无任何崩溃报告）。现把发现探测整体放入 Task.Run（线程池无
  SyncContext，GetResult 安全），UI 线程仅等待最终结果
- **默认端口实例扫描不到**：发行版内探测此前只认命令行显式 `--port` 参数，
  `dsh web` 裸启动（用默认端口）的实例被跳过；现对无 `--port` 的进程用
  `ss -tlnp` 按 pid 反推实际监听端口
- **跨环境同端口误去重**：WSL 与 Windows 是独立网络空间，两边各跑一个
  3080 是合法并存的两实例；发现去重键从"端口"改为"(运行环境, 端口)"
  （注册表、扫描按钮、Scan 内部三处同步修正）
- **内容遮挡**：v0.5.0 的 TabView + 固定 200px 日志 + 全局设置 Expander 在
  小窗口下会把实例内容挤压得难以使用；现实例页独占内容区，控制台默认
  168px 且可收起，任何窗口尺寸下都不再互相遮挡
- **高 DPI 下提示行重叠风险**：InstancePanel 内三处 hint 的负上边距
  （-6px）在字体缩放 150%+ 时可能与上方输入行重叠，归一化为 0

## [0.5.0] - 开发中（待发布）

> **Win/WSL 双界面 + harness 指定版本 + 失败报告落盘修复**：
> Windows 实例与 WSL 实例分别在两个独立标签页中管理（不再混用同一个实例下拉框）；
> 每个实例可指定 harness 版本启动（默认跟随当前环境主实例版本）；
> 后端启动失败时，控制台报错信息 + 启动诊断 + 实例信息 + 时间在核心层直接生成报告文件，
> 保存到用户指定的目录（不再依赖界面事件链路）。

### 新增

- **Win/WSL 双界面**：主窗口改为 TabView 双标签页——"Windows 实例"与"WSL 实例"
  各挂一个独立面板（`InstancePanel`），实例列表/状态/操作/设置互不混用；
  每个面板独立记忆选中实例、独立状态轮询、独立版本探测；标签页头显示
  各环境实例数与运行中数量；共享控制台带 `[WIN·名称]`/`[WSL·名称]` 前缀
- **harness 指定版本**：实例新增 `harnessVersion` 字段——空 = 跟随当前环境主实例版本；
  非空 = 经 `npx --yes @deepseek-ai/dsh@<版本> web ...` 拉取该版本启动
  （Windows 用 npx.cmd，WSL 用发行版内原生 npx；npx 缺失时给出明确失败报告）。
  指定版本时**不再要求本机/发行版已装 dsh**（只需 node/npm）
- **当前环境版本探测**：新增 `Core/HarnessVersion.cs`——Windows 读 npm 全局包
  package.json / 执行 `dsh --version`；WSL 在发行版内执行 `dsh --version`（node 兜底）；
  实例设置提供"检测当前版本"按钮，状态卡与页脚显示生效版本（指定/当前环境）
- **版本列表拉取**：实例设置新增"拉取版本列表"按钮，经 `npm view @deepseek-ai/dsh versions`
  （Windows 侧或发行版内）列出已发布版本供直接选择；离线时仍可手工输入
- **版本号校验**：保存/新建时统一规范化（去 `v` 前缀、允许 `x.y.z[-预发布]`），
  非法输入不会写入配置，控制台给出明确提示
- **新建实例默认版本**：新建/克隆对话框默认"跟随当前环境（v当前版本）"，
  下拉同时提供"指定为当前环境版本"与已发布版本列表；克隆现有实例时默认继承源实例设置
- **失败报告核心层生成**：`BackendManager.FailStart/FailStop` 直接调用 `ErrorReporter`
  落盘（不再依赖 UI 事件订阅），报告文件名 = `DshController-fail_<实例ID>_<时间戳>.md`
  （停止失败为 `stopfail_`）；内容含实例名称/ID/运行环境/端口/DSH_HOME/harness 版本/
  生成时间 + **控制台转录** + **启动诊断表**（发行版列表、发行版用户、Linux $HOME、
  dsh/npx 路径、工作区解析结果、真实命令行）+ 子进程输出转录 + 排障建议；
  控制台同时打印报告路径；CLI `--instance start` 失败同样自动生成报告
- **失败提示条（InfoBar）**：面板顶部内联提示失败类型与报告路径，
  提供"打开报告 / 打开报告目录 / 复制报告路径"，替代打断式弹窗
- **WSL 环境专属设置**：WSL 标签页可单独设置"停止后关闭"策略
  （`smart` / `distroOnly` / `always` / `never`），并提供"扫描发行版"下拉
- **实用小按钮**：推荐空闲端口、打开工作目录、打开 DSH_HOME、复制日志

### 改进

- 界面重构：状态卡（状态点 + 环境徽章 + harness 版本徽标 + 地址 + HOME + 启动方式 + PID）、
  实例下拉显示"名称 · 端口 · 指定版本"、实例设置按"网络/路径/harness 版本/高级"分组、
  空状态卡直接给出"新建实例"入口；全局设置报告目录新增"打开"按钮
- 报告可读性：`json` 配置节改为合法 JSON（转义反斜杠/引号）、WSL 实例不再打印
  无关的 Windows 侧 dsh 4 级回退表、工作目录存在性按运行环境判断、
  排障建议按 WSL/版本/端口/工作区分类
- CLI：`--instance <id> start` 改为**等待就绪或失败**再返回（退出码 0/1，
  失败时打印报告路径）；`--check` 增加 runtime/harness 版本列、Windows 与各
  WSL 发行版的当前环境版本、报告目录存在性
- 新建 WSL 实例默认使用 Linux 原生工作区（`~/dsh-workspaces/<id>`）与
  独立 `DSH_HOME`（`~/dsh-instances/<id>`），避免多实例共用 `~/.dsh`

### 修复

- **XAML 编译失败（阻断构建）**：`TabView` 不存在 `CanCloseTabs` 属性、
  XML 注释中出现非法的 `--` 连字符串——WASDK 1.5 的 XamlCompiler 遇到二者会
  **静默崩溃（exit 1 且无任何诊断输出）**，导致整个项目无法构建；已改为
  `TabViewItem.IsClosable` 与合法注释
- **WSL 关闭策略未被遵守**：`SmartShutdownAsync` 此前无论策略如何都会终止发行版，
  `distroOnly` / `never` 形同虚设；现四档策略严格生效（`never` 完全不动发行版与 VM）
- **停止 WSL 实例可能误杀无关进程**：pkill 兜底模式 `[p]ort <N>` 会匹配任何命令行
  里含"port N"的进程；收窄为 `(@deepseek-ai/[d]sh|[d]sh).*--port <N>`
- 公告 URL 捕获：无捕获组的正则误用 `Groups[1]` 导致 `dsh web: http://...` 捕获失效

### 版本号统一

- csproj / ErrorReporter / build.ps1 三处版本号统一为 0.5.0

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
