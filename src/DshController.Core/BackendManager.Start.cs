// ============================================================================
//  BackendManager · 启动链路（partial 分部；纯搬移，语义与拆分前逐字一致）
// ============================================================================

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using DshController.Core.Abstractions;
using DshController.Core.Storage;

namespace DshController.Core
{
    public sealed partial class BackendManager
    {
        /// <summary>供应商预设凭据环境对（api-presets.json：Enabled 且填了密钥的预设）。
        /// 同步只往实例 settings.yaml 写 env 名，密钥值在拉起子进程时注入——不落盘。</summary>
        private List<(string Name, string Value)> PresetCredentialEnvPairs()
        {
            var pairs = new List<(string, string)>();
            try
            {
                pairs = new ProviderPresetStore(AppPaths.ProviderPresetsFile).CredentialEnvPairs();
                if (pairs.Count > 0) Diag("预设凭据注入", string.Join(", ", pairs.Select(p => p.Item1)));
            }
            catch (Exception ex)
            {
                // 理由: 台账读取失败不阻断启动；实例侧缺该变量时由 dsh MISSING_CREDENTIAL 提示
                Diag("预设凭据注入", "读取失败跳过：" + ex.Message);
            }
            return pairs;
        }

        // ==================== 核心（调用方已持有 _gate） ====================

