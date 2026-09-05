// ============================================================================
//  ArchiveHub — 界面侧访问实例档案的唯一入口（重构 2.0 / P1）
//
//  设计意图：面板不再各自去扫 HOME、探版本、起定时器，只跟这里要"档案里的现成数据"
//  （永远秒回），需要更新时由调度器按各自的刷新间隔在后台完成，或由事件/手动强制。
//  这一层同时承担"事件 → 分面失效"的翻译：实例启动成功、插件装卸、设置变更…
// ============================================================================

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DshController.Core;
using DshController.Core.Archive;
using DshController.Core.Archive.Collectors;
using DshController.Core.Usage;
using DshController.ViewModels;

namespace DshController
{
    public sealed class ArchiveHub : IDisposable, DshController.ViewModels.IArchiveFacade
    {
        private readonly InstanceRegistry _registry;

        public ArchiveHub(InstanceRegistry registry)
        {
            _registry = registry;
            Service = new ArchiveService();
            Scheduler = new RefreshScheduler(
                Service,
                () => _registry.Instances,
                () => _registry.Settings,
                new IFacetCollector[]
                {
                    new LivenessCollector(),
                    new HarnessCollector(),
                    new PluginsCollector(),
                    new UsageCollector(),
                    new HomeCollector(),
                    new WslEnvCollector()
                });
        }

        public ArchiveService Service { get; }
        public RefreshScheduler Scheduler { get; }

        /// <summary>实例清单（IArchiveFacade：视图模型据此构建目标下拉）。</summary>
        public IReadOnlyList<InstanceDef> Instances => _registry.Instances;

        /// <summary>让若干分面立即过期（IArchiveFacade）。</summary>
        public void Invalidate(string instanceId, params string[] facets)
        {
            Service.Invalidate(instanceId, facets);
        }

        /// <summary>启动：先把清单镜像进档案（新建/退役），再起后台调度。</summary>
        public void Start()
        {
            SyncRegistry(force: true);
            // 一次性把用量原型（usage-backup.json）里的历史快照导入档案，
            // 其中包含已删除实例的用量——合并主线时不能让这些数据消失
            try
            {
                UsageImportResult r = UsageBackupImport.ImportDefault(Service, _registry.Instances);
                if (r.Imported > 0)
                    ImportNotice = "已从用量原型备份导入 " + r.Imported + " 个实例的历史用量" +
                                   (r.Retired > 0 ? "（其中 " + r.Retired + " 个实例已删除，档案保留）" : "");
            }
            catch (Exception ex)
            {
                ImportNotice = "导入用量原型备份失败：" + ex.Message;
            }
            Scheduler.Start();
        }

        /// <summary>启动时的一次性导入提示（供主窗口写进控制台）。</summary>
        public string ImportNotice { get; private set; } = "";

        private DateTime _lastSyncUtc = DateTime.MinValue;

        /// <summary>
        /// 把实例清单镜像进档案（新建/改名/退役）。会被状态变化频繁触发，因此默认节流；
        /// 实例增删这种必须立刻生效的场景传 force:true。
        /// </summary>
        public void SyncRegistry(bool force = false)
        {
            DateTime now = DateTime.UtcNow;
            if (!force && now - _lastSyncUtc < TimeSpan.FromSeconds(2)) return;
            _lastSyncUtc = now;
            Service.SyncFromRegistry(_registry.Instances);
        }

        /// <summary>当前可见页面聚焦的实例（调度优先级 + liveness 前台节奏）。</summary>
        public void SetVisible(string instanceId)
        {
            Scheduler.VisibleInstanceId = instanceId ?? "";
        }

        // ==================== 读取（永远秒回） ====================

        public FacetSnapshot Snapshot(string instanceId, string facet)
        {
            return Service.Snapshot(instanceId, facet);
        }

        /// <summary>实例 harness 版本；档案里没有就返回空串（界面按"未检测"展示）。</summary>
        public string HarnessVersion(string instanceId)
        {
            FacetSnapshot s = Service.Snapshot(instanceId, FacetNames.Harness);
            if (s.TryGetData(out Dictionary<string, string> data) &&
                data.TryGetValue("version", out string v)) return v ?? "";
            return "";
        }

