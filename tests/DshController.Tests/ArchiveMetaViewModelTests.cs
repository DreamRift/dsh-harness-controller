// ============================================================================
//  ArchiveMetaViewModelTests — 档案主区「元信息一览」离线单测
//  全部走 FakeArchiveFacade：镜像字段 / 退役时间 / 总计与未知 id 回落 / 重建无残留。
// ============================================================================

using System;
using System.Linq;
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
    }
}
