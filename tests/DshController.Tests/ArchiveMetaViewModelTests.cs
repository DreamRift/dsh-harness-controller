// ============================================================================
//  ArchiveMetaViewModelTests — 档案主区「元信息一览 + 单实例完整用量」离线单测
//  全部走 FakeArchiveFacade：镜像字段 / 退役时间 / 总计与未知 id 回落 / 重建无残留
//  / 2026-09-06 二次改版——单实例用量区（大数卡/四桶/按天钻取/重采/空态）。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DshController.Core.Usage;
using DshController.ViewModels;
using Xunit;

namespace DshController.Tests
{
    public class ArchiveMetaViewModelTests
    {
        private static readonly DateTime RetiredUtc = new DateTime(2026, 9, 1, 6, 0, 0, DateTimeKind.Utc);

        private static FakeArchiveFacade Facade()
        {
            var f = new FakeArchiveFacade();
            f.Add("a1", "甲", false, null);
            f.Add("gone1", "老一", true, null);
            f.Items.First(t => t.Archive.ArchiveId == "gone1").Archive.RetiredAt = RetiredUtc;
            return f;
        }

        /// <summary>单实例用量造数：一个模型 + 一条会话，每日分桶落在 dayOffset 当天。</summary>
        private static UsageFacetData Usage(long tokens, int dayOffset = 0, string title = "会话")
        {
            var m = new UsageModelStat
            {
                Provider = "deepseek",
                Model = "chat",
                Requests = 3,
                Totals = new TokenBuckets { UncachedInput = tokens / 2, CacheRead = tokens / 4, Output = tokens / 4 }
            };
            string day = DateTime.Now.Date.AddDays(dayOffset).ToString("yyyy-MM-dd");
            m.Daily[day] = m.Totals.Clone();
            m.DailyRequests[day] = 3;

            var data = new UsageFacetData
            {
                ModelsComplete = true,
                Models = new List<UsageModelStat> { m },
                SessionCount = 1,
                RequestCount = 3,
                Totals = m.Totals.Clone()
            };
            DateTime at = DateTime.Now.Date.AddDays(dayOffset).AddHours(10);
            data.Sessions.Add(new UsageSessionStat
            {
                SessionId = "s0",
                Title = title,
                CreatedAtMs = new DateTimeOffset(at, TimeZoneInfo.Local.GetUtcOffset(at)).ToUnixTimeMilliseconds(),
                Turns = 4,
                Totals = m.Totals.Clone()
            });
            return data;
        }

        [Fact]
        public void Show活跃档案_镜像字段齐()
        {
            var vm = new ArchiveMetaViewModel(Facade());
            vm.Show("a1");
            Assert.True(vm.HasArchive);
            Assert.False(vm.IsRetired);
            Assert.Equal("活跃档案", vm.StateLine);
            Assert.Equal("原名 甲", vm.Subtitle);
            Assert.Contains(vm.Rows, r => r.Label == "档案 ID" && r.Value == "a1");
            Assert.Contains(vm.Rows, r => r.Label == "Runtime" && r.Value == "windows");
            Assert.Contains(vm.Rows, r => r.Label == "代际" && r.Value == "1 代");
            Assert.Contains(vm.Rows, r => r.Label == "当前代起");
            Assert.DoesNotContain(vm.Rows, r => r.Label == "退役于");
        }

        [Fact]
        public void Show退役档案_退役时间与状态()
        {
            var vm = new ArchiveMetaViewModel(Facade());
            vm.Show("gone1");
            Assert.True(vm.HasArchive);
            Assert.True(vm.IsRetired);
            string expected = RetiredUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
            Assert.Contains(vm.Rows, r => r.Label == "退役于" && r.Value == expected);
            Assert.StartsWith("已退役 · 退役于 ", vm.StateLine);
            Assert.Contains(vm.Rows, r => r.Label == "最近代起");
        }

