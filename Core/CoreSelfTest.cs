// ============================================================================
//  CoreSelfTest — 核心链路无头自检（--selftest-core）
//
//  直接驱动发布二进制内的真实产品代码（BackendManager / ErrorReporter /
//  PortTools / Config），不依赖 GUI 与桌面交互，可在 CI/远程环境运行：
//
//    1. fail-dsh 注入启动 → 断言 EarlyExit 失败 + 报告生成于自定义目录（需求 R3）
//    2. 真实 dsh 启动     → 断言就绪进入 Running（Ready 事件携带 URL）
//    3. 重启              → 断言 Stop→Start 完成、PID 变化、Ready.SuppressAutoOpen
//                           === true（需求 R4：重启绝不拉浏览器）
//    4. 停止              → 断言端口释放、状态回 Stopped
//    5. launcher.json 旧格式（含反斜杠污染）迁移净化
//
//  端口使用 3185+（避开 3080 用户实例与常用测试端口）；退出码 0=全部通过。
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DshController.Core
{
    internal static class CoreSelfTest
    {
        private static int _pass;
        private static int _fail;

        private static void Check(bool cond, string name, string detail = "")
        {
            if (cond) { _pass++; Console.WriteLine("  PASS  " + name + (detail == "" ? "" : "  (" + detail + ")")); }
            else { _fail++; Console.WriteLine("  FAIL  " + name + (detail == "" ? "" : "  (" + detail + ")")); }
        }

        public static int Run(string[] args)
        {
            int basePort = 3185;
            for (int i = 1; i < args.Length; i++)
                if (args[i] == "--port" && i + 1 < args.Length)
                {
                    int p; if (int.TryParse(args[i + 1], out p)) basePort = p;
                }

            string binDir = AppContext.BaseDirectory;
            string rptDir = Path.Combine(Path.GetTempPath(), "dsh-selftest-reports");
            try { if (Directory.Exists(rptDir)) Directory.Delete(rptDir, true); } catch { }

            Console.WriteLine("== DshController core self-test ==");
            Console.WriteLine("report dir: " + rptDir);

            // ---------- 5) 配置迁移 ----------
            Console.WriteLine("[5] launcher.json 净化迁移");
            {
                var c = new Config { Workspace = @"C:\\\\\Users\\\x", DshCommand = @"C:\\\\a\\\\b.cmd" };
                // 直接构造污染值（绕过 setter 不做净化——SanitizePath 在 Load 时执行）
                string polluted = @"C:\\\\\\Users\\\\test";
                string clean = Config.SanitizePath(polluted);
                Check(clean == @"C:\Users\test", "多重反斜杠收敛", polluted + " -> " + clean);
                string unc = @"\\\\server\\share";
                string uncClean = Config.SanitizePath(unc);
                Check(uncClean == @"\\server\share", "UNC 前导双斜杠保留", unc + " -> " + uncClean);
            }

            // ---------- 公共管理器 ----------
            var readyEvents = new List<ReadyEventArgs>();
            var failEvents = new List<StartFailureContext>();
            var logs = new List<string>();
            var mgr = new BackendManager(null); // 无 UI：事件仍在线程池触发
            mgr.Ready += (s, e) => { lock (readyEvents) readyEvents.Add(e); };
            mgr.StartFailed += (s, e) => { lock (failEvents) failEvents.Add(e); };
            mgr.Log += (s, l) => { lock (logs) logs.Add(l); };

            // ---------- 1) 失败注入 → 报告（R3） ----------
            Console.WriteLine("[1] 失败注入 → 错误报告（自定义目录）");
            string failCmd = Path.Combine(binDir, "fail-dsh.cmd");
            File.WriteAllText(failCmd, "@exit /b 42\r\n");
            var cfgFail = new Config
            {
                Host = "127.0.0.1",
                Port = basePort,
                Workspace = Path.GetTempPath(),
                DshCommand = failCmd,
                ErrorReportDir = rptDir
            };
            failEvents.Clear();
            bool started = mgr.StartAsync(cfgFail).GetAwaiter().GetResult();
            for (int i = 0; i < 40 && failEvents.Count == 0; i++) Thread.Sleep(250);
            Check(failEvents.Count == 1, "EarlyExit 失败事件触发",
                failEvents.Count > 0 ? failEvents[0].FailureKind : "无事件");
            Check(failEvents.Count > 0 && failEvents[0].ExitCode == 42, "退出码 42 透传");

            string report = null;
            if (failEvents.Count > 0)
            {
                try { report = ErrorReporter.WriteStartFailure(failEvents[0]); }
                catch { }
            }
            if (report == null && Directory.Exists(rptDir))
            {
                try
                {
                    var fs = Directory.GetFiles(rptDir, "DshController-fail_*.md");
                    if (fs.Length > 0) report = fs[0];
                }
                catch { }
            }
            Check(report != null, "报告生成于自定义目录", report ?? "未生成");
            if (report != null)
            {
                string md = File.ReadAllText(report);
                Check(md.Contains("## dsh 命令解析"), "报告含解析轨迹");
                Check(md.Contains("## 本次配置"), "报告含配置节");
                Check(md.Contains("## 子进程输出转录"), "报告含输出转录");
                Check(md.Contains("## 排障建议"), "报告含排障建议");
                Check(md.Contains("42"), "报告含退出码");
            }
            Check(mgr.State == BackendState.Stopped, "失败后状态回 Stopped");

            // ---------- 2) 真实 dsh 启动 ----------
            Console.WriteLine("[2] 真实 dsh 启动");
            var cfg = new Config
            {
                Host = "127.0.0.1",
                Port = basePort + 1,
                Workspace = Path.GetTempPath(),
                ErrorReportDir = rptDir
            };
            readyEvents.Clear(); failEvents.Clear();
            started = mgr.StartAsync(cfg, new StartOptions { ReadyTimeoutSeconds = 120 }).GetAwaiter().GetResult();
            bool up = false;
            for (int i = 0; i < 120 && !up; i++)
            {
                up = PortTools.ProbeAsync(cfg.Host, cfg.Port).GetAwaiter().GetResult();
                if (!up) Thread.Sleep(1000);
            }
            Check(up, "后端就绪（端口监听）");
            Check(mgr.State == BackendState.Running && mgr.IsMine, "状态 Running(本程序)");
            int pid1 = mgr.ChildPid;
            Check(pid1 > 0, "记录子进程 PID", pid1.ToString());
            Check(readyEvents.Count > 0 && !readyEvents[0].SuppressAutoOpen,
                "正常启动 Ready 不抑制浏览器");

            // ---------- 3) 重启（R4：不拉浏览器） ----------
            Console.WriteLine("[3] 重启（仅后端，不打开浏览器）");
            readyEvents.Clear(); failEvents.Clear();
            bool rst = mgr.RestartAsync(cfg).GetAwaiter().GetResult();
            bool up2 = false;
            for (int i = 0; i < 120 && !up2; i++)
            {
                up2 = PortTools.ProbeAsync(cfg.Host, cfg.Port).GetAwaiter().GetResult();
                if (!up2) Thread.Sleep(1000);
            }
            Check(rst && up2, "重启后端口重新监听");
            Check(mgr.State == BackendState.Running && mgr.IsMine, "重启后 Running(本程序)");
            Check(mgr.ChildPid != pid1 && mgr.ChildPid > 0, "子进程 PID 已更换",
                pid1 + " -> " + mgr.ChildPid);
            Check(readyEvents.Count > 0 && readyEvents[0].SuppressAutoOpen,
                "重启路径 Ready.SuppressAutoOpen = true（不拉浏览器）");
            Check(failEvents.Count == 0, "重启过程无失败报告",
                failEvents.Count > 0 ? failEvents[0].FailureKind : "");

            // ---------- 4) 停止 ----------
            Console.WriteLine("[4] 停止与端口释放");
            bool stopped = mgr.StopAsync(cfg, killExternal: false).GetAwaiter().GetResult();
            Check(stopped, "停止成功");
            bool down = !PortTools.ProbeAsync(cfg.Host, cfg.Port).GetAwaiter().GetResult();
            Check(down, "端口已释放");
            Check(mgr.State == BackendState.Stopped, "状态回 Stopped");

            mgr.Dispose();
            try { File.Delete(failCmd); } catch { }

            Console.WriteLine("== 结果: " + _pass + " passed, " + _fail + " failed ==");
            return _fail == 0 ? 0 : 1;
        }
    }
}
