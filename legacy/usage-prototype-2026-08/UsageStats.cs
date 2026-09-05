// ============================================================================
//  UsageStats — 实例 Token 用量统计（数据层，纯逻辑无 UI 依赖）
//
//  数据源（各实例 DSH_HOME 内，Windows / WSL 同构）：
//    1. <HOME>/storages/session_projcache.json —— 官方 @deepseek-ai/dsh-token-meter
//       落盘的按会话汇总（tokenUsage 四分桶 + sessionStats.turns + title/identity）。
//       轻量、权威，用于 汇总 / 分实例 / 会话明细。
//    2. <HOME>/sessions/<工作区>/<会话>/session.jsonl.zstd —— zstd 压缩的会话
//       事件流，逐请求 usage + request/header 的 provider/model。用于 按模型 /
//       按天（热力图、趋势）统计。
//
//  解析规则（与官方 token-meter 语义对齐）：
//    - usage 事件出现在 assistant/chunk(data.chunk.type=="usage") 与
//      assistant/message(data.usage)；同一 (turn,step) 重复样本是"替换"而非累加；
//    - 模型名取流中最近一次 request/header(data.header.config.{provider,model})；
//    - 未缓存输入优先 uncachedInputTokens，缺失时回退 inputTokens（OpenAI-compat
//      渠道只有 input/output 两个字段，缓存桶记 0）。
//
//  传输：Windows 直接读文件；WSL 经发行版内一次 bash 批处理把全部
//  session.jsonl.zstd 以 base64 帧回传（二进制安全、单次 wsl.exe 往返），
//  Windows 侧统一用 ZstdSharp（纯托管）解压。
//
//  缓存：按 (实例, 文件, size, mtime) 缓存每会话的样本列表，重复解析只处理
//  变化过的文件。这里仅负责源数据读取与内存缓存；持久化快照由
//  UsageStatsBackupStore 写入应用根目录，真实源状态仍以实例 HOME 文件为准。
// ============================================================================

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DshController.Core
{
    // ==================== 数据模型 ====================

    /// <summary>官方 token-meter 四分桶：未缓存输入 / 缓存读 / 缓存写 / 输出。</summary>
    public sealed class TokenBuckets
    {
        public long UncachedInput { get; set; }
        public long CacheRead { get; set; }
        public long CacheWrite { get; set; }
        public long Output { get; set; }

        public long Total => UncachedInput + CacheRead + CacheWrite + Output;

        /// <summary>输入侧缓存命中率 cacheRead / (uncachedInput + cacheRead)；无输入返回 -1。</summary>
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

        public TokenBuckets Clone()
        {
            return new TokenBuckets
            {
                UncachedInput = UncachedInput,
                CacheRead = CacheRead,
                CacheWrite = CacheWrite,
                Output = Output
            };
        }
    }

    /// <summary>会话日志中一条去重后的请求用量样本（(turn,step) 保留最后一条）。</summary>
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

    /// <summary>单会话用量（来自 projcache）。</summary>
    public sealed class SessionUsage
    {
        public string InstanceId { get; set; } = "";
        public string InstanceName { get; set; } = "";
        public string SessionId { get; set; } = "";
        public string Title { get; set; } = "";
        public long CreatedAtMs { get; set; }
        public string Cwd { get; set; } = "";
        public long Turns { get; set; }
        public TokenBuckets Totals { get; set; } = new TokenBuckets();
    }

    /// <summary>按 (provider, model) 聚合的用量（来自会话日志解析）。</summary>
    public sealed class ModelUsage
    {
        public string InstanceId { get; set; } = "";
        public string Provider { get; set; } = "";
        public string Model { get; set; } = "";
        public int Requests { get; set; }
        public long FirstMs { get; set; }
        public long LastMs { get; set; }
        public TokenBuckets Totals { get; set; } = new TokenBuckets();

        /// <summary>按本地日期（yyyy-MM-dd）分桶的每日用量。</summary>
        public SortedDictionary<string, TokenBuckets> Daily { get; } =
            new SortedDictionary<string, TokenBuckets>(StringComparer.Ordinal);

        /// <summary>按本地日期分桶的每日请求次数。</summary>
        public SortedDictionary<string, long> DailyRequests { get; } =
            new SortedDictionary<string, long>(StringComparer.Ordinal);

        public string DisplayName => string.IsNullOrEmpty(Model) ? "(未知模型)" : Model;
    }

    /// <summary>单实例的读取结果与状态。</summary>
    public enum InstanceDataStatus
    {
        Pending,
        Ok,
        Empty,
        DistroNotRunning,
        ReadFailed
    }

    public sealed class InstanceUsage
    {
        public InstanceDef Def { get; set; }
        public InstanceDataStatus Status { get; set; } = InstanceDataStatus.Pending;
        public string StatusText { get; set; } = "";
        public string HomeDisplay { get; set; } = "";
        public List<SessionUsage> Sessions { get; } = new List<SessionUsage>();
        public List<ModelUsage> Models { get; set; } = new List<ModelUsage>();
        public bool ModelsComplete { get; set; }
        public string ModelScanError { get; set; } = "";
        public TokenBuckets Totals { get; } = new TokenBuckets();

        public bool IsOk => Status == InstanceDataStatus.Ok;
    }

    /// <summary>跨实例聚合报告（范围 = 面板当前选择的实例集合）。</summary>
    public sealed class UsageReport
    {
        public TokenBuckets Totals { get; } = new TokenBuckets();
        public int SessionCount { get; set; }
        public int RequestCount { get; set; }
        public int ActiveDays { get; set; }
        public string TopModel { get; set; } = "";
        public List<InstanceUsage> Instances { get; } = new List<InstanceUsage>();
        public List<ModelUsage> Models { get; } = new List<ModelUsage>();
        public List<SessionUsage> Sessions { get; } = new List<SessionUsage>();

        /// <summary>按天升序的每日总量（key = yyyy-MM-dd）。</summary>
        public SortedDictionary<string, TokenBuckets> Daily { get; } =
            new SortedDictionary<string, TokenBuckets>(StringComparer.Ordinal);

        /// <summary>按天升序的每日请求次数（key = yyyy-MM-dd）。</summary>
        public SortedDictionary<string, long> DailyRequests { get; } =
            new SortedDictionary<string, long>(StringComparer.Ordinal);

        public bool ModelsComplete { get; set; }
        public int NotRunningCount { get; set; }
        public int FailedCount { get; set; }
        public int EmptyCount { get; set; }
    }

    // ==================== 读取与聚合 ====================

    public static class UsageStats
    {
        // ---------------- 主入口 ----------------

        /// <summary>
        /// 读取单个实例的用量数据。includeModels=false 只读 projcache（轻量）；
        /// true 时额外解析 sessions/**/session.jsonl.zstd 得到按模型/按天数据。
        /// </summary>
        public static async Task<InstanceUsage> LoadInstanceAsync(InstanceDef def, AppSettings settings,
            bool includeModels, Action<string> log, CancellationToken ct)
        {
            var result = new InstanceUsage { Def = def };
            if (def == null) return result;
            ct.ThrowIfCancellationRequested();

            if (def.IsWsl)
            {
                string distro = def.WslDistro ?? "";
                if (string.IsNullOrWhiteSpace(distro))
                {
                    result.Status = InstanceDataStatus.ReadFailed;
                    result.StatusText = "WSL 实例未配置发行版";
                    return result;
                }
                bool running = await WslTools.IsDistroRunningAsync(distro).ConfigureAwait(true);
                if (!running)
                {
                    result.Status = InstanceDataStatus.DistroNotRunning;
                    result.StatusText = "发行版未运行";
                    return result;
                }
                string root = await WslTools.GetDistroHomeAsync(distro).ConfigureAwait(true);
                string home = WslTools.ResolveLinuxPath(def.WslHome ?? "",
                    string.IsNullOrEmpty(root) ? "/root" : root);
                result.HomeDisplay = distro + " 内 " + home;

                string json = await WslTools.ReadDistroFileAsync(distro,
                    home.TrimEnd('/') + "/storages/session_projcache.json").ConfigureAwait(true);
                FillFromProjCache(result, json, ct);

                if (includeModels && result.IsOk)
                {
                    try
                    {
                        result.Models = await ScanWslModelsAsync(def.Id, distro, home, log, ct).ConfigureAwait(true);
                        result.ModelsComplete = true;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        result.ModelScanError = ex.Message;
                        log?.Invoke("[用量] " + def.Name + ": 会话日志解析失败 — " + ex.Message);
                    }
                }
            }
            else
            {
                string home = string.IsNullOrWhiteSpace(def.Home)
                    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh")
                    : def.Home;
                result.HomeDisplay = home;
                if (!Directory.Exists(home))
                {
                    result.Status = InstanceDataStatus.Empty;
                    result.StatusText = "HOME 不存在（未初始化）";
                    return result;
                }

                string json = null;
                string pc = Path.Combine(home, "storages", "session_projcache.json");
                try { if (File.Exists(pc)) json = File.ReadAllText(pc, Encoding.UTF8); }
                catch (Exception ex) { json = null; result.StatusText = ex.Message; }
                FillFromProjCache(result, json, ct);

                if (includeModels && result.IsOk)
                {
                    try
                    {
                        result.Models = await ScanWindowsModelsAsync(def.Id, home, log, ct).ConfigureAwait(true);
                        result.ModelsComplete = true;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        result.ModelScanError = ex.Message;
                        log?.Invoke("[用量] " + def.Name + ": 会话日志解析失败 — " + ex.Message);
                    }
                }
            }
            return result;
        }

        /// <summary>清空会话解析缓存（下次刷新全量重解析）。</summary>
        public static void ClearCache() => SessionCache.Clear();

        /// <summary>把范围实例聚合成报告（KPI + 模型合并 + 每日合并 + 会话明细）。</summary>
        public static UsageReport Aggregate(IEnumerable<InstanceUsage> instances)
        {
            var report = new UsageReport();
            var modelMap = new Dictionary<string, ModelUsage>(StringComparer.Ordinal);
            foreach (var inst in instances)
            {
                if (inst == null) continue;
                report.Instances.Add(inst);
                foreach (var s in inst.Sessions)
                {
                    report.Totals.Add(s.Totals);
                    report.SessionCount++;
                    report.Sessions.Add(s);
                }
                if (inst.Status == InstanceDataStatus.DistroNotRunning) report.NotRunningCount++;
                else if (inst.Status == InstanceDataStatus.ReadFailed) report.FailedCount++;
                else if (inst.Status == InstanceDataStatus.Empty) report.EmptyCount++;

                if (inst.ModelsComplete)
                {
                    report.ModelsComplete = true;
                    foreach (var m in inst.Models)
                    {
                        string key = m.Provider + "/" + m.Model;
                        if (!modelMap.TryGetValue(key, out var agg))
                        {
                            agg = new ModelUsage { Provider = m.Provider, Model = m.Model, InstanceId = "" };
                            modelMap[key] = agg;
                        }
                        agg.Requests += m.Requests;
                        agg.Totals.Add(m.Totals);
                        if (m.FirstMs > 0 && (agg.FirstMs == 0 || m.FirstMs < agg.FirstMs)) agg.FirstMs = m.FirstMs;
                        if (m.LastMs > agg.LastMs) agg.LastMs = m.LastMs;
                        foreach (var kv in m.Daily)
                        {
                            if (!agg.Daily.TryGetValue(kv.Key, out var day))
                            {
                                day = new TokenBuckets();
                                agg.Daily[kv.Key] = day;
                            }
                            day.Add(kv.Value);
                        }
                        foreach (var kv in m.DailyRequests)
                            agg.DailyRequests[kv.Key] = (agg.DailyRequests.TryGetValue(kv.Key, out long r) ? r : 0) + kv.Value;
                    }
                }
            }

            report.Models.AddRange(modelMap.Values.OrderByDescending(m => m.Totals.Total).ToList());
            foreach (var m in report.Models)
            {
                report.RequestCount += m.Requests;
                foreach (var kv in m.Daily)
                {
                    if (!report.Daily.TryGetValue(kv.Key, out var day))
                    {
                        day = new TokenBuckets();
                        report.Daily[kv.Key] = day;
                    }
                    day.Add(kv.Value);
                }
                foreach (var kv in m.DailyRequests)
                    report.DailyRequests[kv.Key] = (report.DailyRequests.TryGetValue(kv.Key, out long r) ? r : 0) + kv.Value;
            }
            report.ActiveDays = report.Daily.Count(kv => kv.Value.Total > 0);
            report.TopModel = report.Models.Count > 0 ? report.Models[0].DisplayName : "";
            report.Sessions.Sort((x, y) => y.CreatedAtMs.CompareTo(x.CreatedAtMs));
            return report;
        }

        // ---------------- projcache 解析 ----------------

        /// <summary>解析 projcache 文本填充会话列表；json 为空 = 无数据（Empty），损坏 = ReadFailed。</summary>
        private static void FillFromProjCache(InstanceUsage result, string json, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                if (result.Status == InstanceDataStatus.Pending)
                {
                    result.Status = InstanceDataStatus.Empty;
                    if (result.StatusText.Length == 0) result.StatusText = "暂无会话数据";
                }
                return;
            }
            List<SessionUsage> sessions;
            string error;
            if (!TryParseProjCache(json, result.Def?.Id ?? "", result.Def?.Name ?? "", out sessions, out error))
            {
                result.Status = InstanceDataStatus.ReadFailed;
                result.StatusText = "session_projcache.json 解析失败（可能正被 harness 写入）: " + error;
                return;
            }
            result.Sessions.AddRange(sessions);
            foreach (var s in sessions) result.Totals.Add(s.Totals);
            result.Status = InstanceDataStatus.Ok;
            result.StatusText = sessions.Count + " 个会话";
        }

        /// <summary>解析 session_projcache.json（黑盒、容错：缺行/缺字段按空处理）。</summary>
        public static bool TryParseProjCache(string json, string instanceId, string instanceName,
            out List<SessionUsage> sessions, out string error)
        {
            sessions = new List<SessionUsage>();
            error = "";
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("tables", out var tables) ||
                    !tables.TryGetProperty("sessions", out var sessionsEl) ||
                    sessionsEl.ValueKind != JsonValueKind.Object)
                {
                    return true;   // 结构存在但无会话 → 空表
                }
                foreach (var sid in sessionsEl.EnumerateObject())
                {
                    var entry = sid.Value;
                    var s = new SessionUsage
                    {
                        InstanceId = instanceId ?? "",
                        InstanceName = instanceName ?? "",
                        SessionId = sid.Name
                    };
                    if (entry.TryGetProperty("identity", out var identity))
                    {
                        s.CreatedAtMs = L(identity, "createdAt");
                        s.Cwd = S(identity, "cwd");
                    }
                    if (entry.TryGetProperty("rows", out var rows))
                    {
                        if (rows.TryGetProperty("title", out var title) && title.TryGetProperty("val", out var tv))
                            s.Title = tv.ValueKind == JsonValueKind.String ? tv.GetString() ?? "" : "";
                        if (rows.TryGetProperty("sessionStats", out var stats) && stats.TryGetProperty("val", out var sv))
                            s.Turns = L(sv, "turns");
                        if (rows.TryGetProperty("tokenUsage", out var tu) && tu.TryGetProperty("val", out var uv) &&
                            uv.TryGetProperty("totals", out var totals))
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
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        // ---------------- 会话日志（jsonl.zstd）解析 ----------------

        private sealed class CachedSamples
        {
            public long Length;
            public long MtimeEpoch;
            public List<UsageSample> Samples;
        }

        private static readonly ConcurrentDictionary<string, CachedSamples> SessionCache =
            new ConcurrentDictionary<string, CachedSamples>(StringComparer.OrdinalIgnoreCase);

        /// <summary>解析一段解压后的会话 JSONL 文本 → 去重样本列表（(turn,step) 保留最后一条）。</summary>
        public static List<UsageSample> ParseSessionSamples(string jsonl)
        {
            var last = new Dictionary<string, UsageSample>(StringComparer.Ordinal);
            string curProvider = "", curModel = "";
            if (string.IsNullOrEmpty(jsonl)) return new List<UsageSample>();
            foreach (var rawLine in jsonl.Split('\n'))
            {
                var line = rawLine.Trim('\r', ' ', '\t');
                if (line.Length == 0) continue;
                JsonDocument doc;
                try { doc = JsonDocument.Parse(line); }
                catch { continue; }   // 坏行跳过
                using (doc)
                {
                    var root = doc.RootElement;
                    if (root.ValueKind != JsonValueKind.Object) continue;
                    string type = S(root, "type");
                    if (type.Length == 0) continue;

                    if (type == "request/header")
                    {
                        if (root.TryGetProperty("data", out var data) &&
                            data.TryGetProperty("header", out var header) &&
                            header.TryGetProperty("config", out var cfg))
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
                        if (root.TryGetProperty("data", out var data) &&
                            data.TryGetProperty("chunk", out var chunk) &&
                            S(chunk, "type") == "usage" && chunk.TryGetProperty("usage", out var u))
                        {
                            usage = u;
                            turn = L(data, "turn");
                            step = L(data, "step");
                            have = true;
                        }
                    }
                    else if (type == "assistant/message")
                    {
                        if (root.TryGetProperty("data", out var data) && data.TryGetProperty("usage", out var u))
                        {
                            usage = u;
                            turn = L(data, "turn");
                            step = L(data, "step");
                            have = true;
                        }
                    }
                    if (!have) continue;

                    var sample = new UsageSample
                    {
                        Provider = curProvider ?? "",
                        Model = curModel ?? "",
                        TimeMs = L(root, "time"),
                        UncachedInput = LPrefer(usage, "uncachedInputTokens", "inputTokens"),
                        CacheRead = L(usage, "cacheReadTokens"),
                        CacheWrite = L(usage, "cacheWriteTokens"),
                        Output = L(usage, "outputTokens")
                    };
                    // 同一 (turn,step) 重复样本 = 替换（官方 token-meter 语义）
                    last[turn + "/" + step] = sample;
                }
            }
            return last.Values.ToList();
        }

        /// <summary>扫描 Windows 实例 HOME 的全部会话日志并按模型聚合。</summary>
        public static async Task<List<ModelUsage>> ScanWindowsModelsAsync(string instanceId, string home,
            Action<string> log, CancellationToken ct)
        {
            string root = Path.Combine(home, "sessions");
            var perSession = new List<List<UsageSample>>();
            if (!Directory.Exists(root)) return AggregateModels(instanceId, perSession);

            List<string> files = Directory.EnumerateFiles(root, "session.jsonl.zstd", SearchOption.AllDirectories)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
            if (files.Count == 0) return AggregateModels(instanceId, perSession);
            log?.Invoke("[用量] " + instanceId + ": 解析 " + files.Count + " 个会话日志…");

            await Task.Run(() =>
            {
                foreach (var file in files)
                {
                    ct.ThrowIfCancellationRequested();
                    List<UsageSample> samples = LoadWindowsSession(file);
                    if (samples != null) perSession.Add(samples);
                }
            }).ConfigureAwait(true);

            log?.Invoke("[用量] " + instanceId + ": 会话日志解析完成");
            return AggregateModels(instanceId, perSession);
        }

        private static List<UsageSample> LoadWindowsSession(string file)
        {
            try
            {
                var fi = new FileInfo(file);
                if (!fi.Exists) return null;
                long mtime = new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeSeconds();
                string key = "win:" + file;
                if (SessionCache.TryGetValue(key, out var cached) &&
                    cached.Length == fi.Length && cached.MtimeEpoch == mtime)
                    return cached.Samples;

                byte[] compressed = File.ReadAllBytes(file);
                string text = Encoding.UTF8.GetString(Decompress(compressed));
                var samples = ParseSessionSamples(text);
                SessionCache[key] = new CachedSamples { Length = fi.Length, MtimeEpoch = mtime, Samples = samples };
                return samples;
            }
            catch
            {
                return null;   // 单文件损坏不影响整体
            }
        }

        /// <summary>
        /// 扫描 WSL 实例 HOME 的全部会话日志：发行版内一次 bash 批处理把所有
        /// session.jsonl.zstd 以 "@@DSHU size mtime path" / base64 / "@@DSHEND" 帧回传。
        /// </summary>
        public static async Task<List<ModelUsage>> ScanWslModelsAsync(string instanceId, string distro,
            string linuxHome, Action<string> log, CancellationToken ct)
        {
            string sessionsDir = (linuxHome ?? "").TrimEnd('/') + "/sessions";
            string h = WslTools.Shq(sessionsDir);
            string script =
                "if [ -d " + h + " ]; then " +
                "find " + h + " -type f -name 'session.jsonl.zstd' -print0 | " +
                "while IFS= read -r -d '' f; do " +
                "sz=$(stat -c %s \"$f\" 2>/dev/null || echo 0); " +
                "mt=$(stat -c %Y \"$f\" 2>/dev/null || echo 0); " +
                "printf '@@DSHU %s %s %s\\n' \"$sz\" \"$mt\" \"$f\"; " +
                "base64 -w0 \"$f\" 2>/dev/null; " +
                "printf '\\n@@DSHEND\\n'; " +
                "done; fi; " +
                "printf '@@DSHDONE\\n'";
            log?.Invoke("[用量] " + instanceId + ": 正在读取发行版会话日志（单次往返，可能需要数十秒）…");
            var r = await WslTools.RunInDistroAsync(distro, script, 300000).ConfigureAwait(true);
            if (r.Output == null || !r.Output.Contains("@@DSHU "))
            {
                log?.Invoke("[用量] " + instanceId + ": 未读取到会话日志" +
                    (r.TimedOut ? "（读取超时）" : r.Ok ? "（目录为空）" : "（exit=" + r.ExitCode + "）"));
                return new List<ModelUsage>();
            }
            var perSession = ParseWslFrames(r.Output, distro);
            log?.Invoke("[用量] " + instanceId + ": 会话日志解析完成（" + perSession.Count + " 个会话）");
            return AggregateModels(instanceId, perSession);
        }

        /// <summary>解析 bash 批处理的 base64 帧 → 每会话样本列表（离线可测）。</summary>
        public static List<List<UsageSample>> ParseWslFrames(string output, string cacheNamespace)
        {
            var result = new List<List<UsageSample>>();
            if (string.IsNullOrEmpty(output)) return result;
            int pos = 0;
            string s = output;
            while (pos < s.Length)
            {
                int eol = s.IndexOf('\n', pos);
                if (eol < 0) eol = s.Length;
                string line = s.Substring(pos, eol - pos).TrimEnd('\r');
                pos = eol + 1;
                if (line.Length == 0) continue;
                if (line.StartsWith("@@DSHDONE")) break;
                if (!line.StartsWith("@@DSHU ")) continue;

                // @@DSHU <size> <mtime> <path...>
                string header = line.Substring(7);
                int sp1 = header.IndexOf(' ');
                if (sp1 <= 0) continue;
                int sp2 = header.IndexOf(' ', sp1 + 1);
                if (sp2 <= 0) continue;
                long sz = ParseLong(header.Substring(0, sp1));
                long mt = ParseLong(header.Substring(sp1 + 1, sp2 - sp1 - 1));
                string path = header.Substring(sp2 + 1).Trim();

                // 收集到 @@DSHEND 为止的 base64（-w0 单行，容忍中间空行）
                var b64 = new StringBuilder();
                while (pos < s.Length)
                {
                    int eol2 = s.IndexOf('\n', pos);
                    if (eol2 < 0) eol2 = s.Length;
                    string dl = s.Substring(pos, eol2 - pos).TrimEnd('\r');
                    pos = eol2 + 1;
                    if (dl == "@@DSHEND") break;
                    if (dl == "@@DSHDONE") { pos = s.Length; break; }
                    if (dl.Length > 0) b64.Append(dl.Trim());
                }

                List<UsageSample> samples = null;
                if (b64.Length > 0)
                {
                    string key = "wsl:" + cacheNamespace + ":" + path;
                    if (SessionCache.TryGetValue(key, out var cached) &&
                        cached.Length == sz && cached.MtimeEpoch == mt)
                    {
                        samples = cached.Samples;
                    }
                    else
                    {
                        try
                        {
                            byte[] compressed = Convert.FromBase64String(b64.ToString());
                            string text = Encoding.UTF8.GetString(Decompress(compressed));
                            samples = ParseSessionSamples(text);
                            SessionCache[key] = new CachedSamples { Length = sz, MtimeEpoch = mt, Samples = samples };
                        }
                        catch { samples = null; }
                    }
                }
                if (samples != null) result.Add(samples);
            }
            return result;
        }

        // ---------------- 聚合辅助 ----------------

        /// <summary>每会话样本 → 按 (provider, model) 聚合。</summary>
        public static List<ModelUsage> AggregateModels(string instanceId, IEnumerable<List<UsageSample>> sessionLists)
        {
            var map = new Dictionary<string, ModelUsage>(StringComparer.Ordinal);
            foreach (var samples in sessionLists ?? Enumerable.Empty<List<UsageSample>>())
            {
                if (samples == null) continue;
                foreach (var sp in samples)
                {
                    string key = (sp.Provider ?? "") + "/" + (sp.Model ?? "");
                    if (!map.TryGetValue(key, out var m))
                    {
                        m = new ModelUsage { InstanceId = instanceId ?? "", Provider = sp.Provider ?? "", Model = sp.Model ?? "" };
                        map[key] = m;
                    }
                    m.Requests++;
                    m.Totals.UncachedInput += sp.UncachedInput;
                    m.Totals.CacheRead += sp.CacheRead;
                    m.Totals.CacheWrite += sp.CacheWrite;
                    m.Totals.Output += sp.Output;
                    if (sp.TimeMs > 0)
                    {
                        if (m.FirstMs == 0 || sp.TimeMs < m.FirstMs) m.FirstMs = sp.TimeMs;
                        if (sp.TimeMs > m.LastMs) m.LastMs = sp.TimeMs;
                        string day = DateTimeOffset.FromUnixTimeMilliseconds(sp.TimeMs)
                            .ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                        if (!m.Daily.TryGetValue(day, out var db))
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
            }
            return map.Values.OrderByDescending(m => m.Totals.Total).ToList();
        }

        private static byte[] Decompress(byte[] compressed)
        {
            using var input = new MemoryStream(compressed);
            using var zs = new ZstdSharp.DecompressionStream(input);
            using var output = new MemoryStream();
            zs.CopyTo(output);
            return output.ToArray();
        }

        /// <summary>压缩辅助（自检 fixture 用）：JSONL → zstd 字节。</summary>
        public static byte[] CompressForTest(string text)
        {
            byte[] raw = Encoding.UTF8.GetBytes(text);
            using var output = new MemoryStream();
            using (var zs = new ZstdSharp.CompressionStream(output))
            {
                zs.Write(raw, 0, raw.Length);
            }
            return output.ToArray();
        }

        // ---------------- JSON 安全取值 ----------------

        private static long L(JsonElement o, string name)
        {
            if (o.ValueKind != JsonValueKind.Object) return 0;
            if (!o.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Number) return 0;
            if (v.TryGetInt64(out long l)) return l;
            return (long)v.GetDouble();
        }

        /// <summary>优先取 prefer 字段（存在且为数字即用，哪怕 0），否则回退 fallback。</summary>
        private static long LPrefer(JsonElement o, string prefer, string fallback)
        {
            if (o.ValueKind == JsonValueKind.Object && o.TryGetProperty(prefer, out var v) &&
                v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long l))
                return l;
            return L(o, fallback);
        }

        private static string S(JsonElement o, string name)
        {
            if (o.ValueKind != JsonValueKind.Object) return "";
            if (!o.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String) return "";
            return v.GetString() ?? "";
        }

        private static long ParseLong(string s)
        {
            return long.TryParse((s ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long v) ? v : 0;
        }
    }
}
