// ============================================================================
//  用量改版方案 §5 三个钻取助手的离线单测（UsageQuery 纯函数）
//
//  验收锚=盘档规格：按天序列/按模型聚合/会话列表 三类查询各有
//  「空区间 / 边界日 / 无数据」断言；相邻锚=与 Summarize 的口径必须一致
//  （本地日键、闭区间、降序），用同输入对照 Summarize 结果钉死。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using DshController.Core.Usage;
using Xunit;

namespace DshController.Tests
{
    public class UsageDrilldownQueryTests
    {
        private static readonly DateTime Today = DateTime.Now.Date;
        private static string Day(int offset) => Today.AddDays(offset).ToString("yyyy-MM-dd");
        private static long MsAt(int dayOffset, int hour = 10, int min = 0, int sec = 0)
            => new DateTimeOffset(Today.AddDays(dayOffset).AddHours(hour).AddMinutes(min).AddSeconds(sec),
                                  TimeZoneInfo.Local.GetUtcOffset(Today.AddDays(dayOffset).AddHours(hour))).ToUnixTimeMilliseconds();

        private static UsageModelStat Model(string name, params (int day, long tokens)[] days)
        {
            var m = new UsageModelStat { Provider = "deepseek", Model = name };
            foreach ((int day, long tokens) in days)
            {
                m.Daily[Day(day)] = new TokenBuckets { UncachedInput = tokens / 2, CacheRead = tokens / 4, Output = tokens / 4 };
                m.DailyRequests[Day(day)] = 2;
                m.Requests += 2; m.Totals.Add(m.Daily[Day(day)]);
            }
            return m;
        }

        // ==================== ① 按天序列 MergeDaily ====================

        [Fact]
        public void 按天序列_空区间返回空()
        {
            var models = new[] { Model("chat", (-2, 100), (-1, 200)) };
            Assert.Empty(UsageQuery.MergeDaily(models, Today, Today.AddDays(-1)));  // from>to
            Assert.Empty(UsageQuery.MergeDaily(null));
            Assert.Empty(UsageQuery.MergeDaily(new UsageModelStat[0], null, null));
        }

        [Fact]
        public void 按天序列_边界日闭区间与Summarize口径一致()
        {
            UsageModelStat m = Model("chat", (-2, 100), (-1, 200), (0, 300));
            var models = new[] { m };
            // 单点闭区间 from==to：只剩边界当天
            var one = UsageQuery.MergeDaily(models, Today.AddDays(-1), Today.AddDays(-1));
            Assert.Equal(200, one[Day(-1)].Total);
            Assert.False(one.ContainsKey(Day(-2)));
            // 与 Summarize 的 Daily 对照（同范围同输入）
            UsageSummary s = UsageQuery.Summarize(new[] { FacetWith(m) }, Today.AddDays(-1), Today);
            var merged = UsageQuery.MergeDaily(models, Today.AddDays(-1), Today);
            Assert.Equal(s.Daily.ToDictionary(kv => kv.Key, kv => kv.Value.Total),
                         merged.ToDictionary(kv => kv.Key, kv => kv.Value.Total));
        }

        [Fact]
        public void 按天序列_无数据实例返回空且跨模型合并()  // 无 Daily / null 项 / 同日两模型
        {
            Assert.Empty(UsageQuery.MergeDaily(new[] { new UsageModelStat() }));
            Assert.Empty(UsageQuery.MergeDaily(new UsageModelStat[] { null }));
            var two = new[] { Model("a", (-1, 100)), Model("b", (-1, 48)) };  // 48=可整除切桶，免整数除法噪声
            var merged = UsageQuery.MergeDaily(two);
            Assert.Equal(148, merged[Day(-1)].Total);
            Assert.Equal(74 + 37 + 37, merged[Day(-1)].UncachedInput + merged[Day(-1)].CacheRead + merged[Day(-1)].Output);
        }

        // ==================== ② 当日模型构成 DayComposition ====================

