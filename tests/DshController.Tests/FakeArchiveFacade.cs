// ============================================================================
//  FakeArchiveFacade — 视图模型测试用的假档案（重构 2.0 / P3）
//
//  视图模型只认 IArchiveFacade，于是插件页与用量页的 UI 逻辑可以完全离线验证：
//  不起 WinUI、不碰真实实例、不扫盘、不联网。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DshController.Core;
using DshController.Core.Archive;
using DshController.Core.Usage;
using DshController.ViewModels;

namespace DshController.Tests
{
    internal sealed class FakeArchiveFacade : IArchiveFacade
    {
        private readonly List<InstanceDef> _instances = new List<InstanceDef>();
        private readonly Dictionary<string, string> _versions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<InstalledPlugin>> _plugins =
            new Dictionary<string, List<InstalledPlugin>>(StringComparer.OrdinalIgnoreCase);

        public List<(InstanceArchive Archive, UsageFacetData Usage)> Items { get; } =
            new List<(InstanceArchive, UsageFacetData)>();

        public List<string> Refreshed { get; } = new List<string>();
        public List<string> Invalidated { get; } = new List<string>();
        public List<string> PluginScans { get; } = new List<string>();
        public string VisibleInstanceId { get; private set; } = "";
        public HomeFacetData Home { get; set; } = new HomeFacetData { Initialized = true, Exists = true };
        public bool VersionKnown { get; set; } = true;

        public IReadOnlyList<InstanceDef> Instances => _instances;

        // ---------------- 造数据 ----------------

        public InstanceDef AddInstance(string id, string name = null, bool wsl = false)
        {
            var def = new InstanceDef
            {
                Id = id,
                Name = name ?? id,
                Port = 3080,
                Runtime = wsl ? "wsl" : "windows",
                WslDistro = wsl ? "Ubuntu" : ""
            };
            _instances.Add(def);
            return def;
        }

        /// <summary>从清单移除（模拟实例被删；档案 Items 不动，退役语义由 Add(retired) 表达）。</summary>
        public void RemoveInstance(string id) => _instances.RemoveAll(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase));

        public void SetVersion(string id, string version) => _versions[id] = version;

        public void SetPlugins(string id, params string[] pkgs)
        {
            var list = new List<InstalledPlugin>();
            foreach (string p in pkgs) list.Add(new InstalledPlugin { Pkg = p, Version = "1.0.0" });
            _plugins[id] = list;
        }

        public void Add(string id, string name, bool retired, UsageFacetData usage, bool running = false)
        {
            var archive = InstanceArchive.CreateFor(new InstanceDef { Id = id, Name = name }, DateTime.UtcNow);
            if (retired) archive.RetiredAt = DateTime.UtcNow;
            if (running)
            {
                archive.Facets[FacetNames.Liveness] = new FacetSnapshot
                {
                    Status = FacetStatus.Ok,
                    LastGoodAt = DateTime.UtcNow,
                    CollectedAt = DateTime.UtcNow,
                    Data = System.Text.Json.JsonSerializer.SerializeToElement(
                        new Dictionary<string, bool> { ["running"] = true })
                };
            }
            if (usage != null)
            {
                archive.Facets[FacetNames.Usage] = new FacetSnapshot
                {
                    Status = FacetStatus.Ok,
                    LastGoodAt = DateTime.UtcNow,
                    CollectedAt = DateTime.UtcNow
                };
            }
            Items.Add((archive, usage));
        }

        // ---------------- IArchiveFacade ----------------

        public void SetVisible(string instanceId) => VisibleInstanceId = instanceId ?? "";

        public string HarnessVersion(string instanceId) =>
            _versions.TryGetValue(instanceId ?? "", out string v) ? v : "";

        public bool HarnessKnown(string instanceId) => VersionKnown;

        public Task<string> EnsureHarnessAsync(string instanceId) =>
            Task.FromResult(HarnessVersion(instanceId));

        public Task<List<InstalledPlugin>> EnsurePluginsAsync(string instanceId, string profile, bool force)
        {
            PluginScans.Add(instanceId + "/" + profile + (force ? "/force" : ""));
            return Task.FromResult(_plugins.TryGetValue(instanceId ?? "", out List<InstalledPlugin> list)
                ? list : new List<InstalledPlugin>());
        }

        public Task<HomeFacetData> EnsureHomeAsync(string instanceId) => Task.FromResult(Home);

        public List<(InstanceArchive Archive, UsageFacetData Usage)> AllUsage() => Items;

        public bool IsRunning(string instanceId)
        {
            foreach (var (archive, _) in Items)
            {
                if (archive == null || !string.Equals(archive.ArchiveId, instanceId, StringComparison.OrdinalIgnoreCase)) continue;
                var snap = archive.Facet(FacetNames.Liveness);
                Dictionary<string, System.Text.Json.JsonElement> data;
                if (!snap.TryGetData(out data)) return false;
                System.Text.Json.JsonElement el;
                return data != null && data.TryGetValue("running", out el) && el.ValueKind == System.Text.Json.JsonValueKind.True;
            }
            return false;
        }

        public Task<FacetSnapshot> RefreshAsync(string instanceId, string facet)
        {
            Refreshed.Add(instanceId + "/" + facet);
            return Task.FromResult(new FacetSnapshot { Status = FacetStatus.Ok });
        }

        public void Invalidate(string instanceId, params string[] facets)
        {
            Invalidated.Add(instanceId + "/" + string.Join(",", facets ?? new string[0]));
        }

        public void OnPluginsChanged(string instanceId) => Invalidated.Add(instanceId + "/plugins");
    }
}
