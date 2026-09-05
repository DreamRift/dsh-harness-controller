// ============================================================================
//  WslEnvCollector — WSL 发行版环境分面（重构 2.0 / P1）
//
//  只负责"发行版侧的环境事实"：装没装、在没在跑、Linux $HOME 在哪、发行版内
//  dsh 是否可用。Windows 实例直接跳过（skipReason 会如实写进档案，界面照原样展示）。
//  注意：不在这里探版本——版本是 harness 分面的事，各分面各自计时、互不牵连。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DshController.Core.Archive.Collectors
{
    public sealed class WslEnvCollector : IFacetCollector
    {
        public string Facet => FacetNames.WslEnv;
        public bool Persist => true;
        public bool UsesWsl(InstanceContext ctx) => true;

        public bool CanRun(InstanceContext ctx, out string skipReason)
        {
            skipReason = "";
            if (ctx?.Def == null) { skipReason = "实例定义缺失"; return false; }
            if (!ctx.IsWsl) { skipReason = "Windows 实例无需 WSL 环境信息"; return false; }
            if (string.IsNullOrWhiteSpace(ctx.Def.WslDistro)) { skipReason = "未设置发行版"; return false; }
            return true;
        }

        public async Task<FacetResult> CollectAsync(InstanceContext ctx, CancellationToken ct)
        {
            string distro = ctx.Def.WslDistro ?? "";
            bool wslInstalled = await WslTools.IsInstalledAsync().ConfigureAwait(false);
            if (!wslInstalled)
                return FacetResult.Failed("本机未安装 WSL");

            IReadOnlyList<string> distros = await WslTools.ListDistrosAsync().ConfigureAwait(false);
            bool distroExists = false;
            foreach (string d in distros ?? new List<string>())
                if (string.Equals(d, distro, StringComparison.OrdinalIgnoreCase)) { distroExists = true; break; }

            bool running = distroExists && await WslTools.IsDistroRunningAsync(distro).ConfigureAwait(false);
            string linuxHome = "";
            string distroUser = "";
            bool dshAvailable = false;

            if (running)
            {
                distroUser = await WslTools.GetDistroUserAsync(distro).ConfigureAwait(false);
                linuxHome = await WslTools.GetDistroHomeAsync(distro).ConfigureAwait(false);
                var probe = await WslTools.RunInDistroAsync(distro,
                    "command -v dsh >/dev/null 2>&1 && echo DSH_OK || echo DSH_NONE", 60000).ConfigureAwait(false);
                dshAvailable = (probe.Output ?? "").Contains("DSH_OK");
            }

            var data = new Dictionary<string, object>
            {
                ["distro"] = distro,
                ["distroExists"] = distroExists,
                ["running"] = running,
                ["distroUser"] = distroUser ?? "",
                ["linuxHome"] = linuxHome ?? "",
                ["dshAvailable"] = dshAvailable
            };
            if (!distroExists) return FacetResult.Empty(data, "wsl");
            return FacetResult.Ok(data, "wsl");
        }
    }
}
