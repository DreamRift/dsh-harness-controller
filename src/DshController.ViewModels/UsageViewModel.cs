// ============================================================================
//  UsageViewModel — 用量统计页（按 usage-redesign-proposal.md 定稿改版）
//
//  规矩不变：数据只从实例档案来（ArchiveHub），页面不扫盘不起定时器；
//  状态 [ObservableProperty]，动作 [RelayCommand]，View 只有 x:Bind。
//  本版新增：统一视图实例卡（D2-A）、hero 四桶堆叠（D1-A）、
//  按天页内钻取（D3-A）、会话行展开、刷新摘要与空态可行动（均见方案 §3）。
//  钻取/实例卡实现分居本文件与 UsageViewModel.Drill.cs（partial）。
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
    public partial class UsageViewModel : ObservableObject
    {
        private readonly IArchiveFacade _hub;
        private readonly Action<string> _log;

        public UsageViewModel(IArchiveFacade hub, Action<string> log = null)
        {
            _hub = hub;
            _log = log;
            Scopes = new ObservableCollection<UsageScopeItem>();
            Models = new ObservableCollection<UsageModelRow>();
            Sessions = new ObservableCollection<UsageSessionRow>();
            DailyBars = new ObservableCollection<UsageDayBar>();
            InstanceCards = new ObservableCollection<UsageInstanceCardRow>();
        }

        public ObservableCollection<UsageScopeItem> Scopes { get; }
        public ObservableCollection<UsageModelRow> Models { get; }
        public ObservableCollection<UsageSessionRow> Sessions { get; }
        public ObservableCollection<UsageDayBar> DailyBars { get; }
        public ObservableCollection<UsageInstanceCardRow> InstanceCards { get; }

        [ObservableProperty] public partial bool IsBusy { get; set; }
        [ObservableProperty] public partial bool RefreshEnabled { get; set; } = true;
        [ObservableProperty] public partial bool EmptyVisible { get; set; } = true;

        partial void OnIsBusyChanged(bool value) => RefreshEnabled = !value;
        [ObservableProperty] public partial string StatusText { get; set; } = "";
        [ObservableProperty] public partial string UpdatedText { get; set; } = "";
        [ObservableProperty] public partial UsageScopeItem SelectedScope { get; set; }
        [ObservableProperty] public partial int RangeDays { get; set; }          // 0 = 全部
        [ObservableProperty] public partial string RangeLabel { get; set; } = "全部时间";
        [ObservableProperty] public partial bool HasData { get; set; }
        [ObservableProperty] public partial bool IsAllView { get; set; } = true;  // 统一视图
        [ObservableProperty] public partial bool ShowFocus { get; set; }          // 聚焦视图
        [ObservableProperty] public partial bool EmptyCanChangeRange { get; set; }
        [ObservableProperty] public partial string EmptyText { get; set; } =
            "还没有用量数据。启动实例并产生会话后，这里会自动出现统计。";

        // KPI
        [ObservableProperty] public partial string TotalTokensText { get; set; } = "—";
        [ObservableProperty] public partial string RequestsText { get; set; } = "—";
        [ObservableProperty] public partial string SessionsText { get; set; } = "—";
        [ObservableProperty] public partial string HitRateText { get; set; } = "—";
        [ObservableProperty] public partial string ActiveDaysText { get; set; } = "—";
        [ObservableProperty] public partial string TopModelText { get; set; } = "—";
        [ObservableProperty] public partial string CompletenessText { get; set; } = "";
        [ObservableProperty] public partial string HeroSubText { get; set; } = "";
        [ObservableProperty] public partial string GrandTotalText { get; set; } = "";
        // hero 四桶堆叠条像素宽（卡宽固定，比例×330）
        [ObservableProperty] public partial double BarWUncached { get; set; }
        [ObservableProperty] public partial double BarWCacheRead { get; set; }
        [ObservableProperty] public partial double BarWCacheWrite { get; set; }
        [ObservableProperty] public partial double BarWOutput { get; set; }

        /// <summary>柱状图画布高度（View 端固定，VM 据此算每根柱子的像素高度）。</summary>
        public double ChartHeight { get; set; } = 120;

        /// <summary>当前汇总快照（钻取/实例卡共用；纯内存，永不取数）。</summary>
        private UsageSummary _summary = new UsageSummary();

        // ==================== 生命周期 ====================

        /// <summary>页面进入：重建范围下拉并从档案渲染（不触发采集）。</summary>
        public void OnShown()
        {
            RebuildScopes();
            Reload();
        }

        private void RebuildScopes()
        {
            string keep = SelectedScope?.ArchiveId ?? "";
            Scopes.Clear();
            Scopes.Add(new UsageScopeItem { ArchiveId = "", Label = "全部实例（含已删除）" });
            foreach ((InstanceArchive archive, UsageFacetData usage) in _hub.AllUsage())
            {
                if (archive == null) continue;
                Scopes.Add(new UsageScopeItem
                {
                    ArchiveId = archive.ArchiveId,
                    IsRetired = archive.IsRetired,
                    Label = InstanceDisplayName.ForArchive(archive) + "（" + InstanceDisplayName.TooltipForArchive(archive) + "）" +
                            (archive.IsRetired ? "（已删除 · 档案保留）" : "") +
                            (usage == null ? "（无用量数据）" : "")
                });
            }
            UsageScopeItem match = Scopes.FirstOrDefault(s =>
                string.Equals(s.ArchiveId, keep, StringComparison.OrdinalIgnoreCase));
            SelectedScope = match ?? Scopes[0];
        }

        partial void OnSelectedScopeChanged(UsageScopeItem value) => Reload();

        // ==================== 渲染 ====================

        /// <summary>从档案重算并刷新界面（纯内存，无 IO）。范围/实例切换即走这里：
        /// 同帧原子重建集合，不存在中间空白态（无白屏承诺的机制面）。</summary>
        public void Reload()
        {
            List<UsageFacetData> sources = CollectSources(out DateTime? newest);
            DateTime? from = RangeDays > 0 ? DateTime.Now.Date.AddDays(-(RangeDays - 1)) : (DateTime?)null;
            UsageSummary summary = UsageQuery.Summarize(sources, from, null);
            _summary = summary;

            IsAllView = SelectedScope == null || SelectedScope.IsAll;
            ShowFocus = !IsAllView;
            HasData = sources.Count > 0 && (summary.Totals.Total > 0 || summary.SessionCount > 0);
            EmptyVisible = !HasData;
            TotalTokensText = UsageQuery.FormatTokens(summary.Totals.Total);
            RequestsText = RequestsDisplay(summary);
            SessionsText = summary.SessionCount.ToString("N0");
            HitRateText = summary.CacheHitRate < 0 ? "—" : (summary.CacheHitRate * 100).ToString("0.#") + "%";
            ActiveDaysText = summary.ActiveDays.ToString("N0");
            TopModelText = string.IsNullOrEmpty(summary.TopModel) ? "—" : summary.TopModel;
            CompletenessText = summary.ModelsComplete
                ? ""
                : "部分实例的会话日志未能完整解析，按模型/按天的数据可能偏低。";
            EmptyCanChangeRange = sources.Count > 0 && !HasData;

            BuildHeroBars(summary);

            Models.Clear();
            long grand = summary.Models.Sum(m => m.Totals.Total);
            foreach (UsageModelStat m in summary.Models.Take(30))
                Models.Add(UsageModelRow.From(m, grand));

            BuildBars(summary);
            ResetDrill();                       // 范围/实例切换即清钻取（方案 §3.2 一致性）

            UpdatedText = newest.HasValue
                ? "档案更新于 " + newest.Value.ToLocalTime().ToString("MM-dd HH:mm")
                : "尚未采集";
            StatusText = HasData
                ? summary.Models.Count + " 个模型 · " + summary.SessionCount + " 个会话"
                : "";
            EmptyText = sources.Count == 0
                ? "所选范围内还没有用量档案。点「刷新」立即采集一次，或启动实例产生会话后再来看。"
                : "所选时间范围内没有用量记录，换个范围试试。";

            if (IsAllView) BuildInstanceCards(sources, from);
            else InstanceCards.Clear();
        }

        private void BuildHeroBars(UsageSummary summary)
        {
            long t = summary.Totals.Total;
            if (t <= 0)
            {
                BarWUncached = BarWCacheRead = BarWCacheWrite = BarWOutput = 0;
                HeroSubText = "";
                return;
            }
            const double k = 330;
            BarWUncached = k * summary.Totals.UncachedInput / t;
            BarWCacheRead = k * summary.Totals.CacheRead / t;
            BarWCacheWrite = k * summary.Totals.CacheWrite / t;
            BarWOutput = k * summary.Totals.Output / t;
            HeroSubText = "未缓存 " + Pct(summary.Totals.UncachedInput, t) +
                          " · 缓存读 " + Pct(summary.Totals.CacheRead, t) +
                          " · 缓存写 " + Pct(summary.Totals.CacheWrite, t) +
                          " · 输出 " + Pct(summary.Totals.Output, t);
        }

        private static string Pct(long v, long total) => (v * 100.0 / total).ToString("0.#") + "%";

        /// <summary>请求数显示口径（唯一实现）：日志不完整时带 + 号。KPI 与实例卡共用。</summary>
        private static string RequestsDisplay(UsageSummary s) =>
            s.ModelsComplete ? s.RequestCount.ToString("N0") : s.RequestCount.ToString("N0") + "+";

        private List<UsageFacetData> CollectSources(out DateTime? newestCollectedAt)
        {
            var list = new List<UsageFacetData>();
            newestCollectedAt = null;
            foreach ((InstanceArchive archive, UsageFacetData usage) in _hub.AllUsage())
            {
                if (usage == null || archive == null) continue;
                if (SelectedScope != null && !SelectedScope.IsAll &&
                    !string.Equals(archive.ArchiveId, SelectedScope.ArchiveId, StringComparison.OrdinalIgnoreCase))
                    continue;
                list.Add(usage);
                DateTime? at = archive.Facet(FacetNames.Usage).LastGoodAt;
                if (at.HasValue && (!newestCollectedAt.HasValue || at > newestCollectedAt)) newestCollectedAt = at;
            }
            return list;
        }

        private void BuildBars(UsageSummary summary)
        {
            DailyBars.Clear();
            if (summary.Daily.Count == 0) return;

            int take = RangeDays > 0 ? RangeDays : 60;
            List<KeyValuePair<string, TokenBuckets>> days = summary.Daily.ToList();
            if (days.Count > take) days = days.Skip(days.Count - take).ToList();
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
                bar.ToggleCommand = new RelayCommand(() => ToggleDay(day));
                DailyBars.Add(bar);
            }
            foreach (UsageDayBar bar in DailyBars) bar.BarHeight = Math.Max(2, bar.HeightRatio * ChartHeight);
        }

        // ==================== 命令 ====================

        [RelayCommand]
        private void SetRange(string days)
        {
            RangeDays = int.TryParse(days, out int d) ? d : 0;
            RangeLabel = RangeDays > 0 ? "最近 " + RangeDays + " 天" : "全部时间";
            Reload();
        }

        /// <summary>强制重采：当前范围内的实例（已退役档案没有实例可采，只重新渲染）。</summary>
        [RelayCommand]
        private async Task RefreshAsync()
        {
            if (IsBusy) return;
            IsBusy = true;
            StatusText = "正在采集用量…（会话日志较多时需要几秒）";
            int ok = 0, bad = 0;
            try
            {
                List<string> targets = SelectedScope != null && !SelectedScope.IsAll
                    ? new List<string> { SelectedScope.ArchiveId }
                    : _hub.AllUsage().Where(t => t.Archive != null && !t.Archive.IsRetired)
                          .Select(t => t.Archive.ArchiveId).ToList();

                foreach (string id in targets)
                {
                    FacetSnapshot s = await _hub.RefreshAsync(id, FacetNames.Usage);
                    if (s.Status == FacetStatus.Failed)
                    { _log?.Invoke("[用量] " + id + " 采集失败：" + s.Error); bad++; }
                    else if (s.Status == FacetStatus.Skipped)
                    { _log?.Invoke("[用量] " + id + " 跳过：" + s.SkipReason); bad++; }
                    else ok++;
                }
                RebuildScopes();
                Reload();
                StatusText = targets.Count == 0
                    ? "当前范围没有在线实例可采，仅重新渲染档案"
                    : "采集完成：成功 " + ok + " · 失败 " + bad;
            }
            catch (Exception ex)
            {
                _log?.Invoke("[用量] 采集异常：" + ex.Message);
                StatusText = "采集失败：" + ex.Message;
            }
            finally
            {
                IsBusy = false;
            }
        }
    }
}