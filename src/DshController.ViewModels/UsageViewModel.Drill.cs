// ============================================================================
//  UsageViewModel 按天页内钻取（partial 之二，定稿方案 D3-A）
//
//  全部数据来自本帧 _summary 与 _sessionLabels——无 IO、无取数。
//  2026-09-06 二次改版：实例卡移除后本文件只剩按天钻取；聚合会话行标题
//  带实例来源前缀（找不到标签时优雅降级，不加前缀）。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
                if (_sessionLabels.TryGetValue(s, out string label) && !string.IsNullOrEmpty(label))
                    row.Title = label + " · " + row.Title;   // 聚合视图：标注来源实例
                var self = row;
                self.ExpandCommand = new RelayCommand(() => self.IsExpanded = !self.IsExpanded);
                Sessions.Add(row);
            }
        }
    }
}
