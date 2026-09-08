// ============================================================================
//  UsageParser.Telemetry — DSH 会话日志的 TTFT 折叠（纯解析，无 IO）
// ============================================================================

using System;
using System.Collections.Generic;
using System.Text.Json;

namespace DshController.Core.Usage
{
    public static partial class UsageParser
    {
        private sealed class StepTiming
        {
            public long StartedAt;
            public long FirstTokenAt;
        }

        /// <summary>
        /// 在一次解压结果中提取原有 token 样本与 DSH 官方 sessionStats 同口径的 TTFT。
        /// 未闭合的步骤不产生样本，避免以 0 伪造延迟。
        /// </summary>
        public static UsageSessionScan ParseSessionScan(string jsonl)
        {
            var scan = new UsageSessionScan { Samples = ParseSessionSamples(jsonl) };
            if (string.IsNullOrEmpty(jsonl)) return scan;

            var open = new Dictionary<string, StepTiming>(StringComparer.Ordinal);
            foreach (string raw in jsonl.Split('\n'))
            {
                string line = raw.Trim('\r', ' ', '\t');
                if (line.Length == 0) continue;
                try
                {
                    using (JsonDocument doc = JsonDocument.Parse(line))
                        FoldTiming(doc.RootElement, scan, open);
                }
                catch (JsonException)
                {
                    // 理由: 活跃会话的最后一行可能尚未写完整，不能影响其它已提交步骤。
                }
            }
            return scan;
        }

        private static void FoldTiming(JsonElement root, UsageSessionScan scan,
            Dictionary<string, StepTiming> open)
        {
            if (root.ValueKind != JsonValueKind.Object) return;
            string type = S(root, "type");
            if (type == "session")
            {
                scan.SessionId = S(root, "id");
                return;
            }
            if (!root.TryGetProperty("data", out JsonElement data)) return;
            long turn = L(data, "turn"), step = L(data, "step");
            string key = turn + "/" + step;
            long time = L(root, "time");

            if (type == "step/start")
            {
                if (time > 0) open[key] = new StepTiming { StartedAt = time };
                return;
            }
            if (!open.TryGetValue(key, out StepTiming timing)) return;

            if (type == "assistant/chunk")
            {
                if (timing.FirstTokenAt == 0 && data.TryGetProperty("chunk", out JsonElement chunk) &&
                    IsNonEmptyDelta(chunk) && time > 0)
                    timing.FirstTokenAt = time;
                return;
            }
            if (type == "assistant/attempt")
            {
                if (timing.FirstTokenAt == 0 && data.TryGetProperty("stream", out JsonElement stream))
                    timing.FirstTokenAt = FirstTokenInStream(stream);
                return;
            }
            if (type != "assistant/message") return;

            if (timing.FirstTokenAt == 0 && data.TryGetProperty("stream", out JsonElement messageStream))
                timing.FirstTokenAt = FirstTokenInStream(messageStream);
            if (timing.StartedAt > 0 && timing.FirstTokenAt >= timing.StartedAt && time >= timing.FirstTokenAt)
            {
                long ttft = timing.FirstTokenAt - timing.StartedAt;
                long model = time - timing.StartedAt;
                scan.Latency.Add(ttft, model);
                string day = DayKey(time);
                if (!string.IsNullOrEmpty(day))
                {
                    if (!scan.DailyLatency.TryGetValue(day, out UsageLatencyStats daily))
                    {
                        daily = new UsageLatencyStats();
                        scan.DailyLatency[day] = daily;
                    }
                    daily.Add(ttft, model);
                }
            }
            open.Remove(key);
        }

        private static long FirstTokenInStream(JsonElement stream)
        {
            if (stream.ValueKind != JsonValueKind.Array) return 0;
            foreach (JsonElement member in stream.EnumerateArray())
            {
                if (member.ValueKind != JsonValueKind.Object) continue;
                JsonElement chunk = member;
                if (member.TryGetProperty("chunk", out JsonElement nested)) chunk = nested;
                long time = L(member, "time");
                if (time > 0 && IsNonEmptyDelta(chunk)) return time;
            }
            return 0;
        }

        private static bool IsNonEmptyDelta(JsonElement chunk)
        {
            string type = S(chunk, "type");
            if (type == "text-delta" || type == "reasoning-delta") return S(chunk, "text").Length > 0;
            if (type == "tool-call-delta") return S(chunk, "argumentsDelta").Length > 0 || S(chunk, "name").Length > 0;
            return false;
        }
    }
}
