// ============================================================================
//  显示名适配器（App 层）：ComboBox ItemTemplate 的 Binding 入口，
//  逻辑全部转调 ViewModels.InstanceDisplayName（单源在 VM 层，这里只是形状桥）。
// ============================================================================

using System;
using DshController.Core;
using DshController.ViewModels;
using Microsoft.UI.Xaml.Data;

namespace DshController.Views
{
    public sealed class DisplayLabelConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
            => InstanceDisplayName.For(value as InstanceDef);
        public object ConvertBack(object value, Type targetType, object parameter, string language)
            => throw new NotSupportedException("单向显示转换器");   // 理由: 显示名单源解析器只出不进
    }

    public sealed class DisplayTooltipConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
            => InstanceDisplayName.TooltipFor(value as InstanceDef);
        public object ConvertBack(object value, Type targetType, object parameter, string language)
            => throw new NotSupportedException("单向显示转换器");   // 理由: 显示名单源解析器只出不进
    }
}
