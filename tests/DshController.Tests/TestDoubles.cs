// ============================================================================
//  测试替身：假时钟与假采集器（重构 2.0 / P1）
//
//  档案子系统的核心逻辑（TTL 判定、失败不覆盖、单飞、调度预算）全部依赖
//  "时间"与"采集行为"，这两个替身让它们能在毫秒内确定性地被验证。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DshController.Core.Abstractions;
using DshController.Core.Archive;

namespace DshController.Tests
{
    public sealed class FakeClock : IClock
    {
        public DateTime UtcNow { get; set; } = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
        public void Advance(TimeSpan by) { UtcNow = UtcNow.Add(by); }
    }

    /// <summary>可编排结果、可计数、可阻塞的采集器。</summary>
    public sealed class FakeCollector : IFacetCollector
    {
        private readonly Func<FacetResult> _result;

        public FakeCollector(string facet, Func<FacetResult> result = null, bool persist = true)
        {
            Facet = facet;
            _result = result ?? (() => FacetResult.Ok(new Dictionary<string, object> { ["v"] = 1 }, "fake"));
            Persist = persist;
        }

        public string Facet { get; }
        public bool Persist { get; }
        public bool Wsl { get; set; }
        public bool Runnable { get; set; } = true;
        public string SkipReason { get; set; } = "被测试禁用";
        public int Calls;

        /// <summary>非空时采集会一直等它完成（用于单飞与并发测试）。</summary>
        public TaskCompletionSource<bool> Gate { get; set; }

        /// <summary>记录采集顺序（调度优先级测试用）。</summary>
        public List<string> CallLog { get; set; }

        public bool UsesWsl(InstanceContext ctx) => Wsl;

        public bool CanRun(InstanceContext ctx, out string skipReason)
        {
            skipReason = Runnable ? "" : SkipReason;
            return Runnable;
        }

        public async Task<FacetResult> CollectAsync(InstanceContext ctx, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            CallLog?.Add(ctx.Def.Id + "/" + Facet);
            if (Gate != null) await Gate.Task.ConfigureAwait(false);
            return _result();
        }
    }
}
