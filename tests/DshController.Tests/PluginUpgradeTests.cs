// ============================================================================
//  来源比对四态矩阵（改版·插件升级治理·来源比对）：
//  无来源（目录没有/官方件/本地链接件/MarketName 兜底）· 同版本 · 市场更低（降级）·
//  可升级（含逐段数值与预发布边界）· 版本缺失的保守不提示。
// ============================================================================

using System;
using System.Collections.Generic;
using DshController.Core;
using Xunit;

namespace DshController.Tests
{
    public class PluginUpgradeTests
    {
        private static InstalledPlugin P(string pkg, string ver = "1.0.0", bool official = false, string depRef = "", string marketName = "")
            => new InstalledPlugin { Pkg = pkg, Version = ver, IsOfficial = official, DepRef = depRef, MarketName = marketName };

        private static CatalogEntry E(string pkg, string name = null)
            => new CatalogEntry { Pkg = pkg ?? "", Name = name ?? pkg ?? "" };

        private static readonly IReadOnlyList<CatalogEntry> Empty = new List<CatalogEntry>();

        // ---------------- 来源匹配 ----------------

        [Fact]
        public void 目录没有该包_无来源()
        {
            Assert.Null(PluginUpgrade.FindSource(P("@scope/one"), new[] { E("@scope/two") }));
            Assert.Equal(UpgradeState.NoSource, PluginUpgrade.Probe(P("@scope/one"), new[] { E("@scope/two") }, _ => "9.9.9"));
        }

        [Fact]
        public void 官方件与本地链接件_视为无来源()
        {
            var cat = new[] { E("@dsh/core") };
            Assert.Null(PluginUpgrade.FindSource(P("@dsh/core", official: true), cat));
            Assert.Null(PluginUpgrade.FindSource(new InstalledPlugin { Pkg = "@dsh/core", DepRef = "link:../dev" }, cat));
            Assert.Null(PluginUpgrade.FindSource(new InstalledPlugin { Pkg = "@dsh/core", DepRef = "file:C:\\local" }, cat));
        }

        [Fact]
        public void 包名匹配大小写不敏感带空白()
        {
            var found = PluginUpgrade.FindSource(P("  @DSH/Core "), new[] { E("@dsh/core") });
            Assert.NotNull(found);
        }

        [Fact]
        public void MarketName兜底匹配条目名()
        {
            var p = P("@scoped/actual", marketName: "archify");
            var found = PluginUpgrade.FindSource(p, new[] { E("other-pkg", name: " Archify ") });
            // 条目名比对忽略大小写 + trim（大小写差异不该漏掉来源）
            Assert.NotNull(found);
            var found2 = PluginUpgrade.FindSource(P("@scoped/actual", marketName: "archify"), new[] { E("other-pkg", name: "archify") });
            Assert.NotNull(found2);
        }

        [Fact]
        public void 空输入稳定()
        {
            Assert.Null(PluginUpgrade.FindSource(null, new[] { E("a") }));
            Assert.Null(PluginUpgrade.FindSource(P("a"), null));
            Assert.Null(PluginUpgrade.FindSource(P("a"), Empty));
            Assert.Equal(UpgradeState.NoSource, PluginUpgrade.Probe(P("a"), Empty, _ => "1"));
        }

        // ---------------- 版本判定四态 ----------------

        [Fact]
        public void 同版本_提示为已最新()
        {
            string target;
            Assert.Equal(UpgradeState.SameVersion, PluginUpgrade.JudgeVersion("1.2.3", "1.2.3", out target));
            Assert.Equal("", target);
        }

        [Fact]
        public void 市场版本更低_降级态不提示()
        {
            string target;
            Assert.Equal(UpgradeState.MarketOlder, PluginUpgrade.JudgeVersion("1.2.0", "1.1.9", out target));
            Assert.Equal("1.1.9", target);   // 目标保留但状态不升级
        }

        [Fact]
        public void 市场更高_可升级并给出目标版本()
        {
            string target;
            Assert.Equal(UpgradeState.Upgradable, PluginUpgrade.JudgeVersion("0.1.1-rc.2", "0.2.0", out target));
            Assert.Equal("0.2.0", target);
        }

        [Fact]
        public void 逐段数值边界_1点2对1点10()
        {
            string target;
            Assert.Equal(UpgradeState.Upgradable, PluginUpgrade.JudgeVersion("1.2.3", "1.10.0", out target));
        }

        [Fact]
        public void 预发布小于正式版本()
        {
            string target;
            Assert.Equal(UpgradeState.MarketOlder, PluginUpgrade.JudgeVersion("1.0.0", "1.0.0-rc.1", out target));
        }

        [Fact]
        public void 版本缺失或空_保守不提示()
        {
            string target;
            Assert.Equal(UpgradeState.SameVersion, PluginUpgrade.JudgeVersion("1.0.0", "", out target));
            Assert.Equal("", target);
            Assert.Equal(UpgradeState.SameVersion, PluginUpgrade.JudgeVersion("", "2.0.0", out target));
            Assert.Equal(UpgradeState.SameVersion, PluginUpgrade.JudgeVersion(null, null, out target));
        }

        [Fact]
        public void 组合探测_来源最新版本全链()
        {
            CatalogEntry src; string tgt;
            var p = P("@a/one", "1.0.0");
            var cat = new[] { E("@a/two"), E("@a/one") };
            var st = PluginUpgrade.Probe(p, cat, e => e.Pkg == "@a/one" ? "1.3.0" : "0.0.1", out src, out tgt);
            Assert.Equal(UpgradeState.Upgradable, st);
            Assert.Equal("@a/one", src.Pkg);
            Assert.Equal("1.3.0", tgt);
            // provider 给空版本 → 保守
            Assert.Equal(UpgradeState.SameVersion, PluginUpgrade.Probe(p, cat, _ => "", out src, out tgt));
        }
    }
}
