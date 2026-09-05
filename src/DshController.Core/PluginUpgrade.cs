// ============================================================================
//  PluginUpgrade — 已装插件的"来源比对 + 版本判定"纯函数（改版·插件升级治理）
//
//  与界面无关、不联网：市场条目本身不带版本号（目录=仓库元数据），
//  "最新版本"由调用方（升级详情弹窗小类）经 provider 注入；本类只回答两问：
//    FindSource：这台已装插件能不能在市场目录里找到来源？
//      · 官方件（IsOfficial）与本地链接件（file:/link:）一律无市场来源
//      · 匹配键逐级降级：npm 包名精确 → MarketName 记录 →（大小写不敏感、trim）
//    JudgeVersion：装了 X、市场是 Y，处于哪一态？
//      · 四态：NoSource / SameVersion（含无法比较的保守情形）/ MarketOlder / Upgradable
//      · 版本序复用 PluginCompat.CompareVersions（61 条自检背书：逐段数值、预发布 <）
// ============================================================================

using System;
using System.Collections.Generic;

namespace DshController.Core
{
    /// <summary>升级判定四态（定稿）。</summary>
    public enum UpgradeState
    {
        NoSource,         // 来源找不到（官方件 / 本地链接件 / 目录里没有）
        SameVersion,      // 已是最新或无法可靠比较（保守不提示）
        MarketOlder,      // 市场版本更低（本地更新，不提示降级）
        Upgradable,       // 市场有更新版本（TargetVersion 给出目标）
    }

    public static class PluginUpgrade
    {
        /// <summary>按包名在市场目录里找来源；官方件与本地链接件视为无来源。</summary>
        public static CatalogEntry FindSource(InstalledPlugin p, IReadOnlyList<CatalogEntry> catalog)
        {
            if (p == null || catalog == null || catalog.Count == 0) return null;
            if (p.IsOfficial || p.LocalLink) return null;

            string pkg = Norm(p.Pkg);
            string marketName = Norm(p.MarketName);
            for (int i = 0; i < catalog.Count; i++)
            {
                CatalogEntry e = catalog[i];
                if (e == null) continue;
                if (pkg.Length > 0 && Eq(Norm(e.Pkg), pkg)) return e;
            }
            if (marketName.Length > 0)
            {
                for (int i = 0; i < catalog.Count; i++)
                {
                    CatalogEntry e = catalog[i];
                    if (e == null) continue;
                    if (Eq(Norm(e.Name), marketName)) return e;   // 安装记录带回了市场条目名：兜底匹配
                }
            }
            return null;
        }

        /// <summary>版本判定：已装 installedVersion vs 市场 marketVersion（可空=无法比较，保守 SameVersion）。</summary>
        public static UpgradeState JudgeVersion(string installedVersion, string marketVersion, out string targetVersion)
        {
            targetVersion = (marketVersion ?? "").Trim();
            string inst = (installedVersion ?? "").Trim();
            if (inst.Length == 0 || targetVersion.Length == 0)
            {
                targetVersion = "";
                return UpgradeState.SameVersion;                 // 缺版本信息：不提示（保守）
            }
            int c = PluginCompat.CompareVersions(inst, targetVersion);
            if (c < 0) return UpgradeState.Upgradable;           // 市场更高 → 可升级
            if (c > 0) return UpgradeState.MarketOlder;          // 市场更低 → 不降级
            targetVersion = "";
            return UpgradeState.SameVersion;                     // 相等
        }

        /// <summary>组合探测：找来源 → 用 provider 取该来源最新版本 → 判定。provider 为空视为无版本可比。</summary>
        public static UpgradeState Probe(InstalledPlugin p, IReadOnlyList<CatalogEntry> catalog,
                                         Func<CatalogEntry, string> latestVersionProvider, out CatalogEntry source, out string targetVersion)
        {
            source = FindSource(p, catalog);
            targetVersion = "";
            if (source == null) return UpgradeState.NoSource;
            string latest = latestVersionProvider == null ? null : latestVersionProvider(source);
            return JudgeVersion(p.Version, latest, out targetVersion);
        }

        public static UpgradeState Probe(InstalledPlugin p, IReadOnlyList<CatalogEntry> catalog,
                                         Func<CatalogEntry, string> latestVersionProvider)
        {
            CatalogEntry s; string t;
            return Probe(p, catalog, latestVersionProvider, out s, out t);
        }

        private static string Norm(string s)
        {
            return (s ?? "").Trim();
        }

        /// <summary>包名/条目名比对忽略大小写（npm 名约定小写，忽略无假匹配风险；条目名大小写差异不该漏掉来源）。</summary>
        private static bool Eq(string a, string b)
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }
}
