// ============================================================================
//  用量解析与聚合的离线单测（重构 2.0 / P2）
//
//  口径来自官方 token-meter，原型分支已用 fixture 验证过；合并进主线时把这些
//  断言一并迁到 xUnit：解析规则一旦被改坏，秒级就能发现。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Text;
using DshController.Core.Usage;
using Xunit;

namespace DshController.Tests
{
    public class UsageProjCacheTests
    {
        private const string Sample = @"{
          ""tables"": { ""sessions"": {
            ""sess-1"": {
              ""identity"": { ""createdAt"": 1755676800000, ""cwd"": ""/work/demo"" },
              ""rows"": {
                ""title"": { ""val"": ""重构会话"" },
                ""sessionStats"": { ""val"": { ""turns"": 12, ""steps"": 40 } },
                ""tokenUsage"": { ""val"": { ""totals"": {
                    ""uncachedInputTokens"": 1000, ""cacheReadTokens"": 2000,
                    ""cacheWriteTokens"": 250, ""outputTokens"": 1000 } } }
              }
            },
            ""sess-2"": { ""identity"": { ""createdAt"": 1755763200000 } }
          } } }";

        [Fact]
        public void 解析会话总账()
        {
            Assert.True(UsageParser.TryParseProjCache(Sample, out List<UsageSessionStat> sessions, out string err));
            Assert.Equal("", err);
            Assert.Equal(2, sessions.Count);

            UsageSessionStat s1 = sessions.Find(s => s.SessionId == "sess-1");
            Assert.Equal("重构会话", s1.Title);
            Assert.Equal(12, s1.Turns);
            Assert.Equal(4250, s1.Totals.Total);
            Assert.Equal(2000.0 / 3000.0, s1.Totals.CacheHitRate, 6);
        }

        [Fact]
        public void 缺字段的会话按空值处理()
        {
            UsageParser.TryParseProjCache(Sample, out List<UsageSessionStat> sessions, out _);
            UsageSessionStat s2 = sessions.Find(s => s.SessionId == "sess-2");
            Assert.Equal("", s2.Title);
            Assert.Equal(0, s2.Turns);
            Assert.Equal(0, s2.Totals.Total);
        }

        [Fact]
        public void 损坏的总账返回失败并带原因()
        {
            Assert.False(UsageParser.TryParseProjCache("{ broken", out List<UsageSessionStat> s, out string err));
            Assert.Empty(s);
            Assert.NotEqual("", err);
        }

        [Fact]
        public void 结构不认识时按空表而不是失败()
        {
            Assert.True(UsageParser.TryParseProjCache("{}", out List<UsageSessionStat> s, out _));
            Assert.Empty(s);
            Assert.True(UsageParser.TryParseProjCache(@"{""tables"":{}}", out s, out _));
            Assert.Empty(s);
        }