        [Fact]
        public void Show总计与未知空id_清空回落()
        {
            var vm = new ArchiveMetaViewModel(Facade());
            vm.Show(ArchivesRailViewModel.TotalsId);
            Assert.False(vm.HasArchive);
            Assert.Empty(vm.Rows);
            vm.Show("nope");
            Assert.False(vm.HasArchive);
            Assert.Empty(vm.Rows);
            vm.Show("");
            Assert.False(vm.HasArchive);
            Assert.Empty(vm.Rows);
        }

        [Fact]
        public void 重复Show_整表重建无残留()
        {
            var vm = new ArchiveMetaViewModel(Facade());
            vm.Show("a1");
            vm.Show("gone1");
            Assert.DoesNotContain(vm.Rows, r => r.Label == "当前代起");
            Assert.Contains(vm.Rows, r => r.Label == "退役于");
            Assert.Equal("已退役 · 退役于 " + RetiredUtc.ToLocalTime().ToString("yyyy-MM-dd"), vm.StateLine);
        }

        [Fact]
        public void Show后_CurrentArchiveId正确()
        {
            var vm = new ArchiveMetaViewModel(Facade());
            vm.Show("a1");
            Assert.Equal("a1", vm.CurrentArchiveId);
            vm.Show("nope");
            Assert.Equal("", vm.CurrentArchiveId);
            vm.Show("");
            Assert.Equal("", vm.CurrentArchiveId);
        }

        // ==================== 单实例完整用量（2026-09-06 二次改版） ====================

        [Fact]
        public void Show活跃档案带用量_单实例用量区齐()
        {
            var fake = new FakeArchiveFacade();
            fake.Add("a1", "甲", false, Usage(1000));
            var vm = new ArchiveMetaViewModel(fake);
            vm.Show("a1");

            Assert.True(vm.HasArchive);
            Assert.True(vm.HasUsage);
            Assert.Equal(UsageQuery.FormatTokens(1000), vm.TotalTokensText);
            Assert.Equal("1", vm.SessionsText);
            Assert.Single(vm.DailyBars);
            Assert.Single(vm.Models);
            Assert.Single(vm.Sessions);
            Assert.Equal(330, vm.BarWUncached + vm.BarWCacheRead + vm.BarWCacheWrite + vm.BarWOutput, 0);
            Assert.Contains("缓存读 25%", vm.HeroSubText);          // 四桶 500/250/0/250
            Assert.Equal("33.3%", vm.HitRateText);
            Assert.True(vm.UsageRefreshEnabled);
            Assert.Contains("档案更新于", vm.UsageUpdatedText);      // fake 的 usage 分面快照带 LastGoodAt
            Assert.Equal("", vm.UsageBarsNote);                      // 单日数据无截断
        }

        [Fact]
        public void Show档案用量超30天_柱图截断带标注()
        {
            var fake = new FakeArchiveFacade();
            fake.Add("a1", "甲", false, UsageSpanDays(35));
            var vm = new ArchiveMetaViewModel(fake);
            vm.Show("a1");

            // 默认近 7 天；扩大为自定义范围后才触发详情兼容柱图的 30 天截断标注。
            vm.UsageRangeStart = DateTimeOffset.Now.Date.AddDays(-34);
            vm.UsageRangeEnd = DateTimeOffset.Now.Date;
            vm.ApplyCustomUsageRangeCommand.Execute(null);

            Assert.Equal(30, vm.DailyBars.Count);                    // 详情卡只画最近 30 根
            Assert.Contains("30", vm.UsageBarsNote);                 // W3：不再静默截断
            Assert.Contains("35", vm.UsageBarsNote);
        }

