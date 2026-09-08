# DshController 测试账（TEST-RESULTS）

> **作用**：验证总账与开放票的**唯一**记录处（原 verify-report / closing-report 已并入本文，
> 历史计划/审计/调研文档已删，细节在 git 历史）。
> 记账纪律：历史轮次只留一行摘要；数字只在本文件更新；证据截图不入库
> （`docs/evidence/` 已 gitignore，需要证据时重跑 `verify/gui-*.ps1` 现生成）。
> 新开对话：读「一、当前状态」和「二、开放票」即可接手。

## 一、当前状态（2026-09-08 · v2.1.1 已发布）

> **v2.1.1 正式发布**：档案用量工作台、TTFT、零 Token 会话清理与模型能力三态已同步至 `应用\`、`源码\`、Git tag 与 GitHub Release。

| 项 | 现值 |
|---|---|
| 离线单测 | **418/418**（只增不减；总量/详情统一 Cockpit 风格面板与零 Token 会话过滤覆盖） |
| 约定机检 | 164 文件 0 违规（R1 台账例外见开放票 W1） |
| 构建 | 无增量 `--no-incremental` 0 警告 0 错误 |
| GUI 探针最近全绿记录 | uia **35/35**（W6 修复后 switch-arch-usage 462/390ms，MAX-SWITCH-LATENCY 462ms）、archives-rail **22/22**、api-presets **10/10**、new-instance-sync **4/4**、detail-panel **15/16**（唯一红=ext-still-running，环境性）、width 42/42（09-05 基线） |

## 二、开放票（新对话从这里接）

| # | 内容 | 状态 |
|---|---|---|
| W1 | R1 台账 5 条超长文件（CoreSelfTest 571 / MainWindow 539 / HomeManager 539 / Cli 534 / InstanceDiscovery 437 行），例外登记 `tools\conventions-allow.txt` | **挂账**：用户裁决留台账另开工单（2026-09-06 再确认暂不处理） |
| W4 | npx 指定版本 / WSL 实例的真机重启仅单测覆盖 | **环境限制**：本机无该形态实例，有则补跑 selftest |
| W5 | 发布流程：git 提交、版本 2.0.0→2.1.0、覆盖 `应用\`/`源码\`、CHANGELOG 发布段 | **已清**：2026-09-06 v2.1.0 发布（tag+强推 GitHub,历史重写后 release/下载数完好;`应用\版本说明.md` 就位;旧 2.0.0 zip 移至项目根留档） |
| 目检三单 | ①重启连点不弹浏览器 ②用量页 vs 定稿线框 ③按钮三档宽度 + 设置区走查 | **用户暂缓**（2026-09-06 明确） |
| 环境性 | detail-panel 的 ext-still-running 红 = 用户 3080 后端未监听；GitHub Release / Portable 产物验证未做（既定延后） | 后端启动即绿 / 不做 |

## 三、怎么跑（原样可复制）

```powershell
cd <项目根>\dev
powershell -ExecutionPolicy Bypass -File tools\check-conventions.ps1     # PASS
dotnet test tests\DshController.Tests\DshController.Tests.csproj         # 全绿
dotnet build DshController.slnx -nologo --no-incremental                 # 0 警告 0 错误
# 真实环境（起进程用 ≥3185；3080 用户后端只读）
src\DshController.App\bin\x64\Debug\net10.0-windows10.0.19041.0\DshController.exe --selftest-core    # 89/0
src\DshController.App\bin\x64\Debug\net10.0-windows10.0.19041.0\DshController.exe --selftest-plugins # 61/0
src\DshController.App\bin\x64\Debug\net10.0-windows10.0.19041.0\DshController.exe --archive-check    # 体检通过
# GUI 探针（证据 PNG 现生成到 docs/evidence/<轮次>，不入库）
powershell -ExecutionPolicy Bypass -File verify\gui-<name>-check.ps1 -Exe src\DshController.App\bin\x64\Debug\net10.0-windows10.0.19041.0\DshController.exe -Out docs\evidence\<轮次>
# GUI 冒烟：启动→等 12s→CloseMainWindow→查 exe 目录无 crash.log
# 设计台（样式核对）：同 exe 加 --dev 参数启动
```

## 四、历史轮次（一行/轮，细节看 git log 对应提交）

| 日期 | 轮次 | 要点 | 测试 |
|---|---|---|---|
| 2026-08-16 | v0.2.0 | WinForms 单文件 → WinUI 3 三层雏形 | — |
| 09-03 | v2.0.0 重构 + 2026-09 整改轮（六项） | Core/ViewModels/App 三层 + 103 处裸 catch 清零 + 四件套双轨验证；tag v2.0.0 已发布 GitHub | 176/176 |
| 09-04 | 顶栏四页改版 + 供应商同步 | API 预设页 + 同步预览/写入引擎 + ProviderId 稳定路由 | 275 |
| 09-05 | API 页照 dsh 模型页适配 | 主从布局 + 模型探针 + 内置"DeepSeek 官方" + 实例/插件页改版 | 330 |
| 09-05 | WSL 离线枚举/拉起扫描 + 不可达三根因修复 | WSL 实例支持 | 342 |
| 09-05 | 左栏实例列表闪烁修复 + 用量二次改版 | 恒聚合看板 + 单实例用量入详情页 + 两页互斥 | 382 |
| 09-06 | API 页 llm-pi-ai 同步迁移 | 同步目标迁 `llm-pi-ai.providers`（根级 providers 证实为死配置）+ 增补式合并 + 非官方自动思考档四档 + 探针多模态三态 + 官方仅送 key + 启动注入 `DSH_PRESET_*`；契约见 `provider-sync-alignment.md` | 404 |
| 09-06 | 挂账清偿 | W6 首开 ~13s→~0.5s（Reload 异步帧 + ListView 虚拟化）、W3 柱图截断标注（60/30）、W2 残留 Debug.WriteLine、N7-WSL 写入通道（/mnt/c 中转+备份；顺带修 WSL 实例误写 Windows HOME）；用户真实 settings.yaml 根级死段清理（备份留存） | 407 |
| 09-07 | 档案详情用量 Cockpit 风格改版 | 详情页五 KPI、自定义日期范围、平滑渐变趋势图/Tooltip/竖线/节点高亮/数据表；Usage 采集器同次扫描折叠 DSH sessionStats TTFT 并写入档案与按日聚合；图片请求与估算价值保持诚实占位 | 415 |
| 09-07 | 档案页整体统一 Cockpit 风格 | 总量聚合页与单档案详情页共用五 KPI、日期范围、趋势图、Tooltip、数据表与动画；档案左栏和元信息改为统计工作台卡片；总量默认近 7 天，退役历史与刷新权限保持原语义 | 417 |
| 09-08 | 趋势算法与会话明细修正 | 曲线按 Cockpit `CodexUsageTrend` 的坐标归一化与 Catmull-Rom 等价 Bézier 重绘，静态层与 hover 层分离；零 Token 会话不再落档、导入或展示 | 418 |
| 09-08 | v2.1.1 发布 | 档案总量/详情工作台统一、TTFT、零 Token 会话清理、会话标题展示修正、模型图片/视频/音频能力三态；正式产物/源码/tag/GitHub 同步 | 418 |

## 五、历史存档（v0.2.0 当轮，2026-08-16）

> 环境：Windows 10.0.26200 (x64)，.NET SDK 6.0.136，WinUI 3 / Windows App SDK 1.5。
> 该轮「尚未执行」清单中的 GitHub Release / Portable 产物验证至今仍未做（既定延后项）。

| 项目 | 结果 |
|---|---|
| `dotnet build -p:Platform=x64` | 通过，0 警告 0 错误 |
| `build.ps1` Release publish | 通过，无 `NETSDK1179` 警告 |
| `--version` | `DshController 0.2.0` |
| `--check`（默认 3080 只读） | 通过，dsh 已解析，3080 为 UP |
