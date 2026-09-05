// ============================================================================
//  GalleryView — 开发态设计台（重构 2.0 / P4）
//
//  为什么需要它：WinUI 3 的 XAML 热重载依赖 Visual Studio，命令行构建下没有；
//  与其反复"制造真实运行条件"去看某个状态长什么样，不如把全部令牌、控件样式与
//  状态分支用假数据摆在一页上——改样式时只看这里，15 秒 Debug 构建即可验证。
//  仅在 --dev 启动参数下出现在侧边栏，正式使用不受影响。
// ============================================================================

using System;
using System.Collections.Generic;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DshController.Views
{
    public sealed partial class GalleryView : UserControl
    {
        /// <summary>命令行是否带 --dev（决定侧边栏是否显示设计台）。</summary>
        public static bool IsDevMode
        {
            get
            {
                foreach (string a in Environment.GetCommandLineArgs())
                    if (string.Equals(a, "--dev", StringComparison.OrdinalIgnoreCase)) return true;
                return false;
            }
        }

        public GalleryView()
        {
            InitializeComponent();
            Loaded += (s, e) => Build();
        }

        private void Build()
        {
            BuildSwatches();
            BuildStates();
            BuildFacetBadges();
        }

        /// <summary>色板：按令牌名取主题资源，缺失的令牌会以红色边框暴露出来。</summary>
        private void BuildSwatches()
        {
            string[] tokens =
            {
                "BgBaseBrush", "BgCardBrush", "BgSubtleBrush", "BgCodeBrush",
                "LabelPrimaryBrush", "LabelSecondaryBrush", "LabelTertiaryBrush",
                "BrandPrimaryBrush", "BrandSoftBrush",
                "StateRunBrush", "StateStartingBrush", "StateStopBrush", "DangerBrush"
            };
            SwatchHost.Items.Clear();
            foreach (string token in tokens)
            {
                var swatch = new Border
                {
                    Width = 68,
                    Height = 44,
                    CornerRadius = new CornerRadius(8),
                    Margin = new Thickness(0, 0, 8, 8),
                    BorderThickness = new Thickness(1),
                    BorderBrush = Brush(token) == null ? new SolidColorBrush(Colors.Red) : Brush("BorderBrushToken"),
                    Background = Brush(token) ?? new SolidColorBrush(Colors.Transparent)
                };
                var cell = new StackPanel { Width = 76, Spacing = 2 };
                cell.Children.Add(swatch);
                cell.Children.Add(new TextBlock
                {
                    Text = token.Replace("Brush", ""),
                    FontSize = 10,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = Brush("LabelTertiaryBrush")
                });
                SwatchHost.Items.Add(cell);
            }
        }

        /// <summary>实例状态点：运行 / 启动中 / 已停止 / 失败四态。</summary>
        private void BuildStates()
        {
            var states = new List<(string Label, string Token)>
            {
                ("运行中 · 本程序启动", "StateRunBrush"),
                ("启动中", "StateStartingBrush"),
                ("已停止", "StateStopBrush"),
                ("启动失败", "DangerBrush")
            };
            StateHost.Children.Clear();
            foreach ((string label, string token) in states)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
                row.Children.Add(new Border
                {
                    Width = 9,
                    Height = 9,
                    CornerRadius = new CornerRadius(5),
                    VerticalAlignment = VerticalAlignment.Center,
                    Background = Brush(token)
                });
                row.Children.Add(new TextBlock
                {
                    Text = label,
                    FontSize = 12.5,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = Brush("LabelPrimaryBrush")
                });
                StateHost.Children.Add(row);
            }
        }

        /// <summary>档案分面的新鲜度徽标：新鲜 / 过期 / 未采集 / 失败。</summary>
        private void BuildFacetBadges()
        {
            var badges = new List<(string Text, string Token)>
            {
                ("新鲜（3 分钟前）", "StateRunBrush"),
                ("已过期（2 天前）", "StateStartingBrush"),
                ("未采集", "LabelTertiaryBrush"),
                ("采集失败", "DangerBrush")
            };
            FacetHost.Children.Clear();
            foreach ((string text, string token) in badges)
            {
                var chip = new Border
                {
                    Background = Brush("BgSubtleBrush"),
                    BorderBrush = Brush(token),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(9),
                    Padding = new Thickness(9, 3, 9, 3)
                };
                chip.Child = new TextBlock { Text = text, FontSize = 11.5, Foreground = Brush(token) };
                FacetHost.Children.Add(chip);
            }
        }

        private static Brush Brush(string key)
        {
            object value = Application.Current.Resources.TryGetValue(key, out object v) ? v : null;
            return value as Brush;
        }
    }
}