        [Fact]
        public void 当日构成_非法日键与无数据返回空()
        {
            var models = new[] { Model("chat", (-1, 200)) };
            Assert.Empty(UsageQuery.DayComposition(models, ""));
            Assert.Empty(UsageQuery.DayComposition(models, null));
            Assert.Empty(UsageQuery.DayComposition(models, "2019-01-01"));      // 当日无数据
            Assert.Empty(UsageQuery.DayComposition(new UsageModelStat[0], Day(-1)));
        }

        [Fact]
        public void 当日构成_只取该日_占比降序()
        {
            var models = new[] { Model("big", (-1, 1200), (-2, 9996)), Model("small", (-1, 400)) };
            var comp = UsageQuery.DayComposition(models, Day(-1));
            Assert.Equal(2, comp.Count);
            Assert.Equal("big", comp[0].DisplayName);                            // 降序
            Assert.Equal(0.75, comp[0].Share, 3);
            Assert.Equal(0.25, comp[1].Share, 3);
            Assert.Equal(1600, comp.Sum(c => c.Totals.Total));                   // 边界日 -2 的大数未被混入
            Assert.Equal("deepseek/small", comp[1].Key);
        }

        [Fact]
        public void 当日构成_当日有键但零桶不计入()
        {
            var m = new UsageModelStat { Provider = "p", Model = "z" };
            m.Daily[Day(-1)] = new TokenBuckets();
            Assert.Empty(UsageQuery.DayComposition(new[] { m }, Day(-1)));
        }

        // ==================== ③ 会话按日 SessionsOnDay ====================

        private static UsageSessionStat Session(string id, long ms) =>
            new UsageSessionStat { SessionId = id, CreatedAtMs = ms, Totals = new TokenBuckets { Output = 1 } };

        [Fact]
        public void 会话按日_空输入与非法键返回空()
        {
            Assert.Empty(UsageQuery.SessionsOnDay(null, Day(-1)));
            Assert.Empty(UsageQuery.SessionsOnDay(new List<UsageSessionStat>(), Day(-1)));
            Assert.Empty(UsageQuery.SessionsOnDay(new[] { Session("a", MsAt(-1)) }, ""));
        }

        [Fact]
        public void 会话按日_边界归属与倒序()
        {
            var sessions = new[]
            {
                Session("早", MsAt(-1, 0, 0, 0)),
                Session("深夜", MsAt(-1, 23, 59, 59)),
                Session("次日零点", MsAt(0, 0, 0, 0)),
                Session("正午", MsAt(-1, 12, 30, 0))
            };
            var onDay = UsageQuery.SessionsOnDay(sessions, Day(-1));
            Assert.Equal(3, onDay.Count);                                        // 次日零点被排除（DayKey 口径）
            Assert.Equal(new[] { "深夜", "正午", "早" }, onDay.Select(s => s.SessionId).ToArray()); // 倒序
            Assert.Equal("次日零点", UsageQuery.SessionsOnDay(sessions, Day(0)).Single().SessionId);
        }

        [Fact]
        public void 会话按日_与Summarize范围过滤结果一致()  // 相邻锚：同一日数据在两口径下应吻合
        {
            UsageModelStat m = Model("chat", (-1, 200));
            var sessions = new[] { Session("x", MsAt(-1)), Session("y", MsAt(-5)) };
            UsageSummary s = UsageQuery.Summarize(new[] { FacetWith(m, sessions) }, Today.AddDays(-1), Today);
            Assert.Equal(s.Sessions.Select(x => x.SessionId).OrderBy(x => x),
                         UsageQuery.SessionsOnDay(sessions, Day(-1)).Select(x => x.SessionId).OrderBy(x => x));
        }

        private static UsageFacetData FacetWith(UsageModelStat m, IEnumerable<UsageSessionStat> sessions = null)
        {
            return new UsageFacetData
            {
                ModelsComplete = true,
                Models = new List<UsageModelStat> { m },
                Totals = new TokenBuckets { UncachedInput = m.Totals.UncachedInput, CacheRead = m.Totals.CacheRead, Output = m.Totals.Output },
                RequestCount = m.Requests,
                Sessions = sessions != null ? sessions.ToList() : new List<UsageSessionStat>()
            };
        }
    }
}
