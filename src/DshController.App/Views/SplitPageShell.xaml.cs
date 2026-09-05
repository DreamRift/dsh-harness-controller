// ============================================================================
//  SplitPageShell — 两栏页面壳的依赖属性（纯展示，无逻辑；说明见 XAML 头注释）
// ============================================================================

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DshController.Views
{
    public sealed partial class SplitPageShell : UserControl
    {
        public SplitPageShell()
        {
            InitializeComponent();
        }

        public static readonly DependencyProperty LeftEyebrowProperty =
            DependencyProperty.Register(nameof(LeftEyebrow), typeof(string), typeof(SplitPageShell),
                new PropertyMetadata(""));

        public static readonly DependencyProperty LeftContentProperty =
            DependencyProperty.Register(nameof(LeftContent), typeof(object), typeof(SplitPageShell),
                new PropertyMetadata(null));

        public static readonly DependencyProperty BodyContentProperty =
            DependencyProperty.Register(nameof(BodyContent), typeof(object), typeof(SplitPageShell),
                new PropertyMetadata(null));

        /// <summary>左栏顶部小标（页面名，PageEyebrow 形）。</summary>
        public string LeftEyebrow
        {
            get => (string)GetValue(LeftEyebrowProperty);
            set => SetValue(LeftEyebrowProperty, value);
        }

        /// <summary>左栏内容（列表/树；StackPanel、ItemsRepeater 均可）。</summary>
        public object LeftContent
        {
            get => GetValue(LeftContentProperty);
            set => SetValue(LeftContentProperty, value);
        }

        /// <summary>主内容区（页面宿主）。</summary>
        public object BodyContent
        {
            get => GetValue(BodyContentProperty);
            set => SetValue(BodyContentProperty, value);
        }
    }
}
