// ============================================================================
//  Cli — 无界面自检模式（v0.1.0 语义原样移植到 v0.2.0 架构）
//
//    DshController.exe --check                        打印 dsh 解析结果与端口状态
//    DshController.exe --spawn-test [--port N] [--home <dir>]
//                                                     真实启动/停止 dsh web 实例（可选 DSH_HOME 注入验证）
//    DshController.exe --spawn-test-node [--port N]   仅验证进程管线（微型 node 服务）
//    DshController.exe --instance <id> start|stop|restart|status
//                                                     定向操作指定实例（无 GUI）
//    DshController.exe --version                      打印版本
//  退出码语义与 v0.1.0 一致；输出同时转录到 exe 旁 cli.log。
//  附加 --noredirect 可在禁止管道重定向的受限环境运行。
// ============================================================================

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
            if (a == "--instance" && args.Length >= 3)
            {
                AttachConsoleOutput();
                exitCode = Instance(args);
                return true;
            }
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

        /// <summary>版本号展示：空 → (未检测到)，否则加 v 前缀。</summary>
        private static string FormatVer(string v)
        {
            return string.IsNullOrWhiteSpace(v) ? "(未检测到)" : "v" + v.Trim();
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

            // v0.3.0：主配置取实例清单的第一个实例（launcher.json 迁移后已退役）
            var registry = InstanceRegistry.Load();
            InstanceDef main = registry.Instances.Count > 0 ? registry.Instances[0] : new InstanceDef();
            Config cfg = main.ToConfig(registry.Settings);
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
            Out("report dir   : " + cfg.EffectiveErrorReportDir +
                (Directory.Exists(cfg.EffectiveErrorReportDir) ? " (存在)" : " (不存在，首次写报告时自动创建)"));
            Out("harness ver  : " + FormatVer(HarnessVersion.ResolveWindowsAsync(cfg).GetAwaiter().GetResult()) +
                "  (Windows 当前环境主实例)");
            foreach (string distro in registry.Instances
                .Where(d => d.IsWsl && !string.IsNullOrWhiteSpace(d.WslDistro))
                .Select(d => d.WslDistro.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                Out("harness ver  : " + FormatVer(HarnessVersion.ResolveWslAsync(distro).GetAwaiter().GetResult()) +
                    "  (WSL " + distro + ")");
            }
            Out("launcher.json: " + InstanceRegistry.LegacyFilePath);
            Out("instances.json: " + InstanceRegistry.FilePath);
            Out("");
            Out("instances:");
            foreach (InstanceDef def in registry.Instances)
            {
                string home = string.IsNullOrEmpty(def.Home) ? "(默认 ~/.dsh)" : def.Home;
                if (def.IsWsl) home = "wsl:" + (string.IsNullOrEmpty(def.WslHome) ? "~/.dsh" : def.WslHome);
                bool instanceUp = PortTools.ProbeAsync(def.Host, def.Port).GetAwaiter().GetResult();
                string listenerPid = instanceUp
                    ? PortTools.FindListenerPidAsync(def.Port).GetAwaiter().GetResult().ToString()
                    : "-";
                string ver = string.IsNullOrEmpty(def.HarnessVersion)
                    ? "跟随当前环境" : "指定 " + def.HarnessVersion;
                Out("  [" + def.Id + "] " + def.Name +
                    (def.IsWsl ? "  runtime=wsl(" + (string.IsNullOrEmpty(def.WslDistro) ? "?" : def.WslDistro) + ")" : "  runtime=windows") +
                    "  port=" + def.Port +
                    "  home=" + home + "  backend=" + (instanceUp ? "UP" : "DOWN") +
                    "  pid=" + listenerPid + "  harness=" + ver);
            }
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
            string homeDir = null;
            for (int i = 1; i < args.Length; i++)
            {
                if (args[i] == "--port" && i + 1 < args.Length)
                {
                    int p; if (int.TryParse(args[i + 1], out p)) port = p;
                }
                if (args[i] == "--noredirect") noRedirect = true;
                if (args[i] == "--home" && i + 1 < args.Length) homeDir = args[i + 1];
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
            if (!string.IsNullOrEmpty(homeDir)) psi.EnvironmentVariables["DSH_HOME"] = homeDir;
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

                    if (!string.IsNullOrEmpty(homeDir))
                    {
                        string expected = Path.Combine(homeDir, "profiles", "web");
                        if (Directory.Exists(expected))
                            Trace("step: DSH_HOME 自动初始化 OK (profiles/web 已生成)");
                        else
                        {
                            var rfail = PortTools.EnsurePortFreeAsync(cfg.Host, port, child.Id).GetAwaiter().GetResult();
                            Trace("step: stop backend -> " + (rfail.Item1 ? "OK" : "FAIL"));
                            Trace(rfail.Item2);
                            Out("FAIL: DSH_HOME 未自动初始化: " + expected);
                            WriteCliLog(transcript);
                            return 1;
                        }
                    }

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

        /// <summary>--instance &lt;id&gt; start|stop|restart|status [--noredirect]：对指定实例执行无 GUI 操作。</summary>
        private static int Instance(string[] args)
        {
            var transcript = new StringBuilder();
            Action<string> Out = line =>
            {
                transcript.AppendLine(line);
                try { Console.WriteLine(line); } catch { }
            };
            Action<string> Trace = line => { Out(line); WriteCliLog(transcript); };

            string id = args[1];
            string op = args[2].ToLowerInvariant();

            var registry = InstanceRegistry.Load();
            if (!registry.TryGet(id, out InstanceDef def))
            {
                Out("instance not found: " + id);
                WriteCliLog(transcript);
                return 1;
            }

            var im = new InstanceManager(null, registry);
            im.For(id).Log += (s, line) => Trace(line);

            try
            {
                if (op == "status") return InstanceStatus(id, def, im, Out, WriteCliLog, transcript);
                if (op == "start") return InstanceStart(id, im, Out, WriteCliLog, transcript);
                if (op == "stop") return InstanceStop(id, im, Out, WriteCliLog, transcript);
                if (op == "restart") return InstanceRestart(id, im, Out, WriteCliLog, transcript);
                Out("FAIL: unknown instance operation '" + args[2] + "', expected start|stop|restart|status");
                WriteCliLog(transcript);
                return 1;
            }
            finally
            {
                im.DisposeAll();
            }
        }

        private static int InstanceStatus(string id, InstanceDef def, InstanceManager im,
            Action<string> Out, Action<StringBuilder> writeClilog, StringBuilder transcript)
        {
            bool up = PortTools.ProbeAsync(def.Host, def.Port).GetAwaiter().GetResult();
            int listenerPid = up ? PortTools.FindListenerPidAsync(def.Port).GetAwaiter().GetResult() : 0;
            Out("id: " + def.Id);
            Out("name: " + def.Name);
            Out("runtime: " + (def.IsWsl
                ? "wsl(" + (string.IsNullOrWhiteSpace(def.WslDistro) ? "?" : def.WslDistro) + ")"
                : "windows"));
            Out("state: " + (up ? "Running" : "Stopped"));
            Out("port: " + def.Port);
            Out("pid: " + (listenerPid > 0 ? listenerPid.ToString() : "0"));
            Out("url: " + PortTools.Url(def.Host, def.Port));
            Out("home: " + (def.IsWsl
                ? "wsl:" + (string.IsNullOrWhiteSpace(def.WslHome) ? "~/.dsh" : def.WslHome)
                : (string.IsNullOrEmpty(def.Home) ? "(默认 ~/.dsh)" : def.Home)));
            Out("workspace: " + def.Workspace);
            Out("harness: " + (string.IsNullOrEmpty(def.HarnessVersion)
                ? "跟随当前环境" : "指定 " + def.HarnessVersion));
            writeClilog(transcript);
            return 0;
        }

        private static int InstanceStart(string id, InstanceManager im,
            Action<string> Out, Action<StringBuilder> writeClilog, StringBuilder transcript)
        {
            Out("starting instance " + id + " ...");

            // v0.5.0：CLI 启动改为"等到就绪或失败"再返回——
            // 就绪 → 0；失败 → 1 并打印核心层生成的失败报告路径。
            BackendManager mgr = im.For(id);
            var settled = new ManualResetEventSlim(false);
            string readyUrl = null;
            string failKind = null;
            string reportPath = null;

            EventHandler<ReadyEventArgs> onReady = (s, e) =>
            {
                readyUrl = e.Url;
                settled.Set();
            };
            EventHandler<StartFailureContext> onFail = (s, ctx) =>
            {
                failKind = ctx.FailureKind;
                reportPath = ctx.ReportPath;
                settled.Set();
            };
            mgr.Ready += onReady;
            mgr.StartFailed += onFail;

            try
            {
                bool entered = im.StartAsync(id).GetAwaiter().GetResult();
                if (entered && !settled.IsSet)
                {
                    // 就绪循环在后台任务里跑：最多等 ReadyTimeout + 20s 余量
                    settled.Wait(TimeSpan.FromSeconds(new StartOptions().ReadyTimeoutSeconds + 20));
                }

                if (readyUrl != null)
                {
                    Out("started, ready at " + readyUrl);
                    writeClilog(transcript);
                    return 0;
                }
                if (failKind != null)
                {
                    Out("FAIL: 启动失败（" + failKind + "）");
                    Out(string.IsNullOrEmpty(reportPath)
                        ? "report: (未生成，请检查报告目录是否可写)"
                        : "report: " + reportPath);
                    writeClilog(transcript);
                    return 1;
                }
                Out(entered
                    ? "FAIL: 等待就绪超时，未收到就绪或失败事件"
                    : "FAIL: 启动未被受理（实例可能已在运行或正忙）");
                writeClilog(transcript);
                return 1;
            }
            finally
            {
                mgr.Ready -= onReady;
                mgr.StartFailed -= onFail;
                settled.Dispose();
            }
        }

        private static int InstanceStop(string id, InstanceManager im,
            Action<string> Out, Action<StringBuilder> writeClilog, StringBuilder transcript)
        {
            bool ok = im.StopAsync(id, killExternal: true).GetAwaiter().GetResult();
            Out(ok ? "stopped" : "FAIL: 停止失败，请查看上方日志");
            writeClilog(transcript);
            return ok ? 0 : 1;
        }

        private static int InstanceRestart(string id, InstanceManager im,
            Action<string> Out, Action<StringBuilder> writeClilog, StringBuilder transcript)
        {
            bool ok = im.RestartAsync(id).GetAwaiter().GetResult();
            Out(ok ? "restarted" : "FAIL: 重启失败，请查看上方日志");
            writeClilog(transcript);
            return ok ? 0 : 1;
        }
    }
}
