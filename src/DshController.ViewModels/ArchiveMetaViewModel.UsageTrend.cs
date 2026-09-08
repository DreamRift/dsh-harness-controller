// ============================================================================
//  ArchiveMetaViewModel.UsageTrend — 档案详情的范围、KPI 与趋势桶（纯内存）
// ============================================================================

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.Input;
using DshController.Core.Usage;

namespace DshController.ViewModels
{
    public sealed partial class ArchiveMetaViewModel
    {
        public ObservableCollection<UsageKpiRow> Kpis { get; } = new ObservableCollection<UsageKpiRow>();
        public ObservableCollection<UsageTrendPoint> TrendPoints { get; } = new ObservableCollection<UsageTrendPoint>();
        private DateTime? _rangeFrom;
        private DateTime? _rangeTo;
        private DateTime? _selectedFrom;
        private DateTime? _selectedTo;

        [RelayCommand]
        private void SetUsageRange(string key)
        {
            UsageRangeKey = key ?? "7";
            UsageRangeError = "";
            DateTime today = DateTime.Now.Date;
            switch (UsageRangeKey)
            {
                case "day": SetRange(today, today, "今天"); break;
                case "week": SetRange(today.AddDays(-((int)today.DayOfWeek + 6) % 7), today, "本周"); break;
                case "month": SetRange(new DateTime(today.Year, today.Month, 1), today, "本月"); break;
                case "custom":
                    UsageRangeLabel = "自定义";
                    UsageRangeStart ??= today.AddDays(-6);
                    UsageRangeEnd ??= today;
                    ApplyCustomUsageRange();
                    break;
                default: SetRange(today.AddDays(-6), today, "近 7 天"); break;
            }
        }

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
            UsageRangeError = "";
            SetRange(from, to, from.ToString("yyyy-MM-dd") + " — " + to.ToString("yyyy-MM-dd"));
        }

        [RelayCommand]
        private void SetTrendMetric(string metric)
        {
            TrendMetric = metric == "requests" ? "requests" : "tokens";
            TrendTotalText = TrendMetric == "requests" ? RequestsText : TotalTokensText;
        }

        private void SetRange(DateTime from, DateTime to, string label)
        {
            _rangeFrom = from;
            _rangeTo = to;
            UsageRangeStart = from;
            UsageRangeEnd = to;
            UsageRangeLabel = label;
            RebuildUsageRange();
        }

        private void RebuildUsageRange()
        {
            if (_currentUsage == null) return;
            _usageSummary = UsageQuery.Summarize(new[] { _currentUsage }, _rangeFrom, _rangeTo);
            ApplyUsageSummary(_usageSummary);
        }

        private void ApplyUsageSummary(UsageSummary summary)
        {
            TotalTokensText = UsageQuery.FormatTokens(summary.Totals.Total);
            RequestsText = summary.ModelsComplete ? summary.RequestCount.ToString("N0") : summary.RequestCount.ToString("N0") + "+";
            SessionsText = summary.SessionCount.ToString("N0");
            HitRateText = summary.CacheHitRate < 0 ? "—" : (summary.CacheHitRate * 100).ToString("0.#") + "%";
            ActiveDaysText = summary.ActiveDays.ToString("N0");
            TopModelText = string.IsNullOrEmpty(summary.TopModel) ? "—" : summary.TopModel;
            AverageTtftText = UsageDashboardBuilder.FormatTtft(summary.Latency.AverageTtftMs);
            TtftSampleText = summary.Latency.Samples > 0 ? summary.Latency.Samples.ToString("N0") + " 个有效步骤样本" : "无有效步骤样本";
            TrendTotalText = TrendMetric == "requests" ? RequestsText : TotalTokensText;
            BuildKpis();
            BuildMetaHeroBars(summary);
            BuildMetaBars(summary);
            BuildTrend(summary);

            Models.Clear();
            long grand = summary.Models.Sum(m => m.Totals.Total);
            foreach (UsageModelStat model in summary.Models.Take(30)) Models.Add(UsageModelRow.From(model, grand));
            ResetUsageDrill();
        }

        private void BuildKpis()
        {
            Kpis.Clear();
            foreach (UsageKpiRow row in UsageDashboardBuilder.BuildKpis(_usageSummary)) Kpis.Add(row);
        }

        private void BuildTrend(UsageSummary summary)
        {
            TrendPoints.Clear();
            if (!_rangeFrom.HasValue || !_rangeTo.HasValue) return;
            foreach (UsageTrendPoint point in UsageDashboardBuilder.BuildTrend(summary, _rangeFrom.Value, _rangeTo.Value))
            {
                point.SelectCommand = new RelayCommand(() => SelectTrendPoint(point));
                TrendPoints.Add(point);
            }
        }

        private void SelectTrendPoint(UsageTrendPoint point)
        {
            if (point == null || !TryDay(point.StartDay, out DateTime from) || !TryDay(point.EndDay, out DateTime to)) return;
            if (_selectedFrom == from && _selectedTo == to) { ResetUsageDrill(); return; }
            _selectedFrom = from;
            _selectedTo = to;
            SelectedDay = point.Label;
            HasDay = true;
            DayStripText = point.Label + "：总 Token " + point.TokensText + " · 请求 " + point.RequestsText + " · 平均首 Token " + point.TtftText;
            DayModelsText = "范围内模型：" + string.Join(" · ", _usageSummary.Models.Take(3).Select(m => m.DisplayName));
            RebuildMetaSessions(filtered: true);
        }

        private static bool TryDay(string text, out DateTime day) => DateTime.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day);
    }
}
