// ============================================================================
//  PluginTargetViewModel 的离线单测（重构 2.0 / P3）
//
//  这块逻辑此前在两个插件面板里各写一遍（实例下拉同步、profile 归一、版本文案、
//  已装读取、忙态、过期结果丢弃），而且完全无法测试——正是 v1.0.1 那个
//  "首次进入页面不显示默认实例版本" 缺陷的温床。现在一份实现 + 一组断言。
// ============================================================================

using System.Linq;
using System.Threading.Tasks;
using DshController.Core.Archive;
using DshController.ViewModels;
using Xunit;

namespace DshController.Tests
{
    public class PluginTargetViewModelTests
    {
        [Theory]
        [InlineData("", "web")]
        [InlineData("  ", "web")]
        [InlineData("web", "web")]
        [InlineData("dev-1.0", "dev-1.0")]
        [InlineData("a b", null)]
        [InlineData("../etc", null)]
        [InlineData("x;rm", null)]
        public void profile归一化只放行安全字符(string input, string expected)
        {
            Assert.Equal(expected, PluginTargetViewModel.NormalizeProfile(input));
        }

        [Fact]
        public void 首次刷新即选中默认实例()
        {
            var fake = new FakeArchiveFacade();
            fake.AddInstance("win-1", "Windows 实例");
            fake.AddInstance("wsl-1", "WSL 实例", wsl: true);

            var vm = new PluginTargetViewModel(fake);
            vm.RefreshInstances();

            // 正是 v1.0.1 修过的缺陷：默认实例必须首次就绪，而不是等用户切换一次
            Assert.Equal(2, vm.Instances.Count);
            Assert.Equal("win-1", vm.SelectedInstance.Id);
            Assert.Equal("win-1", fake.VisibleInstanceId);
        }

        [Fact]
        public void 重建列表时保持当前选中项()
        {
            var fake = new FakeArchiveFacade();
            fake.AddInstance("a");
            fake.AddInstance("b");
            var vm = new PluginTargetViewModel(fake);
            vm.RefreshInstances();
            vm.SelectedInstance = vm.Instances.Single(d => d.Id == "b");

            vm.RefreshInstances();

            Assert.Equal("b", vm.SelectedInstance.Id);
        }

        [Fact]
        public async Task 加载已装插件并生成信息条()
        {
            var fake = new FakeArchiveFacade();
            fake.AddInstance("a");
            fake.SetVersion("a", "0.1.1-rc.2");
            fake.SetPlugins("a", "@scope/one", "@scope/two");

            var vm = new PluginTargetViewModel(fake);
            vm.RefreshInstances();
            Assert.True(await vm.ReloadAsync());

            Assert.Equal(2, vm.Installed.Count);
            Assert.Contains("@SCOPE/ONE", vm.InstalledPkgs);
            Assert.Equal("harness v0.1.1-rc.2 · 已装 2 个包", vm.MetaText);
            Assert.False(vm.IsBusy);
        }

        [Fact]
        public async Task 版本尚未探测时信息条如实说正在检测()
        {
            var fake = new FakeArchiveFacade { VersionKnown = false };
            fake.AddInstance("a");

            var vm = new PluginTargetViewModel(fake);
            vm.RefreshInstances();
            await vm.ReloadAsync();

            Assert.StartsWith("正在检测 harness 版本…", vm.MetaText);
        }

        [Fact]
        public async Task profile非法时拒绝加载并给出提示()
        {
            var fake = new FakeArchiveFacade();
            fake.AddInstance("a");
            var vm = new PluginTargetViewModel(fake);
            vm.RefreshInstances();
            vm.Profile = "bad profile";

            Assert.False(await vm.ReloadAsync());
            Assert.Contains("非法字符", vm.StatusText);
            Assert.Empty(fake.PluginScans);
        }

        [Fact]
        public async Task 没有实例时清空状态而不是抛()
        {
            var vm = new PluginTargetViewModel(new FakeArchiveFacade());
            vm.RefreshInstances();

            Assert.Null(vm.SelectedInstance);
            Assert.False(await vm.ReloadAsync());
            Assert.Empty(vm.Installed);
            Assert.Equal("", vm.MetaText);
        }

        [Fact]
        public async Task 插件操作后强制重扫不吃档案缓存()
        {
            var fake = new FakeArchiveFacade();
            fake.AddInstance("a");
            var vm = new PluginTargetViewModel(fake);
            vm.RefreshInstances();

            await vm.ReloadAsync();
            await vm.ReloadAfterPluginOpAsync();

            Assert.Equal("a/web", fake.PluginScans[0]);
            Assert.Equal("a/web/force", fake.PluginScans[1]);
            Assert.Contains("a/plugins", fake.Invalidated);
        }

        [Fact]
        public async Task 手动刷新让版本HOME插件三个分面一起失效()
        {
            var fake = new FakeArchiveFacade();
            fake.AddInstance("a");
            var vm = new PluginTargetViewModel(fake);
            vm.RefreshInstances();

            await vm.ForceRefreshAsync();

            Assert.Contains(fake.Invalidated, s => s.Contains(FacetNames.Harness) &&
                                                   s.Contains(FacetNames.Home) &&
                                                   s.Contains(FacetNames.Plugins));
        }

        [Fact]
        public async Task HOME未初始化的结论会传给视图()
        {
            var fake = new FakeArchiveFacade { Home = new HomeFacetData { Exists = false, Initialized = false } };
            fake.AddInstance("a");
            var vm = new PluginTargetViewModel(fake);
            vm.RefreshInstances();

            await vm.ReloadAsync();

            Assert.False(vm.HomeInitialized);
        }

        [Fact]
        public async Task 数据变化会通知视图重绘()
        {
            var fake = new FakeArchiveFacade();
            fake.AddInstance("a");
            fake.SetPlugins("a", "@scope/one");
            var vm = new PluginTargetViewModel(fake);
            vm.RefreshInstances();
            int notified = 0;
            vm.DataChanged += (s, e) => notified++;

            await vm.ReloadAsync();

            Assert.Equal(1, notified);
        }
    }
}
