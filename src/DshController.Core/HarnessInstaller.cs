// ============================================================================
//  HarnessInstaller — dsh 本体安装/升级（npm 全局）。Windows 与 WSL 各一条执行通道。
//
//  语义：把选定版本的 @deepseek-ai/dsh 真装进实例的运行环境——
//    Windows 实例 → Windows 全局 npm（%APPDATA%\npm\npm.cmd install -g）；
//    WSL 实例    → 该发行版内全局 npm（登录 shell 加载 PATH 后 npm install -g）。
//  两环境互不影响；成功后由调用方把 def.HarnessVersion 置空（跟随环境）并落盘。
//
//  版本安全：版本号先过 HarnessVersion.TryNormalizeVersion，再过字符白名单
//  （数字/字母/点/连字符），拼进命令行前零注入面（cmd 元字符/空白一律拒绝）。
//  过程输出经 log 回调逐行吐给调用方；超时杀整个进程树返回 false。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DshController.Core
{
    public static class HarnessInstaller
    {
        /// <summary>被安装的 npm 包名（两条通道共用）。</summary>
        public const string Package = "@deepseek-ai/dsh";

        /// <summary>版本号字符白名单：semver 核心段与预发布段只含 数字/字母/点/连字符。</summary>
        private static readonly Regex VerCharsRx = new Regex(
            @"^[0-9A-Za-z.\-]+$", RegexOptions.Compiled);

        // ---------------- 纯函数：命令拼接与版本校验 ----------------

        /// <summary>
        /// 拼 npm 安装命令参数（纯函数，供两通道与单测复用）：
        /// 返回完整命令行文本（不含宿主 exe），如 npm install -g @deepseek-ai/dsh@0.1.1-rc.2。
        /// 版本非法（空/格式不对/含空白或 cmd 元字符/以 - 或 . 开头）返回空串。
        /// </summary>
        public static string BuildInstallCommand(string version)
        {
            string v = CleanVersion(version, out _);
            return v == null ? "" : "npm install -g " + Package + "@" + v;
        }

        /// <summary>
        /// 校验并规范化版本号；非法返回 null，中文错误说明经 error 带出（供日志）。
        /// 两道闸：① 原始输入（去 v/V 前缀、去"（说明）"中文后缀后）必须整体落在字符白名单内——
        /// 空格与 cmd 元字符（&amp;|;&lt;&gt; 等）一律整条拒绝，杜绝"垃圾尾巴被悄悄剥掉"的注入面；
        /// ② 再过 HarnessVersion.TryNormalizeVersion 剥前缀/后缀并锁定 semver 形态。
        /// 注意"跟随当前环境"这类宽松输入（空串/默认/跟随前缀）在这里不合法——安装必须有具体版本。
        /// </summary>
        private static string CleanVersion(string version, out string error)
        {
            error = "";
            string s = (version ?? "").Trim();
            if (s.Length == 0)
            {
                error = "版本号为空，请先选择或输入要安装的版本。";
                return null;
            }

            // ① 原始输入白名单（允许 v/V 前缀与"（说明）"中文后缀，与 TryNormalizeVersion 的下拉文案语义一致）
            string probe = s[0] == 'v' || s[0] == 'V' ? s.Substring(1) : s;
            int paren = probe.IndexOf('（');
            if (paren >= 0)
            {
                if (!probe.EndsWith("）", StringComparison.Ordinal))
                {
                    error = "版本号含不允许的字符：" + s;
                    return null;
                }
                probe = probe.Substring(0, paren);
            }
            if (probe.Length == 0 || !VerCharsRx.IsMatch(probe) || probe[0] == '-' || probe[0] == '.')
            {
                error = "版本号含不允许的字符（版本只含数字/字母/点/连字符）：" + s;
                return null;
            }

            // ② 形态规范化（semver；"默认/跟随"返回空串 = 未指定具体版本，不允许安装）
            if (!HarnessVersion.TryNormalizeVersion(s, out string v) || v.Length == 0)
            {
                error = "版本号格式不正确（应为 x.y.z 或 x.y.z-rc.n，如 0.1.1-rc.2）：" + s;
                return null;
            }
            return v;
        }

        // ---------------- Windows 通道 ----------------

        /// <summary>
        /// Windows：cmd.exe /c "&lt;npm.cmd 路径&gt; install -g @deepseek-ai/dsh@&lt;version&gt;"。
        /// npm.cmd 定位：优先 %APPDATA%\npm\npm.cmd，其次 PATH（HarnessVersion.FindNpmWindows）；
        /// 定位失败返回 false + log 说明。stdout/stderr 逐行经 log 回调；超时杀进程树返回 false。
        /// </summary>
        public static async Task<bool> UpgradeWindowsAsync(string version, Action<string> log = null,
            int timeoutMs = 300000)
        {
            string v = CleanVersion(version, out string err);
            if (v == null)
            {
                log?.Invoke("dsh 升级失败：" + err);
                return false;
            }

            string npm = HarnessVersion.FindNpmWindows();
            if (string.IsNullOrEmpty(npm))
            {
                log?.Invoke("dsh 升级失败：未找到 npm（%APPDATA%\\npm\\npm.cmd 与 PATH 均未命中），请先安装 Node.js/npm。");
                return false;
            }

            string args = "/d /s /c \"\"" + npm + "\" install -g " + Package + "@" + v + "\"";
            log?.Invoke("$ npm install -g " + Package + "@" + v + "（npm: " + npm + "）");

            var psi = new ProcessStartInfo("cmd.exe", args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            var sw = Stopwatch.StartNew();
            try
            {
                using (var p = new Process { StartInfo = psi })
                using (var cts = new CancellationTokenSource(timeoutMs))
                {
                    p.OutputDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data)) log?.Invoke(e.Data); };
                    p.ErrorDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data)) log?.Invoke("[stderr] " + e.Data); };
                    p.Start();
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                    try { await p.WaitForExitAsync(cts.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException)
                    {
                        try { p.Kill(entireProcessTree: true); }
                        catch
                        {
                            // 理由: 超时后强杀失败（npm 已自行退出/句柄失效）无副作用，结果已按超时判定
                        }
                        log?.Invoke("dsh 升级失败：npm 安装超时（超过 " + (timeoutMs / 1000) + " 秒），已终止进程。");
                        return false;
                    }
                    try { p.WaitForExit(); }
                    catch
                    {
                        // 理由: 等待异步输出排空是尽力而为，失败不影响以退出码判定结果
                    }
                    if (p.ExitCode == 0)
                    {
                        log?.Invoke("dsh 升级完成（npm install -g " + Package + "@" + v +
                                    "，用时 " + (sw.ElapsedMilliseconds / 1000.0).ToString("0.#") + " 秒）。");
                        return true;
                    }
                    log?.Invoke("dsh 升级失败：npm install 退出码 " + p.ExitCode + "（详见上方输出）。");
                    return false;
                }
            }
            catch (Exception ex)
            {
                log?.Invoke("dsh 升级失败：npm 进程启动/执行异常：" + ex.Message);
                return false;
            }
        }

        // ---------------- WSL 通道 ----------------

        /// <summary>
        /// WSL：发行版内 npm install -g @deepseek-ai/dsh@&lt;version&gt;
        /// （走 WslTools.RunInDistroAsync 登录 shell，自动加载 node PATH）。
        /// 发行版未配置返回 false + log 说明；输出尾部经 log 回调；超时由 WslTools 杀进程返回 false。
        /// </summary>
        public static async Task<bool> UpgradeWslAsync(string distro, string version, Action<string> log = null,
            int timeoutMs = 300000)
        {
            string v = CleanVersion(version, out string err);
            if (v == null)
            {
                log?.Invoke("dsh 升级失败：" + err);
                return false;
            }
            if (string.IsNullOrWhiteSpace(distro))
            {
                log?.Invoke("dsh 升级失败：WSL 实例缺少发行版配置，请先在实例设置中填好发行版。");
                return false;
            }

            string cmd = "npm install -g " + Package + "@" + v;
            log?.Invoke("[" + distro + "] $ " + cmd);

            var r = await WslTools.RunInDistroAsync(distro, cmd, timeoutMs).ConfigureAwait(false);
            foreach (string line in TailLines(r.Output, 60)) log?.Invoke(line);
            if (r.Error.Trim().Length > 0)
                foreach (string line in TailLines(r.Error, 15)) log?.Invoke("[stderr] " + line);

            if (r.Ok)
            {
                log?.Invoke("dsh 升级完成（" + distro + " 内 npm install -g " + Package + "@" + v + "）。");
                return true;
            }
            log?.Invoke(r.TimedOut
                ? "dsh 升级失败：发行版内 npm 安装超时，已终止。"
                : "dsh 升级失败：发行版内 npm install 退出码 " + r.ExitCode + "（详见上方输出）。");
            return false;
        }

        /// <summary>取文本尾部至多 max 行（去空行；WSL 输出可能很长，只回吐尾部）。</summary>
        private static List<string> TailLines(string text, int max)
        {
            var lines = new List<string>();
            foreach (string raw in (text ?? "").Replace("\r", "").Split('\n'))
            {
                string line = raw.TrimEnd();
                if (line.Trim().Length > 0) lines.Add(line);
            }
            if (lines.Count > max) lines = lines.GetRange(lines.Count - max, max);
            return lines;
        }
    }
}
