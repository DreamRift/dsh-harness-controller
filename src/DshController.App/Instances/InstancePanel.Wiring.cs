// ============================================================================
//  InstancePanel — 实例过滤、事件接线与日志转发
//
//  后端事件（Log/输出/状态/就绪/失败/公告 URL）的订阅集中在这里；
//  订阅按实例 id 幂等，删除实例时随 BackendManager 一起释放。
//  （重构 2.0 / P3：InstancePanel 按职责拆成多个 partial 文件，单文件不超过 400 行。）
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
        // ==================== 实例过滤与事件接线 ====================

        private IEnumerable<InstanceDef> InstancesOfEnv()
        {
            return _registry.Instances.Where(d => d.IsWsl == IsWslPanel);
        }

        private void WireAll()
        {
            foreach (InstanceDef def in InstancesOfEnv()) WireInstance(def);
        }

        /// <summary>为清单内本环境、尚未接线的实例补接线。跨面板新增实例（如 WIN 面板
        /// 扫描发现 WSL 实例/已安装发行版）后由 UpdateFooter 汇聚点统一调用；
        /// WireInstance 内部有 _wired 去重，重复调用无副作用。</summary>
        public void EnsureWired()
        {
            if (_closing) return;
            WireAll();
        }

        private void WireInstance(InstanceDef def)
        {
            if (!_wired.Add(def.Id)) return;
            var mgr = _instanceMgr.For(def.Id);
            mgr.Log += (s, line) => PushLog(PrefixFor(s) + line);
            mgr.OutputBatched += (s, e) =>
            {
                string prefix = PrefixFor(s);
                foreach (string line in e.Lines) PushLog(prefix + line);
            };
            mgr.StateChanged += (s, e) => { if (IsSelectedManager(s)) UpdateUiState(e.State, e.Mine, e.Pid); };
            mgr.Ready += OnBackendReady;
            mgr.StartFailed += OnStartFailed;
            mgr.AnnouncedUrlChanged += (s, url) => { if (IsSelectedManager(s)) UpdateAnnounced(url); };
        }

        private bool IsSelectedManager(object sender)
        {
            if (_closing || string.IsNullOrEmpty(_selectedId)) return false;
            return ReferenceEquals(sender, _instanceMgr.For(_selectedId));
        }

        private InstanceDef DefForSender(object sender)
        {
            foreach (InstanceDef d in InstancesOfEnv())
            {
                if (ReferenceEquals(_instanceMgr.For(d.Id), sender)) return d;
            }
            return null;
        }

        private string PrefixFor(object sender)
        {
            InstanceDef def = DefForSender(sender);
            return def == null ? "" : "[" + (IsWslPanel ? "WSL·" : "WIN·") + def.Name + "] ";
        }

        private void PushLog(string line)
        {
            if (_closing) return;
            try { _console?.Invoke(line); }
            catch { /* 理由: 控制台回调失败不能影响后端操作本身 */ }
        }

        private InstanceDef SelectedDef()
        {
            if (!_registry.TryGet(_selectedId, out InstanceDef def)) return null;
            if (def.IsWsl != IsWslPanel) return null;
            return def;
        }

    }
}
