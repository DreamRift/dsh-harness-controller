// ============================================================================
//  UsageSelfTest — 用量统计核心自检（--selftest-usage，全离线）
//
//  覆盖：projcache 解析（完整/缺行/损坏/空表）、会话 JSONL 解析（(turn,step)
//  替换语义 / request-header 模型关联 / inputTokens 回退 / 坏行容忍）、
//  zstd 压缩往返 + Windows 扫描（含损坏文件跳过）、增量缓存失效、
//  跨实例聚合、WSL base64 帧解析。
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using DshController.Core;

namespace DshController.Core
{
    internal static class UsageSelfTest
    {
        private static int _pass;
        private static int _fail;
        private static string _group = "";

        public static int Run(string[] args)
        {
            Console.WriteLine("== 用量统计核心自检（--selftest-usage）==");
            string dir = Path.Combine(Path.GetTempPath(), "dsh-usage-selftest-" +
                Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                RunAll(dir);
            }
            catch (Exception ex)
            {
                Fail("自检未捕获异常: " + ex);
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
            Console.WriteLine();
            Console.WriteLine("== 结果: " + _pass + " 通过 / " + _fail + " 失败 ==");
            return _fail == 0 ? 0 : 1;
        }

        private static void Group(string name)
        {
            _group = name;
            Console.WriteLine();
            Console.WriteLine("[" + name + "]");
        }

        private static void Ok(string name)
        {
            _pass++;
            Console.WriteLine("  [PASS] " + name);
        }

        private static void Fail(string name)
        {
            _fail++;
            Console.WriteLine("  [FAIL] " + (_group.Length > 0 ? _group + " / " : "") + name);
        }

        private static void Assert(bool cond, string name)
        {
            if (cond) Ok(name); else Fail(name);
        }

        private static readonly long Day1Ms = new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        private static readonly long Day2Ms = new DateTimeOffset(2026, 8, 21, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

        private static void RunAll(string dir)
        {
            Directory.CreateDirectory(dir);
            TestProjCache(dir);
            string home = Path.Combine(dir, "home");
            TestJsonlParsing();
            TestWindowsScan(home);
            TestCacheInvalidation(home);
            TestAggregate();
            TestLocalBackup(dir);
            TestWslFrames();
        }

        // ---------------- projcache ----------------

        private static string ProjCacheJson()
        {
            return new StringBuilder()
               .Append("{\"unit\":{\"name\":\"session_projcache\",\"version\":3},")
                .Append("\"tables\":{\"sessions\":{")
                .Append("\"sess-1\":{\"identity\":{\"createdAt\":1787028858174,\"cwd\":\"C:\\\\work\\\\proj\"},")
                .Append("\"rows\":{")
                .Append("\"sessionStats\":{\"val\":{\"turns\":5,\"steps\":42}},")
                .Append("\"title\":{\"val\":\"调研会话\"},")
                .Append("\"tokenUsage\":{\"val\":{\"totals\":{\"uncachedInputTokens\":1000,\"outputTokens\":200,\"cacheReadTokens\":3000,\"cacheWriteTokens\":50}}}")
                .Append("}},")
                .Append("\"sess-2\":{\"identity\":{\"createdAt\":1787115000000},")
                .Append("\"rows\":{\"title\":{\"val\":\"无用量会话\"}}}")
                .Append("}}}")
                .ToString();
        }

        private static void TestProjCache(string dir)
        {
            Group("projcache 解析");
            var ok = UsageStats.TryParseProjCache(ProjCacheJson(), "inst", "主实例",
                out List<SessionUsage> sessions, out string error);
            Assert(ok, "正常 JSON 解析成功（" + error + "）");
            Assert(sessions.Count == 2, "会话数 = 2（实际 " + sessions.Count + "）");
            var s1 = sessions.FirstOrDefault(s => s.SessionId == "sess-1");
            var s2 = sessions.FirstOrDefault(s => s.SessionId == "sess-2");
            Assert(s1 != null, "sess-1 存在");
            Assert(s1.Title == "调研会话", "sess-1 标题");
            Assert(s1.Turns == 5, "sess-1 轮次 = 5");
            Assert(s1.Cwd == "C:\\work\\proj", "sess-1 工作区");
            Assert(s1.CreatedAtMs == 1787028858174L, "sess-1 创建时间");
            Assert(s1.InstanceId == "inst" && s1.InstanceName == "主实例", "实例信息带出");
            Assert(s1.Totals.UncachedInput == 1000, "sess-1 未缓存输入 = 1000");
            Assert(s1.Totals.CacheRead == 3000, "sess-1 缓存读取 = 3000");
            Assert(s1.Totals.CacheWrite == 50, "sess-1 缓存写入 = 50");
            Assert(s1.Totals.Output == 200, "sess-1 输出 = 200");
            Assert(s1.Totals.Total == 4250, "sess-1 总量 = 4250");
            Assert(s2 != null && s2.Totals.Total == 0 && s2.Turns == 0, "sess-2 缺行按空处理");

            Assert(!UsageStats.TryParseProjCache("{oops", "i", "n", out _, out string err2) && err2.Length > 0,
                "损坏 JSON 返回失败 + 错误信息");

            var empty = UsageStats.TryParseProjCache("{\"tables\":{\"sessions\":{}}}", "i", "n",
                out List<SessionUsage> es, out _);
            Assert(empty && es.Count == 0, "空 sessions 表解析成功且为空");

            var noTables = UsageStats.TryParseProjCache("{\"foo\":1}", "i", "n",
                out List<SessionUsage> ns, out _);
            Assert(noTables && ns.Count == 0, "无 tables 结构按空表处理");

            // 实例级 LoadInstanceAsync：损坏 projcache → ReadFailed（Windows 分支）
            string badHome = Path.Combine(dir, "badhome");
            Directory.CreateDirectory(Path.Combine(badHome, "storages"));
            File.WriteAllText(Path.Combine(badHome, "storages", "session_projcache.json"), "{broken");
            var def = new InstanceDef { Id = "bad", Name = "坏实例", Home = badHome };
            var inst = UsageStats.LoadInstanceAsync(def, new AppSettings(), false, null, CancellationToken.None)
                .GetAwaiter().GetResult();
            Assert(inst.Status == InstanceDataStatus.ReadFailed, "损坏 projcache → ReadFailed");

            string emptyHome = Path.Combine(dir, "emptyhome");
            Directory.CreateDirectory(emptyHome);
            var def2 = new InstanceDef { Id = "e", Name = "空实例", Home = emptyHome };
            var inst2 = UsageStats.LoadInstanceAsync(def2, new AppSettings(), false, null, CancellationToken.None)
                .GetAwaiter().GetResult();
            Assert(inst2.Status == InstanceDataStatus.Empty, "无 projcache → Empty");
        }

        // ---------------- 会话 JSONL ----------------

        private static string SessionJsonl()
        {
            return string.Join("\n",
                "{\"type\":\"session/start\",\"time\":" + Day1Ms + "}",
                "{\"type\":\"request/header\",\"time\":" + Day1Ms + ",\"data\":{\"header\":{\"config\":{\"provider\":\"deepseek\",\"model\":\"deepseek-chat\"}}}}",
                "{\"type\":\"assistant/chunk\",\"time\":" + Day1Ms + ",\"data\":{\"turn\":1,\"step\":1,\"chunk\":{\"type\":\"usage\",\"usage\":{\"inputTokens\":100,\"outputTokens\":10,\"cacheReadTokens\":200}}}}",
                "{\"type\":\"assistant/chunk\",\"time\":" + (Day1Ms + 100) + ",\"data\":{\"turn\":1,\"step\":1,\"chunk\":{\"type\":\"usage\",\"usage\":{\"inputTokens\":150,\"outputTokens\":20,\"cacheReadTokens\":300}}}}",
                "{\"type\":\"request/header\",\"time\":" + (Day1Ms + 200) + ",\"data\":{\"header\":{\"config\":{\"provider\":\"deepseek\",\"model\":\"deepseek-reasoner\"}}}}",
                "{\"type\":\"assistant/message\",\"time\":" + (Day1Ms + 300) + ",\"data\":{\"turn\":2,\"step\":1,\"usage\":{\"inputTokens\":30,\"outputTokens\":40}}}",
                "{bad json line",
                "{\"type\":\"assistant/chunk\",\"time\":" + Day2Ms + ",\"data\":{\"turn\":2,\"step\":2,\"chunk\":{\"type\":\"usage\",\"usage\":{\"uncachedInputTokens\":5,\"outputTokens\":6,\"cacheReadTokens\":7,\"cacheWriteTokens\":8}}}}",
                "");
        }

        private static void TestJsonlParsing()
        {
            Group("会话 JSONL 解析");
            var samples = UsageStats.ParseSessionSamples(SessionJsonl());
            Assert(samples.Count == 3, "(turn,step) 去重后 3 条样本（实际 " + samples.Count + "）");

            var chat = samples.FirstOrDefault(s => s.Model == "deepseek-chat");
            Assert(chat != null, "deepseek-chat 样本存在");
            if (chat != null)
            {
                Assert(chat.Provider == "deepseek", "provider 关联自 request/header");
                Assert(chat.UncachedInput == 150, "同一 (turn,step) 替换为最后一条（150）");
                Assert(chat.CacheRead == 300, "缓存读取替换为 300");
                Assert(chat.Output == 20, "输出 = 20");
                Assert(chat.TimeMs == Day1Ms + 100, "样本时间 = 最后一条时间");
            }

            var reasoner = samples.Where(s => s.Model == "deepseek-reasoner").ToList();
            Assert(reasoner.Count == 2, "deepseek-reasoner 样本 2 条");
            if (reasoner.Count == 2)
            {
                var m = reasoner.FirstOrDefault(s => s.UncachedInput == 30);
                var c = reasoner.FirstOrDefault(s => s.UncachedInput == 5);
                Assert(m != null && m.CacheRead == 0 && m.Output == 40,
                    "assistant/message 路径 + inputTokens 回退（无缓存桶记 0）");
                Assert(c != null && c.UncachedInput == 5 && c.CacheRead == 7 && c.CacheWrite == 8 && c.Output == 6,
                    "四桶齐全样本");
                Assert(reasoner.Any(s => s.TimeMs == Day2Ms), "跨天时间戳保留");
            }

            Assert(UsageStats.ParseSessionSamples("").Count == 0, "空文本 → 0 样本");
            Assert(UsageStats.ParseSessionSamples("not json\n\n").Count == 0, "非 JSON 文本 → 0 样本不抛异常");

            var agg = UsageStats.AggregateModels("inst-a", new List<List<UsageSample>> { samples });
            Assert(agg.Count == 2, "按模型聚合成 2 行");
            var aggChat = agg.FirstOrDefault(m => m.Model == "deepseek-chat");
            var aggReasoner = agg.FirstOrDefault(m => m.Model == "deepseek-reasoner");
            Assert(aggChat != null && aggChat.Requests == 1, "chat 请求数 = 1");
            Assert(aggChat != null && aggChat.Totals.Total == 470, "chat 总量 = 470（实际 " + (aggChat?.Totals.Total ?? -1) + "）");
            Assert(aggChat != null && aggChat.Daily.Count == 1, "chat 单日");
            Assert(aggReasoner != null && aggReasoner.Requests == 2, "reasoner 请求数 = 2");
            Assert(aggReasoner != null && aggReasoner.Totals.Total == 96, "reasoner 总量 = 96（35+7+8+46）");
            Assert(aggReasoner != null && aggReasoner.Daily.Count == 2, "reasoner 跨 2 天");
            Assert(aggReasoner != null && aggReasoner.FirstMs > 0 && aggReasoner.LastMs >= aggReasoner.FirstMs,
                "首末时间戳");
        }

        // ---------------- Windows 扫描（zstd 往返 + 损坏文件） ----------------

        private static void WriteSession(string home, string ws, string sid, string jsonl)
        {
            string dir = Path.Combine(home, "sessions", ws, sid);
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, "session.jsonl.zstd"),
                UsageStats.CompressForTest(jsonl));
        }

