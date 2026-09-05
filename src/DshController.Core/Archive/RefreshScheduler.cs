// ============================================================================
//  RefreshScheduler — 档案采集的统一调度（重构 2.0 / P1）
//
//  取代原来两个面板各自常驻的 1 秒 DispatcherQueueTimer（页面隐藏也照跑）。
//  现在全应用只有这一个循环，且：
//    - 按分面各自的 TTL 判定是否到期（RefreshPolicy）；
//    - 当前可见实例优先，其余排后；
//    - 全局并发上限 + WSL 单独更严的上限（wsl.exe 往返昂贵）；
//    - 窗口不可见时 liveness 自动降到后台间隔；
//    - 全程可取消，落盘走档案服务的去抖 flush。
//  RunOnceAsync 是纯粹的"一次调度评估"，可用假时钟与假采集器离线单测。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DshController.Core.Abstractions;

namespace DshController.Core.Archive
{
    public sealed class RefreshScheduler : IDisposable
    {
        private readonly ArchiveService _archive;
        private readonly Func<IReadOnlyList<InstanceDef>> _defs;
        private readonly Func<AppSettings> _settings;
        private readonly List<IFacetCollector> _collectors;
        private readonly IClock _clock;

        private CancellationTokenSource _cts;
        private Task _loop;

        public RefreshScheduler(ArchiveService archive,
            Func<IReadOnlyList<InstanceDef>> defsProvider,
            Func<AppSettings> settingsProvider,
            IEnumerable<IFacetCollector> collectors,
            IClock clock = null)
        {
            _archive = archive ?? throw new ArgumentNullException(nameof(archive));
            _defs = defsProvider ?? (() => new List<InstanceDef>());
            _settings = settingsProvider ?? (() => new AppSettings());
            _collectors = new List<IFacetCollector>(collectors ?? new List<IFacetCollector>());
            _clock = clock ?? SystemClock.Instance;
        }

        /// <summary>当前可见页面对应的实例（优先采集）。</summary>
        public string VisibleInstanceId { get; set; } = "";

        /// <summary>窗口是否处于前台（false 时 liveness 走后台间隔）。</summary>
        public bool Foreground { get; set; } = true;

        /// <summary>调度评估周期。</summary>
        public TimeSpan TickInterval { get; set; } = TimeSpan.FromSeconds(1);

        public bool IsRunning => _loop != null && !_loop.IsCompleted;

        // ==================== 循环 ====================

