// ============================================================================
//  ArchiveMetaViewModel.Usage — 档案详情页「单实例完整用量」（partial 之二）
//
//  2026-09-06 二次改版：用量看板改成纯聚合后，单实例完整用量并入档案详情页
//  （大数卡 / hero 四桶 / 按天钻取 / 模型排行 / 会话明细 / 单档案重采）。
//  全部数据来自 Show(archiveId) 时捕获的 usage 快照与档案元数据——
//  无扫描、无探针、无网络；重采走 IArchiveFacade.RefreshAsync 单档案单分面。
//  口径与 UsageViewModel（看板）完全一致：Summarize 全期 = projcache 权威总账，
//  hero 四桶比例 ×330，按天柱取最后 30 天（看板 60、详情卡窄），
//  钻取用 DayComposition / SessionsOnDay；会话行不加实例前缀（单实例视图）。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DshController.Core.Archive;
using DshController.Core.Usage;

namespace DshController.ViewModels
{
    public sealed partial class ArchiveMetaViewModel
    {
        // ---------------- 集合（View 用 x:Bind ItemsSource） ----------------

        public ObservableCollection<UsageModelRow> Models { get; } = new ObservableCollection<UsageModelRow>();
        public ObservableCollection<UsageSessionRow> Sessions { get; } = new ObservableCollection<UsageSessionRow>();
        public ObservableCollection<UsageDayBar> DailyBars { get; } = new ObservableCollection<UsageDayBar>();

        /// <summary>按天柱状图画布高度（View 端固定，VM 据此算每根柱子的像素高度）。</summary>
        public double UsageChartHeight { get; set; } = 120;

        // 按天钻取（与 UsageViewModel.Drill 同款四件套）
        [ObservableProperty] public partial string SelectedDay { get; set; } = "";
        [ObservableProperty] public partial bool HasDay { get; set; }
        [ObservableProperty] public partial string DayStripText { get; set; } = "";
        [ObservableProperty] public partial string DayModelsText { get; set; } = "";

        /// <summary>按天柱图截断标注（W3：详情卡取最后 30 天，超出不再静默）。</summary>
        [ObservableProperty] public partial string UsageBarsNote { get; set; } = "";

        /// <summary>当前汇总快照（按天钻取共用；纯内存，永不取数）。</summary>
        private UsageSummary _usageSummary = new UsageSummary();

        /// <summary>Show 时捕获的当前档案与 usage 快照（钻取/重建共用，无 IO）。</summary>
        private InstanceArchive _currentArchive;
        private UsageFacetData _currentUsage;

        // ==================== 构建 ====================

        /// <summary>无档案/无数据路径的整体清空（空态文案与更新时间由本方法定）。</summary>
        private void ClearUsage()
        {
            ResetUsageNumbers();
            UsageEmptyText = "";
            UsageUpdatedText = "尚未采集";
            UsageRefreshEnabled = false;
        }

        /// <summary>数字区复位（ClearUsage 与 BuildUsage 无数据路径共用；不动 UsageUpdatedText）。</summary>
        private void ResetUsageNumbers()
        {
            HasUsage = false;
            TotalTokensText = HitRateText = RequestsText = SessionsText = ActiveDaysText = TopModelText = "—";
            HeroSubText = "";
            BarWUncached = BarWCacheRead = BarWCacheWrite = BarWOutput = 0;
            Models.Clear();
            Sessions.Clear();
            DailyBars.Clear();
            TrendPoints.Clear();
            Kpis.Clear();
            UsageBarsNote = "";
            SelectedDay = "";
            HasDay = false;
            DayStripText = "";
            DayModelsText = "";
            AverageTtftText = "—";
            TtftSampleText = "无有效步骤样本";
            TrendTotalText = "—";
        }

