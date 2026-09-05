// ============================================================================
//  ArchiveMetaViewModel — 档案页主区「元信息一览」（改版·元信息一览）
//
//  · 数据只来自 IArchiveFacade.AllUsage()（档案镜像内存快照）——零扫描零探针；
//  · Show(archiveId) 重建单份档案的镜像字段（代际/runtime/退役时间…）；
//    总计行伪 id 或未知 id → HasArchive=false（主区回落用量看板）；
//  · 选中联动由 App 层转发（RailArch.RowSelected / ApplyPage 档案页前置）。
// ============================================================================

using System;
using System.Collections.Generic;
using DshController.Core;
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

    public sealed class ArchiveMetaViewModel
    {
        private readonly IArchiveFacade _facade;

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

        public ArchiveMetaViewModel(IArchiveFacade facade)
        {
            _facade = facade ?? throw new ArgumentNullException(nameof(facade));
        }

        /// <summary>呈现指定档案的元信息；总计/空/未知 id 一律清空（主区回落用量看板）。</summary>
        public void Show(string archiveId)
        {
            var rows = new List<ArchiveMetaRow>();
            Title = "";
            Subtitle = "";
            StateLine = "";
            IsRetired = false;
            HasArchive = false;
            CurrentArchiveId = "";
            if (string.IsNullOrEmpty(archiveId)) { Rows = rows; return; }

            InstanceArchive found = null;
            List<(InstanceArchive Archive, UsageFacetData Usage)> all =
                _facade.AllUsage() ?? new List<(InstanceArchive Archive, UsageFacetData Usage)>();
            foreach ((InstanceArchive archive, _) in all)
            {
                if (archive != null && string.Equals(archive.ArchiveId, archiveId, StringComparison.OrdinalIgnoreCase))
                {
                    found = archive;
                    break;
                }
            }
            if (found == null) { Rows = rows; return; }

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
        }
    }
}
