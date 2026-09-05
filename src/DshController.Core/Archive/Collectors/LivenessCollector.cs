// ============================================================================
//  LivenessCollector — 在线状态分面（重构 2.0 / P1）
//
//  唯一高频分面：探一次 TCP（1.2s 上限）+ 命中时查监听 PID（netstat 结果有 3s 缓存）。
//  Persist=false：它不会因为自己变化就触发落盘（否则每 2 秒写一次盘，把档案磨成日志）；
//  但档案因其他分面写盘时会顺带保存这份快照，正好当作"上次已知状态"。
// ============================================================================

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DshController.Core.Archive.Collectors
{
    public sealed class LivenessCollector : IFacetCollector
    {
        public string Facet => FacetNames.Liveness;
        public bool Persist => false;
        public bool UsesWsl(InstanceContext ctx) => false;   // 走 Windows 侧端口转发，不进发行版

        public bool CanRun(InstanceContext ctx, out string skipReason)
        {
            skipReason = "";
            if (ctx?.Def == null) { skipReason = "实例定义缺失"; return false; }
            if (ctx.Def.Port < 1 || ctx.Def.Port > 65535) { skipReason = "端口非法"; return false; }
            return true;
        }

        public async Task<FacetResult> CollectAsync(InstanceContext ctx, CancellationToken ct)
        {
            string host = string.IsNullOrEmpty(ctx.Def.Host) ? "127.0.0.1" : ctx.Def.Host;
            int port = ctx.Def.Port;
            bool up = await PortTools.ProbeAsync(host, port, ct).ConfigureAwait(false);
            int pid = up ? await PortTools.FindListenerPidAsync(port).ConfigureAwait(false) : 0;

            var data = new Dictionary<string, object>
            {
                ["running"] = up,
                ["host"] = host,
                ["port"] = port,
                ["listenerPid"] = pid,
                ["url"] = PortTools.Url(host, port)
            };
            return FacetResult.Ok(data, "tcp-probe");
        }
    }
}
