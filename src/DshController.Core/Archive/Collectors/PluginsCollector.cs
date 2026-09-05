// ============================================================================
//  PluginsCollector — 已装插件分面（重构 2.0 / P1）
//
//  数据仍以实例 HOME 的 profiles/<profile>/package.json 为唯一事实（黑盒读取），
//  市场安装记录只做来源标注。原来每次进入插件页都要重扫一遍（Windows 毫秒级、
//  WSL 一次 wsl.exe 往返），现在扫完进档案：默认 6 小时过期，插件增删改后由事件失效。
// ============================================================================

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DshController.Core.Archive.Collectors
{
    public sealed class PluginsCollector : IFacetCollector
    {
        public string Facet => FacetNames.Plugins;
        public bool Persist => true;
        public bool UsesWsl(InstanceContext ctx) => ctx != null && ctx.IsWsl;

        public bool CanRun(InstanceContext ctx, out string skipReason)
        {
            skipReason = "";
            if (ctx?.Def == null) { skipReason = "实例定义缺失"; return false; }
            if (ctx.IsWsl && string.IsNullOrWhiteSpace(ctx.Def.WslDistro))
            {
                skipReason = "WSL 实例未设置发行版";
                return false;
            }
            return true;
        }

        public async Task<FacetResult> CollectAsync(InstanceContext ctx, CancellationToken ct)
        {
            Config cfg = ctx.ToConfig();
            string profile = string.IsNullOrWhiteSpace(ctx.Profile) ? "web" : ctx.Profile;
            List<PluginRecord> records = PluginRecords.Load(ctx.Def.Id);
            List<InstalledPlugin> installed;
            string home;

            if (ctx.IsWsl)
            {
                string distro = cfg.WslDistro ?? "";
                if (!await WslTools.IsDistroRunningAsync(distro).ConfigureAwait(false))
                    return FacetResult.Skipped("WSL 发行版未运行（可手动唤醒后刷新）");

                string root = await WslTools.GetDistroHomeAsync(distro).ConfigureAwait(false);
                home = WslTools.ResolveLinuxPath(cfg.WslHome ?? "", string.IsNullOrEmpty(root) ? "/root" : root);
                installed = await InstalledPlugins.ReadWslAsync(distro, home, profile, records).ConfigureAwait(false);
            }
            else
            {
                home = string.IsNullOrWhiteSpace(cfg.Home) ? Storage.AppPaths.DefaultDshHome : cfg.Home;
                installed = await InstalledPlugins.ReadWindowsAsync(home, profile, records).ConfigureAwait(false);
            }

            var items = new List<object>();
            foreach (InstalledPlugin p in installed ?? new List<InstalledPlugin>())
            {
                items.Add(new Dictionary<string, object>
                {
                    ["pkg"] = p.Pkg,
                    ["version"] = p.Version ?? "",
                    ["inBundles"] = p.InBundles,
                    ["isOfficial"] = p.IsOfficial,
                    ["depRef"] = p.DepRef ?? "",
                    ["sourceMark"] = p.SourceMark ?? "",
                    ["sourceText"] = p.SourceText ?? "",
                    ["repo"] = p.Repo ?? "",
                    ["marketName"] = p.MarketName ?? ""
                });
            }

            var data = new Dictionary<string, object>
            {
                ["profile"] = profile,
                ["home"] = home ?? "",
                ["count"] = items.Count,
                ["items"] = items
            };
            return items.Count == 0
                ? FacetResult.Empty(data, "profile-package.json")
                : FacetResult.Ok(data, "profile-package.json");
        }
    }
}
