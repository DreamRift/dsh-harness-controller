// ============================================================================
//  PluginMarketPanel · 条目详情弹窗（partial 分部；纯搬移，逐字一致）
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
            panel.Children.Add(MonoLine("目标实例：" + (SelectedDef() == null
                    ? "（未选择）"
                    : DshController.ViewModels.InstanceDisplayName.For(SelectedDef()) + "（" + DshController.ViewModels.InstanceDisplayName.Original(SelectedDef()) + "）")));

            var compat = new TextBlock
            {
                Text = "支持版本：" + (en.MinHost.Trim().Length > 0
                    ? PluginCompat.Judge(en, _target.HarnessVersion).Text
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
                    CompatInfo ci = await PluginCompat.JudgeDetailedAsync(en, _target.HarnessVersion);
                    _dq.TryEnqueue(() =>
                    {
                        try
                        {
                            bool declared = ci.State != CompatState.NotDeclared;
                            compat.Text = declared ? "支持版本：" + ci.Text : "";
                            compat.Visibility = declared ? Visibility.Visible : Visibility.Collapsed;
                            note.Visibility = declared ? Visibility.Visible : Visibility.Collapsed;
                        }
                        catch
                        {
                            // 理由: 此处仅更新兼容信息提示的 UI 文本，若窗口已在关闭则相关控件不可用，吞掉并保留面板现状即可。
                        }
                    });
                });
            }

            ContentDialogResult r = await _dialogs.ShowAsync(dlg);   // 经 DialogService：竞态时按 None（关闭）处理
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
    }
}
