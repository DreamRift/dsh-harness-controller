// ============================================================================
//  PluginCatalog · 合并去重+过滤（partial 分部；纯搬移，逐字一致）
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using DshController.Core.Storage;

namespace DshController.Core
{
    public static partial class PluginCatalog
    {
        /// <summary>
        /// 合并去重：npm 包名 与 owner/repo 双键并组（不分大小写）——同一插件只要共享
        /// 任一键（npm 相同或仓库相同）就合并为一组（官方条目带 npm+repo、精选条目只有
        /// repo 时同样能并成一组）。每组取"最优"变体展示（可安装 > 已审核 > 中文简介 >
        /// star），全部变体保留。
        /// </summary>
        public static List<CatalogEntry> Merge(IEnumerable<IEnumerable<CatalogEntry>> sources)
        {
            var groups = new List<List<CatalogEntry>>();
            var pkgIndex = new Dictionary<string, List<CatalogEntry>>(StringComparer.Ordinal);
            var repoIndex = new Dictionary<string, List<CatalogEntry>>(StringComparer.Ordinal);
            if (sources != null)
                foreach (IEnumerable<CatalogEntry> src in sources)
                {
                    if (src == null) continue;
                    foreach (CatalogEntry e in src)
                    {
                        string pk = KeyOfPkg(e), rk = KeyOfRepo(e);
                        if (pk.Length == 0 && rk.Length == 0) continue;
                        List<CatalogEntry> group = null;
                        if (pk.Length > 0 && pkgIndex.TryGetValue(pk, out var byPkg)) group = byPkg;
                        if (group == null && rk.Length > 0 && repoIndex.TryGetValue(rk, out var byRepo)) group = byRepo;
                        if (group == null) { group = new List<CatalogEntry>(); groups.Add(group); }
                        group.Add(e);
                        if (pk.Length > 0) pkgIndex[pk] = group;
                        if (rk.Length > 0) repoIndex[rk] = group;
                    }
                }

            var result = new List<CatalogEntry>(groups.Count);
            foreach (List<CatalogEntry> variants in groups)
            {
                CatalogEntry best = variants.OrderByDescending(BestScore).First();
                var merged = Clone(best);
                merged.Variants = variants;
                merged.Sources = variants.Select(v => v.SourceName)
                    .Where(n => !string.IsNullOrEmpty(n)).Distinct().ToList();
                result.Add(merged);
            }
            result.Sort((a, b) =>
            {
                int c = b.Stars.CompareTo(a.Stars);
                return c != 0 ? c : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });
            return result;
        }

        private static string KeyOfPkg(CatalogEntry e)
        {
            string pkg = (e.Pkg ?? "").Trim();
            return pkg.Length > 0 ? "pkg:" + pkg.ToLowerInvariant() : "";
        }

        private static string KeyOfRepo(CatalogEntry e)
        {
            string repo = (e.Repo ?? "").Trim().TrimStart('/').TrimEnd('/');
            return repo.Length > 0 ? "repo:" + repo.ToLowerInvariant() : "";
        }

        private static long BestScore(CatalogEntry e)
        {
            long s = 0;
            if (e.Installable) s += 1_000_000_000;
            if ((e.Pkg ?? "").Trim().Length > 0) s += 500_000_000;   // npm 包名 = 首选安装目标，合并时优先保留
            if (e.Verified) s += 100_000_000;
            if ((e.Desc ?? "").Length > 0) s += 10_000_000;
            s += Math.Min(e.Stars, 9_999_999);
            return s;
        }

        private static CatalogEntry Clone(CatalogEntry e)
        {
            return new CatalogEntry
            {
                Name = e.Name, Pkg = e.Pkg, Repo = e.Repo, Desc = e.Desc, DescEn = e.DescEn,
                Category = e.Category, CategoryLabel = e.CategoryLabel, Stars = e.Stars,
                Downloads = e.Downloads, UpdatedAt = e.UpdatedAt, Verified = e.Verified,
                Unverified = e.Unverified, Install = e.Install, DshBundle = e.DshBundle,
                MinHost = e.MinHost, Tags = e.Tags ?? new List<string>(), Profile = e.Profile,
                SourceId = e.SourceId, SourceName = e.SourceName
            };
        }
        // ==================== 过滤 ====================

        public sealed class FilterOptions
        {
            public string Keyword = "";
            public string Category = "";         // "" = 全部分类
            public bool OnlyInstallable;
            public bool OnlyVerified;
            public bool SortByStars = true;
            public int Max = 400;
        }

        public static List<CatalogEntry> Filter(CatalogFile file, FilterOptions f)
        {
            var list = new List<CatalogEntry>();
            if (file?.Plugins == null || f == null) return list;

            string kw = (f.Keyword ?? "").Trim();
            string cat = (f.Category ?? "").Trim();
            foreach (CatalogEntry e in file.Plugins)
            {
                if (e == null) continue;
                if (f.OnlyInstallable && !e.Installable) continue;
                if (f.OnlyVerified && !e.Verified) continue;
                if (cat.Length > 0 && !string.Equals((e.Category ?? "").Trim(), cat, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (kw.Length > 0 && !MatchKeyword(e, kw)) continue;
                list.Add(e);
            }

            if (f.SortByStars)
                list.Sort((a, b) =>
                {
                    int c = b.Stars.CompareTo(a.Stars);
                    if (c != 0) return c;
                    return string.Compare(b.UpdatedAt, a.UpdatedAt, StringComparison.Ordinal);
                });
            else
                list.Sort((a, b) => string.Compare(b.UpdatedAt, a.UpdatedAt, StringComparison.Ordinal));

            if (f.Max > 0 && list.Count > f.Max) list = list.GetRange(0, f.Max);
            return list;
        }

        /// <summary>合并后数据里出现过的全部分类（按条目数降序；分类下拉构建用）。</summary>
        public static List<KeyValuePair<string, int>> DistinctCategories(CatalogFile file)
        {
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (file?.Plugins != null)
                foreach (CatalogEntry e in file.Plugins)
                {
                    string c = (e.Category ?? "").Trim();
                    if (c.Length == 0) continue;
                    counts[c] = (counts.TryGetValue(c, out int n) ? n : 0) + 1;
                }
            return counts.OrderByDescending(kv => kv.Value)
                .Select(kv => new KeyValuePair<string, int>(kv.Key, kv.Value)).ToList();
        }

        private static bool MatchKeyword(CatalogEntry e, string keyword)
        {
            string k = keyword.Trim();
            if (k.Length == 0) return true;
            if (Contains(e.Name, k) || Contains(e.Desc, k) || Contains(e.DescEn, k) ||
                Contains(e.Pkg, k) || Contains(e.Repo, k) || Contains(e.Category, k) ||
                Contains(e.CategoryLabel, k) || Contains(e.MinHost, k))
                return true;
            if (e.Tags != null)
                foreach (string t in e.Tags)
                    if (Contains(t, k)) return true;
            return false;
        }

        private static bool Contains(string hay, string needle)
        {
            return !string.IsNullOrEmpty(hay) &&
                   hay.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
