# 用量界面设计调研（research-usage-designs）

> 目标：为 DshController（WinUI 3 桌面应用，位于 `dev`）的「Token 用量统计页」收集可落地的布局 / 指标 / 交互要点。
> 约束：数据是**离线**从本机会话日志聚合（档案 usage 分面），无云端 API、无实时流、按实例（多 DSH 后端实例）。本调研只读源码，未改任何代码。

---

## 0. 本页数据底座（从源码读出的可用字段清单）

下面字段全部来自 **档案 usage 分面**（`UsageFacetData`，由 `UsageCollector` 采集后持久化，页面经 `ArchiveHub` 读取）。先摸清"手里有什么"，后面每一处"有数据 / 无数据"的结论都以此为准。

### 0.1 四桶（TokenBuckets）——核心口径
`dev/src/DshController.Core/Usage/UsageModels.cs` L19-34
- `UncachedInput`（未缓存输入）· `CacheRead`（缓存读）· `CacheWrite`（缓存写）· `Output`（输出）
- `Total` = 四桶之和（L34）
- `CacheHitRate` = cacheRead / (uncachedInput + cacheRead)，无输入返回 -1（L36-45）
- 口径注释（L8）：与官方 `@deepseek-ai/dsh-token-meter` 对齐，四分桶 = 未缓存输入 / 缓存读 / 缓存写 / 输出；按天分桶用本地日期（yyyy-MM-dd）。

### 0.2 会话级（UsageSessionStat）
`UsageModels.cs` L78-97 — `SessionId`·`Title`·`CreatedAtMs`·`Cwd`·`Turns`·`Totals`。
（来源：`<HOME>/storages/session_projcache.json`，权威总账，见 `UsageParser.cs` L26-77 与 `UsageCollector.cs` L5-6。）

### 0.3 模型级（UsageModelStat）
`UsageModels.cs` L100-135 — `Provider`·`Model`·`Requests`·`FirstMs`·`LastMs`·`Totals`·`Daily`（Dictionary<string,TokenBuckets>）·`DailyRequests`（按天请求数）·`DisplayName`·`FullName`。
（来源：`<HOME>/sessions/**/session.jsonl.zstd` 逐请求 usage 解析，见 `UsageParser.cs` L80-196 与 `UsageScanner.cs`。）

### 0.4 分面 / 汇总（UsageFacetData / UsageSummary）
`UsageModels.cs` L138-172 — `Home`·`Totals`·`SessionCount`·`RequestCount`·`ModelsComplete`·`ModelScanError`·`ScannedFiles`·`Models`·`Sessions`（上限 `MaxSessions = 500`，L171）。
`dev/src/DshController.Core/Usage/UsageQuery.cs` L17-37 — `UsageSummary`：`Totals`·`SessionCount`·`RequestCount`·`ActiveDays`·`ModelsComplete`·`TopModel`·`Models`·`Sessions`·`Daily`·`DailyRequests`·`CacheHitRate`。
聚合函数 `Summarize(sources, fromDay, toDay)`（L45-106）：`fromLocal/toLocal` 为 null = 不限；指定范围时按每日聚合。`FormatTokens`（L149-154）输出"亿 / 万 / N0"。

### 0.5 现有页面已经"用到了"哪些（View / ViewModel）
`dev/src/DshController.ViewModels/UsageViewModel.cs`：范围下拉 `Scopes`（含"全部实例 + 已删除档案"L74-93）· 范围 `RangeDays`（7/30/全部）· KPI 字符串属性（L54-60：总 tokens / 请求次数 / 会话数 / 缓存命中率 / 活跃天数 / 用得最多的模型）· 按天柱状图 `DailyBars`（L156-180，最多取 `RangeDays>0?RangeDays:60` 天）· 模型排行 `Models`（前 30，L119-120）· 会话明细 `Sessions`（前 200，L123-124）· 强制重采 `Refresh`（L193-224）。
`dev/src/DshController.ViewModels/UsageRows.cs`：`UsageModelRow`（L23-52：Model/Provider/RequestsText/TotalText/InputText/OutputText/CacheText/SharePercent）、`UsageSessionRow`（L54-83：Title/CreatedText/TurnsText/TotalText/Cwd/ToolTip）、`UsageDayBar`（L86-94：Day/ShortDay/Total/HeightRatio/BarHeight/ToolTip）。
`dev/src/DshController.App/Views/UsageView.xaml`：工具栏（范围 + 7/30/全部 + 刷新，L27-57）· KPI 卡片（L62-115）· 完整度提示（L117-118）· 按天柱状图（L120-141）· 模型排行（L143-176，列：Model/Provider/请求/总量/占比）· 会话明细（L178-207，列：Title/时间/轮次/总量 + ToolTip）· 底部状态（L211-224）。

