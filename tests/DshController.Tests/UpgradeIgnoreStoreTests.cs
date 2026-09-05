// ============================================================================
//  忽略台账（改版·插件升级治理）：忽略到版本、更高版本自动失效、跨"重启"持久、
//  覆盖与撤销、损坏兜底、空输入稳定、与版本判定链合用。
// ============================================================================

using System;
using System.IO;
using DshController.Core;
using Xunit;

namespace DshController.Tests
{
    public class UpgradeIgnoreStoreTests : IDisposable
    {
        private readonly string _dir;
        private readonly string _file;

        public UpgradeIgnoreStoreTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "dsh-upignore-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(_dir);
            _file = Path.Combine(_dir, "upgrade-ignores.json");
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); }
            catch { /* 理由: 临时目录清理失败不应让测试结果失真 */ }
        }

        private UpgradeIgnoreStore Fresh()
        {
            var s = new UpgradeIgnoreStore(_file);
            s.Load();
            return s;
        }

        [Fact]
        public void 忽略v12后该版本不再报可升级_出现v13恢复上报()
        {
            UpgradeIgnoreStore s = Fresh();
            Assert.True(s.SetIgnored("i1", "@a/one", "1.2"));
            Assert.True(s.IsSuppressed("i1", "@a/one", "1.2"));
            Assert.False(s.IsSuppressed("i1", "@a/one", "1.3"));
            Assert.True(s.IsSuppressed("i1", "@a/one", "1.1"));   // ≤ 目标都抑制（防御性；更低版本由四态判定挡在 MarketOlder）
        }

        [Fact]
        public void 重启后忽略仍生效()
        {
            UpgradeIgnoreStore s1 = Fresh();
            s1.SetIgnored("i1", "@a/one", "1.2");
            UpgradeIgnoreStore s2 = Fresh();                       // 重载同一文件 = 应用重启
            Assert.Equal("1.2", s2.IgnoredUpTo("i1", "@a/one"));
            Assert.True(s2.IsSuppressed("i1", "@a/one", "1.2"));
            Assert.False(s2.IsSuppressed("i1", "@a/one", "1.5"));
        }

        [Fact]
        public void 再次忽略覆盖为新目标并持久()
        {
            UpgradeIgnoreStore s = Fresh();
            s.SetIgnored("i1", "@a/one", "1.2");
            s.SetIgnored("i1", "@a/one", "1.4");
            Assert.True(s.IsSuppressed("i1", "@a/one", "1.3"));
            Assert.False(s.IsSuppressed("i1", "@a/one", "1.5"));
            Assert.Equal("1.4", Fresh().IgnoredUpTo("i1", "@a/one"));
        }

        [Fact]
        public void 实例与包键互不串台_键忽略大小写()
        {
            UpgradeIgnoreStore s = Fresh();
            s.SetIgnored("i1", "@A/One", "1.2");
            Assert.True(s.IsSuppressed("I1", "@a/one", "1.2"));
            Assert.False(s.IsSuppressed("i2", "@a/one", "1.2"));
            Assert.False(s.IsSuppressed("i1", "@a/two", "1.2"));
        }

        [Fact]
        public void 撤销忽略恢复上报()
        {
            UpgradeIgnoreStore s = Fresh();
            s.SetIgnored("i1", "@a/one", "1.2");
            Assert.True(s.Clear("i1", "@a/one"));
            Assert.False(s.IsSuppressed("i1", "@a/one", "1.2"));
            Assert.Equal("", s.IgnoredUpTo("i1", "@a/one"));
            Assert.False(s.Clear("i1", "@a/one"));
        }

        [Fact]
        public void 损坏文件按空台账兜底_参数不全不写()
        {
            File.WriteAllText(_file, "{ 这不是合法 json");
            UpgradeIgnoreStore s = Fresh();
            Assert.Equal("", s.IgnoredUpTo("i1", "@a/one"));
            Assert.False(s.IsSuppressed("i1", "@a/one", "1.0"));
            Assert.False(s.SetIgnored("", "@a/one", "1.0"));
            Assert.False(s.SetIgnored("i1", " ", "1.0"));
            Assert.False(s.SetIgnored("i1", "@a/one", null));
            Assert.False(s.IsSuppressed(null, null, ""));
        }

        [Fact]
        public void 与版本判定链合用_可升级且未忽略才提示()
        {
            UpgradeIgnoreStore s = Fresh();
            string target;
            Assert.Equal(UpgradeState.Upgradable, PluginUpgrade.JudgeVersion("1.1", "1.2", out target));
            s.SetIgnored("i1", "@a/one", target);
            Assert.True(s.IsSuppressed("i1", "@a/one", target));
            Assert.Equal(UpgradeState.Upgradable, PluginUpgrade.JudgeVersion("1.1", "1.3", out target));
            Assert.False(s.IsSuppressed("i1", "@a/one", target));
        }
    }
}