        private static void TestWindowsScan(string home)
        {
            Group("Windows 扫描（zstd 往返）");
            UsageStats.ClearCache();
            WriteSession(home, "ws1", "s1", SessionJsonl());
            WriteSession(home, "ws2", "s2", SessionJsonl());
            string badDir = Path.Combine(home, "sessions", "ws3", "s3");
            Directory.CreateDirectory(badDir);
            File.WriteAllBytes(Path.Combine(badDir, "session.jsonl.zstd"), Encoding.UTF8.GetBytes("not zstd bytes"));

            var models = UsageStats.ScanWindowsModelsAsync("inst-a", home, null, CancellationToken.None)
                .GetAwaiter().GetResult();
            Assert(models.Count == 2, "损坏文件跳过，2 个模型（实际 " + models.Count + "）");
            var chat = models.FirstOrDefault(m => m.Model == "deepseek-chat");
            var reasoner = models.FirstOrDefault(m => m.Model == "deepseek-reasoner");
            Assert(chat != null && chat.Requests == 2, "两个会话合并 chat 请求 = 2");
            Assert(chat != null && chat.Totals.Total == 940, "chat 总量 = 940（实际 " + (chat?.Totals.Total ?? -1) + "）");
            Assert(reasoner != null && reasoner.Totals.Total == 192, "reasoner 总量 = 192");
            Assert(models.First().Totals.Total >= models.Last().Totals.Total, "按总量降序");

            UsageStats.ClearCache();
        }

