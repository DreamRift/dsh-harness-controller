// ============================================================================
//  ArchivesRailViewModelTests — 档案左栏（改版·含退役列表）离线单测
//  全部走 FakeArchiveFacade：分组顺序 / 退役日期 / 选中态 / Refresh 保持。
// ============================================================================

using System;
using System.Linq;
using DshController.ViewModels;
using Xunit;

namespace DshController.Tests
{
    public class ArchivesRailViewModelTests
    {
        private static readonly DateTime RetiredUtc = new DateTime(2026, 9, 1, 6, 0, 0, DateTimeKind.Utc);

        /// <summary>两活跃 + 两退役（退役时间差两天，验证降序）。</summary>
        private static FakeArchiveFacade Facade()
        {
            var f = new FakeArchiveFacade();
            f.Add("a1", "甲", false, null);
            f.Add("a2", "乙", false, null);
            f.Add("gone1", "老一", true, null);
            f.Add("gone2", "老二", true, null);
            f.Items.First(t => t.Archive.ArchiveId == "gone1").Archive.RetiredAt = RetiredUtc;
            f.Items.First(t => t.Archive.ArchiveId == "gone2").Archive.RetiredAt = RetiredUtc.AddDays(2);
            return f;
        }

        [Fact]
        public void 空档案_总计置顶且默认选中总计()
        {
            var vm = new ArchivesRailViewModel(new FakeArchiveFacade());
            vm.Refresh();
            Assert.Single(vm.Rows);
            Assert.Equal(ArchivesRailViewModel.TotalsId, vm.Rows[0].Id);
            Assert.True(vm.Rows[0].IsTotals);
            Assert.Equal(ArchivesRailViewModel.TotalsId, vm.SelectedId);
        }

        [Fact]
        public void 分组顺序_总计活跃退役降序()
        {
            var vm = new ArchivesRailViewModel(Facade());
            vm.Refresh();
            Assert.Equal(new[]
            {
                ArchivesRailViewModel.TotalsId, "a1", "a2", "gone2", "gone1"
            }, vm.Rows.Select(r => r.Id).ToArray());
        }

        [Fact]
        public void 退役行徽标置灰_活跃行无()
        {
            var vm = new ArchivesRailViewModel(Facade());
            vm.Refresh();
            ArchivesRailRow retired = vm.Rows.Single(r => r.Id == "gone1");
            Assert.Equal("已退役", retired.Badge);
            Assert.True(retired.IsRetired);
            Assert.True(retired.RowOpacity < 1.0);
            ArchivesRailRow active = vm.Rows.Single(r => r.Id == "a1");
            Assert.Equal("", active.Badge);
            Assert.False(active.IsRetired);
            Assert.Equal(1.0, active.RowOpacity);
        }

        [Fact]
        public void 退役日期本地格式进tooltip()
        {
            var vm = new ArchivesRailViewModel(Facade());
            vm.Refresh();
            string expected = "退役于 " + RetiredUtc.ToLocalTime().ToString("yyyy-MM-dd");
            Assert.Contains(expected, vm.Rows.Single(r => r.Id == "gone1").Tooltip);
        }

        [Fact]
        public void 活跃行tooltip无退役标注()
        {
            var vm = new ArchivesRailViewModel(Facade());
            vm.Refresh();
            ArchivesRailRow active = vm.Rows.Single(r => r.Id == "a1");
            Assert.Equal("原名 甲", active.Tooltip);
            Assert.DoesNotContain("退役于", active.Tooltip);
        }

        [Fact]
        public void 总计行计数摘要()
        {
            var vm = new ArchivesRailViewModel(Facade());
            vm.Refresh();
            ArchivesRailRow totals = vm.Rows[0];
            Assert.Contains("档案 4 份", totals.SubText);
            Assert.Contains("退役 2", totals.SubText);
            Assert.Equal("总计", totals.Title);
        }

        [Fact]
        public void 选中切换_未知与空id忽略()
        {
            var vm = new ArchivesRailViewModel(Facade());
            vm.Refresh();
            Assert.True(vm.Select("a1"));
            Assert.Equal("a1", vm.SelectedId);
            Assert.True(vm.Select(ArchivesRailViewModel.TotalsId));
            Assert.Equal(ArchivesRailViewModel.TotalsId, vm.SelectedId);
            Assert.False(vm.Select("nope"));
            Assert.Equal(ArchivesRailViewModel.TotalsId, vm.SelectedId);
            Assert.False(vm.Select(null));
            Assert.Equal(ArchivesRailViewModel.TotalsId, vm.SelectedId);
        }

        [Fact]
        public void 退役行与总计行均可选中()
        {
            var vm = new ArchivesRailViewModel(Facade());
            vm.Refresh();
            Assert.True(vm.Select("gone1"));
            Assert.Equal("gone1", vm.SelectedId);
            Assert.True(vm.Select(ArchivesRailViewModel.TotalsId));
            Assert.Equal(ArchivesRailViewModel.TotalsId, vm.SelectedId);
        }

        [Fact]
        public void Refresh后选中保持()
        {
            var vm = new ArchivesRailViewModel(Facade());
            vm.Refresh();
            vm.Select("a2");
            vm.Refresh();
            Assert.Equal("a2", vm.SelectedId);
        }

        [Fact]
        public void Refresh后选中消失回落总计()
        {
            FakeArchiveFacade f = Facade();
            var vm = new ArchivesRailViewModel(f);
            vm.Refresh();
            vm.Select("a2");
            f.Items.Remove(f.Items.First(t => t.Archive.ArchiveId == "a2"));
            vm.Refresh();
            Assert.Equal(ArchivesRailViewModel.TotalsId, vm.SelectedId);
        }
    }
}