### 0.6 关键结论：页面"手里有什么 / 没什么"
- **能算的**：四桶总览、按天四桶序列（含按天请求数）、按模型/按 provider 聚合（含首末时间）、按会话明细（标题/时间/轮次/cwd/四桶）、活跃天数、模型排行与占比、缓存命中率。
- **不能／没有的**：Cost（成本）无字段；reasoning 拆分无字段（output 是单桶）；context 占用率 / 上下文窗口 / context 组成（system/tools/message）无字段；耗时类（llmMs/toolMs/ttftMs/decodeMs）无字段；step 数无字段；tool 调用数无字段。这些都来自 DSH 的其他投影（见 §2），`UsageFacet` 采集时未落盘。
---

## 1. 布局范式

### 1.1 顶部"视图切换 + 统一 vs 聚焦"双轴 —— ccusage
- 来源：ccusage 官方文档 / 仓库 `https://github.com/ccusage/ccusage`（README：`ccusage daily|weekly|monthly|session|blocks`；文档 `https://ccusage.com/guide/all-reports`）。
- 借鉴什么：**统一视图（Unified）与聚焦视图（Focused）** 双轴。统一 = 跨所有来源一张表，带 `Agent` 列做横向比较（ccusage 把 Claude/Codex/OpenCode 等 18 个 CLI 并到一个表）；聚焦 = 只看一个来源，去掉比较层、展示该来源的细分（ccusage 文档原话："Unified 表带 Agent 列供比较，Focused 视图移除比较层并展示选择来源的更细信息"）。
- 为何适合本页：本页天然是"多实例"（每台 DSH 后端 = 一个 source）。现状 `UsageViewModel` 只有"全部实例 / 单实例"的范围下拉（L74-93），没有"跨实例对比"的横向维度。**把"全部实例"升级为"统一视图"——每个实例一列/一行并行展示 KPI 与四桶，是 ccusage 最有价值的移植点**；单实例则保留现有聚焦视图，可多展示该实例的按天/按模型/按会话。

### 1.2 KPI 卡片横向布局 + 附注口径 —— ccusage / opencode 仪表盘
- 来源：ccusage `https://ccusage.com/guide/daily-reports`（每行/每卡带 Input / Output / Cache Create / Cache Read / Total Tokens / Cost）；opencode-token-dashboard `https://github.com/heimoshuiyu/opencode-token-dashboard` README（Summary Cards：Total / active / input·output·reasoning·cache / request count / runtime）。
- 借鉴什么：把"总 tokens"拆成**可解释的子桶卡片**（未缓存输入 / 缓存读 / 缓存写 / 输出），而不是只给一个总数字；每张卡下方用小字标注**口径来源**（例：本页现有"请求次数 → 来自会话日志"、"会话数 → 会话总账口径"，见 `UsageView.xaml` L82/90）。
- 为何适合本页：本页四桶数据完整（§0.1），但现有 KPI 只展示"总 tokens"一个数字（`UsageView.xaml` L71-76）。把"总 tokens"卡扩展为"输入（未缓存）/ 缓存读 / 缓存写 / 输出"四并排或一枚小环/堆叠条，能立刻把"钱花在哪 / 缓存省了多少"表达出来——这是离线数据里最直接的价值点。