        private static void TestCacheInvalidation(string home)
        {
            Group("增量缓存");
            string file = Path.Combine(home, "sessions", "ws1", "s1", "session.jsonl.zstd");
            var first = UsageStats.ScanWindowsModelsAsync("inst-a", home, null, CancellationToken.None)
                .GetAwaiter().GetResult();
            var chat1 = first.FirstOrDefault(m => m.Model == "deepseek-chat");
            Assert(chat1 != null && chat1.Totals.Total == 940, "缓存命中：首次结果稳定");

            string changed = SessionJsonl().Replace("\"inputTokens\":150", "\"inputTokens\":999");
            File.WriteAllBytes(file, UsageStats.CompressForTest(changed));
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(1));
            var second = UsageStats.ScanWindowsModelsAsync("inst-a", home, null, CancellationToken.None)
                .GetAwaiter().GetResult();
            var chat2 = second.FirstOrDefault(m => m.Model == "deepseek-chat");
            Assert(chat2 != null && chat2.Totals.Total != 940 && chat2.Totals.Total > 940,
                "文件变化后重新解析（" + (chat2?.Totals.Total ?? -1) + "）");

            // 还原，避免影响后续断言（若有）
            File.WriteAllBytes(file, UsageStats.CompressForTest(SessionJsonl()));
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(2));
        }

        // ---------------- 跨实例聚合 ----------------

        private static void TestAggregate()
        {
            Group("跨实例聚合");
            var i1 = new InstanceUsage { Def = new InstanceDef { Id = "a", Name = "实例A" } };
            i1.Sessions.Add(new SessionUsage
            {
                SessionId = "s1",
                CreatedAtMs = 1787028858174L,
                Totals = new TokenBuckets { UncachedInput = 1000, CacheRead = 3000, CacheWrite = 50, Output = 200 }
            });
            i1.Models = UsageStats.AggregateModels("a", new List<List<UsageSample>>
            {
                UsageStats.ParseSessionSamples(SessionJsonl())
            });
            i1.ModelsComplete = true;
            i1.Status = InstanceDataStatus.Ok;

            var i2 = new InstanceUsage { Def = new InstanceDef { Id = "b", Name = "实例B", Runtime = "wsl", WslDistro = "Ubuntu" } };
            i2.Sessions.Add(new SessionUsage
            {
                SessionId = "s2",
                CreatedAtMs = 1787028800000L,
                Totals = new TokenBuckets { UncachedInput = 10, Output = 5 }
            });
            i2.Status = InstanceDataStatus.DistroNotRunning;

            var rep = UsageStats.Aggregate(new List<InstanceUsage> { i1, i2 });
            Assert(rep.SessionCount == 2, "会话数 = 2");
            Assert(rep.Totals.UncachedInput == 1010, "未缓存输入合并 = 1010");
            Assert(rep.Totals.CacheRead == 3000, "缓存读取合并 = 3000");
            Assert(rep.Totals.CacheWrite == 50, "缓存写入合并 = 50");
            Assert(rep.Totals.Output == 205, "输出合并 = 205");
            Assert(rep.Totals.Total == 4265, "总量 = 4265");
            Assert(rep.Models.Count == 2, "模型合并 2 行");
            Assert(rep.RequestCount == 3, "请求次数 = 3");
            Assert(rep.TopModel == "deepseek-chat", "最常用模型 = deepseek-chat（实际 " + rep.TopModel + "）");
            Assert(rep.ActiveDays == 2, "活跃天数 = 2（实际 " + rep.ActiveDays + "）");
            Assert(rep.Daily.Count == 2, "每日桶 2 天");
            Assert(rep.NotRunningCount == 1, "未运行实例计数");
            double hit = rep.Totals.CacheHitRate;
            Assert(hit > 0.7 && hit < 0.8, "缓存命中率 ≈ 74.8%（实际 " + (hit * 100).ToString("0.0") + "%）");
            Assert(rep.Sessions.First().SessionId == "s1", "会话按时间降序（s1 createdAt 更大）");
        }

        // ---------------- 应用目录本地备份 ----------------

        private static void TestLocalBackup(string dir)
        {
            Group("应用目录本地备份");
            string file = Path.Combine(dir, "usage-backup.json");
            using (UsageStatsBackupStore.UseFilePathForTest(file))
            {
                var def = new InstanceDef { Id = "backup-a", Name = "备份实例", Home = Path.Combine(dir, "gone-home") };
                var usage = new InstanceUsage
                {
                    Def = def,
                    Status = InstanceDataStatus.Ok,
                    StatusText = "2 个会话",
                    ModelsComplete = true
                };
                usage.Sessions.Add(new SessionUsage
                {
                    InstanceId = def.Id,
                    InstanceName = def.Name,
                    SessionId = "backup-session",
                    Title = "历史会话",
                    CreatedAtMs = Day1Ms,
                    Totals = new TokenBuckets { UncachedInput = 100, CacheRead = 200, Output = 50 }
                });
                usage.Models = UsageStats.AggregateModels(def.Id, new List<List<UsageSample>>
                {
                    UsageStats.ParseSessionSamples(SessionJsonl())
                });
                UsageStatsBackupStore.Merge(new[] { usage });
                Assert(File.Exists(file), "快照写入应用目录文件");

                var loaded = UsageStatsBackupStore.LoadForScope(null, new[] { def });
                Assert(loaded.Count == 1 && loaded[0].Sessions.Count == 1, "快照可读且会话保留");
                Assert(loaded[0].Totals.Total == 350, "快照会话总量保留");
                Assert(loaded[0].Models.Count == 2 && loaded[0].ModelsComplete, "快照模型与按天数据保留");

                // 当前注册表移除实例后，历史快照仍作为可查看实例返回。
                var historical = UsageStatsBackupStore.LoadForScope(null, Array.Empty<InstanceDef>());
                Assert(historical.Count == 1 && historical[0].Def.Id == def.Id,
                    "注册表删除实例后仍可从快照查看");

                // 空/失败读取不能覆盖已有真实数据。
                var empty = new InstanceUsage { Def = def, Status = InstanceDataStatus.ReadFailed };
                UsageStatsBackupStore.Merge(new[] { empty });
                var preserved = UsageStatsBackupStore.LoadForScope(def.Id, new[] { def });
                Assert(preserved.Count == 1 && preserved[0].Totals.Total == 350,
                    "失败读取不会覆盖已有快照");
            }
        }

        // ---------------- WSL base64 帧 ----------------

        private static void TestWslFrames()
        {
            Group("WSL 帧解析");
            byte[] compressed = UsageStats.CompressForTest(SessionJsonl());
            string b64 = Convert.ToBase64String(compressed);
            string output = string.Join("\n",
                "junk line",
                "@@DSHU " + compressed.Length + " 1700000000 /home/u/.dsh/sessions/a/session.jsonl.zstd",
                b64,
                "@@DSHEND",
                "@@DSHU 5 123 /home/u/.dsh/sessions/b/empty.jsonl.zstd",
                "",
                "@@DSHEND",
                "@@DSHDONE",
                "");

            var frames = UsageStats.ParseWslFrames(output, "wsl-main");
            Assert(frames.Count == 1, "空 base64 帧跳过，解析 1 帧（实际 " + frames.Count + "）");
            Assert(frames[0].Count == 3, "帧内容 = 3 条样本");
            var chat = frames[0].FirstOrDefault(s => s.Model == "deepseek-chat");
            Assert(chat != null && chat.UncachedInput == 150, "帧样本值正确");

            Assert(UsageStats.ParseWslFrames("", "x").Count == 0, "空输出 → 0 帧");
            Assert(UsageStats.ParseWslFrames("@@DSHDONE\n", "x").Count == 0, "仅 DONE → 0 帧");
            string badFrame = "@@DSHU 10 1 /x\n!!!notbase64!!!\n@@DSHEND\n@@DSHDONE\n";
            Assert(UsageStats.ParseWslFrames(badFrame, "x").Count == 0, "坏 base64 帧跳过不抛异常");
        }
    }
}
