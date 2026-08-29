// ============================================================================
//  PluginManagePanel — 已装插件管理交互（v0.6.0，code-behind，无 MVVM）
//
//  与 PluginMarketPanel 同一套面板模式。数据 = 实例 HOME 的真实状态
//  （InstalledPlugins 黑盒读取）+ 市场记录来源标注（PluginRecords）；
//  升级/卸载 = DSH 官方命令（dsh plugin update/remove），成功后刷新列表并
//  提示重启；卸载成功同步清除对应的市场安装记录。
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using DshController.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DshController
{
    /// <summary>已装插件列表条目（x:Bind 数据源；状态变化靠重建列表）。</summary>
    public sealed class ManageItem
    {
        public InstalledPlugin Plugin { get; set; }

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
        private InstanceManager _instanceMgr;
        private Action<string> _console;
        private DispatcherQueue _dq;

        private bool _closing;
        private bool _busy;
        private bool _loadingInstances;
        private string _instanceId = "";
        private string _instanceVersion = "";
        private List<InstalledPlugin> _installed = new List<InstalledPlugin>();

        public PluginManagePanel()
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
            ShowEmpty("选择一个实例后查看其已装插件。");
        }

        public void Shutdown()
        {
            _closing = true;
        }

        /// <summary>MainWindow 切到本页时调用：刷新实例列表与已装状态。</summary>
        public void OnShown()
        {
            if (_closing || _busy) return;
            RefreshInstances();
            _ = ReloadInstalledAsync();
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
            }
            finally { _loadingInstances = false; }
        }

        private async void CmbInstance_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loadingInstances || _closing) return;
            if (CmbInstance.SelectedItem is InstanceDef def && !string.Equals(def.Id, _instanceId, StringComparison.OrdinalIgnoreCase))
            {
                _instanceId = def.Id;
                TxtInstanceMeta.Text = "正在检测实例 harness 版本…";
                _instanceVersion = await PluginUiHelper.DetectVersionAsync(_registry, def);
                await ReloadInstalledAsync();
            }
        }

        private string EffectiveProfile()
        {
            return PluginUiHelper.NormalizeProfile(TxtProfile.Text);
        }

        // ==================== 已装状态加载与展示 ====================

        private async Task ReloadInstalledAsync()
        {
            InstanceDef def = SelectedDef();
            if (def == null)
            {
                ShowEmpty("没有可用实例：请先在「Windows 实例 / WSL 实例」页创建一个实例。");
                UpdateMeta();
                return;
            }
            string profile = EffectiveProfile();
            if (profile == null)
            {
                ShowEmpty("profile 含非法字符（仅允许字母、数字、-、_、.）。");
                return;
            }
            SetBusy(true, "正在读取实例 HOME 的已装插件…");
            List<InstalledPlugin> installed = new List<InstalledPlugin>();
            bool homeMissing = false;
            try
            {
                Config cfg = def.ToConfig(_registry.Settings);
                List<PluginRecord> records = PluginRecords.Load(def.Id);
                if (def.IsWsl)
                {
                    string home = await PluginUiHelper.ResolveEffectiveHomeAsync(cfg);
                    if (home == null)
                    {
                        string root = await WslTools.GetDistroHomeAsync(cfg.WslDistro ?? "");
                        home = WslTools.ResolveLinuxPath("", string.IsNullOrEmpty(root) ? "/root" : root);
                    }
                    installed = await InstalledPlugins.ReadWslAsync(cfg.WslDistro ?? "", home, profile, records) ??
                                new List<InstalledPlugin>();
                    homeMissing = installed.Count == 0;
                }
                else
                {
                    string home = string.IsNullOrWhiteSpace(cfg.Home)
                        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh")
                        : cfg.Home;
                    homeMissing = !Directory.Exists(home) ||
                                  !File.Exists(Path.Combine(home, "profiles", profile, "package.json"));
                    if (!homeMissing)
                        installed = await InstalledPlugins.ReadWindowsAsync(home, profile, records) ??
                                    new List<InstalledPlugin>();
                }
            }
            catch (Exception ex)
            {
                PushLog("[插件] 读取已装状态失败：" + ex.Message);
            }
            SetBusy(false, "");
            _installed = installed;
            UpdateMeta();
            RenderList(def, profile, homeMissing);
        }

        private void UpdateMeta()
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
                                   " · HOME: " + home + " · 已装 " + _installed.Count + " 个包";
        }

        private void RenderList(InstanceDef def, string profile, bool homeMissing)
        {
            if (_installed.Count == 0)
            {
                ShowEmpty(homeMissing
                    ? "该实例的 HOME 尚未生成 profile（首次启动 dsh 后自动创建）。安装插件或先启动一次实例后再来查看。"
                    : "该实例的 profile「" + profile + "」下没有已安装的插件。可到「插件市场」搜索安装。");
                return;
            }
            HideEmpty();
            ListPlugins.ItemsSource = _installed.Select(p => new ManageItem { Plugin = p }).ToList();
        }

        // ==================== 升级 / 卸载 ====================

        private async void BtnUpdate_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            if (!((sender as FrameworkElement)?.Tag is ManageItem item)) return;
            await RunManageOpAsync(item, PluginOp.Update);
        }

        private async void BtnUninstall_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            if (!((sender as FrameworkElement)?.Tag is ManageItem item)) return;
            await RunManageOpAsync(item, PluginOp.Remove);
        }

        private async Task RunManageOpAsync(ManageItem item, PluginOp op)
        {
            InstanceDef def = SelectedDef();
            if (def == null) return;
            string profile = EffectiveProfile();
            if (profile == null)
            {
                await InfoAsync("profile 只允许字母、数字、-、_、.（默认 web）。", "profile 非法");
                return;
            }
            if (item.Plugin.IsOfficial)
            {
                await InfoAsync("官方基础包（" + item.Plugin.Pkg + "）随 harness 版本管理，" +
                                "不能通过插件命令" + PluginOpText.Label(op) + "。", "不允许的操作");
                return;
            }
            string verb = PluginOpText.Label(op);
            string verbCmd = PluginOpText.Verb(op);

            var msg = new StringBuilder();
            msg.AppendLine("包：" + item.Plugin.Pkg + (string.IsNullOrEmpty(item.Plugin.Version) ? "" : "（v" + item.Plugin.Version + "）"));
            msg.AppendLine("实例：" + def.PickerLabel + "（profile " + profile + "）");
            msg.AppendLine("命令：dsh plugin --profile " + profile + " " + verbCmd + " " + item.Plugin.Pkg);
            if (op == PluginOp.Remove)
            {
                msg.AppendLine();
                msg.AppendLine("卸载会移除该 profile 对此插件的依赖与 bundle 声明，此操作不可撤销。确认卸载？");
                if (!await ConfirmAsync(msg.ToString(), "卸载插件")) return;
            }
            else
            {
                msg.AppendLine();
                msg.AppendLine("升级 bundle 插件后需重启实例才会生效。继续？");
                if (!await ConfirmAsync(msg.ToString(), "升级插件")) return;
            }

            Config cfg = def.ToConfig(_registry.Settings);
            SetBusy(true, "正在" + verb + " " + item.Plugin.Pkg + " …（输出见控制台）");
            ListPlugins.IsEnabled = false;
            PluginOpResult result;
            try
            {
                result = def.IsWsl
                    ? await PluginInstaller.RunWslAsync(cfg, op, profile, item.Plugin.Pkg, PushLog)
                    : await PluginInstaller.RunWindowsAsync(cfg, op, profile, item.Plugin.Pkg, PushLog);
            }
            finally
            {
                ListPlugins.IsEnabled = true;
                SetBusy(false, "");
            }

            if (!result.Ok)
            {
                PushLog("[插件] " + verb + "失败" + (result.Error.Trim().Length > 0 ? "：" + result.Error.Trim() : ""));
                await InfoAsync(verb + "失败。\n\n命令：" + result.Command + "\n\n" +
                                (result.Error.Trim().Length > 0 ? result.Error.Trim() : "详见控制台输出。"),
                                verb + "失败");
                return;
            }

            if (op == PluginOp.Remove)
            {
                PluginRecords.Remove(def.Id, item.Plugin.Pkg);
                PushLog("[插件] 已清除市场安装记录（如有）：" + item.Plugin.Pkg);
            }
            await ReloadInstalledAsync();
            if (op == PluginOp.Update)
            {
                // 升级后把市场记录里的版本号回写为实例 HOME 中实测到的新版本
                string newVer = _installed.FirstOrDefault(
                    p => string.Equals(p.Pkg, item.Plugin.Pkg, StringComparison.OrdinalIgnoreCase))?.Version ?? "";
                List<PluginRecord> records = PluginRecords.Load(def.Id);
                PluginRecord rec = PluginRecords.Find(records, item.Plugin.Pkg);
                if (rec != null)
                {
                    rec.Version = newVer;
                    PluginRecords.Save(def.Id, records);
                    PushLog("[插件] 市场记录版本已更新：" + item.Plugin.Pkg +
                            (newVer.Length > 0 ? " → v" + newVer : ""));
                }
            }

            var mgr = _instanceMgr.For(def.Id);
            bool running = mgr.State == BackendState.Running || mgr.State == BackendState.Starting ||
                           mgr.State == BackendState.Restarting;
            try
            {
                var dlg = new ContentDialog
                {
                    Title = verb + "完成",
                    Content = running
                        ? "已" + verb + " " + item.Plugin.Pkg + "。\nbundle 插件需重启实例后生效，是否立即重启实例「" + def.Name + "」？"
                        : "已" + verb + " " + item.Plugin.Pkg + "。\n实例当前未运行，下次启动时生效。",
                    PrimaryButtonText = running ? "立即重启" : "知道了",
                    CloseButtonText = running ? "稍后" : "",
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = XamlRoot
                };
                ContentDialogResult r = await dlg.ShowAsync();
                if (running && r == ContentDialogResult.Primary)
                {
                    PushLog("[插件] 重启实例 " + def.Name + " …");
                    bool ok = await _instanceMgr.RestartAsync(def.Id);
                    PushLog("[插件] 重启" + (ok ? "完成。" : "未完成，请到实例页查看状态。"));
                }
            }
            catch { }
        }

        // ==================== 小件 ====================

        private void BtnRefresh_Click(object sender, RoutedEventArgs e)
        {
            if (!_busy) _ = ReloadInstalledAsync();
        }

        private void SetBusy(bool busy, string text)
        {
            _busy = busy;
            BusyRing.IsActive = busy;
            TxtStatus.Text = text ?? "";
            BtnRefresh.IsEnabled = !busy;
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
