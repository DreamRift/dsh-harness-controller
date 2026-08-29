// ============================================================================
//  DshController — DeepSeek Harness 后端控制面板（Windows）
//
//  功能:
//    ▶ 启动后端   启动 `dsh web`（Harness Web profile），就绪后自动打开浏览器
//    ⏸ 暂停/停止  结束 `dsh web` 进程树（Harness 无内置 pause 命令，会话数据
//                 保留在 $DSH_HOME，重新启动即可继续）
//    🌐 打开界面  一键打开浏览器访问 Harness Web 界面
//
//  编译:  csc /target:winexe /codepage:65001 /r:System.dll /r:System.Core.dll
//         /r:System.Drawing.dll /r:System.Windows.Forms.dll DshController.cs
//  无第三方依赖，仅需 .NET Framework 4.x（Windows 10/11 自带）。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DshController
{
    // ------------------------------------------------------------------------
    // 入口：GUI 模式 + 两个无界面自检模式（便于部署验证）
    // ------------------------------------------------------------------------
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            // 全局异常钩子：异步回调/后台线程的未处理异常也留下 crash.log
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                try
                {
                    File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.log"),
                        (e.ExceptionObject ?? new Exception("unknown")).ToString(), Encoding.UTF8);
                }
                catch { }
            };

            try
            {
                if (args.Length > 0)
                {
                    string a = args[0].ToLowerInvariant();
                    if (a == "--check") return Cli.Check(args);
                    if (a == "--spawn-test") return Cli.SpawnTest(args, useRealDsh: true);
                    if (a == "--spawn-test-node") return Cli.SpawnTest(args, useRealDsh: false);
                    if (a == "--version")
                    {
                        Console.WriteLine("DshController 1.0.0");
                        return 0;
                    }
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += (s, e) =>
                    MessageBox.Show(e.Exception.ToString(), "DshController 异常", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Application.Run(new MainForm());
                return 0;
            }
            catch (Exception ex)
            {
                try
                {
                    File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.log"),
                        ex.ToString(), Encoding.UTF8);
                }
                catch { }
                MessageBox.Show(ex.ToString(), "DshController 启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }
    }

    // ------------------------------------------------------------------------
    // 配置（launcher.json，与 exe 同目录；手写极简 JSON 避免外部依赖）
    // ------------------------------------------------------------------------
    internal sealed class Config
    {
        public string Host = "127.0.0.1";
        public int Port = 3080;
        public string Workspace = "";
        public string DshCommand = "";      // 手动指定的 dsh 启动命令（可选）
        public bool AutoOpenBrowser = true; // 启动就绪后自动打开界面
        public bool StopOnExit = true;      // 退出程序时停止由本程序启动的后端

        public static string FilePath
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "launcher.json"); }
        }

        public static Config Load()
        {
            var c = new Config();
            try
            {
                if (File.Exists(FilePath))
                {
                    string s = File.ReadAllText(FilePath, Encoding.UTF8);
                    foreach (string line in s.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        string t = line.Trim().TrimEnd(',');
                        int i = t.IndexOf(':');
                        if (i <= 0) continue;
                        string key = t.Substring(0, i).Trim().Trim('"');
                        string val = t.Substring(i + 1).Trim().Trim('"');
                        // Save() 写入时会把 \ 和 " 转义为 \\ 与 \"，读取时必须反转义，
                        // 否则每保存一次反斜杠数量翻倍（历史 launcher.json 已被污染成
                        // 数百个连续反斜杠，这里用正则把它们一次性收敛回单个）
                        val = Regex.Replace(val, @"\\+", @"\");
                        val = val.Replace("\\\"", "\"");
                        switch (key)
                        {
                            case "host": c.Host = string.IsNullOrEmpty(val) ? c.Host : val; break;
                            case "port": { int p; if (int.TryParse(val, out p)) c.Port = p; } break;
                            case "workspace": c.Workspace = val; break;
                            case "dshCommand": c.DshCommand = val; break;
                            case "autoOpenBrowser": c.AutoOpenBrowser = ParseBool(val, c.AutoOpenBrowser); break;
                            case "stopOnExit": c.StopOnExit = ParseBool(val, c.StopOnExit); break;
                        }
                    }
                }
            }
            catch { /* 配置损坏时使用默认值 */ }
            if (string.IsNullOrEmpty(c.Workspace))
                c.Workspace = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            return c;
        }

        private static bool ParseBool(string v, bool def)
        {
            bool b;
            return bool.TryParse(v, out b) ? b : def;
        }

        public void Save()
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("{");
                sb.AppendLine("  \"host\": \"" + JsonEsc(Host) + "\",");
                sb.AppendLine("  \"port\": " + Port + ",");
                sb.AppendLine("  \"workspace\": \"" + JsonEsc(Workspace) + "\",");
                sb.AppendLine("  \"dshCommand\": \"" + JsonEsc(DshCommand) + "\",");
                sb.AppendLine("  \"autoOpenBrowser\": " + (AutoOpenBrowser ? "true" : "false") + ",");
                sb.AppendLine("  \"stopOnExit\": " + (StopOnExit ? "true" : "false"));
                sb.AppendLine("}");
                File.WriteAllText(FilePath, sb.ToString(), Encoding.UTF8);
            }
            catch { /* 保存失败不影响运行 */ }
        }

        private static string JsonEsc(string s)
        {
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }

    // ------------------------------------------------------------------------
    // dsh 命令解析（对应官方文档：dsh web 即 --profile web 的别名）
    // ------------------------------------------------------------------------
    internal sealed class DshCommand
    {
        public string Kind;      // "cmd"（npm 的 .cmd shim）或 "node"（node + bin.js）
        public string Path1;     // cmd shim 路径，或 node.exe 路径
        public string Path2;     // node 模式下：@deepseek-ai/dsh/lib/bin.js

        public string Describe()
        {
            return Kind == "cmd" ? Path1 : Path1 + " \"" + Path2 + "\"";
        }
    }

    internal static class DshResolver
    {
        /// <summary>依次尝试：配置指定 → %APPDATA%\npm\dsh.cmd → PATH 上的 dsh → node + bin.js。</summary>
        public static DshCommand Resolve(Config cfg)
        {
            // 1. 用户配置里显式指定的命令
            if (!string.IsNullOrEmpty(cfg.DshCommand) && File.Exists(cfg.DshCommand))
                return new DshCommand { Kind = "cmd", Path1 = cfg.DshCommand };

            // 2. npm 全局 shim（本机安装 @deepseek-ai/dsh 后必然存在）
            string npmDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm");
            string shim = Path.Combine(npmDir, "dsh.cmd");
            if (File.Exists(shim)) return new DshCommand { Kind = "cmd", Path1 = shim };

            // 3. PATH 上的 dsh（纯文件扫描，不依赖管道）
            string onPath = FindOnPath("dsh.cmd");
            if (onPath != null) return new DshCommand { Kind = "cmd", Path1 = onPath };
            onPath = FindOnPath("dsh.exe");
            if (onPath != null) return new DshCommand { Kind = "cmd", Path1 = onPath };

            // 4. 直接调用 node + dsh 包入口
            string node = FindNode();
            string bin = Path.Combine(npmDir, "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js");
            if (!string.IsNullOrEmpty(node) && File.Exists(bin))
                return new DshCommand { Kind = "node", Path1 = node, Path2 = bin };

            return null;
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

        /// <summary>用 cmd 的 where 在 PATH 中查找可执行文件（.exe/.cmd）。带超时保护。</summary>
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

        /// <summary>查找 node.exe：先试常见安装位置与 PATH 扫描（不依赖管道），最后用 where 兜底。</summary>
        public static string FindNode()
        {
            string npmNode = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "node.exe");
            if (File.Exists(npmNode)) return npmNode;
            string pfNode = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "node.exe");
            if (File.Exists(pfNode)) return pfNode;
            try
            {
                string pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
                foreach (string dir in pathEnv.Split(';'))
                {
                    if (string.IsNullOrEmpty(dir)) continue;
                    string cand = Path.Combine(dir.Trim('"'), "node.exe");
                    if (File.Exists(cand)) return cand;
                }
            }
            catch { }
            return WhereExe("node");
        }
    }

    // ------------------------------------------------------------------------
    // 后端进程工具：HTTP 探测 / netstat 找监听进程 / taskkill 杀进程树
    // ------------------------------------------------------------------------
    internal static class Backend
    {
        public static string Url(string host, int port)
        {
            return "http://" + host + ":" + port + "/";
        }

        /// <summary>探测后端是否可访问（TCP 握手成功即认为在线；不依赖 HTTP 语义与系统代理）。</summary>
        public static bool Probe(string host, int port)
        {
            try
            {
                using (var client = new System.Net.Sockets.TcpClient())
                {
                    IAsyncResult ar = client.BeginConnect(host, port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(1200)) return false;
                    client.EndConnect(ar);
                    return client.Connected;
                }
            }
            catch { return false; }
        }

        /// <summary>通过 netstat 找出监听指定端口的进程 PID（用于停止外部启动的后端）。</summary>
        public static int FindListenerPid(int port)
        {
            try
            {
                var psi = new ProcessStartInfo("netstat.exe", "-ano -p TCP")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    StandardOutputEncoding = Encoding.UTF8
                };
                using (Process p = Process.Start(psi))
                {
                    // 异步收集输出（避免大输出写满管道缓冲导致死锁），带超时放弃
                    var outTask = Task.Run(() => p.StandardOutput.ReadToEnd());
                    if (!p.WaitForExit(3000))
                    {
                        try { p.Kill(); } catch { }
                        return 0;
                    }
                    string outp = outTask.Result;
                    Regex rx = new Regex(":" + port + "\\s+[0-9.:\\[\\]]+\\s+LISTENING\\s+(\\d+)", RegexOptions.IgnoreCase);
                    foreach (string line in outp.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        Match m = rx.Match(line);
                        if (m.Success)
                        {
                            int pid;
                            if (int.TryParse(m.Groups[1].Value, out pid)) return pid;
                        }
                    }
                }
            }
            catch { }
            return 0;
        }

        /// <summary>结束进程树。策略：先 .NET Process.Kill() 强杀主进程（可靠、不依赖外部工具），
        /// 再 taskkill /T /F 兜底清掉整棵进程树（真实环境下 dsh 有 cmd→node→worker 多层子进程）。
        /// 返回主进程是否已消失。</summary>
        public static bool KillTree(int pid, out string message)
        {
            message = "";
            bool alive = true;

            // 1) 直接终止主进程
            try
            {
                using (Process p = Process.GetProcessById(pid))
                {
                    p.Kill();
                    alive = !p.WaitForExit(3000);
                }
            }
            catch (Exception ex)
            {
                message = ex.Message;
            }

            // 2) taskkill /T /F 兜底杀树（清子进程；失败不致命，主进程可能已被杀）
            try
            {
                var psi = new ProcessStartInfo("taskkill.exe", "/PID " + pid + " /T /F")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using (Process t = Process.Start(psi))
                {
                    if (!t.WaitForExit(10000))
                    {
                        try { t.Kill(); } catch { }
                    }
                }
            }
            catch { }

            // 3) 验证主进程是否已消失
            try
            {
                Process.GetProcessById(pid);
                alive = true;
            }
            catch { alive = false; }
            if (!alive && string.IsNullOrEmpty(message)) message = "ok";
            return !alive;
        }

        /// <summary>等待端口变为指定状态，返回实际等待秒数。</summary>
        public static double WaitForPort(string host, int port, bool up, int timeoutSeconds)
        {
            DateTime started = DateTime.UtcNow;
            while (DateTime.UtcNow - started < TimeSpan.FromSeconds(timeoutSeconds))
            {
                if (Probe(host, port) == up) break;
                Thread.Sleep(500);
            }
            return (DateTime.UtcNow - started).TotalSeconds;
        }

        /// <summary>确保端口释放：先杀已知启动器进程树，若端口仍被监听（如 dsh 派生出的脱离
        /// node 进程），再定位监听进程并一并结束。返回最终端口是否关闭。</summary>
        public static bool EnsurePortFree(string host, int port, int knownPid, out string log)
        {
            var sb = new StringBuilder();
            bool closed = !Probe(host, port);
            if (knownPid > 0 && !closed)
            {
                string m;
                KillTree(knownPid, out m);
                sb.AppendLine("kill tree(" + knownPid + "): " + m);
                closed = !Probe(host, port);
            }
            if (!closed)
            {
                int listener = FindListenerPid(port);
                if (listener > 0 && listener != knownPid)
                {
                    string m;
                    KillTree(listener, out m);
                    sb.AppendLine("kill listener(" + listener + "): " + m);
                }
                WaitForPort(host, port, false, 15);
                closed = !Probe(host, port);
            }
            sb.AppendLine("port closed: " + (closed ? "YES" : "NO"));
            log = sb.ToString().TrimEnd();
            return closed;
        }
    }

    // ------------------------------------------------------------------------
    // 无界面 CLI 模式（GUI-subsystem exe 需要 AttachConsole 才能在终端看到输出；
    // 同时把完整输出写到 exe 旁的 cli.log，便于脚本读取）
    // ------------------------------------------------------------------------
    internal static class Native
    {
        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool AttachConsole(int dwProcessId);

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        public static extern IntPtr GetStdHandle(int nStdHandle);

        public const int STD_OUTPUT_HANDLE = -11;
        public const int ATTACH_PARENT_PROCESS = -1;
    }

    internal static class Cli
    {
        /// <summary>在 CLI 模式下把 stdout 接到父进程控制台（从 cmd/终端运行时可显示）。</summary>
        private static void AttachConsoleOutput()
        {
            try
            {
                if (Native.AttachConsole(Native.ATTACH_PARENT_PROCESS))
                {
                    var handle = new Microsoft.Win32.SafeHandles.SafeFileHandle(
                        Native.GetStdHandle(Native.STD_OUTPUT_HANDLE), false);
                    Console.SetOut(new StreamWriter(new FileStream(handle, FileAccess.Write)) { AutoFlush = true });
                    Console.SetError(new StreamWriter(new FileStream(handle, FileAccess.Write)) { AutoFlush = true });
                }
            }
            catch { }
        }

        /// <summary>把一行输出同时写入控制台与 cli.log 转录。</summary>
        private static void WriteCliLog(StringBuilder transcript)
        {
            try
            {
                File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cli.log"),
                    transcript.ToString(), Encoding.UTF8);
            }
            catch { }
        }

        /// <summary>--check：打印 dsh 解析结果与端口状态，用于部署自检。</summary>
        public static int Check(string[] args)
        {
            AttachConsoleOutput();
            var transcript = new StringBuilder();
            Action<string> Out = line => { transcript.AppendLine(line); Console.WriteLine(line); };

            Config cfg = Config.Load();
            int port = cfg.Port;
            for (int i = 1; i < args.Length; i++)
            {
                if (args[i] == "--port" && i + 1 < args.Length)
                {
                    int p; if (int.TryParse(args[i + 1], out p)) port = p;
                }
            }
            DshCommand dsh = DshResolver.Resolve(cfg);
            bool up = Backend.Probe(cfg.Host, port);
            Out("dsh command  : " + (dsh == null ? "(NOT FOUND)" : dsh.Describe()));
            Out("host         : " + cfg.Host);
            Out("port         : " + port);
            Out("workspace    : " + cfg.Workspace);
            Out("backend      : " + (up ? "UP" : "DOWN"));
            if (up) Out("listener pid : " + Backend.FindListenerPid(port));
            Out("launcher.json: " + Config.FilePath);
            WriteCliLog(transcript);
            return dsh == null ? 2 : 0;
        }

        /// <summary>--spawn-test [--port N] [--noredirect]：真实启动 → 等待就绪 → 杀进程树 → 验证端口关闭。
        /// useRealDsh=false 时改用一个微型 node HTTP 服务（test-server.js）只验证进程管线。
        /// --noredirect 在受限环境（如无命名管道的沙箱）下跳过输出重定向。</summary>
        public static int SpawnTest(string[] args, bool useRealDsh)
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
                DshCommand dsh = DshResolver.Resolve(cfg);
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
                string js = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "test-server.js");
                psi = new ProcessStartInfo(node, "\"" + js + "\" " + port);
                desc = "node test-server";
            }

            if (Backend.Probe(cfg.Host, port))
            {
                Out("FAIL: port " + port + " already in use by pid " + Backend.FindListenerPid(port));
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

                    // 等待就绪（最长 180 秒）
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
                        if (Backend.Probe(cfg.Host, port)) { up = true; break; }
                        Thread.Sleep(700);
                    }
                    if (!up)
                    {
                        Trace("step: backend did not become ready within 180s");
                        WriteCliLog(transcript);
                        return 1;
                    }
                    Trace("READY: " + Backend.Url(cfg.Host, port) + " after ~" + (int)(DateTime.UtcNow - started).TotalSeconds + "s");

                    // 杀进程树并确保端口释放
                    string msg;
                    bool ok = Backend.EnsurePortFree(cfg.Host, port, child.Id, out msg);
                    Trace("step: stop backend -> " + (ok ? "OK" : "FAIL"));
                    Trace(msg);
                    WriteCliLog(transcript);
                    return ok ? 0 : 1;
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

    // ------------------------------------------------------------------------
    // 主窗体
    // ------------------------------------------------------------------------
    internal sealed class MainForm : Form
    {
        private readonly Config _cfg;
        private Process _child;                 // 由本程序启动的 dsh 进程（仅在 Start 成功后赋值）
        private int _childPid;                  // 启动成功时记录的 PID（避免事后访问 _child.Id 抛异常）
        private readonly object _lock = new object();
        private volatile bool _starting;
        private bool _probing;
        private bool _closing;
        private string _announcedUrl = "";      // 从 dsh 输出中捕获的公告 URL
        private string _lastState = "";

        private readonly Label _lblStatus = new Label();
        private readonly Label _lblPid = new Label();
        private readonly Label _lblUrl = new Label();
        private readonly Button _btnStart = new Button();
        private readonly Button _btnStop = new Button();
        private readonly Button _btnOpen = new Button();
        private readonly TextBox _txtHost = new TextBox();
        private readonly TextBox _txtPort = new TextBox();
        private readonly TextBox _txtWorkspace = new TextBox();
        private readonly Button _btnBrowse = new Button();
        private readonly CheckBox _chkAutoOpen = new CheckBox();
        private readonly CheckBox _chkStopOnExit = new CheckBox();
        private readonly TextBox _txtLog = new TextBox();
        private readonly System.Windows.Forms.Timer _timer = new System.Windows.Forms.Timer();
        private readonly Label _lblFooter = new Label();

        public MainForm()
        {
            _cfg = Config.Load();
            BuildUi();
            _timer.Interval = 1000;
            _timer.Tick += async (s, e) => await RefreshStateAsync();
            _timer.Start();
            AppendLog("DshController 已启动。dsh 命令: " + DescribeDsh());
            AppendLog("后端界面: " + Backend.Url(_cfg.Host, _cfg.Port));
            RefreshStateUi();
        }

        // ---------------- UI 构建 ----------------
        private void BuildUi()
        {
            Text = "DSH Harness 控制器";
            Font = new Font("Microsoft YaHei UI", 9F);
            ClientSize = new Size(620, 620);
            MinimumSize = new Size(560, 480);
            StartPosition = FormStartPosition.CenterScreen;
            FormClosing += OnFormClosing;
            try { Icon = MakeIcon(); } catch { }

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                Padding = new Padding(10),
                BackColor = Color.White
            };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 190));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 210));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Controls.Add(root);

            // ---------- 第 1 行：状态 + 控制按钮 ----------
            var grpTop = new GroupBox { Text = "后端控制", Dock = DockStyle.Fill };
            var top = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
            top.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
            top.RowStyles.Add(new RowStyle(SizeType.Percent, 45));

            // 状态区
            var statusPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(6, 2, 6, 2) };
            statusPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
            statusPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            AddRow(statusPanel, "状态：", _lblStatus);
            AddRow(statusPanel, "进程 PID：", _lblPid);
            AddRow(statusPanel, "界面地址：", _lblUrl);
            statusPanel.RowCount = 3;
            _lblStatus.AutoSize = true; _lblStatus.Font = new Font(Font, FontStyle.Bold);
            _lblPid.AutoSize = true;
            _lblUrl.AutoSize = true;
            top.Controls.Add(statusPanel, 0, 0);

            // 按钮区
            var btnPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, Padding = new Padding(6, 6, 6, 6) };
            btnPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.3f));
            btnPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.3f));
            btnPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.4f));
            StyleButton(_btnStart, "▶ 启动后端", "启动 dsh web 后端；就绪后自动打开浏览器界面");
            StyleButton(_btnStop, "⏸ 暂停/停止", "结束 dsh web 后端进程树（会话数据保留，重启可继续）");
            StyleButton(_btnOpen, "🌐 打开界面", "一键在默认浏览器打开 Harness 界面");
            _btnStart.Click += async (s, e) => await StartBackendAsync();
            _btnStop.Click += (s, e) => StopBackend();
            _btnOpen.Click += (s, e) => OpenBrowser();
            btnPanel.Controls.Add(_btnStart, 0, 0);
            btnPanel.Controls.Add(_btnStop, 1, 0);
            btnPanel.Controls.Add(_btnOpen, 2, 0);
            top.Controls.Add(btnPanel, 0, 1);
            grpTop.Controls.Add(top);
            root.Controls.Add(grpTop, 0, 0);

            // ---------- 第 2 行：设置 ----------
            var grpCfg = new GroupBox { Text = "设置（保存在 launcher.json）", Dock = DockStyle.Fill };
            var cfgPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(6, 4, 6, 4) };
            cfgPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
            cfgPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            AddRow(cfgPanel, "主机：", _txtHost);
            AddRow(cfgPanel, "端口：", _txtPort);
            AddRow(cfgPanel, "工作目录：", _txtWorkspace);
            cfgPanel.RowCount = 3;
            _txtHost.Text = _cfg.Host;
            _txtPort.Text = _cfg.Port.ToString();
            _txtWorkspace.Text = _cfg.Workspace;
            _txtHost.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            _txtPort.Anchor = AnchorStyles.Left;
            _txtPort.Width = 100;
            _txtWorkspace.Anchor = AnchorStyles.Left | AnchorStyles.Right;

            var ws = new TableLayoutPanel { Anchor = AnchorStyles.Left | AnchorStyles.Right, ColumnCount = 2 };
            ws.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            ws.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));
            _txtWorkspace.Dock = DockStyle.Fill;
            _btnBrowse.Text = "浏览…";
            _btnBrowse.Dock = DockStyle.Fill;
            _btnBrowse.Click += (s, e) =>
            {
                using (var dlg = new FolderBrowserDialog { Description = "选择 dsh 工作目录（将作为默认 workspace 根目录）", SelectedPath = _txtWorkspace.Text })
                {
                    if (dlg.ShowDialog(this) == DialogResult.OK) _txtWorkspace.Text = dlg.SelectedPath;
                }
            };
            ws.Controls.Add(_txtWorkspace, 0, 0);
            ws.Controls.Add(_btnBrowse, 1, 0);
            cfgPanel.Controls.Add(ws, 1, 2);

            var opts = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(8, 2, 8, 2) };
            _chkAutoOpen.Text = "启动后自动打开浏览器界面";
            _chkAutoOpen.Checked = _cfg.AutoOpenBrowser;
            _chkAutoOpen.AutoSize = true;
            _chkStopOnExit.Text = "退出时停止由本程序启动的后端";
            _chkStopOnExit.Checked = _cfg.StopOnExit;
            _chkStopOnExit.AutoSize = true;
            opts.Controls.Add(_chkAutoOpen);
            opts.Controls.Add(_chkStopOnExit);
            cfgPanel.Controls.Add(opts, 0, 3);
            cfgPanel.SetColumnSpan(opts, 2);
            cfgPanel.RowCount = 4;
            cfgPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            grpCfg.Controls.Add(cfgPanel);
            root.Controls.Add(grpCfg, 0, 1);

            // ---------- 第 3 行：日志 ----------
            var grpLog = new GroupBox { Text = "后端输出日志", Dock = DockStyle.Fill };
            var logPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
            logPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            logPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            _txtLog.Multiline = true;
            _txtLog.ReadOnly = true;
            _txtLog.Dock = DockStyle.Fill;
            _txtLog.Font = new Font("Consolas", 9F);
            _txtLog.BackColor = Color.FromArgb(252, 252, 252);
            _txtLog.ScrollBars = ScrollBars.Both;
            _txtLog.WordWrap = false;
            var btnClear = new Button { Text = "清空日志", Dock = DockStyle.Fill };
            btnClear.Click += (s, e) => _txtLog.Clear();
            logPanel.Controls.Add(_txtLog, 0, 0);
            logPanel.Controls.Add(btnClear, 0, 1);
            grpLog.Controls.Add(logPanel);
            root.Controls.Add(grpLog, 0, 2);

            // ---------- 底栏 ----------
            _lblFooter.Dock = DockStyle.Fill;
            _lblFooter.ForeColor = Color.Gray;
            _lblFooter.TextAlign = ContentAlignment.MiddleLeft;
            _lblFooter.Padding = new Padding(10, 2, 10, 2);
            root.Controls.Add(_lblFooter, 0, 3);
            root.RowCount = 4;
            root.RowStyles.Clear();
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 190));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 210));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        }

        private static void AddRow(TableLayoutPanel panel, string text, Control ctrl)
        {
            var lbl = new Label { Text = text, AutoSize = true, Anchor = AnchorStyles.Left };
            panel.Controls.Add(lbl, 0, panel.RowCount);
            panel.Controls.Add(ctrl, 1, panel.RowCount);
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        }

        private static void StyleButton(Button b, string text, string tip)
        {
            b.Text = text;
            b.Dock = DockStyle.Fill;
            b.Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold);
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderColor = Color.FromArgb(180, 180, 180);
            b.FlatAppearance.BorderSize = 1;
            b.BackColor = Color.White;
            b.UseVisualStyleBackColor = true;
            b.Margin = new Padding(6);
            if (!string.IsNullOrEmpty(tip)) b.SetToolTip(tip);
        }

        private static Icon MakeIcon()
        {
            using (var bmp = new Bitmap(32, 32))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Transparent);
                    using (var brush = new SolidBrush(Color.FromArgb(0, 122, 204)))
                    {
                        g.FillEllipse(brush, 2, 2, 28, 28);
                    }
                    using (var pen = new Pen(Color.White, 3))
                    {
                        g.DrawLine(pen, 11, 22, 11, 10);
                        g.DrawLine(pen, 11, 10, 22, 16);
                        g.DrawLine(pen, 22, 16, 11, 22);
                        g.DrawLine(pen, 21, 22, 21, 10);
                        g.DrawLine(pen, 21, 10, 25, 10);
                        g.DrawLine(pen, 25, 10, 25, 22);
                        g.DrawLine(pen, 25, 22, 21, 22);
                    }
                }
                return Icon.FromHandle(bmp.GetHicon());
            }
        }

        // ---------------- 状态刷新 ----------------

        /// <summary>安全判断子进程是否存活。对"从未启动/句柄已释放"的 Process 对象
        /// 调用 HasExited 会抛 InvalidOperationException（"没有与此对象关联的进程"），
        /// 这里统一吞掉并视为不在运行，避免状态刷新定时器崩溃。</summary>
        private static bool IsChildAlive(Process p)
        {
            if (p == null) return false;
            try { return !p.HasExited; }
            catch { return false; }
        }

        private async Task RefreshStateAsync()
        {
            if (_probing || _closing) return;
            _probing = true;
            try
            {
                bool up = await Task.Run(() => Backend.Probe(_cfg.Host, _cfg.Port));
                Ui(() => ApplyState(up));
            }
            finally { _probing = false; }
        }

        private void ApplyState(bool up)
        {
            // 快照 + 安全判断：_child 可能被 Exited 回调线程置空，
            // 且 HasExited 对未成功启动的进程会抛异常
            Process child;
            lock (_lock) { child = _child; }
            bool mine = IsChildAlive(child);
            string state;
            Color color;
            if (_starting) { state = "● 启动中…"; color = Color.FromArgb(230, 126, 34); }
            else if (up) { state = mine ? "● 运行中（本程序启动）" : "● 运行中（外部进程）"; color = Color.FromArgb(39, 174, 96); }
            else { state = "● 已停止"; color = Color.FromArgb(120, 120, 120); }
            if (state != _lastState)
            {
                _lastState = state;
                _lblStatus.Text = state;
                _lblStatus.ForeColor = color;
            }
            int myPid;
            lock (_lock) { myPid = _childPid; }
            _lblPid.Text = mine ? myPid.ToString() : (up ? ("外部 " + Backend.FindListenerPid(_cfg.Port)) : "—");
            _lblUrl.Text = Backend.Url(_cfg.Host, _cfg.Port);
            _btnStart.Enabled = !_starting && !up;
            _btnStop.Enabled = !_starting && (mine || up);
            _btnOpen.Enabled = true;
            _lblFooter.Text = "dsh 命令: " + DescribeDsh() +
                (string.IsNullOrEmpty(_announcedUrl) ? "" : "    公告 URL: " + _announcedUrl);
        }

        private void RefreshStateUi()
        {
            ApplyState(Backend.Probe(_cfg.Host, _cfg.Port));
        }

        // ---------------- 启动 ----------------
        private async Task StartBackendAsync()
        {
            if (_starting) return;
            if (!TryReadSettings()) return;

            if (Backend.Probe(_cfg.Host, _cfg.Port))
            {
                AppendLog("后端已在运行（" + Backend.Url(_cfg.Host, _cfg.Port) + "），不再重复启动，直接打开界面。");
                OpenBrowser();
                return;
            }

            DshCommand dsh = DshResolver.Resolve(_cfg);
            if (dsh == null)
            {
                MessageBox.Show(this, "未找到 dsh 命令。请确认已安装 @deepseek-ai/dsh，\n或在 launcher.json 的 dshCommand 中填写完整路径。", "启动失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            _starting = true;
            ApplyState(false);
            try
            {
                string ws = _cfg.Workspace;
                if (!Directory.Exists(ws))
                {
                    try { Directory.CreateDirectory(ws); }
                    catch (Exception ex)
                    {
                        AppendLog("无法创建工作目录 " + ws + "：" + ex.Message);
                        ws = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    }
                }

                var psi = new ProcessStartInfo
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                    WorkingDirectory = ws
                };
                if (dsh.Kind == "cmd")
                {
                    psi.FileName = "cmd.exe";
                    psi.Arguments = "/d /s /c \"\"" + dsh.Path1 + "\" web --host " + _cfg.Host + " --port " + _cfg.Port + "\"";
                }
                else
                {
                    psi.FileName = dsh.Path1;
                    psi.Arguments = "\"" + dsh.Path2 + "\" web --host " + _cfg.Host + " --port " + _cfg.Port;
                }

                var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
                p.OutputDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data)) OnChildOutput(e.Data, isError: false); };
                p.ErrorDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data)) OnChildOutput(e.Data, isError: true); };
                p.Exited += (s, e) => OnChildExited();

                // 注意：_child 只在 Start 成功之后才赋值。
                // 若提前赋值，Start() 抛异常时会留下一个"从未启动"的 Process 对象，
                // 之后定时器对它调用 HasExited 会抛"没有与此对象关联的进程"。
                try
                {
                    if (!p.Start())
                    {
                        AppendLog("启动 dsh 失败：进程未能创建。");
                        return;
                    }
                }
                catch (Exception ex)
                {
                    AppendLog("启动 dsh 失败：" + ex.Message);
                    if (ex is System.ComponentModel.Win32Exception)
                        AppendLog("（提示：从服务/非交互环境启动时，请检查工作目录是否可访问、cmd.exe 是否在 PATH 中。）");
                    return;
                }
                lock (_lock) { _child = p; _childPid = p.Id; }
                AppendLog("已启动 dsh web（PID " + p.Id + "，工作目录: " + ws + "）");
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();

                // 后台等待就绪
                await Task.Run(() => WaitReadyLoop(p));
            }
            finally
            {
                _starting = false;
                ApplyState(Backend.Probe(_cfg.Host, _cfg.Port));
            }
        }

        private void WaitReadyLoop(Process p)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(180);
            while (DateTime.UtcNow < deadline)
            {
                if (_closing) return;
                if (!IsChildAlive(p)) { OnChildExited(); return; }
                if (Backend.Probe(_cfg.Host, _cfg.Port))
                {
                    string url = Backend.Url(_cfg.Host, _cfg.Port);
                    AppendLog("后端已就绪: " + url);
                    Ui(() =>
                    {
                        _announcedUrl = url;
                        ApplyState(true);
                        if (_chkAutoOpen.Checked) OpenBrowser();
                    });
                    return;
                }
                Thread.Sleep(800);
            }
            AppendLog("等待后端就绪超时（180 秒）。请查看上方日志输出。");
        }

        // ---------------- 停止 / 暂停 ----------------
        private void StopBackend()
        {
            if (_starting) return;

            Process mine = null;
            int minePid = 0;
            lock (_lock) { mine = _child; minePid = _childPid; }

            if (mine != null && IsChildAlive(mine))
            {
                AppendLog("正在停止本程序启动的后端（PID " + minePid + "）…");
                string log;
                bool ok = Backend.EnsurePortFree(_cfg.Host, _cfg.Port, minePid, out log);
                AppendLog("停止后端: " + (ok ? "成功，端口已释放。" : "端口未能释放！"));
                foreach (string l in log.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    AppendLog("  " + l);
                try { mine.WaitForExit(8000); } catch { }
                lock (_lock) { _child = null; _childPid = 0; }
            }
            else if (Backend.Probe(_cfg.Host, _cfg.Port))
            {
                int pid = Backend.FindListenerPid(_cfg.Port);
                if (pid <= 0)
                {
                    AppendLog("检测到后端在线，但无法定位监听进程。");
                    return;
                }
                var r = MessageBox.Show(this,
                    "检测到后端由外部进程 PID " + pid + " 提供。\n\n是否结束该进程及其子进程来“暂停”后端？",
                    "确认停止外部后端",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (r != DialogResult.Yes) { AppendLog("已取消停止外部进程。"); return; }
                AppendLog("正在停止外部后端（PID " + pid + "）…");
                string log;
                bool ok = Backend.EnsurePortFree(_cfg.Host, _cfg.Port, pid, out log);
                AppendLog("停止后端: " + (ok ? "成功，端口已释放。" : "端口未能释放！"));
                foreach (string l in log.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    AppendLog("  " + l);
            }
            else
            {
                AppendLog("后端当前未运行，无需停止。");
            }

            bool down = !Backend.Probe(_cfg.Host, _cfg.Port);
            AppendLog(down ? "后端已停止，端口已释放。" : "端口仍在监听，后端可能未完全退出。");
            Ui(() => ApplyState(!down));
        }

        // ---------------- 打开界面 ----------------
        private void OpenBrowser()
        {
            string url = Backend.Url(_cfg.Host, _cfg.Port);
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                AppendLog("已在默认浏览器打开: " + url);
            }
            catch (Exception ex)
            {
                AppendLog("打开浏览器失败: " + ex.Message);
            }
        }

        // ---------------- 子进程事件 ----------------
        private void OnChildOutput(string line, bool isError)
        {
            // 捕获 dsh 打印的公告 URL（形如 "dsh web: http://..."）
            Match m = Regex.Match(line, @"(https?://[^\s]+)");
            if (m.Success)
            {
                string u = m.Groups[1].Value;
                if (u.StartsWith("http://" + _cfg.Host + ":" + _cfg.Port, StringComparison.OrdinalIgnoreCase))
                    _announcedUrl = u;
            }
            AppendLog((isError ? "[err] " : "      ") + line);
        }

        private void OnChildExited()
        {
            Process p = null;
            lock (_lock)
            {
                if (_child != null) { p = _child; }
            }
            if (p == null) return;
            int code = -1;
            try { if (p.HasExited) code = p.ExitCode; } catch { }
            AppendLog("dsh 进程已退出，退出码 " + code + "。");
            lock (_lock) { _child = null; _childPid = 0; }
            Ui(() =>
            {
                if (!_starting) ApplyState(Backend.Probe(_cfg.Host, _cfg.Port));
            });
        }

        // ---------------- 杂项 ----------------
        private string DescribeDsh()
        {
            DshCommand d = DshResolver.Resolve(_cfg);
            return d == null ? "(未找到)" : d.Describe();
        }

        private bool TryReadSettings()
        {
            int port;
            if (!int.TryParse(_txtPort.Text.Trim(), out port) || port < 1 || port > 65535)
            {
                MessageBox.Show(this, "端口必须是 1–65535 的整数。", "设置无效", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            string host = _txtHost.Text.Trim();
            if (string.IsNullOrEmpty(host))
            {
                MessageBox.Show(this, "主机不能为空。", "设置无效", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            _cfg.Host = host;
            _cfg.Port = port;
            _cfg.Workspace = _txtWorkspace.Text.Trim();
            _cfg.AutoOpenBrowser = _chkAutoOpen.Checked;
            _cfg.StopOnExit = _chkStopOnExit.Checked;
            return true;
        }

        private void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            _closing = true;
            _timer.Stop();
            TryReadSettings();
            _cfg.Save();

            Process mine = null;
            int minePid = 0;
            lock (_lock) { mine = _child; minePid = _childPid; }
            if (mine != null && IsChildAlive(mine) && _cfg.StopOnExit)
            {
                string log;
                Backend.EnsurePortFree(_cfg.Host, _cfg.Port, minePid, out log);
                try { mine.WaitForExit(8000); } catch { }
                lock (_lock) { _child = null; _childPid = 0; }
            }
        }

        private void AppendLog(string line)
        {
            string text = DateTime.Now.ToString("HH:mm:ss") + "  " + line + Environment.NewLine;
            Ui(() =>
            {
                _txtLog.AppendText(text);
                if (_txtLog.TextLength > 200000)
                {
                    _txtLog.Text = _txtLog.Text.Substring(_txtLog.TextLength - 150000);
                }
                _txtLog.SelectionStart = _txtLog.TextLength;
                _txtLog.ScrollToCaret();
            });
        }

        private void Ui(Action a)
        {
            if (IsDisposed || _closing) return;
            if (InvokeRequired) BeginInvoke(a);
            else a();
        }
    }

    internal static class ControlExtensions
    {
        public static void SetToolTip(this Control ctrl, string tip)
        {
            var tt = new ToolTip();
            tt.SetToolTip(ctrl, tip);
        }
    }
}