### 1.3 按天"柱状图 + 点击钻取到当天会话" —— opencode-token-dashboard
- 来源：`https://github.com/heimoshuiyu/opencode-token-dashboard` README（"Cache Miss Drill-down：点击某数据点钻入该天的会话与逐消息缓存生命周期"；"Trend Chart — 按天/按小时，7 天按小时、30/90/180/365/全部 按天"）。
- 借鉴什么：趋势图不只是一根"总量柱"，而是**可点击、可下钻**——点击某天柱 → 落到"这一天的会话列表 / 该天按模型构成"。
- 为何适合本页：现有 `DailyBars` 只是静态柱 + ToolTip（`UsageView.xaml` L131-137，ToolTip 仅显示总量）。数据上 `UsageModelStat.Daily` / `DailyRequests` 就有**每天的四桶与请求数**（§0.3），完全撑得起"点一天 → 看当天四桶构成 + 当天模型排行或会话列表"。这是本页"从总览到明细"最有价值的交互闭环。

### 1.4 模型排行"前 N + 横向占比条" —— opencode 仪表盘
- 来源：`https://github.com/heimoshuiyu/opencode-token-dashboard` README（"Model Leaderboard — Top 8 horizontal bar chart"）。
- 借鉴什么：模型排行用**横向条形 + 占比**，前 N 名一眼看出"谁在用、占多少"。
- 为何适合本页：现有模型排行是列表 + 文本占比（`UsageView.xaml` L169-170 `ShareText`）。把每行加一条**按占比的进度条**（`UsageModelRow.SharePercent` 已有，L32-33），可视化比纯文本更符合"排行"心智；top N 也从"前 30"收窄更聚焦。

### 1.5 每实例"聚类 / 分组"段落 —— ccusage `--instances`
- 来源：ccusage `https://ccusage.com/guide/daily-reports`（"Project Analysis：Group usage by project / `--instances` 按项目分组输出"）。
- 借鉴什么：在"全部"视图里按**项目（= 本页的实例）分组**成独立小节，各自一个小表/卡。
- 为何适合本页：本页已有"全部实例 / 单实例"二态，但没有"全部实例横向分组并排"。ccusage 的 `--instances` 提示可以做一个**实例分组网格**（每个实例一张小卡：名称 + 四桶 + 占比），是"按实例管理"这一产品直觉的直接落点。

---

## 2. 指标口径

> 约定：本页"有数据" = `UsageFacetData` 里能取到（§0）;"无数据" = 需依赖 DSH 其他投影或外部定价源，本页拿不到。

### 2.1 有数据可支撑的指标
| 指标 | 数据来源（文件:行号） | 建议呈现 |
|---|---|---|
| 总 tokens | `UsageFacetData.Totals.Total` / `UsageSummary.Totals.Total`（UsageModels.cs L34；UsageQuery.cs L19） | KPI 卡（现有） |
| 未缓存输入 / 缓存读 / 缓存写 / 输出 | `TokenBuckets` 四字段（UsageModels.cs L21-31） | KPI 子桶卡或堆叠条（新增） |
| 缓存命中率 | `TokenBuckets.CacheHitRate`（L36-45；无输入 -1） | KPI 卡（现有 `HitRateText`） |
| 请求次数 | `UsageSummary.RequestCount` / `UsageModelStat.Requests`（UsageQuery.cs L87；UsageModels.cs L109） | KPI + 模型行（现有） |
| 会话数 / 轮次 | `Summary.SessionCount` / `UsageSessionStat.Turns`（UsageQuery.cs L21；UsageModels.cs L93） | KPI + 明细列（现有） |
| 活跃天数 | `Summary.ActiveDays`（UsageQuery.cs L102：Daily 中 Total>0 的天数） | KPI（现有） |
| 按天总量 & 按天请求数 | `Summary.Daily` / `Summary.DailyRequests`（UsageQuery.cs L29-33） | 柱状图 + 下钻（§1.3） |
| 按模型：provider/model/首末时间/请求数/四桶/按天 | `UsageModelStat` 全套（UsageModels.cs L100-135） | 模型排行 + 每模型下钻 |
| 最常用模型 | `Summary.TopModel`（UsageQuery.cs L103） | KPI（现有） |
| 完整度 | `UsageFacetData.ModelsComplete` / `ModelScanError` / `ScannedFiles`（UsageModels.cs L154-160） | 页顶提示（现有 `CompletenessText`，UsageView.xaml L117-118） |
| 每个实例的 home | `UsageFacetData.Home`（UsageModels.cs L141） | 实例卡/空态里可展示（可加） |
| 会话明细（标题/时间/轮次/cwd/四桶） | `UsageSessionStat`（UsageModels.cs L80-96） | 明细列表（现有，ToolTip 已含四桶） |

