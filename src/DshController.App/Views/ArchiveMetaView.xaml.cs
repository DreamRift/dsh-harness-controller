// ============================================================================
//  ArchiveMetaView — 档案主区「单实例档案详情」的 View 侧薄壳：注入视图模型 +
//  Show 转发 + x:Bind 刷新。状态与格式化全在视图模型，这里零逻辑。
//  2026-09-06 二次改版：完整用量区并入本页（会话 ID 复制钮与 UsageView 同款转发）。
// ============================================================================

using System;
using DshController.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace DshController.Views
{
    public sealed partial class ArchiveMetaView : UserControl
    {
        /// <summary>用户点击改名按钮（App 层弹窗并处理）。</summary>
        public event Action<string> RenameRequested;

        public ArchiveMetaView()
        {
            InitializeComponent();
        }

        public ArchiveMetaViewModel ViewModel { get; private set; }

        /// <summary>x:Bind 函数：非空串 → Visible（截断标注等辅助文案显隐用，与 UsageView 同款）。</summary>
        public Microsoft.UI.Xaml.Visibility NonEmpty(string s) =>
            string.IsNullOrEmpty(s) ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;

        /// <summary>MainWindow 构造后注入依赖。</summary>
        public void Bind(ArchiveMetaViewModel viewModel)
        {
            ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
            Bindings.Update();
        }

        /// <summary>左栏选中变化：重建该档案的镜像字段并刷新绑定。</summary>
        public void Show(string archiveId)
        {
            if (ViewModel == null) return;
            ViewModel.Show(archiveId);
            Bindings.Update();
        }

        private void BtnRename_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
            if (ViewModel != null && !string.IsNullOrEmpty(ViewModel.CurrentArchiveId))
                RenameRequested?.Invoke(ViewModel.CurrentArchiveId);
        }

        /// <summary>会话 ID 复制钮：模板内 Click 转发（文本走 Tag，视图模型不碰剪贴板）。</summary>
        private void CopySessionId_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
            if (sender is Microsoft.UI.Xaml.Controls.Button b && b.Tag is string s && !string.IsNullOrEmpty(s))
            {
                var pkg = new Windows.ApplicationModel.DataTransfer.DataPackage();
                pkg.SetText(s);
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(pkg);
                Windows.ApplicationModel.DataTransfer.Clipboard.Flush();
            }
        }
    }
}
