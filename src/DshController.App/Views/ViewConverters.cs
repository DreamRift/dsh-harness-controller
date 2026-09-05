// ============================================================================
//  UsageView 用的一个取反可见性转换器（空态/无数据标注这类互斥可见性）。
//  仅此一个：其余样式语义都交给布尔属性 + 标准 BooleanToVisibilityConverter。
// ============================================================================

using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

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
}