// ============================================================================
//  PluginInstaller — DSH 官方插件命令封装（v0.6.0 插件市场）
//
//  严格走 DSH 官方插件管理命令（底层封装 pnpm，语义与 pnpm 一致）：
//    dsh plugin --profile <p> add    <npm包名 | github:owner/repo>
//    dsh plugin --profile <p> remove <包名>
//    dsh plugin --profile <p> update <包名>
//  bundle 插件的增删需重启实例后生效（官方语义），由调用方提示。
//
//  实例隔离：Windows 实例经 DSH_HOME 环境变量注入（与 BackendManager 启动一致，
//  空 = 不注入走默认 ~/.dsh）；WSL 实例在发行版内执行（DSH_HOME 前缀内联传递）。
//
//  已知坑（dsh-routing-suite/install.ps1 实证）：dsh plugin 把参数转发给 pnpm
//  时经 cmd.exe 重建命令行，含空格的本地路径会被拆断。因此目标一律经过
//  ValidateTarget 白名单字符校验（禁空白与 cmd 元字符），npm/github 来源天然安全。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DshController.Core
{
    public enum PluginOp { Add, Remove, Update }

    public static class PluginOpText
    {
        public static string Verb(PluginOp op)
        {
            return op == PluginOp.Add ? "add" : op == PluginOp.Remove ? "remove" : "update";
        }

        public static string Label(PluginOp op)
        {
            return op == PluginOp.Add ? "安装" : op == PluginOp.Remove ? "卸载" : "升级";
        }
    }

    /// <summary>一次插件命令执行的结果。</summary>
    public sealed class PluginOpResult
    {
        public bool Ok { get; set; }
        public int ExitCode { get; set; } = -1;
        public bool TimedOut { get; set; }
        public string Output { get; set; } = "";
        public string Error { get; set; } = "";
        public string Command { get; set; } = "";   // 展示用命令行（错误提示/日志用）
    }

    public static class PluginInstaller
    {
        /// <summary>add（github: 来源要 clone + 构建）比 remove/update 给更长超时。</summary>
        public const int DefaultAddTimeoutMs = 600000;
        public const int DefaultOpTimeoutMs = 300000;

        private static readonly Regex TargetRx = new Regex(
            @"^[A-Za-z0-9@/\\:._~\-]+$", RegexOptions.Compiled);

        private static readonly object ProcLock = new object();
        private static readonly List<Process> ActiveProcs = new List<Process>();

        /// <summary>
        /// 目标合法性校验：npm 包名 / pkg@ver / github:owner/repo / 无空格本地路径。
        /// 返回 null = 合法；否则返回中文错误说明。
        /// 白名单字符集同时挡住了空白（pnpm/cmd 拆断坑）与 cmd 元字符。
        /// </summary>
        public static string ValidateTarget(string target)
        {
            string t = (target ?? "").Trim();
            if (t.Length == 0) return "安装目标为空。";
            if (t.Length > 260) return "目标过长。";
            if (!TargetRx.IsMatch(t))
            {
                return "目标含不允许的字符。注意：dsh plugin 转发 pnpm 时含空格的本地路径会被拆断，" +
                       "请使用 npm 包名、github:owner/repo 或无空格的本地路径（必要时建无空格 junction）。";
            }
            if (t.StartsWith("github:", StringComparison.OrdinalIgnoreCase))
            {
                string repo = t.Substring("github:".Length).TrimStart('/');
                if (!Regex.IsMatch(repo, @"^[A-Za-z0-9_.\-]+/[A-Za-z0-9_.\-]+$"))
                    return "github 来源格式应为 github:owner/repo。";
            }
            return null;
        }

        // ==================== Windows 实例 ====================

        /// <summary>
        /// 在 Windows 实例上执行官方插件命令。dsh 解析与 BackendManager 启动一致：
        /// 实例锁定 harness 版本走 npx，否则 4 级回退。DSH_HOME 非空才注入。
        /// </summary>
        public static async Task<PluginOpResult> RunWindowsAsync(Config cfg, PluginOp op, string profile,
            string target, Action<string> log, int timeoutMs = DefaultAddTimeoutMs)
        {
            string verb = PluginOpText.Verb(op);
            string invalid = ValidateTarget(target);
            if (invalid != null)
                return new PluginOpResult { Error = invalid, Command = Describe(op, profile, target) };

            // 解析 dsh 命令（锁定版本 → npx；否则 4 级回退）
            DshCommand dsh;
            string pinned = (cfg.HarnessVersion ?? "").Trim();
            if (pinned.Length > 0)
            {
                string npx = HarnessVersion.FindNpxWindows();
                if (string.IsNullOrEmpty(npx))
                    return new PluginOpResult
                    {
                        Error = "实例指定了 harness 版本 " + pinned + "，需要 npx 执行插件命令，但本机未找到 npx.cmd。",
                        Command = Describe(op, profile, target)
                    };
                dsh = new DshCommand { Kind = "npx", Path1 = npx, Path2 = pinned };
            }
            else
            {
                dsh = new DshResolver().Resolve(cfg);
                if (dsh == null)
                    return new PluginOpResult
                    {
                        Error = "未找到 dsh 命令（4 级回退均未命中）。请先安装 @deepseek-ai/dsh 或在全局设置中指定 dshCommand。",
                        Command = Describe(op, profile, target)
                    };
            }

            (string fileName, string args) = BuildWindowsCommand(dsh, op, profile, target);
            var result = new PluginOpResult { Command = fileName + " " + args };
            log?.Invoke("$ " + result.Command);

            var psi = new ProcessStartInfo(fileName, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = Directory.Exists(cfg.Workspace ?? "") ? cfg.Workspace : null
            };
            if (!string.IsNullOrEmpty(cfg.Home))
                psi.EnvironmentVariables["DSH_HOME"] = cfg.Home;   // 与 BackendManager 同语义：空 = 默认 ~/.dsh

            var sw = Stopwatch.StartNew();
            try
            {
                using (var p = new Process { StartInfo = psi })
                using (var cts = new CancellationTokenSource(timeoutMs))
                {
                    var outList = new List<string>();
                    var errList = new List<string>();
                    p.OutputDataReceived += (s, e) =>
                    {
                        if (!string.IsNullOrEmpty(e.Data))
                        {
                            lock (outList) outList.Add(e.Data);
                            log?.Invoke(e.Data);
                        }
                    };
                    p.ErrorDataReceived += (s, e) =>
                    {
                        if (!string.IsNullOrEmpty(e.Data))
                        {
                            lock (errList) errList.Add(e.Data);
                            log?.Invoke("[stderr] " + e.Data);
                        }
                    };
                    lock (ProcLock) ActiveProcs.Add(p);
                    try
                    {
                        p.Start();
                        p.BeginOutputReadLine();
                        p.BeginErrorReadLine();
                        try
                        {
                            await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            try { p.Kill(entireProcessTree: true); } catch
                            {
                                // 理由: 命令已超时，进程可能已自行退出，Kill 失败无副作用，仅为尽力清理残留。
                            }
                            result.TimedOut = true;
                        }
                    }
                    finally
                    {
                        lock (ProcLock) ActiveProcs.Remove(p);
                    }

                    try { p.WaitForExit(); } catch
                    {
                        // 理由: 等待异步输出排空是尽力而为，失败时结果仍以已捕获的 Output/Error 为准，不影响超时判定。
                    }   // 确保异步输出排空
                    if (!result.TimedOut) result.ExitCode = p.ExitCode;
                    lock (outList) result.Output = string.Join(Environment.NewLine, outList);
                    lock (errList) result.Error = string.Join(Environment.NewLine, errList);
                }
            }
            catch (Exception ex)
            {
                result.Error = (result.Error.Length > 0 ? result.Error + "\n" : "") + ex.Message;
            }

            result.Ok = !result.TimedOut && result.ExitCode == 0;
            log?.Invoke(string.Format("插件命令{0}（{1}，exit={2}）",
                result.Ok ? "完成" : result.TimedOut ? "超时" : "失败",
                sw.ElapsedMilliseconds / 1000.0 + "s", result.ExitCode));
            return result;
        }

        /// <summary>拼装 Windows 命令行（与 BackendManager 的 cmd /s /c 引号约定一致；自检共用）。</summary>
        public static (string FileName, string Args) BuildWindowsCommand(DshCommand dsh, PluginOp op,
            string profile, string target)
        {
            string tail = " plugin --profile " + profile + " " + PluginOpText.Verb(op) + " " + target;
            if (dsh.Kind == "cmd")
                return ("cmd.exe", "/d /s /c \"\"" + dsh.Path1 + "\"" + tail + "\"");
            if (dsh.Kind == "npx")
                return ("cmd.exe", "/d /s /c \"\"" + dsh.Path1 + "\" --yes @deepseek-ai/dsh@" + dsh.Path2 + tail + "\"");
            // node + bin.js
            return (dsh.Path1, "\"" + dsh.Path2 + "\"" + tail);
        }

        private static string Describe(PluginOp op, string profile, string target)
        {
            return "dsh plugin --profile " + profile + " " + PluginOpText.Verb(op) + " " + target;
        }

        // ==================== WSL 实例 ====================

        /// <summary>
        /// 在 WSL 实例的发行版内执行官方插件命令（登录 shell 加载 PATH）。
        /// 实例配置了 wslHome 时内联 DSH_HOME=... 传递；为空时用发行版默认 ~/.dsh。
        /// </summary>
        public static async Task<PluginOpResult> RunWslAsync(Config cfg, PluginOp op, string profile,
            string target, Action<string> log, int timeoutMs = DefaultAddTimeoutMs)
        {
            string verb = PluginOpText.Verb(op);
            string invalid = ValidateTarget(target);
            if (invalid != null)
                return new PluginOpResult { Error = invalid, Command = Describe(op, profile, target) };

            string distro = cfg.WslDistro ?? "";
            if (string.IsNullOrWhiteSpace(distro))
                return new PluginOpResult { Error = "WSL 实例缺少发行版配置。", Command = Describe(op, profile, target) };

            bool running = await WslTools.IsDistroRunningAsync(distro).ConfigureAwait(false);
            log?.Invoke(string.Format("发行版 {0}：{1}", distro, running ? "运行中" : "未运行（命令会自动拉起，稍慢）"));

            // DSH_HOME 解析：配置了 wslHome → 展开为绝对路径并内联注入
            string homePrefix = "";
            if (!string.IsNullOrWhiteSpace(cfg.WslHome))
            {
                string root = await WslTools.GetDistroHomeAsync(distro).ConfigureAwait(false);
                string home = WslTools.ResolveLinuxPath(cfg.WslHome,
                    string.IsNullOrEmpty(root) ? "/root" : root);
                homePrefix = "DSH_HOME=" + WslTools.Shq(home) + " ";
                log?.Invoke("DSH_HOME（实例隔离）: " + home);
            }
            else
            {
                log?.Invoke("DSH_HOME: 未注入（使用发行版默认 ~/.dsh）");
            }

            // dsh 定位：锁定版本 → 发行版内 npx；否则解析发行版内 dsh 绝对路径，失败退回 PATH
            string runner;
            string pinned = (cfg.HarnessVersion ?? "").Trim();
            if (pinned.Length > 0)
            {
                string npx = await HarnessVersion.ResolveWslNpxAsync(distro).ConfigureAwait(false);
                runner = (string.IsNullOrEmpty(npx) ? "npx" : npx) + " --yes @deepseek-ai/dsh@" + pinned;
            }
            else
            {
                string dshPath = await WslLaunch.ResolveWslDshAsync(distro, cfg.DshCommand).ConfigureAwait(false);
                runner = string.IsNullOrEmpty(dshPath) ? "dsh" : dshPath;
            }

            string cmd = homePrefix + runner + " plugin --profile " + profile + " " + verb + " " + target;
            var result = new PluginOpResult { Command = cmd };
            log?.Invoke("$ " + cmd);

            var sw = Stopwatch.StartNew();
            var r = await WslTools.RunInDistroAsync(distro, cmd, timeoutMs).ConfigureAwait(false);
            result.TimedOut = r.TimedOut;
            result.ExitCode = r.ExitCode;
            result.Output = r.Output ?? "";
            result.Error = r.Error ?? "";
            result.Ok = r.Ok;

            // 输出过长的命令结果只把尾部打进控制台
            foreach (string line in TailLines(result.Output, 40)) log?.Invoke(line);
            if (result.Error.Trim().Length > 0)
                foreach (string line in TailLines(result.Error, 10)) log?.Invoke("[stderr] " + line);
            log?.Invoke(string.Format("插件命令{0}（{1}s，exit={2}）",
                result.Ok ? "完成" : result.TimedOut ? "超时" : "失败",
                (sw.ElapsedMilliseconds / 1000.0).ToString("0.#"), result.ExitCode));
            return result;
        }

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

        /// <summary>窗口关闭时终止仍在进行的 Windows 侧插件命令（WSL 侧命令会自行完成，只写实例 HOME，无害）。</summary>
        public static void KillAll()
        {
            lock (ProcLock)
            {
                foreach (Process p in ActiveProcs)
                {
                    try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch
                    {
                        // 理由: 窗口关闭时尽力终止残留命令，进程可能已退出导致 Kill 抛异常，吞掉避免关闭流程被打断。
                    }
                }
                ActiveProcs.Clear();
            }
        }
    }
}
