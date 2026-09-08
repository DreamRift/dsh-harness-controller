// ============================================================================
//  UsageViewModel — 用量统计页（档案页用量看板 · 纯聚合口径）
//
//  规矩不变：数据只从实例档案来（ArchiveHub），页面不扫盘不起定时器；
//  状态 [ObservableProperty]，动作 [RelayCommand]，View 只有 x:Bind。
//  2026-09-06 二次改版——总计纯聚合（用户指令）：恒显示全部实例合并的用量，
//  实例卡/实例范围切换废除（只留时间切换），单实例完整用量移至
//  ArchiveMetaViewModel（单实例档案详情页）。按天钻取分居本文件与
//  UsageViewModel.Drill.cs（partial）。
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

        /// <summary>聚合会话 → 实例显示名的前缀映射（随 Reload 同帧重建，供会话行标注来源）。</summary>
        private Dictionary<UsageSessionStat, string> _sessionLabels = new Dictionary<UsageSessionStat, string>();

        /// <summary>加载世代计数：后台慢算完成后仅最新一代落界（快速切页/切范围不串台）。</summary>
        private int _loadGen;

        public UsageViewModel(IArchiveFacade hub, Action<string> log = null)
        {
            _hub = hub;
            _log = log;
            Models = new ObservableCollection<UsageModelRow>();
            Sessions = new ObservableCollection<UsageSessionRow>();
            DailyBars = new ObservableCollection<UsageDayBar>();
            Kpis = new ObservableCollection<UsageKpiRow>();
            TrendPoints = new ObservableCollection<UsageTrendPoint>();
            SetDashboardRange("7", reload: false);
        }

        public ObservableCollection<UsageModelRow> Models { get; }
        public ObservableCollection<UsageSessionRow> Sessions { get; }
        public ObservableCollection<UsageDayBar> DailyBars { get; }
        public ObservableCollection<UsageKpiRow> Kpis { get; }
        public ObservableCollection<UsageTrendPoint> TrendPoints { get; }

        [ObservableProperty] public partial bool IsBusy { get; set; }
        [ObservableProperty] public partial bool RefreshEnabled { get; set; } = true;
        [ObservableProperty] public partial bool EmptyVisible { get; set; } = true;

        partial void OnIsBusyChanged(bool value) => RefreshEnabled = !value;
        [ObservableProperty] public partial string StatusText { get; set; } = "";
        [ObservableProperty] public partial string UpdatedText { get; set; } = "";
        [ObservableProperty] public partial int RangeDays { get; set; } = 7;     // 0 = 全部，-1 = 自定义
        [ObservableProperty] public partial string RangeLabel { get; set; } = "近 7 天";
        [ObservableProperty] public partial bool HasData { get; set; }
        [ObservableProperty] public partial bool EmptyCanChangeRange { get; set; }
        [ObservableProperty] public partial string EmptyText { get; set; } =
            "全部档案都还没有用量数据。点「刷新」立即采集一次，或启动实例产生会话后再来看。";

        // KPI
        [ObservableProperty] public partial string TotalTokensText { get; set; } = "—";
        [ObservableProperty] public partial string RequestsText { get; set; } = "—";
        [ObservableProperty] public partial string SessionsText { get; set; } = "—";
        [ObservableProperty] public partial string HitRateText { get; set; } = "—";
        [ObservableProperty] public partial string ActiveDaysText { get; set; } = "—";
        [ObservableProperty] public partial string TopModelText { get; set; } = "—";
        [ObservableProperty] public partial string CompletenessText { get; set; } = "";
        [ObservableProperty] public partial string HeroSubText { get; set; } = "";
        [ObservableProperty] public partial string UsageRangeKey { get; set; } = "7";
        [ObservableProperty] public partial DateTimeOffset? UsageRangeStart { get; set; }
        [ObservableProperty] public partial DateTimeOffset? UsageRangeEnd { get; set; }
        [ObservableProperty] public partial string UsageRangeError { get; set; } = "";
        [ObservableProperty] public partial string TrendMetric { get; set; } = "tokens";
        [ObservableProperty] public partial string TrendTotalText { get; set; } = "—";
        /// <summary>按天柱图截断标注（W3：「全部」档超过 60 根不再静默截断）。</summary>
        [ObservableProperty] public partial string BarsNote { get; set; } = "";
        // hero 四桶堆叠条像素宽（卡宽固定，比例×330）
        [ObservableProperty] public partial double BarWUncached { get; set; }
        [ObservableProperty] public partial double BarWCacheRead { get; set; }
        [ObservableProperty] public partial double BarWCacheWrite { get; set; }
        [ObservableProperty] public partial double BarWOutput { get; set; }

        /// <summary>柱状图画布高度（View 端固定，VM 据此算每根柱子的像素高度）。</summary>
        public double ChartHeight { get; set; } = 120;

        /// <summary>当前汇总快照（按天钻取共用；纯内存，永不取数）。</summary>
        private UsageSummary _summary = new UsageSummary();

        // ==================== 生命周期 ====================

        /// <summary>页面进入：从档案渲染全部实例的合并用量（不触发采集）。
        /// 同步便捷入口（测试与既有调用方走这里）；View 首开走 ReloadAsync（W6：重算移出 UI 线程）。</summary>
        public void OnShown() => Reload();

        // ==================== 渲染 ====================

        /// <summary>一帧重建的计算结果（后台线程产出，UI 线程落界；中间不碰 Observable 集合）。</summary>
        private sealed class UsageFrame
        {
            public UsageSummary Summary = new UsageSummary();
            public Dictionary<UsageSessionStat, string> SessionLabels = new Dictionary<UsageSessionStat, string>();
            public bool HasData;
            public bool EmptyCanChangeRange;
            public string EmptyText = "";
            public string TotalTokensText = "—", RequestsText = "—", SessionsText = "—",
                HitRateText = "—", ActiveDaysText = "—", TopModelText = "—", CompletenessText = "";
            public string HeroSubText = "";
            public double BarWUncached, BarWCacheRead, BarWCacheWrite, BarWOutput;
            public List<UsageModelRow> ModelRows = new List<UsageModelRow>();
            public List<UsageDayBar> Bars = new List<UsageDayBar>();
            public List<UsageKpiRow> Kpis = new List<UsageKpiRow>();
            public List<UsageTrendPoint> TrendPoints = new List<UsageTrendPoint>();
            public string BarsNote = "";
            public string StatusText = "";
        }

        /// <summary>异步重建（W6）：快照收集留在调用线程（轻），Summarize/行构造移入后台线程，
        /// 完成后仅最新世代落界——快速切页/切范围时旧结果被丢弃，不串台。</summary>
        public async Task ReloadAsync()
        {
            int gen = ++_loadGen;
            int rangeDays = RangeDays;
            DateTime? from = _rangeFrom, to = _rangeTo;
            double chartHeight = ChartHeight;
            List<(InstanceArchive Archive, UsageFacetData Usage)> pairs = CollectPairs(out DateTime? newest);
            UsageFrame frame = await Task.Run(() => ComputeFrame(pairs, from, to, rangeDays, chartHeight));
            if (gen != _loadGen) return;
            ApplyFrame(frame, newest);
        }

        /// <summary>同步重建（纯内存，无 IO；与 ReloadAsync 共用同一套计算与落界）。</summary>
        public void Reload()
        {
            _loadGen++;
            List<(InstanceArchive Archive, UsageFacetData Usage)> pairs = CollectPairs(out DateTime? newest);
            ApplyFrame(ComputeFrame(pairs, _rangeFrom, _rangeTo, RangeDays, ChartHeight), newest);
        }

        /// <summary>后台线程：全量会话遍历 + 汇总排序 + 行/柱构造（W6 的重活全在这）。
        /// 实例方法但只读参数与静态——不碰 Observable 状态，后台执行安全。</summary>
        private UsageFrame ComputeFrame(
            List<(InstanceArchive Archive, UsageFacetData Usage)> pairs,
            DateTime? from, DateTime? to, int rangeDays, double chartHeight)
        {
            var f = new UsageFrame();

            var labels = new Dictionary<UsageSessionStat, string>();
            foreach ((InstanceArchive archive, UsageFacetData usage) in pairs)
            {
                if (archive == null || usage == null || usage.Sessions == null) continue;
                string label = InstanceDisplayName.ForArchive(archive);
                foreach (UsageSessionStat s in usage.Sessions)
                {
                    if (s != null) labels[s] = label;
                }
            }
            f.SessionLabels = labels;

            UsageSummary summary = UsageQuery.Summarize(
                pairs.Select(p => p.Usage).Where(u => u != null), from, to);
            f.Summary = summary;

            f.HasData = pairs.Count > 0 && (summary.Totals.Total > 0 || summary.SessionCount > 0);
            f.EmptyCanChangeRange = pairs.Count > 0 && !f.HasData;
            f.TotalTokensText = UsageQuery.FormatTokens(summary.Totals.Total);
            f.RequestsText = RequestsDisplay(summary);
            f.SessionsText = summary.SessionCount.ToString("N0");
            f.HitRateText = summary.CacheHitRate < 0 ? "—" : (summary.CacheHitRate * 100).ToString("0.#") + "%";
            f.ActiveDaysText = summary.ActiveDays.ToString("N0");
            f.TopModelText = string.IsNullOrEmpty(summary.TopModel) ? "—" : summary.TopModel;
            f.CompletenessText = summary.ModelsComplete
                ? ""
                : "部分实例的会话日志未能完整解析，按模型/按天的数据可能偏低。";

            long total = summary.Totals.Total;
            if (total > 0)
            {
                const double k = 330;
                f.BarWUncached = k * summary.Totals.UncachedInput / total;
                f.BarWCacheRead = k * summary.Totals.CacheRead / total;
                f.BarWCacheWrite = k * summary.Totals.CacheWrite / total;
                f.BarWOutput = k * summary.Totals.Output / total;
                f.HeroSubText = "未缓存 " + Pct(summary.Totals.UncachedInput, total) +
                                " · 缓存读 " + Pct(summary.Totals.CacheRead, total) +
                                " · 缓存写 " + Pct(summary.Totals.CacheWrite, total) +
                                " · 输出 " + Pct(summary.Totals.Output, total);
            }

            long grand = summary.Models.Sum(m => m.Totals.Total);
            foreach (UsageModelStat m in summary.Models.Take(30))
                f.ModelRows.Add(UsageModelRow.From(m, grand));

            f.Kpis = UsageDashboardBuilder.BuildKpis(summary);
            DateTime trendFrom = from ?? UsageDashboardBuilder.WindowStart(summary, DateTime.Now.Date);
            DateTime trendTo = to ?? DateTime.Now.Date;
            f.TrendPoints = UsageDashboardBuilder.BuildTrend(summary, trendFrom, trendTo);

            f.Bars = BuildBars(summary, rangeDays, chartHeight, out string barsNote);
            f.BarsNote = barsNote;
            f.StatusText = f.HasData
                ? summary.Models.Count + " 个模型 · " + summary.SessionCount + " 个会话"
                : "";
            f.EmptyText = pairs.Count == 0
                ? "全部档案都还没有用量数据。点「刷新」立即采集一次，或启动实例产生会话后再来看。"
                : "所选时间范围内没有用量记录，换个范围试试。";
            return f;
        }

        /// <summary>UI 线程：把后台算好的一帧原子落界（同帧重建集合，无中间空白态）。</summary>
        private void ApplyFrame(UsageFrame f, DateTime? newest)
        {
            _summary = f.Summary;
            _sessionLabels = f.SessionLabels;

            HasData = f.HasData;
            EmptyVisible = !f.HasData;
            TotalTokensText = f.TotalTokensText;
            RequestsText = f.RequestsText;
            SessionsText = f.SessionsText;
            HitRateText = f.HitRateText;
            ActiveDaysText = f.ActiveDaysText;
            TopModelText = f.TopModelText;
            CompletenessText = f.CompletenessText;
            EmptyCanChangeRange = f.EmptyCanChangeRange;
            BarsNote = f.BarsNote;

            BarWUncached = f.BarWUncached;
            BarWCacheRead = f.BarWCacheRead;
            BarWCacheWrite = f.BarWCacheWrite;
            BarWOutput = f.BarWOutput;
            HeroSubText = f.HeroSubText;

            Models.Clear();
            foreach (UsageModelRow row in f.ModelRows) Models.Add(row);

            DailyBars.Clear();
            foreach (UsageDayBar bar in f.Bars) DailyBars.Add(bar);

            Kpis.Clear();
            foreach (UsageKpiRow row in f.Kpis) Kpis.Add(row);
            TrendPoints.Clear();
            foreach (UsageTrendPoint point in f.TrendPoints)
            {
                point.SelectCommand = new RelayCommand(() => SelectTrendPoint(point));
                TrendPoints.Add(point);
            }
            TrendTotalText = TrendMetric == "requests" ? RequestsText : TotalTokensText;

            ResetDrill();                       // 时间范围切换即清钻取（方案 §3.2 一致性）

            UpdatedText = newest.HasValue
                ? "档案更新于 " + newest.Value.ToLocalTime().ToString("MM-dd HH:mm")
                : "尚未采集";
            StatusText = f.StatusText;
            EmptyText = f.EmptyText;
        }

        /// <summary>恒取全部非空用量（2026-09-06 二次改版：看板只看合并口径）。
        /// 只做快照收集（轻，留调用线程）；Summarize/行构造在后台帧里做（W6）。</summary>
        private List<(InstanceArchive Archive, UsageFacetData Usage)> CollectPairs(out DateTime? newestCollectedAt)
        {
            var list = new List<(InstanceArchive, UsageFacetData)>();
            newestCollectedAt = null;
            foreach ((InstanceArchive archive, UsageFacetData usage) in _hub.AllUsage())
            {
                if (usage == null || archive == null) continue;
                list.Add((archive, usage));
                DateTime? at = archive.Facet(FacetNames.Usage).LastGoodAt;
                if (at.HasValue && (!newestCollectedAt.HasValue || at > newestCollectedAt)) newestCollectedAt = at;
            }
            return list;
        }

        private static string Pct(long v, long total) => (v * 100.0 / total).ToString("0.#") + "%";

        /// <summary>请求数显示口径（唯一实现）：日志不完整时带 + 号。</summary>
        private static string RequestsDisplay(UsageSummary s) =>
            s.ModelsComplete ? s.RequestCount.ToString("N0") : s.RequestCount.ToString("N0") + "+";

        /// <summary>按天柱构造（W3：「全部」档超出 60 根不再静默截断，附标注文案）。</summary>
        private List<UsageDayBar> BuildBars(
            UsageSummary summary, int rangeDays, double chartHeight, out string barsNote)
        {
            var bars = new List<UsageDayBar>();
            barsNote = "";
            if (summary.Daily.Count == 0) return bars;

            int take = rangeDays > 0 ? rangeDays : 60;
            List<KeyValuePair<string, TokenBuckets>> days = summary.Daily.ToList();
            if (rangeDays == 0 && days.Count > take)
                barsNote = "按天图仅显示最近 " + take + " 天（共 " + days.Count + " 天），切换时间范围可查看更早记录。";
            if (days.Count > take) days = days.Skip(days.Count - take).ToList();
            long max = days.Max(kv => kv.Value.Total);
            if (max <= 0) return bars;

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
                string day = kv.Key;   // 闭包捕获；命令在 UI 点击时执行（UI 线程），此时 _summary 已同帧就位
                bar.ToggleCommand = new RelayCommand(() => ToggleDay(day));
                bars.Add(bar);
            }
            foreach (UsageDayBar bar in bars) bar.BarHeight = Math.Max(2, bar.HeightRatio * chartHeight);
            return bars;
        }

        // ==================== 命令 ====================

        [RelayCommand]
        private void SetRange(string days)
        {
            SetDashboardRange(days, reload: true);
        }

        /// <summary>强制重采：全部未退役实例（退役档案没有实例可采，只重新渲染）。</summary>
        [RelayCommand]
        private async Task RefreshAsync()
        {
            if (IsBusy) return;
            IsBusy = true;
            StatusText = "正在采集用量…（会话日志较多时需要几秒）";
            int ok = 0, bad = 0;
            try
            {
                List<string> targets = _hub.AllUsage()
                    .Where(t => t.Archive != null && !t.Archive.IsRetired)
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
                await ReloadAsync();
                StatusText = targets.Count == 0
                    ? "没有未退役实例可采，仅重新渲染档案"
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
