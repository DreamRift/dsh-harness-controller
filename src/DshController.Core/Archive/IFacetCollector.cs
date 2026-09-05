// ============================================================================
//  IFacetCollector — 分面采集器契约（重构 2.0 / P1）
//
//  新增一类"实例信息"= 新增一个采集器 + 一个 DTO + 一条 TTL 默认值 + 一组离线测试，
//  不需要改档案模型、仓库、调度器或界面取数路径。这是本次重构给未来留的扩展位。
// ============================================================================

using System;
using System.Threading;
using System.Threading.Tasks;

namespace DshController.Core.Archive
{
    /// <summary>采集时的上下文（实例定义 + 全局设置 + 已知运行状态）。</summary>
    public sealed class InstanceContext
    {
        public InstanceDef Def { get; set; }
        public AppSettings Settings { get; set; }

        /// <summary>已知的运行状态（由 liveness 分面提供，避免每个采集器重复探端口）。</summary>
        public bool IsRunning { get; set; }

        /// <summary>插件相关分面的目标 profile（默认 web）。</summary>
        public string Profile { get; set; } = "web";

        public bool IsWsl => Def != null && Def.IsWsl;

        public Config ToConfig() => Def?.ToConfig(Settings);
    }

    public enum FacetOutcome
    {
        /// <summary>采到有效数据。</summary>
        Ok,

        /// <summary>采集成功，但目标确实没有内容（例如该 profile 一个插件都没装）。</summary>
        Empty,

        /// <summary>采集失败（保留上一次的成功数据）。</summary>
        Failed,

        /// <summary>前置条件不满足，主动跳过（例如 WSL 发行版没运行）。</summary>
        Skipped
    }

    public sealed class FacetResult
    {
        public FacetOutcome Outcome { get; private set; }
        public object Data { get; private set; }
        public string Source { get; private set; } = "";
        public string Error { get; private set; } = "";
        public string SkipReason { get; private set; } = "";

        public static FacetResult Ok(object data, string source = "")
            => new FacetResult { Outcome = FacetOutcome.Ok, Data = data, Source = source ?? "" };

        public static FacetResult Empty(object data = null, string source = "")
            => new FacetResult { Outcome = FacetOutcome.Empty, Data = data, Source = source ?? "" };

        public static FacetResult Failed(string error)
            => new FacetResult { Outcome = FacetOutcome.Failed, Error = error ?? "" };

        public static FacetResult Skipped(string reason)
            => new FacetResult { Outcome = FacetOutcome.Skipped, SkipReason = reason ?? "" };
    }

    public interface IFacetCollector
    {
        /// <summary>分面名（见 FacetNames）。</summary>
        string Facet { get; }

        /// <summary>
        /// 该分面的变化是否需要触发落盘。false = 高频信息（如 liveness），
        /// 不因自身变化写盘，但会随其他分面的写入顺带保存。
        /// </summary>
        bool Persist { get; }

        /// <summary>是否需要进 WSL 发行版（调度器据此走更严的并发预算）。</summary>
        bool UsesWsl(InstanceContext ctx);

        /// <summary>前置条件检查；返回 false 时 skipReason 会写进档案，界面可如实展示原因。</summary>
        bool CanRun(InstanceContext ctx, out string skipReason);

        Task<FacetResult> CollectAsync(InstanceContext ctx, CancellationToken ct);
    }
}
