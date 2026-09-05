// ============================================================================
//  BackendManager · WSL 启停（partial 分部；纯搬移，语义与拆分前逐字一致）
// ============================================================================

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using DshController.Core.Abstractions;
using DshController.Core.Storage;

namespace DshController.Core
{
    public sealed partial class BackendManager
    {
        // ==================== WSL 实例（v0.4.0） ====================

        /// <summary>
        /// WSL 实例启动：预检（WSL/发行版/用户/dsh/工作区）→ 写启动脚本 →
        /// spawn wsl.exe -d distro --exec bash /tmp/dshwsl-&lt;port&gt;.sh →
        /// 复用 ReadyLoopAsync（Windows 侧 TCP 探测，经 localhost 转发）。
        /// DSH_HOME 经 WSLENV=DSH_HOME/u 传入（值原样，不做路径翻译）。
        /// </summary>
        private async Task<bool> StartWslCoreAsync(Config cfg, StartOptions opts)
        {
            string distro = string.IsNullOrWhiteSpace(cfg.WslDistro) ? "Ubuntu-24.04" : cfg.WslDistro.Trim();
            string pinnedVersion = (cfg.HarnessVersion ?? "").Trim();

            ClearDiag();
            Diag("运行环境", "WSL2（发行版内运行）");
            Diag("实例", (cfg.InstanceName ?? "") + "（" + (cfg.InstanceId ?? "") + "）");
            Diag("发行版(配置)", distro);
            Diag("harness 版本", pinnedVersion.Length > 0
                ? "指定 " + pinnedVersion + "（发行版内 npx 拉取）"
                : "跟随发行版内主实例版本");

            // 1) 预检：WSL 本体
            if (!await WslTools.IsInstalledAsync().ConfigureAwait(false))
            {
                FailStart(cfg, "WSL 未安装",
                    "WSL2 尚未安装。请在管理员 PowerShell 运行 wsl --install --no-distribution（装完可能需重启一次）。", null, null);
                return false;
            }
            // 2) 发行版存在
            var distros = await WslTools.ListDistrosAsync().ConfigureAwait(false);
            Diag("已安装发行版", distros.Count > 0 ? string.Join(", ", distros) : "（无）");
            if (!distros.Contains(distro, StringComparer.OrdinalIgnoreCase))
            {
                FailStart(cfg, "发行版不存在",
                    "发行版 " + distro + " 未安装（已安装: " + (distros.Count > 0 ? string.Join(", ", distros) : "无")
                    + "）。请在管理员 PowerShell 运行 wsl --install -d " + distro, null, null);
                return false;
            }
            // 3) 唤醒发行版（自动冷启动）
            var ping = await WslTools.RunInDistroAsync(distro, "echo DSHWSL_PING_OK", 180000).ConfigureAwait(false);
            if (!ping.Ok || ping.Output.Trim() != "DSHWSL_PING_OK")
            {
                FailStart(cfg, "发行版无响应", "wsl -d " + distro + " 命令执行失败：" + ping, null, null);
                return false;
            }
            // 4) 用户与 DSH_HOME
            var user = await WslTools.GetDistroUserAsync(distro).ConfigureAwait(false);
            if (string.IsNullOrEmpty(user) || user == "root")
            {
                FailStart(cfg, "发行版用户未初始化",
                    "默认用户是 " + (string.IsNullOrEmpty(user) ? "(无响应)" : "root") + "。请先运行 wsl -d " + distro + " 完成首次用户设置。", null, null);
                return false;
            }
            var distroHomeRoot = await WslTools.GetDistroHomeAsync(distro).ConfigureAwait(false);
            if (string.IsNullOrEmpty(distroHomeRoot))
            {
                FailStart(cfg, "无法读取发行版 $HOME", distro + " 的 $HOME 为空。", null, null);
                return false;
            }
            string wslHome = WslTools.ResolveLinuxPath(cfg.WslHome, distroHomeRoot);
            await WslTools.RunInDistroAsync(distro, "mkdir -p " + WslTools.Shq(wslHome)).ConfigureAwait(false);
            Diag("发行版用户", user);
            Diag("Linux $HOME", distroHomeRoot);
            Diag("DSH_HOME(Linux)", wslHome);

            // 首次启动：WSL home 无凭据且 Windows 侧存在 → 自动复制 settings.yaml/.credentials.yaml（chmod 600）
            var credCheck = await WslTools.RunInDistroAsync(distro,
                "test -f " + WslTools.Shq(wslHome + "/.credentials.yaml") + " && echo DSHWSL_HAS || echo DSHWSL_NONE").ConfigureAwait(false);
            if (credCheck.Output.Trim() != "DSHWSL_HAS")
            {
                string winDsh = AppPaths.DefaultDshHome;
                if (System.IO.File.Exists(System.IO.Path.Combine(winDsh, ".credentials.yaml")))
                {
                    string wslWinDsh = await WslTools.WindowsToWslPathAsync(distro, winDsh).ConfigureAwait(false);
                    string syncCmd = string.Join("\n",
                        "for f in settings.yaml .credentials.yaml; do",
                        "  if [ -f " + WslTools.Shq(wslWinDsh + "/$f") + " ]; then cp -f " +
                        WslTools.Shq(wslWinDsh + "/$f") + " " + WslTools.Shq(wslHome + "/$f") + "; fi;",
                        "done",
                        "chmod 600 " + WslTools.Shq(wslHome + "/.credentials.yaml") + " 2>/dev/null || true",
                        "echo DSHWSL_CRED_SYNCED");
                    var syncR = await WslTools.RunInDistroAsync(distro, syncCmd).ConfigureAwait(false);
                    LogUi(syncR.Ok && syncR.Output.Contains("DSHWSL_CRED_SYNCED")
                        ? "已从 Windows 侧自动同步凭据/设置到 " + wslHome
                        : "凭据自动同步未完成（可手动在发行版内配置）");
                }
            }

            // 5) 启动命令：指定版本 → 发行版内 npx（不要求发行版已装 dsh）；
            //                跟随环境 → 发行版内原生 dsh（排除 /mnt 的 Windows shim）
            string dshCmd = await WslLaunch.ResolveWslDshAsync(distro, cfg.DshCommand).ConfigureAwait(false);
            Diag("发行版内 dsh", string.IsNullOrEmpty(dshCmd) ? "(未找到)" : dshCmd);
            string npxDir = "";
            string launchDsh = dshCmd;
            if (pinnedVersion.Length > 0)
            {
                string npx = await HarnessVersion.ResolveWslNpxAsync(distro).ConfigureAwait(false);
                Diag("发行版内 npx", string.IsNullOrEmpty(npx) ? "(未找到)" : npx);
                if (string.IsNullOrEmpty(npx))
                {
                    FailStart(cfg, "发行版内 npx 未找到",
                        "实例指定了 harness 版本 " + pinnedVersion + "，需要发行版内 npx 拉取指定版本，" +
                        "但 " + distro + " 内未找到原生 npx。请在该发行版内安装 Node.js/npm，" +
                        "或把该实例的 harness 版本改回\"跟随当前环境\"。",
                        null, null);
                    return false;
                }
                npxDir = HarnessVersion.DirOf(npx);
                launchDsh = "npx"; // 启动脚本内 exec npx --yes @deepseek-ai/dsh@<ver> web ...
                LogUi("harness 版本: 指定 " + pinnedVersion + "（发行版内 npx " + npx + " 拉取，首次使用需联网下载）");
            }
            else
            {
                if (string.IsNullOrEmpty(dshCmd))
                {
                    FailStart(cfg, "WSL 内 dsh 未找到",
                        "WSL " + distro + " 内未找到原生 dsh（已排除 /mnt 下的 Windows shim）。" +
                        "请在该发行版内执行: npm install -g @deepseek-ai/dsh；" +
                        "或给该实例指定一个 harness 版本，改用发行版内 npx 拉取该版本启动。", null, null);
                    return false;
                }
                LogUi("harness 版本: 跟随当前环境（" + dshCmd + "）");
            }

            // 6) 工作区路径：Windows 路径（C:\...）转 /mnt/c 按需共享；Linux 路径（~/ 或 /）原生隔离
            string wslWs;
            if (WslTools.IsWindowsPath(cfg.Workspace))
                wslWs = await WslTools.WindowsToWslPathAsync(distro, cfg.Workspace).ConfigureAwait(false);
            else
                wslWs = WslTools.ResolveLinuxPath(cfg.Workspace, distroHomeRoot);
            var dirCheck = await WslTools.RunInDistroAsync(distro,
                "mkdir -p " + WslTools.Shq(wslWs) + "; test -d " + WslTools.Shq(wslWs) + " && echo DSHWSL_YES || echo DSHWSL_NO").ConfigureAwait(false);
            Diag("工作区(Linux)", wslWs + (dirCheck.Output.Trim() == "DSHWSL_YES" ? "" : "（不可访问）"));
            if (dirCheck.Output.Trim() != "DSHWSL_YES")
            {
                FailStart(cfg, "工作区不可访问", "工作区 " + wslWs + " 在 " + distro + " 内无法访问。", null, null);
                return false;
            }

            // 7) 写启动脚本（经 /mnt/c 拷贝，规避引号转义）
            string uploadDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "DshController-wsl");
            string scriptPath = "/tmp/dshwsl-" + cfg.Port + ".sh";
            // R4·子进程侧：重启（SuppressAutoOpen）时发行版内 dsh web 同样须带 --no-open
            string script = WslLaunch.BuildLaunchScript(cfg.Port, wslWs, launchDsh, cfg.TrustedHosts,
                pinnedVersion, npxDir, opts.SuppressAutoOpen);
            foreach (string sl in script.Split('\n'))
                if (sl.StartsWith("exec ", StringComparison.Ordinal)) LastSpawnArguments = sl;
            Diag("启动脚本", distro + ":" + scriptPath);
            string noOpenTag = opts.SuppressAutoOpen ? " --no-open" : "";
            Diag("启动命令", (pinnedVersion.Length > 0
                ? "npx --yes @deepseek-ai/dsh@" + pinnedVersion + " web"
                : dshCmd + " web") + noOpenTag + " --host 127.0.0.1 --port " + cfg.Port);
            if (!await WslTools.WriteDistroFileAsync(distro, scriptPath, script, uploadDir).ConfigureAwait(false))
            {
                FailStart(cfg, "写入启动脚本失败", "无法把启动脚本写入 " + distro + ":/tmp/dshwsl-" + cfg.Port + ".sh", null, null);
                return false;
            }

