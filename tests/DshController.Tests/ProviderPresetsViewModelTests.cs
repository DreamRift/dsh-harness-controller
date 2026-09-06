// ============================================================================
//  ProviderPresetsViewModelTests — API 编辑页 VM 离线单测（改版·供应商编辑页）
//  覆盖：增/改/删走全局台账、校验错误透传、选中态保持。
// ============================================================================

using System;
using System.IO;
using DshController.Core;
using DshController.ViewModels;
using Xunit;

namespace DshController.Tests
{
    public class ProviderPresetsViewModelTests : IDisposable
    {
        private readonly string _file = Path.Combine(Path.GetTempPath(), "dsh-pvm-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".json");

        public void Dispose()
        {
            try { File.Delete(_file); }
            catch (Exception) { /* 理由: 测试临时文件清理失败可忽略 */ }
        }

        [Fact]
        public void 新增_行出现并选中()
        {
            var vm = new ProviderPresetsViewModel(new ProviderPresetStore(_file));
            Assert.True(vm.TryAdd("DeepSeek", "deepseek", "https://api.deepseek.com", "sk-1", "chat", true, out string id, out string err));
            Assert.Equal(2, vm.Rows.Count);                  // 内置官方置顶 + 新行
            Assert.True(vm.Rows[0].IsBuiltin);
            Assert.Equal(id, vm.SelectedId);
            Assert.Equal("DeepSeek", vm.RowById(id).Name);
            Assert.Equal("自定义", vm.RowById(id).TagText);
            Assert.Contains("启用", vm.RowById(id).MetaText);
        }

        [Fact]
        public void 校验错误透传_不改行()
        {
            var vm = new ProviderPresetsViewModel(new ProviderPresetStore(_file));
            Assert.False(vm.TryAdd("", "deepseek", "", "", "", true, out _, out string err));
            Assert.Equal("预设名不能为空", err);
            Assert.Single(vm.Rows);                          // 仅内置官方
            Assert.True(vm.TryAdd("A", "deepseek", "", "", "", true, out string id, out _));
            Assert.False(vm.TryUpdate(id, "A", "deepseek", "ftp://x", "", "", true, out string err2));
            Assert.Equal("BaseUrl 须以 http:// 或 https:// 开头", err2);
            Assert.Equal("A", vm.RowById(id).Name);
        }

        [Fact]
        public void 编辑与删除_选中态维护()
        {
            var vm = new ProviderPresetsViewModel(new ProviderPresetStore(_file));
            vm.TryAdd("旧名", "deepseek", "", "", "", true, out string id, out _);
            Assert.True(vm.TryUpdate(id, "新名", "deepseek", "https://x.com/v1", "", "", true, out _));
            Assert.Equal("新名", vm.RowById(id).Name);
            Assert.True(vm.TryDelete(id));
            Assert.Single(vm.Rows);                          // 内置官方仍在
            Assert.True(vm.Rows[0].IsBuiltin);
            Assert.Equal("", vm.SelectedId);
        }

        [Fact]
        public void 落盘重开_列表仍在()
        {
            var vm1 = new ProviderPresetsViewModel(new ProviderPresetStore(_file));
            vm1.TryAdd("持久", "deepseek", "", "", "", true, out string id, out _);
            var vm2 = new ProviderPresetsViewModel(new ProviderPresetStore(_file));
            Assert.Equal(2, vm2.Rows.Count);
            Assert.Equal("持久", vm2.RowById(id).Name);
            Assert.True(vm2.Rows[0].IsBuiltin);
        }
    }
}
