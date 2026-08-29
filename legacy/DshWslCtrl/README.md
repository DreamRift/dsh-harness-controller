# DshWslCtrl — WSL 控制验证版（上一代项目）

DshController v0.4.0 的 `Core/WslTools.cs` 即从本项目的 `WslTools.cs` 移植
（wsl.exe 封装 / UTF-16LE 输出解码 / 发行版管理 / 路径转换 / 经 /mnt/c 的
文件上传），详见主 README「WSL2 实例」章节。本目录仅作历史参考留档：

- `WslTools.cs` / `Config.cs` — 验证过的 WSL 互操作层源码
- `Assets/`、`scripts/fetch-debian-rootfs.ps1` — 配套资源与 Debian rootfs 获取脚本

不参与主工程编译（csproj 对 `legacy\**` 有排除规则）。原 bin/obj/publish
构建产物已于 2026-08-29 整理时删除。
