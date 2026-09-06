// ============================================================================
//  ProviderPresetsView — API 页详情区 View 侧薄壳（照 dsh 模型页适配）：
//  注入 VM + 生命周期转发 + 卡片内轻动作转发（增删模型行/取消/保存直达 VM）；
//  弹窗与网络动作（同步/删除确认/获取模型）经事件交 App 层（MainWindow.ApiPresets）。
// ============================================================================

using System;
using DshController.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DshController.Views
{
    public sealed partial class ProviderPresetsView : UserControl
    {
        /// <summary>空态「添加提供方」。</summary>
        public event Action AddRequested;
        /// <summary>「同步到实例…」（App 层弹预览确认）。</summary>
        public event Action<string> SyncRequested;
        /// <summary>「删除提供方」（App 层弹确认）。</summary>
        public event Action<string> DeleteRequested;
        /// <summary>「获取可用模型」（App 层发起 Core 探针 + 勾选弹窗）。</summary>
        public event Action FetchRequested;

        public ProviderPresetsView()
        {
            InitializeComponent();
        }

        public ProviderPresetsViewModel ViewModel { get; private set; }

        /// <summary>MainWindow 构造后注入依赖。</summary>
        public void Bind(ProviderPresetsViewModel vm)
        {
            ViewModel = vm ?? throw new ArgumentNullException(nameof(vm));
            Bindings.Update();   // x:Bind 在 ViewModel 赋值后需要显式刷新一次
        }

        // ---- 字符串→可见性（校验/提示行：空串收起） ----
        public Visibility NonEmpty(string text)
            => string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;

        public Visibility Empty(string text)
            => string.IsNullOrEmpty(text) ? Visibility.Visible : Visibility.Collapsed;

        // ---- 轻动作转发（直达 VM，无弹窗无网络） ----

        private void BtnPresetAdd_Click(object sender, RoutedEventArgs e) => AddRequested?.Invoke();

        private void PresetSave_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel != null) ViewModel.TrySaveEditor(out _);
        }

        private void PresetCancel_Click(object sender, RoutedEventArgs e) => ViewModel?.CancelEditor();

        private void PresetModelAdd_Click(object sender, RoutedEventArgs e) => ViewModel?.Editor?.AddModelRow();

        private void RemoveModelRow_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is DraftModelRow row)
                ViewModel?.Editor?.RemoveModelRow(row);
        }

        // ---- 重动作转发（弹窗/网络在 App 层） ----

        private void BtnPresetSync_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel != null && ViewModel.HasSelection) SyncRequested?.Invoke(ViewModel.SelectedId);
        }

        private void BtnPresetDelete_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel != null && ViewModel.HasSelection) DeleteRequested?.Invoke(ViewModel.SelectedId);
        }

        private void PresetFetch_Click(object sender, RoutedEventArgs e) => FetchRequested?.Invoke();
    }
}