            // 8) spawn wsl.exe（CREATE_NO_WINDOW：不挂接父控制台；stdout/stderr UTF-8 中继复用现有管道）
            var psi = new ProcessStartInfo
            {
                FileName = "wsl.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            psi.ArgumentList.Add("-d");
            psi.ArgumentList.Add(distro);
            psi.ArgumentList.Add("--exec");
            psi.ArgumentList.Add("bash");
            psi.ArgumentList.Add(scriptPath);
            psi.EnvironmentVariables["DSH_HOME"] = wslHome;
            string wslenv = psi.EnvironmentVariables.ContainsKey("WSLENV")
                ? psi.EnvironmentVariables["WSLENV"] : "";
            psi.EnvironmentVariables["WSLENV"] = WslTools.AppendWslenv(wslenv, "DSH_HOME/u");

            SetState(BackendState.Starting, false, 0);
            ClearRing();
            LogUi("WSL 实例: 发行版 " + distro + " | 用户 " + user + " | DSH_HOME " + wslHome);
            LogUi("         工作区 " + cfg.Workspace + " (=WSL " + wslWs + ") | dsh " + dshCmd);

            var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            p.OutputDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data)) OnChildLine(e.Data, false); };
            p.ErrorDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data)) OnChildLine(e.Data, true); };
            p.Exited += (s, e) => OnChildExited(p);
            try
            {
                if (!p.Start())
                {
                    FailStart(cfg, "进程创建失败", "wsl.exe Process.Start 返回 false。", null, null);
                    return false;
                }
            }
            catch (Exception ex)
            {
                FailStart(cfg, "进程启动异常（SpawnError）", ex.Message, ex, null);
                return false;
            }
            lock (_gate) { _child = p; _childPid = p.Id; _mine = true; }
            LogUi("已启动 wsl.exe（PID " + p.Id + "，发行版 " + distro + "）");
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            StartPump();

            _cancelDueToStop = false;
            _readyCts = new CancellationTokenSource();
            var ct = _readyCts.Token;
            var thisCfg = cfg;
            var thisOpts = opts;
            _readyTask = ReadyLoopAsync(thisCfg, thisOpts, p, p.Id, ct);
            return true;
        }

        /// <summary>
        /// WSL 实例停止：杀 wsl.exe 宿主 → 发行版内 pidfile 精确路径 + 进程组 TERM→KILL
        /// → 按 wslShutdownPolicy 智能关闭发行版/VM（发行版内还有其他实例则跳过）。
        /// </summary>
        private async Task<bool> StopWslCoreAsync(Config cfg, bool killExternal, BackendState intermediateState)
        {
            Process mine; int minePid;
            lock (_gate) { mine = _child; minePid = _childPid; }
            bool mineAlive = IsChildAlive(mine);
            bool up = await PortTools.ProbeAsync(cfg.Host, cfg.Port).ConfigureAwait(false);

            if (!mineAlive && !up)
            {
                LogUi("后端当前未运行，无需停止。");
                SetState(BackendState.Stopped, false, 0);
                return true;
            }
            if (!mineAlive && up && !killExternal)
            {
                LogUi("检测到外部后端在线；未授权结束外部进程，已跳过。");
                SetState(BackendState.Running, mine: false, pid: 0);
                return false;
            }

            SetState(intermediateState, mineAlive, minePid);
            _cancelDueToStop = true;
            try { if (_readyCts != null) _readyCts.Cancel(); }
            catch
            {
                // 理由: 取消就绪等待取消源，可能已释放，静默忽略（停止路径属正常流程）
            }
            if (_readyTask != null)
            {
                try { await Task.WhenAny(_readyTask, Task.Delay(8000)).ConfigureAwait(false); }
                catch
                {
                    // 理由: 等待就绪任务至多8秒，任务被取消/异常均属停止流程，静默跳过
                }
            }

            string distro = string.IsNullOrWhiteSpace(cfg.WslDistro) ? "Ubuntu-24.04" : cfg.WslDistro.Trim();

            // 1) 杀 wsl.exe 宿主（断开客户端；Linux 侧 harness 由下一步显式处理）
            if (mineAlive)
            {
                LogUi("正在终止 wsl.exe 宿主（PID " + minePid + "）…");
                try { mine.Kill(entireProcessTree: true); mine.WaitForExit(5000); }
                catch
                {
                    // 理由: wsl.exe 宿主可能已自行退出，终止/等待失败无副作用，尽力而为
                }
            }

            // 2) 发行版内停止 harness（pidfile 校验 + 进程组 TERM→KILL；同发行版多实例互不影响）
            if (await WslTools.IsDistroRunningAsync(distro).ConfigureAwait(false))
            {
                LogUi("正在停止 " + distro + " 内的 harness（端口 " + cfg.Port + "）…");
                await WslLaunch.StopHarnessInDistroAsync(distro, cfg.Port, LogUi).ConfigureAwait(false);

                // 3) 按策略智能关闭发行版 / VM
                LogUi("WSL 收尾 ...");
                await WslLaunch.SmartShutdownAsync(distro, cfg.WslShutdownPolicy, LogUi).ConfigureAwait(false);
            }
            else
            {
                LogUi("发行版 " + distro + " 未在运行，无需停止。");
            }

            lock (_gate) { _child = null; _childPid = 0; _mine = false; }
            bool down = !await PortTools.ProbeAsync(cfg.Host, cfg.Port).ConfigureAwait(false);
            LogUi(down ? "后端已停止，端口已释放。" : "端口仍在监听，后端可能未完全退出。");
            SetState(BackendState.Stopped, false, 0);
            return down;
        }

    }
}
