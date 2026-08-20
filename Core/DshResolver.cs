// ============================================================================
//  DshResolver — dsh / node 命令解析（v0.2.0）
//
//  解析策略与 legacy 相同的 4 级回退：
//    ① launcher.json 显式指定 → ② %APPDATA%\npm\dsh.cmd → ③ PATH 上的 dsh →
//    ④ node + @deepseek-ai/dsh/lib/bin.js
//  v0.2.0 改进：
//    - 解析结果按 dshCommand 配置缓存（v0.1.0 每秒状态刷新都全盘重扫）；
//    - 新增 ResolutionTrace：把 4 级候选路径与命中情况暴露给错误报告。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace DshController.Core
{
    public sealed class DshCommand
    {
        public string Kind;    // "cmd"（npm shim）| "node"（node + bin.js）| "npx"（指定版本，v0.5.0）
        public string Path1;   // cmd shim 路径 / node.exe 路径 / npx.cmd 路径
        public string Path2;   // node 模式：@deepseek-ai/dsh/lib/bin.js；npx 模式：锁定版本号

        public string Describe()
        {
            if (Kind == "npx")
                return "npx --yes @deepseek-ai/dsh@" + Path2 + "（经 " + Path1 + "）";
            return Kind == "cmd" ? Path1 : Path1 + " \"" + Path2 + "\"";
        }
    }

    public sealed class ResolutionStep
    {
        public string Source;   // ①②③④ 描述
        public string Path;     // 候选路径
        public bool Found;
    }

    public sealed class DshResolver
    {
        private DshCommand _cache;
        private string _cacheKey;
        private readonly object _lock = new object();

        public static string NpmGlobalDir
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm");
            }
        }

        /// <summary>带缓存解析（缓存键 = dshCommand 配置值；force 时强制重扫）。</summary>
        public DshCommand Resolve(Config cfg, bool force = false)
        {
            lock (_lock)
            {
                if (!force && _cache != null && string.Equals(_cacheKey, cfg.DshCommand ?? "", StringComparison.OrdinalIgnoreCase))
                    return _cache;
                _cache = ResolveCore(cfg);
                _cacheKey = cfg.DshCommand ?? "";
                return _cache;
            }
        }

        private static DshCommand ResolveCore(Config cfg)
        {
            // 1. 用户配置里显式指定的命令
            if (!string.IsNullOrEmpty(cfg.DshCommand) && File.Exists(cfg.DshCommand))
                return new DshCommand { Kind = "cmd", Path1 = cfg.DshCommand };

            // 2. npm 全局 shim（本机安装 @deepseek-ai/dsh 后必然存在）
            string npmDir = NpmGlobalDir;
            string shim = Path.Combine(npmDir, "dsh.cmd");
            if (File.Exists(shim)) return new DshCommand { Kind = "cmd", Path1 = shim };

            // 3. PATH 上的 dsh（纯文件扫描，不依赖管道）
            string onPath = FindOnPath("dsh.cmd") ?? FindOnPath("dsh.exe");
            if (onPath != null) return new DshCommand { Kind = "cmd", Path1 = onPath };

            // 4. 直接调用 node + dsh 包入口
            string node = FindNode();
            string bin = Path.Combine(npmDir, "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js");
            if (!string.IsNullOrEmpty(node) && File.Exists(bin))
                return new DshCommand { Kind = "node", Path1 = node, Path2 = bin };

            return null;
        }

        /// <summary>逐级记录解析过程（供错误报告使用；不写缓存）。</summary>
        public List<ResolutionStep> Trace(Config cfg)
        {
            var steps = new List<ResolutionStep>();
            string npmDir = NpmGlobalDir;

            string shim = Path.Combine(npmDir, "dsh.cmd");
            string bin = Path.Combine(npmDir, "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js");

            steps.Add(new ResolutionStep { Source = "① launcher.json dshCommand", Path = cfg.DshCommand, Found = !string.IsNullOrEmpty(cfg.DshCommand) && File.Exists(cfg.DshCommand) });
            steps.Add(new ResolutionStep { Source = "② npm 全局 shim", Path = shim, Found = File.Exists(shim) });
            string p1 = FindOnPath("dsh.cmd"), p2 = FindOnPath("dsh.exe");
            steps.Add(new ResolutionStep { Source = "③ PATH 扫描 dsh.cmd", Path = p1, Found = p1 != null });
            steps.Add(new ResolutionStep { Source = "③ PATH 扫描 dsh.exe", Path = p2, Found = p2 != null });
            string node = FindNode();
            steps.Add(new ResolutionStep { Source = "④ node 入口 node.exe", Path = node, Found = !string.IsNullOrEmpty(node) });
            steps.Add(new ResolutionStep { Source = "④ node 入口 bin.js", Path = bin, Found = File.Exists(bin) });
            return steps;
        }

        /// <summary>在 PATH 中按文件名找可执行文件（纯文件系统扫描，无子进程）。</summary>
        public static string FindOnPath(string exeName)
        {
            try
            {
                string pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
                foreach (string dir in pathEnv.Split(';'))
                {
                    if (string.IsNullOrEmpty(dir)) continue;
                    string cand = Path.Combine(dir.Trim('"'), exeName);
                    if (File.Exists(cand)) return cand;
                }
            }
            catch { }
            return null;
        }

        /// <summary>查找 node.exe：常见位置 → PATH 扫描 → where 兜底。</summary>
        public static string FindNode()
        {
            string npmNode = Path.Combine(NpmGlobalDir, "node.exe");
            if (File.Exists(npmNode)) return npmNode;
            string pfNode = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "node.exe");
            if (File.Exists(pfNode)) return pfNode;
            string onPath = FindOnPath("node.exe");
            if (onPath != null) return onPath;
            return WhereExe("node");
        }

        /// <summary>cmd where 兜底查找（带 3 秒超时保护）。</summary>
        public static string WhereExe(string name)
        {
            try
            {
                var psi = new ProcessStartInfo("cmd.exe", "/d /c where " + name)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8
                };
                using (Process p = Process.Start(psi))
                {
                    var outTask = Task.Run(() => p.StandardOutput.ReadToEnd());
                    if (!p.WaitForExit(3000))
                    {
                        try { p.Kill(); } catch { }
                        return null;
                    }
                    string outp = outTask.Result;
                    foreach (string raw in outp.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        string line = raw.Trim();
                        if (line.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                            line.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
                            return line;
                    }
                }
            }
            catch { }
            return null;
        }
    }
}
