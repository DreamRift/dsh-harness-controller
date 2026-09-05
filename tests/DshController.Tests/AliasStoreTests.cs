// ============================================================================
//  AliasStoreTests — 别名台账离线单测（改版·改名入口）
// ============================================================================

using System;
using System.IO;
using DshController.Core.Storage;
using Xunit;

namespace DshController.Tests
{
    public class AliasStoreTests : IDisposable
    {
        private readonly string _file = Path.Combine(Path.GetTempPath(), "dsh-alias-test-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".json");

        public void Dispose()
        {
            try { File.Delete(_file); }
            catch (Exception) { /* 理由: 测试临时文件清理失败可忽略（句柄占用等） */ }
        }

        [Fact]
        public void 设置别名_落盘可读回()
        {
            var s1 = new AliasStore(_file);
            s1.Set("a1", "我的机器");
            var s2 = new AliasStore(_file);
            Assert.Equal("我的机器", s2.Get("a1"));
        }

        [Fact]
        public void 清除别名_空串()
        {
            var s = new AliasStore(_file);
            s.Set("a1", "别名");
            s.Set("a1", "");
            Assert.Equal("", s.Get("a1"));
        }

        [Fact]
        public void 不存在的id_返回空()
        {
            var s = new AliasStore(_file);
            Assert.Equal("", s.Get("nope"));
        }

        [Fact]
        public void 空id忽略()
        {
            var s = new AliasStore(_file);
            s.Set("", "某");
            Assert.Equal("", s.Get(""));
        }

        [Fact]
        public void 损坏文件_空台账不抛()
        {
            File.WriteAllText(_file, "{broken}");
            var s = new AliasStore(_file);
            Assert.Equal("", s.Get("a1"));
        }
    }
}
