// ============================================================================
//  PluginCatalog · 磁盘缓存（partial 分部；纯搬移，逐字一致）
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
                // 理由: 写缓存只是目录拉取成功后的尽力而为优化，失败不影响本次已在内存返回的目录数据，仅下次拉取需重新联网。
            }
        }
    }
}
