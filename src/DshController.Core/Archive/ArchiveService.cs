// ============================================================================
//  ArchiveService — 档案的读写、采集编排与新鲜度判定（重构 2.0 / P1）
//
//  界面只跟这个类打交道：需要"某实例的某类信息"就问它要快照，永远秒回
//  （命中内存/磁盘档案），需要更新时由调度器或用户手动触发采集。
//
//  四条不变量：
//    1. 单飞：同一 (实例, 分面) 同时只有一次采集在跑，重复请求合并到同一个 Task；
//    2. 失败不覆盖：Failed/Skipped 只更新状态与错误，data 与 lastGoodAt 保留；
//       Ok/Empty 才写数据（Empty 也是有效结论——"确实没有"，否则删完插件仍显示旧列表）；
//    3. 删除不销毁：实例从清单消失只写 retiredAt，档案文件永久保留；
//    4. 落盘去抖：写操作打脏标记，由 FlushDirty() 统一落盘（调度器周期调用 + 退出时兜底）。
// ============================================================================

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DshController.Core.Abstractions;
using DshController.Core.Storage;

namespace DshController.Core.Archive
{
    public sealed class FacetChangedEventArgs : EventArgs
    {
        public string ArchiveId { get; set; } = "";
        public string Facet { get; set; } = "";
        public FacetSnapshot Snapshot { get; set; }
    }

    public sealed class ArchiveService
    {
        private readonly ArchiveStore _store;
        private readonly IClock _clock;
        private readonly ConcurrentDictionary<string, InstanceArchive> _cache =
            new ConcurrentDictionary<string, InstanceArchive>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, Task<FacetSnapshot>> _inflight =
            new ConcurrentDictionary<string, Task<FacetSnapshot>>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, byte> _dirty =
            new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        private readonly object _writeLock = new object();

        public ArchiveService(ArchiveStore store = null, IClock clock = null)
        {
            _store = store ?? new ArchiveStore();
            _clock = clock ?? SystemClock.Instance;
        }

        public event EventHandler<FacetChangedEventArgs> Changed;

        public ArchiveStore Store => _store;

        // ==================== 档案取用 ====================

        /// <summary>取档案（含已退役）；没有则返回 null。</summary>
        public InstanceArchive Get(string archiveId)
        {
            if (string.IsNullOrEmpty(archiveId)) return null;
            return _cache.GetOrAdd(archiveId, id => _store.Read(id));
        }

        /// <summary>取档案，没有就按当前实例定义新建一份（不落盘，等去抖 flush）。</summary>
        public InstanceArchive GetOrCreate(InstanceDef def)
        {
            if (def == null || string.IsNullOrEmpty(def.Id)) return null;
            InstanceArchive archive = Get(def.Id);
            if (archive != null) return archive;
            archive = InstanceArchive.CreateFor(def, _clock.UtcNow);
            _cache[def.Id] = archive;
            MarkDirty(def.Id);
            return archive;
        }

        /// <summary>全部档案（磁盘里的历史档案 + 内存里尚未落盘的）。</summary>
        public IReadOnlyList<InstanceArchive> All()
        {
            var map = new Dictionary<string, InstanceArchive>(StringComparer.OrdinalIgnoreCase);
            foreach (string id in _store.ListIds())
            {
                InstanceArchive a = Get(id);
                if (a != null) map[a.ArchiveId] = a;
            }
            foreach (KeyValuePair<string, InstanceArchive> kv in _cache)
                if (kv.Value != null) map[kv.Key] = kv.Value;
            var list = new List<InstanceArchive>(map.Values);
            list.Sort((x, y) => string.Compare(x.DisplayName, y.DisplayName, StringComparison.OrdinalIgnoreCase));
            return list;
        }

        public FacetSnapshot Snapshot(string archiveId, string facet)
        {
            InstanceArchive a = Get(archiveId);
            return a == null ? new FacetSnapshot() : a.Facet(facet);
        }

        // ==================== 与实例清单同步 ====================

        /// <summary>
        /// 用当前实例清单刷新档案的身份信息：
        /// 新实例建档、已有实例镜像最新定义、清单里消失的实例标记退役（文件保留）。
        /// </summary>
        public void SyncFromRegistry(IEnumerable<InstanceDef> defs)
        {
            var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (defs != null)
            {
                foreach (InstanceDef def in defs)
                {
                    if (def == null || string.IsNullOrEmpty(def.Id)) continue;
                    live.Add(def.Id);
                    MirrorIdentity(def);
                }
            }
            foreach (string id in _store.ListIds())
            {
                if (live.Contains(id)) continue;
                InstanceArchive a = Get(id);
                if (a != null && !a.IsRetired) Retire(id);
            }
        }