        /// <summary>档案里是否已有该实例的版本结论（含"探测过但没结果"）。</summary>
        public bool HarnessKnown(string instanceId)
        {
            FacetSnapshot s = Service.Snapshot(instanceId, FacetNames.Harness);
            return s.Status == FacetStatus.Ok || s.Status == FacetStatus.Empty;
        }

        /// <summary>已装插件（按 profile 过滤）；档案里没有对应 profile 的数据时返回 null。</summary>
        public List<InstalledPlugin> PluginsFromArchive(string instanceId, string profile)
        {
            FacetSnapshot s = Service.Snapshot(instanceId, FacetNames.Plugins);
            if (!s.TryGetData(out PluginsFacetData data) || data == null) return null;
            if (!string.Equals(data.Profile ?? "", profile ?? "", StringComparison.OrdinalIgnoreCase)) return null;

            var list = new List<InstalledPlugin>();
            foreach (PluginsFacetItem item in data.Items ?? new List<PluginsFacetItem>())
            {
                list.Add(new InstalledPlugin
                {
                    Pkg = item.Pkg ?? "",
                    Version = item.Version ?? "",
                    InBundles = item.InBundles,
                    IsOfficial = item.IsOfficial,
                    DepRef = item.DepRef ?? "",
                    SourceMark = item.SourceMark ?? "",
                    Repo = item.Repo ?? "",
                    MarketName = item.MarketName ?? ""
                });
            }
            return list;
        }

        /// <summary>用量分面视图；档案里没有返回 null。</summary>
        public UsageFacetData UsageFromArchive(string instanceId)
        {
            FacetSnapshot s = Service.Snapshot(instanceId, FacetNames.Usage);
            return s.TryGetData(out UsageFacetData data) ? data : null;
        }

        /// <summary>运行徽标的档案读法（liveness 分面 ["running"]，与 RefreshScheduler 同源姿势；不探端口）。</summary>
        public bool IsRunning(string instanceId)
        {
            var snap = Snapshot(instanceId, FacetNames.Liveness);
            if (!snap.TryGetData(out System.Collections.Generic.Dictionary<string, System.Text.Json.JsonElement> data)) return false;
            return data != null && data.TryGetValue("running", out System.Text.Json.JsonElement el)
                   && el.ValueKind == System.Text.Json.JsonValueKind.True;
        }

        /// <summary>全部档案（含已退役）的用量数据 + 展示名，供用量页做跨实例汇总。</summary>
        public List<(InstanceArchive Archive, UsageFacetData Usage)> AllUsage()
        {
            var list = new List<(InstanceArchive, UsageFacetData)>();
            foreach (InstanceArchive a in Service.All())
            {
                FacetSnapshot s = a.Facet(FacetNames.Usage);
                list.Add((a, s.TryGetData(out UsageFacetData d) ? d : null));
            }
            return list;
        }

        /// <summary>HOME 分面视图；档案里没有返回 null。</summary>
        public HomeFacetData HomeFromArchive(string instanceId)
        {
            FacetSnapshot s = Service.Snapshot(instanceId, FacetNames.Home);
            return s.TryGetData(out HomeFacetData data) ? data : null;
        }

        /// <summary>确保 HOME 状态已采集（首次进入页面时用；带时间预算，不会久等）。</summary>
        public async Task<HomeFacetData> EnsureHomeAsync(string instanceId)
        {
            FacetSnapshot s = Service.Snapshot(instanceId, FacetNames.Home);
            if (s.Status == FacetStatus.Never)
                await Scheduler.RefreshNowAsync(instanceId, FacetNames.Home).ConfigureAwait(true);
            return HomeFromArchive(instanceId);
        }

        // ==================== 采集（按需 / 强制） ====================

