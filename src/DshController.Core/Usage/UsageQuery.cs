// ============================================================================
//  UsageQuery — 用量的范围查询与汇总（重构 2.0 / P2）
//
//  原型分支里"遍历模型 → 按范围筛每日 → 累加四桶"这段逻辑重复了 4 处
//  （聚合、面板范围、趋势图、环形图各写一遍），口径一旦要改就是四处同步。
//  这里收敛成一个函数：给定 usage 分面数据 + 时间范围 → 一份汇总结果。
//  范围为空 = 全部（用 projcache 的权威总量，不受会话日志解析完整度影响）。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;

namespace DshController.Core.Usage
{
    /// <summary>范围汇总结果（KPI + 模型排行 + 按天序列 + 会话明细）。</summary>
    public sealed class UsageSummary
    {
        public TokenBuckets Totals { get; set; } = new TokenBuckets();
        public int SessionCount { get; set; }
        public int RequestCount { get; set; }
        public int ActiveDays { get; set; }
        public bool ModelsComplete { get; set; }
        public string TopModel { get; set; } = "";
        public List<UsageModelStat> Models { get; set; } = new List<UsageModelStat>();
        public List<UsageSessionStat> Sessions { get; set; } = new List<UsageSessionStat>();
        public UsageLatencyStats Latency { get; set; } = new UsageLatencyStats();

        /// <summary>按天升序的每日总量（key = yyyy-MM-dd）。</summary>
        public SortedDictionary<string, TokenBuckets> Daily { get; set; } =
            new SortedDictionary<string, TokenBuckets>(StringComparer.Ordinal);

        public SortedDictionary<string, long> DailyRequests { get; set; } =
            new SortedDictionary<string, long>(StringComparer.Ordinal);

        public SortedDictionary<string, UsageLatencyStats> DailyLatency { get; set; } =
            new SortedDictionary<string, UsageLatencyStats>(StringComparer.Ordinal);

        /// <summary>输入侧缓存命中率；无输入返回 -1。</summary>
        public double CacheHitRate => Totals.CacheHitRate;
    }

    public static class UsageQuery
    {
        /// <summary>
        /// 汇总一批实例的用量数据。fromDay/toDay 为 null = 不限（此时总量取 projcache 权威口径）。
        /// 指定范围时按会话日志的每日数据聚合，会话按创建时间过滤。
        /// </summary>
        public static UsageSummary Summarize(IEnumerable<UsageFacetData> sources,
            DateTime? fromLocal = null, DateTime? toLocal = null)
        {
            var summary = new UsageSummary { ModelsComplete = true };
            var modelMap = new Dictionary<string, UsageModelStat>(StringComparer.Ordinal);
            bool ranged = fromLocal.HasValue || toLocal.HasValue;
            string fromKey = fromLocal?.ToString("yyyy-MM-dd");
            string toKey = toLocal?.ToString("yyyy-MM-dd");
            bool any = false;

            foreach (UsageFacetData data in sources ?? Enumerable.Empty<UsageFacetData>())
            {
                if (data == null) continue;
                any = true;
                if (!data.ModelsComplete) summary.ModelsComplete = false;

                foreach (UsageSessionStat s in data.Sessions ?? new List<UsageSessionStat>())
                {
                    if (s == null || !s.HasTokenUsage) continue;
                    if (ranged && !InRange(UsageParser.DayKey(s.CreatedAtMs), fromKey, toKey)) continue;
                    summary.Sessions.Add(s);
                    summary.SessionCount++;
                    if (ranged) summary.Totals.Add(s.Totals);
                }
                if (!ranged) summary.Totals.Add(data.Totals);
                if (!ranged) summary.Latency.Add(data.Latency);

                foreach (KeyValuePair<string, UsageLatencyStats> latency in data.DailyLatency ??
                    new Dictionary<string, UsageLatencyStats>())
                {
                    if (ranged && !InRange(latency.Key, fromKey, toKey)) continue;
                    if (!summary.DailyLatency.TryGetValue(latency.Key, out UsageLatencyStats target))
                    {
                        target = new UsageLatencyStats();
                        summary.DailyLatency[latency.Key] = target;
                    }
                    target.Add(latency.Value);
                    if (ranged) summary.Latency.Add(latency.Value);
                }

                foreach (UsageModelStat m in data.Models ?? new List<UsageModelStat>())
                {
                    string key = m.Provider + "/" + m.Model;
                    if (!modelMap.TryGetValue(key, out UsageModelStat agg))
                    {
                        agg = new UsageModelStat { Provider = m.Provider, Model = m.Model };
                        modelMap[key] = agg;
                    }
                    MergeModel(agg, m, ranged, fromKey, toKey);
                }
            }
            if (!any) return summary;

            summary.Models = modelMap.Values.Where(m => m.Requests > 0 || m.Totals.Total > 0)
                .OrderByDescending(m => m.Totals.Total).ToList();
            foreach (UsageModelStat m in summary.Models)
            {
                summary.RequestCount += m.Requests;
                foreach (KeyValuePair<string, TokenBuckets> kv in m.Daily)
                {
                    if (!summary.Daily.TryGetValue(kv.Key, out TokenBuckets day))
                    {
                        day = new TokenBuckets();
                        summary.Daily[kv.Key] = day;
                    }
                    day.Add(kv.Value);
                }
                foreach (KeyValuePair<string, long> kv in m.DailyRequests)
                    summary.DailyRequests[kv.Key] =
                        (summary.DailyRequests.TryGetValue(kv.Key, out long r) ? r : 0) + kv.Value;
            }

            summary.ActiveDays = summary.Daily.Count(kv => kv.Value.Total > 0);
            summary.TopModel = summary.Models.Count > 0 ? summary.Models[0].DisplayName : "";
            summary.Sessions.Sort((x, y) => y.CreatedAtMs.CompareTo(x.CreatedAtMs));
            return summary;
        }

