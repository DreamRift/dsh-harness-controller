// ============================================================================
//  PluginCatalog — 插件目录数据：拉取 / 缓存 / 过滤（v0.6.0 插件市场）
//
//  数据源：awesome-dsh-plugin 社区目录（每日抓取 GitHub `dsh-plugin` topic 并
//  人工复核），条目遵循其下游市场接口规范（字段契约只增不改）：
//    { version, updatedAt, source, plugins: [ { name, pkg, repo, desc, descEn,
//      category, stars, updatedAt, verified, install, dshBundle, minHost,
//      tags, profile } ] }
//  规范中与本控制器直接相关的消费约定：
//    - minHost = 插件仓库明确声明的最低 DSH 版本：声明了就原样展示，不得推测；
//    - dshBundle=false 或 pkg 为空 → 只可浏览，不提供安装入口；
//    - 消费方至少 24h 本地缓存，网络失败时用过期缓存兜底。
//  容错：根对象 plugins 数组 / 根数组两种形态都能解析；未知字段经
//  JsonExtensionData 保留；单条解析失败跳过不中断；缓存写盘失败不影响内存使用。
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

namespace DshController.Core
{
    /// <summary>目录中的一个插件条目（字段名与数据源规范一致）。</summary>
    public sealed class CatalogEntry
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        /// <summary>npm 包名（dsh plugin add 的首选目标）；空 = 无 npm 包。</summary>
        [JsonPropertyName("pkg")]
        public string Pkg { get; set; } = "";

        /// <summary>GitHub 仓库 owner/repo。</summary>
        [JsonPropertyName("repo")]
        public string Repo { get; set; } = "";

        [JsonPropertyName("desc")]
        public string Desc { get; set; } = "";

        [JsonPropertyName("descEn")]
        public string DescEn { get; set; } = "";

        [JsonPropertyName("category")]
        public string Category { get; set; } = "";

        [JsonPropertyName("stars")]
        public int Stars { get; set; }

        /// <summary>仓库最近推送时间（数据源每日刷新）。</summary>
        [JsonPropertyName("updatedAt")]
        public string UpdatedAt { get; set; } = "";

        /// <summary>数据源人工复核标记。</summary>
        [JsonPropertyName("verified")]
        public bool Verified { get; set; }

        /// <summary>数据源给出的官方安装命令示例（展示用）。</summary>
        [JsonPropertyName("install")]
        public string Install { get; set; } = "";

        /// <summary>是否声明了有效 dsh.bundle（false = 只可浏览）。</summary>
        [JsonPropertyName("dshBundle")]
        public bool DshBundle { get; set; }

        /// <summary>仓库明确声明的最低 DSH 版本（未声明则为空，不得推测）。</summary>
        [JsonPropertyName("minHost")]
        public string MinHost { get; set; } = "";

        [JsonPropertyName("tags")]
        public List<string> Tags { get; set; } = new List<string>();

        /// <summary>推荐生效 profile（如 web）；空 = 未声明。</summary>
        [JsonPropertyName("profile")]
        public string Profile { get; set; } = "";

        /// <summary>保留未知字段（规范"只增不改"，前向兼容）。</summary>
        [JsonExtensionData]
        public Dictionary<string, JsonElement> Extra { get; set; }

        /// <summary>安装目标：npm 包名优先，否则 github:owner/repo；都为空 = 无可装来源。</summary>
        [JsonIgnore]
        public string InstallTarget
        {
            get
            {
                string p = (Pkg ?? "").Trim();
                if (p.Length > 0) return p;
                string r = (Repo ?? "").Trim().TrimStart('/');
                if (r.Length > 0 && r.Contains('/')) return "github:" + r;
                return "";
            }
        }

