# DshController 测试结果与测试账

> **当前状态 → 「一、现状账」；历史轮次结果在文末「历史存档」，逐轮追加、不覆写。**
> 新开对话请从这一节接上；跑法见 §三。

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
| N1 | 本轮新 GUI 过程小修待记（LogList 断线 / ShowAsync 裸调统一 / PluginCompat 宽松归一 / 探针过滤器 `*-crash_*`） | 会话事实在案 | **已清·在案**：LogList 单源绑定于 PageHost；弹窗全走 DialogService（9 文件 18 处）；归一先例被 Mapper.ApiAdapter 引用；探针统一盯 `*-crash_*.md` |
| N2 | 探针资产 9→12（本轮新增 archives/api/new-instance） | 共享验证工具 | **在案**：探针清单与用途已入本文 §1.1 表 |
| N3 | 批量连跑偶发时序抖动（gui-uia-check / gui-plugin-pages-check 各 1 项） | 单独复跑均全绿 | **环境观察**：连跑 12 探针资源争用所致，无真回归；单跑复验均通过 |
| N4 | 子代理派发故障 = 环境观察；本会话未派发子代理 | 全自演/自查 | **在案**：按约定以两遍法/自演红队兜底（裁决轮次已落盘） |
| N5 | 模型不支持图像输入（glm-5.3-flash） | 截图文件+探针数值为凭 | **环境观察**：证据链以探针输出与截图文件路径/字节数为准 |
| N6 | PageHost 超 400 行 → 拆 `MainWindow.ApiPresets.cs` partial | R1 守线 | **已清**：约定机检 142 文件 PASS |
| N7 | WSL 侧 providers 写入走 Linux 侧 HOME；深层非根级 providers 合并按根级新增（契约边界） | 明示在案 | **挂账/边界**：Windows 侧跳过有明示；根级契约已注 writer 头与对齐 doc |


---

# 三、怎么跑（原样可复制）

```powershell
cd C:\Users\cty05\Documents\AI\DshController\dev
powershell -ExecutionPolicy Bypass -File tools\check-conventions.ps1     # PASS
dotnet test tests\DshController.Tests\DshController.Tests.csproj         # 176/176
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