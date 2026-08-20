// ============================================================================
//  ErrorReporter — 启动失败 / 崩溃 详细报告（v0.2.0 新增，需求 R3）
//
//  触发点（6 类）：
//    1. dsh 解析失败        2. Process.Start 异常     3. 子进程早退
//    4. 就绪超时(180s)      5. 端口无法释放           6. 全局未处理异常
//  输出：Markdown 报告 → cfg.EffectiveErrorReportDir（UI 可自定义），
//  写入失败兜底 exe 目录 reports\。返回文件路径供 UI 弹窗"打开报告"。
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace DshController.Core
{
    public sealed class StartFailureContext
    {
        public string FailureKind;              // 中文短标签，如"dsh 未找到"
        public string Summary = "";             // 一句话摘要
        public Exception Exception;             // 可空
        public Config Config;                   // 配置快照（可空）
        public List<ResolutionStep> Trace;      // dsh 解析轨迹（可空）
        public IReadOnlyList<string> CapturedOutput; // 子进程输出（最近 N 行）
        public int? ExitCode;                   // 早退时的退出码
        public string Extra = "";               // 额外信息（端口详情等）
        public string InstanceId;               // 实例 ID（多实例 v0.3.0，可空）
        public string InstanceHome;             // 实例 DSH_HOME（多实例 v0.3.0，可空）
        public string ReportPath;               // 已生成报告的完整路径（v0.5.0 核心层写入）
        public IReadOnlyList<string> ConsoleLog;                        // 控制台转录（v0.5.0）
        public IReadOnlyList<KeyValuePair<string, string>> Diagnostics; // 启动诊断键值（v0.5.0）
        public string Phase = "start";          // start / stop（v0.5.0：报告标题与文件名前缀）
    }

    public static class ErrorReporter
    {
        public const string AppVersion = "0.5.0";

        /// <summary>
        /// 启动失败报告。文件名 = DshController-fail_&lt;实例ID&gt;_&lt;时间戳&gt;.md
        /// （实例信息 + 时间同时体现在文件名与内容中）。
        /// 目标目录 = 用户指定的报告目录（cfg.EffectiveErrorReportDir），
        /// 写入失败兜底 exe 目录 reports\。返回写入的文件路径；彻底失败返回 null。
        /// </summary>
        public static string WriteStartFailure(StartFailureContext ctx)
        {
            Config cfg = ctx?.Config;
            string instance = SanitizeFileNamePart(
                string.IsNullOrEmpty(ctx?.InstanceId) ? (cfg?.InstanceId ?? "") : ctx.InstanceId);
            bool stopPhase = string.Equals(ctx?.Phase, "stop", StringComparison.OrdinalIgnoreCase);
            string name = "DshController-" + (stopPhase ? "stopfail" : "fail") + "_" +
                (instance.Length > 0 ? instance + "_" : "") +
                DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".md";
            string md = BuildMarkdown(ctx);
            return WriteReport(name, md, cfg);
        }

        /// <summary>全局崩溃报告。phase: cli/xaml/task/domain。</summary>
        public static string WriteCrash(Exception ex, string phase, Config cfg = null)
        {
            string name = "DshController-crash_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".md";
            var sb = new StringBuilder();
            sb.AppendLine("# DshController 崩溃报告");
            sb.AppendLine();
            sb.AppendLine("- 生成时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("- 应用版本: " + AppVersion);
            sb.AppendLine("- 崩溃阶段: " + phase);
            sb.AppendLine();
            sb.AppendLine("## 异常详情");
            sb.AppendLine();
            sb.AppendLine("```");
            sb.AppendLine(ex == null ? "(null)" : ex.ToString());
            sb.AppendLine("```");
            AppendEnvironment(sb);
            return WriteReport(name, sb.ToString(), cfg);
        }

        /// <summary>全局崩溃报告（多实例 v0.3.0）：从注册表快照取全局报告目录。</summary>
        public static string WriteCrash(Exception ex, string phase, InstanceRegistry registry)
        {
            var cfg = new Config { ErrorReportDir = registry?.Settings?.ErrorReportDir ?? "" };
            return WriteCrash(ex, phase, cfg);
        }

        // ------------------------------------------------------------------

        private static string BuildMarkdown(StartFailureContext ctx)
        {
            var sb = new StringBuilder();
            Config cfg = ctx.Config ?? new Config();
            bool stopPhase = string.Equals(ctx.Phase, "stop", StringComparison.OrdinalIgnoreCase);

            sb.AppendLine("# DSH 后端" + (stopPhase ? "停止" : "启动") + "失败报告");
            sb.AppendLine();
            sb.AppendLine("- 生成时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"));
            sb.AppendLine("- 应用版本: " + AppVersion);
            sb.AppendLine("- 失败类型: **" + (ctx.FailureKind ?? "未知") + "**");
            sb.AppendLine();
            sb.AppendLine("## 摘要");
            sb.AppendLine();
            sb.AppendLine(string.IsNullOrEmpty(ctx.Summary) ? ctx.FailureKind : ctx.Summary);
            if (ctx.ExitCode.HasValue) sb.AppendLine("子进程退出码: `" + ctx.ExitCode.Value + "`");
            if (!string.IsNullOrEmpty(ctx.Extra)) sb.AppendLine(ctx.Extra);

            // 1 实例信息（v0.5.0：实例信息 + 时间 完整归档）
            sb.AppendLine();
            sb.AppendLine("## 实例信息");
            sb.AppendLine();
            string instanceId = !string.IsNullOrEmpty(ctx.InstanceId) ? ctx.InstanceId : (cfg.InstanceId ?? "");
            string instanceName = !string.IsNullOrEmpty(cfg.InstanceName) ? cfg.InstanceName : "（未命名）";
            string instanceHome = !string.IsNullOrEmpty(ctx.InstanceHome) ? ctx.InstanceHome : (cfg.Home ?? "");
            sb.AppendLine("- 实例名称: `" + instanceName + "`");
            sb.AppendLine("- 实例 ID: `" + (instanceId.Length > 0 ? instanceId : "（默认实例）") + "`");
            sb.AppendLine("- 运行环境: **" + (cfg.IsWsl ? "WSL2" : "Windows") + "**");
            sb.AppendLine("- 主机:端口: `" + cfg.Host + ":" + cfg.Port + "`");
            if (cfg.IsWsl)
            {
                sb.AppendLine("- WSL 发行版: `" + (string.IsNullOrWhiteSpace(cfg.WslDistro) ? "（未配置）" : cfg.WslDistro) + "`");
                sb.AppendLine("- WSL DSH_HOME(Linux): `" + (string.IsNullOrWhiteSpace(cfg.WslHome) ? "~/.dsh" : cfg.WslHome) + "`");
            }
            else
            {
                sb.AppendLine("- DSH_HOME: `" + (string.IsNullOrEmpty(instanceHome) ? "~/.dsh（默认，不注入）" : instanceHome) + "`");
            }
            string hv = (cfg.HarnessVersion ?? "").Trim();
            sb.AppendLine("- harness 版本: " + (hv.Length > 0
                ? "**锁定 " + hv + "**（经 npx 拉取）"
                : "跟随当前环境主实例版本（未锁定）"));
            sb.AppendLine("- 工作目录: `" + cfg.Workspace + "`" + (cfg.IsWsl
                ? "（WSL 实例：路径在发行版内解析，Windows 侧不校验）"
                : "（" + (Directory.Exists(cfg.Workspace) ? "存在" : "不存在") + "）"));

            // 1.5 启动诊断（v0.5.0：核心层逐步记录的真实启动上下文）
            if (ctx.Diagnostics != null && ctx.Diagnostics.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("## 启动诊断（失败前已确认的事实）");
                sb.AppendLine();
                sb.AppendLine("| 项目 | 值 |");
                sb.AppendLine("|---|---|");
                foreach (KeyValuePair<string, string> kv in ctx.Diagnostics)
                    sb.AppendLine("| " + kv.Key + " | `" + (string.IsNullOrEmpty(kv.Value) ? "—" : kv.Value.Replace("|", "\\|")) + "` |");
            }

            // 1.6 控制台转录（v0.5.0：用户要求"控制台的具体报错信息"进报告）
            sb.AppendLine();
            sb.AppendLine("## 控制台转录（本实例最近日志）");
            sb.AppendLine();
            if (ctx.ConsoleLog != null && ctx.ConsoleLog.Count > 0)
            {
                sb.AppendLine("```log");
                foreach (string line in ctx.ConsoleLog) sb.AppendLine(line);
                sb.AppendLine("```");
            }
            else sb.AppendLine("（无控制台日志）");

            // 2 环境
            AppendEnvironment(sb);

            // 2 dsh 解析（仅 Windows 实例；WSL 实例的解析发生在发行版内，见启动诊断）
            sb.AppendLine();
            sb.AppendLine("## dsh 命令解析");
            sb.AppendLine();
            if (cfg.IsWsl)
            {
                sb.AppendLine("（WSL 实例：dsh/npx 在发行版 `" +
                    (string.IsNullOrWhiteSpace(cfg.WslDistro) ? "?" : cfg.WslDistro) +
                    "` 内解析，结果见上方「启动诊断」表）");
            }
            else if (ctx.Trace != null && ctx.Trace.Count > 0)
            {
                sb.AppendLine("| 来源 | 候选路径 | 结果 |");
                sb.AppendLine("|---|---|---|");
                foreach (ResolutionStep s in ctx.Trace)
                    sb.AppendLine("| " + s.Source + " | `" + (string.IsNullOrEmpty(s.Path) ? "—" : s.Path) +
                                  "` | " + (s.Found ? "✅ 存在" : "❌ 未找到") + " |");
            }
            else sb.AppendLine("（未记录解析轨迹）");

            // 3 配置
            sb.AppendLine();
            sb.AppendLine("## 本次配置（instances.json 视角）");
            sb.AppendLine();
            sb.AppendLine("```json");
            sb.AppendLine("{");
            sb.AppendLine("  \"instanceId\": " + J(cfg.InstanceId) + ",");
            sb.AppendLine("  \"name\": " + J(cfg.InstanceName) + ",");
            sb.AppendLine("  \"runtime\": " + J(cfg.Runtime) + ",");
            sb.AppendLine("  \"host\": " + J(cfg.Host) + ",");
            sb.AppendLine("  \"port\": " + cfg.Port + ",");
            sb.AppendLine("  \"workspace\": " + J(cfg.Workspace) + ",");
            sb.AppendLine("  \"dshCommand\": " + J(cfg.DshCommand) + ",");
            sb.AppendLine("  \"home\": " + J(cfg.Home) + ",");
            if (cfg.IsWsl)
            {
                sb.AppendLine("  \"wslDistro\": " + J(cfg.WslDistro) + ",");
                sb.AppendLine("  \"wslHome\": " + J(cfg.WslHome) + ",");
                sb.AppendLine("  \"wslShutdownPolicy\": " + J(cfg.WslShutdownPolicy) + ",");
            }
            sb.AppendLine("  \"harnessVersion\": " + J(cfg.HarnessVersion) + ",");
            sb.AppendLine("  \"trustedHosts\": " + FormatTrustedHosts(cfg.TrustedHosts) + ",");
            sb.AppendLine("  \"autoOpenBrowser\": " + (cfg.AutoOpenBrowser ? "true" : "false") + ",");
            sb.AppendLine("  \"stopOnExit\": " + (cfg.StopOnExit ? "true" : "false") + ",");
            sb.AppendLine("  \"errorReportDir\": " + J(cfg.EffectiveErrorReportDir));
            sb.AppendLine("}");
            sb.AppendLine("```");

            // 4 端口与进程
            sb.AppendLine();
            sb.AppendLine("## 端口状态");
            sb.AppendLine();
            bool up = PortTools.ProbeAsync(cfg.Host, cfg.Port).GetAwaiter().GetResult();
            sb.AppendLine("- 探测 `" + PortTools.Url(cfg.Host, cfg.Port) + "`: " + (up ? "**在线**" : "离线"));
            if (up)
            {
                int pid = PortTools.FindListenerPidAsync(cfg.Port).GetAwaiter().GetResult();
                sb.AppendLine("- 监听 PID: " + (pid > 0 ? pid.ToString() : "(netstat 未定位到)"));
            }
            sb.AppendLine("- 工作目录存在: " + (Directory.Exists(cfg.Workspace) ? "是" : "否（`" + cfg.Workspace + "`）"));

            // 5 输出转录
            sb.AppendLine();
            sb.AppendLine("## 子进程输出转录（最近输出）");
            sb.AppendLine();
            if (ctx.CapturedOutput != null && ctx.CapturedOutput.Count > 0)
            {
                sb.AppendLine("```");
                foreach (string line in ctx.CapturedOutput.Take(400)) sb.AppendLine(line);
                sb.AppendLine("```");
            }
            else sb.AppendLine("（无输出）");

            // 6 异常
            if (ctx.Exception != null)
            {
                sb.AppendLine();
                sb.AppendLine("## 异常详情");
                sb.AppendLine();
                sb.AppendLine("```");
                sb.AppendLine(ctx.Exception.ToString());
                sb.AppendLine("```");
            }

            // 7 建议
            sb.AppendLine();
            sb.AppendLine("## 排障建议");
            sb.AppendLine();
            sb.AppendLine(SuggestionsFor(ctx));
            return sb.ToString();
        }

        private static string FormatTrustedHosts(string[] trustedHosts)
        {
            if (trustedHosts == null || trustedHosts.Length == 0) return "[]";
            return "[" + string.Join(", ", trustedHosts.Select(J)) + "]";
        }

        /// <summary>JSON 字符串字面量（转义反斜杠/引号，保证报告里的 json 块可直接解析）。</summary>
        private static string J(string s)
        {
            if (s == null) return "\"\"";
            var sb = new StringBuilder("\"");
            foreach (char c in s)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\t': sb.Append("\\t"); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.Append('"').ToString();
        }

        private static void AppendEnvironment(StringBuilder sb)
        {
            sb.AppendLine();
            sb.AppendLine("## 环境");
            sb.AppendLine();
            sb.AppendLine("- 操作系统: " + PortTools.OsDescription());
            sb.AppendLine("- .NET: " + Environment.Version + (Environment.Is64BitProcess ? " (x64 进程)" : ""));
            string home = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh");
            sb.AppendLine("- $DSH_HOME (`" + home + "`): " + (Directory.Exists(home) ? "存在" : "不存在"));
            string node = DshResolver.FindNode();
            sb.AppendLine("- node.exe: " + (node == null ? "未找到" : "`" + node + "`"));
            string npmDir = DshResolver.NpmGlobalDir;
            sb.AppendLine("- npm 全局目录: `" + npmDir + "` (" + (Directory.Exists(npmDir) ? "存在" : "不存在") + ")");
        }

        private static string SuggestionsFor(StartFailureContext ctx)
        {
            string k = ctx.FailureKind ?? "";
            bool isWsl = ctx.Config != null && ctx.Config.IsWsl;

            // WSL 专属失败先判定（否则会被通用的"未找到"分支吃掉）
            if (k.Contains("发行版") || k.Contains("WSL"))
                return "1. `wsl --list --verbose` 确认发行版名称与本实例设置完全一致（区分大小写以外的拼写）\n" +
                       "2. 首次使用发行版需先 `wsl -d <发行版>` 完成用户初始化（默认用户不能是 root）\n" +
                       "3. 发行版内安装 harness：`npm install -g @deepseek-ai/dsh`（WSL 实例不使用 Windows 侧 dsh）\n" +
                       "4. 也可以给该实例锁定 harness 版本，改用发行版内 npx 拉取指定版本启动";
            if (k.Contains("npx") || k.Contains("版本"))
                return "1. 指定版本启动依赖 npx：确认" + (isWsl ? "WSL 发行版内" : "本机") + "已安装 Node.js/npm\n" +
                       "2. 确认版本号拼写正确（形如 0.1.0-rc.7）；可先执行 `npx --yes @deepseek-ai/dsh@<版本> --version` 验证\n" +
                       "3. 首次拉取需联网；也可把该实例的 harness 版本改回「跟随当前环境」";
            if (k.Contains("工作区") || k.Contains("工作目录"))
                return "1. 确认工作目录存在且当前用户可读写\n" +
                       "2. WSL 实例的工作区如需共享 Windows 文件，请填 Windows 路径（自动转 /mnt/c）；" +
                       "纯 Linux 隔离请填 `~/xxx` 形式\n" +
                       "3. 路径含空格/中文时无需手动加引号，程序已做转义";
            if (k.Contains("未找到") || k.Contains("dsh"))
                return "1. 确认已安装：`npm i -g @deepseek-ai/dsh`\n" +
                       "2. 或在全局设置的 dshCommand 中填写 dsh.cmd 完整路径\n" +
                       "3. 安装后无需重启本程序，直接重试启动\n" +
                       "4. 或给该实例锁定 harness 版本，改用 npx 拉取指定版本启动";
            if (k.Contains("启动") || k.Contains("Spawn"))
                return "1. 检查工作目录是否可访问（见上方配置节）\n" +
                       "2. 从服务/非交互环境启动时确认 cmd.exe 在 PATH 中\n" +
                       "3. 尝试在终端手动执行「启动诊断」表里的命令行验证";
            if (k.Contains("早退"))
                return "1. 查看上方「控制台转录」「子进程输出转录」中的报错行\n" +
                       "2. 常见原因：node 版本过低 / 端口参数被占用 / DSH_HOME 损坏 / 锁定的版本不存在\n" +
                       "3. 可在终端手动运行「启动诊断」表里的命令行复现并观察完整输出";
            if (k.Contains("超时"))
                return "1. 首次启动可能较慢（依赖初始化；锁定版本时 npx 还要下载包），可直接重试\n" +
                       "2. 查看输出转录确认无报错卡点\n" +
                       "3. 机器负载高时适当延长等待";
            if (k.Contains("端口"))
                return "1. 端口被其他进程占用：在实例设置中更换端口，或用任务管理器结束占用进程\n" +
                       "2. 若为本程序残留进程，重启本程序会自动清理\n" +
                       "3. WSL 实例与 Windows 实例共享同一套 localhost 端口，注意不要撞号";
            return "1. 查看上方各节信息定位原因\n2. 可附带本报告提 Issue: https://github.com/DreamRift/dsh-harness-controller/issues";
        }

        /// <summary>落盘：目标目录 → 兜底 exe\reports。返回路径或 null。</summary>
        private static string WriteReport(string fileName, string content, Config cfg)
        {
            string targetDir = cfg != null ? cfg.EffectiveErrorReportDir : null;
            if (string.IsNullOrEmpty(targetDir)) targetDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "DshController", "error-reports");

            string path = TryWrite(targetDir, fileName, content);
            if (path == null)
            {
                string fallback = Path.Combine(AppContext.BaseDirectory, "reports");
                path = TryWrite(fallback, fileName, content);
            }
            return path;
        }

        /// <summary>文件名片段净化：仅保留字母/数字/_/-，超长截断。</summary>
        private static string SanitizeFileNamePart(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            var sb = new StringBuilder();
            foreach (char c in s.Trim())
            {
                if (char.IsLetterOrDigit(c) || c == '_' || c == '-') sb.Append(c);
                else if (sb.Length > 0 && sb[sb.Length - 1] != '-') sb.Append('-');
                if (sb.Length >= 40) break;
            }
            return sb.ToString().Trim('-');
        }

        private static string TryWrite(string dir, string fileName, string content)
        {
            try
            {
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, fileName);
                File.WriteAllText(path, content, new UTF8Encoding(false));
                return path;
            }
            catch
            {
                return null;
            }
        }
    }
}
