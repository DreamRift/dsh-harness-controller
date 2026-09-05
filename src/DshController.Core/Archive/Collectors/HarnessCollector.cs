// ============================================================================
//  HarnessCollector — 实例 harness 版本分面（重构 2.0 / P1）
//
//  取代原来散在界面里的 PluginUiHelper.VersionCache：那是个没有 TTL、不落盘、
//  只在进程内活着的字典，导致"首次进入页面看不到默认实例版本"（v1.0.1 修的 bug）
//  和"每次重启应用都要重新探一遍"。现在版本进档案：冷启动即有值，24h 过期，
//  实例启动成功/改版本字段时由事件强制失效。
// ============================================================================

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DshController.Core.Archive.Collectors
{
    public sealed class HarnessCollector : IFacetCollector
    {
        public string Facet => FacetNames.Harness;
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
            string version = ctx.IsWsl
                ? await HarnessVersion.ResolveWslAsync(ctx.Def.WslDistro ?? "").ConfigureAwait(false)
                : await HarnessVersion.ResolveWindowsAsync(ctx.ToConfig()).ConfigureAwait(false);

            var data = new Dictionary<string, object>
            {
                ["version"] = version ?? "",
                ["pinned"] = (ctx.Def.HarnessVersion ?? "").Trim()
            };
            // 探不到版本是"确实没有结论"，不是失败——用 Empty 让界面如实显示"未检测到"
            return string.IsNullOrEmpty(version)
                ? FacetResult.Empty(data, ctx.IsWsl ? "wsl" : "windows")
                : FacetResult.Ok(data, ctx.IsWsl ? "wsl" : "windows");
        }
    }
}
