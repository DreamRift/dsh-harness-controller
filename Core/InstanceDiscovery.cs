// ============================================================================
//  InstanceDiscovery — 自动发现"正在运行但未注册"的 harness 后端（v0.5.0）
//
//  背景：实例清单（instances.json）按 exe 所在目录存放，换目录/换发布目录后
//  旧清单不在，界面上就看不到仍在运行的后端（尤其 WSL 实例经 wslrelay 代理，
//  端口还在、但局部没有任何注册项）。
//
//  本组件在注册表为空或缺少运行中实例时被调用：
//    ① netstat 枚举 127.0.0.1 监听端口 → PID；
//    ② PowerShell CIM 拉取进程命令行（wsl.exe 宿主 / node/cmd 包装）；
//    ③ 反推出实例：WSL 实例（wsl.exe -d <发行版> --exec bash /tmp/dshwsl-<port>.sh）
//       或 Windows 实例（命令行含 dsh + " web " + --port）；
//    ④ WSL 实例再从发行版内 /proc/<pid>/environ 读取真实 DSH_HOME。
//  发现的实例以 "auto-*" id 追加进注册表（首次保存后持久化），
//  用户可在实例设置里改名/补全后保存，替换成正式配置。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace DshController.Core
{
    public static class InstanceDiscovery
    {
        /// <summary>扫描并返回"不在给定清单中"的运行中实例（保守：只认 dsh web 后端）。</summary>
        public static List<InstanceDef> Scan()
        {
            var result = new List<InstanceDef>();
            try
            {
                var listeners = ListListeners();                 // port -> pid
                if (listeners.Count == 0) return result;
                var cmd = GetProcessCommandLines();              // pid -> commandline
                var claimed = new HashSet<int>();

                // ① WSL 宿主：wsl.exe -d <发行版> --exec bash /tmp/dshwsl-<port>.sh
                var wslHostRx = new Regex(
                    @"-d\s+([A-Za-z0-9_.-]+)[^\r\n]*dshwsl-(\d+)\.sh",
                    RegexOptions.Compiled | RegexOptions.IgnoreCase);
                foreach (var kv in cmd)
                {
                    Match m = wslHostRx.Match(kv.Value ?? "");
                    if (!m.Success) continue;
                    int port = int.Parse(m.Groups[2].Value);
                    string distro = m.Groups[1].Value;
                    if (claimed.Contains(port)) continue;
                    claimed.Add(port);
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

                // ② Windows：监听进程命令行含 dsh + " web " + --port <port>
                foreach (var l in listeners)
                {
                    if (claimed.Contains(l.Key)) continue;
                    string c = null;
                    cmd.TryGetValue(l.Value, out c);
                    if (string.IsNullOrEmpty(c)) continue;
                    if (LooksLikeWindowsDsh(c, l.Key))
                    {
                        claimed.Add(l.Key);
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
                    "tr '\\0' '\\n' < /proc/$p/environ 2>/dev/null | grep '^DSH_HOME=' | head -n1; fi";
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