        private static void MergeModel(UsageModelStat agg, UsageModelStat src,
            bool ranged, string fromKey, string toKey)
        {
            if (!ranged)
            {
                agg.Requests += src.Requests;
                agg.Totals.Add(src.Totals);
            }
            if (src.FirstMs > 0 && (agg.FirstMs == 0 || src.FirstMs < agg.FirstMs)) agg.FirstMs = src.FirstMs;
            if (src.LastMs > agg.LastMs) agg.LastMs = src.LastMs;

            foreach (KeyValuePair<string, TokenBuckets> kv in src.Daily ?? new Dictionary<string, TokenBuckets>())
            {
                if (ranged && !InRange(kv.Key, fromKey, toKey)) continue;
                if (!agg.Daily.TryGetValue(kv.Key, out TokenBuckets day))
                {
                    day = new TokenBuckets();
                    agg.Daily[kv.Key] = day;
                }
                day.Add(kv.Value);
                if (ranged) agg.Totals.Add(kv.Value);
            }
            foreach (KeyValuePair<string, long> kv in src.DailyRequests ?? new Dictionary<string, long>())
            {
                if (ranged && !InRange(kv.Key, fromKey, toKey)) continue;
                agg.DailyRequests[kv.Key] =
                    (agg.DailyRequests.TryGetValue(kv.Key, out long r) ? r : 0) + kv.Value;
                if (ranged) agg.Requests += (int)kv.Value;
            }
        }

        // ==================== 用量改版方案 §5 的三个钻取助手（纯内存，口径与 Summarize 对齐） ====================

