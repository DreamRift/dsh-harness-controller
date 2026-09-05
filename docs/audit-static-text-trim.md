# 静态说明文字清理·前后对照清单（小类：静态说明文字清理）

规则：删=纯静态解释句（无数据绑定）；其说明义务由 tooltip 承接或本就冗余。动态状态提示、输入占位符、设计台（--dev GalleryView）、定稿用量页卡下口径注全部保留。
（行号为删除前位置；删除后行号自然上移。）

| # | 文件:行 | 原文（节选） | 处置 | 说明义务去向 |
|---|---|---|---|---|
| M1 | MainWindow.xaml:73 | 管理本地运行时 | 删 | 导航窗格只剩「工作台」eyebrow |
| M2 | MainWindow.xaml:159 | 配置所有实例共用的目录、工作区与插件市场连接。 | 删 | 纯复述页名 |
| M3 | MainWindow.xaml:166 | 这些设置会影响新建实例、诊断报告和插件目录。 | 删 | 节内字段各有 tooltip |
| M4 | MainWindow.xaml:256-257 | 实例信息统一存进「实例档案」…0 = 只在手动刷新…（两行长段） | 删→收拢 | 「数据刷新」标题 tooltip 承接精简版（含 0=只手动） |
| M5 | MainWindow.xaml:307-314 | 报告目录与 dshCommand 修改后立即生效…（整块信息条） | 删→收拢 | 「应用设置」标题 tooltip 承接全文 |
| I1 | InstancePanel.xaml:19-20 + xaml.cs:108-110 | 选择一个实例，查看（Windows/WSL）…后端状态。（含代码内双态赋值） | 删 | eyebrow/title 已按环境切换（WSL/WINDOWS RUNTIME WORKSPACE），副题属重复 |
| I2 | InstancePanel.xaml:276-277 | 跟随当前环境会直接使用已安装的 dsh；指定版本会通过 npx 拉取。 | 删→收拢 | 版本下拉 tooltip 承接 |
| I3 | InstancePanel.xaml:297-298 | smart = 无其他实例时再关闭发行版或 VM | 删→收拢 | 关闭策略下拉 tooltip 承接（列内 TextBlock 占宽即除） |
| P1 | PluginManagePanel.xaml:24-25 | 读取实例 HOME 中的真实安装状态，升级和卸载均通过 DSH 官方命令完成。 | 删 | 页头只剩 eyebrow+标题 |
| P2 | PluginMarketPanel.xaml:31 | 搜索社区插件，并按 DSH 官方方式安装到指定实例。 | 删 | 同上 |

## 明确保留（清理边界说明）
- 用量页每张统计卡下的口径小注（来自会话日志 / 会话总账口径 / 缓存读·输入合计）：**定稿方案 §2 明文要求保留**，评审门已锁定。
- 一切 ToolTipService.ToolTip、PlaceholderText（输入格式提示）、StatusText/TxtArchiveInfo/TxtFreshness 等动态状态。
- 设计台 GalleryView 与 DshTheme.xaml 中 PageSubtitle 样式定义本身（样式仍被设计台展示用）。
- 导航项标签（本就短词）、实例页各字段标题（FieldLabel 为标签非说明句）。

## 验证
- dotnet build：0 警告 0 错误；dotnet test：176/176；check-conventions：PASS（93 文件）；
- GUI 冒烟：起 12s → CloseMainWindow 正常退出 → 无 crash.log；
- 页头余量复查：全应用 xaml 中 PageSubtitle 仅剩设计台 2 处 + 样式定义 1 处。