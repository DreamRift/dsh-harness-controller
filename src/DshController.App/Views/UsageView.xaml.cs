// ============================================================================
//  UsageView — 用量统计页（重构 2.0 / P2）
//
//  样板意义：code-behind 只剩"注入 ViewModel + 生命周期转发"两件事，
//  没有任何状态字段、没有格式化、没有数据访问；界面状态全部经 x:Bind 绑定。
//  后续页面按这个形状迁移（P3）。
// ============================================================================

using DshController.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace DshController.Views
{
    public sealed partial class UsageView : UserControl
    {
        public UsageView()
        {
            InitializeComponent();
        }

        public UsageViewModel ViewModel { get; private set; }

        /// <summary>MainWindow 构造后注入依赖。</summary>
        public void Init(UsageViewModel viewModel)
        {
            ViewModel = viewModel;
            Bindings.Update();   // x:Bind 在 ViewModel 赋值后需要显式刷新一次
        }

        /// <summary>切到本页：重建范围列表并从档案渲染（不触发采集）。</summary>
        public void OnShown()
        {
            ViewModel?.OnShown();
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