        /// <summary>
        /// 按天升序合并各模型的每日数据为逐日总量序列（统一视图每实例的迷你趋势用）。
        /// 范围语义与 Summarize 一致：fromLocal/toLocal=本地日期闭区间，null=不限；空区间（from&gt;to）→ 空结果。
        /// </summary>
        public static SortedDictionary<string, TokenBuckets> MergeDaily(
            IEnumerable<UsageModelStat> models, DateTime? fromLocal = null, DateTime? toLocal = null)
        {
            var daily = new SortedDictionary<string, TokenBuckets>(StringComparer.Ordinal);
            string fromKey = fromLocal?.ToString("yyyy-MM-dd");
            string toKey = toLocal?.ToString("yyyy-MM-dd");
            foreach (UsageModelStat m in models ?? Enumerable.Empty<UsageModelStat>())
            {
                if (m == null || m.Daily == null) continue;
                foreach (KeyValuePair<string, TokenBuckets> kv in m.Daily)
                {
                    if (!InRange(kv.Key, fromKey, toKey)) continue;
                    if (!daily.TryGetValue(kv.Key, out TokenBuckets day))
                    {
                        day = new TokenBuckets();
                        daily[kv.Key] = day;
                    }
                    day.Add(kv.Value);
                }
            }
            return daily;
        }

        /// <summary>当日模型构成的一行（Key=provider/model；Share=该模型当日总量/当日合计）。</summary>
        public sealed class ModelDayShare
        {
            public string Key { get; set; } = "";
            public string DisplayName { get; set; } = "";
            public TokenBuckets Totals { get; set; } = new TokenBuckets();
            public double Share { get; set; }
        }

        /// <summary>
        /// 某一天的下钻构成（dayKey=yyyy-MM-dd，本地日期，与 UsageParser.DayKey 同口径）：
        /// 当日有 tokens 的模型按当日量降序 + 占比；非法键/当日无数据 → 空列表（调用方显示空态）。
        /// </summary>
        public static List<ModelDayShare> DayComposition(
            IEnumerable<UsageModelStat> models, string dayKey)
        {
            var list = new List<ModelDayShare>();
            if (string.IsNullOrEmpty(dayKey) || models == null) return list;
            long dayTotal = 0;
            foreach (UsageModelStat m in models)
            {
                if (m == null || m.Daily == null) continue;
                if (!m.Daily.TryGetValue(dayKey, out TokenBuckets b) || b == null || b.Total <= 0) continue;
                list.Add(new ModelDayShare
                {
                    Key = m.Provider + "/" + m.Model,
                    DisplayName = m.DisplayName,
                    Totals = b
                });
                dayTotal += b.Total;
            }
            if (dayTotal <= 0) return new List<ModelDayShare>();
            foreach (ModelDayShare s in list) s.Share = (double)s.Totals.Total / dayTotal;
            list.Sort((x, y) => y.Totals.Total.CompareTo(x.Totals.Total));
            return list;
        }

        /// <summary>
        /// 某本地日的会话列表（点按天柱钻取会话明细用）：dayKey 口径同 UsageParser.DayKey；
        /// 非法键/空输入 → 空列表；与 Summarize 一致按创建时间倒序。
        /// </summary>
        public static List<UsageSessionStat> SessionsOnDay(
            IEnumerable<UsageSessionStat> sessions, string dayKey)
        {
            var list = new List<UsageSessionStat>();
            if (string.IsNullOrEmpty(dayKey) || sessions == null) return list;
            foreach (UsageSessionStat s in sessions)
            {
                if (s == null || !s.HasTokenUsage) continue;
                if (UsageParser.DayKey(s.CreatedAtMs) == dayKey) list.Add(s);
            }
            list.Sort((x, y) => y.CreatedAtMs.CompareTo(x.CreatedAtMs));
            return list;
        }

        /// <summary>日期键是否落在闭区间内（两端可空）。</summary>
        public static bool InRange(string dayKey, string fromKey, string toKey)
        {
            if (string.IsNullOrEmpty(dayKey)) return false;
            if (fromKey != null && string.CompareOrdinal(dayKey, fromKey) < 0) return false;
            if (toKey != null && string.CompareOrdinal(dayKey, toKey) > 0) return false;
            return true;
        }

        /// <summary>把 token 数格式化成人读单位（中文：亿 / 万）。</summary>
        public static string FormatTokens(long v)
        {
            if (v >= 100000000) return (v / 100000000.0).ToString("0.##") + " 亿";
            if (v >= 10000) return (v / 10000.0).ToString("0.##") + " 万";
            return v.ToString("N0");
        }
    }
}
