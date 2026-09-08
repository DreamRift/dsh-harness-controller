// ============================================================================
//  UsageParser — 两种源数据的解析（重构 2.0 / P2，移植自原型分支并保留其口径）
//
//  源 1：<HOME>/storages/session_projcache.json —— 官方 token-meter 落盘的按会话
//        汇总（权威总账）。黑盒容错解析：缺行/缺字段按 0/空，结构不认识按空表。
//  源 2：<HOME>/sessions/**/session.jsonl.zstd —— 会话事件流，逐请求 usage。
//        规则（与官方语义对齐，原型分支已用 fixture 验证过，此处原样保留）：
//          · usage 出现在 assistant/chunk(data.chunk.type=="usage") 与 assistant/message(data.usage)；
//          · 同一 (turn,step) 的重复样本是"替换"而非累加；
//          · 模型名取流中最近一次 request/header 的 data.header.config.{provider,model}；
//          · 未缓存输入优先 uncachedInputTokens，缺失回退 inputTokens（OpenAI 兼容
//            渠道只有 input/output，缓存桶记 0）。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace DshController.Core.Usage
{
    public static partial class UsageParser
    {
        /// <summary>解析 session_projcache.json；返回 false 表示文件损坏（error 带原因）。</summary>
        public static bool TryParseProjCache(string json, out List<UsageSessionStat> sessions, out string error)
        {
            sessions = new List<UsageSessionStat>();
            error = "";
            try
            {
                using (JsonDocument doc = JsonDocument.Parse(json))
                {
                    if (!doc.RootElement.TryGetProperty("tables", out JsonElement tables) ||
                        !tables.TryGetProperty("sessions", out JsonElement sessionsEl) ||
                        sessionsEl.ValueKind != JsonValueKind.Object)
                    {
                        return true;   // 结构存在但没有会话 → 空表（不是错误）
                    }
                    foreach (JsonProperty sid in sessionsEl.EnumerateObject())
                    {
                        JsonElement entry = sid.Value;
                        var s = new UsageSessionStat { SessionId = sid.Name };
                        if (entry.TryGetProperty("identity", out JsonElement identity))
                        {
                            s.CreatedAtMs = L(identity, "createdAt");
                            s.Cwd = S(identity, "cwd");
                        }
                        if (entry.TryGetProperty("rows", out JsonElement rows))
                        {
                            if (rows.TryGetProperty("title", out JsonElement title) &&
                                title.TryGetProperty("val", out JsonElement tv))
                                s.Title = tv.ValueKind == JsonValueKind.String ? tv.GetString() ?? "" : "";
                            if (rows.TryGetProperty("sessionStats", out JsonElement stats) &&
                                stats.TryGetProperty("val", out JsonElement sv))
                                s.Turns = L(sv, "turns");
                            if (rows.TryGetProperty("tokenUsage", out JsonElement tu) &&
                                tu.TryGetProperty("val", out JsonElement uv) &&
                                uv.TryGetProperty("totals", out JsonElement totals))
                            {
                                s.Totals.UncachedInput = L(totals, "uncachedInputTokens");
                                s.Totals.CacheRead = L(totals, "cacheReadTokens");
                                s.Totals.CacheWrite = L(totals, "cacheWriteTokens");
                                s.Totals.Output = L(totals, "outputTokens");
                            }
                        }
                        sessions.Add(s);
                    }
                    return true;
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        /// <summary>解析一段解压后的会话 JSONL → 去重样本列表。</summary>
        public static List<UsageSample> ParseSessionSamples(string jsonl)
        {
            var last = new Dictionary<string, UsageSample>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(jsonl)) return new List<UsageSample>();

            string curProvider = "", curModel = "";
            foreach (string rawLine in jsonl.Split('\n'))
            {
                string line = rawLine.Trim('\r', ' ', '\t');
                if (line.Length == 0) continue;
                JsonDocument doc;
                try { doc = JsonDocument.Parse(line); }
                catch { continue; /* 理由: 会话日志可能正被写入，坏行跳过即可 */ }

                using (doc)
                {
                    JsonElement root = doc.RootElement;
                    if (root.ValueKind != JsonValueKind.Object) continue;
                    string type = S(root, "type");
                    if (type.Length == 0) continue;

                    if (type == "request/header")
                    {
                        if (root.TryGetProperty("data", out JsonElement hdata) &&
                            hdata.TryGetProperty("header", out JsonElement header) &&
                            header.TryGetProperty("config", out JsonElement cfg))
                        {
                            curProvider = S(cfg, "provider");
                            curModel = S(cfg, "model");
                        }
                        continue;
                    }

                    JsonElement usage = default;
                    long turn = 0, step = 0;
                    bool have = false;
                    if (type == "assistant/chunk")
                    {
                        if (root.TryGetProperty("data", out JsonElement data) &&
                            data.TryGetProperty("chunk", out JsonElement chunk) &&
                            S(chunk, "type") == "usage" && chunk.TryGetProperty("usage", out JsonElement u))
                        {
                            usage = u;
                            turn = L(data, "turn");
                            step = L(data, "step");
                            have = true;
                        }
                    }
                    else if (type == "assistant/message")
                    {
                        if (root.TryGetProperty("data", out JsonElement data) &&
                            data.TryGetProperty("usage", out JsonElement u))
                        {
                            usage = u;
                            turn = L(data, "turn");
                            step = L(data, "step");
                            have = true;
                        }
                    }
                    if (!have) continue;

                    last[turn + "/" + step] = new UsageSample
                    {
                        Provider = curProvider ?? "",
                        Model = curModel ?? "",
                        TimeMs = L(root, "time"),
                        UncachedInput = LPrefer(usage, "uncachedInputTokens", "inputTokens"),
                        CacheRead = L(usage, "cacheReadTokens"),
                        CacheWrite = L(usage, "cacheWriteTokens"),
                        Output = L(usage, "outputTokens")
                    };
                }
            }
            return last.Values.ToList();
        }

        /// <summary>每会话样本 → 按 (provider, model) 聚合（含按天分桶，本地日期）。</summary>
        public static List<UsageModelStat> AggregateModels(IEnumerable<List<UsageSample>> sessionLists)
        {
            var map = new Dictionary<string, UsageModelStat>(StringComparer.Ordinal);
            foreach (List<UsageSample> samples in sessionLists ?? Enumerable.Empty<List<UsageSample>>())
            {
                if (samples == null) continue;
                foreach (UsageSample sp in samples)
                {
                    string key = (sp.Provider ?? "") + "/" + (sp.Model ?? "");
                    if (!map.TryGetValue(key, out UsageModelStat m))
                    {
                        m = new UsageModelStat { Provider = sp.Provider ?? "", Model = sp.Model ?? "" };
                        map[key] = m;
                    }
                    m.Requests++;
                    m.Totals.UncachedInput += sp.UncachedInput;
                    m.Totals.CacheRead += sp.CacheRead;
                    m.Totals.CacheWrite += sp.CacheWrite;
                    m.Totals.Output += sp.Output;
                    if (sp.TimeMs <= 0) continue;

                    if (m.FirstMs == 0 || sp.TimeMs < m.FirstMs) m.FirstMs = sp.TimeMs;
                    if (sp.TimeMs > m.LastMs) m.LastMs = sp.TimeMs;
                    string day = DayKey(sp.TimeMs);
                    if (!m.Daily.TryGetValue(day, out TokenBuckets db))
                    {
                        db = new TokenBuckets();
                        m.Daily[day] = db;
                    }
                    db.UncachedInput += sp.UncachedInput;
                    db.CacheRead += sp.CacheRead;
                    db.CacheWrite += sp.CacheWrite;
                    db.Output += sp.Output;
                    m.DailyRequests[day] = (m.DailyRequests.TryGetValue(day, out long rc) ? rc : 0) + 1;
                }
            }
            var list = new List<UsageModelStat>(map.Values);
            list.Sort((a, b) => b.Totals.Total.CompareTo(a.Totals.Total));
            return list;
        }

        /// <summary>时间戳 → 本地日期键（yyyy-MM-dd）。</summary>
        public static string DayKey(long unixMs)
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(unixMs).ToLocalTime()
                .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        // ---------------- JSON 安全取值 ----------------

        internal static long L(JsonElement o, string name)
        {
            if (o.ValueKind != JsonValueKind.Object) return 0;
            if (!o.TryGetProperty(name, out JsonElement v) || v.ValueKind != JsonValueKind.Number) return 0;
            return v.TryGetInt64(out long l) ? l : (long)v.GetDouble();
        }

        /// <summary>优先取 prefer（存在且为数字即用，哪怕是 0），否则回退 fallback。</summary>
        internal static long LPrefer(JsonElement o, string prefer, string fallback)
        {
            if (o.ValueKind == JsonValueKind.Object && o.TryGetProperty(prefer, out JsonElement v) &&
                v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long l))
                return l;
            return L(o, fallback);
        }

        internal static string S(JsonElement o, string name)
        {
            if (o.ValueKind != JsonValueKind.Object) return "";
            if (!o.TryGetProperty(name, out JsonElement v) || v.ValueKind != JsonValueKind.String) return "";
            return v.GetString() ?? "";
        }
    }
}