        /// <summary>用 Show 捕获的快照重建单实例用量区（纯内存）。</summary>
        private void BuildUsage(InstanceArchive archive, UsageFacetData usage)
        {
            _currentArchive = archive;
            _currentUsage = usage;

            DateTime? lastGood = _currentArchive.Facet(FacetNames.Usage).LastGoodAt;
            UsageUpdatedText = lastGood.HasValue
                ? "档案更新于 " + lastGood.Value.ToLocalTime().ToString("MM-dd HH:mm")
                : "尚未采集";
            UsageRefreshEnabled = HasArchive && !IsRetired && !UsageBusy;

            if (_currentUsage == null ||
                (_currentUsage.Totals.Total <= 0 && !_currentUsage.Sessions.Any(s => s != null && s.HasTokenUsage)))
            {
                ResetUsageNumbers();                     // KPI/条宽/集合/钻取清空，保留 UsageUpdatedText
                UsageEmptyText = "该档案还没有用量数据。活跃实例产生会话并采集后，这里会显示统计。";
                return;
            }

            SetUsageRange(UsageRangeKey);                // 只重算当前档案内存快照，默认近 7 天
            HasUsage = true;
        }

        /// <summary>hero 四桶堆叠条（与 UsageViewModel.BuildHeroBars 同口径，比例×330）。</summary>
        private void BuildMetaHeroBars(UsageSummary s)
        {
            long t = s.Totals.Total;
            if (t <= 0)
            {
                BarWUncached = BarWCacheRead = BarWCacheWrite = BarWOutput = 0;
                HeroSubText = "";
                return;
            }
            const double k = 330;
            BarWUncached = k * s.Totals.UncachedInput / t;
            BarWCacheRead = k * s.Totals.CacheRead / t;
            BarWCacheWrite = k * s.Totals.CacheWrite / t;
            BarWOutput = k * s.Totals.Output / t;
            HeroSubText = "未缓存 " + Pct(s.Totals.UncachedInput, t) +
                          " · 缓存读 " + Pct(s.Totals.CacheRead, t) +
                          " · 缓存写 " + Pct(s.Totals.CacheWrite, t) +
                          " · 输出 " + Pct(s.Totals.Output, t);
        }

        private static string Pct(long v, long total) => (v * 100.0 / total).ToString("0.#") + "%";

        /// <summary>按天柱状图（详情卡窄，取最后 30 天；口径同 UsageViewModel.BuildBars；
        /// W3：超出 30 根不再静默截断，附标注文案）。</summary>
        private void BuildMetaBars(UsageSummary s)
        {
            DailyBars.Clear();
            UsageBarsNote = s.Daily.Count > 30
                ? "按天图仅显示最近 30 天（共 " + s.Daily.Count + " 天）。"
                : "";
            if (s.Daily.Count == 0) return;

            List<KeyValuePair<string, TokenBuckets>> days = s.Daily.ToList();
            if (days.Count > 30) days = days.Skip(days.Count - 30).ToList();
            long max = days.Max(kv => kv.Value.Total);
            if (max <= 0) return;

            foreach (KeyValuePair<string, TokenBuckets> kv in days)
            {
                TokenBuckets b = kv.Value;
                var bar = new UsageDayBar
                {
                    Day = kv.Key,
                    ShortDay = kv.Key.Length >= 10 ? kv.Key.Substring(5) : kv.Key,
                    Total = b.Total,
                    HeightRatio = (double)b.Total / max,
                    ToolTip = kv.Key + "：" + UsageQuery.FormatTokens(b.Total) + " tokens\n" +
                              "未缓存 " + UsageQuery.FormatTokens(b.UncachedInput) +
                              " · 缓存读 " + UsageQuery.FormatTokens(b.CacheRead) +
                              " · 缓存写 " + UsageQuery.FormatTokens(b.CacheWrite) +
                              " · 输出 " + UsageQuery.FormatTokens(b.Output) +
                              "\n点击钻取当天"
                };
                string day = kv.Key;   // 闭包捕获
                bar.ToggleCommand = new RelayCommand(() => ToggleUsageDay(day));
                DailyBars.Add(bar);
            }
            foreach (UsageDayBar bar in DailyBars) bar.BarHeight = Math.Max(2, bar.HeightRatio * UsageChartHeight);
        }

        // ==================== 按天页内钻取（与 UsageViewModel.Drill 同款） ====================

