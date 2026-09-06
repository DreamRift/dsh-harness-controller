// ============================================================================
//  InstancesRailView — 左栏列表的 View 侧薄壳：绑定注入 + 选中事件转发 +
//  新建/扫描入口事件转发 + 回设选中的防重入门闩。界面状态全在视图模型，这里零逻辑。
// ============================================================================

using System;
using System.Linq;
using DshController.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DshController.Views
{
    public sealed partial class InstancesRailView : UserControl
    {
        private InstancesRailViewModel _vm;
        private bool _syncing;

        /// <summary>用户点选了一行（代码回设选中不会触发）。</summary>
        public event Action<InstanceRailRow> RowSelected;

        /// <summary>左栏「＋ 新建实例」被点击（环境/发行版选择由 MainWindow 层弹窗完成）。</summary>
        public event Action NewInstanceRequested;

        /// <summary>左栏「扫描」被点击。</summary>
        public event Action ScanRequested;

        public InstancesRailView()
        {
            InitializeComponent();
        }

        public void Bind(InstancesRailViewModel vm)
        {
            _vm = vm ?? throw new ArgumentNullException(nameof(vm));
            RailList.ItemsSource = _vm.Rows;
        }

        private void BtnRailNew_Click(object sender, RoutedEventArgs e)
        {
            NewInstanceRequested?.Invoke();
        }

        private void BtnRailScan_Click(object sender, RoutedEventArgs e)
        {
            ScanRequested?.Invoke();
        }

        private void RailList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncing || _vm == null) return;
            InstanceRailRow row = RailList.SelectedItem as InstanceRailRow;
            if (row != null) RowSelected?.Invoke(row);
        }

        /// <summary>把视图选中同步到视图模型状态（重建行后恢复高亮用）。
        /// 目标行已是当前选中项时不再赋值/滚动——配合视图模型的稳定态跳过重建，
        /// 周期刷新在无变化时对列表零触碰（消除等间隔高亮闪烁与滚动微跳）。</summary>
        public void SyncSelection(string id)
        {
            if (_vm == null) return;
            _syncing = true;
            try
            {
                InstanceRailRow row = string.IsNullOrEmpty(id)
                    ? null
                    : _vm.Rows.FirstOrDefault(r => r.Id == id);
                if (ReferenceEquals(RailList.SelectedItem, row)) return;
                RailList.SelectedItem = row;
                if (row != null) RailList.ScrollIntoView(row);
            }
            finally { _syncing = false; }
        }
    }
}
