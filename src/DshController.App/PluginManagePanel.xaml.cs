// ============================================================================
//  PluginManagePanel — 已装插件管理（重构 2.0 / P3 重写）
//
//  这一版把"目标实例 + profile + 版本 + 已装状态"整块交给共享的
//  PluginTargetViewModel（与插件市场页同一份实现，原来两边逐字重复 14 个方法），
//  面板自身只剩：渲染列表、升级/卸载的编排、以及重启提示。
//  数据一律来自实例档案；确认/提示对话框走统一的 DialogService。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using DshController.Core;
using DshController.Core.Archive;
using DshController.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DshController
{
    /// <summary>已装插件列表条目（x:Bind 数据源；状态变化靠重建列表）。</summary>
    public sealed class ManageItem
    {
        public InstalledPlugin Plugin { get; set; }

        // ---- 升级提示（改版·升级详情弹窗）：仅"目录找到来源+市场版本更高+未被忽略"时展示 ----
        public string MarketVersion { get; set; } = "";
        public string Changelog { get; set; } = "";
        public string IgnoredUpTo { get; set; } = "";

        /// <summary>探测命中（可升级且未忽略）才显示「升级」钮。</summary>
        public bool HasUpgradeHint { get { return !string.IsNullOrEmpty(MarketVersion); } }

        public string Pkg => Plugin?.Pkg ?? "";

        public string VersionText => string.IsNullOrEmpty(Plugin?.Version) ? "版本未知" : "v" + Plugin.Version;

        public string DepText
        {
            get
            {
                string dep = Plugin?.DepRef ?? "";
                return dep.Length > 0 ? "依赖声明: " + dep : "";
            }
        }

        public Visibility BundleVis => Plugin != null && Plugin.InBundles ? Visibility.Visible : Visibility.Collapsed;

        public Visibility MarketVis => Plugin != null && Plugin.SourceMark == "market"
            ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>非市场来源（官方/手动/本地链接）的徽标。</summary>
        public Visibility OtherSourceVis => Plugin != null && Plugin.SourceMark != "market"
            ? Visibility.Visible : Visibility.Collapsed;

        public string SourceText => Plugin?.SourceText ?? "";

        public string MarketMeta
        {
            get
            {
                if (Plugin == null || Plugin.SourceMark != "market") return "";
                string repo = (Plugin.Repo ?? "").Trim();
                string name = (Plugin.MarketName ?? "").Trim();
                string tail = repo.Length > 0 ? " · github.com/" + repo : "";
                return "来自插件市场" + (name.Length > 0 && name != Plugin.Pkg ? "：" + name : "") + tail;
            }
        }

        /// <summary>官方基础包随 harness 版本管理，不允许通过插件命令升级/卸载。</summary>
        public bool CanUpgrade => Plugin != null && !Plugin.IsOfficial;

        public bool CanUninstall => Plugin != null && !Plugin.IsOfficial;

        public string UninstallTip
        {
            get
            {
                if (Plugin == null) return "";
                if (Plugin.IsOfficial) return "官方基础包随 harness 版本管理，禁止卸载";
                return "dsh plugin remove " + Plugin.Pkg + "（官方命令；bundle 插件卸载后需重启实例）";
            }
        }
    }

    public sealed partial class PluginManagePanel : UserControl
    {
        private InstanceRegistry _registry;
        private ArchiveHub _archive;
        private PluginTargetViewModel _target;
        private PluginOpsService _ops;
        private DialogService _dialogs;
        private UpgradeIgnoreStore _ignores;      // 改版·升级详情弹窗：忽略本次升级的台账
        private Action<string> _console;
        private bool _closing;
        private bool _syncingSelection;

        public PluginManagePanel()
        {
            InitializeComponent();
        }

        /// <summary>MainWindow 在构造后调用：注入依赖。</summary>
        public void Init(InstanceRegistry registry, InstanceManager instanceMgr, Action<string> console,
            ArchiveHub archive)
        {
            _registry = registry;
            _archive = archive;
            _console = console;
            _dialogs = new DialogService(() => XamlRoot);
            _ops = new PluginOpsService(registry, instanceMgr, archive, _dialogs, PushLog);
            _ignores = new UpgradeIgnoreStore();
            _ignores.Load();

            _target = new PluginTargetViewModel(archive);
            _target.DataChanged += (s, e) => RenderList();
            _target.PropertyChanged += (s, e) => ApplyTargetState(e.PropertyName);
            CmbInstance.ItemsSource = _target.Instances;

            _target.RefreshInstances();
            SyncPicker();
            ShowEmpty("选择一个实例后查看其已装插件。");
        }

        public void Shutdown() => _closing = true;

        /// <summary>共享目标视图模型（改版·管理实例树：左栏树与本页面同一份状态）。</summary>
        public DshController.ViewModels.PluginTargetViewModel Target => _target;

        /// <summary>左栏树选中实例：写共享 VM（combo 经 SyncPicker 自动跟随），随后刷新已装列表。</summary>
        public void SelectFromTree(InstanceDef def)
        {
            if (_closing || _target == null || def == null || def == _target.SelectedInstance) return;
            _target.SelectedInstance = def;           // PropertyChanged → SyncPicker + Meta
            _ = _target.ReloadAsync();                // 命中档案秒回，未命中走采集
        }

        /// <summary>MainWindow 切到本页时调用：刷新实例列表与已装状态（命中档案时秒回）。</summary>
        public void OnShown()
        {
            if (_closing || _target == null || _target.IsBusy) return;
            _target.RefreshInstances();
            SyncPicker();
            _ = _target.ReloadAsync();
        }

        // ==================== 视图状态同步 ====================

        private void ApplyTargetState(string propertyName)
        {
            if (_closing) return;
            switch (propertyName)
            {
                case nameof(PluginTargetViewModel.IsBusy):
                    BusyRing.IsActive = _target.IsBusy;
                    BtnRefresh.IsEnabled = !_target.IsBusy;
                    CmbInstance.IsEnabled = !_target.IsBusy;
                    ListPlugins.IsEnabled = !_target.IsBusy;
                    break;
                case nameof(PluginTargetViewModel.StatusText):
                    TxtStatus.Text = _target.StatusText;
                    break;
                case nameof(PluginTargetViewModel.MetaText):
                    TxtInstanceMeta.Text = BuildMeta();
                    break;
                case nameof(PluginTargetViewModel.SelectedInstance):
                    SyncPicker();
                    TxtInstanceMeta.Text = BuildMeta();
                    break;
            }
        }

        /// <summary>信息条 = 共享视图模型的版本/包数 + 本页关心的 HOME 展示。</summary>
        private string BuildMeta()
        {
            InstanceDef def = _target.SelectedInstance;
            if (def == null) return "";
            Config cfg = def.ToConfig(_registry.Settings);
            string home = def.IsWsl ? PluginUiHelper.WslHomeDisplay(cfg) : PluginUiHelper.WindowsHomeDisplay(cfg);
            return _target.MetaText.Replace(" · 已装", " · HOME: " + home + " · 已装");
        }

        private void SyncPicker()
        {
            _syncingSelection = true;
            try { CmbInstance.SelectedItem = _target.SelectedInstance; }
            finally { _syncingSelection = false; }
        }

        private async void CmbInstance_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncingSelection || _closing) return;
            if (CmbInstance.SelectedItem is InstanceDef def && def != _target.SelectedInstance)
            {
                _target.SelectedInstance = def;
                await _target.ReloadAsync();
            }
        }

        // ==================== 列表渲染 ====================

        private void RenderList()
        {
            if (_closing) return;
            TxtInstanceMeta.Text = BuildMeta();
            // 探测移到 ItemsSource 赋值后（开头时列表还旧/空，rows==null 会空跑）

            if (_target.SelectedInstance == null)
            {
                ShowEmpty("没有可用实例：请先在「Windows 实例 / WSL 实例」页创建一个实例。");
                return;
            }
            if (_target.EffectiveProfile == null)
            {
                ShowEmpty("profile 含非法字符（仅允许字母、数字、-、_、.）。");
                return;
            }
            if (_target.Installed.Count == 0)
            {
                ShowEmpty(!_target.HomeInitialized
                    ? "该实例的 HOME 尚未生成 profile（首次启动 dsh 后自动创建）。安装插件或先启动一次实例后再来查看。"
                    : "该实例的 profile「" + _target.EffectiveProfile + "」下没有已安装的插件。可到「插件市场」搜索安装。");
                return;
            }
            HideEmpty();
            ListPlugins.ItemsSource = _target.Installed.Select(p => new ManageItem
            {
                Plugin = p,
                MarketVersion = HintFor(p.Pkg).MarketVersion,       // 升级提示：探测结果注入（无则空 → 按钮隐藏）
                IgnoredUpTo = HintFor(p.Pkg).IgnoredUpTo
            }).ToList();
            if (!_suppressProbe) _ = ProbeUpgradeHintsAsync();     // 列表就绪后才探测（旧实现放开头=白跑）
        }

        // ==================== 升级 / 卸载 ====================

        // 旧「升级=直接跑 Update」入口退役（改版·升级详情弹窗）：现在先开详情窗，三选项后再走 ops 通道。

        private async void BtnUninstall_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is ManageItem item) await RunOpAsync(item, PluginOp.Remove);
        }

        private async Task RunOpAsync(ManageItem item, PluginOp op)
        {
            if (_target.IsBusy) return;
            InstanceDef def = _target.SelectedInstance;
            string profile = _target.EffectiveProfile;
            if (def == null || item?.Plugin == null) return;
            if (profile == null)
            {
                await _dialogs.InfoAsync("profile 只允许字母、数字、-、_、.（默认 web）。", "profile 非法");
                return;
            }
            if (item.Plugin.IsOfficial)
            {
                await _dialogs.InfoAsync("官方基础包（" + item.Plugin.Pkg + "）随 harness 版本管理，" +
                                         "不能通过插件命令" + PluginOpText.Label(op) + "。", "不允许的操作");
                return;
            }

            var msg = new StringBuilder();
            msg.AppendLine("包：" + item.Plugin.Pkg +
                           (string.IsNullOrEmpty(item.Plugin.Version) ? "" : "（v" + item.Plugin.Version + "）"));
            msg.AppendLine("实例：" + DshController.ViewModels.InstanceDisplayName.For(def) +
                    "（" + DshController.ViewModels.InstanceDisplayName.Original(def) + " · profile " + profile + "）");
            msg.AppendLine("命令：dsh plugin --profile " + profile + " " + PluginOpText.Verb(op) + " " + item.Plugin.Pkg);
            msg.AppendLine();
            msg.AppendLine(op == PluginOp.Remove
                ? "卸载会移除该 profile 对此插件的依赖与 bundle 声明，此操作不可撤销。确认卸载？"
                : "升级 bundle 插件后需重启实例才会生效。继续？");
            if (!await _dialogs.ConfirmAsync(msg.ToString(),
                    op == PluginOp.Remove ? "卸载插件" : "升级插件")) return;

            SetOpBusy(true, "正在" + PluginOpText.Label(op) + " " + item.Plugin.Pkg + " …（输出见控制台）");
            bool ok;
            try
            {
                ok = await _ops.RunAsync(def, op, profile, item.Plugin.Pkg);
            }
            finally
            {
                SetOpBusy(false, "");
            }
            if (!ok) return;

            if (op == PluginOp.Remove)
            {
                PluginRecords.Remove(def.Id, item.Plugin.Pkg);
                PushLog("[插件] 已清除市场安装记录（如有）：" + item.Plugin.Pkg);
            }
            await _target.ReloadAfterPluginOpAsync();
            if (op == PluginOp.Update) WriteBackMarketVersion(def, item.Plugin.Pkg);
            await _ops.PromptRestartAsync(def, PluginOpText.Label(op), item.Plugin.Pkg);
        }

        /// <summary>升级后把市场记录里的版本号回写为档案中实测到的新版本。</summary>
        private void WriteBackMarketVersion(InstanceDef def, string pkg)
        {
            string newVer = _target.Installed
                .FirstOrDefault(p => string.Equals(p.Pkg, pkg, StringComparison.OrdinalIgnoreCase))?.Version ?? "";
            List<PluginRecord> records = PluginRecords.Load(def.Id);
            PluginRecord rec = PluginRecords.Find(records, pkg);
            if (rec == null) return;
            rec.Version = newVer;
            PluginRecords.Save(def.Id, records);
            PushLog("[插件] 市场记录版本已更新：" + pkg + (newVer.Length > 0 ? " → v" + newVer : ""));
        }

        // ==================== 小件 ====================

        private void BtnRefresh_Click(object sender, RoutedEventArgs e)
        {
            if (!_target.IsBusy) _ = _target.ForceRefreshAsync();
        }

        private void SetOpBusy(bool busy, string text)
        {
            BusyRing.IsActive = busy;
            TxtStatus.Text = text ?? "";
            BtnRefresh.IsEnabled = !busy;
            CmbInstance.IsEnabled = !busy;
            ListPlugins.IsEnabled = !busy;
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
            try { _console?.Invoke(line); }
            catch { /* 理由: 控制台回调失败不应影响插件操作本身 */ }
        }
    }
}
