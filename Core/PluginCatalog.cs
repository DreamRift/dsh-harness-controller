// ============================================================================
//  PluginCatalog — 插件目录：多来源拉取 / 合并去重 / 中文分类（v0.6.1）
//
//  v0.6.1 按真实数据源 schema 重写（v0.6.0 依据的"接口规范"文档与线上文件不一致，
//  导致官方源能下载但解析不出可用字段）。内置来源（多选，多选时合并去重）：
//    official    官方全量目录  https://awesome-dsh-plugin.com/plugins.json
//                （awesome-dsh-plugin 每日构建，2400+ 条，全量中文简介、npm 包名、
//                 官方中文分类标签 categories{code:{en,zh}}、install 命令示例）
//    curated     GitHub 精选快照  data/market.json（600 精选，条目带 category_zh）
//                —— GitHub 仓库数据，经 jsDelivr CDN 镜像拉取（raw 直连兜底；
//                部分网络直连 GitHub 不可达而 CDN 可达）
//    github-live GitHub 实时搜索  api.github.com topic:dsh-plugin（直连，
//                未审核条目需确认后安装；部分网络不可达——失败不影响其他来源）
//    custom      自定义 URL（AppSettings.PluginRegistryUrl，兼容官方快照同构
//                JSON 或旧接口规范 {name,pkg,repo,dshBundle,minHost,...}）
//  去重键：npm 包名优先，否则 GitHub owner/repo（不分大小写）；合并保留各源
//  变体（Variants），字段取"最优"变体（可安装 > 已审核 > 中文简介 > star）。
//  缓存：每来源独立磁盘缓存（24h，失败回退过期缓存），来源间互不影响。
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
    // ==================== 条目模型 ====================

    /// <summary>目录中的一个插件条目（已归一化；多来源合并后 Sources/Variants 指向各源原条目）。</summary>
    public sealed class CatalogEntry
    {
        public string Name { get; set; } = "";
        /// <summary>npm 包名（dsh plugin add 首选目标）；空 = 无 npm 包。</summary>
        public string Pkg { get; set; } = "";
        /// <summary>GitHub 仓库 owner/repo。</summary>
        public string Repo { get; set; } = "";
        public string Desc { get; set; } = "";
        public string DescEn { get; set; } = "";
        /// <summary>来源原始分类代码（过滤用）。</summary>
        public string Category { get; set; } = "";
        /// <summary>分类中文标签（来源自带或内置映射；卡片展示用）。</summary>
        public string CategoryLabel { get; set; } = "";
        public int Stars { get; set; }
        public int Downloads { get; set; }
        public string UpdatedAt { get; set; } = "";
        /// <summary>来源人工复核标记。</summary>
        public bool Verified { get; set; }
        /// <summary>未审核条目（如 GitHub 实时搜索）：可安装但需确认。</summary>
        public bool Unverified { get; set; }
        /// <summary>来源给出的官方安装命令示例（展示用）。</summary>
        public string Install { get; set; } = "";
        /// <summary>是否确认提供 dsh.bundle。</summary>
        public bool DshBundle { get; set; }
        /// <summary>仓库明确声明的最低 DSH 版本（未声明则为空，不得推测）。</summary>
        public string MinHost { get; set; } = "";
        public List<string> Tags { get; set; } = new List<string>();
        public string Profile { get; set; } = "";

        // ---- 来源归属（合并时填充，不参与序列化/比较） ----

        [JsonIgnore] public string SourceId { get; set; } = "";
        [JsonIgnore] public string SourceName { get; set; } = "";
        /// <summary>合并后条目的全部来源显示名（去重）。</summary>
        [JsonIgnore] public List<string> Sources { get; set; }
        /// <summary>合并后条目的各来源原条目（详情里"从此源安装"用）。</summary>
        [JsonIgnore] public List<CatalogEntry> Variants { get; set; }

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

        /// <summary>是否可安装（有确定安装来源；Unverified 条目也可装但需确认）。</summary>
        [JsonIgnore]
        public bool Installable => InstallTarget.Length > 0;
    }

    /// <summary>目录数据根（合并后）。</summary>
    public sealed class CatalogFile
    {
        public List<CatalogEntry> Plugins { get; set; } = new List<CatalogEntry>();
    }

    // ==================== 来源定义 ====================

    public enum MarketSourceKind { JsonCatalog, GithubTopic }

    public sealed class MarketSource
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Note { get; set; } = "";
        /// <summary>URL 回退链：逐个尝试，第一个成功者生效。</summary>
        public string[] Urls { get; set; } = Array.Empty<string>();
        public MarketSourceKind Kind { get; set; } = MarketSourceKind.JsonCatalog;

        public override string ToString() => Name;
    }

    public static class MarketSources
    {
        /// <summary>官方全量目录（每日构建，数据最全：中文简介 + npm 包名 + 中文分类）。</summary>
        public static readonly MarketSource Official = new MarketSource
        {
            Id = "official",
            Name = "官方全量",
            Note = "awesome-dsh-plugin 每日构建 · 2400+ 插件 · 中文简介 · npm 包名 · 官方中文分类",
            Urls = new[] { "https://awesome-dsh-plugin.com/plugins.json" },
            Kind = MarketSourceKind.JsonCatalog
        };

        /// <summary>GitHub 精选快照（GitHub 仓库数据经 jsDelivr CDN 镜像，600 精选 + 中文分类）。</summary>
        public static readonly MarketSource Curated = new MarketSource
        {
            Id = "curated",
            Name = "GitHub精选",
            Note = "GitHub 仓库数据 · 经 jsDelivr CDN 镜像（直连不可达也可用）· 600 精选 · 中文分类",
            Urls = new[]
            {
                "https://cdn.jsdelivr.net/gh/bruc3van/awesome-dsh-plugin@main/data/market.json",
                "https://raw.githubusercontent.com/bruc3van/awesome-dsh-plugin/main/data/market.json"
            },
            Kind = MarketSourceKind.JsonCatalog
        };

        /// <summary>GitHub 实时搜索（直连 api.github.com；未审核条目；部分网络不可达）。</summary>
        public static readonly MarketSource GithubLive = new MarketSource
        {
            Id = "github-live",
            Name = "GitHub实时",
            Note = "直连 api.github.com 搜索 topic:dsh-plugin（最新 · 未审核 · 部分网络不可达，失败不影响其他来源）",
            Urls = new[] { "https://api.github.com/search/repositories?q=topic:dsh-plugin&sort=stars&order=desc&per_page=100" },
            Kind = MarketSourceKind.GithubTopic
        };

        /// <summary>自定义来源（AppSettings.PluginRegistryUrl 非空时启用）。</summary>
        public static MarketSource Custom(string url) => new MarketSource
        {
            Id = "custom",
            Name = "自定义源",
            Note = url,
            Urls = new[] { url },
            Kind = MarketSourceKind.JsonCatalog
        };

        public static IReadOnlyList<MarketSource> BuiltIn => new[] { Official, Curated, GithubLive };

        public static MarketSource ById(string id) =>
            BuiltIn.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    // ==================== 加载结果 ====================

    /// <summary>单个来源的加载状态（UI 展示哪个源成功/失败/用了缓存）。</summary>
    public sealed class SourceStatus
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public bool Ok { get; set; }
        public bool FromCache { get; set; }
        public int Count { get; set; }
        public string Error { get; set; } = "";
        public DateTime FetchedAt { get; set; }
    }

    public sealed class MarketLoadResult
    {
        public CatalogFile Catalog { get; set; } = new CatalogFile();
        public List<SourceStatus> Statuses { get; set; } = new List<SourceStatus>();

        /// <summary>一行汇总（UI 状态栏）：各源结果 + 数据时间。</summary>
        public string SummaryText
        {
            get
            {
                var parts = new List<string>();
                foreach (SourceStatus s in Statuses)
                {
                    if (s.Ok) parts.Add(s.Name + " " + s.Count + (s.FromCache ? "（缓存）" : ""));
                    else parts.Add(s.Name + " 失败" + (s.Error.Length > 0 ? "：" + s.Error : ""));
                }
                DateTime t = Statuses.Where(s => s.Ok).Select(s => s.FetchedAt).DefaultIfEmpty(DateTime.MinValue).Max();
                string time = t == DateTime.MinValue ? ""
                    : " ｜ 数据时间 " + t.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.CurrentCulture);
                return string.Join(" ｜ ", parts) + time;
            }
        }
    }

    // ==================== 目录服务 ====================

    public static class PluginCatalog
    {
        private static readonly object Lock = new object();
        private static readonly Dictionary<string, CatalogSourceData> Memory =
            new Dictionary<string, CatalogSourceData>(StringComparer.OrdinalIgnoreCase);

        /// <summary>来源自带的中文分类标签（official 顶层 categories 等），随加载更新。</summary>
        public static Dictionary<string, string> DynamicCategoryLabels { get; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private sealed class CatalogSourceData
        {
            public DateTime FetchedAt;
            public List<CatalogEntry> Entries = new List<CatalogEntry>();
        }

        public static string CacheDir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DshController", "plugin-cache");

        private static string CacheFilePath(string sourceId) =>
            Path.Combine(CacheDir, "catalog-" + SanitizeId(sourceId) + ".json");

        private static string SanitizeId(string id)
        {
            string safe = string.Join("_", (id ?? "src").Split(
                Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
            return safe.Length == 0 ? "src" : safe;
        }

        // ---------------- 来源选择（设置 → 实际来源列表） ----------------

        /// <summary>按设置解析启用来源：内置多选 + 自定义 URL（填了才启用）。null/空 = 默认 official+curated。</summary>
        public static List<MarketSource> SelectSources(AppSettings settings)
        {
            var chosen = new List<MarketSource>();
            List<string> ids = settings?.PluginSources;
            IEnumerable<string> effective = (ids == null || ids.Count == 0)
                ? new[] { "official", "curated" }
                : ids;
            foreach (string id in effective)
            {
                MarketSource s = MarketSources.ById(id);
                if (s != null && !chosen.Contains(s)) chosen.Add(s);
            }
            string custom = (settings?.PluginRegistryUrl ?? "").Trim();
            if (custom.Length > 0)
            {
                if (!custom.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                    !custom.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    custom = "https://" + custom;
                chosen.Add(MarketSources.Custom(custom));
            }
            return chosen;
        }

        // ---------------- 多来源加载与合并 ----------------

        /// <summary>
        /// 并行加载全部启用来源 → 合并去重。单个来源失败自动回退其过期缓存；
        /// 全部失败且无缓存时返回空目录（UI 显示各源状态）。
        /// 缓存新鲜窗口 = 设置的自动刷新间隔（小时，默认 24，0 = 每次都联网）。
        /// </summary>
        public static async Task<MarketLoadResult> LoadAllAsync(AppSettings settings, bool force)
        {
            List<MarketSource> sources = SelectSources(settings);
            int hours = settings?.PluginAutoRefreshHours ?? 24;
            if (hours < 0) hours = 0;
            TimeSpan freshWindow = TimeSpan.FromHours(hours);

            var tasks = sources.Select(async s =>
            {
                var st = new SourceStatus { Id = s.Id, Name = s.Name };
                CatalogSourceData data = await LoadSourceAsync(s, force, freshWindow, st).ConfigureAwait(false);
                return new { source = s, status = st, data };
            }).ToArray();

            var results = await Task.WhenAll(tasks).ConfigureAwait(false);

            var statuses = new List<SourceStatus>();
            var perSource = new List<CatalogSourceData>();
            foreach (var r in results)
            {
                statuses.Add(r.status);
                if (r.data != null) perSource.Add(r.data);
            }

            var merged = new CatalogFile { Plugins = Merge(perSource.Select(d => d.Entries)) };
            return new MarketLoadResult { Catalog = merged, Statuses = statuses };
        }

        private static async Task<CatalogSourceData> LoadSourceAsync(MarketSource source, bool force,
            TimeSpan freshWindow, SourceStatus status)
        {
            // ① 内存缓存
            CatalogSourceData mem;
            lock (Lock) Memory.TryGetValue(source.Id, out mem);
            if (!force && mem != null && DateTime.UtcNow - mem.FetchedAt < freshWindow)
            {
                status.Ok = true;
                status.Count = mem.Entries.Count;
                status.FetchedAt = mem.FetchedAt;
                return mem;
            }

            // ①.5 磁盘缓存仍在新鲜窗口内（应用重启后内存为空）→ 直接用，不联网
            if (!force && mem == null)
            {
                CatalogSourceData disk = ReadCache(source.Id);
                if (disk != null && DateTime.UtcNow - disk.FetchedAt < freshWindow)
                {
                    lock (Lock) Memory[source.Id] = disk;
                    status.Ok = true;
                    status.FromCache = true;
                    status.Count = disk.Entries.Count;
                    status.FetchedAt = disk.FetchedAt;
                    return disk;
                }
            }

            // ② 网络（URL 回退链）
            string err = "";
            foreach (string url in source.Urls)
            {
                string json = await HttpFetch.GetStringAsync(url, 30000).ConfigureAwait(false);
                if (json == null) { if (err.Length == 0) err = "网络无响应"; continue; }
                List<CatalogEntry> entries = ParseSourceJson(json, source.Kind, out string parseErr);
                if (entries == null || entries.Count == 0)
                {
                    if (err.Length == 0) err = parseErr.Length > 0 ? parseErr : "解析失败";
                    continue;
                }

                var data = new CatalogSourceData { FetchedAt = DateTime.UtcNow, Entries = entries };
                lock (Lock) Memory[source.Id] = data;
                WriteCache(source.Id, url, json);
                status.Ok = true;
                status.Count = entries.Count;
                status.FetchedAt = data.FetchedAt;
                return data;
            }

            // ③ 失败 → 过期磁盘缓存兜底
            CatalogSourceData cached = ReadCache(source.Id);
            if (cached != null)
            {
                lock (Lock) Memory[source.Id] = cached;
                status.Ok = true;
                status.FromCache = true;
                status.Error = err;
                status.Count = cached.Entries.Count;
                status.FetchedAt = cached.FetchedAt;
                return cached;
            }
            status.Ok = false;
            status.Error = err;
            return null;
        }

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

        // ==================== 各来源 schema 解析 ====================

        /// <summary>
        /// 自适应解析一个来源的 JSON。支持四种形态（按顶层键/条目键探测）：
        /// ① 官方快照 {plugins:[{name,owner,url,npm,description:{zh,en},category,stars,...}]}
        /// ② 精选快照 {entries:[{full_name,description,stargazers_count,pushed_at,category,category_zh}]}
        /// ③ GitHub 搜索 {items:[{full_name,html_url,description,stargazers_count,pushed_at}]}
        /// ④ 旧接口规范 {plugins:[{name,pkg,repo,desc,dshBundle,minHost,...}]} 或根数组（自定义源）
        /// </summary>
        public static List<CatalogEntry> ParseSourceJson(string json, MarketSourceKind kind, out string error)
        {
            error = "";
            if (string.IsNullOrWhiteSpace(json)) { error = "空数据"; return null; }
            try
            {
                using (var doc = JsonDocument.Parse(json))
                {
                    var root = doc.RootElement;
                    if (root.ValueKind == JsonValueKind.Array)
                        return ParseSpecEntries(root, "自定义源");

                    if (root.ValueKind != JsonValueKind.Object) { error = "数据形态无法识别"; return null; }

                    if (root.TryGetProperty("entries", out var entriesEl) &&
                        entriesEl.ValueKind == JsonValueKind.Array)
                        return ParseCurated(entriesEl);                       // ② 精选快照

                    if (root.TryGetProperty("items", out var itemsEl) &&
                        itemsEl.ValueKind == JsonValueKind.Array)
                        return ParseGithubItems(itemsEl);                     // ③ GitHub 搜索

                    if (root.TryGetProperty("plugins", out var pluginsEl) &&
                        pluginsEl.ValueKind == JsonValueKind.Array)
                    {
                        // 探测条目形态：含 npm/owner → 官方快照；含 pkg/dshBundle/minHost → 旧规范
                        bool looksOfficial = false, looksSpec = false;
                        foreach (JsonElement e in pluginsEl.EnumerateArray())
                        {
                            if (e.ValueKind != JsonValueKind.Object) continue;
                            looksOfficial |= e.TryGetProperty("npm", out _) || e.TryGetProperty("owner", out _);
                            looksSpec |= e.TryGetProperty("pkg", out _) || e.TryGetProperty("dshBundle", out _) ||
                                         e.TryGetProperty("minHost", out _);
                            if (looksOfficial || looksSpec) break;
                        }
                        // 官方快照的中文分类标签进动态映射（分类中文显示用）
                        if (looksOfficial && root.TryGetProperty("categories", out var catsEl) &&
                            catsEl.ValueKind == JsonValueKind.Object)
                            ExtractCategoryLabels(catsEl);
                        if (looksOfficial) return ParseOfficial(pluginsEl, Str(root, "updated"));
                        return ParseSpecEntries(pluginsEl, "自定义源");        // ④ 旧规范
                    }

                    error = "未找到插件数组字段";
                    return null;
                }
            }
            catch (JsonException)
            {
                error = "JSON 解析失败";
                return null;
            }
            catch (Exception ex)
            {
                error = "解析异常：" + ex.Message;
                return null;
            }
        }

        /// <summary>① 官方全量快照（plugins.json）。</summary>
        private static List<CatalogEntry> ParseOfficial(JsonElement arr, string sourceUpdated)
        {
            var list = new List<CatalogEntry>();
            foreach (JsonElement e in arr.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object) continue;
                string name = Str(e, "name");
                string url = Str(e, "url");
                string repo = RepoFromUrl(url);
                if (repo.Length == 0 && name.Length > 0) repo = name;   // 兜底：包名即仓库名
                string descZh = "", descEn = "";
                if (e.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.Object)
                {
                    descZh = Str(d, "zh");
                    descEn = Str(d, "en");
                }
                else if (d.ValueKind == JsonValueKind.String)
                {
                    descEn = d.GetString() ?? "";
                }
                string npm = Str(e, "npm").Trim();
                string install = Str(e, "install");
                var entry = new CatalogEntry
                {
                    Name = name.Length > 0 ? name : repo,
                    Pkg = npm,
                    Repo = repo,
                    Desc = descZh,
                    DescEn = descEn,
                    Category = Str(e, "category"),
                    CategoryLabel = CategoryLabel(Str(e, "category")),
                    Stars = Num(e, "stars"),
                    Downloads = Num(e, "downloads"),
                    UpdatedAt = sourceUpdated ?? "",
                    Verified = true,                       // awesome 项目人工审核收录
                    Install = install,
                    DshBundle = npm.Length > 0 || install.Trim().Length > 0,
                    SourceId = "official",
                    SourceName = "官方全量"
                };
                if (entry.Name.Length == 0 && entry.Repo.Length == 0) continue;
                list.Add(entry);
            }
            return list;
        }

        /// <summary>② 精选快照（market.json entries）。</summary>
        private static List<CatalogEntry> ParseCurated(JsonElement arr)
        {
            var list = new List<CatalogEntry>();
            foreach (JsonElement e in arr.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object) continue;
                string repo = Str(e, "full_name").Trim().TrimStart('/');
                if (repo.Length == 0) continue;
                string cat = Str(e, "category");
                string catZh = Str(e, "category_zh");
                list.Add(new CatalogEntry
                {
                    Name = repo,
                    Repo = repo,
                    Desc = Str(e, "description"),
                    Category = cat,
                    CategoryLabel = catZh.Length > 0 ? catZh : CategoryLabel(cat),
                    Stars = Num(e, "stargazers_count"),
                    UpdatedAt = Str(e, "pushed_at"),
                    Verified = true,                       // 精选池（人工复核）
                    DshBundle = true,                      // market.json 即 dsh.bundle 精选文件
                    SourceId = "curated",
                    SourceName = "GitHub精选"
                });
            }
            return list;
        }

        /// <summary>③ GitHub 实时搜索（items）。</summary>
        private static List<CatalogEntry> ParseGithubItems(JsonElement arr)
        {
            var list = new List<CatalogEntry>();
            foreach (JsonElement e in arr.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object) continue;
                string repo = Str(e, "full_name").Trim().TrimStart('/');
                if (repo.Length == 0) continue;
                list.Add(new CatalogEntry
                {
                    Name = repo,
                    Repo = repo,
                    Desc = Str(e, "description"),
                    Stars = Num(e, "stargazers_count"),
                    UpdatedAt = Str(e, "pushed_at"),
                    Verified = false,
                    Unverified = true,                     // 未经人工审核，安装前需确认
                    DshBundle = true,                      // 目标形似插件仓库，bundle 与否由 dsh 装配时判定
                    SourceId = "github-live",
                    SourceName = "GitHub实时"
                });
            }
            return list;
        }

        /// <summary>④ 旧接口规范条目（自定义源兼容）。</summary>
        private static List<CatalogEntry> ParseSpecEntries(JsonElement arr, string sourceName)
        {
            var list = new List<CatalogEntry>();
            foreach (JsonElement e in arr.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object) continue;
                string pkg = Str(e, "pkg");
                string repo = Str(e, "repo").Trim().TrimStart('/');
                if (pkg.Length == 0 && repo.Length == 0) continue;
                var entry = new CatalogEntry
                {
                    Name = Str(e, "name").Length > 0 ? Str(e, "name") : (pkg.Length > 0 ? pkg : repo),
                    Pkg = pkg,
                    Repo = repo,
                    Desc = Str(e, "desc"),
                    DescEn = Str(e, "descEn"),
                    Category = Str(e, "category"),
                    CategoryLabel = CategoryLabel(Str(e, "category")),
                    Stars = Num(e, "stars"),
                    UpdatedAt = Str(e, "updatedAt"),
                    Verified = Bool(e, "verified"),
                    Install = Str(e, "install"),
                    DshBundle = Bool(e, "dshBundle"),
                    MinHost = Str(e, "minHost"),
                    Profile = Str(e, "profile"),
                    SourceId = "custom",
                    SourceName = sourceName
                };
                if (e.TryGetProperty("tags", out var tagsEl) && tagsEl.ValueKind == JsonValueKind.Array)
                    foreach (var t in tagsEl.EnumerateArray())
                        if (t.ValueKind == JsonValueKind.String) entry.Tags.Add(t.GetString() ?? "");
                if (e.TryGetProperty("minHost", out _) && !e.TryGetProperty("dshBundle", out _))
                    entry.DshBundle = entry.InstallTarget.Length > 0;
                list.Add(entry);
            }
            return list;
        }

        private static string Str(JsonElement e, string name)
        {
            if (e.ValueKind != JsonValueKind.Object) return "";
            if (e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String)
                return v.GetString() ?? "";
            return "";
        }

        private static int Num(JsonElement e, string name)
        {
            if (e.ValueKind != JsonValueKind.Object) return 0;
            if (e.TryGetProperty(name, out var v))
            {
                if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i)) return i;
                if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out int s)) return s;
            }
            return 0;
        }

        private static bool Bool(JsonElement e, string name)
        {
            return e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) &&
                   v.ValueKind == JsonValueKind.True;
        }

        /// <summary>https://github.com/owner/repo(.git)/... → owner/repo。</summary>
        public static string RepoFromUrl(string url)
        {
            string u = (url ?? "").Trim();
            if (u.Length == 0) return "";
            int idx = u.IndexOf("github.com/", StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return "";
            string rest = u.Substring(idx + "github.com/".Length).TrimEnd('/');
            if (rest.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
                rest = rest.Substring(0, rest.Length - 4);
            string[] seg = rest.Split('/');
            if (seg.Length < 2) return "";
            string owner = seg[0].Trim(), name = seg[1].Trim();
            if (owner.Length == 0 || name.Length == 0) return "";
            return owner + "/" + name;
        }

        /// <summary>官方快照顶层 categories{code:{en,zh}} → 动态中文标签映射。</summary>
        public static void ExtractCategoryLabels(JsonElement catsObj)
        {
            lock (Lock)
            {
                foreach (var kv in catsObj.EnumerateObject())
                {
                    if (kv.Value.ValueKind != JsonValueKind.Object) continue;
                    if (kv.Value.TryGetProperty("zh", out var zh) && zh.ValueKind == JsonValueKind.String)
                    {
                        string label = zh.GetString() ?? "";
                        if (label.Length > 0) DynamicCategoryLabels[kv.Name] = label;
                    }
                }
            }
        }

        // ==================== 分类中文显示 ====================

        /// <summary>内置分类中文映射（各来源已知代码 + 旧规范代码；未知的原样返回）。</summary>
        private static readonly Dictionary<string, string> BuiltinCategoryLabels =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["tool"] = "工具", ["tools"] = "工具",
                ["ui"] = "界面", ["dev"] = "开发",
                ["agent"] = "智能体", ["agents"] = "智能体",
                ["data"] = "数据", ["session"] = "会话",
                ["other"] = "其他", ["usage"] = "用量与计费",
                ["theme"] = "主题外观", ["model"] = "模型接入",
                ["identity"] = "身份与通信", ["memory"] = "记忆",
                ["browser"] = "浏览器", ["vision"] = "视觉",
                ["voice"] = "语音", ["docs"] = "文档",
                ["skill"] = "技能", ["workflow"] = "工作流",
                ["git"] = "Git", ["security"] = "安全",
                ["remote"] = "远程", ["market"] = "市场",
                ["fun"] = "趣味", ["agi"] = "AGI 探索",
                ["developer-tools"] = "开发者工具",
                ["ui-experience"] = "界面体验",
                ["web-browser"] = "网页浏览",
                ["knowledge-research"] = "知识研究",
                ["agents-workflows"] = "智能体与工作流",
                ["media-vision"] = "媒体与视觉",
                ["integrations-sharing"] = "集成与分享",
                ["ecosystem-resources"] = "生态资源",
                ["utilities"] = "实用工具"
            };

        /// <summary>分类中文标签：来源动态映射 → 内置映射 → 原代码（绝不编造）。</summary>
        public static string CategoryLabel(string code)
        {
            string c = (code ?? "").Trim();
            if (c.Length == 0) return "未分类";
            lock (Lock)
            {
                if (DynamicCategoryLabels.TryGetValue(c, out string dyn) && dyn.Length > 0) return dyn;
            }
            return BuiltinCategoryLabels.TryGetValue(c, out string label) ? label : c;
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

        // ==================== 磁盘缓存（按来源分文件，存原始 JSON） ====================

        private sealed class CacheFile
        {
            [JsonPropertyName("fetchedAt")]
            public string FetchedAt { get; set; } = "";

            [JsonPropertyName("url")]
            public string Url { get; set; } = "";

            [JsonPropertyName("raw")]
            public string Raw { get; set; } = "";
        }

        private static CatalogSourceData ReadCache(string sourceId)
        {
            try
            {
                string path = CacheFilePath(sourceId);
                if (!File.Exists(path)) return null;
                var cache = JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(path, Encoding.UTF8));
                if (cache == null || string.IsNullOrEmpty(cache.Raw)) return null;
                DateTime t = DateTime.TryParse(cache.FetchedAt, null, DateTimeStyles.RoundtripKind, out var parsed)
                    ? parsed : DateTime.MinValue;
                List<CatalogEntry> entries = ParseSourceJson(cache.Raw, MarketSourceKind.JsonCatalog, out _);
                if (entries == null || entries.Count == 0) return null;
                return new CatalogSourceData { FetchedAt = t, Entries = entries };
            }
            catch
            {
                return null;
            }
        }

        private static void WriteCache(string sourceId, string url, string raw)
        {
            try
            {
                Directory.CreateDirectory(CacheDir);
                var cache = new CacheFile
                {
                    FetchedAt = DateTime.UtcNow.ToString("O"),
                    Url = url ?? "",
                    Raw = raw ?? ""
                };
                string path = CacheFilePath(sourceId);
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(cache), new UTF8Encoding(false));
                File.Move(tmp, path, overwrite: true);
            }
            catch
            {
                // 缓存写盘失败不影响本次使用
            }
        }
    }
}
