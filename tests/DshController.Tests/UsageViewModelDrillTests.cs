// ============================================================================
//  用量看板按天钻取与合并口径的离线单测（2026-09-06 二次改版 · 纯聚合）
//
//  实例卡/合计条已废（单实例完整用量移至 ArchiveMetaViewModel），本文件只验证：
//  多实例合并的柱图/模型/会话、按天钻取（选中→过滤→清除→再点退出）、
//  范围切换清钻取、hero 口径、刷新摘要与空态。不起 WinUI、不碰真实档案。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using DshController.Core.Usage;
using DshController.ViewModels;
using Xunit;

namespace DshController.Tests
{
    public class UsageViewModelDrillTests
    {
        private static readonly DateTime Today = DateTime.Now.Date;
        private static long Ms(int dayOffset, int hour = 10) =>
            new DateTimeOffset(Today.AddDays(dayOffset).AddHours(hour),
                TimeZoneInfo.Local.GetUtcOffset(Today.AddDays(dayOffset).AddHours(hour))).ToUnixTimeMilliseconds();

        private static UsageFacetData Usage(long tokens, params (string id, int day)[] sessions)
        {
            var m = new UsageModelStat { Provider = "deepseek", Model = "chat", Requests = 3, Totals = new TokenBuckets { UncachedInput = tokens / 2, CacheRead = tokens / 4, Output = tokens / 4 } };
            string today = Today.ToString("yyyy-MM-dd");
            if (sessions.Length == 0)
            {
                m.Daily[today] = m.Totals.Clone();
                m.DailyRequests[today] = 3;
            }
            else
            {
                foreach ((string id2, int day2) in sessions)
                {
                    string dk = Today.AddDays(day2).ToString("yyyy-MM-dd");
                    m.Daily[dk] = new TokenBuckets { UncachedInput = 50, CacheRead = 25, Output = 25 };
                    m.DailyRequests[dk] = 1;
                }
                m.Totals = new TokenBuckets { UncachedInput = 100 * sessions.Length, CacheRead = 50 * sessions.Length, Output = 50 * sessions.Length };
            }
            var data = new UsageFacetData
            {
                ModelsComplete = true,
                Models = new List<UsageModelStat> { m },
                SessionCount = (sessions.Length > 0 ? sessions.Length : 1),
                RequestCount = 3,
                Totals = m.Totals.Clone()
            };
            if (sessions.Length == 0)
                data.Sessions.Add(new UsageSessionStat { SessionId = "s0", Title = "会话", CreatedAtMs = Ms(0), Turns = 2, Totals = m.Totals.Clone() });
            else
                foreach ((string id, int day) in sessions)
                    data.Sessions.Add(new UsageSessionStat { SessionId = id, Title = "会话 " + id, CreatedAtMs = Ms(day), Turns = 2, Totals = new TokenBuckets { Output = 100 } });
            return data;
        }

        [Fact]
        public void 看板为多实例合并口径_柱图模型会话齐全()
        {
            var fake = new FakeArchiveFacade();
            fake.Add("a", "实例A", false, Usage(200, ("今天", 0)));
            fake.Add("b", "实例B", false, Usage(200, ("今天", 0)));
            var vm = new UsageViewModel(fake);
            vm.OnShown();

            UsageDayBar today = vm.DailyBars.Single(b => b.Day == Today.ToString("yyyy-MM-dd"));
            Assert.Equal(200, today.Total);                         // 每实例当日 100，合并 200
            Assert.Single(vm.Models);                               // 同 provider/model 合并成一行
            Assert.Equal("400", vm.Models[0].TotalText);            // projcache 总账 200 + 200
            Assert.Equal(2, vm.Sessions.Count);
            Assert.True(vm.HasData);
        }

