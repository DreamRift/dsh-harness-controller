// ============================================================================
//  UsageQuery 的离线单测（重构 2.0 / P2）
//
//  原型分支里"按范围聚合"被复制了 4 份；合并成一处之后，这里就是它唯一的
//  行为规范：不限范围时用总账权威口径，限定范围时按会话日志的每日数据重算。
// ============================================================================

using System;
using System.Collections.Generic;
using DshController.Core.Usage;
using Xunit;

namespace DshController.Tests
{
    public class UsageQueryTests
    {
        private static readonly DateTime Today = DateTime.Now.Date;

        private static string Day(int offset) => Today.AddDays(offset).ToString("yyyy-MM-dd");

        private static long MsFor(int dayOffset) =>
            new DateTimeOffset(Today.AddDays(dayOffset).AddHours(10)).ToUnixTimeMilliseconds();

        private static UsageFacetData Sample(string model, int dayOffset, long tokens, int requests = 1)
        {
            var m = new UsageModelStat
            {
                Provider = "deepseek",
                Model = model,
                Requests = requests,
                Totals = new TokenBuckets { UncachedInput = tokens / 2, Output = tokens / 2 }
            };
            m.Daily[Day(dayOffset)] = new TokenBuckets { UncachedInput = tokens / 2, Output = tokens / 2 };
            m.DailyRequests[Day(dayOffset)] = requests;

            var data = new UsageFacetData
            {
                ModelsComplete = true,
                Models = new List<UsageModelStat> { m },
                RequestCount = requests,
                SessionCount = 1,
                Totals = new TokenBuckets { UncachedInput = tokens / 2, Output = tokens / 2 },
                Sessions = new List<UsageSessionStat>
                {
                    new UsageSessionStat
                    {
                        SessionId = model + dayOffset,
                        CreatedAtMs = MsFor(dayOffset),
                        Totals = new TokenBuckets { UncachedInput = tokens / 2, Output = tokens / 2 }
                    }
                }
            };
            return data;
        }

        [Fact]
        public void 不限范围时用总账权威口径()
        {
            UsageSummary s = UsageQuery.Summarize(new[] { Sample("chat", -1, 1000), Sample("chat", -40, 500) });

            Assert.Equal(1500, s.Totals.Total);
            Assert.Equal(2, s.SessionCount);
            Assert.Equal(2, s.RequestCount);
            Assert.Equal("chat", s.TopModel);
            Assert.Single(s.Models);              // 同名模型跨实例合并
        }

        [Fact]
        public void 限定范围时只算范围内的每日数据()
        {
            UsageSummary s = UsageQuery.Summarize(
                new[] { Sample("chat", -1, 1000), Sample("chat", -40, 500) },
                Today.AddDays(-6), null);

            Assert.Equal(1000, s.Totals.Total);   // 40 天前那笔被排除
            Assert.Equal(1, s.SessionCount);
            Assert.Equal(1, s.RequestCount);
            Assert.Equal(1, s.ActiveDays);
        }

        [Fact]
        public void 多模型按总量降序且计算活跃天数()
        {
            UsageSummary s = UsageQuery.Summarize(new[]
            {
                Sample("small", -1, 100), Sample("big", -2, 900), Sample("big", -3, 100)
            });

            Assert.Equal(3, s.Models.Count == 2 ? 3 : s.ActiveDays);   // 三天各有数据
            Assert.Equal("big", s.Models[0].Model);
            Assert.Equal(1000, s.Models[0].Totals.Total);
        }

        [Fact]
        public void 任一实例日志不完整则整体标注不完整()
        {
            UsageFacetData incomplete = Sample("chat", -1, 100);
            incomplete.ModelsComplete = false;

            Assert.False(UsageQuery.Summarize(new[] { Sample("chat", -1, 100), incomplete }).ModelsComplete);
            Assert.True(UsageQuery.Summarize(new[] { Sample("chat", -1, 100) }).ModelsComplete);
        }

        [Fact]
        public void 空输入返回空汇总而不是抛()
        {
            UsageSummary s = UsageQuery.Summarize(null);
            Assert.Equal(0, s.Totals.Total);
            Assert.Empty(s.Models);
            Assert.Equal("", s.TopModel);
            Assert.Equal(-1, s.CacheHitRate);
        }

        [Fact]
        public void 零Token会话不进入会话统计或明细()
        {
            UsageFacetData data = Sample("chat", -1, 100);
            data.Sessions.Add(new UsageSessionStat
            {
                SessionId = "empty-session",
                CreatedAtMs = MsFor(-1),
                Totals = new TokenBuckets()
            });

            UsageSummary summary = UsageQuery.Summarize(new[] { data });

            Assert.Single(summary.Sessions);
            Assert.Equal(1, summary.SessionCount);
            Assert.DoesNotContain(summary.Sessions, s => s.SessionId == "empty-session");
        }

        [Theory]
        [InlineData(0, "0")]
        [InlineData(999, "999")]
        [InlineData(12345, "1.23 万")]
        [InlineData(250000000, "2.5 亿")]
        public void token数按中文单位格式化(long value, string expected)
        {
            Assert.Equal(expected, UsageQuery.FormatTokens(value));
        }

        [Fact]
        public void 日期范围判定的边界()
        {
            Assert.True(UsageQuery.InRange("2026-08-20", "2026-08-20", "2026-08-20"));
            Assert.False(UsageQuery.InRange("2026-08-19", "2026-08-20", null));
            Assert.False(UsageQuery.InRange("2026-08-21", null, "2026-08-20"));
            Assert.True(UsageQuery.InRange("2026-08-21", null, null));
            Assert.False(UsageQuery.InRange("", "2026-08-20", null));
        }
    }
}