### 2.2 无数据、须放弃/降级的指标（附理由）
- **Cost（成本，USD）**：ccusage 用它做核心列（daily/session 报表），但需从 LiteLLM / models.dev 定价目录按 model+timestamp 现算（`https://ccusage.com/guide/cost-modes`：`--mode auto|calculate|display`，`totalCost = input*inPrice + output*outPrice + cacheCreate5m*... + cacheRead*cacheReadPrice`）。**本页 `UsageFacetData` 无任何 cost 字段，且离线无定价源** → 除非自建"每模型离线价目表"，否则**放弃**。若要，可做"可选 + 本地价目表"且务必标注为估算（本页不含，建议列入排除项 §4）。
- **reasoning 拆分**：opencode 仪表盘把 input/output/**reasoning**/cache 分开（README Summary Cards）。DSH 官方 token-meter 注释"Reasoning remains an output subdivision"（`dsh-token-meter/README.md` L22/L28），本页 `Output` 是单桶 → **无 reasoning 字段，放弃**。
- **context 占用率 / 上下文窗口 / context 组成**：来自 token-meter 的 `contextPressure`（projectedTokens / pressureTokens / contextWindow）与 `contextBreakdown`（systemTokens / toolsTokens / messageTokens）（`dsh-token-meter/lib/types/usage-projection.d.ts` L37-48；`breakdown-projection.d.ts` L14-24）。**`UsageFacet` 采集时未落盘**（usage 分面只有四桶/模型/会话/按天）→ **放弃**。
- **耗时/延迟（llmMs / toolMs / ttftMs / ttftSteps / decodeMs / decodeTokens）**：来自 DSH 的 `sessionStats` 投影（`@deepseek-ai/dsh-session-stats/lib/types/projection.js` L27-36）；本页 `UsageSessionStat` 只有 `Turns`，无步/耗时 → **放弃**。这是 DSH 会话 UI（`StatsLine`，见 §3.6）才有的东西。
- **step / tool 调用数**：同上，`sessionStats` 有 `steps`、tool 对，本页未落盘 → **放弃**。

### 2.3 与 DSH 自家口径的一致性（务必对齐）
本页四桶命名（`uncachedInput / cacheRead / cacheWrite / output`）与官方 `@deepseek-ai/dsh-token-meter` 的投影字段（`uncachedInputTokens / cacheReadTokens / cacheWriteTokens / outputTokens`）一一对应（`dsh-token-meter/lib/types/usage-projection.d.ts` L12-28；`UsageModels.cs` L8 注释已声明对齐）。ccusage 的"Cache Create"即本页/DSH 的"cacheWrite"（缓存写 = 创建缓存条目），"Cache Read"即 cacheRead。**命名上建议优先用 DSH 词汇（未缓存输入 / 缓存读 / 缓存写 / 输出），避免与第三方 CLI 词（cache create）混用。**
---

## 3. 交互模式

### 3.1 范围切换：两级（实例维度 × 时间维度）
- 来源：本页现状 `UsageViewModel.RebuildScopes` + `SetRange`（L74-93 / L184-190，7/30/全部）+ `InRange`（`UsageQuery.cs` L140-146）；ccusage `--since/--until/--last N`（https://ccusage.com/guide/daily-reports）。
- 借鉴什么：**两级范围**分开——一级"哪几个实例"（全部/单实例，现有下拉），二级"哪段时间"（现有按钮，可加"最近 N 天 / 本月 / 全部"）。`Summarize(from,to)` 已原生支持日期闭区间（`UsageQuery.cs` L45-52），改范围纯内存重算，无需重采（现有 `Reload` 即如此，L100-105）。
- 为何适合本页：完全复用现有架构，零新增采集；`RangeDays` 与 `InRange` 逻辑已就绪。可补一个"最近 N 天"输入或"本月/本季度"快捷项。

