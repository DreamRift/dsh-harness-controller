// ============================================================================
//  InstancesRailView — 左栏列表的 View 侧薄壳：绑定注入 + 选中事件转发 +
//  回设选中的防重入门闩。界面状态全在视图模型，这里零逻辑。
// ============================================================================

using System;
using System.Linq;
using DshController.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace DshController.Views
{
    public sealed partial class InstancesRailView : UserControl
    {
        private InstancesRailViewModel _vm;
        private bool _syncing;

        /// <summary>用户点选了一行（代码回设选中不会触发）。</summary>
        public event Action<InstanceRailRow> RowSelected;

        public InstancesRailView()
        {
            InitializeComponent();
        }

        public void Bind(InstancesRailViewModel vm)
        {
            _vm = vm ?? throw new ArgumentNullException(nameof(vm));
            RailList.ItemsSource = _vm.Rows;
        }

        /// <summary>头部小标动态化（旧环境行上的实例数/运行数能力迁到此处）。</summary>
        public void SetHeader(string text)
        {
            if (!string.IsNullOrWhiteSpace(text)) TxtRailHeader.Text = text;
        }

        private void RailList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncing || _vm == null) return;
            InstanceRailRow row = RailList.SelectedItem as InstanceRailRow;
            if (row != null) RowSelected?.Invoke(row);
        }

        /// <summary>把视图选中同步到视图模型状态（重建行后恢复高亮用）。</summary>
        public void SyncSelection(string id)
        {
            if (_vm == null) return;
            _syncing = true;
            try
            {
                InstanceRailRow row = string.IsNullOrEmpty(id)
                    ? null
                    : _vm.Rows.FirstOrDefault(r => r.Id == id);
                RailList.SelectedItem = row;
                if (row != null) RailList.ScrollIntoView(row);
            }
            finally { _syncing = false; }
        }
    }
}
