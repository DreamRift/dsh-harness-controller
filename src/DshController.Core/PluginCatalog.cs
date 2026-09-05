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
using DshController.Core.Storage;

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

    public static partial class PluginCatalog
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

        public static string CacheDir => AppPaths.PluginCacheDir;

        private static string CacheFilePath(string sourceId) =>
            Path.Combine(CacheDir, "catalog-" + SanitizeId(sourceId) + ".json");

        private static string SanitizeId(string id)
        {
            string safe = string.Join("_", (id ?? "src").Split(
                Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
            return safe.Length == 0 ? "src" : safe;
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
    }
}
