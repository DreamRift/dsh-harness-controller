// ============================================================================
//  IArchiveFacade — 视图模型看到的"档案"（重构 2.0 / P2 起，P3 扩充）
//
//  视图模型不认识 WinUI，也不认识采集器与调度器；它只需要"从档案要数据"和
//  "请求刷新"。App 侧由 ArchiveHub 实现，测试侧用假实现——于是插件页与用量页的
//  取数、空态、忙态、刷新语义都能离线验证。
// ============================================================================

using System.Collections.Generic;
using System.Threading.Tasks;
using DshController.Core;
using DshController.Core.Archive;
using DshController.Core.Usage;

namespace DshController.ViewModels
{
    public interface IArchiveFacade
    {
        // ---------------- 实例清单 ----------------

        /// <summary>当前实例清单（顺序由调用方决定，视图模型自行排序）。</summary>
        IReadOnlyList<InstanceDef> Instances { get; }

        /// <summary>当前可见页面聚焦的实例（影响采集优先级）。</summary>
        void SetVisible(string instanceId);

        // ---------------- 分面读取 ----------------

        /// <summary>实例 harness 版本；档案里没有结论返回空串。</summary>
        string HarnessVersion(string instanceId);

        /// <summary>档案里是否已有版本结论（含"探测过但没结果"）。</summary>
        bool HarnessKnown(string instanceId);

        /// <summary>确保版本已探测（命中档案即零成本）。</summary>
        Task<string> EnsureHarnessAsync(string instanceId);

        /// <summary>指定 profile 的已装插件；命中档案即秒回，未命中才真去扫。</summary>
        Task<List<InstalledPlugin>> EnsurePluginsAsync(string instanceId, string profile, bool force);

        /// <summary>HOME 状态（是否初始化、profile 列表、体积）。</summary>
        Task<HomeFacetData> EnsureHomeAsync(string instanceId);

        /// <summary>全部档案（含已退役）及其用量数据。</summary>
        List<(InstanceArchive Archive, UsageFacetData Usage)> AllUsage();

        /// <summary>实例是否运行中：读档案 liveness 分面的现成结论，绝不探端口/起进程。</summary>
        bool IsRunning(string instanceId);

        // ---------------- 失效与刷新 ----------------

        /// <summary>强制重采某实例的某个分面。</summary>
        Task<FacetSnapshot> RefreshAsync(string instanceId, string facet);

        /// <summary>让若干分面立即过期（数据保留，等新数据到达再替换）。</summary>
        void Invalidate(string instanceId, params string[] facets);

        /// <summary>插件装/卸/升级之后。</summary>
        void OnPluginsChanged(string instanceId);
    }

    /// <summary>HOME 分面的强类型视图（与 Core 的 HomeCollector 写入结构一一对应）。</summary>
    public sealed class HomeFacetData
    {
        public string Path { get; set; } = "";
        public bool Exists { get; set; }
        public bool Initialized { get; set; }
        public List<string> Profiles { get; set; } = new List<string>();
        public long SizeBytes { get; set; }
        public bool SizeTruncated { get; set; }
    }
}