### 3.2 刷新：手动强制重采 + 明确"重"与"慢"
- 来源：本页 `Refresh`（`UsageViewModel.cs` L193-224）；ccusage `--offline`（离线缓存定价）与定期监控建议（https://ccusage.com/guide/cost-modes）。
- 借鉴什么：刷新**必须显式**（重采会话日志，非空则慢，现有 `StatusText` L198 提示"会话日志较多时需要几秒"）；**重采失败不覆盖成功**（`UsageCollector.cs` L6-8、L74-77 `ModelsComplete=false` 如实标注）。
- 为何适合本页：符合项目"界面不自己取数 / 失败不覆盖"两条铁律（AGENTS.md §1/§2）。给刷新按钮加"重"态与取消（慢时可能几秒~几十秒），并保留"某实例采集失败/跳过"的日志（`UsageViewModel.cs` L209-210）。

### 3.3 hover / tooltip：既有，但只做了"总量"
- 来源：本页 `UsageSessionRow.ToolTip`（含四桶，`UsageRows.cs` L76-80）与 `UsageDayBar.ToolTip`（`UsageView.xaml` L177）；DSH 官方 `StatsLine.formatTokens` 的紧凑数字格式（517 / 12.2K / 517K / 1.2M，`dsh-client-ui-conversation/lib/types/client/chat/StatsLine.d.ts` L39）。
- 借鉴什么：**数字密度要分级**——大数用紧凑格式（`FormatTokens` 现有"亿/万/N0"），hover 时给精确值 + 口径说明。模型行/柱状图目前无 tooltip 四桶，可补（数据都有）。
- 为何适合本页：WinUI 桌面 + 窄卡内放不下大数表格，hover 给"精确值 + 四桶"恰好补齐；`FormatTokens` 现有实现可复用，建议加一档"悬停显示原始/精算值"。

### 3.4 空态 / 加载态：已有基础，需区分"没数据 vs 没采集"
- 来源：本页 `HasData`/`EmptyText`（`UsageViewModel.cs` L50-51、L106、L134-136，分"尚无档案"与"范围内无记录"）；`IsBusy`/`ProgressRing`（`UsageView.xaml` L217-218）。
- 借鉴什么：空态要用**可行动**措辞——"点「刷新」立即采集一次，或启动实例产生会话后再来看"（现有已具备，L135）。需再补三态：a) 实例从未采集（无档案）；b) 有档案但范围无记录；c) 该实例 `ModelsComplete=false`（按模型/按天可能偏低，用 `CompletenessText` 已在页顶提示，L117-118）。
- 为何适合本页：离线聚合天然会"某实例离线/没跑过"，三态空态比纯"暂无数据"更诚实、更少困惑。

### 3.5 钻取：从"总览"深入"某实例 / 某模型 / 某天 / 某会话"——opencode 下钻范式
- 来源：https://github.com/heimoshuiyu/opencode-token-dashboard README（Cache Miss Drill-down：点击数据点 → 当天会话 + 逐消息缓存生命周期）。
- 借鉴什么：把一层总览做成**可点的漏斗**：实例卡 → 该实例；模型行 → 该模型的按天/按会话；某天柱 → 该天四桶+模型构成；会话行 → 该会话明细。
- 为何适合本页：数据链路完全闭合（§0.1/0.3 有模型按天、会话四桶、实例维度），且 `Summarize` 支持按范围过滤。WinUI 里用"点到即刷新下方区域"而非跳页，最贴合桌面单窗口。

### 3.6 DSH 自家会话 UI 的占用率/计时交互（参考，但本页不实现）
- 来源：`@deepseek-ai/dsh-client-ui-conversation/lib/types/client/skeleton/ContextMeter.d.ts` L1-13（发送按钮旁一个**环形** context-occupancy 表，点击展开 contextBreakdown 组成）与 `StatsLine.d.ts` L5-20（turns/steps/llmMs/toolMs/ttftMs/decode 一行展示）。
- 借鉴什么：**信息密度分层**——一个"轻量汇总"（环/一行）+点击展开"组成明细"。
- 为何适合本页：虽然本页无 contextPressure/sessionStats 数据（§2.2），但这个"轻量汇总 + 点击展开"的**交互模式**可复用在"总 tokens 环 + 点击展开四桶"上；只取模式、不取指标。

---

## 4. 明确排除项（本页面做不了/不该做的）

