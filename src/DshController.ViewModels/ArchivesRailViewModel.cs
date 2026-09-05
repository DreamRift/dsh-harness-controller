// ============================================================================
//  ArchivesRailViewModel — 档案页左边栏（改版·含退役列表）
//
//  · 数据只来自 IArchiveFacade.AllUsage()（含退役档案）——界面零取数铁律原样成立；
//  · 行分三类：「总计」伪 id totals 置顶 → 活跃实例行（沿用 AllUsage 现序）→
//    退役行（按退役时间降序，新退役在前）；退役行置灰+「已退役」徽标，
//    tooltip 追加「退役于 yyyy-MM-dd」（本地日期）；
//  · 选中态视图模型化：Select(id)/SelectedId；进入默认选中总计（SelectedId 初始
//    落到 totals），Refresh 后原选中仍在则保持、消失则回落总计。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using DshController.Core;
using DshController.Core.Archive;
using DshController.Core.Usage;

namespace DshController.ViewModels
{
    /// <summary>档案页左栏一行：总计行 / 活跃行 / 退役行（Refresh 整表重建，无残留状态）。</summary>
    public sealed class ArchivesRailRow
    {
        /// <summary>总计行伪 id（TotalsId）；其余为档案 id（=实例 id）。</summary>
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";      // 总计 / 环境:端口（显示名单源）
        public string SubText { get; set; } = "";    // 总计：份数摘要；实例行：原名
        public string Badge { get; set; } = "";      // 「已退役」徽标文案；空=无徽标
        public string Tooltip { get; set; } = "";    // 原名（退役行另起一行退役日期）
        public bool IsRetired { get; set; }
        public bool IsTotals { get; set; }
        /// <summary>退役行置灰系数（&lt;1 呈灰态）；纯数值，不引入 UI 类型。</summary>
        public double RowOpacity { get; set; } = 1.0;
    }

    public sealed class ArchivesRailViewModel
    {
        /// <summary>总计行伪 id（跨实例汇总主区在后续小类接入）。</summary>
        public const string TotalsId = "totals";

        private readonly IArchiveFacade _facade;

        public ObservableCollection<ArchivesRailRow> Rows { get; } = new ObservableCollection<ArchivesRailRow>();

        /// <summary>当前选中行 Id（Refresh 后仍在则保持，消失回落总计）。</summary>
        public string SelectedId { get; private set; } = "";

        public ArchivesRailViewModel(IArchiveFacade facade)
        {
            _facade = facade ?? throw new ArgumentNullException(nameof(facade));
        }

        /// <summary>重建三类行：总计置顶 → 活跃（AllUsage 现序）→ 退役（退役时间降序）。</summary>
        public void Refresh()
        {
            List<(InstanceArchive Archive, UsageFacetData Usage)> all =
                _facade.AllUsage() ?? new List<(InstanceArchive Archive, UsageFacetData Usage)>();

            var active = new List<InstanceArchive>();
            var retired = new List<InstanceArchive>();
            foreach ((InstanceArchive archive, _) in all)
            {
                if (archive == null) continue;
                if (archive.IsRetired) retired.Add(archive);
                else active.Add(archive);
            }
            retired.Sort((x, y) => y.RetiredAt.Value.CompareTo(x.RetiredAt.Value));   // 新退役在前

            var next = new List<ArchivesRailRow> { TotalsRow(active.Count + retired.Count, retired.Count) };
            foreach (InstanceArchive a in active) next.Add(InstanceRow(a));
            foreach (InstanceArchive a in retired) next.Add(InstanceRow(a));

            Rows.Clear();
            foreach (ArchivesRailRow r in next) Rows.Add(r);

            if (!Rows.Any(r => r.Id == SelectedId)) SelectedId = TotalsId;   // 默认/回落：总计
        }

        /// <summary>选中一行（UI 事件转发进来）：总计与退役行均可选中；未知 id 忽略。</summary>
        public bool Select(string id)
        {
            if (string.IsNullOrEmpty(id) || SelectedId == id) return false;
            if (!Rows.Any(r => r.Id == id)) return false;
            SelectedId = id;
            return true;
        }

        private static ArchivesRailRow TotalsRow(int total, int retired)
        {
            return new ArchivesRailRow
            {
                Id = TotalsId,
                IsTotals = true,
                Title = "总计",
                SubText = "档案 " + total + " 份 · 退役 " + retired,
                Tooltip = "全部档案与用量（含已退役；档案删除后仍永久保留）"
            };
        }

        private static ArchivesRailRow InstanceRow(InstanceArchive a)
        {
            bool isRetired = a.IsRetired;
            return new ArchivesRailRow
            {
                Id = a.ArchiveId,
                Title = InstanceDisplayName.ForArchive(a),                  // 环境:端口（显示名单源）
                SubText = string.IsNullOrWhiteSpace(a.DisplayName) ? a.ArchiveId : a.DisplayName,
                Badge = isRetired ? "已退役" : "",
                Tooltip = isRetired
                    ? InstanceDisplayName.TooltipForArchive(a) + "\n退役于 " + a.RetiredAt.Value.ToLocalTime().ToString("yyyy-MM-dd")
                    : InstanceDisplayName.TooltipForArchive(a),
                IsRetired = isRetired,
                RowOpacity = isRetired ? 0.55 : 1.0
            };
        }
    }
}
