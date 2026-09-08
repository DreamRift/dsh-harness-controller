// ============================================================================
//  UsageDashboardBuilder — 总量/单档案共用的 Cockpit 风格 KPI 与趋势数据
//  纯内存、纯 ViewModel 层：不识别 WinUI，也不读档案或文件。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DshController.Core.Usage;

namespace DshController.ViewModels
{
    public static class UsageDashboardBuilder
    {
        public static List<UsageKpiRow> BuildKpis(UsageSummary summary)
        {
            summary ??= new UsageSummary();
            string tokens = UsageQuery.FormatTokens(summary.Totals.Total);
            string requests = summary.ModelsComplete ? summary.RequestCount.ToString("N0") : summary.RequestCount.ToString("N0") + "+";
            string ttft = FormatTtft(summary.Latency.AverageTtftMs);
            string sample = summary.Latency.Samples > 0 ? summary.Latency.Samples.ToString("N0") + " 个有效步骤样本" : "无有效步骤样本";
            return new List<UsageKpiRow>
            {
                new UsageKpiRow { Kind = "requests", Label = "总请求数", Value = requests, Detail = "来自会话日志" },
                new UsageKpiRow { Kind = "images", Label = "图片请求", Value = "—", Detail = "当前档案未采集" },
                new UsageKpiRow { Kind = "tokens", Label = "总 Token 数", Value = tokens, Detail = BucketDetail(summary.Totals, summary.Totals.Total) },
                new UsageKpiRow { Kind = "value", Label = "估算价值", Value = "—", Detail = "待接入价格表" },
                new UsageKpiRow { Kind = "latency", Label = "平均首 Token", Value = ttft, Detail = sample }
            };
        }

        public static List<UsageTrendPoint> BuildTrend(UsageSummary summary, DateTime from, DateTime to)
        {
            var output = new List<UsageTrendPoint>();
            if (to < from) return output;
            int days = (to - from).Days + 1;
            if (days <= 90)
            {
                for (DateTime day = from; day <= to; day = day.AddDays(1)) AddPoint(output, summary, day, day);
            }
            else
            {
                for (DateTime week = WeekStart(from); week <= to; week = week.AddDays(7))
                    AddPoint(output, summary, week < from ? from : week, week.AddDays(6) > to ? to : week.AddDays(6));
            }
            return output;
        }

        public static DateTime WindowStart(UsageSummary summary, DateTime today)
        {
            if (summary?.Daily == null || summary.Daily.Count == 0) return today;
            return DateTime.TryParseExact(summary.Daily.Keys.First(), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out DateTime day) ? day : today;
        }

        public static string FormatTtft(double ms) => ms < 0 ? "—" : ms < 1000
            ? Math.Round(ms).ToString("N0") + " ms" : (ms / 1000).ToString("0.##") + " s";

        private static void AddPoint(List<UsageTrendPoint> output, UsageSummary summary, DateTime from, DateTime to)
        {
            var tokens = new TokenBuckets();
            var latency = new UsageLatencyStats();
            long requests = 0;
            for (DateTime day = from; day <= to; day = day.AddDays(1))
            {
                string key = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                if (summary.Daily.TryGetValue(key, out TokenBuckets bucket)) tokens.Add(bucket);
                if (summary.DailyRequests.TryGetValue(key, out long count)) requests += count;
                if (summary.DailyLatency.TryGetValue(key, out UsageLatencyStats stats)) latency.Add(stats);
            }
            string label = from == to ? from.ToString("M/d") : from.ToString("M/d") + "–" + to.ToString("M/d");
            string tokenText = UsageQuery.FormatTokens(tokens.Total);
            string requestText = requests.ToString("N0");
            string ttftText = FormatTtft(latency.AverageTtftMs);
            output.Add(new UsageTrendPoint
            {
                StartDay = from.ToString("yyyy-MM-dd"), EndDay = to.ToString("yyyy-MM-dd"), Label = label,
                Tokens = tokens.Total, Requests = requests, AverageTtftMs = latency.AverageTtftMs,
                TokensText = tokenText, RequestsText = requestText, TtftText = ttftText,
                ToolTip = label + "\n总 Token 数  " + tokenText + "\n总请求数  " + requestText + "\n平均首 Token  " + ttftText
            });
        }

        private static DateTime WeekStart(DateTime day) => day.AddDays(-((int)day.DayOfWeek + 6) % 7);

        private static string BucketDetail(TokenBuckets buckets, long total)
        {
            if (total <= 0) return "未缓存/缓存/输出";
            return "输入 " + UsageQuery.FormatTokens(buckets.UncachedInput + buckets.CacheRead + buckets.CacheWrite) +
                   " / 输出 " + UsageQuery.FormatTokens(buckets.Output);
        }
    }
}