        [Fact]
        public void 按天钻取_选中当日过滤会话_清除与再点同柱退出()
        {
            var fake = new FakeArchiveFacade();
            fake.Add("a", "实例A", false, Usage(200, ("昨天", -1), ("今天", 0)));
            var vm = new UsageViewModel(fake);
            vm.OnShown();
            Assert.Equal(2, vm.Sessions.Count);

            string yesterdayKey = Today.AddDays(-1).ToString("yyyy-MM-dd");
            UsageDayBar yesterday = vm.DailyBars.Single(b => b.Day == yesterdayKey);
            yesterday.ToggleCommand.Execute(null);

            Assert.True(vm.HasDay); Assert.Equal(yesterdayKey, vm.SelectedDay);
            Assert.Contains("未缓存输入 50", vm.DayStripText);      // 当日四桶（FormatTokens 口径）
            Assert.Contains("当日模型", vm.DayModelsText);
            Assert.True(yesterday.IsSelected);
            UsageSessionRow only = Assert.Single(vm.Sessions);      // 只剩昨天会话（聚合视图带前缀）
            Assert.EndsWith("会话 昨天", only.Title);
            only.ExpandCommand.Execute(null);                       // 展开↔收起
            Assert.True(only.IsExpanded);
            Assert.Contains("未缓存输入", only.ExactDetailText);
            only.ExpandCommand.Execute(null);
            Assert.False(only.IsExpanded);

            vm.ClearDayCommand.Execute(null);
            Assert.False(vm.HasDay); Assert.Equal(2, vm.Sessions.Count);
            Assert.DoesNotContain(vm.DailyBars, b => b.IsSelected);

            yesterday.ToggleCommand.Execute(null);
            Assert.True(vm.HasDay);
            yesterday.ToggleCommand.Execute(null);                  // 再点同柱 = 退出
            Assert.False(vm.HasDay); Assert.Equal(2, vm.Sessions.Count);
            Assert.DoesNotContain(vm.DailyBars, b => b.IsSelected);
        }

        [Fact]
        public void 钻取后切范围_钻取态自动清空()
        {
            var fake = new FakeArchiveFacade();
            fake.Add("a", "实例A", false, Usage(200, ("昨天", -1)));
            var vm = new UsageViewModel(fake);
            vm.OnShown();
            vm.DailyBars.First().ToggleCommand.Execute(null);
            Assert.True(vm.HasDay);

            vm.SetRangeCommand.Execute("7");
            Assert.False(vm.HasDay); Assert.Equal("", vm.SelectedDay);
            Assert.All(vm.DailyBars, b => Assert.False(b.IsSelected));
        }

        [Fact]
        public void hero四桶堆叠占比与命中率口径自洽()
        {
            var fake = new FakeArchiveFacade();
            fake.Add("a", "实例A", false, Usage(10000));
            var vm = new UsageViewModel(fake);
            vm.OnShown();

            Assert.Equal(330, vm.BarWUncached + vm.BarWCacheRead + vm.BarWCacheWrite + vm.BarWOutput, 0);
            // 四桶 5000/2500/0/2500：命中率 = 缓存读/(未缓存+缓存读) = 2500/7500 = 33.3%
            Assert.Contains("缓存读 25%", vm.HeroSubText);
            Assert.Equal("33.3%", vm.HitRateText);
        }

        [Fact]
        public async System.Threading.Tasks.Task 刷新后状态行给出成功失败摘要()
        {
            var fake = new FakeArchiveFacade();
            fake.Add("a", "实例A", false, Usage(1000));
            var vm = new UsageViewModel(fake);
            vm.OnShown();

            await vm.RefreshCommand.ExecuteAsync(null);

            Assert.Equal("采集完成：成功 1 · 失败 0", vm.StatusText);
            Assert.False(vm.IsBusy); Assert.True(vm.RefreshEnabled);
        }

        [Fact]
        public void 范围内无记录时空态给换范围出口()
        {
            var fake = new FakeArchiveFacade();
            UsageFacetData old = Usage(1000, ("远古", -40));
            fake.Add("a", "实例A", false, old);
            var vm = new UsageViewModel(fake);
            vm.OnShown();
            vm.SetRangeCommand.Execute("7");

            Assert.False(vm.HasData); Assert.True(vm.EmptyVisible);
            Assert.True(vm.EmptyCanChangeRange);
            Assert.Contains("换个范围试试", vm.EmptyText);
        }

        [Fact]
        public void 空数据时柱图为空并显示空态()
        {
            var vm = new UsageViewModel(new FakeArchiveFacade());
            vm.OnShown();

            Assert.Empty(vm.DailyBars);
            Assert.Empty(vm.Models);
            Assert.Empty(vm.Sessions);
            Assert.False(vm.HasData);
            Assert.True(vm.EmptyVisible);
        }
    }
}
