// ============================================================================
//  BackendManager — dsh 后端进程生命周期管理（v0.2.0 核心）
//
//  相比 legacy（MainForm 直连进程+端口 API，_starting 布尔散落各处）：
//    - 显式状态机 Stopped/Starting/Running/Stopping/Restarting 驱动 UI 按钮态；
//    - 就绪等待独立可取消 → 启动中也能随时"停止"（修 legacy D9：180s 内按钮全禁用）；
//    - 输出走 Channel + 100ms 批量泵（修 legacy E1：每行一次 BeginInvoke 洪泛）；
//    - 环形缓冲最近 2000 行供错误报告转录；
//    - Restart 路径硬编码 SuppressAutoOpen=true：无论配置如何都不拉浏览器（需求 R4）。
//  所有事件经 DispatcherQueue 投递到 UI 线程；UI 不直接接触 Process。
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
using Microsoft.UI.Dispatching;

namespace DshController.Core
{
    public enum BackendState { Stopped, Starting, Running, Stopping, Restarting }

    public sealed class StartOptions
    {
        public bool SuppressAutoOpen;      // 重启路径必须置 true（R4 不变量）
        public int ReadyTimeoutSeconds = 180;
    }

    public sealed class StateChangedEventArgs : EventArgs
    {
        public BackendState State;
        public bool Mine;                  // 运行中时是否本程序启动
        public int Pid;                    // 本程序子进程 PID（Mine 时有效）
    }

    public sealed class ReadyEventArgs : EventArgs
    {
        public string Url;
        public bool SuppressAutoOpen;      // UI 依此决定是否开浏览器
    }

    public sealed class OutputBatchEventArgs : EventArgs
    {
        public string[] Lines;             // 原始输出行（无时间戳）
    }

    public sealed class BackendManager : IDisposable
    {
        private const int RingCapacity = 2000;

        private readonly DispatcherQueue _dq;
        private readonly DshResolver _resolver = new DshResolver();
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);

        private Process _child;
        private int _childPid;
        private CancellationTokenSource _readyCts;
        private Task _readyTask;
        private CancellationTokenSource _pumpCts;
        private Task _pumpTask;
        private Channel<string> _output;
        private readonly object _ringLock = new object();
        private readonly LinkedList<string> _ring = new LinkedList<string>();

        private BackendState _state = BackendState.Stopped;
        private bool _mine;
        private volatile string _announcedUrl = "";
        private bool _cancelDueToStop;     // 停止引发的取消：就绪循环不再报"启动失败"

        public event EventHandler<StateChangedEventArgs> StateChanged;
        public event EventHandler<OutputBatchEventArgs> OutputBatched;
        public event EventHandler<ReadyEventArgs> Ready;
        public event EventHandler<string> AnnouncedUrlChanged;
        public event EventHandler<StartFailureContext> StartFailed;
        public event EventHandler<string> Log;                 // 管理器自身日志（不含子进程输出）

        public BackendManager(DispatcherQueue dq) { _dq = dq; }

        public BackendState State { get { lock (_gate) { return _state; } } }
        public bool IsMine { get { lock (_gate) { return _mine; } } }
        public int ChildPid { get { lock (_gate) { return _childPid; } } }
        public string AnnouncedUrl { get { return _announcedUrl; } }
        public DshResolver Resolver { get { return _resolver; } }

        public string DescribeDsh(Config cfg)
        {
            DshCommand d = _resolver.Resolve(cfg);
            return d == null ? "(未找到)" : d.Describe();
        }

        public List<string> RecentOutput(int maxLines)
        {
            lock (_ringLock)
            {
                return _ring.Skip(Math.Max(0, _ring.Count - maxLines)).ToList();
            }
        }

        // ==================== 对外操作 ====================

        /// <summary>启动后端。返回是否进入了运行/启动流程（失败路径内部发 StartFailed）。</summary>
        public async Task<bool> StartAsync(Config cfg, StartOptions opts = null)
        {
            opts = opts ?? new StartOptions();
            await _gate.WaitAsync().ConfigureAwait(false);
            try { return await StartCoreAsync(cfg, opts).ConfigureAwait(false); }
            finally { _gate.Release(); }
        }

