// ============================================================================
//  ProviderPresetsView — 供应商预设编辑页 View 侧薄壳：注入 VM + 选中转发 +
//  事件转发（弹窗在 App 层 DialogService）。零逻辑零取数。
// ============================================================================

using System;
using DshController.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace DshController.Views
{
    public sealed partial class ProviderPresetsView : UserControl
    {
        private ProviderPresetsViewModel _vm;
        private bool _syncing;

        public event Action AddRequested;
        public event Action<string> EditRequested;
        public event Action<string> DeleteRequested;
        public event Action<string> SyncRequested;

        public ProviderPresetsView()
        {
            InitializeComponent();
        }

        public void Bind(ProviderPresetsViewModel vm)
        {
            _vm = vm ?? throw new ArgumentNullException(nameof(vm));
            PresetList.ItemsSource = _vm.Rows;
        }

        public void RefreshActionStates()
        {
            bool hasSel = _vm != null && !string.IsNullOrEmpty(_vm.SelectedId);
            if (BtnPresetEdit != null) BtnPresetEdit.IsEnabled = hasSel;
            if (BtnPresetDelete != null) BtnPresetDelete.IsEnabled = hasSel;
            if (BtnPresetSync != null) BtnPresetSync.IsEnabled = hasSel;
        }

        /// <summary>把视图选中同步到 VM（代码回设/编辑后恢复高亮用）。</summary>
        public void SyncSelection(string id)
        {
            if (_vm == null) return;
            _syncing = true;
            try
            {
                ProviderPresetRow row = string.IsNullOrEmpty(id) ? null : _vm.RowById(id);
                PresetList.SelectedItem = row;
                if (row != null) PresetList.ScrollIntoView(row);
            }
            finally { _syncing = false; }
            RefreshActionStates();
        }

        private void PresetList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncing || _vm == null) return;
            ProviderPresetRow row = PresetList.SelectedItem as ProviderPresetRow;
            if (row != null) _vm.Select(row.Id);
            RefreshActionStates();
        }

        private void BtnPresetAdd_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => AddRequested?.Invoke();

        private void BtnPresetEdit_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
            if (_vm != null && !string.IsNullOrEmpty(_vm.SelectedId)) EditRequested?.Invoke(_vm.SelectedId);
        }

        private void BtnPresetDelete_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
            if (_vm != null && !string.IsNullOrEmpty(_vm.SelectedId)) DeleteRequested?.Invoke(_vm.SelectedId);
        }

        private void BtnPresetSync_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
            if (_vm != null && !string.IsNullOrEmpty(_vm.SelectedId)) SyncRequested?.Invoke(_vm.SelectedId);
        }
    }
}
