// ============================================================================
//  BackendManager · 停止链路（partial 分部；纯搬移，语义与拆分前逐字一致）
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
        private async Task<bool> StopCoreAsync(Config cfg, bool killExternal, BackendState intermediateState)
        {
            // WSL 实例走独立停止路径（kill wsl.exe 宿主 → 发行版内 pidfile/进程组 → 智能关闭）
            if (cfg.IsWsl)
            {
                return await StopWslCoreAsync(cfg, killExternal, intermediateState).ConfigureAwait(false);
            }

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
            try { if (_readyCts != null) _readyCts.Cancel(); }
            catch
            {
                // 理由: 取消就绪等待取消源，可能已释放，静默忽略（停止路径属正常流程）
            }
            if (_readyTask != null)
            {
                try { await Task.WhenAny(_readyTask, Task.Delay(8000)).ConfigureAwait(false); }
                catch
                {
                    // 理由: _readyTask 可能已被取消，等待至多8秒，异常/取消均属停止流程，静默跳过
                }
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

            try { if (mine != null && !mine.WaitForExit(8000)) { } }
            catch
            {
                // 理由: 等待退出可能抛异常（进程未启动/已释放），等待失败可接受，交由后续端口探测判断
            }
            try { if (mine != null) mine.Dispose(); }
            catch
            {
                // 理由: 释放进程对象，若已被释放则静默忽略，避免停止路径二次释放
            }
            lock (_gate) { _child = null; _childPid = 0; _mine = false; }

            bool down = !await PortTools.ProbeAsync(cfg.Host, cfg.Port).ConfigureAwait(false);
            LogUi(down ? "后端已停止，端口已释放。" : "端口仍在监听，后端可能未完全退出。");
            SetState(BackendState.Stopped, false, 0);
            return down;
        }

        private void FailStop(Config cfg, string kind, string summary, string extra)
        {
            LogUi("停止失败：" + kind + "。" + summary);

            var ctx = new StartFailureContext
            {
                FailureKind = kind,
                Summary = summary,
                Exception = null,
                Config = cfg,
                Trace = null,
                CapturedOutput = RecentOutput(100),
                ConsoleLog = RecentConsole(200),
                Diagnostics = DiagSnapshot(),
                InstanceId = cfg?.InstanceId ?? "",
                InstanceHome = cfg?.Home ?? "",
                Phase = "stop",
                ExitCode = null,
                Extra = "```\n" + (extra ?? "") + "\n```"
            };
            try
            {
                ctx.ReportPath = ErrorReporter.WriteStartFailure(ctx);
                if (!string.IsNullOrEmpty(ctx.ReportPath))
                    LogUi("已生成停止失败报告: " + ctx.ReportPath);
            }
            catch
            {
                // 理由: 失败报告本身属兜底路径，生成失败已无处再报；StartFailed 事件照常发出
            }
            RunOnUi(() =>
            {
                var h = StartFailed;
                if (h != null) h(this, ctx);
            });
        }

    }
}
