// ============================================================================
//  BackendManager · 就绪循环与输出泵（partial 分部；纯搬移，语义与拆分前逐字一致）
// ============================================================================

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
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
                        try { code = p.ExitCode; }
                        catch
                        {
                            // 理由: 子进程可能在读取退出码前已退出，读取失败回退默认-1，仅用于"就绪前早退"提示
                        }
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
                catch
                {
                    // 理由: 超时清理时端口可能已被释放，确保端口空闲属尽力而为，失败不影响已报告的超时结果
                }
                try { p.Kill(entireProcessTree: true); }
                catch
                {
                    // 理由: 就绪超时后子进程可能已自行退出，Kill 失败既无必要也无副作用，尽力清理
                }
                lock (_gate) { if (_childPid == pid) { _child = null; _childPid = 0; _mine = false; } }
                SetState(BackendState.Stopped, false, 0);
            }
            catch (OperationCanceledException)
            {
                // 停止引发的取消：静默退出（StopCoreAsync 后续收尾）
                // 理由: 停止流程主动取消就绪等待属正常退出路径，静默返回交由调用方收尾
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
            try { if (_pumpCts != null) _pumpCts.Cancel(); }
            catch
            {
                // 理由: 重启泵时取消旧的取消源，可能已释放，静默忽略，随后新建替身
            }
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
                catch (OperationCanceledException)
                {
                    // 理由: 输出泵被主动取消视为正常结束，静默退出不再消费输出
                }
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
                // 无捕获组的正则取整个匹配（m.Groups[1] 不存在会抛异常，
                // 曾导致公告 URL 捕获静默失效，v0.5.0 修复）
                Match m = Regex.Match(line, @"https?://[^\s]+");
                if (m.Success)
                {
                    string u = m.Value;
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
            catch
            {
                // 理由: 捕获公告 URL 失败只是少一条提示，不影响输出转发主流程，静默忽略
            }
            try { if (_output != null) _output.Writer.TryWrite(tagged); }
            catch
            {
                // 理由: 通道可能在输出泵已取消/关闭后写入，TryWrite 失败即无人消费，静默忽略
            }
        }

        private void OnChildExited(Process p)
        {
            try
            {
                int pid = 0;
                try { pid = p.Id; }
                catch
                {
                    // 理由: 进程对象可能已释放，读取 Id 失败回退默认0，后续按 current 校验会排除该对象
                }
                int code = -1;
                try { code = p.ExitCode; }
                catch
                {
                    // 理由: 子进程可能在读取退出码前已退出释放，读取失败回退默认-1，仅用于退出提示
                }
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
            catch
            {
                // 理由: 进程退出回调内异常会逃逸成未处理异常，静默忽略，状态已在上层按 current 校验
            }
        }

    }
}
