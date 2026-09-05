// ============================================================================
//  UsageViewModel 钻取与统一视图实例卡（partial 之二，定稿方案 D2-A/D3-A）
//
//  全部数据来自本帧 _summary 与档案元组——无 IO、无取数。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DshController.Core.Archive;
using DshController.Core.Usage;

namespace DshController.ViewModels
{
    public partial class UsageViewModel
    {
        // ==================== 按天页内钻取（D3-A） ====================

        [ObservableProperty] public partial string SelectedDay { get; set; } = "";
        [ObservableProperty] public partial bool HasDay { get; set; }
        [ObservableProperty] public partial string DayStripText { get; set; } = "";
        [ObservableProperty] public partial string DayModelsText { get; set; } = "";

        /// <summary>清钻取：会话列表回到全量（Reload 与「清除」共用）。</summary>
        private void ResetDrill()
        {
            SelectedDay = "";
            HasDay = false;
            DayStripText = "";
            DayModelsText = "";
            foreach (UsageDayBar bar in DailyBars) bar.IsSelected = false;   // 柱子的选中环一并摘掉
            RebuildSessions(filtered: false);
        }

        private void ToggleDay(string day)
        {
            if (string.IsNullOrEmpty(day)) return;
            if (SelectedDay == day) { ResetDrill(); return; }   // 再点同柱 = 退出

            SelectedDay = day;
            HasDay = true;
            TokenBuckets b = _summary.Daily.TryGetValue(day, out TokenBuckets d) ? d : new TokenBuckets();
            DayStripText = day + " 当日：未缓存输入 " + UsageQuery.FormatTokens(b.UncachedInput) +
                           " · 缓存读 " + UsageQuery.FormatTokens(b.CacheRead) +
                           " · 缓存写 " + UsageQuery.FormatTokens(b.CacheWrite) +
                           " · 输出 " + UsageQuery.FormatTokens(b.Output);
            var comp = UsageQuery.DayComposition(_summary.Models, day);
            DayModelsText = comp.Count == 0 ? "" : "当日模型：" + string.Join(" · ",
                comp.Take(3).Select(c => c.DisplayName + " " + (c.Share * 100).ToString("0.#") + "%")) +
                (comp.Count > 3 ? " · 其它 " + (comp.Skip(3).Sum(c => c.Share) * 100).ToString("0.#") + "%" : "");
            foreach (UsageDayBar bar in DailyBars) bar.IsSelected = bar.Day == day;
            RebuildSessions(filtered: true);
        }

        [RelayCommand]
        private void ClearDay() => ResetDrill();

        private void RebuildSessions(bool filtered)
        {
            Sessions.Clear();
            IEnumerable<UsageSessionStat> src = filtered && !string.IsNullOrEmpty(SelectedDay)
                ? UsageQuery.SessionsOnDay(_summary.Sessions, SelectedDay)
                : _summary.Sessions ?? Enumerable.Empty<UsageSessionStat>();
            foreach (UsageSessionStat s in src.Take(200))
            {
                UsageSessionRow row = UsageSessionRow.From(s);
                var self = row;
                self.ExpandCommand = new RelayCommand(() => self.IsExpanded = !self.IsExpanded);
                Sessions.Add(row);
            }
        }

        // ==================== 统一视图实例卡（D2-A） ====================

        private void BuildInstanceCards(List<UsageFacetData> scopeSources, DateTime? from)
        {
            InstanceCards.Clear();
            const double k = 200;
            foreach ((InstanceArchive archive, UsageFacetData usage) in _hub.AllUsage())
            {
                if (archive == null) continue;
                var card = new UsageInstanceCardRow
                {
                    ArchiveId = archive.ArchiveId,
                    Name = InstanceDisplayName.ForArchive(archive),   // 环境:端口（显示名单源；原名在 tooltip）
                    RetiredText = archive.IsRetired ? "已删除 · 档案保留" : ""
                };
                if (IsArchiveRunning(archive)) { card.RunningText = "●运行中"; card.RunningVisible = true; }
                string id = archive.ArchiveId;
                card.OpenCommand = new RelayCommand(() => OpenInstance(id));

                card.ToolTip = card.Name + "（" + InstanceDisplayName.TooltipForArchive(archive) + "）" +
                               (card.RetiredText.Length > 0 ? "（" + card.RetiredText + "）" : "") +
                               "\n精确值：未缓存输入 " + usage?.Totals.UncachedInput.ToString("N0") +
                               " · 缓存读 " + usage?.Totals.CacheRead.ToString("N0") +
                               " · 缓存写 " + usage?.Totals.CacheWrite.ToString("N0") +
                               " · 输出 " + usage?.Totals.Output.ToString("N0") +
                               "\nprojcache 总账口径 · 点击卡片进入聚焦视图";
                if (usage == null || (usage.Totals.Total <= 0 && usage.Sessions.Count == 0))
                {
                    card.HasData = false;
                    card.NoDataText = "无用量数据";
                    card.ToolTip = card.Name + "（" + InstanceDisplayName.TooltipForArchive(archive) + "）\n无用量数据：启动该实例产生会话后点「刷新」采集";
                    InstanceCards.Add(card);
                    continue;
                }
                card.HasData = true;
                UsageSummary s = UsageQuery.Summarize(new[] { usage }, from, null);
                long t = s.Totals.Total;
                card.TotalText = UsageQuery.FormatTokens(t);
                card.HitRateText = s.CacheHitRate < 0 ? "—" : (s.CacheHitRate * 100).ToString("0.#") + "%";
                card.StatsLine = RequestsDisplay(s) + " 请求 · " + s.SessionCount + " 会话";
                if (t > 0)
                {
                    card.BarWUncached = k * s.Totals.UncachedInput / t;
                    card.BarWCacheRead = k * s.Totals.CacheRead / t;
                    card.BarWCacheWrite = k * s.Totals.CacheWrite / t;
                    card.BarWOutput = k * s.Totals.Output / t;
                }
                var daily = UsageQuery.MergeDaily(usage.Models, from, null);
                List<KeyValuePair<string, TokenBuckets>> rows = daily.ToList();
                if (rows.Count > 14) rows = rows.Skip(rows.Count - 14).ToList();
                long mx = rows.Count > 0 ? Math.Max(1, rows.Max(r => r.Value.Total)) : 1;
                card.SparkHeights = rows.Select(r => 2.0 + 22.0 * r.Value.Total / mx).ToList();
                InstanceCards.Add(card);
            }
            UsageSummary g = _summary;   // 全部实例的本帧汇总（合计条）
            GrandTotalText = "合计：总 " + UsageQuery.FormatTokens(g.Totals.Total) +
                             " · " + RequestsDisplay(g) + " 请求 · " + g.SessionCount + " 会话" +
                             (g.CacheHitRate >= 0 ? " · 命中 " + (g.CacheHitRate * 100).ToString("0.#") + "%" : "");
        }

        /// <summary>运行徽标经 façade 透出（定稿 §5：IArchiveFacade.IsRunning，仍是档案读、不新取数）。</summary>
        private bool IsArchiveRunning(InstanceArchive archive)
        {
            return archive != null && _hub.IsRunning(archive.ArchiveId);
        }

        private void OpenInstance(string archiveId)
        {
            UsageScopeItem item = Scopes.FirstOrDefault(s =>
                string.Equals(s.ArchiveId, archiveId, StringComparison.OrdinalIgnoreCase));
            if (item != null) SelectedScope = item;   // 触发 Reload → 聚焦视图
        }
    }
}