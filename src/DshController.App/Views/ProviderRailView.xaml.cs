// ============================================================================
//  ProviderRailView — API 页左栏（提供方列表）View 侧薄壳：注入 VM + 选中转发 +
//  添加事件转发。零逻辑零取数（InstancesRailView 同型）。
// ============================================================================

using System;
using DshController.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace DshController.Views
{
    public sealed partial class ProviderRailView : UserControl
    {
        private ProviderPresetsViewModel _vm;
        private bool _syncing;

        /// <summary>「添加提供方」动作（App 层转发到 VM.BeginCreate）。</summary>
        public event Action AddRequested;

        public ProviderRailView()
        {
            InitializeComponent();
        }

        public void Bind(ProviderPresetsViewModel vm)
        {
            _vm = vm ?? throw new ArgumentNullException(nameof(vm));
            ProviderList.ItemsSource = _vm.Rows;
            SyncSelection(_vm.SelectedId);
        }

        /// <summary>把 VM 选中同步回列表（代码回设/保存后恢复高亮用）。</summary>
        public void SyncSelection(string id)
        {
            if (_vm == null) return;
            _syncing = true;
            try
            {
                ProviderPresetRow row = string.IsNullOrEmpty(id) ? null : _vm.RowById(id);
                ProviderList.SelectedItem = row;
                if (row != null) ProviderList.ScrollIntoView(row);
            }
            finally { _syncing = false; }
        }

        private void BtnProviderAdd_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
            => AddRequested?.Invoke();

        private void ProviderList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncing || _vm == null) return;
            if (ProviderList.SelectedItem is ProviderPresetRow row) _vm.Select(row.Id);
        }
    }
}
