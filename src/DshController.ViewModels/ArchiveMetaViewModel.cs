// ============================================================================
//  ArchiveMetaViewModel — 档案页主区「单实例档案详情」（元信息 + 完整用量）
//
//  · 数据只来自 IArchiveFacade.AllUsage()（档案镜像内存快照）——零扫描零探针；
//  · Show(archiveId) 重建单份档案的镜像字段（代际/runtime/退役时间…）；
//    总计行伪 id 或未知 id → HasArchive=false（主区回落用量看板）；
//  · 2026-09-06 二次改版——单实例完整用量并入详情页（大数卡 / hero 四桶 /
//    按天钻取 / 模型排行 / 会话明细 / 单档案重采），实现在
//    ArchiveMetaViewModel.Usage.cs（partial，与本文件同口径同契约）；
//  · 选中联动由 App 层转发（RailArch.RowSelected / ApplyPage 档案页前置）。
// ============================================================================

using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using DshController.Core.Archive;
using DshController.Core.Usage;

namespace DshController.ViewModels
{
    /// <summary>元信息一行：镜像字段标签 + 值。</summary>
    public sealed class ArchiveMetaRow
    {
        public string Label { get; set; } = "";
        public string Value { get; set; } = "";
    }

    public sealed partial class ArchiveMetaViewModel : ObservableObject
    {
        private readonly IArchiveFacade _facade;
        private readonly Action<string> _log;

        /// <summary>标题=环境:端口（显示名单源）。</summary>
        public string Title { get; private set; } = "";

        /// <summary>副标题=原名（TooltipForArchive 单源文案）。</summary>
        public string Subtitle { get; private set; } = "";

        /// <summary>状态行：活跃档案 / 已退役 · 退役于 yyyy-MM-dd。</summary>
        public string StateLine { get; private set; } = "";

        public bool IsRetired { get; private set; }

        public bool HasArchive { get; private set; }

        /// <summary>当前正在显示的档案 id（改名按钮/事件用；无档案时为空）。</summary>
        public string CurrentArchiveId { get; private set; } = "";

        /// <summary>镜像字段行（每次 Show 整表重建，新引用触发绑定刷新）。</summary>
        public IReadOnlyList<ArchiveMetaRow> Rows { get; private set; } = new List<ArchiveMetaRow>();

        // ---------------- 用量区（View 用 OneWay 绑定；命令完成后自动更新，不依赖 Bindings.Update） ----------------

        /// <summary>当前档案是否有可展示的用量数据（无数据时显示 UsageEmptyText）。</summary>
        [ObservableProperty] public partial bool HasUsage { get; set; }

        /// <summary>单档案用量重采进行中（与看板的 IsBusy 同义，独立守卫）。</summary>
        [ObservableProperty] public partial bool UsageBusy { get; set; }

        /// <summary>「重采用量」按钮可用态 = 有档案 && 未退役 && 不在忙。</summary>
        [ObservableProperty] public partial bool UsageRefreshEnabled { get; set; }

        /// <summary>用量空态文案（HasUsage=false 时展示）。</summary>
        [ObservableProperty] public partial string UsageEmptyText { get; set; } = "";

        /// <summary>档案更新于 MM-dd HH:mm（usage 分面 LastGoodAt；无则「尚未采集」）。</summary>
        [ObservableProperty] public partial string UsageUpdatedText { get; set; } = "";

        // 大数卡
        [ObservableProperty] public partial string TotalTokensText { get; set; } = "—";
        [ObservableProperty] public partial string HitRateText { get; set; } = "—";
        [ObservableProperty] public partial string RequestsText { get; set; } = "—";
        [ObservableProperty] public partial string SessionsText { get; set; } = "—";
        [ObservableProperty] public partial string ActiveDaysText { get; set; } = "—";
        [ObservableProperty] public partial string TopModelText { get; set; } = "—";
        [ObservableProperty] public partial string HeroSubText { get; set; } = "";

        // hero 四桶堆叠条像素宽（与 UsageViewModel.BuildHeroBars 同口径，比例×330）
        [ObservableProperty] public partial double BarWUncached { get; set; }
        [ObservableProperty] public partial double BarWCacheRead { get; set; }
        [ObservableProperty] public partial double BarWCacheWrite { get; set; }
        [ObservableProperty] public partial double BarWOutput { get; set; }

        /// <summary>UsageBusy 变化即重算按钮可用态（退役档案始终禁用，BuildUsage/Show 也会重算）。</summary>
        partial void OnUsageBusyChanged(bool value) =>
            UsageRefreshEnabled = HasArchive && !IsRetired && !value;

        public ArchiveMetaViewModel(IArchiveFacade facade, Action<string> log = null)
        {
            _facade = facade ?? throw new ArgumentNullException(nameof(facade));
            _log = log;
        }

        /// <summary>呈现指定档案的元信息与用量；总计/空/未知 id 一律清空（主区回落用量看板）。</summary>
        public void Show(string archiveId)
        {
            var rows = new List<ArchiveMetaRow>();
            Title = "";
            Subtitle = "";
            StateLine = "";
            IsRetired = false;
            HasArchive = false;
            CurrentArchiveId = "";
            if (string.IsNullOrEmpty(archiveId)) { ClearUsage(); Rows = rows; return; }

            InstanceArchive found = null;
            UsageFacetData foundUsage = null;
            List<(InstanceArchive Archive, UsageFacetData Usage)> all =
                _facade.AllUsage() ?? new List<(InstanceArchive Archive, UsageFacetData Usage)>();
            foreach ((InstanceArchive archive, UsageFacetData usage) in all)
            {
                if (archive != null && string.Equals(archive.ArchiveId, archiveId, StringComparison.OrdinalIgnoreCase))
                {
                    found = archive;
                    foundUsage = usage;
                    break;
                }
            }
            if (found == null) { ClearUsage(); Rows = rows; return; }

            HasArchive = true;
            CurrentArchiveId = found.ArchiveId;
            IsRetired = found.IsRetired;
            Title = InstanceDisplayName.ForArchive(found);
            Subtitle = InstanceDisplayName.TooltipForArchive(found);
            StateLine = found.IsRetired
                ? "已退役 · 退役于 " + found.RetiredAt.Value.ToLocalTime().ToString("yyyy-MM-dd")
                : "活跃档案";

            rows.Add(new ArchiveMetaRow { Label = "档案 ID", Value = found.ArchiveId });
            rows.Add(new ArchiveMetaRow { Label = "Runtime", Value = found.Runtime == "wsl" ? "wsl" : "windows" });
            int port = InstanceDisplayName.ArchivePort(found);
            rows.Add(new ArchiveMetaRow { Label = "端口", Value = port > 0 ? port.ToString() : "—" });
            rows.Add(new ArchiveMetaRow { Label = "创建于", Value = found.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm") });
            int epochs = found.Epochs == null ? 0 : found.Epochs.Count;
            rows.Add(new ArchiveMetaRow { Label = "代际", Value = epochs + " 代" });
            var cur = found.CurrentEpoch;
            if (cur != null)
            {
                rows.Add(new ArchiveMetaRow
                {
                    Label = found.IsRetired ? "最近代起" : "当前代起",
                    Value = cur.From.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
                });
            }
            if (found.IsRetired)
            {
                rows.Add(new ArchiveMetaRow
                {
                    Label = "退役于",
                    Value = found.RetiredAt.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
                });
            }
            Rows = rows;

            BuildUsage(found, foundUsage);
        }
    }
}
