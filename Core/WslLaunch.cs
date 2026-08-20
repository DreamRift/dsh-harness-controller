// ============================================================================
//  WslLaunch — WSL 实例的启动脚本生成与发行版内停止/关闭（v0.4.0）
//
//  从 DshWslCtrl 验证版移植并裁剪为 DshController 前台模型：
//    启动脚本 = cd 工作区 + pidfile + exec dsh web（输出经 wsl.exe UTF-8 中继，
//              直接复用 BackendManager 现有的输出管道/就绪探测/状态机）；
//    停止 = pidfile 校验后进程组 TERM→KILL 升级，再按策略智能关闭发行版/VM。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace DshController.Core
{
    public delegate void WslLogLine(string line);

    public static class WslLaunch
    {
        /// <summary>识别“发行版内任意 harness 进程”的 pgrep 模式（[x] 技巧避免匹配 wrapper 自身）。</summary>
        public const string AnyDshPattern = @"@deepseek-ai/[d]sh|[d]sh web";

        /// <summary>
        /// 生成发行版内启动脚本（LF 行尾）。前台模式：exec 直接替换为 dsh。
        /// v0.5.0：harnessVersion 非空时 exec npx --yes @deepseek-ai/dsh@&lt;版本&gt; web ...，
        /// 并把 npx 所在 bin 目录（与 node/npm 同目录）加入 PATH。
        /// --exec bash 是非登录 shell，dsh 是 npm shim、内部 exec node 依赖 PATH，
        /// 因此显式把 dsh 所在 bin 目录（与 node 同目录）加入 PATH。
        /// </summary>
        public static string BuildLaunchScript(int port, string wslWorkspace, string dshCmd,
            IReadOnlyList<string> trustedHosts, string harnessVersion = "", string npxDir = "")
        {
            string pinned = (harnessVersion ?? "").Trim();
            var sb = new StringBuilder();
            sb.Append("#!/bin/bash\n");
            sb.Append("# DshController WSL 实例启动脚本 port=").Append(port).Append("\n");
            if (pinned.Length > 0 && npxDir.Length > 0)
            {
                sb.Append("export PATH=\"").Append(npxDir).Append(":$PATH\"\n");
            }
            else
            {
                int slash = dshCmd.LastIndexOf('/');
                if (slash > 0)
                {
                    string dshDir = dshCmd.Substring(0, slash);
                    sb.Append("export PATH=\"").Append(dshDir).Append(":$PATH\"\n");
                }
            }
            sb.Append("cd ").Append(WslTools.Shq(wslWorkspace))
              .Append(" || { echo \"[dsh] 工作区不可访问: ").Append(wslWorkspace).Append("\" >&2; exit 1; }\n");
            sb.Append("echo $$ > /tmp/dshwsl-").Append(port).Append(".pid\n");

            if (pinned.Length > 0)
            {
                sb.Append("exec npx --yes @deepseek-ai/dsh@").Append(pinned);
            }
            else
            {
                sb.Append("exec ").Append(WslTools.Shq(dshCmd));
            }
            sb.Append(" web --host 127.0.0.1 --port ").Append(port);
            if (trustedHosts != null)
                foreach (var h in trustedHosts)
                    sb.Append(" --trusted-host ").Append(WslTools.Shq(h));
            sb.Append("\n");
            return sb.ToString();
        }

        /// <summary>
        /// 解析 WSL 内 dsh 绝对路径（登录 shell 以加载 profile 的 PATH）。
        /// 排除 /mnt 开头的 Windows shim（Linux 环境下路径语义混乱），只认 WSL 原生 dsh。
        /// </summary>
        public static async Task<string> ResolveWslDshAsync(string distro, string configuredCommand)
        {
            string cmd = string.IsNullOrWhiteSpace(configuredCommand) ? "dsh" : configuredCommand.Trim();
            var r = await WslTools.RunInDistroAsync(distro,
                "command -v " + WslTools.Shq(cmd) + " 2>/dev/null; echo '---'; " +
                "type -aP " + WslTools.Shq(cmd) + " 2>/dev/null || true");
            if (!r.Ok) return "";
            foreach (var line in WslTools.SplitLines(r.Output))
            {
                var p = line.Trim();
                if (p.Length == 0 || p == "---") continue;
                if (!p.StartsWith("/", StringComparison.Ordinal)) continue;
                if (p.StartsWith("/mnt/", StringComparison.Ordinal)) continue;
                return p;
            }
            return "";
        }

        /// <summary>
        /// 发行版内停止单个 harness 实例：pidfile 精确路径优先（校验 cmdline 防误杀），
        /// 进程组 TERM→KILL 升级，最后清理 pidfile/脚本。
        /// </summary>
        public static async Task StopHarnessInDistroAsync(string distro, int port, WslLogLine log)
        {
            long pid = 0;
            var pidRaw = await WslTools.ReadDistroFileAsync(distro, "/tmp/dshwsl-" + port + ".pid");
            if (pidRaw != null) long.TryParse(pidRaw.Trim(), out pid);

            bool signaled = false;
            if (pid > 0)
            {
                var argsLine = (await WslTools.RunInDistroAsync(distro,
                    "ps -o args= -p " + pid + " 2>/dev/null || true")).Output;
                if (argsLine.Contains("--port " + port))
                {
                    var pgidRaw = (await WslTools.RunInDistroAsync(distro,
                        "ps -o pgid= -p " + pid + " 2>/dev/null || true")).Output.Trim();
                    if (long.TryParse(pgidRaw, out var pgid) && pgid > 0)
                    {
                        log?.Invoke("  SIGTERM → 进程组 " + pgid + "（主进程 " + pid + "）");
                        await WslTools.RunInDistroAsync(distro,
                            "kill -TERM -- -" + pgid + " 2>/dev/null || true");
                        signaled = true;
                    }
                }
            }
            string pattern = "[p]ort " + port; // [x] 技巧：wrapper 自身命令行不含字面 "--port N"
            if (!signaled)
            {
                log?.Invoke("  SIGTERM → 匹配进程（pattern: port " + port + "）");
                await WslTools.RunInDistroAsync(distro,
                    "pkill -TERM -f " + WslTools.Shq(pattern) + " 2>/dev/null || true");
            }
            for (int i = 0; i < 20; i++)
            {
                var left = await WslTools.PgrepAsync(distro, pattern);
                bool tcpClosed = !await WslTools.ProbeTcpAsync("127.0.0.1", port, 800);
                if (left.Count == 0 && tcpClosed) break;
                await Task.Delay(500);
            }
            var still = await WslTools.PgrepAsync(distro, pattern);
            if (still.Count > 0)
            {
                log?.Invoke("  超时未退出，升级 SIGKILL ...");
                await WslTools.RunInDistroAsync(distro,
                    "pkill -KILL -f " + WslTools.Shq(pattern) + " 2>/dev/null || true");
            }
            await WslTools.RunInDistroAsync(distro,
                "rm -f /tmp/dshwsl-" + port + ".pid /tmp/dshwsl-" + port + ".sh 2>/dev/null || true");
        }

        /// <summary>
        /// 按策略智能关闭：发行版内已无 harness 实例 → wsl -t 终止发行版；
        /// always → 无条件 wsl --shutdown；smart → 无其他发行版运行才 --shutdown；
        /// distroOnly → 只 -t（VM 由系统空闲后自动回收）。
        /// </summary>
        public static async Task SmartShutdownAsync(string distro, string policy, WslLogLine log)
        {
            var remaining = await WslTools.PgrepAsync(distro, AnyDshPattern);
            if (remaining.Count > 0)
            {
                log?.Invoke("  发行版内仍有 " + remaining.Count + " 个 harness 进程（其他实例），跳过发行版终止");
                return;
            }
            log?.Invoke("  终止发行版 " + distro + " ...");
            await WslTools.TerminateDistroAsync(distro);

            if (policy != null && policy.Equals("always", StringComparison.OrdinalIgnoreCase))
            {
                log?.Invoke("  always 策略: wsl --shutdown ...");
                await WslTools.ShutdownVmAsync();
            }
            else if (policy == null || policy.Equals("smart", StringComparison.OrdinalIgnoreCase))
            {
                var running = await WslTools.ListRunningDistrosAsync();
                if (running.Count == 0)
                {
                    log?.Invoke("  无其他发行版运行，wsl --shutdown 立即释放 VM 资源 ...");
                    await WslTools.ShutdownVmAsync();
                }
                else
                {
                    log?.Invoke("  仍有其他发行版在运行（" + string.Join(", ", running) + "），跳过 wsl --shutdown");
                }
            }
        }
    }
}