        /// <summary>清钻取：会话列表回到全量（BuildUsage 重建与「清除」共用）。</summary>
        private void ResetUsageDrill()
        {
            SelectedDay = "";
            HasDay = false;
            DayStripText = "";
            DayModelsText = "";
            _selectedFrom = null;
            _selectedTo = null;
            foreach (UsageDayBar bar in DailyBars) bar.IsSelected = false;   // 柱子的选中环一并摘掉
            RebuildMetaSessions(filtered: false);
        }

        private void ToggleUsageDay(string day)
        {
            if (string.IsNullOrEmpty(day)) return;
            if (SelectedDay == day) { ResetUsageDrill(); return; }   // 再点同柱 = 退出

            SelectedDay = day;
            if (DateTime.TryParse(day, out DateTime selected))
            {
                _selectedFrom = selected.Date;
                _selectedTo = selected.Date;
            }
            HasDay = true;
            TokenBuckets b = _usageSummary.Daily.TryGetValue(day, out TokenBuckets d) ? d : new TokenBuckets();
            DayStripText = day + " 当日：未缓存输入 " + UsageQuery.FormatTokens(b.UncachedInput) +
                           " · 缓存读 " + UsageQuery.FormatTokens(b.CacheRead) +
                           " · 缓存写 " + UsageQuery.FormatTokens(b.CacheWrite) +
                           " · 输出 " + UsageQuery.FormatTokens(b.Output);
            var comp = UsageQuery.DayComposition(_usageSummary.Models, day);
            DayModelsText = comp.Count == 0 ? "" : "当日模型：" + string.Join(" · ",
                comp.Take(3).Select(c => c.DisplayName + " " + (c.Share * 100).ToString("0.#") + "%")) +
                (comp.Count > 3 ? " · 其它 " + (comp.Skip(3).Sum(c => c.Share) * 100).ToString("0.#") + "%" : "");
            foreach (UsageDayBar bar in DailyBars) bar.IsSelected = bar.Day == day;
            RebuildMetaSessions(filtered: true);
        }

        [RelayCommand]
        private void ClearUsageDay() => ResetUsageDrill();

        private void RebuildMetaSessions(bool filtered)
        {
            Sessions.Clear();
            IEnumerable<UsageSessionStat> src = filtered && _selectedFrom.HasValue && _selectedTo.HasValue
                ? _usageSummary.Sessions.Where(s => s != null && s.HasTokenUsage &&
                    UsageParser.DayKey(s.CreatedAtMs).CompareTo(_selectedFrom.Value.ToString("yyyy-MM-dd")) >= 0 &&
                    UsageParser.DayKey(s.CreatedAtMs).CompareTo(_selectedTo.Value.ToString("yyyy-MM-dd")) <= 0)
                : (IEnumerable<UsageSessionStat>)(_usageSummary.Sessions ?? new List<UsageSessionStat>());
            foreach (UsageSessionStat st in src.Take(200))
            {
                var row = UsageSessionRow.From(st);          // 单实例视图：不加实例名前缀
                var self = row;
                self.ExpandCommand = new RelayCommand(() => self.IsExpanded = !self.IsExpanded);
                Sessions.Add(row);
            }
        }

        // ==================== 命令 ====================

        /// <summary>单档案重采用量：只刷当前档案的 usage 分面，成功后 Show 重读快照重建。</summary>
        [RelayCommand]
        private async Task RefreshUsageAsync()
        {
            if (UsageBusy) return;
            if (!HasArchive || IsRetired || string.IsNullOrEmpty(CurrentArchiveId)) return;
            UsageBusy = true;
            try
            {
                FacetSnapshot snapshot = await _facade.RefreshAsync(CurrentArchiveId, FacetNames.Usage);
                if (snapshot.Status == FacetStatus.Failed)
                { _log?.Invoke("[用量] " + CurrentArchiveId + " 采集失败：" + snapshot.Error); }
                else if (snapshot.Status == FacetStatus.Skipped)
                { _log?.Invoke("[用量] " + CurrentArchiveId + " 跳过：" + snapshot.SkipReason); }
                Show(CurrentArchiveId);                      // 重读快照重建（纯内存）
            }
            catch (Exception ex)
            {
                _log?.Invoke("[用量] 采集异常：" + ex.Message);
            }
            finally
            {
                UsageBusy = false;
            }
        }
    }
}