        [Fact]
        public void 无输入时缓存命中率为负一()
        {
            Assert.Equal(-1, new TokenBuckets { Output = 100 }.CacheHitRate);
        }
    }

    public class UsageSessionLogTests
    {
        private const long Day1 = 1755676800000;   // 2026-08-20 (UTC)
        private const long Day2 = 1755763200000;   // 2026-08-21 (UTC)

        private static string Header(string provider, string model) =>
            @"{""type"":""request/header"",""data"":{""header"":{""config"":{""provider"":""" + provider +
            @""",""model"":""" + model + @"""}}}}";

        private static string Chunk(long time, long turn, long step, long uncached, long read, long write, long output) =>
            @"{""type"":""assistant/chunk"",""time"":" + time + @",""data"":{""turn"":" + turn +
            @",""step"":" + step + @",""chunk"":{""type"":""usage"",""usage"":{""uncachedInputTokens"":" + uncached +
            @",""cacheReadTokens"":" + read + @",""cacheWriteTokens"":" + write +
            @",""outputTokens"":" + output + @"}}}}";

        [Fact]
        public void 同一turn和step的重复样本是替换而非累加()
        {
            string jsonl = string.Join("\n",
                Header("deepseek", "chat"),
                Chunk(Day1, 1, 1, 100, 0, 0, 10),
                Chunk(Day1, 1, 1, 150, 0, 0, 20));

            List<UsageSample> samples = UsageParser.ParseSessionSamples(jsonl);

            Assert.Single(samples);
            Assert.Equal(150, samples[0].UncachedInput);
            Assert.Equal(20, samples[0].Output);
        }

        [Fact]
        public void 模型名取最近一次请求头()
        {
            string jsonl = string.Join("\n",
                Header("deepseek", "chat"),
                Chunk(Day1, 1, 1, 10, 0, 0, 1),
                Header("deepseek", "reasoner"),
                Chunk(Day1, 2, 1, 20, 0, 0, 2));

            List<UsageSample> samples = UsageParser.ParseSessionSamples(jsonl);

            Assert.Equal(2, samples.Count);
            Assert.Contains(samples, s => s.Model == "chat");
            Assert.Contains(samples, s => s.Model == "reasoner");
        }

        [Fact]
        public void 未缓存输入缺失时回退inputTokens()
        {
            string jsonl = Header("openai", "gpt") + "\n" +
                @"{""type"":""assistant/message"",""time"":" + Day1 +
                @",""data"":{""turn"":1,""step"":1,""usage"":{""inputTokens"":300,""outputTokens"":40}}}";

            List<UsageSample> samples = UsageParser.ParseSessionSamples(jsonl);

            Assert.Single(samples);
            Assert.Equal(300, samples[0].UncachedInput);
            Assert.Equal(0, samples[0].CacheRead);
            Assert.Equal(40, samples[0].Output);
        }

        [Fact]
        public void 坏行跳过不影响其他样本()
        {
            string jsonl = string.Join("\n",
                Header("deepseek", "chat"),
                "{ this is not json",
                Chunk(Day1, 1, 1, 100, 0, 0, 10));

            Assert.Single(UsageParser.ParseSessionSamples(jsonl));
        }

        [Fact]
        public void 空输入返回空列表()
        {
            Assert.Empty(UsageParser.ParseSessionSamples(""));
            Assert.Empty(UsageParser.ParseSessionSamples(null));
        }

        [Fact]
        public void 按模型聚合并按天分桶()
        {
            var samples = new List<UsageSample>
            {
                new UsageSample { Provider = "deepseek", Model = "chat", TimeMs = Day1, UncachedInput = 100, Output = 10 },
                new UsageSample { Provider = "deepseek", Model = "chat", TimeMs = Day2, UncachedInput = 200, Output = 20 },
                new UsageSample { Provider = "deepseek", Model = "reasoner", TimeMs = Day2, UncachedInput = 50, Output = 5 }
            };

            List<UsageModelStat> models = UsageParser.AggregateModels(new[] { samples });

            Assert.Equal(2, models.Count);
            Assert.Equal("chat", models[0].Model);          // 按总量降序
            Assert.Equal(2, models[0].Requests);
            Assert.Equal(330, models[0].Totals.Total);
            Assert.Equal(2, models[0].Daily.Count);         // 跨两天
            Assert.Equal(1, models[0].DailyRequests[UsageParser.DayKey(Day1)]);
        }

        [Fact]
        public void 没有时间戳的样本不进按天桶()
        {
            var samples = new List<UsageSample>
            {
                new UsageSample { Model = "x", TimeMs = 0, UncachedInput = 10 }
            };

            List<UsageModelStat> models = UsageParser.AggregateModels(new[] { samples });

            Assert.Single(models);
            Assert.Equal(1, models[0].Requests);
            Assert.Empty(models[0].Daily);
        }
    }

    public class UsageZstdAndFrameTests
    {
        [Fact]
        public void 压缩解压往返()
        {
            string text = @"{""type"":""assistant/chunk""}" + "\n";
            byte[] packed = UsageScanner.CompressForTest(text);
            Assert.NotEmpty(packed);
            Assert.Equal(text, Encoding.UTF8.GetString(UsageScanner.Decompress(packed)));
        }

        [Fact]
        public void WSL帧解析出会话样本()
        {
            string jsonl = @"{""type"":""request/header"",""data"":{""header"":{""config"":{""provider"":""p"",""model"":""m""}}}}" + "\n" +
                           @"{""type"":""assistant/chunk"",""time"":1755676800000,""data"":{""turn"":1,""step"":1,""chunk"":{""type"":""usage"",""usage"":{""uncachedInputTokens"":10,""outputTokens"":2}}}}";
            string b64 = Convert.ToBase64String(UsageScanner.CompressForTest(jsonl));
            string frames = "@@DSHU 123 456 /root/.dsh/sessions/a/session.jsonl.zstd\n" + b64 +
                            "\n@@DSHEND\n@@DSHDONE\n";

            List<List<UsageSample>> sessions = UsageScanner.ParseWslFrames(frames, "test-ns-" + Guid.NewGuid());

            Assert.Single(sessions);
            Assert.Single(sessions[0]);
            Assert.Equal("m", sessions[0][0].Model);
        }

        [Fact]
        public void 空帧与坏帧都不抛()
        {
            Assert.Empty(UsageScanner.ParseWslFrames("", "ns"));
            Assert.Empty(UsageScanner.ParseWslFrames("@@DSHDONE\n", "ns"));
            Assert.Empty(UsageScanner.ParseWslFrames("@@DSHU 1 2 /x\n!!!not-base64!!!\n@@DSHEND\n", "ns"));
        }

        [Fact]
        public void 清单输出解析出文件大小与时间()
        {
            string output = "@@DSHL 4096 1755676800 /root/.dsh/sessions/a/session.jsonl.zstd\n" +
                            "@@DSHL 8192 1755763200 /root/.dsh/sessions/b/session.jsonl.zstd\n@@DSHDONE\n";

            List<UsageScanner.WslFileEntry> entries = UsageScanner.ParseListOutput(output);

            Assert.Equal(2, entries.Count);
            Assert.Equal(4096, entries[0].Size);
            Assert.Equal(1755763200, entries[1].Mtime);
            Assert.EndsWith("b/session.jsonl.zstd", entries[1].Path);
        }

        [Fact]
        public void 取内容脚本只包含被点名的文件()
        {
            string script = UsageScanner.BuildFetchScript(new[] { "/root/a b/session.jsonl.zstd" });

            Assert.Contains("base64 -w0", script);
            Assert.Contains("'/root/a b/session.jsonl.zstd'", script);   // 带空格的路径被安全引用
            Assert.Contains("@@DSHDONE", script);
        }
    }
}
