// ============================================================================
//  UsageViewModel 的离线单测（重构 2.0 / P2）
//
//  这是新界面架构的第一份"UI 逻辑可测"证明：视图模型只依赖 IArchiveFacade，
//  于是范围切换、KPI 文案、空态提示、刷新命令都能在毫秒内验证，
//  不需要启动 WinUI，也不需要真实实例。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DshController.Core;
using DshController.Core.Archive;
using DshController.Core.Usage;
using DshController.ViewModels;
using Xunit;

namespace DshController.Tests
{
    public class UsageViewModelTests
    {
        private static UsageFacetData Usage(long tokens, int sessions = 1)
        {
            var m = new UsageModelStat
            {
                Provider = "deepseek",
                Model = "chat",
                Requests = 3,
                Totals = new TokenBuckets { UncachedInput = tokens / 2, CacheRead = tokens / 4, Output = tokens / 4 }
            };
            string today = DateTime.Now.ToString("yyyy-MM-dd");
            m.Daily[today] = m.Totals.Clone();
            m.DailyRequests[today] = 3;

            var data = new UsageFacetData
            {
                ModelsComplete = true,
                Models = new List<UsageModelStat> { m },
                SessionCount = sessions,
                RequestCount = 3,
                Totals = m.Totals.Clone()
            };
            for (int i = 0; i < sessions; i++)
            {
                data.Sessions.Add(new UsageSessionStat
                {
                    SessionId = "s" + i,
                    Title = "会话 " + i,
                    CreatedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    Turns = 4,
                    Totals = new TokenBuckets { Output = tokens / sessions }
                });
            }
            return data;
        }

        [Fact]
        public void 范围下拉包含全部与每份档案且标注已删除()
        {
            var fake = new FakeArchiveFacade();
            fake.Add("a", "实例A", retired: false, Usage(1000));
            fake.Add("gone", "老实例", retired: true, Usage(500));

            var vm = new UsageViewModel(fake);
            vm.OnShown();

            Assert.Equal(3, vm.Scopes.Count);                       // 全部 + 2 份档案
            Assert.True(vm.Scopes[0].IsAll);
            Assert.Contains(vm.Scopes, s => s.Label.Contains("已删除"));
        }

        [Fact]
        public void 默认统计全部实例含已删除的历史用量()
        {
            var fake = new FakeArchiveFacade();
            fake.Add("a", "实例A", false, Usage(1000));
            fake.Add("gone", "老实例", true, Usage(500));

            var vm = new UsageViewModel(fake);
            vm.OnShown();

            Assert.True(vm.HasData);
            Assert.Equal("1,500", vm.TotalTokensText);              // 1000 + 500，退役档案照样计入
            Assert.Equal("2", vm.SessionsText);
            Assert.Equal("chat", vm.TopModelText);
        }

        [Fact]
        public void 切到单个实例只统计它()
        {
            var fake = new FakeArchiveFacade();
            fake.Add("a", "实例A", false, Usage(1000));
            fake.Add("b", "实例B", false, Usage(400));

            var vm = new UsageViewModel(fake);
            vm.OnShown();
            vm.SelectedScope = vm.Scopes.Single(s => s.ArchiveId == "b");

            Assert.Equal("400", vm.TotalTokensText);
        }

        [Fact]
        public void 没有任何用量档案时给出空态提示()
        {
            var vm = new UsageViewModel(new FakeArchiveFacade());
            vm.OnShown();

            Assert.False(vm.HasData);
            Assert.Contains("还没有用量档案", vm.EmptyText);
            Assert.Equal("—", vm.TopModelText);
        }

        [Fact]
        public void 切换时间范围会更新标签与柱状图()
        {
            var fake = new FakeArchiveFacade();
            fake.Add("a", "实例A", false, Usage(1000));
            var vm = new UsageViewModel(fake);
            vm.OnShown();

            vm.SetRangeCommand.Execute("7");
            Assert.Equal(7, vm.RangeDays);
            Assert.Equal("最近 7 天", vm.RangeLabel);
            Assert.Single(vm.DailyBars);                            // 今天有数据

            vm.SetRangeCommand.Execute("0");
            Assert.Equal("全部时间", vm.RangeLabel);
        }

        [Fact]
        public void 日志不完整时请求数带加号并给出提示()
        {
            var fake = new FakeArchiveFacade();
            UsageFacetData partial = Usage(1000);
            partial.ModelsComplete = false;
            fake.Add("a", "实例A", false, partial);

            var vm = new UsageViewModel(fake);
            vm.OnShown();

            Assert.EndsWith("+", vm.RequestsText);
            Assert.Contains("未能完整解析", vm.CompletenessText);
        }

        [Fact]
        public async Task 刷新命令只对未退役实例发起采集()
        {
            var fake = new FakeArchiveFacade();
            fake.Add("a", "实例A", false, Usage(1000));
            fake.Add("gone", "老实例", true, Usage(500));
            var vm = new UsageViewModel(fake);
            vm.OnShown();

            await vm.RefreshCommand.ExecuteAsync(null);

            Assert.Single(fake.Refreshed);
            Assert.Equal("a/usage", fake.Refreshed[0]);
            Assert.False(vm.IsBusy);
        }
    }
}