        /// <summary>把实例定义镜像进档案（身份分面零成本，永远新鲜）。</summary>
        public void MirrorIdentity(InstanceDef def)
        {
            if (def == null || string.IsNullOrEmpty(def.Id)) return;
            InstanceArchive archive = GetOrCreate(def);
            if (archive == null) return;

            DateTime now = _clock.UtcNow;
            archive.DisplayName = string.IsNullOrWhiteSpace(def.Name) ? def.Id : def.Name;
            archive.Runtime = def.IsWsl ? "wsl" : "windows";

            // 实例被删后又以同 id 重建：结束旧的一代，开新的一代，历史定义保留
            if (archive.IsRetired)
            {
                archive.RetiredAt = null;
                archive.Epochs.Add(new ArchiveEpoch { From = now, Def = def });
            }
            else
            {
                ArchiveEpoch current = archive.CurrentEpoch;
                if (current == null) archive.Epochs.Add(new ArchiveEpoch { From = now, Def = def });
                else current.Def = def;
            }

            var data = new Dictionary<string, object>
            {
                ["id"] = def.Id,
                ["name"] = def.Name ?? "",
                ["runtime"] = def.IsWsl ? "wsl" : "windows",
                ["host"] = def.Host ?? "",
                ["port"] = def.Port,
                ["home"] = def.Home ?? "",
                ["wslDistro"] = def.WslDistro ?? "",
                ["wslHome"] = def.WslHome ?? "",
                ["workspace"] = def.Workspace ?? "",
                ["pinnedHarnessVersion"] = def.HarnessVersion ?? "",
                // 改版·启动时间打点：启动成功路径写清单（InstanceManager.ApplyStartStamp），
                // 镜像随清单带出；round-trip UTC ISO 字符串，空 = 从未启动
                ["lastStartedAt"] = def.LastStartedAt?.ToString("O") ?? ""
            };
            ApplyResult(archive, FacetNames.Identity, FacetResult.Ok(data, "registry"), 0);
        }

        /// <summary>实例从清单中删除：只写退役时间，档案文件永久保留。</summary>
        public void Retire(string archiveId)
        {
            InstanceArchive archive = Get(archiveId);
            if (archive == null || archive.IsRetired) return;
            DateTime now = _clock.UtcNow;
            archive.RetiredAt = now;
            ArchiveEpoch current = archive.CurrentEpoch;
            if (current != null && !current.To.HasValue) current.To = now;
            MarkDirty(archiveId);
        }

        // ==================== 新鲜度与采集 ====================

        /// <summary>ttl 为 null = 不自动刷新；Zero = 永远新鲜（身份分面）。</summary>
        public bool IsDue(string archiveId, string facet, TimeSpan? ttl)
        {
            if (!ttl.HasValue) return false;
            FacetSnapshot s = Snapshot(archiveId, facet);
            if (!s.CollectedAt.HasValue) return true;
            if (ttl.Value == TimeSpan.Zero) return false;
            return _clock.UtcNow - s.CollectedAt.Value >= ttl.Value;
        }

        /// <summary>
        /// 让某些分面立即过期（事件驱动：实例启动成功、插件装卸、改了 HOME/发行版…）。
        /// 只清"采集时间"，数据与 lastGoodAt 保留——界面在新数据到达前仍显示旧值。
        /// facets 为空 = 该实例的全部分面。
        /// </summary>
        public void Invalidate(string archiveId, params string[] facets)
        {
            InstanceArchive archive = Get(archiveId);
            if (archive == null) return;
            IEnumerable<string> targets = facets == null || facets.Length == 0
                ? new List<string>(archive.Facets.Keys) : facets;
            foreach (string facet in targets)
            {
                if (string.IsNullOrEmpty(facet)) continue;
                if (!archive.Facets.TryGetValue(facet, out FacetSnapshot s) || s == null) continue;
                s.CollectedAt = null;
            }
        }

        /// <summary>
        /// 采集一个分面。force=false 且数据仍新鲜时直接返回缓存快照（零成本）。
        /// 同一 (实例, 分面) 的并发调用合并为一次真实采集。
        /// </summary>
        public Task<FacetSnapshot> CollectAsync(InstanceContext ctx, IFacetCollector collector,
            TimeSpan? ttl, bool force, CancellationToken ct = default)
        {
            if (ctx?.Def == null || collector == null) return Task.FromResult(new FacetSnapshot());
            string id = ctx.Def.Id;
            GetOrCreate(ctx.Def);
            if (!force && !IsDue(id, collector.Facet, ttl))
                return Task.FromResult(Snapshot(id, collector.Facet));

            string key = id + "|" + collector.Facet;
            var tcs = new TaskCompletionSource<FacetSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<FacetSnapshot> shared = _inflight.GetOrAdd(key, tcs.Task);
            if (!ReferenceEquals(shared, tcs.Task)) return shared;   // 已有同样的采集在跑，合并过去

            // 先把占位 Task 放进字典，再启动真正的采集；清理放在采集之后，
            // 保证"同步完成的采集"不会出现清理早于登记、把完成态 Task 永久留在字典里的竞态。
            _ = PumpAsync(key, tcs, ctx, collector, ct);
            return tcs.Task;
        }