        /// <summary>停止后端。killExternal=true 时允许结束外部监听进程（UI 负责事先确认）。</summary>
        public async Task<bool> StopAsync(Config cfg, bool killExternal)
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try { return await StopCoreAsync(cfg, killExternal, BackendState.Stopping).ConfigureAwait(false); }
            finally { _gate.Release(); }
        }

        /// <summary>重启：停止（含外部实例）→ 重新启动；全程 SuppressAutoOpen=true（R4）。</summary>
        public async Task<bool> RestartAsync(Config cfg)
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                bool mine;
                int pid;
                lock (_gate) { mine = _mine; pid = _childPid; }
                if (State == BackendState.Stopped) return false;

                LogUi("⟳ 重启：正在停止后端（浏览器不会自动打开）…");
                SetState(BackendState.Restarting, mine, pid);
                bool stopped = await StopCoreAsync(cfg, killExternal: true,
                    intermediateState: BackendState.Restarting).ConfigureAwait(false);
                if (!stopped)
                {
                    LogUi("⟳ 重启中止：后端未能完全停止（见失败报告）。");
                    SetState(BackendState.Stopped, false, 0);
                    return false;
                }
                LogUi("⟳ 后端已停止，端口已释放。正在重新启动（不打开浏览器）…");
                return await StartCoreAsync(cfg, new StartOptions { SuppressAutoOpen = true }).ConfigureAwait(false);
            }
            finally { _gate.Release(); }
        }

        public void Dispose()
        {
            try { if (_pumpCts != null) _pumpCts.Cancel(); } catch { }
            try { if (_readyCts != null) _readyCts.Cancel(); } catch { }
            try { if (_child != null) _child.Dispose(); } catch { }
            _gate.Dispose();
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

            // 1) 解析 dsh
            DshCommand dsh = _resolver.Resolve(cfg);
            if (dsh == null)
            {
                FailStart(cfg, "dsh 命令未找到",
                    "4 级回退（配置 → npm shim → PATH → node 入口）均未命中，详见解析表。",
                    ex: null, exitCode: null);
                return false;
            }
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
            if (dsh.Kind == "cmd")
            {
                psi.FileName = "cmd.exe";
                psi.Arguments = "/d /s /c \"\"" + dsh.Path1 + "\" web --host " + cfg.Host + " --port " + cfg.Port + "\"";
            }
            else
            {
                psi.FileName = dsh.Path1;
                psi.Arguments = "\"" + dsh.Path2 + "\" web --host " + cfg.Host + " --port " + cfg.Port;
            }

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

        private async Task<bool> StopCoreAsync(Config cfg, bool killExternal, BackendState intermediateState)
        {
            Process mine; int minePid;
            lock (_gate) { mine = _child; minePid = _childPid; }
            bool mineAlive = IsChildAlive(mine);

            bool up = await PortTools.ProbeAsync(cfg.Host, cfg.Port).ConfigureAwait(false);
            if (!mineAlive && !up)
            {
                LogUi("后端当前未运行，无需停止。");
                SetState(BackendState.Stopped, false, 0);
                return true;
            }
            if (!mineAlive && up && !killExternal)
            {
                LogUi("检测到外部后端在线；未授权结束外部进程，已跳过。");
                SetState(BackendState.Running, mine: false, pid: 0);
                return false;
            }

            SetState(intermediateState, mineAlive, minePid);

            // 取消就绪等待（标记为停止性取消，避免误报"启动失败"）
            _cancelDueToStop = true;
            try { if (_readyCts != null) _readyCts.Cancel(); } catch { }
            if (_readyTask != null)
            {
                try { await Task.WhenAny(_readyTask, Task.Delay(8000)).ConfigureAwait(false); }
                catch { }
            }

            int target = mineAlive ? minePid : 0;
            if (target == 0 && up) target = await PortTools.FindListenerPidAsync(cfg.Port).ConfigureAwait(false);
            if (target == 0 && up)
            {
                LogUi("检测到后端在线，但无法定位监听进程。");
            }
            else if (target > 0)
            {
                LogUi("正在停止后端（PID " + target + "）…");
                var r = await PortTools.EnsurePortFreeAsync(cfg.Host, cfg.Port, target).ConfigureAwait(false);
                bool freed = r.Item1;
                foreach (string l in r.Item2.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    LogUi("  " + l);
                if (!freed)
                {
                    FailStop(cfg, "端口无法释放",
                        "停止后 " + PortTools.Url(cfg.Host, cfg.Port) + " 仍在监听。", r.Item2);
                }
            }

            try { if (mine != null && !mine.WaitForExit(8000)) { } } catch { }
            try { if (mine != null) mine.Dispose(); } catch { }
            lock (_gate) { _child = null; _childPid = 0; _mine = false; }

            bool down = !await PortTools.ProbeAsync(cfg.Host, cfg.Port).ConfigureAwait(false);
            LogUi(down ? "后端已停止，端口已释放。" : "端口仍在监听，后端可能未完全退出。");
            SetState(BackendState.Stopped, false, 0);
            return down;
        }

        // ==================== 就绪循环 / 输出泵 ====================

        private async Task ReadyLoopAsync(Config cfg, StartOptions opts, Process p, int pid, CancellationToken ct)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(opts.ReadyTimeoutSeconds);
            try
            {
                while (DateTime.UtcNow < deadline)
                {
                    if (ct.IsCancellationRequested)
                    {
                        if (!_cancelDueToStop)
                        {
                            // 外部取消（如异常路径）：交由停止流程收尾
                        }
                        return;
                    }
                    if (!IsChildAlive(p))
                    {
                        int code = -1;
                        try { code = p.ExitCode; } catch { }
                        FailStart(cfg, "子进程早退",
                            "dsh web 进程在就绪前退出（退出码 " + code + "），输出转录见下文。",
                            null, code);
                        return;
                    }
                    if (await PortTools.ProbeAsync(cfg.Host, cfg.Port, ct).ConfigureAwait(false))
                    {
                        string url = PortTools.Url(cfg.Host, cfg.Port);
                        LogUi("后端已就绪: " + url);
                        SetState(BackendState.Running, true, pid);
                        RaiseReady(cfg, opts.SuppressAutoOpen);
                        return;
                    }
                    await Task.Delay(800, ct).ConfigureAwait(false);
                }
                // 超时：杀掉无响应子进程（v0.2.0 行为变更：不留僵尸，见 CHANGELOG）
                LogUi("等待后端就绪超时（" + opts.ReadyTimeoutSeconds + " 秒），正在清理进程…");
                FailStart(cfg, "就绪超时",
                    "等待 " + opts.ReadyTimeoutSeconds + " 秒后端口仍未监听，进程已被清理。可重试启动。",
                    null, null);
                try { await PortTools.EnsurePortFreeAsync(cfg.Host, cfg.Port, pid).ConfigureAwait(false); }
                catch { }
                try { p.Kill(entireProcessTree: true); } catch { }
                lock (_gate) { if (_childPid == pid) { _child = null; _childPid = 0; _mine = false; } }
                SetState(BackendState.Stopped, false, 0);
            }
            catch (OperationCanceledException)
            {
                // 停止引发的取消：静默退出（StopCoreAsync 后续收尾）
            }
            catch (Exception ex)
            {
                FailStart(cfg, "就绪等待异常", ex.Message, ex, null);
            }
        }

        private void StartPump()
        {
            _output = Channel.CreateBounded<string>(new BoundedChannelOptions(8000)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true
            });
            try { if (_pumpCts != null) _pumpCts.Cancel(); } catch { }
            _pumpCts = new CancellationTokenSource();
            var ct = _pumpCts.Token;
            var channel = _output;
            _pumpTask = Task.Run(async () =>
            {
                var batch = new List<string>(128);
                try
                {
                    while (!ct.IsCancellationRequested)
                    {
                        if (!await channel.Reader.WaitToReadAsync(ct).ConfigureAwait(false)) break;
                        DateTime windowStart = DateTime.UtcNow;
                        batch.Clear();
                        while ((DateTime.UtcNow - windowStart).TotalMilliseconds < 100 && batch.Count < 500)
                        {
                            string line;
                            if (channel.Reader.TryRead(out line)) batch.Add(line);
                            else await Task.Delay(15, ct).ConfigureAwait(false);
                        }
                        if (batch.Count > 0)
                        {
                            var snapshot = batch.ToArray();
                            RunOnUi(() =>
                            {
                                var h = OutputBatched;
                                if (h != null) h(this, new OutputBatchEventArgs { Lines = snapshot });
                            });
                        }
                    }
                }
                catch (OperationCanceledException) { }
            }, ct);
        }

        private void OnChildLine(string line, bool isError)
        {
            string tagged = isError ? "[err] " + line : line;
            lock (_ringLock)
            {
                _ring.AddLast(tagged);
                while (_ring.Count > RingCapacity) _ring.RemoveFirst();
            }
            // 捕获 dsh 公告 URL
            try
            {
                Match m = Regex.Match(line, @"https?://[^\s]+");
                if (m.Success)
                {
                    string u = m.Groups[1].Value;
                    if (u.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                    {
                        _announcedUrl = u;
                        RunOnUi(() =>
                        {
                            var h = AnnouncedUrlChanged;
                            if (h != null) h(this, u);
                        });
                    }
                }
            }
            catch { }
            try { if (_output != null) _output.Writer.TryWrite(tagged); } catch { }
        }

        private void OnChildExited(Process p)
        {
            try
            {
                int pid = 0;
                try { pid = p.Id; } catch { }
                int code = -1;
                try { code = p.ExitCode; } catch { }
                bool isCurrent = false;
                lock (_gate) { isCurrent = _childPid != 0 && _childPid == pid; }
                if (!isCurrent) return; // 旧的/已移交的进程对象
                LogUi("dsh 进程已退出，退出码 " + code + "。");
                RunOnUi(() =>
                {
                    lock (_gate)
                    {
                        if (_state == BackendState.Running)
                        {
                            _child = null; _childPid = 0; _mine = false;
                            _state = BackendState.Stopped;
                        }
                    }
                    RaiseStateChanged(BackendState.Stopped, false, 0);
                });
            }
            catch { }
        }

        // ==================== 失败上报 ====================

        private void FailStart(Config cfg, string kind, string summary, Exception ex, int? exitCode)
        {
            var ctx = new StartFailureContext
            {
                FailureKind = kind,
                Summary = summary,
                Exception = ex,
                Config = cfg,
                Trace = _resolver.Trace(cfg),
                CapturedOutput = RecentOutput(200),
                ExitCode = exitCode
            };
            LogUi("启动失败：" + kind + "。" + (summary ?? ""));
            RunOnUi(() =>
            {
                var h = StartFailed;
                if (h != null) h(this, ctx);
            });
            lock (_gate) { if (_state == BackendState.Starting) _state = BackendState.Stopped; }
            RaiseStateChanged(BackendState.Stopped, false, 0);
        }

        private void FailStop(Config cfg, string kind, string summary, string extra)
        {
            var ctx = new StartFailureContext
            {
                FailureKind = kind,
                Summary = summary,
                Exception = null,
                Config = cfg,
                Trace = null,
                CapturedOutput = RecentOutput(100),
                ExitCode = null,
                Extra = "```\n" + (extra ?? "") + "\n```"
            };
            LogUi("停止失败：" + kind + "。" + summary);
            RunOnUi(() =>
            {
                var h = StartFailed;
                if (h != null) h(this, ctx);
            });
        }

        // ==================== 状态与 UI 投递 ====================

        private void SetState(BackendState s, bool mine, int pid)
        {
            lock (_gate) { _state = s; if (s == BackendState.Running || s == BackendState.Stopped) _mine = mine; }
            RaiseStateChanged(s, mine, pid);
        }

        private void RaiseStateChanged(BackendState s, bool mine, int pid)
        {
            RunOnUi(() =>
            {
                var h = StateChanged;
                if (h != null) h(this, new StateChangedEventArgs { State = s, Mine = mine, Pid = pid });
            });
        }

        private void RaiseReady(Config cfg, bool suppressAutoOpen)
        {
            RunOnUi(() =>
            {
                var h = Ready;
                if (h != null) h(this, new ReadyEventArgs
                {
                    Url = PortTools.Url(cfg.Host, cfg.Port),
                    SuppressAutoOpen = suppressAutoOpen
                });
            });
        }

        private void LogUi(string line)
        {
            RunOnUi(() =>
            {
                var h = Log;
                if (h != null) h(this, line);
            });
        }

        private void RunOnUi(Action a)
        {
            try
            {
                if (_dq != null) _dq.TryEnqueue(() => { try { a(); } catch { } });
                else a();
            }
            catch { }
        }

        private void ClearRing()
        {
            lock (_ringLock) { _ring.Clear(); }
        }

        private static bool IsChildAlive(Process p)
        {
            if (p == null) return false;
            try { return !p.HasExited; }
            catch { return false; }
        }
    }
}
