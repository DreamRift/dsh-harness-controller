// ============================================================================
//  ArchiveMetaView — 档案主区元信息一览的 View 侧薄壳：注入视图模型 +
//  Show 转发 + x:Bind 刷新。状态与格式化全在视图模型，这里零逻辑。
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
    }
}