        /// <summary>确保版本已探测：档案里有结论就直接返回，否则采一次。</summary>
        public async Task<string> EnsureHarnessAsync(string instanceId)
        {
            if (!HarnessKnown(instanceId))
                await Scheduler.RefreshNowAsync(instanceId, FacetNames.Harness).ConfigureAwait(true);
            return HarnessVersion(instanceId);
        }

        /// <summary>
        /// 取指定 profile 的已装插件：档案命中直接用；未命中（或用户切了 profile）才真去扫一次。
        /// </summary>
        public async Task<List<InstalledPlugin>> EnsurePluginsAsync(string instanceId, string profile, bool force)
        {
            if (!force)
            {
                List<InstalledPlugin> cached = PluginsFromArchive(instanceId, profile);
                if (cached != null) return cached;
            }
            await CollectPluginsAsync(instanceId, profile).ConfigureAwait(true);
            return PluginsFromArchive(instanceId, profile) ?? new List<InstalledPlugin>();
        }

        /// <summary>按指定 profile 强制采集插件分面（profile 不是实例定义的一部分，需显式传入）。</summary>
        public async Task<FacetSnapshot> CollectPluginsAsync(string instanceId, string profile)
        {
            if (!_registry.TryGet(instanceId, out InstanceDef def)) return new FacetSnapshot();
            var ctx = new InstanceContext
            {
                Def = def,
                Settings = _registry.Settings,
                Profile = string.IsNullOrWhiteSpace(profile) ? "web" : profile
            };
            FacetSnapshot snapshot = await Service
                .CollectAsync(ctx, new PluginsCollector(), ttl: null, force: true)
                .ConfigureAwait(true);
            Service.FlushDirty();
            return snapshot;
        }

        public Task<FacetSnapshot> RefreshAsync(string instanceId, string facet)
        {
            return Scheduler.RefreshNowAsync(instanceId, facet);
        }

        // ==================== 事件 → 分面失效 ====================

        /// <summary>实例启动成功：profile 可能刚被创建，版本/HOME/插件都要重看。</summary>
        public void OnInstanceStarted(string instanceId)
        {
            Service.Invalidate(instanceId, FacetNames.Harness, FacetNames.Home,
                FacetNames.Plugins, FacetNames.WslEnv);
        }

        /// <summary>插件装/卸/升级之后。</summary>
        public void OnPluginsChanged(string instanceId)
        {
            Service.Invalidate(instanceId, FacetNames.Plugins);
        }

        /// <summary>实例设置变更（HOME / 发行版 / 指定版本…）。</summary>
        public void OnInstanceSettingsChanged(string instanceId)
        {
            SyncRegistry(force: true);
            Service.Invalidate(instanceId, FacetNames.Harness, FacetNames.Home, FacetNames.WslEnv,
                FacetNames.Plugins);
        }

        /// <summary>实例被删除：档案标记退役后仍永久保留。</summary>
        public void OnInstanceRemoved(string instanceId)
        {
            Service.Retire(instanceId);
            Service.FlushDirty();
        }

        public void Dispose()
        {
            try { Scheduler.Dispose(); }
            catch { /* 理由: 关闭路径，调度器已停或已释放都无副作用 */ }
            try { Service.FlushDirty(); }
            catch { /* 理由: 退出前的兜底落盘，失败只丢这一次的增量 */ }
        }
    }

    /// <summary>插件分面数据的强类型视图（与 PluginsCollector 写入的结构一一对应）。</summary>
    public sealed class PluginsFacetData
    {
        public string Profile { get; set; } = "";
        public string Home { get; set; } = "";
        public int Count { get; set; }
        public List<PluginsFacetItem> Items { get; set; } = new List<PluginsFacetItem>();
    }

    public sealed class PluginsFacetItem
    {
        public string Pkg { get; set; } = "";
        public string Version { get; set; } = "";
        public bool InBundles { get; set; }
        public bool IsOfficial { get; set; }
        public string DepRef { get; set; } = "";
        public string SourceMark { get; set; } = "";
        public string SourceText { get; set; } = "";
        public string Repo { get; set; } = "";
        public string MarketName { get; set; } = "";
    }
}
