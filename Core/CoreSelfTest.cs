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
//    6. 外部实例直接重启   → 断言管理器初始 Stopped 时仍可停止外部并启动本程序后端
//
//  端口使用 3185+（避开 3080 用户实例与常用测试端口）；退出码 0=全部通过。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
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
            string miRoot = Path.Combine(Path.GetTempPath(), "dsh-mi-test");
            try { if (Directory.Exists(miRoot)) Directory.Delete(miRoot, true); } catch { }
            try { Directory.CreateDirectory(Path.Combine(miRoot, "homes")); } catch { }
            string legacyHome = MakeTestHome("legacy-" + Process.GetCurrentProcess().Id);
            bool hadSelftestInstances = File.Exists(Path.Combine(binDir, "instances.json"));
            string selftestInstancesBak = null;

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
                ErrorReportDir = rptDir,
                Home = legacyHome
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
            for (int i = 0; i < 20 && !(mgr.State == BackendState.Running && mgr.IsMine); i++)
                Thread.Sleep(250);
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
            for (int i = 0; i < 20 && !(mgr.State == BackendState.Running && mgr.IsMine); i++)
                Thread.Sleep(250);
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

            // ---------- 6) 外部实例直接重启 ----------
            Console.WriteLine("[6] 外部实例直接重启");
            string extNode = DshResolver.FindNode();
            if (string.IsNullOrEmpty(extNode))
            {
                Check(false, "node 可用于外部实例");
            }
            else
            {
                int extPort = basePort + 2;
                string extJs = Path.Combine(binDir, "test-server.js");
                var extCfg = new Config
                {
                    Host = "127.0.0.1",
                    Port = extPort,
                    Workspace = Path.GetTempPath(),
                    ErrorReportDir = rptDir
                };
                var extPsi = new ProcessStartInfo(extNode, "\"" + extJs + "\" " + extPort)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using (Process ext = Process.Start(extPsi))
                {
                    bool extUp = false;
                    for (int i = 0; i < 20 && !extUp; i++)
                    {
                        extUp = PortTools.ProbeAsync(extCfg.Host, extCfg.Port).GetAwaiter().GetResult();
                        if (!extUp) Thread.Sleep(250);
                    }
                    Check(extUp, "外部实例已监听");

                    var extMgr = new BackendManager(null);
                    var extReady = new List<ReadyEventArgs>();
                    var extFail = new List<StartFailureContext>();
                    extMgr.Ready += (s, e) => { lock (extReady) extReady.Add(e); };
                    extMgr.StartFailed += (s, e) => { lock (extFail) extFail.Add(e); };
                    Check(extMgr.State == BackendState.Stopped, "外部实例下管理器状态为 Stopped");

                    bool extRst = extMgr.RestartAsync(extCfg).GetAwaiter().GetResult();
                    bool extUp2 = false;
                    for (int i = 0; i < 120 && !extUp2; i++)
                    {
                        extUp2 = PortTools.ProbeAsync(extCfg.Host, extCfg.Port).GetAwaiter().GetResult();
                        if (!extUp2) Thread.Sleep(1000);
                    }
                    Check(extRst && extUp2, "外部实例直接重启成功");
                    for (int i = 0; i < 20 && !(extMgr.State == BackendState.Running && extMgr.IsMine); i++)
                        Thread.Sleep(250);
                    Check(extMgr.State == BackendState.Running && extMgr.IsMine, "重启后为本程序启动");
                    Check(extReady.Count > 0 && extReady[0].SuppressAutoOpen,
                        "外部重启 Ready.SuppressAutoOpen = true");
                    Check(extFail.Count == 0, "外部重启无失败报告",
                        extFail.Count > 0 ? extFail[0].FailureKind : "");

                    bool extStopped = extMgr.StopAsync(extCfg, killExternal: false).GetAwaiter().GetResult();
                    Check(extStopped, "外部重启后停止成功");
                    extMgr.Dispose();
                }
            }

            // ---------- 7) 注册表与 v1→v2 迁移 ----------
            Console.WriteLine("[7] 注册表与迁移");
            {
                // 7.0 守卫：bin 目录 fixtures 只在自检作用域内临时备份/写入，
                // 防止触碰用户真实 launcher.json/instances.json。
                string instancesPath = Path.Combine(binDir, "instances.json");
                string legacyPath = Path.Combine(binDir, "launcher.json");
                bool hadInstances = File.Exists(instancesPath);
                bool hadLegacy = File.Exists(legacyPath);
                string bakInstances = null;
                string bakLegacy = null;
                try
                {
                    if (hadInstances) { bakInstances = instancesPath + ".selftest-bak"; File.Copy(instancesPath, bakInstances, true); }
                    if (hadLegacy) { bakLegacy = legacyPath + ".selftest-bak"; File.Copy(legacyPath, bakLegacy, true); }
                    if (File.Exists(instancesPath)) File.Delete(instancesPath);

                    var oldLauncher7 = new Config
                    {
                        Host = "127.0.0.1",
                        Port = 3185,
                        Workspace = @"C:\Temp\legacy-ws",
                        DshCommand = @"C:\Temp\legacy-dsh.cmd",
                        ErrorReportDir = @"C:\Temp\legacy-rpt",
                        Theme = AppTheme.Dark,
                        AutoOpenBrowser = false,
                        StopOnExit = false
                    };
                    File.WriteAllText(legacyPath,
                        System.Text.Json.JsonSerializer.Serialize(oldLauncher7, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }),
                        new System.Text.UTF8Encoding(false));

                    var reg = InstanceRegistry.Load();
                    Check(reg.Instances.Count == 1, "迁移生成 default 实例", reg.Instances.Count.ToString());
                    if (reg.Instances.Count > 0)
                    {
                        var d0 = reg.Instances[0];
                        Check(d0.Id == "default", "迁移实例 id=default", d0.Id);
                        Check(d0.Home == "", "迁移 default home 留空", d0.Home);
                        Check(d0.Port == 3185, "迁移端口保留", d0.Port.ToString());
                    }
                    Check(reg.Settings.DshCommand == @"C:\Temp\legacy-dsh.cmd", "settings.dshCommand 回填", reg.Settings.DshCommand);
                    Check(reg.Settings.ErrorReportDir == @"C:\Temp\legacy-rpt", "settings.errorReportDir 回填", reg.Settings.ErrorReportDir);
                    Check(reg.Settings.Theme == AppTheme.Dark, "settings.theme 回填", reg.Settings.Theme.ToString());
                    Check(File.Exists(legacyPath + ".v1.bak"), "launcher.json.v1.bak 存在");
                }
                finally
                {
                    if (bakInstances != null && File.Exists(bakInstances))
                    {
                        try { File.Copy(bakInstances, instancesPath, true); File.Delete(bakInstances); } catch { }
                    }
                    else if (File.Exists(instancesPath))
                    {
                        try { File.Delete(instancesPath); } catch { }
                    }
                    if (bakLegacy != null && File.Exists(bakLegacy))
                    {
                        try { File.Copy(bakLegacy, legacyPath, true); File.Delete(bakLegacy); } catch { }
                    }
                    else if (File.Exists(legacyPath))
                    {
                        try { File.Delete(legacyPath); } catch { }
                    }
                    try { if (File.Exists(legacyPath + ".v1.bak")) File.Delete(legacyPath + ".v1.bak"); } catch { }
                }

                // 不通过磁盘 Load 污染 bin 目录；直接覆盖 Load 无法注入静态 FilePath，
                // 因此静态成员校验已在上述临时备份场景内覆盖，新增文件与损坏回退
                // 略过真实磁盘写入，避免对发布目录造成难以恢复的副作用。
                Check(InstanceRegistry.IsValidId("a-b_1"), "合法 id 通过", "a-b_1");
                Check(!InstanceRegistry.IsValidId(""), "空 id 拒绝");
                Check(!InstanceRegistry.IsValidId("a/b"), "斜杠 id 拒绝");
                Check(!InstanceRegistry.IsValidId(new string('x', 65)), "超长 id 拒绝");

                // InstanceRegistry 构造函数为 private；用反射创建空 registry 避免
                // 依赖真实 bin 目录，同时仍能覆盖 CRUD 语义。
                InstanceRegistry regC = NewInstanceRegistry();
                Check(!regC.TryGet("default", out _), "空 registry 无 default");
                regC.Add(new InstanceDef { Id = "x", Name = "x", Port = basePort + 7 });
                Check(regC.TryGet("x", out _), "CRUD Add 后存在");
                regC.Remove("x");
                Check(!regC.TryGet("x", out _), "CRUD Remove 后不存在");
            }

            // ---------- 8)-[11] 注册表隔离（InstanceManager 成功启动会 Save registry；
            // 在 bin 目录临时隔离 instances.json，保证新增用例不污染真实配置） ----------
            string selftestInstancesPath = Path.Combine(binDir, "instances.json");
            if (hadSelftestInstances && File.Exists(selftestInstancesPath))
            {
                selftestInstancesBak = selftestInstancesPath + ".selftest-multibak";
                File.Copy(selftestInstancesPath, selftestInstancesBak, true);
            }
            try { if (File.Exists(selftestInstancesPath)) File.Delete(selftestInstancesPath); } catch { }

            // ---------- 8) DSH_HOME 注入 ----------
            Console.WriteLine("[8] DSH_HOME 注入");
            string home8 = MakeTestHome("mi8-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            {
                var reg8 = NewInstanceRegistry();
                reg8.Add(new InstanceDef
                {
                    Id = "mi8",
                    Name = "注入自检",
                    Home = home8,
                    Host = "127.0.0.1",
                    Port = basePort + 8,
                    Workspace = Path.GetTempPath()
                });
                var mgr8 = new InstanceManager(null, reg8);
                bool started8 = mgr8.StartAsync("mi8").GetAwaiter().GetResult();
                bool up8 = WaitPort("127.0.0.1", basePort + 8, true, 120);
                Check(started8 && up8, "注入 DSH_HOME 后实例就绪", home8);
                Check(new HomeManager().HealthCheck(home8, out string detail8), "自动初始化 profiles/web/cordis.yml", detail8);
                string pkg8 = Path.Combine(home8, "profiles", "web", "package.json");
                Check(File.Exists(pkg8), "profiles/web/package.json 存在");
                if (File.Exists(pkg8))
                {
                    string pkgText8 = File.ReadAllText(pkg8);
                    Check(pkgText8.Contains("@deepseek-ai/dsh-base"), "bundles 含 dsh-base", "文本包含检查");
                }
                mgr8.StopAsync("mi8", false).GetAwaiter().GetResult();
                Check(!PortTools.ProbeAsync("127.0.0.1", basePort + 8).GetAwaiter().GetResult(), "注入实例停止后端口释放");
                mgr8.DisposeAll();
            }

            // ---------- 8.3) 空 home 不注入 ----------
            {
                string envCmd = Path.Combine(Path.GetTempPath(), "dsh-mi-test", "homes", "test-env-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".cmd");
                File.WriteAllText(envCmd, "@echo DSH_HOME=%DSH_HOME% & exit /b 0\r\n");
                var cfgEmptyHome = new Config
                {
                    DshCommand = envCmd,
                    Host = "127.0.0.1",
                    Port = basePort + 9,
                    Workspace = Path.GetTempPath(),
                    Home = ""
                };
                var mgrEmpty = new BackendManager(null);
                bool startedEmpty = mgrEmpty.StartAsync(cfgEmptyHome).GetAwaiter().GetResult();
                bool outputEmptyEnough = false;
                string lineEmptyHome = "";
                for (int i = 0; i < 20 && !outputEmptyEnough; i++)
                {
                    Thread.Sleep(250);
                    lineEmptyHome = mgrEmpty.RecentOutput(200).FirstOrDefault(
                        l => l.StartsWith("[err] ") ? l.Substring(6).StartsWith("DSH_HOME=") : l.StartsWith("DSH_HOME="));
                    outputEmptyEnough = lineEmptyHome != null;
                }
                Check(startedEmpty, "空 home 探针启动成功");
                // 父进程环境可能自带 DSH_HOME（如本工具运行于 dsh 会话内）：
                // 空 home 的正确语义是"不注入新值"，即子进程 DSH_HOME 与父进程一致。
                string parentDshHome = Environment.GetEnvironmentVariable("DSH_HOME") ?? "";
                string actualHome = "";
                if (!string.IsNullOrEmpty(lineEmptyHome))
                {
                    int eq = lineEmptyHome.IndexOf('=');
                    actualHome = eq >= 0 ? lineEmptyHome.Substring(eq + 1).TrimEnd() : lineEmptyHome;
                }
                Check(lineEmptyHome != null && actualHome == parentDshHome,
                    "空 home 不注入（与父环境一致）",
                    "parent='" + parentDshHome + "' actual='" + actualHome + "'");
                mgrEmpty.StopAsync(cfgEmptyHome, false).GetAwaiter().GetResult();
                Check(!PortTools.ProbeAsync("127.0.0.1", basePort + 9).GetAwaiter().GetResult(), "空 home 探针端口释放");
                mgrEmpty.Dispose();
                try { File.Delete(envCmd); } catch { }
            }

            // ---------- 9/10) 双实例并行与数据隔离 ----------
            Console.WriteLine("[9] 双实例并行");
            Console.WriteLine("[10] 数据隔离");
            string homeA = MakeTestHome("a-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            string homeB = MakeTestHome("b-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            {
                var regAb = NewInstanceRegistry();
                regAb.Add(new InstanceDef { Id = "a", Name = "A", Home = homeA, Host = "127.0.0.1", Port = basePort + 10, Workspace = Path.GetTempPath() });
                regAb.Add(new InstanceDef { Id = "b", Name = "B", Home = homeB, Host = "127.0.0.1", Port = basePort + 11, Workspace = Path.GetTempPath() });
                var mgrAb = new InstanceManager(null, regAb);
                bool sa = mgrAb.StartAsync("a").GetAwaiter().GetResult();
                bool sb = mgrAb.StartAsync("b").GetAwaiter().GetResult();
                bool upA = WaitPort("127.0.0.1", basePort + 10, true, 120);
                bool upB = WaitPort("127.0.0.1", basePort + 11, true, 120);
                Check(sa && upA, "实例 A 就绪", (basePort + 10).ToString());
                Check(sb && upB, "实例 B 就绪", (basePort + 11).ToString());
                int pidA = mgrAb.For("a").ChildPid;
                int pidB = mgrAb.For("b").ChildPid;
                Check(pidA > 0 && pidB > 0, "A/B 各自 PID 已记录", pidA + "/" + pidB);

                string markerName = "mi-marker-" + Guid.NewGuid().ToString("N") + ".txt";
                string storageA = Path.Combine(homeA, "storages");
                Directory.CreateDirectory(storageA);
                File.WriteAllText(Path.Combine(storageA, markerName), "A-only");
                Check(File.Exists(Path.Combine(homeA, "storages", markerName)), "A 标记文件已写");
                Check(!File.Exists(Path.Combine(homeB, "storages", markerName)), "B 存储区无 A 标记");
                // sessions 目录由 dsh 在有会话时才创建，不强求存在（隔离语义由 storages 标记验证）

                bool stopA = mgrAb.StopAsync("a", false).GetAwaiter().GetResult();
                bool downA = !WaitPort("127.0.0.1", basePort + 10, true, 30);
                bool upB2 = WaitPort("127.0.0.1", basePort + 11, true, 10);
                Check(stopA && downA, "停止 A 后 A 端口释放", "stop=" + stopA);
                Check(upB2, "停止 A 后 B 仍监听");
                Check(mgrAb.For("b").ChildPid == pidB && pidB > 0, "B PID 保持", pidB.ToString());
                bool stopB = mgrAb.StopAsync("b", false).GetAwaiter().GetResult();
                bool downB = !WaitPort("127.0.0.1", basePort + 11, true, 30);
                Check(stopB && downB, "停止 B 后 B 端口释放", "stop=" + stopB);
                mgrAb.DisposeAll();
            }

            // ---------- 11) 实例锁 ----------
            Console.WriteLine("[11] 实例锁");
            string homeC = MakeTestHome("c-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            {
                var regC11 = NewInstanceRegistry();
                regC11.Add(new InstanceDef { Id = "c", Name = "C", Home = homeC, Host = "127.0.0.1", Port = basePort + 12, Workspace = Path.GetTempPath() });
                var mgrC = new InstanceManager(null, regC11);
                Directory.CreateDirectory(homeC);
                File.WriteAllText(Path.Combine(homeC, ".dsh-instance.lock"), Process.GetCurrentProcess().Id.ToString());
                Check(mgrC.IsLocked("c", out int lockedPid) && lockedPid == Process.GetCurrentProcess().Id, "锁文件判定为存活锁定", lockedPid.ToString());
                bool startLocked = mgrC.StartAsync("c").GetAwaiter().GetResult();
                Check(!startLocked, "锁定 HOME 拒绝二次启动");
                Check(!WaitPort("127.0.0.1", basePort + 12, true, 5), "拒绝启动未占用端口");
                File.Delete(Path.Combine(homeC, ".dsh-instance.lock"));
                Check(!mgrC.IsLocked("c", out _), "删除锁文件后未锁定");
                mgrC.DisposeAll();
            }

            // ---------- 12) 端口分配器 ----------
            // PortAllocator 的公开约束是 3080..3099 段（用户实例端口段）；
            // 自检端口 3192+ 不在该段内，直接问 3198/3199 会被实现回落/拒绝。
            // 这里按实际 API 自检：注入探针占用 3085，验证 3085 顺延到 3086，
            // 同时验证在册端口排除；端口纪律仍避免 3090+ 和 3185+。
            Console.WriteLine("[12] 端口分配器");
            {
                var allocator = new PortAllocator(port => Task.FromResult(port == 3085));
                Check(allocator.SuggestAsync(3085, Array.Empty<int>()).GetAwaiter().GetResult() == 3086,
                    "被占用端口顺延", "3085 -> 3086");
                Check(allocator.SuggestAsync(3086, new[] { 3086 }).GetAwaiter().GetResult() == 3087,
                    "在册端口排除", "3086 in-taken -> 3087");
                Check(allocator.IsFreeAsync(3085, Array.Empty<int>()).GetAwaiter().GetResult() == false,
                    "探针占用端口判为不空闲");
                Check(allocator.IsFreeAsync(3086, Array.Empty<int>()).GetAwaiter().GetResult() == true,
                    "探针空闲端口判为空闲");
            }

            // ---------- 13) 克隆 ----------
            Console.WriteLine("[13] 克隆");
            string src13 = MakeTestHome("src-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            string dst13 = MakeTestHome("dst-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            string dstBlank13 = MakeTestHome("dstb-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            {
                var hm = new HomeManager();
                hm.CreateBlank(src13);
                string srcPkgDir = Path.Combine(src13, "packages");
                string srcProfiles = Path.Combine(src13, "profiles", "web");
                Directory.CreateDirectory(srcPkgDir);
                Directory.CreateDirectory(srcProfiles);
                Directory.CreateDirectory(Path.Combine(src13, "profiles", "node_modules"));
                File.WriteAllText(Path.Combine(srcPkgDir, "demo-1.0.0.tgz"), "demo package placeholder");
                // 当前 HomeManager 重写的是解析后包含 srcHome 的 file:/link: 依赖；
                // 相对 file:../../packages 会被解析到 dst 自身而不会触发重写，
                // 这里用绝对 path 验证同一条重写/复制链路（路径分隔符由实现归一化）。
                var srcPkgObj = new Dictionary<string, Dictionary<string, string>>
                {
                    ["dependencies"] = new Dictionary<string, string>
                    {
                        ["demo-pkg"] = "file:" + Path.Combine(src13, "packages", "demo-1.0.0.tgz")
                    }
                };
                File.WriteAllText(Path.Combine(srcProfiles, "package.json"),
                    System.Text.Json.JsonSerializer.Serialize(srcPkgObj, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }),
                    new System.Text.UTF8Encoding(false));
                File.WriteAllText(Path.Combine(src13, "profiles", "node_modules", "ignore-me.txt"), "ignored");

                hm.Clone(src13, dst13, CloneLevel.Full);
                Check(!Directory.Exists(Path.Combine(dst13, "profiles", "node_modules")), "profiles/node_modules 未复制");
                Check(File.Exists(Path.Combine(dst13, "packages", "demo-1.0.0.tgz")), "packages/demo-1.0.0.tgz 已复制");
                string dstPkgJson = Path.Combine(dst13, "profiles", "web", "package.json");
                Check(File.Exists(dstPkgJson), "目标 package.json 存在");
                if (File.Exists(dstPkgJson))
                {
                    // JSON 序列化会把反斜杠写为 \\，先还原一次再做完整路径包含检查。
                    string dstPkgText = File.ReadAllText(dstPkgJson).Replace("\\\\", "\\");
                    Check(dstPkgText.Contains(Path.GetFullPath(dst13)) && dstPkgText.Contains("packages"),
                        "依赖路径重写指向新 HOME",
                        "期望含 " + Path.GetFullPath(dst13) + "；实际:\n" + dstPkgText);
                }

                hm.Clone(src13, dstBlank13, CloneLevel.Blank);
                Check(Directory.Exists(dstBlank13), "Blank 目标目录存在");
                Check(Directory.GetFiles(dstBlank13, "*", SearchOption.AllDirectories).Length == 0, "Blank 不复制文件");
            }

            // 恢复 [8]-[11] 期间被 InstanceManager 写入的临时 registry。
            if (selftestInstancesBak != null && File.Exists(selftestInstancesBak))
            {
                File.Copy(selftestInstancesBak, Path.Combine(binDir, "instances.json"), true);
                try { File.Delete(selftestInstancesBak); } catch { }
            }
            else if (File.Exists(Path.Combine(binDir, "instances.json")))
            {
                try { File.Delete(Path.Combine(binDir, "instances.json")); } catch { }
            }

            mgr.Dispose();
            try { File.Delete(failCmd); } catch { }

            Console.WriteLine("== 结果: " + _pass + " passed, " + _fail + " failed ==");
            return _fail == 0 ? 0 : 1;
        }

        /// <summary>创建本次自检专用临时 HOME；调用方负责不触碰真实 ~/.dsh。</summary>
        private static string MakeTestHome(string name)
        {
            string home = Path.Combine(Path.GetTempPath(), "dsh-mi-test", "homes", name);
            try { Directory.CreateDirectory(home); } catch { }
            return home;
        }

        /// <summary>通过反射创建空 InstanceRegistry，避免触碰真实 bin 目录文件。</summary>
        private static InstanceRegistry NewInstanceRegistry()
        {
            var ctor = typeof(InstanceRegistry).GetConstructor(
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
                null, new[] { typeof(InstancesFile) }, null);
            if (ctor == null)
                throw new InvalidOperationException("InstanceRegistry 私有构造函数不可用。");
            return (InstanceRegistry)ctor.Invoke(new object[] { new InstancesFile() });
        }

        /// <summary>等待端口进入/离开指定状态；返回最终端口探测结果。</summary>
        private static bool WaitPort(string host, int port, bool up, int timeoutSeconds)
        {
            return PortTools.WaitForPortAsync(host, port, up, timeoutSeconds).GetAwaiter().GetResult();
        }
    }
}
