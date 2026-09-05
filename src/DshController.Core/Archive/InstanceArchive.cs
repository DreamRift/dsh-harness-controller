// ============================================================================
//  InstanceArchive — 实例档案（重构 2.0 / P1 核心数据模型）
//
//  一个实例 = 一份档案文件。档案的三条硬规矩：
//    1. **不随实例删除而消失**——删除只写 retiredAt，文件永久保留，历史可查；
//    2. **失败不覆盖成功**——采集失败/为空只更新状态与错误，data 与 lastGoodAt 保留；
//    3. **分面各自计时**——每个 facet 自带 collectedAt，刷新间隔互不牵连。
//  data 落盘为原始 JSON（JsonElement），存储层因此与具体分面解耦：
//  新增分面只需加采集器与 DTO，不用改档案模型或仓库。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DshController.Core.Archive
{
    /// <summary>一次采集的结果快照。</summary>
    public sealed class FacetSnapshot
    {
        [JsonPropertyName("status")]
        public string Status { get; set; } = FacetStatus.Never;

        /// <summary>最近一次采集尝试的时间（无论成败）。</summary>
        [JsonPropertyName("collectedAt")]
        public DateTime? CollectedAt { get; set; }

        /// <summary>最近一次拿到有效数据的时间（失败时保留旧值）。</summary>
        [JsonPropertyName("lastGoodAt")]
        public DateTime? LastGoodAt { get; set; }

        [JsonPropertyName("durationMs")]
        public int DurationMs { get; set; }

        /// <summary>数据来源标注（如 npm-package-json / dsh --version）。</summary>
        [JsonPropertyName("source")]
        public string Source { get; set; } = "";

        [JsonPropertyName("error")]
        public string Error { get; set; } = "";

        [JsonPropertyName("skipReason")]
        public string SkipReason { get; set; } = "";

        /// <summary>分面数据（原始 JSON；用 TryGetData&lt;T&gt; 取回强类型）。</summary>
        [JsonPropertyName("data")]
        public JsonElement? Data { get; set; }

        [JsonIgnore]
        public bool HasData => Data.HasValue && Data.Value.ValueKind != JsonValueKind.Null &&
                               Data.Value.ValueKind != JsonValueKind.Undefined;

        /// <summary>把 data 还原成强类型；缺失或解析失败返回 false。</summary>
        public bool TryGetData<T>(out T value)
        {
            value = default;
            if (!HasData) return false;
            try
            {
                value = Data.Value.Deserialize<T>(Storage.JsonStore.ReadOptions);
                return value != null;
            }
            catch
            {
                return false;   // 理由: 旧版本写入的分面结构可能与当前 DTO 不符，按"无数据"处理
            }
        }

        public FacetSnapshot Clone()
        {
            return new FacetSnapshot
            {
                Status = Status,
                CollectedAt = CollectedAt,
                LastGoodAt = LastGoodAt,
                DurationMs = DurationMs,
                Source = Source,
                Error = Error,
                SkipReason = SkipReason,
                Data = Data
            };
        }
    }

    /// <summary>一代实例定义（同一个 id 被删除后重建即追加一代）。</summary>
    public sealed class ArchiveEpoch
    {
        [JsonPropertyName("from")]
        public DateTime From { get; set; }

        [JsonPropertyName("to")]
        public DateTime? To { get; set; }

        /// <summary>该代的实例定义快照（删除后仍可看到当时的端口/HOME/发行版）。</summary>
        [JsonPropertyName("def")]
        public InstanceDef Def { get; set; }
    }

    public sealed class InstanceArchive
    {
        public const int CurrentSchema = 1;

        [JsonPropertyName("schema")]
        public int Schema { get; set; } = CurrentSchema;

        [JsonPropertyName("archiveId")]
        public string ArchiveId { get; set; } = "";

        /// <summary>展示名（取最近一代的实例名，供档案列表页直接用）。</summary>
        [JsonPropertyName("displayName")]
        public string DisplayName { get; set; } = "";

        [JsonPropertyName("runtime")]
        public string Runtime { get; set; } = "windows";

        [JsonPropertyName("createdAt")]
        public DateTime CreatedAt { get; set; }

        /// <summary>非空 = 实例已从清单中删除；档案本身继续保留。</summary>
        [JsonPropertyName("retiredAt")]
        public DateTime? RetiredAt { get; set; }

        [JsonPropertyName("epochs")]
        public List<ArchiveEpoch> Epochs { get; set; } = new List<ArchiveEpoch>();

        [JsonPropertyName("facets")]
        public Dictionary<string, FacetSnapshot> Facets { get; set; } =
            new Dictionary<string, FacetSnapshot>(StringComparer.OrdinalIgnoreCase);

        [JsonIgnore]
        public bool IsRetired => RetiredAt.HasValue;

        /// <summary>当前（未结束的）一代；没有则取最后一代。</summary>
        [JsonIgnore]
        public ArchiveEpoch CurrentEpoch
        {
            get
            {
                for (int i = Epochs.Count - 1; i >= 0; i--)
                    if (!Epochs[i].To.HasValue) return Epochs[i];
                return Epochs.Count > 0 ? Epochs[Epochs.Count - 1] : null;
            }
        }

        public FacetSnapshot Facet(string name)
        {
            if (name == null) return new FacetSnapshot();
            if (Facets.TryGetValue(name, out FacetSnapshot s) && s != null) return s;
            return new FacetSnapshot();
        }

        /// <summary>反序列化后的规范化：字典换成忽略大小写、补默认值。</summary>
        public InstanceArchive Normalize()
        {
            if (Epochs == null) Epochs = new List<ArchiveEpoch>();
            var normalized = new Dictionary<string, FacetSnapshot>(StringComparer.OrdinalIgnoreCase);
            if (Facets != null)
            {
                foreach (KeyValuePair<string, FacetSnapshot> kv in Facets)
                    if (kv.Value != null) normalized[kv.Key] = kv.Value;
            }
            Facets = normalized;
            if (string.IsNullOrEmpty(Runtime)) Runtime = "windows";
            if (CreatedAt == default) CreatedAt = DateTime.UtcNow;
            return this;
        }

        public static InstanceArchive CreateFor(InstanceDef def, DateTime nowUtc)
        {
            var archive = new InstanceArchive
            {
                ArchiveId = def?.Id ?? "",
                DisplayName = string.IsNullOrWhiteSpace(def?.Name) ? (def?.Id ?? "") : def.Name,
                Runtime = def != null && def.IsWsl ? "wsl" : "windows",
                CreatedAt = nowUtc
            };
            archive.Epochs.Add(new ArchiveEpoch { From = nowUtc, Def = def });
            return archive;
        }
    }
}
