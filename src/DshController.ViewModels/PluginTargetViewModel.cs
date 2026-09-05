// ============================================================================
//  PluginTargetViewModel — 插件页共享的"目标实例 + profile + 已装状态"（重构 2.0 / P3）
//
//  插件市场页与插件管理页此前各自实现了同一套东西：实例下拉与同步、profile 归一、
//  版本探测与"检测中"文案、已装列表读取、忙态与过期结果丢弃……逐字重复 14 个方法。
//  现在只有这一份实现，两个页面共用；而且因为不依赖 WinUI，可以离线单测。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using DshController.Core;
using DshController.Core.Archive;

namespace DshController.ViewModels
{
    public partial class PluginTargetViewModel : ObservableObject
    {
        private readonly IArchiveFacade _archive;
        private int _reloadSeq;                 // 加载代号：快速切换实例时丢弃过期结果

        public PluginTargetViewModel(IArchiveFacade archive)
        {
            _archive = archive;
            Instances = new ObservableCollection<InstanceDef>();
            Installed = new List<InstalledPlugin>();
            InstalledPkgs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        public ObservableCollection<InstanceDef> Instances { get; }

        /// <summary>当前 profile 下的已装插件（来自档案）。</summary>
        public List<InstalledPlugin> Installed { get; private set; }

        public HashSet<string> InstalledPkgs { get; private set; }

        [ObservableProperty] public partial InstanceDef SelectedInstance { get; set; }
        [ObservableProperty] public partial string Profile { get; set; } = "web";
        [ObservableProperty] public partial bool IsBusy { get; set; }
        [ObservableProperty] public partial string StatusText { get; set; } = "";
        [ObservableProperty] public partial string MetaText { get; set; } = "";
        [ObservableProperty] public partial bool HomeInitialized { get; set; } = true;
        [ObservableProperty] public partial string HarnessVersion { get; set; } = "";

        /// <summary>已装状态或版本发生变化，视图据此重绘列表。</summary>
        public event EventHandler DataChanged;

        public bool HasInstance => SelectedInstance != null;

        /// <summary>归一化后的 profile；含非法字符时返回 null。</summary>
        public string EffectiveProfile => NormalizeProfile(Profile);

        /// <summary>profile 归一化：空 → web；只允许字母、数字、-、_、.（要拼进路径与 shell）。</summary>
        public static string NormalizeProfile(string profile)
        {
            string p = (profile ?? "").Trim();
            if (p.Length == 0) p = "web";
            foreach (char c in p)
                if (!(char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.')) return null;
            return p;
        }

        // ==================== 实例列表 ====================

        /// <summary>重建实例下拉；保持当前选中项，必要时回退到第一个。</summary>
        public void RefreshInstances()
        {
            string keep = SelectedInstance?.Id ?? "";
            List<InstanceDef> list = (_archive.Instances ?? new List<InstanceDef>())
                .OrderBy(d => d.IsWsl)
                .ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            Instances.Clear();
            foreach (InstanceDef d in list) Instances.Add(d);

            InstanceDef target = list.FirstOrDefault(d =>
                string.Equals(d.Id, keep, StringComparison.OrdinalIgnoreCase)) ?? list.FirstOrDefault();
            // 直接赋值即可：与旧实现不同，这里没有"事件被屏蔽导致默认实例没被选中"的坑
            SelectedInstance = target;
        }

        partial void OnSelectedInstanceChanged(InstanceDef value)
        {
            if (value != null) _archive.SetVisible(value.Id);
            UpdateMeta();
        }

        // ==================== 已装状态 ====================

        /// <summary>
        /// 从档案加载所选实例的版本与已装插件。force=true 时绕过档案强制重扫。
        /// 返回 false 表示因 profile 非法或没有实例而未加载。
        /// </summary>
        public async Task<bool> ReloadAsync(bool force = false)
        {
            InstanceDef def = SelectedInstance;
            if (def == null)
            {
                Installed = new List<InstalledPlugin>();
                InstalledPkgs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                MetaText = "";
                RaiseChanged();
                return false;
            }
            string profile = EffectiveProfile;
            if (profile == null)
            {
                StatusText = "profile 含非法字符（仅允许字母、数字、-、_、.）。";
                return false;
            }

            int seq = ++_reloadSeq;
            IsBusy = true;
            StatusText = force ? "正在重新扫描实例 HOME…" : "正在读取实例档案…";
            UpdateMeta();
            try
            {
                Task<string> versionTask = _archive.EnsureHarnessAsync(def.Id);
                List<InstalledPlugin> installed =
                    await _archive.EnsurePluginsAsync(def.Id, profile, force).ConfigureAwait(true) ??
                    new List<InstalledPlugin>();
                if (seq != _reloadSeq) return false;             // 已被更新的一次加载接管

                Installed = installed;
                InstalledPkgs = new HashSet<string>(installed.Select(p => p.Pkg), StringComparer.OrdinalIgnoreCase);

                HomeFacetData home = await _archive.EnsureHomeAsync(def.Id).ConfigureAwait(true);
                HomeInitialized = home == null || home.Initialized;

                HarnessVersion = await versionTask.ConfigureAwait(true) ?? "";
                if (seq != _reloadSeq) return false;
            }
            catch (Exception ex)
            {
                StatusText = "读取已装状态失败：" + ex.Message;
                return false;
            }
            finally
            {
                if (seq == _reloadSeq) IsBusy = false;
            }

            StatusText = "";
            UpdateMeta();
            RaiseChanged();
            return true;
        }

        /// <summary>插件装/卸/升级之后：让档案失效并强制重扫一次。</summary>
        public Task<bool> ReloadAfterPluginOpAsync()
        {
            if (SelectedInstance != null) _archive.OnPluginsChanged(SelectedInstance.Id);
            return ReloadAsync(force: true);
        }

        /// <summary>手动刷新：版本 / HOME / 插件全部重采。</summary>
        public Task<bool> ForceRefreshAsync()
        {
            if (SelectedInstance != null)
                _archive.Invalidate(SelectedInstance.Id, FacetNames.Harness, FacetNames.Home, FacetNames.Plugins);
            return ReloadAsync(force: true);
        }

        /// <summary>信息条文案：版本 · HOME · 已装包数。未探测完时如实说"正在检测"。</summary>
        public void UpdateMeta()
        {
            InstanceDef def = SelectedInstance;
            if (def == null)
            {
                MetaText = "";
                return;
            }
            bool known = _archive.HarnessKnown(def.Id);
            string version = known ? _archive.HarnessVersion(def.Id) : "";
            string versionText = !known
                ? "正在检测 harness 版本…"
                : (version.Length > 0 ? "harness v" + version : "harness 版本未检测");
            MetaText = versionText + " · 已装 " + Installed.Count + " 个包";
        }

        private void RaiseChanged()
        {
            try { DataChanged?.Invoke(this, EventArgs.Empty); }
            catch
            {
                // 理由: 订阅方（视图）异常不得影响数据加载流程
            }
        }
    }
}
