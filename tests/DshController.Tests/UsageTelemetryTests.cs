// ============================================================================
//  UsageTelemetryTests — DSH step 时序的 TTFT 与档案范围统计
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using DshController.Core.Usage;
using Xunit;

namespace DshController.Tests
{
    public class UsageTelemetryTests
    {
        private static string Event(string type, long time, string data) =>
            "{\"type\":\"" + type + "\",\"time\":" + time + ",\"data\":" + data + "}";

        [Fact]
        public void 完整步骤折叠TTFT并按完成日归档()
        {
            const long start = 1755676800000;
            string jsonl = string.Join("\n",
                "{\"type\":\"session\",\"id\":\"s1\"}",
                Event("step/start", start, "{\"turn\":1,\"step\":1}"),
                Event("assistant/chunk", start + 120, "{\"turn\":1,\"step\":1,\"chunk\":{\"type\":\"text-delta\",\"text\":\"\"}}"),
                Event("assistant/chunk", start + 350, "{\"turn\":1,\"step\":1,\"chunk\":{\"type\":\"text-delta\",\"text\":\"hi\"}}"),
                Event("assistant/message", start + 900, "{\"turn\":1,\"step\":1}"));

            UsageSessionScan scan = UsageParser.ParseSessionScan(jsonl);

            Assert.Equal("s1", scan.SessionId);
            Assert.Equal(1, scan.Latency.Samples);
            Assert.Equal(350, scan.Latency.TtftMs);
            Assert.Equal(900, scan.Latency.ModelMs);
            Assert.Single(scan.DailyLatency);
            Assert.Equal(1, scan.DailyLatency[UsageParser.DayKey(start + 900)].Samples);
        }

        [Fact]
        public void 不完整或倒序步骤不伪造延迟()
        {
            const long start = 1755676800000;
            string jsonl = string.Join("\n",
                Event("step/start", start, "{\"turn\":1,\"step\":1}"),
                Event("assistant/message", start + 20, "{\"turn\":1,\"step\":1}"),
                Event("step/start", start + 100, "{\"turn\":2,\"step\":1}"),
                Event("assistant/chunk", start + 50, "{\"turn\":2,\"step\":1,\"chunk\":{\"type\":\"text-delta\",\"text\":\"late\"}}"),
                Event("assistant/message", start + 200, "{\"turn\":2,\"step\":1}"));

            UsageSessionScan scan = UsageParser.ParseSessionScan(jsonl);

            Assert.Equal(0, scan.Latency.Samples);
            Assert.Empty(scan.DailyLatency);
        }

        [Fact]
        public void 范围查询只合并范围内延迟()
        {
            DateTime today = DateTime.Now.Date;
            string oldDay = today.AddDays(-8).ToString("yyyy-MM-dd");
            string todayKey = today.ToString("yyyy-MM-dd");
            var data = new UsageFacetData
            {
                Totals = new TokenBuckets { Output = 999 },
                ModelsComplete = true,
                DailyLatency = new Dictionary<string, UsageLatencyStats>
                {
                    [oldDay] = new UsageLatencyStats { Samples = 1, TtftMs = 800 },
                    [todayKey] = new UsageLatencyStats { Samples = 2, TtftMs = 600 }
                }
            };

            UsageSummary summary = UsageQuery.Summarize(new[] { data }, today.AddDays(-6), today);

            Assert.Equal(2, summary.Latency.Samples);
            Assert.Equal(300, summary.Latency.AverageTtftMs);
            Assert.Single(summary.DailyLatency);
        }

        [Fact]
        public void WSL帧与直接日志得到相同时序样本()
        {
            const long start = 1755676800000;
            string jsonl = string.Join("\n",
                "{\"type\":\"session\",\"id\":\"wsl-s\"}",
                Event("step/start", start, "{\"turn\":1,\"step\":1}"),
                Event("assistant/chunk", start + 250, "{\"turn\":1,\"step\":1,\"chunk\":{\"type\":\"text-delta\",\"text\":\"x\"}}"),
                Event("assistant/message", start + 500, "{\"turn\":1,\"step\":1}"));
            string frames = "@@DSHU 1 1 /root/s/session.jsonl.zstd\n" +
                Convert.ToBase64String(UsageScanner.CompressForTest(jsonl)) + "\n@@DSHEND\n@@DSHDONE\n";

            UsageSessionScan scan = UsageScanner.ParseWslScanFrames(frames, "telemetry-" + Guid.NewGuid()).Single();

            Assert.Equal("wsl-s", scan.SessionId);
            Assert.Equal(1, scan.Latency.Samples);
            Assert.Equal(250, scan.Latency.TtftMs);
        }
    }
}
