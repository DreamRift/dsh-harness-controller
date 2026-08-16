// ============================================================================
//  Cli — 无界面自检模式（v0.1.0 语义原样移植到 v0.2.0 架构）
//
//    DshController.exe --check                        打印 dsh 解析结果与端口状态
//    DshController.exe --spawn-test [--port N]        真实启动/停止 dsh web 实例
//    DshController.exe --spawn-test-node [--port N]   仅验证进程管线（微型 node 服务）
//    DshController.exe --version                      打印版本
//  退出码语义与 v0.1.0 一致；输出同时转录到 exe 旁 cli.log。
//  附加 --noredirect 可在禁止管道重定向的受限环境运行。
// ============================================================================

using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DshController.Core
{
    internal static class Cli
    {
        /// <summary>尝试处理 CLI 参数。返回 true 表示已作为 CLI 运行（out 退出码）。</summary>
        public static bool TryRun(string[] args, out int exitCode)
        {
            exitCode = 0;
            if (args == null || args.Length == 0) return false;
            string a = args[0].ToLowerInvariant();
            if (a == "--check") { exitCode = Check(args); return true; }
            if (a == "--spawn-test") { exitCode = SpawnTest(args, useRealDsh: true); return true; }
            if (a == "--spawn-test-node") { exitCode = SpawnTest(args, useRealDsh: false); return true; }
            if (a == "--version" || a == "-v")
            {
                AttachConsoleOutput();
                Console.WriteLine("DshController " + ErrorReporter.AppVersion);
                return true;
            }
            if (a == "--selftest-core")
            {
                AttachConsoleOutput();
                exitCode = CoreSelfTest.Run(args);
                return true;
            }
            return false; // 未知参数 → 继续启动 GUI（与 v0.1.0 行为一致）
        }

        /// <summary>把 stdout 接到父进程控制台（从终端运行时可显示）。</summary>
        private static void AttachConsoleOutput()
        {
            try
            {
                if (NativeMethods.AttachConsole(NativeMethods.ATTACH_PARENT_PROCESS))
                {
                    var handle = new Microsoft.Win32.SafeHandles.SafeFileHandle(
                        NativeMethods.GetStdHandle(NativeMethods.STD_OUTPUT_HANDLE), false);
                    Console.SetOut(new StreamWriter(new FileStream(handle, FileAccess.Write)) { AutoFlush = true });
                    Console.SetError(new StreamWriter(new FileStream(handle, FileAccess.Write)) { AutoFlush = true });
                }
            }
            catch { }
        }

        private static void WriteCliLog(StringBuilder transcript)
        {
            try
            {
                File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "cli.log"),
                    transcript.ToString(), new UTF8Encoding(false));
            }
            catch { }
        }

        /// <summary>--check：打印 dsh 解析结果与端口状态。</summary>
        private static int Check(string[] args)
        {
            AttachConsoleOutput();
            var transcript = new StringBuilder();
            Action<string> Out = line => { transcript.AppendLine(line); try { Console.WriteLine(line); } catch { } };

            Config cfg = Config.Load();
            int port = cfg.Port;
            for (int i = 1; i < args.Length; i++)
                if (args[i] == "--port" && i + 1 < args.Length)
                {
                    int p; if (int.TryParse(args[i + 1], out p)) port = p;
                }

            var resolver = new DshResolver();
            DshCommand dsh = resolver.Resolve(cfg);
            bool up = PortTools.ProbeAsync(cfg.Host, port).GetAwaiter().GetResult();
            Out("dsh command  : " + (dsh == null ? "(NOT FOUND)" : dsh.Describe()));
            Out("host         : " + cfg.Host);
            Out("port         : " + port);
            Out("workspace    : " + cfg.Workspace);
            Out("backend      : " + (up ? "UP" : "DOWN"));
            if (up) Out("listener pid : " + PortTools.FindListenerPidAsync(port).GetAwaiter().GetResult());
            Out("report dir   : " + cfg.EffectiveErrorReportDir);
            Out("launcher.json: " + Config.FilePath);
            WriteCliLog(transcript);
            return dsh == null ? 2 : 0;
        }

        /// <summary>--spawn-test：真实启动 → 等待就绪 → 杀进程树 → 验证端口关闭。</summary>
        private static int SpawnTest(string[] args, bool useRealDsh)
        {
            AttachConsoleOutput();
            var transcript = new StringBuilder();
            Action<string> Out = line =>
            {
                transcript.AppendLine(line);
                try { Console.WriteLine(line); } catch { }
            };
            Action<string> Trace = line => { Out(line); WriteCliLog(transcript); };

            Config cfg = Config.Load();
            int port = cfg.Port + 1;
            bool noRedirect = false;
            for (int i = 1; i < args.Length; i++)
            {
                if (args[i] == "--port" && i + 1 < args.Length)
                {
                    int p; if (int.TryParse(args[i + 1], out p)) port = p;
                }
                if (args[i] == "--noredirect") noRedirect = true;
            }

            string desc;
            ProcessStartInfo psi;
            if (useRealDsh)
            {
                var resolver = new DshResolver();
                DshCommand dsh = resolver.Resolve(cfg);
                if (dsh == null)
                {
                    Out("FAIL: dsh command not found");
                    WriteCliLog(transcript);
                    return 2;
                }
                Trace("step: dsh resolved - " + dsh.Describe());
                desc = "dsh web";
                if (dsh.Kind == "cmd")
                {
                    psi = new ProcessStartInfo("cmd.exe",
                        "/d /s /c \"\"" + dsh.Path1 + "\" web --host " + cfg.Host + " --port " + port + "\"");
                }
                else
                {
                    psi = new ProcessStartInfo(dsh.Path1,
                        "\"" + dsh.Path2 + "\" web --host " + cfg.Host + " --port " + port);
                }
            }
            else
            {
                string node = DshResolver.FindNode();
                if (string.IsNullOrEmpty(node))
                {
                    Out("FAIL: node not found on PATH");
                    WriteCliLog(transcript);
                    return 2;
                }
                Trace("step: node resolved - " + node);
                string js = Path.Combine(AppContext.BaseDirectory, "test-server.js");
                psi = new ProcessStartInfo(node, "\"" + js + "\" " + port);
                desc = "node test-server";
            }

            if (PortTools.ProbeAsync(cfg.Host, port).GetAwaiter().GetResult())
            {
                Out("FAIL: port " + port + " already in use by pid " +
                    PortTools.FindListenerPidAsync(port).GetAwaiter().GetResult());
                WriteCliLog(transcript);
                return 1;
            }
            Trace("step: port " + port + " free, spawning...");

            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            if (!noRedirect)
            {
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.StandardOutputEncoding = Encoding.UTF8;
                psi.StandardErrorEncoding = Encoding.UTF8;
            }
            psi.WorkingDirectory = cfg.Workspace;
            if (!Directory.Exists(psi.WorkingDirectory)) Directory.CreateDirectory(psi.WorkingDirectory);

            try
            {
                using (Process child = Process.Start(psi))
                {
                    Trace("step: spawned pid " + child.Id + " (" + desc + ")");
                    if (!noRedirect)
                    {
                        child.OutputDataReceived += (s, e) => { try { if (!string.IsNullOrEmpty(e.Data)) Out("  [out] " + e.Data); } catch { } };
                        child.ErrorDataReceived += (s, e) => { try { if (!string.IsNullOrEmpty(e.Data)) Out("  [err] " + e.Data); } catch { } };
                        child.BeginOutputReadLine();
                        child.BeginErrorReadLine();
                    }

                    DateTime started = DateTime.UtcNow;
                    bool up = false;
                    while (DateTime.UtcNow - started < TimeSpan.FromSeconds(180))
                    {
                        if (child.HasExited)
                        {
                            Trace("step: process exited early with code " + child.ExitCode);
                            WriteCliLog(transcript);
                            return 1;
                        }
                        if (PortTools.ProbeAsync(cfg.Host, port).GetAwaiter().GetResult()) { up = true; break; }
                        Thread.Sleep(700);
                    }
                    if (!up)
                    {
                        Trace("step: backend did not become ready within 180s");
                        WriteCliLog(transcript);
                        return 1;
                    }
                    Trace("READY: " + PortTools.Url(cfg.Host, port) + " after ~" +
                          (int)(DateTime.UtcNow - started).TotalSeconds + "s");

                    var r = PortTools.EnsurePortFreeAsync(cfg.Host, port, child.Id).GetAwaiter().GetResult();
                    Trace("step: stop backend -> " + (r.Item1 ? "OK" : "FAIL"));
                    Trace(r.Item2);
                    WriteCliLog(transcript);
                    return r.Item1 ? 0 : 1;
                }
            }
            catch (Exception ex)
            {
                Out("FAIL: " + ex.Message);
                WriteCliLog(transcript);
                return 1;
            }
        }
    }
}