        /// <summary>是否可安装（规范：有有效 dsh.bundle 且能确定安装来源）。</summary>
        [JsonIgnore]
        public bool Installable => DshBundle && InstallTarget.Length > 0;
    }

    /// <summary>目录文件根结构。</summary>
    public sealed class CatalogFile
    {
        [JsonPropertyName("version")]
        public int Version { get; set; }

        [JsonPropertyName("updatedAt")]
        public string UpdatedAt { get; set; } = "";

        [JsonPropertyName("source")]
        public string Source { get; set; } = "";

        [JsonPropertyName("plugins")]
        public List<CatalogEntry> Plugins { get; set; } = new List<CatalogEntry>();
    }

    /// <summary>一次目录加载的结果（网络 / 内存 / 磁盘缓存）。</summary>
    public sealed class CatalogLoadResult
    {
        public CatalogFile Catalog;
        public bool FromNetwork;        // 本次是否成功走了网络刷新
        public bool Stale;              // 是否为过期缓存兜底（网络失败时）
        public DateTime FetchedAt;      // 数据抓取时间
        public string SourceUrl = "";
        public string Error = "";       // 网络失败原因（Stale 时供 UI 展示）

        /// <summary>UI 展示的数据新鲜度说明。</summary>
        [JsonIgnore]
        public string FreshnessText
        {
            get
            {
                string t = FetchedAt == DateTime.MinValue
                    ? "未知时间"
                    : FetchedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);
                if (FromNetwork) return "已联网刷新 · 数据时间 " + t;
                if (Stale) return "网络失败，使用离线缓存（" + t + " 抓取）" + (Error.Length > 0 ? "：" + Error : "");
                return "缓存数据（" + t + " 抓取）";
            }
        }
    }

    public static class PluginCatalog
    {
        /// <summary>默认目录源（awesome-dsh-plugin 全量快照，与 dsh-market 默认源一致）。</summary>
        public const string DefaultRegistryUrl = "https://awesome-dsh-plugin.com/plugins.json";

        /// <summary>精选子集（人工复核 + minHost 字段更完整），源不可用时的第二候选。</summary>
        public const string FallbackRegistryUrl =
            "https://raw.githubusercontent.com/bruc3van/awesome-dsh-plugin/main/data/market.json";

        private static readonly TimeSpan FreshWindow = TimeSpan.FromHours(24);   // 规范约定 ≥24h 缓存

        private static CatalogLoadResult _memory;                                // 进程内缓存
        private static readonly object Lock = new object();

        public static string CacheDir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DshController", "plugin-cache");

        private static string CacheFilePath => Path.Combine(CacheDir, "catalog.json");

        /// <summary>
        /// 获取目录数据：内存缓存 → 24h 内磁盘缓存 → 网络刷新；网络失败回退过期磁盘缓存。
        /// force=true 跳过新鲜缓存强制走网络（"刷新"按钮）。
        /// </summary>
        public static async Task<CatalogLoadResult> LoadAsync(string registryUrl, bool force)
        {
            string url = NormalizeUrl(registryUrl);
            lock (Lock)
            {
                if (!force && _memory != null && string.Equals(_memory.SourceUrl, url, StringComparison.OrdinalIgnoreCase))
                    return _memory;
            }

            // ① 磁盘缓存（未 force 且未过期时直接用，避免每次开页都联网）
            CatalogLoadResult cached = ReadCache();
            if (!force && cached != null && string.Equals(cached.SourceUrl, url, StringComparison.OrdinalIgnoreCase)
                && DateTime.UtcNow - cached.FetchedAt < FreshWindow)
            {
                lock (Lock) { _memory = cached; }
                return cached;
            }

            // ② 网络刷新：主源失败自动尝试精选源（同一规范两种快照）
            string err = "";
            foreach (string candidate in CandidateUrls(url))
            {
                string json = await HttpFetch.GetStringAsync(candidate, 25000).ConfigureAwait(false);
                if (json == null)
                {
                    if (err.Length == 0) err = "目录源无响应";
                    continue;
                }
                CatalogFile parsed = Parse(json);
                if (parsed == null || parsed.Plugins.Count == 0)
                {
                    if (err.Length == 0) err = "目录数据解析失败";
                    continue;
                }
                var ok = new CatalogLoadResult
                {
                    Catalog = parsed,
                    FromNetwork = true,
                    FetchedAt = DateTime.UtcNow,
                    SourceUrl = candidate
                };
                WriteCache(ok);
                lock (Lock) { _memory = ok; }
                return ok;
            }

            // ③ 网络失败 → 过期磁盘缓存兜底（规范约定）
            if (cached != null && cached.Catalog != null)
            {
                cached.Stale = true;
                cached.Error = err;
                lock (Lock) { _memory = cached; }
                return cached;
            }
            return new CatalogLoadResult { Catalog = new CatalogFile(), Error = err };
        }

        private static IEnumerable<string> CandidateUrls(string primary)
        {
            yield return primary;
            if (!string.Equals(primary, FallbackRegistryUrl, StringComparison.OrdinalIgnoreCase))
                yield return FallbackRegistryUrl;
        }

        private static string NormalizeUrl(string configured)
        {
            string u = (configured ?? "").Trim();
            if (u.Length == 0) u = DefaultRegistryUrl;
            if (!u.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !u.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                u = "https://" + u;
            return u;
        }

        /// <summary>解析目录 JSON：根对象带 plugins 数组，或根本身就是条目数组。</summary>
        public static CatalogFile Parse(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                var opts = new JsonSerializerOptions
                {
                    AllowTrailingCommas = true,
                    ReadCommentHandling = JsonCommentHandling.Skip,
                    PropertyNameCaseInsensitive = true
                };
                // 先按根对象解析
                try
                {
                    var root = JsonSerializer.Deserialize<CatalogFile>(json, opts);
                    if (root != null && root.Plugins != null)
                    {
                        root.Plugins = root.Plugins.Where(p => p != null).ToList();
                        return root;
                    }
                }
                catch (JsonException) { /* 尝试根数组形态 */ }

                var list = JsonSerializer.Deserialize<List<CatalogEntry>>(json, opts);
                if (list == null) return null;
                return new CatalogFile { Plugins = list.Where(p => p != null).ToList() };
            }
            catch
            {
                return null;
            }
        }

        // ---------------- 过滤与排序（UI 与自检共用） ----------------

        public sealed class FilterOptions
        {
            public string Keyword = "";          // 匹配 name/desc/descEn/pkg/repo/tags
            public string Category = "";         // "" = 全部分类
            public bool OnlyInstallable;         // 仅显示可安装（dsh.bundle + 来源）
            public bool OnlyVerified;            // 仅显示人工复核条目
            public bool SortByStars = true;      // true = 热度（star），false = 最近更新
            public int Max = 400;                // UI 一次渲染上限，防大列表卡顿
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

        private static bool MatchKeyword(CatalogEntry e, string keyword)
        {
            string k = keyword.Trim();
            if (k.Length == 0) return true;
            if (Contains(e.Name, k) || Contains(e.Desc, k) || Contains(e.DescEn, k) ||
                Contains(e.Pkg, k) || Contains(e.Repo, k) || Contains(e.Category, k) ||
                Contains(e.MinHost, k))
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

        // ---------------- 磁盘缓存（仿 InstanceRegistry 原子写） ----------------

        private sealed class CacheFile
        {
            [JsonPropertyName("fetchedAt")]
            public string FetchedAt { get; set; } = "";

            [JsonPropertyName("sourceUrl")]
            public string SourceUrl { get; set; } = "";

            [JsonPropertyName("catalog")]
            public CatalogFile Catalog { get; set; }
        }

        private static CatalogLoadResult ReadCache()
        {
            try
            {
                if (!File.Exists(CacheFilePath)) return null;
                var cache = JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(CacheFilePath, Encoding.UTF8));
                if (cache?.Catalog == null) return null;
                DateTime t = DateTime.TryParse(cache.FetchedAt, null, DateTimeStyles.RoundtripKind, out var parsed)
                    ? parsed : DateTime.MinValue;
                return new CatalogLoadResult
                {
                    Catalog = cache.Catalog,
                    FetchedAt = t,
                    SourceUrl = cache.SourceUrl ?? ""
                };
            }
            catch
            {
                return null;
            }
        }

        private static void WriteCache(CatalogLoadResult result)
        {
            try
            {
                Directory.CreateDirectory(CacheDir);
                var cache = new CacheFile
                {
                    FetchedAt = result.FetchedAt.ToString("O"),
                    SourceUrl = result.SourceUrl,
                    Catalog = result.Catalog
                };
                var opts = new JsonSerializerOptions { WriteIndented = false, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
                string tmp = CacheFilePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(cache, opts), new UTF8Encoding(false));
                File.Move(tmp, CacheFilePath, overwrite: true);
            }
            catch
            {
                // 缓存写盘失败不影响本次使用（内存已持有）
            }
        }
    }
}
