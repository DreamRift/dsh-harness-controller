// ============================================================================
//  PluginMarketPanel · 数据源/实例选择与列表过滤（partial 分部；纯搬移，逐字一致）
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
    public sealed partial class PluginMarketPanel
    {
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
            if (await _dialogs.ShowAsync(dlg) != ContentDialogResult.Primary) return;   // 经 DialogService 异常兜底：双弹窗竞态按取消处理，不炸

            var ids = boxes.Where(kv => kv.Value.IsChecked == true).Select(kv => kv.Key).ToList();
            _registry.Settings.PluginSources = ids;
            _registry.Settings.PluginRegistryUrl = txtCustom.Text.Trim();
            try { _registry.Save(); }
            catch (Exception ex) { PushLog("[市场] 数据源保存失败（本次会话内生效，重启后回退）：" + ex.Message); }
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

        private InstanceDef SelectedDef() => _target?.SelectedInstance;

        /// <summary>下拉选中项与视图模型保持同步（程序化赋值时不回灌事件）。</summary>
        private void SyncPicker()
        {
            _syncingSelection = true;
            try { CmbInstance.SelectedItem = _target.SelectedInstance; }
            finally { _syncingSelection = false; }
        }

        private async void CmbInstance_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_syncingSelection || _closing) return;
            if (CmbInstance.SelectedItem is InstanceDef def && def != _target.SelectedInstance)
            {
                _target.SelectedInstance = def;
                await _target.ReloadAsync();
            }
        }

        /// <summary>信息条 = 共享视图模型的版本文案 + 本页关心的 HOME 展示。</summary>
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
            int packages = _target.Installed.Count;
            TxtInstanceMeta.Text = _target.MetaText.Replace(" · 已装 " + packages + " 个包", "") +
                                   " · HOME: " + home;
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

        /// <summary>profile 归一化由共享视图模型负责（两个插件页同一份规则）。</summary>
        private string EffectiveProfile()
        {
            _target.Profile = TxtProfile.Text;
            return _target.EffectiveProfile;
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
                CompatInfo ci = PluginCompat.Judge(en, _target.HarnessVersion);
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
            if (pkg.Length > 0) return _target.InstalledPkgs.Contains(pkg);
            // github 来源安装后的真实包名来自插件自身 package.json：用市场记录的仓库匹配
            string repo = (en?.Repo ?? "").Trim();
            if (repo.Length == 0) return false;
            return _target.Installed.Any(p => p.SourceMark == "market" &&
                string.Equals(p.Repo, repo, StringComparison.OrdinalIgnoreCase));
        }
    }
}
