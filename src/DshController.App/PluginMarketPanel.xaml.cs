// ============================================================================
//  PluginMarketPanel — 插件市场交互（v0.6.1，code-behind，无 MVVM）
//
//  与 InstancePanel 同一套面板模式：MainWindow 构造后 Init 注入依赖，页面切换
//  只改可见性（OnShown 惰性加载目录）；控制台输出经回调汇入主窗口共享控制台。
//
//  v0.6.1 多来源：内置 官方全量 / GitHub精选（jsDelivr 镜像）/ GitHub实时 三个
//  来源可多选（「数据源」按钮），多选时按 npm 包名或 owner/repo 合并去重，
//  详情里可指定"从哪个源的条目安装"；自定义源 URL 保留（应用设置）。
//  分类标签全部中文（来源官方中文分类 + 内置映射，未知代码原样显示不编造）。
//
//  安装严格走 DSH 官方命令（Core/PluginInstaller）：dsh plugin --profile <p> add，
//  装到所选实例自己的 DSH_HOME（实例隔离）；安装成功后：
//    - 对比安装前后实例 HOME 的包集合，把新增包写入市场安装记录（PluginRecords）；
//    - bundle 插件提示重启，可一键重启（InstanceManager.RestartAsync）。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using DshController.Core;
using DshController.Core.Archive;
using DshController.Core.Storage;
using DshController.ViewModels;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DshController
{
    /// <summary>市场列表卡片条目（x:Bind 数据源；一次性绑定，状态变化靠重建列表）。</summary>
    public sealed class MarketItem
    {
        public CatalogEntry Entry { get; set; }
        public bool IsInstalled { get; set; }
        public string CompatText { get; set; } = "";

        public string Title
        {
            get
            {
                string n = (Entry?.Name ?? "").Trim();
                if (n.Length > 0) return n;
                string r = (Entry?.Repo ?? "").Trim();
                return r.Length > 0 ? r : "未命名插件";
            }
        }

        public string DescText
        {
            get
            {
                string d = (Entry?.Desc ?? "").Trim();
                if (d.Length > 0) return d;
                d = (Entry?.DescEn ?? "").Trim();
                if (d.Length > 0) return d;
                return "（无简介）";
            }
        }

        public string MetaText
        {
            get
            {
                var parts = new List<string>();
                if (Entry == null) return "";
                if (Entry.Stars > 0) parts.Add("★ " + Entry.Stars.ToString("N0"));
                string cat = string.IsNullOrWhiteSpace(Entry.CategoryLabel)
                    ? (Entry.Category ?? "").Trim()
                    : Entry.CategoryLabel;
                if (cat.Length > 0) parts.Add(cat);
                if (Entry.Downloads > 0) parts.Add("↓ " + Entry.Downloads.ToString("N0"));
                string up = (Entry.UpdatedAt ?? "").Trim();
                if (up.Length >= 10) parts.Add("更新 " + up.Substring(0, 10));
                return string.Join(" · ", parts);
            }
        }

        /// <summary>来源徽标（仅多来源合并时显示，如"官方全量+GitHub精选"）。</summary>
        public string SourceText
        {
            get
            {
                var src = Entry?.Sources;
                if (src == null || src.Count < 2) return "";
                return "来源 " + src.Count + " 源";
            }
        }

        public Visibility MultiSourceVis =>
            Entry?.Sources != null && Entry.Sources.Count > 1 ? Visibility.Visible : Visibility.Collapsed;

        public Visibility UnverifiedVis =>
            Entry != null && Entry.Unverified ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>支持版本行：仅当仓库/包元数据明确声明了版本时显示（未声明整行隐藏，不显示占位文案）。</summary>
        public Visibility CompatVis =>
            string.IsNullOrEmpty(CompatText) ? Visibility.Collapsed : Visibility.Visible;

        public string PkgText
        {
            get
            {
                string p = (Entry?.Pkg ?? "").Trim();
                if (p.Length > 0) return "npm 包: " + p;
                string r = (Entry?.Repo ?? "").Trim();
                return r.Length > 0 ? "来源: github:" + r.TrimStart('/') : "";
            }
        }

        public Visibility VerifiedVis =>
            Entry != null && Entry.Verified ? Visibility.Visible : Visibility.Collapsed;

        public Visibility BrowseVis =>
            Entry != null && !Entry.Installable ? Visibility.Visible : Visibility.Collapsed;

        public Visibility InstalledVis => IsInstalled ? Visibility.Visible : Visibility.Collapsed;

        public Visibility InstallVis =>
            Entry != null && Entry.Installable ? Visibility.Visible : Visibility.Collapsed;

        public string InstallLabel => IsInstalled ? "重新安装" : "安装";

        public bool InstallEnabled => Entry != null && Entry.Installable;

        public string InstallTip
        {
            get
            {
                if (Entry == null) return "";
                if (!Entry.Installable)
                    return "未确定安装来源（无 npm 包名且无仓库），仅可浏览";
                if (Entry.Unverified)
                    return "dsh plugin add " + Entry.InstallTarget + "（未审核条目，安装前会再确认）";
                return "dsh plugin add " + Entry.InstallTarget + "（官方方式，装到所选实例）";
            }
        }
    }

    public sealed partial class PluginMarketPanel : UserControl
    {
        private InstanceRegistry _registry;
        private InstanceManager _instanceMgr;
        private ArchiveHub _archive;                        // 重构 2.0：版本与已装状态来自实例档案
        private Action<string> _console;
        private DispatcherQueue _dq;

        private PluginTargetViewModel _target;              // 目标实例/profile/已装状态（与插件管理页共用）
        private PluginOpsService _ops;
        private DialogService _dialogs;
        private bool _closing;
        private bool _busy;
        private bool _syncingSelection;
        private bool _loadingCategory;
        private CatalogFile _catalog;                       // 已加载（合并去重）的目录数据


        public PluginMarketPanel()
        {
            InitializeComponent();
        }

        /// <summary>MainWindow 在构造后调用：注入依赖。</summary>
        public void Init(InstanceRegistry registry, InstanceManager instanceMgr, Action<string> console,
            ArchiveHub archive)
        {
            _registry = registry;
            _instanceMgr = instanceMgr;
            _archive = archive;
            _console = console;
            _dq = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            _dialogs = new DialogService(() => XamlRoot);
            _ops = new PluginOpsService(registry, instanceMgr, archive, _dialogs, PushLog);

            // 目标实例/profile/版本/已装状态与插件管理页共用同一份实现
            _target = new PluginTargetViewModel(archive);
            _target.DataChanged += (s, e) => { UpdateInstanceMeta(); RebuildList(); };
            _target.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(PluginTargetViewModel.SelectedInstance)) SyncPicker();
                if (e.PropertyName == nameof(PluginTargetViewModel.MetaText)) UpdateInstanceMeta();
            };
            CmbInstance.ItemsSource = _target.Instances;
            _target.RefreshInstances();
            SyncPicker();
            ShowEmpty("正在加载插件目录…数据超过设置的刷新间隔时自动重新联网拉取，失败用本地缓存兜底。");
        }

        /// <summary>窗口关闭时终止后台动作。</summary>
        public void Shutdown()
        {
            _closing = true;
        }

        /// <summary>MainWindow 切到本页时调用：按设置的刷新间隔自动更新目录，并刷新实例与已装状态。</summary>
        public void OnShown()
        {
            if (_closing || _busy) return;
            _target.RefreshInstances();
            SyncPicker();
            // 每次进入都走一次加载：缓存新鲜（未超过设置的自动刷新间隔）时零成本，
            // 超过间隔则自动重新联网拉取（拉取失败回退本地缓存）
            _ = ShowRefreshAsync();
        }

        /// <summary>进入本页的完整刷新：目录（按刷新间隔）→ 实例侧状态（命中档案时秒回）。</summary>
        private async Task ShowRefreshAsync()
        {
            await LoadCatalogAsync(force: false);
            if (_closing) return;
            await _target.ReloadAsync();
        }

        // ==================== 数据加载 ====================

        private async Task LoadCatalogAsync(bool force)
        {
            SetBusy(true, force ? "正在刷新插件目录…" : "正在加载插件目录…");
            MarketLoadResult r;
            try
            {
                r = await PluginCatalog.LoadAllAsync(_registry.Settings, force);
            }
            catch (Exception ex)
            {
                SetBusy(false, "");
                TxtFreshness.Text = "目录加载失败";
                ShowEmpty("插件目录暂时无法加载。请检查网络或数据源设置，然后点击「刷新」重试。\n\n" + ex.Message);
                PushLog("[市场] 目录加载失败：" + ex.Message);
                return;
            }
            SetBusy(false, "");
            _catalog = r.Catalog;
            TxtFreshness.Text = r.SummaryText;
            UpdateSourcesLabel();
            PushLog("[市场] " + r.SummaryText);
            if (_catalog == null || _catalog.Plugins.Count == 0)
            {
                ShowEmpty("插件目录加载失败：所有启用来源都不可用。请点「数据源」检查来源选择、" +
                          "点「刷新」重试，或在应用设置中更换自定义源。");
                return;
            }
            if (_catalog.Plugins.Count > 0)
                PushLog("[市场] 合并去重后共 " + _catalog.Plugins.Count + " 个插件");
            RebuildCategoryCombo();
            RebuildList();
        }

        /// <summary>分类下拉重建：合并数据里出现过的分类，中文标签展示，按条目数排序。</summary>
        private void RebuildCategoryCombo()
        {
            string selected = SelectedCategory();
            var items = new List<object> { new ComboBoxItem { Content = "全部分类", Tag = "" } };
            foreach (KeyValuePair<string, int> kv in PluginCatalog.DistinctCategories(_catalog))
            {
                var item = new ComboBoxItem
                {
                    Content = PluginCatalog.CategoryLabel(kv.Key),
                    Tag = kv.Key
                };
                ToolTipService.SetToolTip(item, kv.Key + " · " + kv.Value + " 个插件");
                items.Add(item);
            }
            _loadingCategory = true;
            try
            {
                CmbCategory.ItemsSource = items;
                ComboBoxItem match = items.OfType<ComboBoxItem>()
                    .FirstOrDefault(i => string.Equals(i.Tag as string, selected, StringComparison.OrdinalIgnoreCase));
                if (match != null) CmbCategory.SelectedItem = match;
                else CmbCategory.SelectedIndex = 0;
            }
            finally { _loadingCategory = false; }
        }
        // ==================== 通用小件 ====================

        private void SetBusy(bool busy, string text)
        {
            _busy = busy;
            BusyRing.IsActive = busy;
            TxtStatus.Text = text ?? "";
            BtnRefreshCatalog.IsEnabled = !busy;
            BtnSources.IsEnabled = !busy;
            CmbInstance.IsEnabled = !busy;
        }

        private void ShowEmpty(string text)
        {
            TxtEmpty.Text = text;
            EmptyState.Visibility = Visibility.Visible;
            ListPlugins.Visibility = Visibility.Collapsed;
        }

        private void HideEmpty()
        {
            EmptyState.Visibility = Visibility.Collapsed;
            ListPlugins.Visibility = Visibility.Visible;
        }

        private void PushLog(string line)
        {
            if (_closing) return;
            try { _console?.Invoke(line); } catch
            {
                // 理由: 日志回调由外部面板注入，面板可能已关闭导致 Invoke 抛异常，吞掉避免日志侧信道打断当前流程。
            }
        }

        // 确认/提示统一走 DialogService（原本两个插件面板各写一份）
        private Task<bool> ConfirmAsync(string message, string title) => _dialogs.ConfirmAsync(message, title);

        private Task InfoAsync(string message, string title) => _dialogs.InfoAsync(message, title);
    }
}