        private async Task<bool> StartCoreAsync(Config cfg, StartOptions opts)
        {
            // 忙检查：仅 Stopped 可发起
            lock (_gate)
            {
                if (_state == BackendState.Starting || _state == BackendState.Stopping ||
                    _state == BackendState.Running) return false;
            }

            // 0) 已在线 → 直接视为运行（外部实例），不重复启动
            if (await PortTools.ProbeAsync(cfg.Host, cfg.Port).ConfigureAwait(false))
            {
                LogUi("后端已在运行（" + PortTools.Url(cfg.Host, cfg.Port) + "），不重复启动。");
                SetState(BackendState.Running, mine: false, pid: 0);
                if (!opts.SuppressAutoOpen) RaiseReady(cfg, suppressAutoOpen: false);
                return true;
            }

            // 0.5) WSL 实例走独立启动路径（v0.4.0）
            if (cfg.IsWsl)
            {
                return await StartWslCoreAsync(cfg, opts).ConfigureAwait(false);
            }

            // 1) 解析启动命令：
            //    指定版本（harnessVersion 非空）→ 只需要 npx，不要求本机已装 dsh；
            //    跟随环境（空）→ 4 级回退解析本机 dsh。
            string pinnedVersion = (cfg.HarnessVersion ?? "").Trim();
            ClearDiag();
            Diag("运行环境", "Windows（本机直接拉起）");
            Diag("实例", (cfg.InstanceName ?? "") + "（" + (cfg.InstanceId ?? "") + "）");
            Diag("harness 版本", pinnedVersion.Length > 0
                ? "指定 " + pinnedVersion + "（npx 拉取 @deepseek-ai/dsh@" + pinnedVersion + "）"
                : "跟随当前环境主实例版本");

            DshCommand dsh;
            if (pinnedVersion.Length > 0)
            {
                string npx = HarnessVersion.FindNpxWindows();
                Diag("npx 路径", string.IsNullOrEmpty(npx) ? "(未找到)" : npx);
                if (string.IsNullOrEmpty(npx))
                {
                    FailStart(cfg, "npx 未找到",
                        "实例指定了 harness 版本 " + pinnedVersion + "，需要 npx 拉取指定版本，" +
                        "但本机未找到 npx.cmd。请安装 Node.js/npm（npm i -g @deepseek-ai/dsh 自带 npx），" +
                        "或把该实例的 harness 版本改回\"跟随当前环境\"。",
                        ex: null, exitCode: null);
                    return false;
                }
                dsh = new DshCommand { Kind = "npx", Path1 = npx, Path2 = pinnedVersion };
                LogUi("harness 版本: 指定 " + pinnedVersion +
                      "（npx 拉取 @deepseek-ai/dsh@" + pinnedVersion + "，首次使用需联网下载）");
            }
            else
            {
                dsh = _resolver.Resolve(cfg);
                if (dsh == null)
                {
                    FailStart(cfg, "dsh 命令未找到",
                        "4 级回退（配置 → npm shim → PATH → node 入口）均未命中，详见解析表。" +
                        "也可以给该实例指定一个 harness 版本，改用 npx 拉取该版本启动。",
                        ex: null, exitCode: null);
                    return false;
                }
                LogUi("harness 版本: 跟随当前环境（" + dsh.Describe() + "）");
            }
            Diag("启动命令", dsh.Describe());
            LogUi("dsh 命令: " + dsh.Describe());

            // 2) 工作目录
            string ws = cfg.Workspace;
            if (!Directory.Exists(ws))
            {
                try { Directory.CreateDirectory(ws); }
                catch (Exception ex)
                {
                    LogUi("无法创建工作目录 " + ws + "：" + ex.Message + "，改用用户目录。");
                    ws = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                }
            }

            // 3) 组装进程
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
            // R4（重启绝不拉浏览器）·子进程侧补丁：抑制自动打开时给 dsh web 传 --no-open；
            // 正常启动的命令行与历史字节一致（现状见 docs/audit-restart-browser.md §4）。
            string webTail = BuildWebTailArgs(cfg, opts.SuppressAutoOpen);

            if (dsh.Kind == "cmd")
            {
                psi.FileName = "cmd.exe";
                psi.Arguments = "/d /s /c \"\"" + dsh.Path1 + "\"" + webTail + "\"";
            }
            else if (dsh.Kind == "npx")
            {
                // v0.5.0 指定版本：npx --yes @deepseek-ai/dsh@<ver> web ...（npx 缓存于 npm 缓存目录）
                psi.FileName = "cmd.exe";
                psi.Arguments = "/d /s /c \"\"" + dsh.Path1 + "\" --yes @deepseek-ai/dsh@" +
                    dsh.Path2 + webTail + "\"";
            }
            else
            {
                psi.FileName = dsh.Path1;
                psi.Arguments = "\"" + dsh.Path2 + "\"" + webTail;
            }

            // v0.3.0 多实例：非空 DSH_HOME 注入到子进程环境，避免实例间共享 ~/.dsh。
            if (!string.IsNullOrEmpty(cfg.Home))
                psi.EnvironmentVariables["DSH_HOME"] = cfg.Home;

            // 供应商预设凭据注入（llm-pi-ai 同步只写 env 名，值随启动注入）
            foreach ((string envName, string envValue) in PresetCredentialEnvPairs())
                psi.EnvironmentVariables[envName] = envValue;

            Diag("工作目录", ws + (Directory.Exists(ws) ? "" : "（不存在）"));
            Diag("DSH_HOME 注入", string.IsNullOrEmpty(cfg.Home) ? "（未注入，使用默认 ~/.dsh）" : cfg.Home);
            Diag("命令行", psi.FileName + " " + psi.Arguments);
            LastSpawnArguments = psi.FileName + " " + psi.Arguments;

            SetState(BackendState.Starting, false, 0);
            ClearRing();

            var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            p.OutputDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data)) OnChildLine(e.Data, false); };
            p.ErrorDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data)) OnChildLine(e.Data, true); };
            p.Exited += (s, e) => OnChildExited(p);

            // _child 只在 Start 成功后赋值（保留 legacy 修复意图：失败不残留 Process 对象）
            try
            {
                if (!p.Start())
                {
                    FailStart(cfg, "进程创建失败", "Process.Start 返回 false。", null, null);
                    return false;
                }
            }
            catch (Exception ex)
            {
                FailStart(cfg, "进程启动异常（SpawnError）",
                    ex is System.ComponentModel.Win32Exception
                        ? ex.Message + "（从服务/非交互环境启动时，请检查工作目录与 cmd.exe 可用性）"
                        : ex.Message,
                    ex, null);
                return false;
            }

            lock (_gate) { _child = p; _childPid = p.Id; _mine = true; }
            LogUi("已启动 dsh web（PID " + p.Id + "，工作目录: " + ws + "）");
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            StartPump();

            // 4) 就绪等待（独立任务，可被停止取消）
            _cancelDueToStop = false;
            _readyCts = new CancellationTokenSource();
            var ct = _readyCts.Token;
            var thisCfg = cfg;
            var thisOpts = opts;
            _readyTask = ReadyLoopAsync(thisCfg, thisOpts, p, p.Id, ct);
            return true;
        }

        // ==================== 失败上报 ====================

        private void FailStart(Config cfg, string kind, string summary, Exception ex, int? exitCode)
        {
            LogUi("启动失败：" + kind + "。" + (summary ?? ""));

            var ctx = new StartFailureContext
            {
                FailureKind = kind,
                Summary = summary,
                Exception = ex,
                Config = cfg,
                // WSL 实例的失败与"Windows 侧 dsh 4 级回退"无关，解析表只对 Windows 实例有意义
                Trace = cfg != null && cfg.IsWsl ? null : _resolver.Trace(cfg),
                CapturedOutput = RecentOutput(200),
                ConsoleLog = RecentConsole(200),
                Diagnostics = DiagSnapshot(),
                InstanceId = cfg?.InstanceId ?? "",
                InstanceHome = cfg?.Home ?? "",
                Phase = "start",
                ExitCode = exitCode
            };

            // v0.5.0：报告在核心层直接生成（不再依赖 UI 事件订阅）——
            // 具体的报错信息（含控制台转录）+ 实例信息 + 时间 落盘到用户指定的报告目录，
            // 文件名含实例 ID 与时间戳；写入失败兜底 exe 目录 reports\。
            try
            {
                ctx.ReportPath = ErrorReporter.WriteStartFailure(ctx);
                if (!string.IsNullOrEmpty(ctx.ReportPath))
                    LogUi("已生成启动失败报告: " + ctx.ReportPath);
                else
                    LogUi("⚠ 启动失败报告写入失败（目标目录: " +
                        (cfg?.EffectiveErrorReportDir ?? "（未知）") + "），请检查该目录是否存在/可写");
            }
            catch (Exception rex)
            {
                LogUi("⚠ 启动失败报告生成异常: " + rex.Message);
            }

            RunOnUi(() =>
            {
                var h = StartFailed;
                if (h != null) h(this, ctx);
            });
            lock (_gate) { if (_state == BackendState.Starting) _state = BackendState.Stopped; }
            RaiseStateChanged(BackendState.Stopped, false, 0);
        }

    }
}