1. **实时流式更新**：数据离线聚合于档案，无流。项目铁律"界面不自己取数、不许起定时器"（AGENTS.md §1）。→ 不实时，只能"手动刷新 + 上次采集时间"。
2. **跨设备/跨机同步或多机汇总**：每份 `UsageFacetData` 属于一个本地实例（按 `Home` 区分），无云端、无共享账本。→ 不做"跨设备合并"。
3. **依赖云端 API 的配额类指标**：如 CodexBar 的"quota window / reset 倒计时 / 额度余额 / spend dashboard"（https://github.com/steipete/CodexBar README：per-provider session/weekly/monthly windows + reset countdowns + credits/spend/cost scans + provider status polling）。这些全部需要**在线登录/勾选/凭证**查询云端额度——本页离线且不碰凭证 → **明确排除**。
4. **Cost（成本，需定价目录）**：ccusage 的 cost、CodexBar 的 spend 都依赖现算定价；本页无成本字段、无离线价目表。若要需自建"每模型定价表"并标注估算，默认排除（见 §2.2）。
5. **terminal/终端专属形态**：ccusage 是 TUI（ASCII 表格、按终端宽度 >=100 显示全列 / <100 紧凑模式，https://ccusage.com/guide/daily-reports "Responsive Display"；5-hour billing `blocks` 是 Claude Code 专属术语）。这些"盒子边框表 / 宽度自适应填列"是终端范式，不适配 WinUI 桌面 → 不照搬，只借鉴其"按可用宽度降级显示列"的思路。
6. **菜单栏/系统托盘常驻小件**：CodexBar 是"macOS 菜单栏每 provider 一行状态条 + Merge Icons + 无 Dock icon"（https://github.com/steipete/CodexBar README）。本页是 WinUI 应用内页面，非系统常驻 → 不做菜单栏形态。
7. **context 占用率/窗口/组成、耗时/延迟、reasoning 拆分、step/tool 计数**：这些来自 DSH 其他投影（`dsh-session-stats`、token-meter 的 contextPressure/contextBreakdown、reasoning 是 output 子类），当前 `UsageFacet` 未落盘 → 不做（若要，属于"新增采集器/新投影"的活，不是本页变量，见 AGENTS.md §3"新增实例信息=新增采集器"）。

---

## 附录：来源清单与可复核性

- **DSH 官方 token 计量（本地）**：`@deepseek-ai/dsh-token-meter`（README.md L13-18 measure/estimate、L22-23 四桶 disjoint、L28 tokenUsage、L30 contextPressure、L34 contextBreakdown；`lib/types/usage-projection.d.ts` L11-48；`breakdown-projection.d.ts` L14-24）。路径：`C:\Users\cty05\AppData\Roaming\npm\node_modules\@deepseek-ai\dsh\node_modules\@deepseek-ai\dsh-token-meter\...`。已打开可复核。
- **DSH 自家会话 UI（本地）**：`@deepseek-ai/dsh-client-ui-conversation/lib/types/client/chat/StatsLine.d.ts`（L5-20 WindowStats、L39 formatTokens、L53 cacheHitPercent、L77 contextOccupancy）与 `lib/types/client/skeleton/ContextMeter.d.ts`（L1-13）。已打开可复核。
- **ccusage**：仓库 https://github.com/ccusage/ccusage（README：daily/weekly/monthly/session/blocks/statusline）；文档 https://ccusage.com/guide/daily-reports、/guide/session-reports、/guide/cost-modes、/guide/all-reports。已抓取正文可复核。
- **CodexBar**：https://github.com/steipete/CodexBar README（menu bar per-provider status / Merge Icons、usage bars、session/weekly/monthly window + reset countdowns、credits/spend/cost scans、provider status polling incident badges、privacy-first reuse sessions）。已抓取 README 可复核。
- **opencode-token-dashboard**：https://github.com/heimoshuiyu/opencode-token-dashboard README（Trend Chart 按天/按小时 7/30/90/180/365/all；Summary Cards total/active/input-output-reasoning-cache/request/runtime；Composition pie；Model Leaderboard top8 bar；Provider Distribution；Cache Miss drill-down 点击数据点→当天会话+逐消息缓存生命周期；Deduplicated Runtime；dark theme/i18n）。已抓取 README 可复核。

> 复核说明：本地来源全部用 read 打开过并标了行号；web 来源的正文均已通过站点/仓库抓取确认（非仅搜索摘要），URL 可访问。凡未能确认者未写入。
