# DshController 测试结果与测试账

> **当前状态 → 「一、现状账」；历史轮次结果在文末「历史存档」，逐轮追加、不覆写。**
> 新开对话请从这一节接上；跑法见 §三。

---

# 一、现状账（2026-09-06 · API 页 llm-pi-ai 同步迁移轮）

> 用户指令：继续改进 API 预设页——①探针**自动获取多模态信息**、同步时自动写入；
> ②**非 DeepSeek 官方来源**的模型同步时**自动写入思考强度配置**（参考用户插件
> `dsh-thinking-efforts` v0.2.0 四档设计）。设计期核实 dsh 源码（`%APPDATA%\npm\node_modules\@deepseek-ai\dsh`）：
> 自定义提供方真身 = **`llm-pi-ai.providers.<key>`**（dsh-llm-pi-ai 适配器读取），根级
> `providers:` **全源码无消费方**——上两轮同步写的是死配置（用户裁决：迁移 + 自动清理）；
> 官方提供方 = 实例 `llm-deepseek` 适配器原生（模型/四档思考/多模态齐全），官方同步**仅送密钥**
> （写 `llm-deepseek.apiKeyEnv`，用户原话"官方就同步一个 key 过去"）。

| 改动 | 落地 |
|---|---|
| 探针多模态 | `ProviderModelProbe` 解析 `architecture.input_modalities`（OpenRouter）/`input_modalities`/`inputModalities`/`input`/`modalities`/逗号串 + 布尔 `supports_vision` 等 → `DiscoveredModel.Multimodal` 三态（无信息=null，不做模型知识库推测——插件非目标） |
| 预设/编辑卡 | `PresetModel.Multimodal`（bool?，旧档缺省=未知）；`DraftModelRow` 三态（编辑卡模型行新增开关，AutomationId `PresetModelMultimodal`）+ 候选勾选窗"多模态"尾注；内置官方 vision-exp 出厂=true |
| 同步目标迁移 | 非官方 → `llm-pi-ai.providers.<key>`；官方 → 仅 `llm-deepseek.apiKeyEnv`（未填密钥整单跳过）。新增 `ProviderSyncMerge.cs`（三个纯函数）+ `ProviderYamlScan.cs`（行扫描，拆分守 R1）；`ProviderSyncWriter` 改编排（备份/原子/**无变化不落盘**） |
| 增补式合并 | 同 key：路由字段更新；模型按 id——name/容量预设权威，compat 等未知字段逐行保留，`reasoningEfforts` 已有（含 false）不覆盖，`input` 非空声明保留/`[]` 可覆盖；实例多出模型保留；新模型规范追加（含四档+input） |
| 旧根级块清理 | 每次同步成功后删根级 `providers.<key>` 旧块，段空连段移除（契约文档 §5 迁移说明） |
| 启动凭据注入 | `BackendManager.Start/Wsl` 拉起实例时注入 `DSH_PRESET_*`（台账 Enabled+有密钥；WSL 逐个 WSLENV `/u`）——同步只写 env 名、值随启动注入，密钥不落盘 |
| 同步预览 | 官方/非官方分支文案（仅密钥引用 vs llm-pi-ai 块+四档+input note）；`ProviderSyncWrite.For(preset)` 单源出写计划 |

| 验证项 | 结果（实测） |
|---|---|
| 约定机检 / 构建 / 离线单测 | PASS（157 文件 0 违规）· 0 警告 0 错误 · **404/404**（382 + 22：探针多模态 5、映射 2、计划 4、写入引擎重写 11、台账 3、草稿 2；R1 拆分 `ProviderSyncMerge`+`ProviderYamlScan` 无新增台账） |
| GUI 探针（真机） | api-presets **10/10**（新增 api-model-multimodal-toggle；sync-confirm-real-write 断言升级：`llm-pi-ai:` + `    probe-preset:` + `reasoningEfforts:` + 12 空格 `off: null` + 根级旧块清除 + `other:` 保留；夹具种根级 probe-preset 旧块验迁移）、new-instance-sync **4/4**；证据 `docs/evidence/2026-09-llm-pi-ai-sync/` |
| GUI 冒烟 | 启动 12s → CloseMainWindow 正常退出 → 无 crash.log |
| 环境备注 | WSL 实例 settings.yaml 写入仍不达 Linux 侧 HOME（N7 剩余半边维持挂账）；根级历史残留块（如 probe-*）无消费方无副作用，可手动删 |

---

# 一、现状账（2026-09-06 · 档案页用量二次改版轮）

> 用户指令：用量看板去掉实例卡与"统计范围"切换，**恒显全部实例合并用量**（含已删除档案）；
> 单实例完整用量移入档案详情页（ArchiveMetaView）。分工：代理 A 起手 VM 改版后中止，
> 代理 A2 补完（ArchiveMetaViewModel.Usage.cs + 三份旧口径测试重写）；代理 B 两次被环境
> 终止且未落盘，View 层（UsageView/ArchiveMetaView/MainWindow 互斥接线）由主控直接完成；
> 代理 C 探针更新与真机验证。门禁与集成由主控收口。

| 改动 | 落地 |
|---|---|
| UsageViewModel 纯聚合 | `CollectSources` 恒取全部非空用量；Scopes/SelectedScope/IsAllView/ShowFocus/InstanceCards/GrandTotalText 全删；会话行标题带来源实例前缀；看板副标题"全部实例合计（含已删除档案）" |
| ArchiveMetaViewModel 用量区 | 新 partial `ArchiveMetaViewModel.Usage.cs`（254 行）：单实例 Summarize + 大数卡/四桶堆叠（×330 与看板同口径）/按天 30 根柱钻取/模型排行/会话明细（不加前缀）+ `RefreshUsageCommand` 单档案重采（退役禁用） |
| 档案页主区互斥 | MainWindow.xaml 单格叠放：左栏选中真实档案 → 详情（元信息+完整用量）；总计/无选中 → 看板。`SyncUsageScope` 删除，`ShowArchiveMeta` 只做互斥可见性（行点击不 Reload，免双重渲染） |

| 验证项 | 结果（实测） |
|---|---|
| 约定机检 / 构建 / 离线单测 | PASS（155 文件 0 违规）· 0 警告 0 错误 · **382/382**（376−19 旧口径 +25 新口径） |
| 新增/重写测试 | UsageViewModelTests 7 例（恒聚合/退役计入/空态/时间范围/请求"+"号/重采范围/会话前缀）、UsageViewModelDrillTests 7 例（合并口径/钻取/复位/空态）、ArchiveMetaViewModelTests 11 例（元信息 5 + 用量区 6：HasUsage/空态/回落/退役禁用重采/钻取） |
| GUI 探针（真机） | archives-rail **22/22**（新增 meta-usage-section / panels-mutual-exclusive-archive / usage-subtitle-aggregate，替换双显断言）、uia **33/35**（2 FAIL 见 W6）、width **42/42**；证据 `docs/evidence/2026-09-usage-merge/`（42 张） |
| `--archive-check` | 体检通过（exit 0，档案 4 份、采集 0 失败；usage 分面抽样 ok） |
| 环境备注 | 验证窗口内用户后端 3080 未监听（会话前未测基线，非本轮命令所致）；探针夹具为 RTEST-A 种入 usage 总账文件走真实采集链路 |

---
# 一、现状账（2026-09-05 · 左栏实例列表等间隔闪烁修复轮）

> 用户报障：实例页左栏列表存在等间隔（≈2.5s）的"下拉式"闪烁。根因：`_railTimer`（2500ms）
> 每 tick 走 `InstancesRailViewModel.Refresh()` 整表 `Rows.Clear()` + 重加**全新行对象**，
> ListView 全量重建容器（瞬时清空=闪烁）；且行对象每轮都是新的，
> `SyncSelection` 每轮重新赋 SelectedItem（高亮重闪）+ 无条件 `ScrollIntoView`（滚动微跳）。
> 修复：稳定态（行序列逐字段一致）跳过重建——`SequenceEqualsCurrent` 纯比较，
> 真有启停/增删/改名/排序变化才重排；`SyncSelection` 目标行已是选中项时不赋值不滚动。

| 验证项 | 结果（实测） |
|---|---|
| 约定机检 / 构建 / 离线单测 | PASS · 0 警告 0 错误 · **344/344**（342→344，+2） |
| 新增测试 | `稳定态刷新不触碰行集合`（重复 Refresh 对 Rows **零 CollectionChanged 事件** + 行引用不变——ListView 无从重建闪烁，机制级断言）、`清单或状态变化才重建`（增删仍正常整表重建，行为不变） |

---
# 一、现状账（2026-09-05 · 实例页/插件页改版轮 · 5 项需求）

> 用户 5 点需求 + 追加升级按钮。子代理分工：轮次一 3 并行（A 插件页删树+profile 中文 /
> B 左栏按钮+新建分步+去选择器 / C 版本清理+页脚+左栏事件接线），轮次二 1 并行
> （D 升级功能 Core+UI）+ 探针 2 并行（E 插件页 4 个 / F 实例页 4 个）。集成与门禁由主控完成。

| 需求 | 落地 |
|---|---|
| 1 插件页左栏 | 删左栏实例树（MainWindow.xaml/PageHost/PluginManagePanel 波及面清零）；实例切换=页面内"目标实例"下拉；实例页左栏删"实例/按最近启动/实例 N 台"字样 |
| 2 profile 中文化 | 市场页+管理页标签改"配置档案"+ToolTip（dsh 实例 HOME 的 profiles/<档案>/，web=官方默认，插件装进该档案）；值"web"保留可编辑，去留待用户按解释裁定 |
| 3 版本去掉+入口上移+新建分步+版本下拉 | VersionText/LaunchModeText/hint 尾缀/旧版本卡/页脚版本段全删（HarnessVersion 字段与启动链保留，存量钉值仍生效）；新建/扫描上左栏顶部（BtnRailNew/BtnRailScan）；新建分步=有 WSL 先弹环境选择→多发行版再选→表单；表单版本下拉自动载 npm 版本（倒序+跟随环境默认+可手输） |
| 4 单实例详情 | 删"当前实例"下拉与全部联动（CmbInstance_SelectionChanged/SyncPickerSelection/_loadingList）；克隆/删除移到状态卡头部；左栏行=唯一选中来源 |
| 5 升级按钮 | 设置区"升级"卡：版本下拉（npm versions 倒序+当前已装旁注）+「升级」；语义=按环境真升级（Win/WSL 两通道 npm install -g，300s 超时日志进控制台），成功后实例切"跟随环境"并提示重启生效；运行中先确认；实例级钉版本手输保留于新建表单 |

| 验证项 | 结果（实测） |
|---|---|
| 约定机检 / 构建 / 离线单测 | PASS · 0 警告 0 错误 · **376/376**（344→376，+33 升级功能 +2 闪烁修复轮） |
| GUI 探针（更新后 8 个） | plugin-rail **14/14**、plugin-pages **17/17**、upgrade-dialog **8/8**、upgrade-exec **10/10**、detail-panel **16/16**、rail-order **10/10**、new-instance-sync **4/4**、width **42/42**（合计 121 项，证据 `docs/evidence/2026-09-ui-revamp/`） |
| GUI 冒烟 / 升级卡实测 | 无 crash.log；设置区展开后 CmbUpgradeVersion/BtnUpgradeInstance 就位，版本下拉实际拉到 npm 全量（15 个版本在线实测） |

集成期修复（代理产出间的断链）：Settings.cs 三处孤儿调用（ReadVersionCombo/UpdateVersionText/
SyncPickerSelection→删除，HarnessVersion 保存改为保持既有值）、PageHost UpdateRailHeader 删除、
孤立 _loadingSettings/_publishedVersions 字段清理、三处残缺 try 块修复；
**行为修复两处**：①左栏选中等间隔闪烁（稳定态零重建+选中幂等，见上轮）；②选中实例瞬间
StatusText 沿用旧值的竞态（RefreshSelectedControls 用档案 liveness 结论立即纠偏——
detail-panel 探针 ext-still-running 因此由红转绿）。

---
# 一、现状账（2026-09-05 · WSL 离线枚举 + 拉起扫描轮）

> 用户改版方案：扫描时先离线枚举机器上已注册的 WSL 发行版（Lxss 注册表，零启动），
> 发现未运行的就弹窗征求许可——「确认」逐个拉起做 dsh 安装探测（探测完自动恢复关机）、
> 「不扫描WSL」跳过。两个子代理并行实现（Core 离线枚举+拉起探测编排 / App 弹窗流），本侧集成。

| 验证项 | 结果（实测） |
|---|---|
| 约定机检 / 构建 / 离线单测 | PASS · 0 警告 0 错误 · **342/342**（330→342，+12 纯函数测试） |
| GUI E2E（发行版停止态 + 实例未注册） | 扫描 → 弹"拉起 WSL 来扫描？"（候选 Ubuntu-26.04，确认/不扫描WSL）→ 确认 → 拉起探测 → 弹"发现已安装的 dsh 发行版（v0.1.1-rc.2 · ~/.dsh 已初始化）" → 全部添加 → 页脚"WSL 实例 1 个"、左栏 `wsl:3081（已停止）` |
| 状态恢复 | 探测后 `wsl -l --running` 为空（我们拉起的发行版已 terminate 恢复关机）；instances.json 已落盘；无 crash.log |

新增 API：`WslTools.ParseRegisteredDistroLines / ListRegisteredDistrosOffline`（Lxss 注册表读，
只缓存肯定语义不涉及）、`InstanceDiscovery.OfflineBootCandidates / SubtractCandidates /
ProbeOfflineDistrosAsync`（拉起探测编排：未运行的才拉起并记入"我们拉起"集合，探测完逐个
terminate；单发行版失败记日志继续）。App 侧：`ConfirmBootWslScanAsync`（确认/不扫描WSL 弹窗）、
`OfferInstalledDistrosAsync` 重构为 `PromptAddInstalledDistrosAsync`（探测结果注入式，运行中/拉起两路合并）。

---
# 一、现状账（2026-09-05 · WSL 扫描不可达修复轮）

> 用户报障：WSL 内的 dsh 扫描不到（启动 WSL 系统后再扫也不到）。根因有三，均已修：
> ① 改版后 WSL 面板仅在存在 WSL 实例时可达，而扫描的"只收本环境实例"过滤 + "已安装
> 发行版询问"仅 WSL 面板触发——WSL 实例清零后任何面板的扫描都永远找不回 WSL（结构死锁）；
> ② `WslTools.IsInstalledAsync` 把否定结果钉死在静态缓存（应用启动早期 wsl.exe 暂时不可用
> 即终身"未安装"）；③ 扫描询问添加不落盘，关窗即丢。本机档案佐证：WSL 实例
> `auto-wslinst-Ubuntu-26_04` 于当日 13:41 退役（retiredAt），与报障时间线吻合。

| 验证项 | 结果（实测） |
|---|---|
| 约定机检 / 构建 / 离线单测 | PASS · 0 警告 0 错误 · **330/330** |
| GUI 实测（修复后） | WIN 面板点「扫描」→ 弹出"发现已安装的 dsh 发行版：Ubuntu-26.04（dsh v0.1.1-rc.2 · ~/.dsh 已初始化）"→ 全部添加 → 页脚"WSL 实例 1 个"、左栏出现 `wsl:3081` 行 → instances.json 已落盘（询问添加即保存） |
| 命令行旁证 | 发行版内 `pgrep -f '@deepseek-ai/[d]sh|[d]sh web'` 无进程（后端未跑，运行中发现空属正常）；DSHINST 探测脚本输出 `0.1.1-rc.2|yes`（已安装路径可用） |

修复明细：发现结果跨环境全收（InstancePanel.Scan 去掉环境过滤）；"已安装发行版询问"不再限定
WSL 面板；`MainWindow.UpdateFooter` 汇聚点调用两面板 `EnsureWired()` 补齐跨面板事件接线；
询问添加立即 `_registry.Save()`；`IsInstalledAsync` 只缓存肯定结果。

---
# 一、现状账（2026-09-05 · API 页照 dsh 模型页适配轮）

> 接续 2026-09-04 顶栏四页改版。本轮：提取官方插件 `@deepseek-ai/dsh-client-ui-settings-models`
> 的模型页逻辑与文案，API 页改为主从布局（左栏提供方 + 详情编辑卡/新建卡 + 模型目录 +
> 获取可用模型探针，端点给出容量即自动填入）；预设数据模型升级（ProviderId 稳定路由 +
> Models 目录数组，旧档案兼容），并新增**内置"DeepSeek 官方"默认提供方**（默认常驻、
> 不可删除、官方路由查重占用）。**测试 275→330**；探针证据 `docs/evidence/2026-09-api-dsh-adapt/`。

| 验证项 | 命令 | 结果（实测） |
|---|---|---|
| 约定机检 | `tools\check-conventions.ps1` | **PASS · 149 文件 0 违规**（例外台账未增） |
| 离线单测 | `dotnet test tests\DshController.Tests\...` | **330/330 通过**（275 基线→330，只增不减） |
| 全量构建 | `dotnet build DshController.slnx` | **0 警告 0 错误** |
| GUI 冒烟 | 启动→14s→CloseMainWindow | **closed-ok · 无 crash.log** |
| gui-api-presets-check（重写为主从走查） | `verify\gui-api-presets-check.ps1` | **9/9**（新建卡/同步预览取消零写入/同步真写入/改名/删除；RTEST-S 临时 HOME，用户状态备份恢复） |

## 1.1 本轮测试账增量（275 → 326，逐组溯源，只增不减）

| 小类 | 测试文件 | 新增 | 累计 |
|---|---|---:|---:|
| dsh 规则移植（路由/密钥/容量/模型目录校验） | `ProviderPresetRulesTests.cs` | 25 | 300 |
| 模型目录探针解析（端点拼接/别名容量/嵌套） | `ProviderModelProbeTests.cs` | 6 | 306 |
| 台账兼容（旧档案播种/路由查重/容量往返） | `ProviderPresetStoreTests.cs` | 3 | 309 |
| 映射与渲染（目录映射/协议透传/容量渲染） | `ProviderConfigMapperTests.cs` | 5 | 314 |
| 编辑卡状态机与 VM 主从（就绪门/密钥只写/采纳/探针目标/查重拦保存） | `ProviderEditorDraftTests.cs` | 12 | 326 |
| 内置"DeepSeek 官方"（播种/置顶/不可删/路由占用/用户改动保留） | `ProviderPresetStoreTests.cs`(+3) / `ProviderEditorDraftTests.cs`(+1) | 4 | 330 |

> 注：探针过程记录的两条工程事实——① WinUI 3 `x:Bind` 的 `UpdateSourceTrigger=PropertyChanged`
> 可用（此前 WMC9999 崩溃真因是绑定路径属性名笔误，被 XamlCompiler 卫星资源缺失掩盖，
> 与 §9"坑"条目同型：先找真实错误再下结论）；② 禁用按钮的 UIA Invoke 会抛
> "无法识别的错误"，探针走查须先满足就绪门（本轮即：先「添加模型」再保存）。

---
# 一、现状账（2026-09-04 · 顶栏四页改版收口）

> 接续 2026-09-03 整改轮。本轮：顶栏四页壳/实例页/插件页左栏与升级治理（接续点回填标定）+ 档案页与显示名（含退役列表/元信息一览/用量并入/跨实例汇总/改名入口）+ API 预设页（存储/编辑页）+ 供应商同步（对齐/预览/写入/新建勾选）+ 收口账。**测试 220→275**；截图证据 `docs/evidence/2026-09-topbar-nav/`（含本轮 archives-rail/archives-meta/api-presets/new-instance 等 ~20 张）。

| 验证项 | 命令 | 结果（实测） |
|---|---|---|
| 约定机检 | `tools\check-conventions.ps1` | **PASS · 142 文件 0 违规**（例外台账未增；PageHost 拆分 partial 守 ≤400） |
| 离线单测 | `dotnet test tests\DshController.Tests\...` | **275/275 通过**（220 基线→275，只增不减；新增 8 个测试文件） |
| 全量构建 | `dotnet build DshController.slnx` | **0 警告 0 错误** |

## 1.1 GUI 探针全量回归（2026-09-04 收口批次）

| 探针 | 项数 | 结果 |
|---|---|---|
| gui-uia-check | 35 | 35/35（批量连跑偶发 1 时序抖动，单独复跑 35/35） |
| gui-width-check（960/1280/1600） | 42 | 42/42 |
| gui-rail-order-check | 11 | 11/11 |
| gui-toolbar-log-check | 32 | 32/32 |
| gui-plugin-rail-check | 14 | 14/14 |
| gui-plugin-pages-check（真实市场安装） | 17 | 17/17（批量连跑偶发 1 时序抖动，单独复跑 17/17） |
| gui-detail-panel-check（3080 只读 + 3185 全生命周期） | 16 | 16/16 |
| gui-upgrade-dialog-check | 8 | 8/8 |
| gui-upgrade-exec-check（真装升级+忽略台账+重启持久） | 10 | 10/10 |
| gui-archives-rail-check（本轮新增） | 20 | 20/20 |
| gui-api-presets-check（本轮新增） | 8 | 8/8 |
| gui-new-instance-sync-check（本轮新增） | 4 | 4/4 |

红队裁决落盘（verify=redteam 小类）：用量看板并入 pass（轮次 1）、写入生效 pass（轮次 1）。
## 1.2 本轮测试账增量（220 → 275，逐组溯源，只增不减）

| 小类 | 测试文件 | 新增 | 累计 |
|---|---|---:|---:|
| 档案页与显示名 · 含退役列表 | `ArchivesRailViewModelTests.cs` | 10 | 230 |
| 档案页与显示名 · 元信息一览 | `ArchiveMetaViewModelTests.cs` | 4 | 234 |
| 档案页与显示名 · 改名入口 | `AliasStoreTests.cs`(5) + `InstanceDisplayNameTests.cs` 别名(3) + Meta CurrentArchiveId(1) | 9 | 243 |
| API 预设页 · 预设存储 | `ProviderPresetStoreTests.cs` | 10 | 253 |
| API 预设页 · 供应商编辑页 | `ProviderPresetsViewModelTests.cs` | 4 | 257 |
| 供应商同步 · 配置格式对齐 | `ProviderConfigMapperTests.cs` | 7 | 264 |
| 供应商同步 · 同步预览窗 | `ProviderSyncPlanTests.cs` | 6 | 270 |
| 供应商同步 · 写入生效 | `ProviderSyncWriterTests.cs` | 5 | 275 |

> 跳过/忽略测试：**0**（历次 `dotnet test` 均 `已跳过: 0`；代码中 `Skipped` 字样为 `FacetStatus.Skipped` 领域状态，非测试跳过）。


---
# 一、现状账（2026-09-03 · 版本 2.0.0 + 2026-09 整改轮）

环境基线：.NET 10 SDK / WindowsAppSDK 2.4.0 / WinUI 3；dsh harness 0.1.1-rc.2（`%APPDATA%\npm`）。

| 验证项 | 命令 | 结果（实测） |
|---|---|---|
| 约定机检 | `tools\check-conventions.ps1` | **PASS · 107 文件 0 违规**（R3 例外=0；R1 剩 5 条、R4 8 条=事件惯用法） |
| 离线单测 | `dotnet test tests\DshController.Tests\...` | **176/176 通过**（153→160→169→176，只增不减） |
| 全量构建 | `dotnet build DshController.slnx` | **0 警告 0 错误**（含 clean 重建 + `-warnaserror` 复跑） |
| 核心自检 | `DshController.exe --selftest-core` | **89 passed / 0 failed**（13 组；真实启停/重启/外部接管，端口 3185-3196） |
| 插件自检 | `DshController.exe --selftest-plugins` | **PASS 61 / FAIL 0**（市场核心，全离线） |
| 档案体检 | `DshController.exe --archive-check` | **体检通过**（3 档案含 1 退役；分面 0 失败） |
| GUI 冒烟 | 启动→12s→CloseMainWindow | **closed-ok · 无 crash.log**（两轨各测；截图见 `docs/evidence/`） |
| 两轨独立验证 | 开发轨+验证轨并行 | 六项差异=0，记录 `docs/verify-report.md` |
| 红队/终审 | 三轮独立裁决 | 小类轮：reject(6 项)→修→**pass**；组级终审：**pass**（裁决已落盘） |

## 1.1 本轮新增测试资产（四个测试文件）

| 文件 | 用例 | 钉死什么 |
|---|---|---|
| `RestartNoBrowserTests.cs` | 7 | R4：重启命令行必含 `--no-open`（Windows 尾参三分支 + WSL 脚本两形态）；正常启动字节级不变；WSL 停止模式兼容 |
| `UsageDrilldownQueryTests.cs` | 9 | 钻取三纯函数（MergeDaily/DayComposition/SessionsOnDay）空区间/边界日/无数据 + 与 Summarize 同输入对照一致（双口径锚） |
| `UsageViewModelDrillTests.cs` | 7 | 用量视图状态迁移：实例卡三态/点卡切聚焦/钻取-过滤-清除往返/切范围自动清钻取/hero-命中率口径自洽/刷新摘要/空态可行动 |
| `FakeArchiveFacade.cs`（改造） | — | 支持 liveness 数据注入（running 参数），façade `IsRunning` 闭环可测 |

## 1.2 selftest-core 中本轮新增断言（R4 双锁）
- [2] 正常启动子进程命令行**不含** `--no-open`（现状保持）
- [3] 重启路径：`Ready.SuppressAutoOpen=true` + 命令行**含** `--no-open` + 子进程输出无 `opening the default browser` 公告（体验等价证据）
- [6] 外部实例接管重启：同上双断言

---

# 二、开放票与人工目检单（新对话从这里接）

| # | 事项 | 状态/说明 | 去向（2026-09-04 收口） |
|---|---|---|---|
| V1 | 真机目检·重启连点不弹浏览器（GUI「重启」×2，含插件一键重启） | 机器侧断言全绿；待人眼一票 | **顺延**：改版新 GUI 已合拢，结构探针（uia 35/35 等）绿；人眼复核仍挂 |
| V2 | 真机目检·用量页 vs 定稿线框（§1 D1-A/D2-A/D3-A/D4-A） | 截图仅覆盖启动页 | **顺延**：用量已并入档案宿主（含退役回顾），探针面板共存 2 项绿；人眼对照线框仍挂 |
| V3 | 真机目检·三档按钮无截断 + 两设置区走查 | 结构走查表 §3 | **结构面已清**：gui-width-check 42/42（960/1280/1600）收口批次绿；人眼观感复核仍挂 |
| W1 | 剩余 5 条 R1 拆分（CoreSelfTest 571/MainWindow 539/HomeManager 539/Cli 534/InstanceDiscovery 437） | 用户裁决另开工单 | **挂账**：台账与 DEVELOPMENT.md §8.2 在案，仍待用户裁决开工单 |
| W2 | `MainWindow.xaml.cs:413` 残留 `Debug.WriteLine` | 建议并入诊断页轮 | **挂账**：随诊断页轮（ILogSink）处理 |
| W3 | 用量页「全部」档柱图截断 60 根未标注 | 目检时留意 | **挂账**：建议补一行小注，随下次用量视觉走查 |
| W4 | npx 指定版本 / WSL 实例的真机重启无对应实例可打 | 仅单测覆盖 | **挂账**：有对应实例的机器上补跑 selftest 面 |
| W5 | 发布流程未走（定稿不发布）：git 未提交、版本仍 2.0.0、`应用\`/`源码\` 未覆盖 | 目检满意后按 §7 执行 | **挂账（定稿不发布）**：本轮明确不做 |
| W6 | 档案页首开 UsageHost UIA 可见 ~13s（uia-check switch-arch-usage normal/dev 双复现） | 建议工单：Reload/Summarize 移出 UI 线程或分帧 + 会话明细/模型排行换 ListView 虚拟化 | **挂账**：终态正确、功能断言全绿；09-04 基线 3s → 数据 ~5 倍增长放大，非本轮代码回归 |
| N1 | 本轮新 GUI 过程小修待记（LogList 断线 / ShowAsync 裸调统一 / PluginCompat 宽松归一 / 探针过滤器 `*-crash_*`） | 会话事实在案 | **已清·在案**：LogList 单源绑定于 PageHost；弹窗全走 DialogService（9 文件 18 处）；归一先例被 Mapper.ApiAdapter 引用；探针统一盯 `*-crash_*.md` |
| N2 | 探针资产 9→12（本轮新增 archives/api/new-instance） | 共享验证工具 | **在案**：探针清单与用途已入本文 §1.1 表 |
| N3 | 批量连跑偶发时序抖动（gui-uia-check / gui-plugin-pages-check 各 1 项） | 单独复跑均全绿 | **环境观察**：连跑 12 探针资源争用所致，无真回归；单跑复验均通过 |
| N4 | 子代理派发故障 = 环境观察；本会话未派发子代理 | 全自演/自查 | **在案**：按约定以两遍法/自演红队兜底（裁决轮次已落盘） |
| N5 | 模型不支持图像输入（glm-5.3-flash） | 截图文件+探针数值为凭 | **环境观察**：证据链以探针输出与截图文件路径/字节数为准 |
| N6 | PageHost 超 400 行 → 拆 `MainWindow.ApiPresets.cs` partial | R1 守线 | **已清**：约定机检 142 文件 PASS |
| N7 | WSL 侧 providers 写入走 Linux 侧 HOME | 明示在案 | **挂账**：2026-09-06 llm-pi-ai 迁移轮已解决"深层非根级 providers 合并"半边（目标改 llm-pi-ai.providers + 增补式合并 + 根级旧块清理）；WSL HOME 半边维持挂账 |


---

# 三、怎么跑（原样可复制）

```powershell
cd C:\Users\cty05\Documents\AI\DshController\dev
powershell -ExecutionPolicy Bypass -File tools\check-conventions.ps1     # PASS
dotnet test tests\DshController.Tests\DshController.Tests.csproj         # 382/382
dotnet build DshController.slnx -nologo                                  # 0 警告 0 错误
# 真实环境（起进程用 ≥3185；3080 用户后端只读）
src\DshController.App\bin\x64\Debug\net10.0-windows10.0.19041.0\DshController.exe --selftest-core    # 89/0
src\DshController.App\bin\x64\Debug\net10.0-windows10.0.19041.0\DshController.exe --selftest-plugins # 61/0
src\DshController.App\bin\x64\Debug\net10.0-windows10.0.19041.0\DshController.exe --archive-check    # 体检通过
# GUI 冒烟：启动→等 12s→CloseMainWindow→查 exe 目录无 crash.log
# 设计台（样式核对）：同 exe 加 --dev 参数启动
```

---

# 历史存档

## v0.2.0（2026-08-16 执行）

> 环境：Windows 10.0.26200 (x64)，.NET SDK 6.0.136，WinUI 3 / Windows App SDK 1.5；dsh 由 `%APPDATA%\npm\dsh.cmd` 解析，3080 外部后端保持运行。
> 注：该轮「尚未执行」清单中的 GitHub Release / Portable 产物验证至今仍未做（沿承为既定不做/延后项）。

| 项目 | 结果 |
|---|---|
| `dotnet build -p:Platform=x64` | 通过，0 警告 0 错误 |
| `build.ps1` Release publish | 通过，无 `NETSDK1179` 警告 |
| `--version` | `DshController 0.2.0` |
| `--check`（默认 3080 只读） | 通过，dsh 已解析，3080 为 UP |
| `--spawn-test-node --port 3187` | 通过，启动/停止与端口释放全绿 |
| `--spawn-test --port 3188` | 通过，真实 dsh 启动/停止与端口释放全绿 |
| `--selftest-core --port 3185`（Debug） | 通过，30 passed / 0 failed |
| `--selftest-core --port 3191`（Release publish-fixed） | 通过，30 passed / 0 failed |
| GUI 启动与状态显示 | 通过，UI Automation 可见文本完整，外部实例识别正常 |
| 明暗主题切换 | 通过，浅色平均亮度约 241，深色约 85，最终恢复 `system` |
| 任务栏/exe 图标 | 通过，`app.ico` 白底黑鲸鱼；40px 约 21% 黑 72% 白，16px 黑约 25% |
| 外部实例直接重启 | 通过，确认后停止外部测试实例并重新启动，Ready 抑制浏览器 |

当时核心自检覆盖 6 组：launcher.json 污染净化 / 失败注入报告（退出码 42）/ 真实 dsh 启动 / 重启 SuppressAutoOpen / 停止端口释放 / 外部实例直接重启。
GUI 验证：主窗口标题、外部实例态、四操作按钮态、日志区与页脚、主题持久化——当轮全部通过。
发布产物：`publish-fixed\DshController.exe`（119MB，框架依赖 .NET 6，WASDK 自包含）。