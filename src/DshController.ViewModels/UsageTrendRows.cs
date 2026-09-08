// ============================================================================
//  UsageTrendRows — 档案详情趋势图与可访问表格共用的展示行（无 UI 类型）
// ============================================================================

using CommunityToolkit.Mvvm.Input;

namespace DshController.ViewModels
{
    public sealed class UsageKpiRow
    {
        public string Kind { get; set; } = "";
        public string Label { get; set; } = "";
        public string Value { get; set; } = "—";
        public string Detail { get; set; } = "";
    }

    public sealed class UsageTrendPoint
    {
        public string StartDay { get; set; } = "";
        public string EndDay { get; set; } = "";
        public string Label { get; set; } = "";
        public long Tokens { get; set; }
        public long Requests { get; set; }
        public double AverageTtftMs { get; set; } = -1;
        public string TokensText { get; set; } = "0";
        public string RequestsText { get; set; } = "0";
        public string TtftText { get; set; } = "—";
        public string ToolTip { get; set; } = "";
        public IRelayCommand SelectCommand { get; set; }
    }
}
