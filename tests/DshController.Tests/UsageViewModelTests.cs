// ============================================================================
//  UsageViewModel 的离线单测（2026-09-06 二次改版 · 纯聚合口径）
//
//  看板恒显示全部实例合并用量（实例卡/范围切换已废，单实例完整用量移至
//  ArchiveMetaViewModel）：这里验证恒聚合、退役计入、空态、时间范围、
//  请求数 "+"、刷新命令与会话来源前缀。不起 WinUI、不碰真实实例。
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
        public void 看板恒为全部实例合并口径()
        {
            var fake = new FakeArchiveFacade();
            fake.Add("a", "实例A", false, Usage(1000));
            fake.Add("b", "实例B", false, Usage(400));

            var vm = new UsageViewModel(fake);
            vm.OnShown();

            Assert.True(vm.HasData);
            Assert.False(vm.EmptyVisible);
            Assert.Equal("1,400", vm.TotalTokensText);              // 1000 + 400，恒合并
            Assert.Equal("2", vm.SessionsText);                     // 两实例各 1 会话
            Assert.Equal("chat", vm.TopModelText);
        }

        [Fact]
        public void 退役档案的历史用量仍计入汇总()
        {
            var fake = new FakeArchiveFacade();
            fake.Add("a", "实例A", false, Usage(1000));
            fake.Add("gone", "老实例", true, Usage(500));

            var vm = new UsageViewModel(fake);
            vm.OnShown();

            Assert.True(vm.HasData);
            Assert.Equal("1,500", vm.TotalTokensText);              // 退役档案照样计入
            Assert.Equal("2", vm.SessionsText);
        }

        [Fact]
        public void 没有任何用量时给出空态提示()
        {
            var vm = new UsageViewModel(new FakeArchiveFacade());
            vm.OnShown();

            Assert.False(vm.HasData);
            Assert.True(vm.EmptyVisible);
            Assert.Contains("全部档案", vm.EmptyText);
            Assert.Equal("—", vm.TopModelText);
        }

        [Fact]
        public void 切换时间范围会更新标签与口径()
        {
            var fake = new FakeArchiveFacade();
            UsageFacetData data = Usage(1000);
            data.Sessions[0].Totals = new TokenBuckets { Output = 600 };   // 会话口径 < projcache 总账
            fake.Add("a", "实例A", false, data);
            var vm = new UsageViewModel(fake);
            vm.OnShown();
            Assert.Equal("1,000", vm.TotalTokensText);                     // 全期 = projcache 权威总账

            vm.SetRangeCommand.Execute("7");
            Assert.Equal(7, vm.RangeDays);
            Assert.Equal("最近 7 天", vm.RangeLabel);
            Assert.Single(vm.DailyBars);                                   // 今天有数据
            Assert.Equal("600", vm.TotalTokensText);                       // 范围口径 = 会话日志按天聚合

            vm.SetRangeCommand.Execute("0");
            Assert.Equal(0, vm.RangeDays);
            Assert.Equal("全部时间", vm.RangeLabel);
            Assert.Equal("1,000", vm.TotalTokensText);
        }

        [Fact]
        public void 日志不完整时请求数带加号()
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
            fake.Add("b", "实例B", false, Usage(400));
            fake.Add("gone", "老实例", true, Usage(500));
            var vm = new UsageViewModel(fake);
            vm.OnShown();

            await vm.RefreshCommand.ExecuteAsync(null);

            Assert.False(vm.IsBusy);
            Assert.Contains("a/usage", fake.Refreshed);
            Assert.Contains("b/usage", fake.Refreshed);
            Assert.DoesNotContain(fake.Refreshed, r => r.StartsWith("gone/", StringComparison.Ordinal));
        }

        [Fact]
        public void 聚合会话行带实例来源前缀()
        {
            var fake = new FakeArchiveFacade();
            fake.Add("a", "实例A", false, Usage(1000));
            fake.Add("b", "实例B", false, Usage(400));
            var vm = new UsageViewModel(fake);
            vm.OnShown();

            Assert.Equal(2, vm.Sessions.Count);
            string labelA = InstanceDisplayName.ForArchive(fake.Items[0].Archive);
            string labelB = InstanceDisplayName.ForArchive(fake.Items[1].Archive);
            Assert.All(vm.Sessions, row => Assert.True(
                row.Title.StartsWith(labelA + " · ", StringComparison.Ordinal) ||
                row.Title.StartsWith(labelB + " · ", StringComparison.Ordinal),
                "会话行应带实例显示名前缀：" + row.Title));
        }
    }
}
