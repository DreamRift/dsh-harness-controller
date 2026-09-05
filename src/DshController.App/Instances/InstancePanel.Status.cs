// ============================================================================
//  InstancePanel — 状态刷新与启停操作
//
//  状态卡渲染、按钮使能矩阵、启动/停止/重启/打开界面。
//  （重构 2.0 / P3：InstancePanel 按职责拆成多个 partial 文件，单文件不超过 400 行；
//   纯逻辑已抽到 ViewModels 层的 InstanceSettingsValidator / InstancePlanFactory。）
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DshController.Core;
using DshController.Core.Archive;
using DshController.Core.Storage;
using DshController.ViewModels;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;

namespace DshController
{
    public sealed partial class InstancePanel : UserControl
    {
        // ==================== 状态刷新 ====================

        private BackendState CurrentStateFor(InstanceDef def) { return _instanceMgr.For(def.Id).State; }
        private bool CurrentMineFor(InstanceDef def) { return _instanceMgr.For(def.Id).IsMine; }
        private int CurrentPidFor(InstanceDef def) { return _instanceMgr.For(def.Id).ChildPid; }

        private async Task ProbeTickAsync()
        {
            if (_closing || !_probeGate.Wait(0)) return;
            try
            {
                InstanceDef def = SelectedDef();
                if (def == null) return;

                string probeId = def.Id;
                BackendState s = _instanceMgr.For(probeId).State;
                if (s == BackendState.Starting || s == BackendState.Stopping || s == BackendState.Restarting)
                    return;

                bool up = await PortTools.ProbeAsync(def.Host, def.Port).ConfigureAwait(true);
                int pid = 0;
                BackendManager mgr = _instanceMgr.For(probeId);
                bool mine = mgr.IsMine && up;
                if (up)
                {
                    pid = mine ? mgr.ChildPid : await PortTools.FindListenerPidAsync(def.Port).ConfigureAwait(true);
                    if (_cachedSelectedId == probeId) _externalPidCache = pid;
                }
                else if (_cachedSelectedId == probeId)
                {
                    _externalPidCache = 0;
                }

                var newState = up ? BackendState.Running : BackendState.Stopped;
                _dq.TryEnqueue(() =>
                {
                    if (_cachedSelectedId == probeId &&
                        ReferenceEquals(mgr, _instanceMgr.For(_selectedId)))
                        UpdateUiState(newState, mine, pid);
                });
            }
            catch (Exception ex)
            {
                // 理由: 端口探测失败（网络栈异常/实例正在重启）不应打断界面；记一行便于排查
                PushLog("[状态] 探测失败：" + ex.Message);
            }
            finally { _probeGate.Release(); }
        }

        private void UpdateUiState(BackendState state, bool mine, int pid)
        {
            if (_closing) return;
            bool changed = _uiState != state || _uiMine != mine;
            _uiState = state; _uiMine = mine;

            string label;
            Brush dotBrush;
            switch (state)
            {
                case BackendState.Starting:
                    label = "启动中…"; dotBrush = StateBrush("StateStartingColor"); break;
                case BackendState.Stopping:
                    label = "正在停止…"; dotBrush = StateBrush("StateStartingColor"); break;
                case BackendState.Restarting:
                    label = "重启中（不打开浏览器）…"; dotBrush = StateBrush("StateStartingColor"); break;
                case BackendState.Running:
                    label = mine ? "运行中 · 本程序启动" : "运行中 · 外部进程";
                    dotBrush = StateBrush("StateRunColor"); break;
                default:
                    label = "已停止"; dotBrush = StateBrush("StateStopColor"); break;
            }
            if (SelectedDef() == null)
            {
                label = "无实例，请新建";
                dotBrush = StateBrush("StateStopColor");
            }
            StatusText.Text = label;
            StatusDot.Background = dotBrush;

            if (state == BackendState.Running && !mine && pid == 0) pid = _externalPidCache;
            PidText.Text = state == BackendState.Running
                ? (mine ? "本程序 " + pid : (pid > 0 ? "外部 " + pid : "外部"))
                : "—";

            bool busy = state == BackendState.Starting || state == BackendState.Stopping || state == BackendState.Restarting;
            bool hasInstance = SelectedDef() != null;
            BtnStart.IsEnabled = hasInstance && state == BackendState.Stopped;
            BtnRestart.IsEnabled = hasInstance && state == BackendState.Running;
            BtnStop.IsEnabled = hasInstance && (state == BackendState.Running || state == BackendState.Starting);
            BtnOpen.IsEnabled = hasInstance;

            // 状态变化时同步标签页头（实例数/运行数）与页脚
            if (changed) NotifyInstancesChanged();
        }

