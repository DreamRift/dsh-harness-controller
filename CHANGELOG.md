# Changelog

本项目遵循 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/) 风格，
版本号遵循 [Semantic Versioning](https://semver.org/lang/zh-CN/)。

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
