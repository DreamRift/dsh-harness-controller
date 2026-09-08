// ============================================================================
//  UsageView 用的一个取反可见性转换器（空态/无数据标注这类互斥可见性）。
//  仅此一个：其余样式语义都交给布尔属性 + 标准 BooleanToVisibilityConverter。
// ============================================================================

using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace DshController.Views
{
    /// <summary>WinUI 3 没有内置 BooleanToVisibilityConverter，本地补一对。</summary>
    public sealed class BoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language) =>
            value is bool b && b ? Visibility.Visible : Visibility.Collapsed;

        public object ConvertBack(object value, Type targetType, object parameter, string language) =>
            throw new NotSupportedException();
    }

    public sealed class InverseBoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language) =>
            value is bool b && b ? Visibility.Collapsed : Visibility.Visible;

        public object ConvertBack(object value, Type targetType, object parameter, string language) =>
            throw new NotSupportedException();
    }

    /// <summary>用量 KPI 的语义色只在呈现层决定，统计模型保持纯 .NET。</summary>
    public sealed class UsageKpiAccentConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            string kind = value as string ?? "";
            Color color = kind switch
            {
                "requests" => Color.FromArgb(255, 62, 143, 245),
                "images" => Color.FromArgb(255, 20, 166, 123),
                "tokens" => Color.FromArgb(255, 229, 142, 35),
                "value" => Color.FromArgb(255, 8, 163, 190),
                "latency" => Color.FromArgb(255, 144, 82, 212),
                _ => Color.FromArgb(255, 62, 143, 245)
            };
            return new SolidColorBrush(color);
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language) =>
            throw new NotSupportedException();
    }

    public sealed class UsageRangeBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            string current = value as string ?? "";
            string target = parameter as string ?? "";
            bool active = string.Equals(current, target, StringComparison.OrdinalIgnoreCase) ||
                (current == "7" && target == "7");
            return new SolidColorBrush(active
                ? Color.FromArgb(78, 65, 134, 237)
                : Color.FromArgb(24, 120, 151, 184));
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language) =>
            throw new NotSupportedException();
    }
}
