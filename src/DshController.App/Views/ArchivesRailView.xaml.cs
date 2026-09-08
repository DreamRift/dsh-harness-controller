// ============================================================================
//  ArchivesRailView — 档案左栏的 View 侧薄壳：绑定注入 + 选中事件转发 +
//  回设选中的防重入门闩。界面状态全在视图模型，这里零逻辑。
// ============================================================================

using System;
using System.Linq;
using DshController.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace DshController.Views
{
    public sealed partial class ArchivesRailView : UserControl
    {
        private ArchivesRailViewModel _vm;
        private bool _syncing;

        public ArchivesRailViewModel ViewModel { get; private set; }

        /// <summary>用户点选了一行（代码回设选中不会触发）。</summary>
        public event Action<ArchivesRailRow> RowSelected;

        public ArchivesRailView()
        {
            InitializeComponent();
        }

        public void Bind(ArchivesRailViewModel vm)
        {
            _vm = vm ?? throw new ArgumentNullException(nameof(vm));
            ViewModel = _vm;
            RailList.ItemsSource = _vm.Rows;
            Bindings.Update();
        }

        public void Refresh()
        {
            if (_vm == null) return;
            _vm.Refresh();
            Bindings.Update();
        }

        private void RailList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncing || _vm == null) return;
            ArchivesRailRow row = RailList.SelectedItem as ArchivesRailRow;
            if (row != null) RowSelected?.Invoke(row);
        }

        /// <summary>把视图选中同步到视图模型状态（重建行后恢复高亮用）。</summary>
        public void SyncSelection(string id)
        {
            if (_vm == null) return;
            _syncing = true;
            try
            {
                ArchivesRailRow row = string.IsNullOrEmpty(id)
                    ? null
                    : _vm.Rows.FirstOrDefault(r => r.Id == id);
                RailList.SelectedItem = row;
                if (row != null) RailList.ScrollIntoView(row);
            }
            finally { _syncing = false; }
        }
    }
}
