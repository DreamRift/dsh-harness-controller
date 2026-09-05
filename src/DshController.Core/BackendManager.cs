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
using DshController.Core.Abstractions;
using DshController.Core.Storage;

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

    public sealed partial class BackendManager : IDisposable
    {
        private const int RingCapacity = 2000;

        private readonly IUiDispatcher _dq;
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
        private readonly object _consoleLock = new object();
        private readonly LinkedList<string> _consoleRing = new LinkedList<string>();
        private readonly object _diagLock = new object();
        private readonly List<KeyValuePair<string, string>> _diag = new List<KeyValuePair<string, string>>();
        private const int ConsoleCapacity = 400;

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

        /// <summary>dispatcher 为 null = 无 UI 环境（CLI/自检），事件就地触发。</summary>
        public BackendManager(IUiDispatcher dispatcher) { _dq = dispatcher; }

        public BackendState State { get { lock (_gate) { return _state; } } }
        public bool IsMine { get { lock (_gate) { return _mine; } } }
        public int ChildPid { get { lock (_gate) { return _childPid; } } }
        public string AnnouncedUrl { get { return _announcedUrl; } }
        public DshResolver Resolver { get { return _resolver; } }

        /// <summary>最近一次子进程调用行（exe+参数或 WSL 脚本 exec 行）；供 --selftest-core R4 断言使用。</summary>
        public string LastSpawnArguments { get; private set; } = "";

        public string DescribeDsh(Config cfg)
        {
            DshCommand d = _resolver.Resolve(cfg);
            return d == null ? "(未找到)" : d.Describe();
        }

        /// <summary>写入管理器自身日志（经 Log 事件投递到 UI；供 InstanceManager/CLI 等外部调用）。</summary>
        public void LogLine(string line)
        {
            LogUi(line);
        }

        public List<string> RecentOutput(int maxLines)
        {
            lock (_ringLock)
            {
                return _ring.Skip(Math.Max(0, _ring.Count - maxLines)).ToList();
            }
        }

        // ==================== 对外操作 ====================

        /// <summary>
        /// 组装 dsh web 子命令尾参数：" web [--no-open] --host H --port P [--trusted-host X ...]"。
        /// suppressAutoOpen=true（重启路径）必须追加 --no-open——R4 子进程侧：dsh web 默认就绪即自开浏览器，
        /// 控制器侧 SuppressAutoOpen 拦不住它（见 docs/audit-restart-browser.md）；false 时与历史命令字节一致。
        /// 注意：cmd /s /c 只剥除首尾引号，参数必须插在最外层引号闭合之前；cmd 中反斜杠不是转义符，
        /// 内层引号原样保留（与现有 ""prog" args" 形态一致）。v0.3.0：trusted-host 可重复传参。
        /// </summary>
        public static string BuildWebTailArgs(Config cfg, bool suppressAutoOpen)
        {
            string trusted = "";
            if (cfg.TrustedHosts != null && cfg.TrustedHosts.Length > 0)
                trusted = " --trusted-host " + string.Join(
                    " --trusted-host ",
                    cfg.TrustedHosts.Select(h => "\"" + h + "\""));
            string noOpen = suppressAutoOpen ? " --no-open" : "";
            return " web" + noOpen + " --host " + cfg.Host + " --port " + cfg.Port + trusted;
        }

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
                lock (_gate)
                {
                    mine = _mine;
                    pid = _childPid;
                    // UI 可能已通过端口探测显示外部实例 Running，但管理器内部仍是 Stopped；
                    // 此时仍应允许“停止外部实例 → 由本程序重启”。
                    if (_state == BackendState.Starting || _state == BackendState.Stopping ||
                        _state == BackendState.Restarting)
                        return false;
                }

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
            try { if (_pumpCts != null) _pumpCts.Cancel(); }
            catch
            {
                // 理由: 释放前取消泵取消源，可能已释放，静默忽略
            }
            try { if (_readyCts != null) _readyCts.Cancel(); }
            catch
            {
                // 理由: 释放前取消就绪等待取消源，可能已释放，静默忽略
            }
            try { if (_child != null) _child.Dispose(); }
            catch
            {
                // 理由: 释放子进程对象，可能已退出/释放，静默忽略避免 Dispose 抛异常
            }
            _gate.Dispose();
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
            // v0.5.0：控制台文本同时进入"控制台转录"环形缓冲，
            // 启动失败时由 ErrorReporter 一并落盘（用户要求：控制台的具体报错信息进报告）
            lock (_consoleLock)
            {
                _consoleRing.AddLast(DateTime.Now.ToString("HH:mm:ss") + "  " + line);
                while (_consoleRing.Count > ConsoleCapacity) _consoleRing.RemoveFirst();
            }
            RunOnUi(() =>
            {
                var h = Log;
                if (h != null) h(this, line);
            });
        }

        /// <summary>控制台转录（管理器日志 + 关键步骤），最近 maxLines 行。</summary>
        public List<string> RecentConsole(int maxLines)
        {
            lock (_consoleLock)
            {
                return _consoleRing.Skip(Math.Max(0, _consoleRing.Count - maxLines)).ToList();
            }
        }

        /// <summary>记录一条启动诊断（进报告的"启动诊断"表；同名键覆盖）。</summary>
        private void Diag(string key, string value)
        {
            lock (_diagLock)
            {
                for (int i = 0; i < _diag.Count; i++)
                {
                    if (string.Equals(_diag[i].Key, key, StringComparison.Ordinal))
                    {
                        _diag[i] = new KeyValuePair<string, string>(key, value ?? "");
                        return;
                    }
                }
                _diag.Add(new KeyValuePair<string, string>(key, value ?? ""));
            }
        }

        private List<KeyValuePair<string, string>> DiagSnapshot()
        {
            lock (_diagLock) { return new List<KeyValuePair<string, string>>(_diag); }
        }

        private void ClearDiag()
        {
            lock (_diagLock) { _diag.Clear(); }
        }

        private void RunOnUi(Action a)
        {
            try
            {
                if (_dq != null) _dq.Post(a);
                else InlineUiDispatcher.Instance.Post(a);
            }
            catch
            {
                // 理由: UI 投递在调度器被销毁后可能抛异常，静默忽略以免退出路径受阻
            }
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
