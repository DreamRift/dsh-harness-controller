// ============================================================================
//  PluginCatalog · 拉取（来源选择+加载）（partial 分部；纯搬移，逐字一致）
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
    }
}