        /// <summary>跨多天的用量造数：一个模型带 N 天按天分桶 + 一条会话。</summary>
        private static UsageFacetData UsageSpanDays(int days)
        {
            var m = new UsageModelStat
            {
                Provider = "deepseek",
                Model = "chat",
                Requests = days,
                Totals = new TokenBuckets { UncachedInput = 10 }
            };
            for (int i = 0; i < days; i++)
            {
                string day = DateTime.Now.Date.AddDays(-i).ToString("yyyy-MM-dd");
                m.Daily[day] = new TokenBuckets { UncachedInput = 10 };
                m.DailyRequests[day] = 1;
            }
            var data = new UsageFacetData
            {
                ModelsComplete = true,
                Models = new List<UsageModelStat> { m },
                SessionCount = 1,
                RequestCount = days,
                Totals = m.Totals.Clone()
            };
            data.Sessions.Add(new UsageSessionStat
            {
                SessionId = "s0",
                Title = "会话 0",
                CreatedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Turns = 1,
                Totals = new TokenBuckets { UncachedInput = 10 }
            });
            return data;
        }

        [Fact]
        public void Show无用量数据_空态与大数卡回落()
        {
            var fake = new FakeArchiveFacade();
            fake.Add("a1", "甲", false, null);
            var vm = new ArchiveMetaViewModel(fake);
            vm.Show("a1");

            Assert.True(vm.HasArchive);
            Assert.False(vm.HasUsage);
            Assert.NotEmpty(vm.UsageEmptyText);
            Assert.Equal("—", vm.TotalTokensText);
            Assert.Empty(vm.DailyBars);
            Assert.Empty(vm.Sessions);
            Assert.Equal("尚未采集", vm.UsageUpdatedText);           // 无 usage 快照 → 无 LastGoodAt
        }

        [Fact]
        public void Show总计与未知id_用量区一并清空()
        {
            var fake = new FakeArchiveFacade();
            fake.Add("a1", "甲", false, Usage(1000));
            var vm = new ArchiveMetaViewModel(fake);
            vm.Show("a1");
            Assert.True(vm.HasUsage);

            vm.Show(ArchivesRailViewModel.TotalsId);
            Assert.False(vm.HasArchive);
            Assert.False(vm.HasUsage);
            Assert.Equal("—", vm.TotalTokensText);
            Assert.Equal("尚未采集", vm.UsageUpdatedText);
            Assert.False(vm.UsageRefreshEnabled);

            vm.Show("nope");
            Assert.False(vm.HasArchive);
            Assert.False(vm.HasUsage);
        }

        [Fact]
        public void Show退役档案带用量_照常展示但禁止重采()
        {
            var fake = new FakeArchiveFacade();
            fake.Add("gone1", "老一", true, Usage(1000));
            var vm = new ArchiveMetaViewModel(fake);
            vm.Show("gone1");

            Assert.True(vm.HasArchive);
            Assert.True(vm.IsRetired);
            Assert.True(vm.HasUsage);
            Assert.Equal(UsageQuery.FormatTokens(1000), vm.TotalTokensText);
            Assert.False(vm.UsageRefreshEnabled);
        }

        [Fact]
        public async Task 重采用量命令_单档案单分面刷新并重建()
        {
            var fake = new FakeArchiveFacade();
            fake.Add("a1", "甲", false, Usage(1000));
            var vm = new ArchiveMetaViewModel(fake);
            vm.Show("a1");

            await vm.RefreshUsageCommand.ExecuteAsync(null);

            Assert.Single(fake.Refreshed);
            Assert.Contains("a1/usage", fake.Refreshed);
            Assert.False(vm.UsageBusy);
            Assert.True(vm.HasUsage);                               // 刷新后 Show 重读快照
            Assert.True(vm.UsageRefreshEnabled);
        }