        private Brush StateBrush(string colorKey)
        {
            try
            {
                string key = ActualTheme == ElementTheme.Dark ? "Dark" : "Light";
                foreach (ResourceDictionary md in Application.Current.Resources.MergedDictionaries)
                {
                    if (md.ThemeDictionaries.ContainsKey(key))
                    {
                        var td = md.ThemeDictionaries[key] as ResourceDictionary;
                        if (td != null && td.ContainsKey(colorKey))
                        {
                            var c = (Windows.UI.Color)td[colorKey];
                            return new SolidColorBrush(c);
                        }
                    }
                }
            }
            catch { /* 理由: 主题资源查找失败时回落到下面的中性灰，纯展示无副作用 */ }
            return new SolidColorBrush(Windows.UI.Color.FromArgb(255, 129, 133, 140));
        }

        // ==================== 操作 ====================

        private async void BtnStart_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_selectedId)) return;
            if (!TryReadSettings(showErrors: true)) return;
            SaveAllSettings();
            FailBar.IsOpen = false;                       // 新的一次启动：清掉上次失败提示
            await RunOpAsync(() => _instanceMgr.StartAsync(_selectedId));
        }

        private async void BtnRestart_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_selectedId) || _uiState != BackendState.Running) return;
            if (!TryReadSettings(showErrors: true)) return;

            bool mine = _uiMine;
            InstanceDef def = SelectedDef();
            if (!mine)
            {
                int pid = _externalPidCache;
                bool ok = await ConfirmAsync(
                    "检测到后端由外部进程" + (pid > 0 ? "（PID " + pid + "）" : "") + "提供。\n\n" +
                    "重启将结束该进程并由本程序重新启动后端。\n浏览器不会自动打开；现有页面刷新即可重连。是否继续？",
                    "确认重启外部后端");
                if (!ok) { PushLog("已取消重启外部后端。"); return; }
            }
            SaveAllSettings();
            await RunOpAsync(() => _instanceMgr.RestartAsync(_selectedId));
        }

        private async void BtnStop_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_selectedId)) return;
            if (_uiState != BackendState.Running && _uiState != BackendState.Starting) return;

            InstanceDef def = SelectedDef();
            bool mine = _uiMine;
            bool up = await PortTools.ProbeAsync(def.Host, def.Port);
            bool killExternal = false;
            if (!mine && up)
            {
                int pid = _externalPidCache;
                killExternal = await ConfirmAsync(
                    "检测到后端由外部进程" + (pid > 0 ? "（PID " + pid + "）" : "") + "提供。\n\n是否结束该进程及其子进程来停止后端？",
                    "确认停止外部后端");
                if (!killExternal) { PushLog("已取消停止外部进程。"); return; }
            }
            SaveAllSettings();
            await RunOpAsync(() => _instanceMgr.StopAsync(_selectedId, killExternal));
        }

        private void BtnOpen_Click(object sender, RoutedEventArgs e)
        {
            InstanceDef def = SelectedDef();
            if (def == null) return;
            OpenBrowser(PortTools.Url(def.Host, def.Port));
        }

        private async Task RunOpAsync(Func<Task<bool>> op)
        {
            try
            {
                UpdateUiStateBusySafe();
                await op();
            }
            catch (Exception ex)
            {
                PushLog("操作异常: " + ex.Message);
                try
                {
                    var cfg = _instanceMgr.ConfigFor(_selectedId);
                    ErrorReporter.WriteCrash(ex, "op", cfg);
                }
                catch { /* 理由: 崩溃报告本身失败时无处可报，上面的日志已经记录了原始异常 */ }
            }
        }

        private void UpdateUiStateBusySafe()
        {
            BtnStart.IsEnabled = false;
            BtnRestart.IsEnabled = false;
            BtnStop.IsEnabled = false;
        }

        private void OnBackendReady(object sender, ReadyEventArgs e)
        {
            if (_closing) return;
            // 就绪动作按"触发该事件的实例自身配置"执行，而不是当前选中实例
            bool autoOpen = SwAutoOpen.IsOn;
            InstanceDef def = DefForSender(sender);
            if (def != null) autoOpen = def.AutoOpenBrowser;
            if (e.SuppressAutoOpen)
            {
                PushLog("后端已就绪（重启路径：未打开浏览器）。浏览器中的旧页面刷新即可重连。");
            }
            else if (autoOpen)
            {
                OpenBrowser(e.Url);
            }
            else
            {
                PushLog("后端已就绪: " + e.Url + "（按实例设置未自动打开浏览器）");
            }
        }

        private void OpenBrowser(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                PushLog("已在默认浏览器打开: " + url);
            }
            catch (Exception ex)
            {
                PushLog("打开浏览器失败: " + ex.Message);
            }
        }

    }
}
