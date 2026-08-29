// ============================================================================
//  PluginMarketPanel — 插件市场交互（v0.6.1，code-behind，无 MVVM）
//
//  与 InstancePanel 同一套面板模式：MainWindow 构造后 Init 注入依赖，页面切换
//  只改可见性（OnShown 惰性加载目录）；控制台输出经回调汇入主窗口共享控制台。
//
//  v0.6.1 多来源：内置 官方全量 / GitHub精选（jsDelivr 镜像）/ GitHub实时 三个
//  来源可多选（「数据源」按钮），多选时按 npm 包名或 owner/repo 合并去重，
//  详情里可指定"从哪个源的条目安装"；自定义源 URL 保留（全局设置）。
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
        private Action<string> _console;
        private DispatcherQueue _dq;

        private bool _closing;
        private bool _busy;
        private bool _catalogStarted;
        private bool _loadingInstances;
        private bool _loadingCategory;
        private CatalogFile _catalog;                       // 已加载（合并去重）的目录数据
        private string _instanceId = "";
        private string _instanceVersion = "";               // 所选实例的 harness 版本
        private List<InstalledPlugin> _installed = new List<InstalledPlugin>();
        private HashSet<string> _installedPkgs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public PluginMarketPanel()
        {
            InitializeComponent();
        }

        /// <summary>MainWindow 在构造后调用：注入依赖。</summary>
        public void Init(InstanceRegistry registry, InstanceManager instanceMgr, Action<string> console)
        {
            _registry = registry;
            _instanceMgr = instanceMgr;
            _console = console;
            _dq = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            RefreshInstances();
            ShowEmpty("正在加载插件目录…首次进入需要联网，之后 24 小时内复用缓存。");
        }

        /// <summary>窗口关闭时终止后台动作。</summary>
        public void Shutdown()
        {
            _closing = true;
        }

        /// <summary>MainWindow 切到本页时调用：惰性加载目录 / 刷新实例与已装状态。</summary>
        public void OnShown()
        {
            if (_closing || _busy) return;
            RefreshInstances();
            if (!_catalogStarted)
            {
                _catalogStarted = true;
                _ = LoadCatalogAsync(force: false);
            }
            else
            {
                _ = RefreshInstalledAsync();
            }
        }

        // ==================== 数据加载 ====================

        private async Task LoadCatalogAsync(bool force)
        {
            SetBusy(true, force ? "正在刷新插件目录…" : "正在加载插件目录…");
            MarketLoadResult r = await PluginCatalog.LoadAllAsync(_registry.Settings, force);
            SetBusy(false, "");
            _catalog = r.Catalog;
            TxtFreshness.Text = r.SummaryText;
            UpdateSourcesLabel();
            PushLog("[市场] " + r.SummaryText);
            if (_catalog == null || _catalog.Plugins.Count == 0)
            {
                ShowEmpty("插件目录加载失败：所有启用来源都不可用。请点「数据源」检查来源选择、" +
                          "点「刷新目录」重试，或在全局设置中更换自定义源。");
                return;
            }
            if (_catalog.Plugins.Count > 0)
                PushLog("[市场] 合并去重后共 " + _catalog.Plugins.Count + " 个插件");
            RebuildCategoryCombo();
            if (SelectedDef() == null) _instanceVersion = "";
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

        // ==================== 数据源选择 ====================

        private async void BtnSources_Click(object sender, RoutedEventArgs e)
        {
            List<string> current = _registry.Settings.PluginSources;
            var selected = new HashSet<string>((current == null || current.Count == 0)
                ? new[] { "official", "curated" } : current, StringComparer.OrdinalIgnoreCase);

            var panel = new StackPanel { Spacing = 10, MinWidth = 460, MaxWidth = 560 };
            var boxes = new Dictionary<string, CheckBox>(StringComparer.OrdinalIgnoreCase);
            foreach (MarketSource s in MarketSources.BuiltIn)
            {
                var box = new CheckBox
                {
                    IsChecked = selected.Contains(s.Id),
                    Content = BuildSourceCaption(s.Name, s.Note)
                };
                panel.Children.Add(box);
                boxes[s.Id] = box;
            }
            panel.Children.Add(new TextBlock
            {
                Text = "多选时按 npm 包名 / GitHub 仓库合并去重，详情里可选择从哪个源的条目安装。",
                FontSize = 11.5,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Application.Current.Resources["LabelTertiaryBrush"] as Brush
            });

            var txtCustom = new TextBox
            {
                Text = _registry.Settings.PluginRegistryUrl ?? "",
                PlaceholderText = "留空 = 不使用自定义源",
                Style = (Style)Application.Current.Resources["InputBox"]
            };
            panel.Children.Add(new TextBlock
            {
                Text = "自定义源 URL（可选，兼容官方快照同构 JSON 或旧接口规范）：",
                FontSize = 12,
                Foreground = Application.Current.Resources["LabelSecondaryBrush"] as Brush
            });
            panel.Children.Add(txtCustom);

            var dlg = new ContentDialog
            {
                Title = "目录数据源",
                Content = panel,
                PrimaryButtonText = "保存并重新加载",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = XamlRoot
            };
            if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

            var ids = boxes.Where(kv => kv.Value.IsChecked == true).Select(kv => kv.Key).ToList();
            _registry.Settings.PluginSources = ids;
            _registry.Settings.PluginRegistryUrl = txtCustom.Text.Trim();
            try { _registry.Save(); } catch { }
            UpdateSourcesLabel();
            PushLog("[市场] 数据源已更新：" +
                    (ids.Count > 0 ? string.Join("、", ids) : "（无内置来源）") +
                    (_registry.Settings.PluginRegistryUrl.Length > 0 ? " + 自定义源" : ""));
            await LoadCatalogAsync(force: true);
        }

        private static StackPanel BuildSourceCaption(string name, string note)
        {
            var sp = new StackPanel { Spacing = 1 };
            sp.Children.Add(new TextBlock
            {
                Text = name,
                FontSize = 13,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = Application.Current.Resources["LabelPrimaryBrush"] as Brush
            });
            sp.Children.Add(new TextBlock
            {
                Text = note,
                FontSize = 11.5,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Application.Current.Resources["LabelTertiaryBrush"] as Brush
            });
            return sp;
        }

        private void UpdateSourcesLabel()
        {
            List<MarketSource> chosen = PluginCatalog.SelectSources(_registry.Settings);
            TxtSourcesLabel.Text = "数据源(" + chosen.Count + ")";
        }

        // ==================== 实例选择 ====================

        private InstanceDef SelectedDef()
        {
            if (_registry == null || string.IsNullOrEmpty(_instanceId)) return null;
            _registry.TryGet(_instanceId, out InstanceDef def);
            return def;
        }

        private void RefreshInstances()
        {
            if (_registry == null) return;
            var list = _registry.Instances
                .OrderBy(d => d.IsWsl)
                .ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            _loadingInstances = true;
            try
            {
                CmbInstance.ItemsSource = list;
                InstanceDef target = string.IsNullOrEmpty(_instanceId)
                    ? list.FirstOrDefault()
                    : list.FirstOrDefault(d => string.Equals(d.Id, _instanceId, StringComparison.OrdinalIgnoreCase));
                if (target != null) CmbInstance.SelectedItem = target;
                else _instanceId = "";
                UpdateInstanceMeta();
            }
            finally { _loadingInstances = false; }
        }

        private async void CmbInstance_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loadingInstances || _closing) return;
            if (CmbInstance.SelectedItem is InstanceDef def && !string.Equals(def.Id, _instanceId, StringComparison.OrdinalIgnoreCase))
            {
                _instanceId = def.Id;
                UpdateInstanceMeta();
                TxtInstanceMeta.Text = "正在检测实例 harness 版本…";
                _instanceVersion = await PluginUiHelper.DetectVersionAsync(_registry, def);
                UpdateInstanceMeta();
                _ = RefreshInstalledAsync();
                RebuildList();
            }
        }

        private void UpdateInstanceMeta()
        {
            InstanceDef def = SelectedDef();
            if (def == null)
            {
                TxtInstanceMeta.Text = "";
                return;
            }
            Config cfg = def.ToConfig(_registry.Settings);
            string home = def.IsWsl ? PluginUiHelper.WslHomeDisplay(cfg) : PluginUiHelper.WindowsHomeDisplay(cfg);
            TxtInstanceMeta.Text = (_instanceVersion.Length > 0 ? "harness v" + _instanceVersion : "harness 版本未检测") +
                                   " · HOME: " + home;
        }

        /// <summary>刷新所选实例（当前 profile）的已装插件与市场记录。</summary>
        private async Task RefreshInstalledAsync()
        {
            InstanceDef def = SelectedDef();
            if (def == null) return;
            string profile = EffectiveProfile();
            if (profile == null) return;
            try
            {
                Config cfg = def.ToConfig(_registry.Settings);
                List<PluginRecord> records = PluginRecords.Load(def.Id);
                List<InstalledPlugin> installed;
                if (def.IsWsl)
                {
                    string home = await PluginUiHelper.ResolveEffectiveHomeAsync(cfg);
                    if (home == null)
                    {
                        string root = await WslTools.GetDistroHomeAsync(cfg.WslDistro ?? "");
                        home = WslTools.ResolveLinuxPath("", string.IsNullOrEmpty(root) ? "/root" : root);
                    }
                    installed = await InstalledPlugins.ReadWslAsync(cfg.WslDistro ?? "", home, profile, records);
                }
                else
                {
                    string home = string.IsNullOrWhiteSpace(cfg.Home)
                        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh")
                        : cfg.Home;
                    installed = await InstalledPlugins.ReadWindowsAsync(home, profile, records);
                }
                _installed = installed ?? new List<InstalledPlugin>();
                _installedPkgs = new HashSet<string>(
                    _installed.Select(p => p.Pkg), StringComparer.OrdinalIgnoreCase);
            }
            catch
            {
                _installed = new List<InstalledPlugin>();
                _installedPkgs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }
            RebuildList();
        }

        // ==================== 列表重建与过滤 ====================

        private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e) => RebuildList();

        private void Filter_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loadingCategory) return;
            RebuildList();
        }

        private void Filter_Toggled(object sender, RoutedEventArgs e) => RebuildList();

        private string SelectedCategory()
        {
            return (CmbCategory.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        }

        private bool SortIsStars()
        {
            return ((CmbSort.SelectedItem as ComboBoxItem)?.Tag as string ?? "stars") == "stars";
        }

        private string EffectiveProfile()
        {
            return PluginUiHelper.NormalizeProfile(TxtProfile.Text);
        }

        private void RebuildList()
        {
            if (_catalog == null || _closing) return;
            string profile = EffectiveProfile();
            if (profile == null)
            {
                ShowEmpty("profile 含非法字符（仅允许字母、数字、-、_、.），无法解析已装状态。");
                return;
            }
            HideEmpty();

            var f = new PluginCatalog.FilterOptions
            {
                Keyword = TxtSearch.Text,
                Category = SelectedCategory(),
                OnlyInstallable = ChkInstallable.IsChecked == true,
                OnlyVerified = ChkVerified.IsChecked == true,
                SortByStars = SortIsStars(),
                Max = 300
            };
            List<CatalogEntry> entries = PluginCatalog.Filter(_catalog, f);

            var items = new List<MarketItem>();
            foreach (CatalogEntry en in entries)
            {
                CompatInfo ci = PluginCompat.Judge(en, _instanceVersion);
                items.Add(new MarketItem
                {
                    Entry = en,
                    IsInstalled = IsEntryInstalled(en),
                    // 未声明支持版本的条目整行隐藏（用户要求：声明了才显示）
                    CompatText = ci.State == CompatState.NotDeclared ? "" : ci.Text
                });
            }
            ListPlugins.ItemsSource = items;

            if (items.Count == 0)
                ShowEmpty("没有匹配的插件。换个关键词，或放宽「仅可安装 / 仅人工复核」过滤。");
        }

        private bool IsEntryInstalled(CatalogEntry en)
        {
            string pkg = (en?.Pkg ?? "").Trim();
            if (pkg.Length > 0) return _installedPkgs.Contains(pkg);
            // github 来源安装后的真实包名来自插件自身 package.json：用市场记录的仓库匹配
            string repo = (en?.Repo ?? "").Trim();
            if (repo.Length == 0) return false;
            return _installed.Any(p => p.SourceMark == "market" &&
                string.Equals(p.Repo, repo, StringComparison.OrdinalIgnoreCase));
        }

        // ==================== 安装 ====================

        private async void BtnInstall_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            if (!((sender as FrameworkElement)?.Tag is MarketItem item)) return;
            await InstallEntryAsync(item);
        }

        /// <summary>安装入口（卡片按钮与详情弹窗共用）：确认 → 官方命令 → 记录 → 重启提示。</summary>
        private async Task InstallEntryAsync(MarketItem item)
        {
            if (_busy) return;
            InstanceDef def = SelectedDef();
            if (def == null)
            {
                await InfoAsync("请先在上方选择要安装到的实例。", "未选择实例");
                return;
            }
            string profile = EffectiveProfile();
            if (profile == null)
            {
                await InfoAsync("profile 只允许字母、数字、-、_、.（默认 web）。", "profile 非法");
                return;
            }
            string target = item.Entry.InstallTarget;
            string invalid = PluginInstaller.ValidateTarget(target);
            if (invalid != null)
            {
                await InfoAsync(invalid, "安装目标不合法");
                return;
            }

            Config cfg = def.ToConfig(_registry.Settings);
            bool shared = PluginUiHelper.UsesSharedDefaultHome(cfg);
            bool initialized = await PluginUiHelper.HomeInitializedAsync(cfg);

            var msg = new StringBuilder();
            msg.AppendLine("插件：" + item.Title);
            msg.AppendLine("安装目标：" + target);
            msg.AppendLine("实例：" + def.PickerLabel + "（profile " + profile + "）");
            msg.AppendLine("方式：dsh plugin --profile " + profile + " add " + target + "（DSH 官方方式）");
            if (item.Entry.MinHost.Trim().Length > 0)
                msg.AppendLine("支持版本：" + PluginCompat.Judge(item.Entry, _instanceVersion).Text);
            if (shared)
                msg.AppendLine().AppendLine("⚠ 该实例未配置独立 HOME，插件将装入默认 ~/.dsh，与其他默认实例共享、无法隔离。");
            if (!initialized)
                msg.AppendLine().AppendLine("⚠ 该实例 HOME 尚未初始化（还没启动过 dsh），建议先在实例页启动一次再安装。");
            if (item.Entry.Unverified)
                msg.AppendLine().AppendLine("⚠ 该条目来自 GitHub 实时搜索，未经人工审核，请确认插件来源可信后再安装。");
            msg.AppendLine().AppendLine("bundle 插件安装后需重启实例才会生效。确认安装？");
            if (!await ConfirmAsync(msg.ToString(), "安装插件到「" + def.Name + "」")) return;

            await RunInstallAsync(def, cfg, profile, target, item);
        }

        private async Task RunInstallAsync(InstanceDef def, Config cfg, string profile, string target, MarketItem item)
        {
            SetBusy(true, "正在安装 " + item.Title + " …（输出见控制台）");
            ListPlugins.IsEnabled = false;
            try
            {
                var before = new HashSet<string>(_installedPkgs, StringComparer.OrdinalIgnoreCase);
                PluginOpResult result = def.IsWsl
                    ? await PluginInstaller.RunWslAsync(cfg, PluginOp.Add, profile, target, PushLog)
                    : await PluginInstaller.RunWindowsAsync(cfg, PluginOp.Add, profile, target, PushLog);

                if (!result.Ok)
                {
                    PushLog("[市场] 安装失败" + (result.Error.Trim().Length > 0 ? "：" + result.Error.Trim() : ""));
                    await InfoAsync("安装失败。\n\n命令：" + result.Command + "\n\n" +
                                    (result.Error.Trim().Length > 0 ? result.Error.Trim() : "详见控制台输出。"),
                                    "安装失败");
                    return;
                }

                // 记录新增包（github: 安装后真实包名以实例 HOME 中新出现的依赖为准）
                await RefreshInstalledAsync();
                List<string> added = _installedPkgs.Except(before, StringComparer.OrdinalIgnoreCase).ToList();
                if (added.Count == 0)
                {
                    PushLog("[市场] 未检测到新增依赖（可能此前已安装），不写市场记录。");
                }
                foreach (string pkg in added)
                {
                    string ver = _installed.FirstOrDefault(p => string.Equals(p.Pkg, pkg, StringComparison.OrdinalIgnoreCase))
                        ?.Version ?? "";
                    PluginRecords.Upsert(def.Id, new PluginRecord
                    {
                        Pkg = pkg,
                        Name = item.Title,
                        Repo = item.Entry.Repo ?? "",
                        Version = ver,
                        Profile = profile,
                        Target = target,
                        InstalledAt = DateTime.UtcNow
                    });
                    PushLog("[市场] 已标记为市场安装：" + pkg + (ver.Length > 0 ? "（v" + ver + "）" : ""));
                }
                RebuildList();
                await PromptRestartAsync(def, "安装", target, force: false);
            }
            finally
            {
                ListPlugins.IsEnabled = true;
                SetBusy(false, "");
            }
        }

        /// <summary>插件命令成功后的重启提示：实例运行中 → 一键重启；未运行 → 提示下次启动生效。</summary>
        private async Task PromptRestartAsync(InstanceDef def, string verb, string target, bool force)
        {
            var mgr = _instanceMgr.For(def.Id);
            bool running = mgr.State == BackendState.Running || mgr.State == BackendState.Starting ||
                           mgr.State == BackendState.Restarting;
            if (!running && !force)
            {
                PushLog("[市场] " + verb + "完成（" + target + "）。实例未运行，下次启动时生效。");
                return;
            }
            try
            {
                var dlg = new ContentDialog
                {
                    Title = verb + "完成",
                    Content = "插件命令已执行成功（" + target + "）。\nbundle 插件需重启实例后生效，是否立即重启实例「" +
                              def.Name + "」？",
                    PrimaryButtonText = "立即重启",
                    CloseButtonText = "稍后",
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = XamlRoot
                };
                if (await dlg.ShowAsync() == ContentDialogResult.Primary)
                {
                    PushLog("[市场] 重启实例 " + def.Name + " …");
                    bool ok = await _instanceMgr.RestartAsync(def.Id);
                    PushLog("[市场] 重启" + (ok ? "完成。" : "未完成，请到实例页查看状态。"));
                }
            }
            catch { }
        }

        // ==================== 详情 ====================

        private async void BtnDetail_Click(object sender, RoutedEventArgs e)
        {
            if (!((sender as FrameworkElement)?.Tag is MarketItem item)) return;
            CatalogEntry en = item.Entry;

            var panel = new StackPanel { Spacing = 8, MinWidth = 440, MaxWidth = 560 };
            panel.Children.Add(new TextBlock
            {
                Text = en.Desc,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 13,
                Foreground = Application.Current.Resources["LabelPrimaryBrush"] as Brush
            });
            if (!string.IsNullOrWhiteSpace(en.DescEn))
                panel.Children.Add(new TextBlock
                {
                    Text = en.DescEn,
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 12,
                    Foreground = Application.Current.Resources["LabelSecondaryBrush"] as Brush
                });
            panel.Children.Add(new TextBlock
            {
                Text = "分类 " + (string.IsNullOrWhiteSpace(en.CategoryLabel) && string.IsNullOrWhiteSpace(en.Category)
                           ? "未分类"
                           : PluginCatalog.CategoryLabel(en.Category)) +
                       " · ★ " + en.Stars.ToString("N0") +
                       (en.UpdatedAt.Length >= 10 ? " · 更新 " + en.UpdatedAt.Substring(0, 10) : "") +
                       (en.Verified ? " · 已人工复核" : "") +
                       (en.Unverified ? " · 未审核" : ""),
                FontSize = 12,
                Foreground = Application.Current.Resources["LabelTertiaryBrush"] as Brush
            });

            if (!string.IsNullOrWhiteSpace(en.Repo))
            {
                panel.Children.Add(new HyperlinkButton
                {
                    Content = "GitHub 仓库：" + en.Repo,
                    NavigateUri = new Uri("https://github.com/" + en.Repo.TrimStart('/'))
                });
            }
            if (!string.IsNullOrWhiteSpace(en.Pkg))
                panel.Children.Add(MonoLine("npm 包：" + en.Pkg));
            if (!string.IsNullOrWhiteSpace(en.Tags != null ? string.Join(" / ", en.Tags) : ""))
                panel.Children.Add(MonoLine("标签：" + string.Join(" / ", en.Tags)));

            string profile = EffectiveProfile() ?? "web";
            panel.Children.Add(MonoLine("安装命令：dsh plugin --profile " + profile + " add " +
                                        (en.Installable ? en.InstallTarget : "（仅浏览，未提供 dsh.bundle）")));
            panel.Children.Add(MonoLine("目标实例：" + (SelectedDef()?.PickerLabel ?? "（未选择）")));

            var compat = new TextBlock
            {
                Text = "支持版本：" + (en.MinHost.Trim().Length > 0
                    ? PluginCompat.Judge(en, _instanceVersion).Text
                    : "正在查询 npm 包元数据…"),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Application.Current.Resources["LabelSecondaryBrush"] as Brush
            };
            panel.Children.Add(compat);
            var note = new TextBlock
            {
                Text = "声明缺失时才会查询包元数据兜底；仓库未声明则不显示该行，不做推测。",
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Application.Current.Resources["LabelTertiaryBrush"] as Brush
            };
            panel.Children.Add(note);
            if (en.MinHost.Trim().Length > 0) note.Visibility = Visibility.Collapsed;   // 已声明，无需说明

            var dlg = new ContentDialog
            {
                Title = item.Title,
                Content = panel,
                CloseButtonText = "关闭",
                XamlRoot = XamlRoot
            };
            if (en.Installable && !_busy) dlg.PrimaryButtonText = "安装到所选实例";

            // 多来源合并：列出各源变体，可选择"从哪个源的条目安装"
            List<CatalogEntry> variants = en.Variants;
            if (variants != null && variants.Count > 1)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = "该插件在 " + variants.Count + " 个来源中都有收录，选择要使用的条目：",
                    FontSize = 12,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = Application.Current.Resources["LabelSecondaryBrush"] as Brush
                });
                foreach (CatalogEntry v in variants)
                {
                    var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
                    row.Children.Add(new TextBlock
                    {
                        Text = (v.SourceName ?? v.SourceId) + " · ★ " + v.Stars.ToString("N0") +
                               (v.Unverified ? " · 未审核" : "") +
                               (v.MinHost.Trim().Length > 0 ? " · 支持 DSH ≥ " + PluginCompat.FloorOfRange(v.MinHost) : ""),
                        FontSize = 12,
                        VerticalAlignment = VerticalAlignment.Center,
                        Foreground = Application.Current.Resources["LabelSecondaryBrush"] as Brush
                    });
                    var btn = new Button
                    {
                        Content = "从此源安装",
                        Style = (Style)Application.Current.Resources["BtnCompact"]
                    };
                    btn.Click += (s, ev) =>
                    {
                        dlg.Hide();
                        if (!_busy) _ = InstallEntryAsync(new MarketItem { Entry = v });
                    };
                    row.Children.Add(btn);
                    panel.Children.Add(row);
                }
            }

            // 兼容信息兜底查询（minHost 缺失 → npm registry 包元数据；仍未声明则整行隐藏）
            if (en.MinHost.Trim().Length == 0)
            {
                _ = Task.Run(async () =>
                {
                    CompatInfo ci = await PluginCompat.JudgeDetailedAsync(en, _instanceVersion);
                    _dq.TryEnqueue(() =>
                    {
                        try
                        {
                            bool declared = ci.State != CompatState.NotDeclared;
                            compat.Text = declared ? "支持版本：" + ci.Text : "";
                            compat.Visibility = declared ? Visibility.Visible : Visibility.Collapsed;
                            note.Visibility = declared ? Visibility.Visible : Visibility.Collapsed;
                        }
                        catch { }
                    });
                });
            }

            ContentDialogResult r = await dlg.ShowAsync();
            if (r == ContentDialogResult.Primary && en.Installable && !_busy)
            {
                await InstallEntryAsync(item);
            }
        }

        private TextBlock MonoLine(string text)
        {
            return new TextBlock
            {
                Text = text,
                FontSize = 12,
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                TextWrapping = TextWrapping.Wrap,
                Foreground = Application.Current.Resources["LabelTertiaryBrush"] as Brush
            };
        }

        // ==================== 工具按钮 ====================

        private async void BtnRefreshCatalog_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            await LoadCatalogAsync(force: true);
        }

        private void BtnOpenHome_Click(object sender, RoutedEventArgs e)
        {
            InstanceDef def = SelectedDef();
            if (def == null) return;
            Config cfg = def.ToConfig(_registry.Settings);
            try
            {
                if (def.IsWsl)
                {
                    PushLog("[市场] WSL 实例 HOME：" + PluginUiHelper.WslHomeDisplay(cfg) +
                            "（发行版内路径，可在终端执行 wsl -d " + (cfg.WslDistro ?? "?") + " 查看）");
                    return;
                }
                string home = string.IsNullOrWhiteSpace(cfg.Home)
                    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh")
                    : cfg.Home;
                Directory.CreateDirectory(home);
                Process.Start(new ProcessStartInfo(home) { UseShellExecute = true });
                PushLog("[市场] 已打开实例 HOME：" + home);
            }
            catch (Exception ex)
            {
                PushLog("[市场] 打开 HOME 失败：" + ex.Message);
            }
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
            try { _console?.Invoke(line); } catch { }
        }

        private async Task<bool> ConfirmAsync(string message, string title)
        {
            try
            {
                var dlg = new ContentDialog
                {
                    Title = title,
                    Content = message,
                    PrimaryButtonText = "确定",
                    CloseButtonText = "取消",
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = XamlRoot
                };
                return await dlg.ShowAsync() == ContentDialogResult.Primary;
            }
            catch { return false; }
        }

        private async Task InfoAsync(string message, string title)
        {
            try
            {
                await new ContentDialog
                {
                    Title = title,
                    Content = message,
                    CloseButtonText = "知道了",
                    XamlRoot = XamlRoot
                }.ShowAsync();
            }
            catch { }
        }
    }
}
