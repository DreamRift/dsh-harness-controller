// ============================================================================
//  HarnessVersion — harness 版本探测与指定版本启动（v0.5.0）
//
//  职责：
//    - 探测"当前环境下 harness 主实例"的版本：
//        Windows：优先读 %APPDATA%\npm\node_modules\@deepseek-ai\dsh\package.json，
//                 失败则执行已解析出的 dsh --version；
//        WSL    ：在发行版内执行 dsh --version（登录 shell 加载 PATH），
//                 失败则读发行版 npm 全局根下的 package.json。
//    - 解析任意输出串中的 semver 版本号（dsh --version 直接打印版本号）。
//    - 解析 npx 可执行文件（Windows shim / WSL 内原生 npx）：
//      指定版本启动 = `npx --yes @deepseek-ai/dsh@<version> web ...`。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DshController.Core
{
    public static class HarnessVersion
    {
        private static readonly Regex VerRx = new Regex(
            @"\d+\.\d+\.\d+(?:-[0-9A-Za-z][0-9A-Za-z.-]*)?",
            RegexOptions.Compiled);

        /// <summary>从任意输出中提取第一个形如 x.y.z[-rc.n] 的版本号；找不到返回空串。</summary>
        public static string Parse(string output)
        {
            if (string.IsNullOrWhiteSpace(output)) return "";
            Match m = VerRx.Match(output);
            return m.Success ? m.Value : "";
        }

        /// <summary>
        /// 规范化用户输入的版本号（v0.5.0）：去空白、去前缀 v/V、去“（当前指定）”等中文后缀，
        /// 校验必须是 x.y.z[-预发布] 形态。空输入 = 跟随当前环境（返回 true + 空串）。
        /// </summary>
        public static bool TryNormalizeVersion(string input, out string normalized)
        {
            normalized = "";
            string s = (input ?? "").Trim();
            if (s.Length == 0) return true;                       // 空 = 跟随当前环境
            if (s.StartsWith("默认", StringComparison.Ordinal) ||
                s.StartsWith("跟随", StringComparison.Ordinal)) return true;
            if (s[0] == 'v' || s[0] == 'V') s = s.Substring(1).Trim();

            // 允许"0.1.0-rc.7（当前环境主实例版本）"这类带说明后缀的下拉文案
            Match m = VerRx.Match(s);
            if (!m.Success) return false;
            // 只接受"版本号开头"的输入，避免把随意文本里的数字误当版本
            if (m.Index != 0) return false;
            normalized = m.Value;
            return true;
        }

        /// <summary>
        /// 探测当前 Windows 环境 harness 主实例版本。
        /// ① 读 npm 全局包 package.json（零子进程、最快）；② 执行已解析的 dsh --version。
        /// </summary>
        public static async Task<string> ResolveWindowsAsync(Config cfg)
        {
            // ① npm 全局安装的包清单
            string pkg = Path.Combine(DshResolver.NpmGlobalDir,
                "node_modules", "@deepseek-ai", "dsh", "package.json");
            if (File.Exists(pkg))
            {
                try
                {
                    using (JsonDocument doc = JsonDocument.Parse(File.ReadAllText(pkg, Encoding.UTF8)))
                    {
                        if (doc.RootElement.TryGetProperty("version", out JsonElement v) &&
                            v.ValueKind == JsonValueKind.String)
                        {
                            string s = v.GetString();
                            if (!string.IsNullOrWhiteSpace(s)) return s.Trim();
                        }
                    }
                }
                catch { }
            }

            // ② dsh --version（commander 直接打印版本号后退出，不启动 profile）
            DshCommand dsh = new DshResolver().Resolve(cfg);
            if (dsh == null) return "";
            try
            {
                string fileName, args;
                if (dsh.Kind == "cmd")
                {
                    fileName = "cmd.exe";
                    args = "/d /s /c \"\"" + dsh.Path1 + "\" --version\"";
                }
                else if (dsh.Kind == "node")
                {
                    fileName = dsh.Path1;
                    args = "\"" + dsh.Path2 + "\" --version";
                }
                else return "";
                string output = await RunCaptureAsync(fileName, args, 20000).ConfigureAwait(false);
                return Parse(output);
            }
            catch { return ""; }
        }

        /// <summary>
        /// 探测 WSL 发行版内 harness 主实例版本。
        /// ① dsh --version；② npm 全局根 + package.json 兜底。
        /// </summary>
        public static async Task<string> ResolveWslAsync(string distro)
        {
            if (string.IsNullOrWhiteSpace(distro)) return "";

            var r1 = await WslTools.RunInDistroAsync(distro,
                "dsh --version 2>/dev/null | head -n1", 120000).ConfigureAwait(false);
            string v = Parse(r1.Output);
            if (v.Length > 0) return v;

            // 兜底：node 直接读 npm 全局包的 package.json（node 随 dsh 一起安装）
            var r2 = await WslTools.RunInDistroAsync(distro,
                "node -p \"require(require('child_process').execSync('npm root -g').toString().trim()" +
                " + '/@deepseek-ai/dsh/package.json').version\" 2>/dev/null",
                120000).ConfigureAwait(false);
            return Parse(r2.Output);
        }

        // ---------------- npx 解析（指定版本启动） ----------------

        /// <summary>Windows 侧 npx shim：npm 全局目录优先，PATH 兜底。找不到返回 null。</summary>
        public static string FindNpxWindows()
        {
            string shim = Path.Combine(DshResolver.NpmGlobalDir, "npx.cmd");
            if (File.Exists(shim)) return shim;
            return DshResolver.FindOnPath("npx.cmd") ?? DshResolver.FindOnPath("npx.exe");
        }

        /// <summary>
        /// 解析 WSL 发行版内原生 npx 绝对路径（登录 shell）。
        /// 排除 /mnt 开头的 Windows shim，只认发行版原生 npx。找不到返回空串。
        /// </summary>
        public static async Task<string> ResolveWslNpxAsync(string distro)
        {
            if (string.IsNullOrWhiteSpace(distro)) return "";
            var r = await WslTools.RunInDistroAsync(distro,
                "command -v npx 2>/dev/null; echo '---'; type -aP npx 2>/dev/null || true",
                120000).ConfigureAwait(false);
            if (!r.Ok) return "";
            foreach (string line in WslTools.SplitLines(r.Output))
            {
                string p = line.Trim();
                if (p.Length == 0 || p == "---") continue;
                if (!p.StartsWith("/", StringComparison.Ordinal)) continue;
                if (p.StartsWith("/mnt/", StringComparison.Ordinal)) continue;
                return p;
            }
            return "";
        }

        /// <summary>npx 的所在目录（用于把 node/npm 同目录加入 WSL 启动脚本 PATH）。</summary>
        public static string DirOf(string linuxAbsPath)
        {
            if (string.IsNullOrEmpty(linuxAbsPath)) return "";
            int slash = linuxAbsPath.LastIndexOf('/');
            return slash > 0 ? linuxAbsPath.Substring(0, slash) : "";
        }

        // ---------------- 可用版本列表（npm registry，v0.5.0） ----------------

        /// <summary>Windows 侧 npm shim：npm 全局目录优先，PATH 兜底。找不到返回 null。</summary>
        public static string FindNpmWindows()
        {
            string shim = Path.Combine(DshResolver.NpmGlobalDir, "npm.cmd");
            if (File.Exists(shim)) return shim;
            return DshResolver.FindOnPath("npm.cmd") ?? DshResolver.FindOnPath("npm.exe");
        }

        /// <summary>
        /// 拉取 @deepseek-ai/dsh 在 npm registry 上的已发布版本（Windows 侧，需联网）。
        /// 返回新→旧排序的版本列表；失败返回空列表（调用方保持"手动输入"能力）。
        /// </summary>
        public static async Task<List<string>> ListVersionsWindowsAsync(int max = 40)
        {
            string npm = FindNpmWindows();
            if (string.IsNullOrEmpty(npm)) return new List<string>();
            string args = "/d /s /c \"\"" + npm + "\" view @deepseek-ai/dsh versions --json\"";
            string output = await RunCaptureAsync("cmd.exe", args, 40000).ConfigureAwait(false);
            return ParseVersionsJson(output, max);
        }

        /// <summary>拉取 @deepseek-ai/dsh 已发布版本（WSL 发行版内执行 npm view，需联网）。</summary>
        public static async Task<List<string>> ListVersionsWslAsync(string distro, int max = 40)
        {
            if (string.IsNullOrWhiteSpace(distro)) return new List<string>();
            var r = await WslTools.RunInDistroAsync(distro,
                "npm view @deepseek-ai/dsh versions --json 2>/dev/null", 60000).ConfigureAwait(false);
            return ParseVersionsJson(r.Output, max);
        }

        /// <summary>解析 npm view --json 的输出（数组或单个字符串），返回新→旧顺序。</summary>
        private static List<string> ParseVersionsJson(string output, int max)
        {
            var list = new List<string>();
            if (string.IsNullOrWhiteSpace(output)) return list;
            int start = output.IndexOfAny(new[] { '[', '"' });
            if (start < 0) return list;
            string json = output.Substring(start).Trim();
            try
            {
                using (JsonDocument doc = JsonDocument.Parse(json))
                {
                    if (doc.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement el in doc.RootElement.EnumerateArray())
                        {
                            if (el.ValueKind != JsonValueKind.String) continue;
                            string v = (el.GetString() ?? "").Trim();
                            if (v.Length > 0) list.Add(v);
                        }
                    }
                    else if (doc.RootElement.ValueKind == JsonValueKind.String)
                    {
                        string v = (doc.RootElement.GetString() ?? "").Trim();
                        if (v.Length > 0) list.Add(v);
                    }
                }
            }
            catch
            {
                return new List<string>();
            }
            list.Reverse();                                        // registry 返回旧→新，界面按新→旧展示
            if (list.Count > max) list = list.GetRange(0, max);
            return list;
        }

        // ------------------------------------------------------------------

        /// <summary>执行命令并采集 stdout+stderr（带超时保护，失败返回空串）。</summary>
        private static async Task<string> RunCaptureAsync(string fileName, string args, int timeoutMs)
        {
            try
            {
                var psi = new ProcessStartInfo(fileName, args)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };
                using (var p = Process.Start(psi))
                {
                    using (var cts = new CancellationTokenSource(timeoutMs))
                    {
                        var tOut = p.StandardOutput.ReadToEndAsync();
                        var tErr = p.StandardError.ReadToEndAsync();
                        try { await p.WaitForExitAsync(cts.Token).ConfigureAwait(false); }
                        catch (OperationCanceledException)
                        {
                            try { p.Kill(entireProcessTree: true); } catch { }
                            return "";
                        }
                        await Task.WhenAll(tOut, tErr).ConfigureAwait(false);
                        return (tOut.Result ?? "") + "\n" + (tErr.Result ?? "");
                    }
                }
            }
            catch
            {
                return "";
            }
        }
    }
}
