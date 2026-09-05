# 收口报告 · 2026-09 整改轮（六项 + 还债 + 收口）

状态：**开发全部完成，按定稿不发布**——`应用\`、`源码\` 未动、版本仍 2.0.0、CHANGELOG 发布段未改、未打 tag。
git：工作区为重构 2.0 + 本轮整改的整体待提交态（提交/发布时机由用户发话，走 DEVELOPMENT.md §7）。

## 一、六项交付对账单（每项→证据→验证）

| # | 交付 | 关键 diff/文件 | 验证锚 |
|---|---|---|---|
| ① | 重启绝不拉浏览器（根因=子进程 dsh web 自开窗，控制器侧抑制拦不住） | Core/BackendManager*.cs `BuildWebTailArgs`/`--no-open` 注入三分支+WSL；WslLaunch.cs `noOpen` 参数 | tests/RestartNoBrowserTests.cs 7 条；--selftest-core 89/0 含 4 条 R4 断言（实测命令行 `web --no-open --host 127.0.0.1 --port 3186/3187`）；docs/audit-restart-browser.md |
| ② | 用量页按定稿方案改版（D1-A/D2-A/D3-A/D4-A） | Core/Usage/UsageQuery.cs +MergeDaily/DayComposition/SessionsOnDay；UsageViewModel(+.Drill)/UsageRows 重写；UsageView.xaml 重写；IArchiveFacade.IsRunning 闭环 | 两轨评审门（docs/usage-redesign-proposal.md 头行定稿注记）；tests 9+7 条迁移/查询测 |
| ③ | 全局中度精简 | MainWindow/InstancePanel/PluginManage/Market 四页头与面板 11 处静态长文删除（tooltip 承接） | docs/audit-static-text-trim.md；残留 grep PageSubtitle=设计台2+样式定义1 |
| ④ | 按钮遮挡治理 | 10 处缩字+抖动消除；DshTheme 新增 BtnInline/SettingHint 令牌 | docs/audit-button-trim.md（960/1280/1600 走查表；组收官加修版本行溢出） |
| ⑤ | 设置两区重排为分节卡片 | MainWindow.xaml 设置页（2卡→3卡13行行形）；InstancePanel.xaml 折叠区（1卡→5卡13行） | 读写 .cs 零改动（当轮文件清单证据）；176 全绿 |
| ⑥ | 质量欠账清还 | 103 处裸 catch（3 处日志承接：MainWindow.xaml.cs:166-168/291-292*、PluginMarketPanel 数据源保存）；BackendManager→5 partial、PluginCatalog→5、PluginMarketPanel→4 | 台账 R3 行 25→0；三自检全绿（89/0、61/0、体检通过）；R1 行 9→5 |

*行号以当轮实测为准（后续行有漂移，语义锚在 docs/audit-restart-browser.md §1 表）。

## 二、计划外问题处置台账

### 顺手修（低风险当场闭环，8 项）
1. 保存类吞错无提示 ×3（主题/应用设置/市场源）→ 接 AppendLog/PushLog —— MainWindow.xaml.cs、PluginMarketPanel.Pickers.cs。
2. 实例设置版本行 960px 溢出（680>590）→ 编辑器列改上下形 —— InstancePanel.xaml 版本卡。
3. 钻取清除时柱选中环不摘（真缺陷）→ ResetDrill 补循环 —— UsageViewModel.Drill.cs。
4. 测试整数除法噪声 → 可整除值修净 —— UsageDrilldownQueryTests.cs。
5. xUnit2008 警告 → Assert.Matches —— RestartNoBrowserTests.cs（守 0 警告门）。
6. R3 工作流残留 7 处上呈点位 → 4 补「理由:」前缀/1 加理由/2…（含裁决记录 3 转日志）—— 台账注记+audit-restart-browser.md。
7. 红队轮二发现 3 minor（孤儿注释/合计 + 号/注释条数 74→176）→ 全清 —— ArchiveHub.cs:153、UsageViewModel.Drill.cs GrandTotalText、ArchiveCheck.cs:6。
8. CmbInstance_SelectionChanged 更名 _Changed → 消 R4 豁免 2 行 —— PluginMarketPanel.Pickers.cs + .xaml。

### 记账不修（各条有台账行）
- Summarize/MergeDaily 双口径归一 → DEVELOPMENT.md §8 近期可做第 6 条（理由：核心聚合改动收益<风险，两口径已有对照单测）。
- 剩余 5 条 R1（CoreSelfTest 571/MainWindow 539/HomeManager 539/Cli 534/InstanceDiscovery 437）→ conventions-allow.txt + 两份 AGENTS §6/§8 + 用户裁决记录（组收官问答「留台账另开工单」）。
- npx 指定版本形态、WSL 实例真机重启：本机无该形态实例，仅单测覆盖 → verify-report.md 挂账第 3 条。
- 真机人眼目检三单（重启连点/用量 vs 线框/三档宽度+设置走查）→ verify-report.md「遗留挂账」。
- 诊断页、L10n、令牌分层：定稿非目标 → 路线图原位（DEVELOPMENT.md §8）。

## 三、验证总账
门禁 check-conventions：107 文件 PASS（R3=0、无新增例外、台账净减 29 行：R1 9→5、R3 25→0、R4 2→0 面板类）。
单测：153→176（+23 全新增，0 删除）；构建 0 警告 0 错误（含 clean 重建与 -warnaserror）；
真实环境：--selftest-core 89/0（拆分前后各一轮）、--selftest-plugins 61/0、--archive-check 体检通过；GUI 冒烟多轮 closed-ok 无 crash.log。
独立验证：四件套双轨（verify-report.md）+ 红队两轮（一轮 reject 全处置、二轮 pass，redteam 裁决已落盘）。

## 四、下一步建议（非本轮承诺）
用户目检三单 → 满意后按 DEVELOPMENT.md §7 发布（git 提交 + 版本 2.1.0 + 覆盖 应用\/源码\）；另开工单处理剩余 5 条 R1。
---

# 收口报告 · 2026-09-04 顶栏四页改版轮（接续上节整改轮）

状态：**开发完成，按定稿不发布**——`应用\`、`源码\` 未动（本轮全程零触碰发布位）、版本仍 2.0.0、未打 tag；git 整体待提交态延续（提交/发布由用户发话，走 DEVELOPMENT.md §7）。

## 一、本轮交付对账单（条目 → 关键文件 → 证据锚）

| 交付 | 关键文件（dev） | 证据锚 |
|---|---|---|
| 顶栏四页壳/实例页/插件页左栏/升级治理（接续点回填标定） | MainWindow.PageHost / SplitPageShell / InstancePanel.* / PluginManagePanel 等（既往交付） | 计划树 mark_task 回填标定；探针 uia 35/35、rail-order 11/11、toolbar-log 32/32、plugin-rail 14/14、plugin-pages 17/17、detail-panel 16/16、upgrade-dialog 8/8、upgrade-exec 10/10（2026-09-04 收口批次） |
| 档案页·含退役列表 | ArchivesRailViewModel/ArchivesRailView/MainWindow+PageHost 接线 | 单测 10；探针 20/20（三类行+点选+改名实时）；`archives-rail-initial/retired-selected.png` |
| 档案页·元信息一览 | ArchiveMetaViewModel/ArchiveMetaView（双行宿主） | 单测 5；探针 meta-active/retired/totals-fallback；`archives-meta-*.png` |
| 档案页·用量看板并入（redteam） | BodyContent 双行 Grid + SyncUsageScope | redteam pass（轮次 1）；探针 panels-both-active/panels-totals-usage-only |
| 档案页·跨实例汇总 | 总计行→IsAllView 全量汇总（既有 UsageViewModel） | 单测覆盖汇总口径；`archives-rail-initial.png`（默认总计选中态） |
| 档案页·改名入口 | AliasStore/InstanceDisplayName 别名支路/PageHost RenameArchiveAsync | 单测 9；探针 rename-aliases/dialog/commit/live 4 项；`archives-rename-*.png` |
| API 预设页·预设存储 | ProviderPresetStore + AppPaths.ProviderPresetsFile | 单测 10（含损坏兜底/路径复核） |
| API 预设页·供应商编辑页 | ProviderPresetsView + ViewModel + MainWindow.ApiPresets partial | 单测 4；探针 8/8（增/预览/取消零写/改/删）；`api-presets-*.png` |
| 供应商同步·配置格式对齐 | ProviderConfigMapper + `docs/provider-sync-alignment.md` | 单测 7；对照表落库 |
| 供应商同步·同步预览窗 | ProviderSyncPlan + PageHost SyncPreviewAsync | 单测 6；探针 sync-preview-opens/cancel-zero-write |
| 供应商同步·写入生效（redteam） | ProviderSyncWriter（行级合并+原子备份） | redteam pass（轮次 1）；单测 5（读回==渲染/只读失败不覆盖）；探针 sync-confirm-real-write（RTEST 临时 HOME 落盘） |
| 供应商同步·新建页同步勾选 | InstancePanel.Create.cs（ChkSyncPresets + 创建写盘） | 探针 4/4（默认勾选/取消零写）；`new-instance-sync-checkbox.png` |
| 收口·门禁套件/测试账/结果账 | `docs/TEST-RESULTS.md`（现状账/探针表/增量溯源/开放票去向） | 三件套 142 PASS / 275 tests / build 0-0；12 探针全量回归 |

## 二、验证总账（2026-09-04 收口批次）
- check-conventions：**142 文件 PASS**（例外台账未增；PageHost 拆 `MainWindow.ApiPresets.cs` partial 守 ≤400）；
- 单测：**220 → 275**（+55 全新增，0 删除，0 跳过；逐组溯源见 TEST-RESULTS.md §1.2）；
- 构建：**0 警告 0 错误**；
- GUI 探针：**12/12 全量回归**（9 原 + 3 新增；uia 35/35、width 42/42、rail-order 11/11、toolbar-log 32/32、plugin-rail 14/14、plugin-pages 17/17、detail-panel 16/16、upgrade-dialog 8/8、upgrade-exec 10/10、archives 20/20、api-presets 8/8、new-instance 4/4；批量连跑 2 项时序抖动经单独复跑全绿，无真回归）；
- 红队裁决落盘：用量看板并入 pass（轮次 1）、写入生效 pass（轮次 1）；
- 端口纪律：动态测试全部 ≥3185；外部 3080 全程只读（detail-panel READ-ONLY 断言 + 探针终检 ext-still-running）。

## 三、证据索引
- 截图与探针输出：`docs/evidence/2026-09-topbar-nav/`（**141 张 png**，含本批 archives-rail/archives-meta/archives-rename/api-presets/new-instance 与历史 batches/widths/detail/pages/plug-rail/upgrade-*/tools/tools 子目录）——链接均可打开、文件在库（已逐目录列出验证）；
- 探针脚本：`verify/gui-*.ps1`（12 个，纯 ASCII、自恢复，用法见各文件头）；
- 账目：`docs/TEST-RESULTS.md`；格式契约：`docs/provider-sync-alignment.md`；

## 四、发布位不动声明
本轮所有改动与验证均发生在 `dev\`；`应用\`、`源码\` 两个发布位**零触碰**（未写入、未构建、未覆盖）。发布动作（git 提交 + 版本号 + 覆盖发布位 + CHANGELOG 发布段）继续等待用户目检后发话（W5 定稿不发布）。
