// ============================================================================
//  用量数据模型（重构 2.0 / P2）
//
//  这些类型同时是"内存模型"与"档案里的持久化结构"——原型分支里各写了一套
//  （SessionUsage/ModelUsage 与 UsageBackupSession/UsageBackupModel 逐字段镜像 +
//  手工双向拷贝），本次合并时直接合成一套，少一层拷贝也少一处漂移源。
//
//  口径与官方 @deepseek-ai/dsh-token-meter 对齐：四分桶 = 未缓存输入 / 缓存读 /
//  缓存写 / 输出；按天分桶用本地日期（yyyy-MM-dd）。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace DshController.Core.Usage
{
    /// <summary>官方 token-meter 四分桶。</summary>
    public sealed class TokenBuckets
    {
        [JsonPropertyName("uncachedInput")]
        public long UncachedInput { get; set; }

        [JsonPropertyName("cacheRead")]
        public long CacheRead { get; set; }

        [JsonPropertyName("cacheWrite")]
        public long CacheWrite { get; set; }

        [JsonPropertyName("output")]
        public long Output { get; set; }

        [JsonIgnore]
        public long Total => UncachedInput + CacheRead + CacheWrite + Output;

        /// <summary>输入侧缓存命中率 cacheRead /(uncachedInput + cacheRead)；无输入返回 -1。</summary>
        [JsonIgnore]
        public double CacheHitRate
        {
            get
            {
                long denom = UncachedInput + CacheRead;
                return denom > 0 ? (double)CacheRead / denom : -1;
            }
        }

        public void Add(TokenBuckets o)
        {
            if (o == null) return;
            UncachedInput += o.UncachedInput;
            CacheRead += o.CacheRead;
            CacheWrite += o.CacheWrite;
            Output += o.Output;
        }

        public TokenBuckets Clone() => new TokenBuckets
        {
            UncachedInput = UncachedInput,
            CacheRead = CacheRead,
            CacheWrite = CacheWrite,
            Output = Output
        };
    }

    /// <summary>会话日志里一条去重后的请求样本（同一 (turn,step) 保留最后一条）。</summary>
    public sealed class UsageSample
    {
        public string Provider { get; set; } = "";
        public string Model { get; set; } = "";
        public long TimeMs { get; set; }
        public long UncachedInput { get; set; }
        public long CacheRead { get; set; }
        public long CacheWrite { get; set; }
        public long Output { get; set; }
    }

    /// <summary>单会话用量（来自 storages/session_projcache.json，权威总账）。</summary>
    public sealed class UsageSessionStat
    {
        [JsonPropertyName("sessionId")]
        public string SessionId { get; set; } = "";

        [JsonPropertyName("title")]
        public string Title { get; set; } = "";

        [JsonPropertyName("createdAtMs")]
        public long CreatedAtMs { get; set; }

        [JsonPropertyName("cwd")]
        public string Cwd { get; set; } = "";

        [JsonPropertyName("turns")]
        public long Turns { get; set; }

        [JsonPropertyName("totals")]
        public TokenBuckets Totals { get; set; } = new TokenBuckets();
    }

    /// <summary>按 (provider, model) 聚合的用量（来自会话日志解析）。</summary>
    public sealed class UsageModelStat
    {
        [JsonPropertyName("provider")]
        public string Provider { get; set; } = "";

        [JsonPropertyName("model")]
        public string Model { get; set; } = "";

        [JsonPropertyName("requests")]
        public int Requests { get; set; }

        [JsonPropertyName("firstMs")]
        public long FirstMs { get; set; }

        [JsonPropertyName("lastMs")]
        public long LastMs { get; set; }

        [JsonPropertyName("totals")]
        public TokenBuckets Totals { get; set; } = new TokenBuckets();

        /// <summary>按本地日期（yyyy-MM-dd）分桶的每日用量。</summary>
        [JsonPropertyName("daily")]
        public Dictionary<string, TokenBuckets> Daily { get; set; } =
            new Dictionary<string, TokenBuckets>(StringComparer.Ordinal);

        /// <summary>按本地日期分桶的每日请求数。</summary>
        [JsonPropertyName("dailyRequests")]
        public Dictionary<string, long> DailyRequests { get; set; } =
            new Dictionary<string, long>(StringComparer.Ordinal);

        [JsonIgnore]
        public string DisplayName => string.IsNullOrEmpty(Model) ? "(未知模型)" : Model;

        [JsonIgnore]
        public string FullName => string.IsNullOrEmpty(Provider) ? DisplayName : Provider + "/" + DisplayName;
    }

    /// <summary>usage 分面的完整数据（即档案里持久化的那一份）。</summary>
    public sealed class UsageFacetData
    {
        [JsonPropertyName("home")]
        public string Home { get; set; } = "";

        /// <summary>projcache 口径的总量（权威，不依赖会话日志解析是否成功）。</summary>
        [JsonPropertyName("totals")]
        public TokenBuckets Totals { get; set; } = new TokenBuckets();

        [JsonPropertyName("sessionCount")]
        public int SessionCount { get; set; }

        [JsonPropertyName("requestCount")]
        public int RequestCount { get; set; }

        /// <summary>会话日志是否完整解析（false 时按模型/按天数据不完整，界面须如实标注）。</summary>
        [JsonPropertyName("modelsComplete")]
        public bool ModelsComplete { get; set; }

        [JsonPropertyName("modelScanError")]
        public string ModelScanError { get; set; } = "";

        [JsonPropertyName("scannedFiles")]
        public int ScannedFiles { get; set; }

        [JsonPropertyName("models")]
        public List<UsageModelStat> Models { get; set; } = new List<UsageModelStat>();

        /// <summary>会话明细（按创建时间倒序，最多保留 MaxSessions 条，避免档案无限膨胀）。</summary>
        [JsonPropertyName("sessions")]
        public List<UsageSessionStat> Sessions { get; set; } = new List<UsageSessionStat>();

        /// <summary>会话明细的保留上限。</summary>
        public const int MaxSessions = 500;
    }
}
