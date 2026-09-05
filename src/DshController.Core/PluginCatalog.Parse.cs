// ============================================================================
//  PluginCatalog · 各来源 schema 解析（partial 分部；纯搬移，逐字一致）
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
    }
}
