# 收口验证报告 · 四件套全绿（两轨并行）

日期：2026-09-03。开发轨（执行方自跑，clean 重建后）与验证轨（独立 subagent 冷跑，不读取开发轨结论）各跑一遍，逐项对照。

| 项 | 命令 | 开发轨 | 验证轨 | 差异 |
|---|---|---|---|---|
| 门禁 | tools\check-conventions.ps1 | exit 0 · 107 文件 · PASS | exit 0 · 107 文件 · PASS | 0 |
| 单测 | dotnet test | exit 0 · **176/176** | exit 0 · **176/176** | 0 |
| 构建 | dotnet build DshController.slnx（开发轨先 `dotnet clean`，并加 `-warnaserror`） | exit 0 · 0 警告 0 错误 | exit 0 · 0 警告 0 错误 | 0 |
| GUI 冒烟 | 启动→12s→CloseMainWindow→4s | closed-ok · alive=True · crash.log=False | closed=True · alive=True · crash 前后=False | 0 |
| 自检·插件 | --selftest-plugins（全离线） | exit 0 · PASS 61 / FAIL 0 | exit 0 · PASS 61 / FAIL 0 | 0 |
| 自检·档案 | --archive-check | exit 0 · 体检通过（3 档案，0 失败） | exit 0 · 体检通过 | 0 |
| 自检·核心 | --selftest-core（真实起停/重启，本日组内已跑两轮） | exit 0 · **89 passed / 0 failed**（拆分前后各一轮） | 终审未重跑（重进程项），由其余两轨+全部离线面背书 | 说明性差异（非数值差异） |

## 测试数量只增不减
153（重构 2.0 基线，v2.0.0）→ 160（重启 R4 单测 +7）→ 169（用量钻取查询 +9）→ **176**（用量视图状态迁移 +7）。本轮无任何用例删除（删除需理由，全部为新增）。

## crash.log
两轨均为 False（验证轨做了前后双测排除历史残留）；crash.log 检查位置 = exe 同目录（运行时状态实际落 %LOCALAPPDATA%\DshController\，两处皆无新增）。

## 端口纪律遵守声明
所有真实自检仅用 ≥3185（3185/3186/3187/3195/3196…）；3080 用户在跑后端全程只读探测（archive-check/selftest 均只读）。

## 遗留挂账（交给后续小类/收口报告）
1. 人眼真机目检三清单：①重启 GUI 连点不弹浏览器 ②用量新页 vs 定稿线框 ③按钮 960/1280/1600 三档无截断 + 两处设置区走查（改值→保存→重开→生效）——机器证据已全绿，主观体感需用户/红队过目。
2. 台账剩余 5 条 R1（MainWindow/HomeManager/InstanceDiscovery/Cli/CoreSelfTest，437~571 行）——用户已裁决留台账，另开工单。
3. npx 指定版本形态与 WSL 实例的真机重启仅单测覆盖（本机无该形态实例）。