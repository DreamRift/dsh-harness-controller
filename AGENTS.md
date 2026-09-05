# 开发区工作约定（dev）

> 完整版在上一级：`..\AGENTS.md`；项目总览：`..\README.md`；
> **动手前必读**：`docs\DEVELOPMENT.md`（结构、落点对照表、发布流程、踩过的坑）。

## 你在哪儿

这里是 **开发区**（纯英文路径，含 git 仓库）。同级还有：
- `..\应用\` 用户正在用的正式版产物 —— **只读**，只在发布时被本目录的产物整体覆盖；
- `..\源码\` 与正式版一一对应的干净源码快照 —— **只读**，不要在那里改或构建。

## 三条铁律

1. **界面不自己取数**：实例相关信息一律从实例档案读（`ArchiveHub` / `ArchiveService`）；
   页面里不允许扫盘、探端口、起进程、拉网络。
2. **门禁不许绕**：`tools\check-conventions.ps1` 构建前必跑；例外登记进
   `tools\conventions-allow.txt` 并写理由，台账**只减不增**。
3. **改动必须有验证**：纯逻辑 xUnit → 视图模型单测 → CLI 自检 → GUI 冒烟。

## 完成前必跑

```powershell
powershell -ExecutionPolicy Bypass -File tools\check-conventions.ps1     # PASS
dotnet test tests\DshController.Tests\DshController.Tests.csproj         # 全绿（当前 176 条）
dotnet build DshController.slnx -nologo                                  # 0 警告 0 错误
```

真实环境验证用 `--selftest-plugins` / `--archive-check` / `--selftest-core`；
**端口纪律**：动态测试只用 ≥3185，3080 是用户在跑的后端，只读探测。
