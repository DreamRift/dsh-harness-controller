// ============================================================================
//  DialogService — 统一的确认/提示对话框（重构 2.0 / P3）
//
//  同一形状的 ContentDialog 此前在四个面板里各写了一遍（≥10 处），文案与按钮
//  措辞逐渐漂移。这里收敛成一个服务：调用方只给标题与内容，
//  XamlRoot 由构造时注入，异常（无 XamlRoot / 重复弹窗）统一按"用户取消"处理。
// ============================================================================

using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DshController
{
    public sealed class DialogService
    {
        private readonly Func<XamlRoot> _rootProvider;

        public DialogService(Func<XamlRoot> rootProvider)
        {
            _rootProvider = rootProvider;
        }

        /// <summary>确认框：确定返回 true；取消、异常或无宿主都返回 false（保守不执行）。</summary>
        public async Task<bool> ConfirmAsync(string message, string title,
            string primaryText = "确定", string closeText = "取消")
        {
            ContentDialogResult r = await ShowAsync(new ContentDialog
            {
                Title = title,
                Content = message,
                PrimaryButtonText = primaryText,
                CloseButtonText = closeText,
                DefaultButton = ContentDialogButton.Close
            });
            return r == ContentDialogResult.Primary;
        }

        /// <summary>提示框（单按钮）。</summary>
        public Task InfoAsync(string message, string title, string closeText = "知道了")
        {
            return ShowAsync(new ContentDialog
            {
                Title = title,
                Content = message,
                CloseButtonText = closeText
            });
        }

        /// <summary>自定义内容的对话框（数据源选择、插件详情这类）。</summary>
        public async Task<ContentDialogResult> ShowAsync(ContentDialog dialog)
        {
            if (dialog == null) return ContentDialogResult.None;
            try
            {
                dialog.XamlRoot = _rootProvider?.Invoke();
                if (dialog.XamlRoot == null) return ContentDialogResult.None;
                return await dialog.ShowAsync();
            }
            catch
            {
                // 理由: 窗口正在关闭或已有对话框在显示——按"用户没有确认"处理，绝不误执行危险操作
                return ContentDialogResult.None;
            }
        }
    }
}
