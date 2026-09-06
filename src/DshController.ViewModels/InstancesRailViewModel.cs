// ============================================================================
//  InstancesRailViewModel — 实例页左边栏（改版·启动序列表）
//  · 数据只来自 IArchiveFacade：清单（含 lastStartedAt/createdAt 打点）+ liveness
//    结论（IsRunning）。界面零取数铁律原样成立。
//  · 排序纯函数 OrderForRail：有启动记录在前按最近启动降序；从未启动的置末、
//    段内按创建时间降序（越新越靠启动段，便于接手）；退役实例不在清单里，天然
//    不出现（档案 AllUsage 不参与本列表）。
//  · 行点击 = 选中 + SetVisible（采集优先级）；跳环境页/同步选择由 App 层转发，
//    详情联动在「详情操作主区」小类接线。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using DshController.Core;

namespace DshController.ViewModels
{
    /// <summary>左栏一行：活跃实例快照（每次 Refresh 整表重建，无残留状态）。</summary>
    public sealed class InstanceRailRow
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Runtime { get; set; } = "windows";   // windows | wsl
        public int Port { get; set; }
        public bool Running { get; set; }
        public DateTime? LastStartedAt { get; set; }
        public DateTime? CreatedAt { get; set; }

        /// <summary>副行小字：instances.json 原名（悬停另见 tooltip 的完整原名/发行版）。</summary>
        public string SubText { get { return OriginalName; } }
        /// <summary>tooltip（改版·环境端口命名）：原名 + WSL 发行版。</summary>
        public string TooltipText { get { return Tooltip; } }
        public string StateText { get { return Running ? "运行中" : "已停止"; } }
        /// <summary>原名（无名字回落 Id）。</summary>
        public string OriginalName { get; set; } = "";
        /// <summary>tooltip 文案（解析器产出）。</summary>
        public string Tooltip { get; set; } = "";
        public bool IsWsl { get { return Runtime == "wsl"; } }
    }

    public sealed class InstancesRailViewModel
    {
        private readonly IArchiveFacade _facade;

        public ObservableCollection<InstanceRailRow> Rows { get; } = new ObservableCollection<InstanceRailRow>();

        /// <summary>当前选中行 Id（Refresh 后按 Id 恢复）。</summary>
        public string SelectedId { get; private set; } = "";

        public InstancesRailViewModel(IArchiveFacade facade)
        {
            _facade = facade ?? throw new ArgumentNullException(nameof(facade));
        }

        /// <summary>排序纯函数（单测主战场）：最近启动在前 → 从未启动按创建时间降序置末。</summary>
        public static IReadOnlyList<InstanceDef> OrderForRail(IEnumerable<InstanceDef> defs)
        {
            return (defs ?? Enumerable.Empty<InstanceDef>())
                .OrderByDescending(d => d != null && d.LastStartedAt.HasValue ? 1 : 0)
                .ThenByDescending(d => d != null && d.LastStartedAt.HasValue ? d.LastStartedAt.Value : DateTime.MinValue)
                .ThenByDescending(d => d != null && d.CreatedAt.HasValue ? d.CreatedAt.Value : DateTime.MinValue)
                .Where(d => d != null && !string.IsNullOrEmpty(d.Id))
                .ToList();
        }

        /// <summary>重排行+刷新运行态（liveness 每轮前台 2s，这里只读结论不探测）。
        /// 稳定态（行序列与内容与现有完全一致）不触碰 Rows——每轮 Clear+重加新对象会让
        /// ListView 整表重建容器（左栏等间隔闪烁的根源），只有真有变化才重排。</summary>
        public void Refresh()
        {
            List<InstanceRailRow> next = OrderForRail(_facade.Instances)
                .Select(d => new InstanceRailRow
                {
                    Id = d.Id,
                    Name = InstanceDisplayName.For(d),                       // 环境:端口（显示名单源）
                    OriginalName = InstanceDisplayName.Original(d),          // 副行/tooltip：原名
                    Tooltip = InstanceDisplayName.TooltipFor(d),
                    Runtime = d.IsWsl ? "wsl" : "windows",
                    Port = d.Port,
                    Running = _facade.IsRunning(d.Id),
                    LastStartedAt = d.LastStartedAt,
                    CreatedAt = d.CreatedAt
                })
                .ToList();

            if (SequenceEqualsCurrent(next)) return;

            Rows.Clear();
            foreach (InstanceRailRow r in next) Rows.Add(r);

            if (!string.IsNullOrEmpty(SelectedId) && !Rows.Any(r => r.Id == SelectedId)) SelectedId = "";
        }

        /// <summary>现有行序列与 next 逐字段一致（同长度、同序、同内容）→ 稳定态。</summary>
        private bool SequenceEqualsCurrent(List<InstanceRailRow> next)
        {
            if (Rows.Count != next.Count) return false;
            for (int i = 0; i < next.Count; i++)
            {
                InstanceRailRow a = Rows[i], b = next[i];
                if (a.Id != b.Id || a.Name != b.Name || a.OriginalName != b.OriginalName
                    || a.Tooltip != b.Tooltip || a.Runtime != b.Runtime || a.Port != b.Port
                    || a.Running != b.Running || a.LastStartedAt != b.LastStartedAt
                    || a.CreatedAt != b.CreatedAt)
                    return false;
            }
            return true;
        }

        /// <summary>选中一行（UI 事件转发进来）：更新 SelectedId + 采集焦点。返回是否变化。空 id = 清除选中。</summary>
        public bool Select(string id)
        {
            if (string.IsNullOrEmpty(id))
            {
                if (SelectedId.Length == 0) return false;
                SelectedId = "";
                return true;
            }
            InstanceRailRow row = Rows.FirstOrDefault(r => r.Id == id);
            if (row == null || SelectedId == id) return false;
            SelectedId = id;
            _facade.SetVisible(id);
            return true;
        }

        /// <summary>该行属于哪个环境子页（App 层跳转用）。</summary>
        public static string SubPageOf(InstanceRailRow row)
        {
            return row != null && row.IsWsl ? "wsl" : "win";
        }
    }
}
