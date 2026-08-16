# DshController v0.2.0 测试结果

> 执行日期：2026-08-16
> 环境：Windows 10.0.26200 (x64)，.NET SDK 6.0.136，WinUI 3 / Windows App SDK 1.5
> dsh：由 `%APPDATA%\npm\dsh.cmd` 解析，现有 3080 外部后端保持运行

## 执行摘要

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
| 任务栏/exe 图标 | 通过，`app.ico` 改为官方风格白底黑色鲸鱼；exe 提取图标约 21% 黑、72% 白（40px），16px 黑色占比约 25% |
| 外部实例直接重启 | 通过，确认后停止外部测试实例并重新启动，Ready 抑制浏览器 |

## 核心自检覆盖

1. `launcher.json` 多重反斜杠污染净化与 UNC 双反斜杠保留。
2. 失败注入：`fail-dsh.cmd` 退出码 42，BackendManager 触发 `StartFailed`，
   错误报告写入自定义目录，包含解析轨迹、配置、输出转录、排障建议和退出码。
3. 真实 dsh 启动：端口监听，状态进入 Running（本程序），Ready 不抑制浏览器。
4. 重启：后端 PID 更换，Ready 事件 `SuppressAutoOpen=true`，无失败报告。
5. 停止：端口释放，状态回到 Stopped。
6. 外部实例直接重启：管理器初始状态为 Stopped 时，仍能停止外部测试实例并启动本程序后端，
   且 Ready 事件 `SuppressAutoOpen=true`。

## GUI 验证

- 主窗口标题：`DSH Harness 控制器`
- 当前 3080 为外部实例时显示：`运行中 · 外部进程`，PID 按运行环境实际值显示
- 操作按钮完整：启动、重启、停止、打开界面；外部运行态下启动按钮禁用，停止/重启可用
- 日志区、复制 URL、自动滚动、清空按钮与页脚 dsh 解析信息正常
- 明暗主题通过主题按钮切换并持久化，最终恢复为 `system`

## 发布产物

- `publish\DshController.exe`（旧版，正在运行；后续手动关闭后可替换为修复版）
- `publish-fixed\DshController.exe`（修复版，119 MB，框架依赖 .NET 6 Desktop Runtime，WASDK 自包含）

## 尚未执行

- GitHub Release 发布（`gh release create`）
- `build.ps1 -Portable` 全自包含产物验证
- 浏览器进程级断言（当前通过 Ready 事件抑制标志验证，未额外断言浏览器进程未创建）
