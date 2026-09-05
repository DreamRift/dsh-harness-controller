// ============================================================================
//  启动序列表（改版·实例页）：排序纯函数矩阵 + 行构建 + 选中迁移。
//  全部离线（FakeArchiveFacade），不起 WinUI、不碰真实档案目录。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using DshController.Core;
using DshController.ViewModels;
using Xunit;

namespace DshController.Tests
{
    public class InstancesRailViewModelTests
    {
        private static DateTime T(int h) => new DateTime(2026, 9, 1, h, 0, 0, DateTimeKind.Utc);

        private static InstanceDef Def(string id, DateTime? started = null, DateTime? created = null, bool wsl = false)
            => new InstanceDef { Id = id, Name = id, Runtime = wsl ? "wsl" : "windows", LastStartedAt = started, CreatedAt = created };

        // ---------------- 排序纯函数 ----------------

        [Fact]
        public void 最近启动的排最前()
        {
            var defs = new[] { Def("old", T(1)), Def("newest", T(9)), Def("mid", T(5)) };
            var ordered = InstancesRailViewModel.OrderForRail(defs).Select(d => d.Id).ToList();
            Assert.Equal(new[] { "newest", "mid", "old" }, ordered);
        }

        [Fact]
        public void 从未启动一律置末且按创建时间降序()
        {
            var defs = new[]
            {
                Def("never-new", created: T(8)),
                Def("started", T(1), created: T(1)),
                Def("never-old", created: T(2)),
                Def("never-null-times"),                       // 连创建时间都没有 → 最末
            };
            var ordered = InstancesRailViewModel.OrderForRail(defs).Select(d => d.Id).ToList();
            Assert.Equal(new[] { "started", "never-new", "never-old", "never-null-times" }, ordered);
        }

        [Fact]
        public void 启动时间相等按创建时间稳定落位()
        {
            var defs = new[] { Def("a", T(3), created: T(1)), Def("b", T(3), created: T(2)) };
            var ordered = InstancesRailViewModel.OrderForRail(defs).Select(d => d.Id).ToList();
            Assert.Equal(new[] { "b", "a" }, ordered);   // 平手时创建新的靠前，序列确定不抖动
        }

        [Fact]
        public void 空与脏输入稳定()
        {
            Assert.Empty(InstancesRailViewModel.OrderForRail(null));
            Assert.Empty(InstancesRailViewModel.OrderForRail(new List<InstanceDef>()));
            var dirty = new[] { null, Def(""), Def("ok", T(1)) };
            Assert.Equal(new[] { "ok" }, InstancesRailViewModel.OrderForRail(dirty).Select(d => d.Id));
        }

        // ---------------- VM 行构建 / 退役不出现 / liveness ----------------

        [Fact]
        public void 退役实例不进左栏()
        {
            var f = new FakeArchiveFacade();
            f.AddInstance("live", "在场");
            f.Add("gone", "已删", retired: true, usage: null);       // 只在档案（AllUsage 含退役），不在清单
            var vm = new InstancesRailViewModel(f);
            vm.Refresh();
            Assert.DoesNotContain(vm.Rows, r => r.Id == "gone");
            Assert.Single(vm.Rows);
        }

        [Fact]
        public void 运行态来自档案liveness结论()
        {
            var f = new FakeArchiveFacade();
            f.AddInstance("run", "在跑").LastStartedAt = T(9);
            f.AddInstance("stop", "停着").LastStartedAt = T(8);
            f.Add("run", "在跑", retired: false, usage: null, running: true);
            var vm = new InstancesRailViewModel(f);
            vm.Refresh();
            Assert.Equal(new[] { "run", "stop" }, vm.Rows.Select(r => r.Id).ToArray());
            Assert.True(vm.Rows[0].Running);
            Assert.False(vm.Rows[1].Running);
            Assert.Equal("运行中", vm.Rows[0].StateText);
        }

        [Fact]
        public void 刷新后选中按Id保留_消失则清空()
        {
            var f = new FakeArchiveFacade();
            f.AddInstance("a", "A").LastStartedAt = T(5);
            f.AddInstance("b", "B").LastStartedAt = T(4);
            var vm = new InstancesRailViewModel(f);
            vm.Refresh();
            Assert.True(vm.Select("b"));
            Assert.Equal("b", vm.SelectedId);
            vm.Refresh();
            Assert.Equal("b", vm.SelectedId);                       // 重建后仍在 → 保留
            Assert.False(vm.Select("b"));                           // 重复选中无变化
            f.RemoveInstance("b");                                  // 选中的实例从清单消失
            vm.Refresh();
            Assert.Equal("", vm.SelectedId);                        // 行消失 → 选中清空
        }

        [Fact]
        public void 空id清除选中()
        {
            var f = new FakeArchiveFacade();
            f.AddInstance("a", "A").LastStartedAt = T(5);
            var vm = new InstancesRailViewModel(f);
            vm.Refresh();
            vm.Select("a");
            Assert.Equal("a", vm.SelectedId);
            Assert.True(vm.Select(null));            // 空/清除 → SelectedId 归零
            Assert.Equal("", vm.SelectedId);
            Assert.False(vm.Select(""));             // 已是空，再清无变化
        }

        [Fact]
        public void 选中驱动采集焦点()
        {
            var f = new FakeArchiveFacade();
            f.AddInstance("a", "A").LastStartedAt = T(5);
            var vm = new InstancesRailViewModel(f);
            vm.Refresh();
            vm.Select("a");
            Assert.Equal("a", f.VisibleInstanceId);                 // SetVisible 转发到位
        }

        [Fact]
        public void 行到子页映射_WSL归wsl其余归win()
        {
            var f = new FakeArchiveFacade();
            f.AddInstance("w", "温", wsl: true);
            f.AddInstance("x", "外");
            var vm = new InstancesRailViewModel(f);
            vm.Refresh();
            Assert.Equal("wsl", InstancesRailViewModel.SubPageOf(vm.Rows.First(r => r.Id == "w")));
            Assert.Equal("win", InstancesRailViewModel.SubPageOf(vm.Rows.First(r => r.Id == "x")));
        }
    }
}
