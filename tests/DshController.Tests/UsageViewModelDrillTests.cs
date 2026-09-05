// ============================================================================
//  用量改版（定稿方案）的视图模型状态迁移单测：统一视图实例卡、按天钻取、
//  范围/实例切换一致性、刷新摘要、空态可行动。不起 WinUI、不碰真实档案。
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
        public void 统一视图实例卡三态与合计条()
        {
            var fake = new FakeArchiveFacade();
            fake.Add("run", "运行实例", false, Usage(10000), running: true);
            fake.Add("gone", "老实例", true, Usage(4000));
            fake.Add("empty", "空实例", false, null);
            var vm = new UsageViewModel(fake);
            vm.OnShown();

            Assert.True(vm.IsAllView);
            Assert.Equal(3, vm.InstanceCards.Count);
            UsageInstanceCardRow run = vm.InstanceCards.Single(c => c.ArchiveId == "run");
            Assert.True(run.RunningVisible); Assert.Equal("●运行中", run.RunningText);
            Assert.True(run.HasData);
            Assert.Equal(200, run.BarWUncached + run.BarWCacheRead + run.BarWCacheWrite + run.BarWOutput, 0); // k=200 满宽
            Assert.NotEmpty(run.SparkHeights);
            Assert.Contains("已删除", vm.InstanceCards.Single(c => c.ArchiveId == "gone").RetiredText);
            UsageInstanceCardRow empty = vm.InstanceCards.Single(c => c.ArchiveId == "empty");
            Assert.False(empty.HasData); Assert.Equal("无用量数据", empty.NoDataText);
            Assert.Contains("合计：总 1.4 万", vm.GrandTotalText);
        }

        [Fact]
        public void 点实例卡切入聚焦视图且范围下拉同步()
        {
            var fake = new FakeArchiveFacade();
            fake.Add("a", "实例A", false, Usage(10000));
            fake.Add("b", "实例B", false, Usage(4000));
            var vm = new UsageViewModel(fake);
            vm.OnShown();

            vm.InstanceCards.Single(c => c.ArchiveId == "b").OpenCommand.Execute(null);

            Assert.Equal("b", vm.SelectedScope.ArchiveId);
            Assert.False(vm.IsAllView); Assert.True(vm.ShowFocus);
            Assert.Equal("4,000", vm.TotalTokensText);
        }

        [Fact]
        public void 按天钻取_选中当日过滤会话再点清除()
        {
            var fake = new FakeArchiveFacade();
            fake.Add("a", "实例A", false, Usage(10000, ("昨天", -1), ("今天", 0), ("更早", -2)));
            var vm = new UsageViewModel(fake);
            vm.OnShown();
            vm.SelectedScope = vm.Scopes.Single(s => s.ArchiveId == "a");

            UsageDayBar yesterday = vm.DailyBars.Single(b => b.Day == Today.AddDays(-1).ToString("yyyy-MM-dd"));
            yesterday.ToggleCommand.Execute(null);

            Assert.True(vm.HasDay); Assert.Equal(yesterday.Day, vm.SelectedDay);
            UsageSessionRow drow = vm.Sessions.Single();                      // 展开↔收起（定稿 §3.5）
            Assert.False(drow.IsExpanded);
            drow.ExpandCommand.Execute(null);
            Assert.True(drow.IsExpanded);
            Assert.Contains("未缓存输入", drow.ExactDetailText);
            drow.ExpandCommand.Execute(null);
            Assert.False(drow.IsExpanded);
            Assert.Contains("当日", vm.DayStripText);
            Assert.Contains("当日模型", vm.DayModelsText);
            Assert.True(yesterday.IsSelected);
            UsageSessionRow only = Assert.Single(vm.Sessions);                  // 只剩当天会话
            Assert.Equal("会话 昨天", only.Title);

            yesterday.ToggleCommand.Execute(null);                              // 再点同柱 = 退出
            Assert.False(vm.HasDay); Assert.Equal(3, vm.Sessions.Count);
            Assert.DoesNotContain(vm.DailyBars, b => b.IsSelected);
        }

        [Fact]
        public void 钻取后切范围或实例_钻取态自动清空()
        {
            var fake = new FakeArchiveFacade();
            fake.Add("a", "实例A", false, Usage(10000, ("昨天", -1)));
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
            vm.SelectedScope = vm.Scopes.Single(s => s.ArchiveId == "a");
            vm.SetRangeCommand.Execute("7");

            Assert.False(vm.HasData); Assert.True(vm.EmptyVisible);
            Assert.True(vm.EmptyCanChangeRange);
            Assert.Contains("换个范围试试", vm.EmptyText);
        }
    }
}