        public void Start()
        {
            if (IsRunning) return;
            _cts = new CancellationTokenSource();
            CancellationToken ct = _cts.Token;
            _loop = Task.Run(async () =>
            {
                while (!ct.IsCancellationRequested)
                {
                    try { await RunOnceAsync(ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                    catch
                    {
                        // 理由: 单次调度失败不能让整个循环退出；具体失败已记进各分面的 error
                    }
                    try { await Task.Delay(TickInterval, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                }
            }, ct);
        }

        public void Stop()
        {
            try { _cts?.Cancel(); }
            catch { /* 理由: 已释放的 CTS 取消无副作用 */ }
            try { _loop?.Wait(TimeSpan.FromSeconds(3)); }
            catch { /* 理由: 等待超时/被取消都只影响退出速度，不影响正确性 */ }
            _archive.FlushDirty();
            _loop = null;
        }

        // ==================== 一次调度评估 ====================

        /// <summary>
        /// 评估一轮：挑出到期的 (实例, 分面) 组合，按预算并发采集，最后落盘。
        /// 返回本轮实际发起的采集次数。
        /// </summary>
        public async Task<int> RunOnceAsync(CancellationToken ct = default)
        {
            AppSettings settings = _settings() ?? new AppSettings();
            RefreshPolicy policy = settings.Refresh ?? new RefreshPolicy();
            IReadOnlyList<InstanceDef> defs = _defs() ?? new List<InstanceDef>();

            var due = new List<WorkItem>();
            foreach (InstanceDef def in defs)
            {
                if (def == null || string.IsNullOrEmpty(def.Id)) continue;
                InstanceContext ctx = BuildContext(def, settings);
                bool visible = string.Equals(def.Id, VisibleInstanceId, StringComparison.OrdinalIgnoreCase);
                foreach (IFacetCollector c in _collectors)
                {
                    TimeSpan? ttl = policy.TtlFor(c.Facet, ctx.IsRunning, Foreground && visible);
                    if (!_archive.IsDue(def.Id, c.Facet, ttl)) continue;
                    due.Add(new WorkItem
                    {
                        Ctx = ctx,
                        Collector = c,
                        Ttl = ttl,
                        Priority = (visible ? 0 : 10) + (c.Facet == FacetNames.Liveness ? 0 : 1)
                    });
                }
            }
            if (due.Count == 0)
            {
                _archive.FlushDirty();
                return 0;
            }

            due.Sort((a, b) => a.Priority.CompareTo(b.Priority));

            using (var globalGate = new SemaphoreSlim(policy.EffectiveMaxConcurrent))
            using (var wslGate = new SemaphoreSlim(policy.EffectiveWslMaxConcurrent))
            {
                var tasks = new List<Task>();
                foreach (WorkItem item in due)
                {
                    if (ct.IsCancellationRequested) break;
                    tasks.Add(RunWorkAsync(item, globalGate, wslGate, ct));
                }
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }

            _archive.FlushDirty();
            return due.Count;
        }

        private async Task RunWorkAsync(WorkItem item, SemaphoreSlim globalGate, SemaphoreSlim wslGate,
            CancellationToken ct)
        {
            bool usesWsl = item.Collector.UsesWsl(item.Ctx);
            await globalGate.WaitAsync(ct).ConfigureAwait(false);
            if (usesWsl) await wslGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await _archive.CollectAsync(item.Ctx, item.Collector, item.Ttl, force: false, ct)
                    .ConfigureAwait(false);
            }
            finally
            {
                if (usesWsl) wslGate.Release();
                globalGate.Release();
            }
        }

        /// <summary>手动刷新：忽略 TTL 强制采集（界面的"刷新"按钮走这里）。</summary>
        public async Task<FacetSnapshot> RefreshNowAsync(string instanceId, string facet,
            CancellationToken ct = default)
        {
            AppSettings settings = _settings() ?? new AppSettings();
            InstanceDef def = FindDef(instanceId);
            IFacetCollector collector = FindCollector(facet);
            if (def == null || collector == null) return new FacetSnapshot();

            FacetSnapshot snapshot = await _archive
                .CollectAsync(BuildContext(def, settings), collector, ttl: null, force: true, ct)
                .ConfigureAwait(false);
            _archive.FlushDirty();
            return snapshot;
        }

        /// <summary>事件驱动的失效（实例启动成功、插件装卸、设置变更…）。</summary>
        public void Invalidate(string instanceId, params string[] facets)
        {
            _archive.Invalidate(instanceId, facets);
        }

        // ==================== 小件 ====================

        private InstanceContext BuildContext(InstanceDef def, AppSettings settings)
        {
            bool running = false;
            FacetSnapshot liveness = _archive.Snapshot(def.Id, FacetNames.Liveness);
            if (liveness.TryGetData(out Dictionary<string, System.Text.Json.JsonElement> data) &&
                data.TryGetValue("running", out System.Text.Json.JsonElement el))
            {
                running = el.ValueKind == System.Text.Json.JsonValueKind.True;
            }
            return new InstanceContext { Def = def, Settings = settings, IsRunning = running };
        }

        private InstanceDef FindDef(string id)
        {
            foreach (InstanceDef d in _defs() ?? new List<InstanceDef>())
                if (d != null && string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase)) return d;
            return null;
        }

        private IFacetCollector FindCollector(string facet)
        {
            foreach (IFacetCollector c in _collectors)
                if (string.Equals(c.Facet, facet, StringComparison.OrdinalIgnoreCase)) return c;
            return null;
        }

        public void Dispose()
        {
            Stop();
            try { _cts?.Dispose(); }
            catch { /* 理由: 重复释放无副作用 */ }
        }

        private sealed class WorkItem
        {
            public InstanceContext Ctx;
            public IFacetCollector Collector;
            public TimeSpan? Ttl;
            public int Priority;
        }
    }
}
