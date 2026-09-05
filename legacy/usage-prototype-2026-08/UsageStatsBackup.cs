// ============================================================================
//  UsageStatsBackup — 用量快照的本地持久化
//
//  文件位置固定为应用根目录：AppContext.BaseDirectory\usage-backup.json。
//  快照保存规范化后的会话、模型和每日数据，展示时不再依赖实例 HOME；
//  这样即使实例随后被删除，历史用量仍可在「用量统计」中查看。
//
//  备份只由控制器写入应用目录，不写回 DSH_HOME，也不使用 LocalAppData、Temp
//  或网络存储。源数据仍由 UsageStats 从实例 HOME 读取。
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace DshController.Core
{
    public sealed class UsageBackupFile
    {
        [JsonPropertyName("version")]
        public int Version { get; set; } = 1;

        [JsonPropertyName("updatedAtUtc")]
        public DateTime UpdatedAtUtc { get; set; }

        [JsonPropertyName("instances")]
        public List<UsageBackupInstance> Instances { get; set; } = new List<UsageBackupInstance>();
    }

    public sealed class UsageBackupInstance
    {
        [JsonPropertyName("definition")]
        public InstanceDef Definition { get; set; } = new InstanceDef();

        [JsonPropertyName("status")]
        public InstanceDataStatus Status { get; set; } = InstanceDataStatus.Pending;

        [JsonPropertyName("statusText")]
        public string StatusText { get; set; } = "";

        [JsonPropertyName("homeDisplay")]
        public string HomeDisplay { get; set; } = "";

        [JsonPropertyName("modelsComplete")]
        public bool ModelsComplete { get; set; }

        [JsonPropertyName("modelScanError")]
        public string ModelScanError { get; set; } = "";

        [JsonPropertyName("snapshotAtUtc")]
        public DateTime SnapshotAtUtc { get; set; }

        [JsonPropertyName("sessions")]
        public List<UsageBackupSession> Sessions { get; set; } = new List<UsageBackupSession>();

        [JsonPropertyName("models")]
        public List<UsageBackupModel> Models { get; set; } = new List<UsageBackupModel>();
    }

    public sealed class UsageBackupSession
    {
        [JsonPropertyName("instanceId")]
        public string InstanceId { get; set; } = "";

        [JsonPropertyName("instanceName")]
        public string InstanceName { get; set; } = "";

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

    public sealed class UsageBackupModel
    {
        [JsonPropertyName("instanceId")]
        public string InstanceId { get; set; } = "";

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

        [JsonPropertyName("daily")]
        public SortedDictionary<string, TokenBuckets> Daily { get; set; } =
            new SortedDictionary<string, TokenBuckets>(StringComparer.Ordinal);

        [JsonPropertyName("dailyRequests")]
        public SortedDictionary<string, long> DailyRequests { get; set; } =
            new SortedDictionary<string, long>(StringComparer.Ordinal);
    }

    /// <summary>
    /// 用量备份读写与快照转换。所有方法都以应用目录文件为唯一持久化目标。
    /// </summary>
    public static class UsageStatsBackupStore
    {
        private static readonly object Gate = new object();
        private static string _filePathOverride;

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            PropertyNameCaseInsensitive = true
        };

        public static string FilePath
        {
            get
            {
                lock (Gate)
                {
                    return string.IsNullOrEmpty(_filePathOverride)
                        ? Path.Combine(AppContext.BaseDirectory, "usage-backup.json")
                        : _filePathOverride;
                }
            }
        }

        // 仅供同程序集离线自检切换到临时文件；正常运行始终使用应用根目录。
        internal static IDisposable UseFilePathForTest(string path)
        {
            lock (Gate)
            {
                string previous = _filePathOverride;
                _filePathOverride = path;
                return new PathOverride(previous);
            }
        }

        private sealed class PathOverride : IDisposable
        {
            private readonly string _previous;
            private bool _disposed;

            public PathOverride(string previous) { _previous = previous; }

            public void Dispose()
            {
                if (_disposed) return;
                lock (Gate)
                {
                    _filePathOverride = _previous;
                    _disposed = true;
                }
            }
        }

        /// <summary>读取当前快照；文件不存在或损坏时返回空快照，不阻断应用启动。</summary>
        public static UsageBackupFile Read()
        {
            lock (Gate) return ReadUnsafe();
        }

        /// <summary>
        /// 返回备份中已不在当前注册表里的实例定义，供用量页显示历史备份选项。
        /// </summary>
        public static List<InstanceDef> HistoricalDefinitions(IEnumerable<InstanceDef> currentDefinitions)
        {
            var currentIds = new HashSet<string>(
                (currentDefinitions ?? Enumerable.Empty<InstanceDef>())
                    .Where(x => x != null && !string.IsNullOrWhiteSpace(x.Id))
                    .Select(x => x.Id), StringComparer.OrdinalIgnoreCase);
            var result = new List<InstanceDef>();
            lock (Gate)
            {
                foreach (UsageBackupInstance snapshot in ReadUnsafe().Instances ?? new List<UsageBackupInstance>())
                {
                    InstanceDef def = snapshot?.Definition;
                    if (def == null || string.IsNullOrWhiteSpace(def.Id) || currentIds.Contains(def.Id)) continue;
                    result.Add(CloneDefinition(def));
                }
            }
            return result.OrderBy(x => x.IsWsl).ThenBy(x => x.Name, StringComparer.CurrentCulture).ToList();
        }

        /// <summary>
        /// 从应用目录快照构造页面展示数据。scopeId=null 表示当前实例 + 历史实例全部汇总。
        /// </summary>
        public static List<InstanceUsage> LoadForScope(string scopeId, IEnumerable<InstanceDef> currentDefinitions)
        {
            var current = (currentDefinitions ?? Enumerable.Empty<InstanceDef>())
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.Id)).ToList();
            lock (Gate)
            {
                var file = ReadUnsafe();
                var map = (file.Instances ?? new List<UsageBackupInstance>())
                    .Where(x => x?.Definition != null && !string.IsNullOrWhiteSpace(x.Definition.Id))
                    .GroupBy(x => x.Definition.Id, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);

                if (!string.IsNullOrWhiteSpace(scopeId))
                {
                    if (map.TryGetValue(scopeId, out UsageBackupInstance one))
                        return new List<InstanceUsage> { ToUsage(one) };
                    InstanceDef selected = current.FirstOrDefault(x =>
                        string.Equals(x.Id, scopeId, StringComparison.OrdinalIgnoreCase));
                    return selected == null
                        ? new List<InstanceUsage>()
                        : new List<InstanceUsage> { EmptyUsage(selected) };
                }

                var result = new List<InstanceUsage>();
                var currentIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (InstanceDef def in current.OrderBy(x => x.IsWsl).ThenBy(x => x.Name, StringComparer.CurrentCulture))
                {
                    currentIds.Add(def.Id);
                    result.Add(map.TryGetValue(def.Id, out UsageBackupInstance snapshot)
                        ? ToUsage(snapshot)
                        : EmptyUsage(def));
                }

                // 追加历史实例：不从当前注册表删除快照，保证实例删除后仍可查阅。
                foreach (UsageBackupInstance snapshot in map.Values
                    .Where(x => !currentIds.Contains(x.Definition.Id))
                    .OrderBy(x => x.Definition.IsWsl)
                    .ThenBy(x => x.Definition.Name, StringComparer.CurrentCulture))
                {
                    result.Add(ToUsage(snapshot));
                }
                return result;
            }
        }

        /// <summary>
        /// 合并实例快照并原子写回应用目录。读取失败/发行版未运行时保留旧数据，避免覆盖历史记录。
        /// </summary>
        public static bool Merge(IEnumerable<InstanceUsage> instances, Action<string> log = null)
        {
            try
            {
                lock (Gate)
                {
                    UsageBackupFile file = ReadUnsafe();
                    var map = (file.Instances ?? new List<UsageBackupInstance>())
                        .Where(x => x?.Definition != null && !string.IsNullOrWhiteSpace(x.Definition.Id))
                        .GroupBy(x => x.Definition.Id, StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);
                    DateTime now = DateTime.UtcNow;

                    foreach (InstanceUsage usage in instances ?? Enumerable.Empty<InstanceUsage>())
                    {
                        string id = usage?.Def?.Id?.Trim();
                        if (string.IsNullOrEmpty(id)) continue;
                        map.TryGetValue(id, out UsageBackupInstance old);
                        if (!ShouldReplace(usage, old)) continue;
                        UsageBackupInstance incoming = FromUsage(usage, now);
                        // projcache 成功但会话日志暂时不可读时，只更新会话汇总；
                        // 保留上一次完整的模型/按天数据，避免一次临时读失败清空图表。
                        if (old != null && !usage.ModelsComplete && old.ModelsComplete &&
                            (incoming.Models == null || incoming.Models.Count == 0))
                        {
                            incoming.Models = old.Models ?? new List<UsageBackupModel>();
                            incoming.ModelsComplete = true;
                            incoming.ModelScanError = old.ModelScanError ?? "";
                        }
                        map[id] = incoming;
                    }

                    file.Version = Math.Max(1, file.Version);
                    file.UpdatedAtUtc = now;
                    file.Instances = map.Values
                        .OrderBy(x => x.Definition.IsWsl)
                        .ThenBy(x => x.Definition.Name, StringComparer.CurrentCulture)
                        .ToList();
                    // 即使本次内容没有变化，也更新时间戳并原子重写，确保“手动刷新/
                    // 实例关闭”确实完成了一次备份动作，而不是只更新内存。
                    WriteUnsafe(file);
                }
                return true;
            }
            catch (Exception ex)
            {
                log?.Invoke("[用量] 本地备份写入失败（" + FilePath + "): " + ex.Message);
                return false;
            }
        }

        /// <summary>从单个实例 HOME 读取完整数据并合并到应用目录快照。</summary>
        public static async Task<bool> UpdateInstanceAsync(InstanceDef def, AppSettings settings,
            Action<string> log, CancellationToken ct)
        {
            if (def == null) return false;
            try
            {
                InstanceUsage usage = await UsageStats.LoadInstanceAsync(
                    def, settings, includeModels: true, log, ct).ConfigureAwait(false);
                return Merge(new[] { usage }, log);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                log?.Invoke("[用量] " + def.Name + " 本地备份更新失败: " + ex.Message);
                return false;
            }
        }

        private static bool ShouldReplace(InstanceUsage usage, UsageBackupInstance old)
        {
            if (usage == null || usage.Def == null) return false;
            if (old == null) return true;
            bool newHasData = usage.Sessions.Count > 0 || usage.Models.Count > 0 || usage.Totals.Total > 0;
            bool oldHasData = (old.Sessions?.Count ?? 0) > 0 || (old.Models?.Count ?? 0) > 0;
            if (newHasData) return true;
            // 空实例可以被新的空快照刷新；已有真实数据时，失败/离线/短暂空读不能覆盖它。
            return !oldHasData && usage.Status == InstanceDataStatus.Empty;
        }

        private static UsageBackupFile ReadUnsafe()
        {
            try
            {
                if (!File.Exists(FilePath)) return new UsageBackupFile();
                UsageBackupFile file = JsonSerializer.Deserialize<UsageBackupFile>(
                    File.ReadAllText(FilePath, Encoding.UTF8), JsonOptions);
                if (file == null) return new UsageBackupFile();
                if (file.Instances == null) file.Instances = new List<UsageBackupInstance>();
                return file;
            }
            catch
            {
                return new UsageBackupFile();
            }
        }

        private static void WriteUnsafe(UsageBackupFile file)
        {
            string tmp = FilePath + ".tmp";
            try
            {
                File.WriteAllText(tmp, JsonSerializer.Serialize(file, JsonOptions), new UTF8Encoding(false));
                File.Move(tmp, FilePath, overwrite: true);
            }
            catch
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                throw;
            }
        }

        private static UsageBackupInstance FromUsage(InstanceUsage source, DateTime now)
        {
            var snapshot = new UsageBackupInstance
            {
                Definition = CloneDefinition(source.Def),
                Status = source.Status,
                StatusText = source.StatusText ?? "",
                HomeDisplay = source.HomeDisplay ?? "",
                ModelsComplete = source.ModelsComplete,
                ModelScanError = source.ModelScanError ?? "",
                SnapshotAtUtc = now
            };

            foreach (SessionUsage s in source.Sessions ?? new List<SessionUsage>())
            {
                snapshot.Sessions.Add(new UsageBackupSession
                {
                    InstanceId = s.InstanceId ?? "",
                    InstanceName = s.InstanceName ?? "",
                    SessionId = s.SessionId ?? "",
                    Title = s.Title ?? "",
                    CreatedAtMs = s.CreatedAtMs,
                    Cwd = s.Cwd ?? "",
                    Turns = s.Turns,
                    Totals = s.Totals?.Clone() ?? new TokenBuckets()
                });
            }

            foreach (ModelUsage m in source.Models ?? new List<ModelUsage>())
            {
                var model = new UsageBackupModel
                {
                    InstanceId = m.InstanceId ?? "",
                    Provider = m.Provider ?? "",
                    Model = m.Model ?? "",
                    Requests = m.Requests,
                    FirstMs = m.FirstMs,
                    LastMs = m.LastMs,
                    Totals = m.Totals?.Clone() ?? new TokenBuckets()
                };
                foreach (var kv in m.Daily ?? new SortedDictionary<string, TokenBuckets>())
                    model.Daily[kv.Key] = kv.Value?.Clone() ?? new TokenBuckets();
                foreach (var kv in m.DailyRequests ?? new SortedDictionary<string, long>())
                    model.DailyRequests[kv.Key] = kv.Value;
                snapshot.Models.Add(model);
            }
            return snapshot;
        }

        private static InstanceUsage ToUsage(UsageBackupInstance snapshot)
        {
            var result = new InstanceUsage
            {
                Def = CloneDefinition(snapshot.Definition),
                Status = snapshot.Status,
                StatusText = snapshot.StatusText ?? "",
                HomeDisplay = snapshot.HomeDisplay ?? "",
                ModelsComplete = snapshot.ModelsComplete,
                ModelScanError = snapshot.ModelScanError ?? ""
            };

            foreach (UsageBackupSession s in snapshot.Sessions ?? new List<UsageBackupSession>())
            {
                var session = new SessionUsage
                {
                    InstanceId = s.InstanceId ?? result.Def.Id,
                    InstanceName = s.InstanceName ?? result.Def.Name,
                    SessionId = s.SessionId ?? "",
                    Title = s.Title ?? "",
                    CreatedAtMs = s.CreatedAtMs,
                    Cwd = s.Cwd ?? "",
                    Turns = s.Turns,
                    Totals = s.Totals?.Clone() ?? new TokenBuckets()
                };
                result.Sessions.Add(session);
                result.Totals.Add(session.Totals);
            }

            foreach (UsageBackupModel m in snapshot.Models ?? new List<UsageBackupModel>())
            {
                var model = new ModelUsage
                {
                    InstanceId = m.InstanceId ?? result.Def.Id,
                    Provider = m.Provider ?? "",
                    Model = m.Model ?? "",
                    Requests = m.Requests,
                    FirstMs = m.FirstMs,
                    LastMs = m.LastMs,
                    Totals = m.Totals?.Clone() ?? new TokenBuckets()
                };
                foreach (var kv in m.Daily ?? new SortedDictionary<string, TokenBuckets>())
                    model.Daily[kv.Key] = kv.Value?.Clone() ?? new TokenBuckets();
                foreach (var kv in m.DailyRequests ?? new SortedDictionary<string, long>())
                    model.DailyRequests[kv.Key] = kv.Value;
                result.Models.Add(model);
            }

            if (result.Sessions.Count > 0 || result.Models.Count > 0 || result.Totals.Total > 0)
            {
                // 有历史数据时，即便源实例已离线/已删除，也按可查看的本地快照处理。
                result.Status = InstanceDataStatus.Ok;
                string stamp = snapshot.SnapshotAtUtc == default(DateTime)
                    ? ""
                    : snapshot.SnapshotAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
                result.StatusText = "本地备份" + (stamp.Length > 0 ? " · " + stamp : "");
            }
            return result;
        }

        private static InstanceUsage EmptyUsage(InstanceDef def)
        {
            return new InstanceUsage
            {
                Def = CloneDefinition(def),
                Status = InstanceDataStatus.Empty,
                StatusText = "尚未生成本地备份",
                HomeDisplay = def.IsWsl ? (def.WslDistro ?? "WSL") : (def.Home ?? "")
            };
        }

        private static InstanceDef CloneDefinition(InstanceDef source)
        {
            if (source == null) return new InstanceDef();
            return new InstanceDef
            {
                Id = source.Id ?? "",
                Name = source.Name ?? "",
                Home = source.Home ?? "",
                Host = source.Host ?? "127.0.0.1",
                Port = source.Port,
                TrustedHosts = source.TrustedHosts == null ? new List<string>() : source.TrustedHosts.ToList(),
                Workspace = source.Workspace ?? "",
                AutoOpenBrowser = source.AutoOpenBrowser,
                StopOnExit = source.StopOnExit,
                CreatedAt = source.CreatedAt,
                LastStartedAt = source.LastStartedAt,
                Runtime = source.Runtime ?? "windows",
                WslDistro = source.WslDistro ?? "",
                WslHome = source.WslHome ?? "",
                HarnessVersion = source.HarnessVersion ?? ""
            };
        }
    }
}
