// ============================================================================
//  InstanceDiscovery — 自动发现"正在运行但未注册"的 harness 后端（v0.5.1）
//
//  背景：实例清单（instances.json）按 exe 所在目录存放，换目录/换发布目录后
//  旧清单不在，界面上就看不到仍在运行的后端（尤其 WSL 实例经 wslrelay 代理，
//  端口还在、但局部没有任何注册项）。
//
//  本组件在注册表为空或缺少运行中实例时被调用：
//    ① WSL（v0.5.1 主路径）：枚举运行中发行版 → 发行版内 pgrep harness 进程 →
//       /proc/<pid>/{cmdline,environ,cwd} 反推端口/DSH_HOME/工作区。
//       不依赖 Windows 侧进程信息——新版商店版 WSL（2.x）里 wsl.exe 宿主
//       启动后即退出、由 wslrelay.exe（命令行只有 --vm-id/--handle）接管，
//       旧版"扫 wsl.exe 命令行"的做法在新版上永远匹配不到（v0.5.1 修复）；
//       且发行版内探测对"WSL 终端手动启动的实例"同样有效。
//    ② 旧版 WSL 回退：wsl.exe 宿主命令行（-d <发行版> … dshwsl-<port>.sh），
//       仅旧版 WSL（宿主进程存活）能命中。
//    ③ Windows：netstat 监听端口 → PID → 命令行含 dsh + " web " + --port。
//  发现的实例以 "auto-*" id 追加进注册表（首次保存后持久化），
//  用户可在实例设置里改名/补全后保存，替换成正式配置。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace DshController.Core
{
    public static class InstanceDiscovery
    {
        /// <summary>识别"发行版内任意 harness 进程"的 pgrep 模式（与 WslLaunch.AnyDshPattern 一致）。</summary>
        private const string DistroProbePattern = "@deepseek-ai/[d]sh|[d]sh web";

        /// <summary>扫描并返回"不在给定清单中"的运行中实例（保守：只认 dsh web 后端）。</summary>
        public static List<InstanceDef> Scan()
        {
            var result = new List<InstanceDef>();
            // v0.5.1：跨分支去重键改为 (环境, 端口)——WSL 与 Windows 同端口并存是合法的
            var claimed = new HashSet<(bool Wsl, int Port)>();

            // ① WSL 主路径：发行版内直接探测（新版 WSL 唯一有效路径）
            try
            {
                foreach (InstanceDef d in ScanRunningDistros())
                {
                    if (claimed.Contains((d.IsWsl, d.Port))) continue;
                    claimed.Add((d.IsWsl, d.Port));
                    result.Add(d);
                }
            }
            catch { /* 发现失败不阻断启动 */ }

            try
            {
                var listeners = ListListeners();                 // port -> pid
                if (listeners.Count == 0) return result;
                var cmd = GetProcessCommandLines();              // pid -> commandline

                // ② 旧版 WSL 回退：wsl.exe 宿主存活时命令行含 -d <发行版> … dshwsl-<port>.sh
                var wslHostRx = new Regex(
                    @"-d\s+([A-Za-z0-9_.-]+)[^\r\n]*dshwsl-(\d+)\.sh",
                    RegexOptions.Compiled | RegexOptions.IgnoreCase);
                foreach (var kv in cmd)
                {
                    Match m = wslHostRx.Match(kv.Value ?? "");
                    if (!m.Success) continue;
                    int port = int.Parse(m.Groups[2].Value);
                    string distro = m.Groups[1].Value;
                    if (claimed.Contains((true, port))) continue;
                    claimed.Add((true, port));
                    string wslHome = ReadWslDshHome(distro, port);
                    result.Add(new InstanceDef
                    {
                        Id = "auto-wsl-" + port,
                        Name = "自动发现：WSL " + distro + " :" + port,
                        Host = "127.0.0.1",
                        Port = port,
                        Workspace = "~/",
                        Runtime = "wsl",
                        WslDistro = distro,
                        WslHome = wslHome,
                        AutoOpenBrowser = false,
                        StopOnExit = false,
                        CreatedAt = DateTime.UtcNow
                    });
                }

                // ③ Windows：监听进程命令行含 dsh + " web " + --port <port>
                foreach (var l in listeners)
                {
                    if (claimed.Contains((false, l.Key))) continue;
                    string c = null;
                    cmd.TryGetValue(l.Value, out c);
                    if (string.IsNullOrEmpty(c)) continue;
                    if (LooksLikeWindowsDsh(c, l.Key))
                    {
                        claimed.Add((false, l.Key));
                        result.Add(new InstanceDef
                        {
                            Id = "auto-win-" + l.Key,
                            Name = "自动发现：Windows :" + l.Key,
                            Host = "127.0.0.1",
                            Port = l.Key,
                            Workspace = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                            Runtime = "windows",
                            AutoOpenBrowser = false,
                            StopOnExit = false,
                            CreatedAt = DateTime.UtcNow
                        });
                    }
                }
            }
            catch { }
            return result;
        }

        /// <summary>
        /// 轻量预检：是否存在"不在已知端口集合里"的监听端口（只跑一次 netstat，毫秒级）。
        /// 返回 false 时不需要做完整的进程命令行探测。
        /// </summary>
        public static bool HasUnregisteredListener(ICollection<int> knownPorts)
        {
            try
            {
                foreach (int port in ListListeners().Keys)
                    if (!knownPorts.Contains(port)) return true;
            }
            catch { }
            return false;
        }

        /// <summary>
        /// 轻量预检（v0.5.1）：是否有正在运行的 WSL 发行版（wsl -l --running，毫秒级）。
        /// 有运行中发行版时即使 netstat 无未注册端口（转发异常/镜像网络差异），
        /// 也应做一次完整的发行版内探测。未安装 WSL 时恒为 false。
        /// </summary>
        public static bool HasRunningWslDistro()
        {
            try
            {
                if (!WslTools.IsInstalledAsync().GetAwaiter().GetResult()) return false;
                return WslTools.ListRunningDistrosAsync().GetAwaiter().GetResult().Count > 0;
            }
            catch { return false; }
        }

        // ==================== WSL 发行版内探测（v0.5.1 主路径） ====================

        /// <summary>一行探测结果的解析形态。</summary>
        private sealed class DistroProbeRow
        {
            public int Port;
            public string Home = "";
            public string Cwd = "";
        }

        /// <summary>枚举运行中发行版，逐个做发行版内 harness 进程探测。</summary>
        private static List<InstanceDef> ScanRunningDistros()
        {
            var result = new List<InstanceDef>();
            if (!WslTools.IsInstalledAsync().GetAwaiter().GetResult()) return result;
            List<string> distros = WslTools.ListRunningDistrosAsync().GetAwaiter().GetResult();
            foreach (string distro in distros)
            {
                List<DistroProbeRow> rows = ProbeDistroHarnesses(distro);
                foreach (DistroProbeRow row in rows)
                {
                    result.Add(new InstanceDef
                    {
                        Id = "auto-wsl-" + row.Port,
                        Name = "自动发现：WSL " + distro + " :" + row.Port,
                        Host = "127.0.0.1",
                        Port = row.Port,
                        Workspace = string.IsNullOrEmpty(row.Cwd) ? "~/" : row.Cwd,
                        Runtime = "wsl",
                        WslDistro = distro,
                        WslHome = row.Home ?? "",
                        AutoOpenBrowser = false,
                        StopOnExit = false,
                        CreatedAt = DateTime.UtcNow
                    });
                }
            }
            return result;
        }

        /// <summary>
        /// 发行版内一次性探测：pgrep harness 进程 → /proc 解析端口/DSH_HOME/cwd。
        /// 输出行格式：DSHDISC|&lt;port&gt;|&lt;pid&gt;|&lt;dsh_home&gt;|&lt;cwd&gt;；同端口多进程只取第一条。
        /// pgrep 模式用 [x] 括号技巧避免匹配探测 shell 自身。
        /// </summary>
        private static List<DistroProbeRow> ProbeDistroHarnesses(string distro)
        {
            var rows = new List<DistroProbeRow>();
            try
            {
                // 单次 bash 调用完成全部探测（避免逐进程多次 wsl.exe 往返）。
                // v0.5.1：兼容未带 --port 的实例（默认端口启动）——此时用
                // ss -tlnp 按 pid 反推其 TCP 监听端口。
                string script =
                    "pids=$(pgrep -f '" + DistroProbePattern + "' 2>/dev/null || true); " +
                    "for p in $pids; do " +
                      "args=$(tr '\\0' ' ' < /proc/$p/cmdline 2>/dev/null) || continue; " +
                      "port=''; " +
                      "case \"$args\" in *--port*) " +
                        "port=$(printf '%s' \"$args\" | sed -n 's/^.*--port[ =]\\{1,\\}\\([0-9]\\{1,5\\}\\).*$/\\1/p'); " +
                        ";; *) " +
                        "port=$(ss -tlnp 2>/dev/null | grep \"pid=$p,\" | awk '{n=split($4,a,\":\"); print a[n]}' | head -n1); " +
                        ";; esac; " +
                      "[ -n \"$port\" ] || continue; " +
                      "home=$(tr '\\0' '\\n' < /proc/$p/environ 2>/dev/null | grep '^DSH_HOME=' | head -n1 | cut -d= -f2-); " +
                      "cwd=$(readlink /proc/$p/cwd 2>/dev/null); " +
                      "echo \"DSHDISC|$port|$p|$home|$cwd\"; " +
                    "done";
                var r = WslTools.RunInDistroAsync(distro, script, 30000).GetAwaiter().GetResult();
                if (!r.Ok && r.Output.Length == 0) return rows;

                var seen = new HashSet<int>();
                foreach (string line in WslTools.SplitLines(r.Output))
                {
                    if (!line.StartsWith("DSHDISC|", StringComparison.Ordinal)) continue;
                    // 限 5 段：cwd 段允许含 '|'（极端路径字符），取第 5 段剩余全部
                    string[] seg = line.Split(new[] { '|' }, 5);
                    if (seg.Length < 5) continue;
                    int port;
                    if (!int.TryParse(seg[1], out port) || port < 1 || port > 65535) continue;
                    if (!seen.Add(port)) continue;               // 同端口去重（wrapper+node 父子进程）
                    rows.Add(new DistroProbeRow { Port = port, Home = seg[3] ?? "", Cwd = seg[4] ?? "" });
                }
            }
            catch { }
            return rows;
        }

        // ==================== 已安装未运行的发行版探测（v0.6.1） ====================

        /// <summary>"已安装 dsh 但当前没有运行中后端"的发行版（主实例不开机也能被「扫描」发现）。</summary>
        public sealed class DistroInstallInfo
        {
            public string Distro { get; set; } = "";
            /// <summary>发行版内 dsh 版本（探测失败为空）。</summary>
            public string DshVersion { get; set; } = "";
            /// <summary>默认 ~/.dsh 是否已初始化（有 web profile）。</summary>
            public bool HomeInitialized { get; set; }
        }

        /// <summary>
        /// 枚举运行中发行版，找出"已安装 dsh 但没有运行中后端"的发行版。
        /// 只探测当前运行中的发行版（不为扫描而拉起发行版）；distrosWithBackend
        /// 中的发行版（已有运行中后端，走运行中发现）跳过。
        /// </summary>
        public static List<DistroInstallInfo> ScanInstalledDistros(ICollection<string> distrosWithBackend)
        {
            var result = new List<DistroInstallInfo>();
            try
            {
                if (!WslTools.IsInstalledAsync().GetAwaiter().GetResult()) return result;
                List<string> distros = WslTools.ListRunningDistrosAsync().GetAwaiter().GetResult();
                foreach (string distro in distros)
                {
                    if (distrosWithBackend != null && distrosWithBackend.Contains(distro)) continue;
                    DistroInstallInfo info = ProbeInstalledDistro(distro);
                    if (info != null) result.Add(info);
                }
            }
            catch { /* 发现失败不阻断扫描 */ }
            return result;
        }

        /// <summary>
        /// 单次 bash 探测一个发行版是否安装了 dsh：PATH（登录 shell）→ npm 全局根兜底。
        /// 输出 DSHINST|&lt;--version 输出&gt;|&lt;yes|no&gt;|&lt;dsh路径&gt;；未安装无输出。
        /// </summary>
        private static DistroInstallInfo ProbeInstalledDistro(string distro)
        {
            try
            {
                string script =
                    "p=$(command -v dsh 2>/dev/null || true); " +
                    "if [ -z \"$p\" ]; then " +
                      "nr=$(npm root -g 2>/dev/null || true); " +
                      "if [ -n \"$nr\" ] && [ -f \"$nr/@deepseek-ai/dsh/package.json\" ]; then p=\"$nr/@deepseek-ai/dsh\"; fi; " +
                    "fi; " +
                    "if [ -n \"$p\" ]; then " +
                      "v=$(dsh --version 2>/dev/null | head -n1); " +
                      "h=no; [ -f \"$HOME/.dsh/profiles/web/cordis.yml\" ] && h=yes; " +
                      "echo \"DSHINST|$v|$h|$p\"; " +
                    "fi";
                var r = WslTools.RunInDistroAsync(distro, script, 60000).GetAwaiter().GetResult();
                return ParseInstallProbeOutput(distro, r.Output);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>解析 DSHINST|... 探测输出（自检共用）；未安装/解析失败返回 null。</summary>
        public static DistroInstallInfo ParseInstallProbeOutput(string distro, string output)
        {
            foreach (string line in WslTools.SplitLines(output ?? ""))
            {
                if (!line.StartsWith("DSHINST|", StringComparison.Ordinal)) continue;
                string[] seg = line.Split(new[] { '|' }, 4);
                if (seg.Length < 4) continue;
                return new DistroInstallInfo
                {
                    Distro = distro ?? "",
                    DshVersion = HarnessVersion.Parse(seg[1]),
                    HomeInitialized = seg[2].Trim() == "yes"
                };
            }
            return null;
        }

        // ==================== 既有辅助 ====================

        /// <summary>命令行是否像 Windows 侧的 dsh web 进程（dsh 字样 + web 子命令 + 该端口）。</summary>
        private static bool LooksLikeWindowsDsh(string cmdline, int port)
        {
            if (string.IsNullOrEmpty(cmdline)) return false;
            if (!cmdline.Contains("dsh", StringComparison.OrdinalIgnoreCase)) return false;
            if (!cmdline.Contains(" web ", StringComparison.OrdinalIgnoreCase)) return false;
            int pos = cmdline.IndexOf("--port", StringComparison.OrdinalIgnoreCase);
            if (pos < 0) return false;
            var m = Regex.Match(cmdline.Substring(pos), @"--port\s+(\d+)");
            return m.Success && int.Parse(m.Groups[1].Value) == port;
        }

        /// <summary>读取 WSL 实例的真实 DSH_HOME（pidfile → /proc/environ）。失败返回空串。</summary>
        private static string ReadWslDshHome(string distro, int port)
        {
            try
            {
                string sh = "if [ -f /tmp/dshwsl-" + port + ".pid ]; then " +
                    "p=$(cat /tmp/dshwsl-" + port + ".pid); " +
                    "tr '\0' '\n' < /proc/$p/environ 2>/dev/null | grep '^DSH_HOME=' | head -n1; fi";
                var r = WslTools.RunInDistroAsync(distro, sh, 15000).GetAwaiter().GetResult();
                if (!r.Ok) return "";
                string line = r.Output.Trim();
                const string prefix = "DSH_HOME=";
                return line.StartsWith(prefix, StringComparison.Ordinal) ? line.Substring(prefix.Length).Trim() : "";
            }
            catch { return ""; }
        }

        /// <summary>netstat -ano -p TCP → 127.0.0.1 监听端口 → 所属 PID。</summary>
        private static Dictionary<int, int> ListListeners()
        {
            var map = new Dictionary<int, int>();
            var psi = new ProcessStartInfo("netstat.exe", "-ano -p TCP")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                StandardOutputEncoding = Encoding.UTF8
            };
            using (var p = Process.Start(psi))
            {
                string text = p.StandardOutput.ReadToEnd();
                p.WaitForExit(5000);
                var lineRx = new Regex(
                    @"^\s*TCP\s+((?:127\.\d+\.\d+\.\d+)|\[::1\]|0\.0\.0\.0|\[::\]):(\d+)\s+\S+\s+LISTENING\s+(\d+)\s*$",
                    RegexOptions.Compiled | RegexOptions.IgnoreCase);
                foreach (string raw in text.Split('\n'))
                {
                    Match m = lineRx.Match(raw.TrimEnd('\r'));
                    if (!m.Success) continue;
                    int port = int.Parse(m.Groups[2].Value);
                    int pid = int.Parse(m.Groups[3].Value);
                    if (pid <= 0) continue;
                    map[port] = pid;
                }
            }
            return map;
        }

        /// <summary>PowerShell CIM 拉取全部进程命令行：pid → cmdline。</summary>
        private static Dictionary<int, string> GetProcessCommandLines()
        {
            var map = new Dictionary<int, string>();
            try
            {
                const string script =
                    "[Console]::OutputEncoding=[Text.Encoding]::UTF8;" +
                    "Get-CimInstance Win32_Process | Where-Object { $_.CommandLine } | " +
                    "ForEach-Object { $_.ProcessId.ToString() + '||' + $_.CommandLine }";
                var psi = new ProcessStartInfo("powershell.exe",
                    "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"" + script + "\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    StandardOutputEncoding = Encoding.UTF8
                };
                using (var p = Process.Start(psi))
                {
                    string text = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(30000);
                    foreach (string raw in text.Split('\n'))
                    {
                        string line = raw.TrimEnd('\r');
                        int sep = line.IndexOf("||", StringComparison.Ordinal);
                        if (sep <= 0) continue;
                        int pid;
                        if (!int.TryParse(line.Substring(0, sep), out pid)) continue;
                        map[pid] = line.Substring(sep + 2);
                    }
                }
            }
            catch { }
            return map;
        }
    }
}