        [Fact]
        public void 单实例用量_按天钻取与清除()
        {
            var fake = new FakeArchiveFacade();
            fake.Add("a1", "甲", false, Usage(1000, -1, "昨天的会话"));
            fake.Add("b1", "乙", false, Usage(2000));
            var vm = new ArchiveMetaViewModel(fake);
            vm.Show("a1");

            UsageDayBar yesterday = vm.DailyBars.Single();          // 单实例视图：只有自己的那天
            Assert.Equal(DateTime.Now.Date.AddDays(-1).ToString("yyyy-MM-dd"), yesterday.Day);
            yesterday.ToggleCommand.Execute(null);

            Assert.True(vm.HasDay);
            Assert.Equal(yesterday.Day, vm.SelectedDay);
            Assert.Contains("当日", vm.DayStripText);
            Assert.True(yesterday.IsSelected);
            Assert.Single(vm.Sessions);

            yesterday.ToggleCommand.Execute(null);                  // 再点同柱 = 退出
            Assert.False(vm.HasDay);

            vm.DailyBars.Single().ToggleCommand.Execute(null);
            Assert.True(vm.HasDay);
            vm.ClearUsageDayCommand.Execute(null);
            Assert.False(vm.HasDay);
            Assert.Equal("", vm.SelectedDay);
            Assert.Single(vm.Sessions);                             // 回到全量
            Assert.DoesNotContain(vm.DailyBars, b => b.IsSelected);
        }

        [Fact]
        public void 档案用量默认近七天并生成五卡趋势点()
        {
            var fake = new FakeArchiveFacade();
            fake.Add("a1", "甲", false, UsageSpanDays(10));
            var vm = new ArchiveMetaViewModel(fake);
            vm.Show("a1");

            Assert.Equal("近 7 天", vm.UsageRangeLabel);
            Assert.Equal(5, vm.Kpis.Count);
            Assert.Equal(7, vm.TrendPoints.Count);
            Assert.Equal("tokens", vm.TrendMetric);
            Assert.Contains(vm.Kpis, k => k.Label == "平均首 Token" && k.Value == "—");
        }

        [Fact]
        public void 档案用量将有效TTFT写入第五张KPI()
        {
            UsageFacetData data = Usage(1000);
            string today = DateTime.Now.Date.ToString("yyyy-MM-dd");
            data.Latency = new UsageLatencyStats { Samples = 2, TtftMs = 600 };
            data.DailyLatency[today] = new UsageLatencyStats { Samples = 2, TtftMs = 600 };
            var fake = new FakeArchiveFacade();
            fake.Add("a1", "甲", false, data);
            var vm = new ArchiveMetaViewModel(fake);
            vm.Show("a1");

            Assert.Contains(vm.Kpis, k => k.Label == "平均首 Token" && k.Value == "300 ms" &&
                k.Detail.Contains("2 个有效步骤样本"));
        }

        [Fact]
        public void 自定义范围非法时保留上次有效趋势()
        {
            var fake = new FakeArchiveFacade();
            fake.Add("a1", "甲", false, UsageSpanDays(10));
            var vm = new ArchiveMetaViewModel(fake);
            vm.Show("a1");
            int prior = vm.TrendPoints.Count;

            vm.UsageRangeStart = DateTimeOffset.Now.Date;
            vm.UsageRangeEnd = DateTimeOffset.Now.Date.AddDays(-1);
            vm.ApplyCustomUsageRangeCommand.Execute(null);

            Assert.NotEmpty(vm.UsageRangeError);
            Assert.Equal(prior, vm.TrendPoints.Count);
        }

        [Fact]
        public void 超过九十天趋势按周聚合且可逆钻取()
        {
            var fake = new FakeArchiveFacade();
            fake.Add("a1", "甲", false, UsageSpanDays(100));
            var vm = new ArchiveMetaViewModel(fake);
            vm.Show("a1");
            vm.UsageRangeStart = DateTimeOffset.Now.Date.AddDays(-99);
            vm.UsageRangeEnd = DateTimeOffset.Now.Date;
            vm.ApplyCustomUsageRangeCommand.Execute(null);

            Assert.InRange(vm.TrendPoints.Count, 14, 16);
            UsageTrendPoint first = vm.TrendPoints[0];
            first.SelectCommand.Execute(null);
            Assert.True(vm.HasDay);
            first.SelectCommand.Execute(null);
            Assert.False(vm.HasDay);
        }
    }
}