        private async Task PumpAsync(string key, TaskCompletionSource<FacetSnapshot> tcs,
            InstanceContext ctx, IFacetCollector collector, CancellationToken ct)
        {
            try
            {
                FacetSnapshot snapshot = await RunCollectAsync(ctx, collector, ct).ConfigureAwait(false);
                tcs.TrySetResult(snapshot);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);   // RunCollectAsync 内部已兜底，这里只是防御
            }
            finally
            {
                _inflight.TryRemove(key, out _);
            }
        }

        private async Task<FacetSnapshot> RunCollectAsync(InstanceContext ctx,
            IFacetCollector collector, CancellationToken ct)
        {
            InstanceArchive archive = GetOrCreate(ctx.Def);
            var sw = Stopwatch.StartNew();
            FacetResult result;
            try
            {
                if (!collector.CanRun(ctx, out string skipReason))
                {
                    result = FacetResult.Skipped(skipReason);
                }
                else
                {
                    result = await collector.CollectAsync(ctx, ct).ConfigureAwait(false) ??
                             FacetResult.Failed("采集器返回空结果");
                }
            }
            catch (OperationCanceledException)
            {
                result = FacetResult.Skipped("已取消");
            }
            catch (Exception ex)
            {
                result = FacetResult.Failed(ex.Message);
            }

            sw.Stop();
            FacetSnapshot snapshot = ApplyResult(archive, collector.Facet, result, (int)sw.ElapsedMilliseconds,
                collector.Persist);
            RaiseChanged(archive.ArchiveId, collector.Facet, snapshot);
            return snapshot;
        }

        /// <summary>把采集结果并入档案（含"失败不覆盖"规则）；返回新的快照。</summary>
        public FacetSnapshot ApplyResult(InstanceArchive archive, string facet, FacetResult result,
            int durationMs, bool persist = true)
        {
            if (archive == null || string.IsNullOrEmpty(facet) || result == null) return new FacetSnapshot();
            DateTime now = _clock.UtcNow;
            FacetSnapshot snapshot = archive.Facet(facet).Clone();
            snapshot.CollectedAt = now;
            snapshot.DurationMs = durationMs;

            switch (result.Outcome)
            {
                case FacetOutcome.Ok:
                case FacetOutcome.Empty:
                    snapshot.Status = result.Outcome == FacetOutcome.Ok ? FacetStatus.Ok : FacetStatus.Empty;
                    snapshot.Data = Serialize(result.Data);
                    snapshot.LastGoodAt = now;
                    snapshot.Error = "";
                    snapshot.SkipReason = "";
                    snapshot.Source = result.Source ?? "";
                    break;
                case FacetOutcome.Failed:
                    // 保留 data 与 lastGoodAt：界面继续显示上次的真实数据 + 一个"上次更新于"标注
                    snapshot.Status = FacetStatus.Failed;
                    snapshot.Error = result.Error ?? "";
                    snapshot.SkipReason = "";
                    break;
                default:
                    snapshot.Status = FacetStatus.Skipped;
                    snapshot.SkipReason = result.SkipReason ?? "";
                    snapshot.Error = "";
                    break;
            }

            archive.Facets[facet] = snapshot;
            if (persist) MarkDirty(archive.ArchiveId);
            return snapshot;
        }

        private static JsonElement? Serialize(object data)
        {
            if (data == null) return null;
            try
            {
                using (JsonDocument doc = JsonDocument.Parse(
                    JsonSerializer.Serialize(data, JsonStore.WriteOptions)))
                {
                    return doc.RootElement.Clone();
                }
            }
            catch
            {
                return null;   // 理由: 采集器返回了不可序列化的对象——按无数据处理，状态与错误仍会记录
            }
        }

        // ==================== 落盘 ====================

        public void MarkDirty(string archiveId)
        {
            if (!string.IsNullOrEmpty(archiveId)) _dirty[archiveId] = 1;
        }

        public bool HasPendingWrites => !_dirty.IsEmpty;

        /// <summary>把脏档案落盘（调度器周期调用，退出前再兜底一次）。返回写入份数。</summary>
        public int FlushDirty()
        {
            var ids = new List<string>(_dirty.Keys);
            int written = 0;
            lock (_writeLock)
            {
                foreach (string id in ids)
                {
                    InstanceArchive archive = Get(id);
                    if (archive == null) { _dirty.TryRemove(id, out _); continue; }
                    if (_store.Write(archive)) written++;
                    _dirty.TryRemove(id, out _);
                }
            }
            return written;
        }

        private void RaiseChanged(string archiveId, string facet, FacetSnapshot snapshot)
        {
            EventHandler<FacetChangedEventArgs> handler = Changed;
            if (handler == null) return;
            try
            {
                handler(this, new FacetChangedEventArgs
                {
                    ArchiveId = archiveId,
                    Facet = facet,
                    Snapshot = snapshot
                });
            }
            catch
            {
                // 理由: 订阅方（界面）异常不得影响采集流程，异常在其自身作用域处理
            }
        }
    }
}
