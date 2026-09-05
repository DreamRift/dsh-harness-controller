# 重启拉浏览器·路径排查报告（小类：重启路径排查）

日期：2026-09。本文件为计划 #L2「重启路径排查」交付物：只排查不修复；修复方向记录供下一小类「重启不弹浏览器」使用。

## 0. 结论（根因）

**新浏览器窗口不是控制器弹的，是子进程 dsh web 自己弹的。** DSH web-app 默认在就绪时自动打开默认浏览器：

- 打开判定：dsh-web-app/lib/index.js:174 —— handoffBrowser = config.openBrowser && !launchedThroughSsh(ctx)；
  index.js:200-206 —— 打印 "dsh web: opening the default browser; pass --no-open to disable" 并执行 internals.openBrowser(webUrl)；
- 开关：dsh-web-app/lib/startup.js:22 —— 提供 --no-open flag（startup.js:43 openBrowser: options.open，缺省为开）。

控制器重启链路已经抑制了控制器侧开浏览器（BackendManager.cs:138-171：RestartAsync → StartCoreAsync 传 SuppressAutoOpen=true，line 168；
UI 侧 InstancePanel.Status.cs:234-253 检查 e.SuppressAutoOpen 后只记日志），
**但全工程没有任何一处给子进程传 --no-open**（grep dev/src *.cs 命中 0）。
重启 = 杀掉旧 dsh 进程 → 起新 dsh 进程 → 新进程自己开浏览器。
CoreSelfTest.cs:160/179-180/243-244 的断言只锁得住控制器侧 Ready 事件，锁不住子进程侧——这正是"代码声称已修、用户仍见新窗口"的原因。

## 1. 四类重启路径逐条追踪

| # | 路径 | 触发动作 | 调用链（行号） | 子进程开浏览器？ | 控制器开浏览器？ |
|---|---|---|---|---|---|
| 1 | UI「重启」按钮 | 实例页点 BtnRestart | InstancePanel.Status.cs:160→177 → InstanceManager.cs:93-106 → BackendManager.cs:139-171（168 行 SuppressAutoOpen=true）→ 命令组装 BackendManager.cs:284-300 **无 --no-open** | **是**（根因） | 否（Status.cs:241 短路） |
| 2 | 插件一键重启 | 装/卸 bundle 后弹窗点「立即重启」 | PluginOpsService.cs:58-82（line 79）→ InstanceManager.cs:93 → 同上 | **是**（同链路） | 否 |
| 3 | CLI restart | DshController.exe instance restart <id> | Cli.cs:416→525-532 → InstanceManager.cs:93 → 同上 | **是**（同链路；CLI 自身不开浏览器） | 否 |
| 4 | WSL/外部进程实例重启 | 对 WSL 实例或被接管的外部进程点「重启」 | 同上 RestartAsync；WSL 停止=StopWslCoreAsync（BackendManager.cs:356-358），启动=StartWslCoreAsync（428 起）→ 脚本 exec dsh web --host 127.0.0.1 --port N（WslLaunch.cs:63 **无 --no-open**）→ ReadyLoopAsync（BackendManager.cs:628） | **是**（发行版内 handoffBrowser 亦真；有 WSLg/wslview 时弹 Windows 浏览器，否则打 "could not open…visit manually"，index.js:204） | 否 |

外部进程实例的"重启"（先 Stop 掉外部 PID 再起自己的）与 #1-3 同链路，根因同一。

## 2. 受控复现实验（端口纪律 ≥3185，实验后已清理）

- dsh web --port 3185 → stdout 出现 "dsh web: opening the default browser; pass --no-open to disable"（子进程自行开浏览器）= 复现成立；
- dsh web --port 3186 --no-open → 仅 "dsh web: http://127.0.0.1:3186"，无 opening 行 = 开关有效；
- taskkill /T /F 双进程树已终止；复测 3185/3186 端口 TCP 探测均 False（已释放）。

## 3. 排查中排除的嫌疑（附证据）

- 子进程退出自动重启：OnChildExited（BackendManager.cs:826-852）只置 Stopped 状态，无重启动作；
- 控制器脚本内 xdg-open/open：WslLaunch.cs grep 0 命中（开浏览器发生在发行版内 dsh 进程侧）；
- 其它 Ready 订阅者开浏览器：全工程 Ready 订阅仅 InstancePanel.Wiring.cs:55（UI，门控见 Status.cs:241）、Cli.cs:475（只收 URL 不开窗）、CoreSelfTest.cs:93/228（只记录）；
- Process.Start(url) 站点全清单：InstancePanel.Status.cs:259（受 SuppressAutoOpen/autoOpen 门控）；MainWindow.xaml.cs:487、InstancePanel.Reports.cs:65/80/153、PluginMarketPanel.xaml.cs:801（打开报告/目录，与重启无关）。

## 4. 启动路径 AutoOpenBrowser 现状记录（本轮不改）

「启动」按钮：InstanceManager.StartAsync（InstanceManager.cs:64-83）→ BackendManager.StartAsync（opts 缺省 SuppressAutoOpen=false，line 122-128）。
现状为双重打开：子进程 dsh 自己开一次 + 实例配置 AutoOpenBrowser=true（InstanceDef.cs:40 默认 true）时控制器 OnBackendReady 再开一次
（InstancePanel.Status.cs:245-247）——即正常启动目前可能弹**两个**标签页；实例设置里「自动打开浏览器」开关（InstancePanel.Settings.cs:86/119）只控制控制器侧那一次。此现状本轮保持，不动。

## 5. 修复方向（供下一小类「重启不弹浏览器」）

重启链路组装 dsh web 命令行时按 opts.SuppressAutoOpen 追加 --no-open：
Windows 三分支（BackendManager.cs:284-300）+ WSL 脚本（WslLaunch.cs:63 一带的 BuildLaunchScript）。
回归断言双层：控制器侧保持 CoreSelfTest 现有断言；子进程侧新增断言（重启路径命令行含 --no-open）。
启动路径是否统一改为"只由控制器开一次"属后续决策，本轮不扩面。