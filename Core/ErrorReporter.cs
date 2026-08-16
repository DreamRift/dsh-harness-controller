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
    }

    public static class ErrorReporter
    {
        public const string AppVersion = "0.3.0";

        /// <summary>启动失败报告。返回写入的文件路径；彻底失败返回 null。</summary>
        public static string WriteStartFailure(StartFailureContext ctx)
        {
            string name = "DshController-fail_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".md";
            string md = BuildMarkdown(ctx);
            return WriteReport(name, md, ctx.Config);
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

            sb.AppendLine("# DSH 后端启动失败报告");
            sb.AppendLine();
            sb.AppendLine("- 生成时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("- 应用版本: " + AppVersion);
            sb.AppendLine("- 失败类型: **" + (ctx.FailureKind ?? "未知") + "**");
            sb.AppendLine();
            sb.AppendLine("## 摘要");
            sb.AppendLine();
            sb.AppendLine(string.IsNullOrEmpty(ctx.Summary) ? ctx.FailureKind : ctx.Summary);
            if (ctx.ExitCode.HasValue) sb.AppendLine("子进程退出码: `" + ctx.ExitCode.Value + "`");
            if (!string.IsNullOrEmpty(ctx.Extra)) sb.AppendLine(ctx.Extra);

            // 1 实例信息
            sb.AppendLine();
            sb.AppendLine("## 实例信息");
            sb.AppendLine();
            if (!string.IsNullOrEmpty(ctx.InstanceId)) sb.AppendLine("实例 ID: `" + ctx.InstanceId + "`");
            if (!string.IsNullOrEmpty(ctx.InstanceHome)) sb.AppendLine("DSH_HOME: `" + ctx.InstanceHome + "`");
            if (string.IsNullOrEmpty(ctx.InstanceId) && string.IsNullOrEmpty(ctx.InstanceHome))
                sb.AppendLine("（默认实例，未注入 DSH_HOME）");

            // 2 环境
            AppendEnvironment(sb);

            // 2 dsh 解析
            sb.AppendLine();
            sb.AppendLine("## dsh 命令解析");
            sb.AppendLine();
            if (ctx.Trace != null && ctx.Trace.Count > 0)
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
            sb.AppendLine("## 本次配置（instances.json）");
            sb.AppendLine();
            sb.AppendLine("```json");
            sb.AppendLine("{");
            sb.AppendLine("  \"host\": \"" + cfg.Host + "\",");
            sb.AppendLine("  \"port\": " + cfg.Port + ",");
            sb.AppendLine("  \"workspace\": \"" + cfg.Workspace + "\",");
            sb.AppendLine("  \"dshCommand\": \"" + cfg.DshCommand + "\",");
            sb.AppendLine("  \"home\": \"" + (string.IsNullOrEmpty(cfg.Home) ? "（默认 ~/.dsh）" : cfg.Home) + "\",");
            sb.AppendLine("  \"trustedHosts\": " + FormatTrustedHosts(cfg.TrustedHosts) + ",");
            sb.AppendLine("  \"autoOpenBrowser\": " + (cfg.AutoOpenBrowser ? "true" : "false") + ",");
            sb.AppendLine("  \"stopOnExit\": " + (cfg.StopOnExit ? "true" : "false") + ",");
            sb.AppendLine("  \"errorReportDir\": \"" + cfg.EffectiveErrorReportDir + "\"");
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
            if (trustedHosts == null || trustedHosts.Length == 0) return "（无）";
            return "\"" + string.Join("、", trustedHosts) + "\"";
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
            if (k.Contains("未找到") || k.Contains("dsh"))
                return "1. 确认已安装：`npm i -g @deepseek-ai/dsh`\n" +
                       "2. 或在设置的 dshCommand 中填写 dsh.cmd 完整路径\n" +
                       "3. 安装后无需重启本程序，直接重试启动";
            if (k.Contains("启动") || k.Contains("Spawn"))
                return "1. 检查工作目录是否可访问（见上方配置节）\n" +
                       "2. 从服务/非交互环境启动时确认 cmd.exe 在 PATH 中\n" +
                       "3. 尝试在终端手动执行上方解析出的命令验证";
            if (k.Contains("早退"))
                return "1. 查看上方输出转录中的报错行\n" +
                       "2. 常见原因：node 版本过低 / 端口参数被占用 / DSH_HOME 损坏\n" +
                       "3. 可在终端手动运行 `dsh web` 复现并观察完整输出";
            if (k.Contains("超时"))
                return "1. 首次启动可能较慢（模型/依赖初始化），可直接重试\n" +
                       "2. 查看输出转录确认无报错卡点\n" +
                       "3. 机器负载高时适当延长等待";
            if (k.Contains("端口"))
                return "1. 端口被其他进程占用：在设置中更换端口，或用任务管理器结束占用进程\n" +
                       "2. 若为本程序残留进程，重启本程序会自动清理";
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
