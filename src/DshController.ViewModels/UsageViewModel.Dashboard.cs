// ============================================================================
//  UsageViewModel.Dashboard — 总计页范围、趋势指标与区间钻取（纯内存）
// ============================================================================

using System;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.Input;
using DshController.Core.Usage;

namespace DshController.ViewModels
{
    public partial class UsageViewModel
    {
        private DateTime? _rangeFrom;
        private DateTime? _rangeTo;
        private DateTime? _selectedFrom;
        private DateTime? _selectedTo;

        [RelayCommand]
        private void SetUsageRange(string key) => SetDashboardRange(key, reload: true);

        [RelayCommand]
        private void ApplyCustomUsageRange()
        {
            if (!UsageRangeStart.HasValue || !UsageRangeEnd.HasValue)
            {
                UsageRangeError = "请选择开始和结束日期。";
                return;
            }
            DateTime from = UsageRangeStart.Value.LocalDateTime.Date;
            DateTime to = UsageRangeEnd.Value.LocalDateTime.Date;
            if (from > to)
            {
                UsageRangeError = "结束日期不能早于开始日期。";
                return;
            }
            UsageRangeKey = "custom";
            RangeDays = -1;
            RangeLabel = from.ToString("yyyy-MM-dd") + " — " + to.ToString("yyyy-MM-dd");
            _rangeFrom = from;
            _rangeTo = to;
            UsageRangeError = "";
            Reload();
        }

        [RelayCommand]
        private void SetTrendMetric(string metric)
        {
            TrendMetric = metric == "requests" ? "requests" : "tokens";
            TrendTotalText = TrendMetric == "requests" ? RequestsText : TotalTokensText;
        }

        private void SetDashboardRange(string key, bool reload)
        {
            UsageRangeKey = key ?? "7";
            UsageRangeError = "";
            DateTime today = DateTime.Now.Date;
            switch (UsageRangeKey)
            {
                case "day": ApplyRange(today, today, "今天", 1); break;
                case "week": ApplyRange(today.AddDays(-((int)today.DayOfWeek + 6) % 7), today, "本周", 7); break;
                case "month": ApplyRange(new DateTime(today.Year, today.Month, 1), today, "本月", today.Day); break;
                case "all":
                case "0": ApplyRange(null, null, "全部时间", 0); break;
                case "custom":
                    UsageRangeStart ??= today.AddDays(-6);
                    UsageRangeEnd ??= today;
                    if (reload) ApplyCustomUsageRange();
                    return;
                case "30": ApplyRange(today.AddDays(-29), today, "最近 30 天", 30); break;
                default: ApplyRange(today.AddDays(-6), today, "近 7 天", 7); break;
            }
            if (reload) Reload();
        }

        private void ApplyRange(DateTime? from, DateTime? to, string label, int days)
        {
            _rangeFrom = from;
            _rangeTo = to;
            RangeDays = days;
            RangeLabel = label;
            UsageRangeStart = from;
            UsageRangeEnd = to;
        }

        private void SelectTrendPoint(UsageTrendPoint point)
        {
            if (point == null || !TryDay(point.StartDay, out DateTime from) || !TryDay(point.EndDay, out DateTime to)) return;
            if (_selectedFrom == from && _selectedTo == to) { ResetDrill(); return; }
            _selectedFrom = from;
            _selectedTo = to;
            SelectedDay = point.Label;
            HasDay = true;
            DayStripText = point.Label + "：总 Token " + point.TokensText + " · 请求 " + point.RequestsText + " · 平均首 Token " + point.TtftText;
            DayModelsText = "范围内模型：" + string.Join(" · ", _summary.Models.Take(3).Select(m => m.DisplayName));
            RebuildSessions(filtered: true);
        }

        private static bool TryDay(string value, out DateTime day) => DateTime.TryParseExact(
            value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day);
    }
}
