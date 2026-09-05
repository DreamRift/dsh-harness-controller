// ============================================================================
//  UsageStatsPanel — 用量统计页（ZCode / Codex 风格改版）
//
//  结构（整页滚动，参照 ZCode「使用统计」+ Codex「用量统计」）：
//    - KPI 条（Codex 风格 5 卡：主值 + 明细副行）——随时间范围筛选；
//    - Token 活动热力图：26 周满宽 GitHub 风格网格（固定列宽单 Grid，月份标签
//      同 Grid 对齐），悬停显示当天 tokens / 请求次数——不随时间范围筛选；
//    - 时间范围筛选（近7日 / 近30日 / 全部 / 自定义起止）：作用于 KPI、趋势、
//      模型用量与会话明细（Token 活动例外）；
//    - 每日 Token 趋势：总消耗单条平滑曲线（Catmull-Rom→Bezier），悬停出现
//      细竖线剖面 + 沿曲线平滑移动的圆点 + 剖面数据卡（日期/tokens/请求）；
//    - 模型用量：环形占比（中心总量）+ 图例明细；
//    - 会话明细：独立视图（按创建时间随范围筛选）。
//
//  图表全部代码后置手绘（Border/Path/Line/Ellipse），不依赖图表库；颜色取自
//  DshTheme 品牌令牌，跟随明暗主题。数据层 Core/UsageStats 只读实例 HOME 文件。
//
//  已知坑：ListView 在 Collapsed 状态下被赋 ItemsSource 会导致 x:Bind 条目
//  不做布局（内容测得 0×0）——因此表格只在可见时赋值；自定义绘制区块不走
//  x:Bind，直接操作 UI 元素。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DshController.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;

namespace DshController
{
    // ==================== 列表行条目（会话明细 x:Bind 数据类） ====================

    public sealed class ScopeItem
    {
        public string Label { get; set; }
        public string InstanceId { get; set; }   // null = 全部实例
        public bool IsBackup { get; set; }
    }

    public sealed class UsageSessionRow
    {
        public string Title { get; set; }
        public string InstanceText { get; set; }
        public string CreatedText { get; set; }
        public string TurnsText { get; set; }
        public string InText { get; set; }
        public string CacheReadText { get; set; }
        public string CacheWriteText { get; set; }
        public string OutText { get; set; }
        public string TotalText { get; set; }
        public string ToolTip { get; set; }
    }

    // ==================== 面板 ====================

    public sealed partial class UsageStatsPanel : UserControl
    {
        private static readonly string[] ModelPalette =
        {
            "#3964FE", "#12A594", "#F5A524", "#E5534B", "#7B61FF"
        };
        private static readonly string OtherColor = "#98A2B3";

        /// <summary>常见 provider 的显示名称（未收录的按分词首字母大写回退）。</summary>
        private static readonly Dictionary<string, string> ProviderDisplayNames =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["deepseek-official"] = "DeepSeek 官方",
                ["deepseek"] = "DeepSeek",
                ["openrouter"] = "OpenRouter",
                ["siliconflow"] = "SiliconFlow 硅基流动",
                ["xinyunspace"] = "XinyunSpace",
                ["xinyunspace-gpt"] = "XinyunSpace GPT",
                ["staryears"] = "StarYears"
            };

        private InstanceRegistry _registry;
        private InstanceManager _instanceMgr;
        private Action<string> _console;
        private Microsoft.UI.Dispatching.DispatcherQueue _dq;
        private bool _busy;
        private bool _loadingScope;
        private string _view = "overview";
        private string _range = "30";            // 7 | 30 | all | custom
        private CancellationTokenSource _cts;
        private List<InstanceUsage> _results = new List<InstanceUsage>();
        private List<UsageSessionRow> _sessionRows = new List<UsageSessionRow>();
        private UsageReport _lastReport;
        private bool _modelsComplete;
        private bool _pickersInitialized;
        private bool _initialBackupStarted;
        private bool _shutdown;

        // 趋势图悬停剖面
        private List<double> _trendVals;
        private List<long> _trendReq;
        private List<string> _trendDays;
        private List<Point> _trendPoints;
        private double _trendPadL, _trendPlotW, _trendPadT, _trendPlotH, _trendMax;
        private Line _vLine;
        private Ellipse _vDot;
        private Border _vCard;
        private TextBlock _vCardDate, _vCardTok, _vCardReq;

        public UsageStatsPanel()
        {
            InitializeComponent();
            HeatMapHost.SizeChanged += (s, e) => { if (_lastReport != null && _view == "overview") BuildHeatmap(); };
            TrendHost.SizeChanged += (s, e) => { if (_lastReport != null && _view == "overview") RenderRangeSections(); };
            TrendHost.PointerMoved += TrendHost_PointerMoved;
            TrendHost.PointerExited += TrendHost_PointerExited;
        }

        public void Init(InstanceRegistry registry, InstanceManager instanceMgr, Action<string> console)
        {
            _registry = registry;
            _instanceMgr = instanceMgr;
            _console = console;
            _dq = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            _instanceMgr.InstanceClosing += OnInstanceClosingAsync;
            _instanceMgr.InstanceClosed += OnInstanceClosedAsync;
            RefreshInstances();
            ApplyL10n();
            SetRange("30", initial: true);
            SetView("overview");
            LoadBackupResults(render: true);
        }

        public void Shutdown()
        {
            _shutdown = true;
            try { _cts?.Cancel(); } catch { }
            try
            {
                if (_instanceMgr != null)
                {
                    _instanceMgr.InstanceClosing -= OnInstanceClosingAsync;
                    _instanceMgr.InstanceClosed -= OnInstanceClosedAsync;
                }
            }
            catch { }
        }

        public void OnShown()
        {
            RefreshInstances();
            LoadBackupResults(render: true);
        }

        /// <summary>
        /// 应用启动时执行一次全量源数据快照。页面平时只展示应用目录中的备份，
        /// 这次后台同步完成后再用备份重新渲染页面。
        /// </summary>
        public async Task InitializeBackupAsync()
        {
            if (_initialBackupStarted || _registry == null) return;
            _initialBackupStarted = true;
            try
            {
                List<InstanceDef> defs = _registry.Instances.ToList();
                if (defs.Count == 0) return;
                Log("[用量] 应用启动：正在创建本地用量备份…");
                // 初次快照放到线程池，避免应用启动时的本地文件解压/WSL 往返阻塞 UI。
                List<InstanceUsage> live = await Task.Run(
                    () => LoadLiveSnapshotsAsync(defs, CancellationToken.None));
                UsageStatsBackupStore.Merge(live, Log);
                RefreshInstances();
                LoadBackupResults(render: true);
                Log("[用量] 应用启动：本地用量备份已更新（" + UsageStatsBackupStore.FilePath + "）");
            }
            catch (Exception ex)
            {
                Log("[用量] 应用启动备份失败：" + ex.Message);
                LoadBackupResults(render: true);
            }
        }

        private void Log(string msg)
        {
            try { _console?.Invoke(msg); } catch { }
        }

        private void SetBusy(bool busy, string status)
        {
            BusyRing.IsActive = busy;
            TxtStatus.Text = status ?? "";
            if (!string.IsNullOrEmpty(status)) EmptyState.Visibility = Visibility.Collapsed;
        }

        private void ShowEmpty(string text)
        {
            TxtEmpty.Text = text;
            EmptyState.Visibility = Visibility.Visible;
        }

        // ==================== 实例范围 ====================

        private void RefreshInstances()
        {
            if (_registry == null) return;
            string keep = (CmbScope.SelectedItem as ScopeItem)?.InstanceId ?? "";
            var items = new List<ScopeItem>
            {
                new ScopeItem { Label = "全部实例（汇总）", InstanceId = null }
            };
            foreach (InstanceDef d in _registry.Instances.OrderBy(x => x.IsWsl).ThenBy(x => x.Name, StringComparer.CurrentCulture))
                items.Add(new ScopeItem { Label = d.PickerLabel, InstanceId = d.Id });
            foreach (InstanceDef d in UsageStatsBackupStore.HistoricalDefinitions(_registry.Instances))
                items.Add(new ScopeItem
                {
                    Label = L10n.T("[备份] ", "[Backup] ") + d.PickerLabel,
                    InstanceId = d.Id,
                    IsBackup = true
                });

            _loadingScope = true;
            try
            {
                CmbScope.ItemsSource = items;
                int idx = items.FindIndex(x => x.InstanceId == keep);
                CmbScope.SelectedIndex = idx >= 0 ? idx : 0;
            }
            finally { _loadingScope = false; }
        }

        private List<InstanceDef> CurrentDefs()
        {
            if (_registry == null) return new List<InstanceDef>();
            string id = (CmbScope.SelectedItem as ScopeItem)?.InstanceId;
            if (string.IsNullOrEmpty(id)) return _registry.Instances.ToList();
            return _registry.TryGet(id, out InstanceDef def) ? new List<InstanceDef> { def } : new List<InstanceDef>();
        }

        private void CmbScope_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loadingScope || _registry == null) return;
            // 切换范围只切换本地快照，不隐式读取实例 HOME；源数据同步由启动、
            // 手动刷新和实例关闭三个明确时机触发。
            LoadBackupResults(render: true);
        }

        private async void BtnRefresh_Click(object sender, RoutedEventArgs e)
        {
            await RefreshAsync();
        }

        private async void BtnWake_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            _busy = true;
            BtnWake.IsEnabled = false;
            SetBusy(true, L10n.T("正在唤醒 WSL 发行版…", "Waking WSL distro…"));
            try
            {
                foreach (string distro in CurrentDefs().Where(d => d.IsWsl)
                    .Select(d => d.WslDistro ?? "").Where(d => d.Length > 0).Distinct())
                {
                    Log("[用量] " + L10n.T("唤醒发行版 ", "Waking distro ") + distro + " …");
                    await WslTools.RunInDistroAsync(distro, "true", 60000);
                }
            }
            catch (Exception ex) { Log("[用量] " + L10n.T("唤醒失败: ", "Wake failed: ") + ex.Message); }
            _busy = false;
            BtnWake.IsEnabled = true;
            await RefreshAsync();
        }

        // ==================== 数据加载 ====================

        private string SelectedScopeId()
        {
            return (CmbScope.SelectedItem as ScopeItem)?.InstanceId;
        }

        /// <summary>从应用目录快照加载页面数据；不会读取实例 HOME。</summary>
        private void LoadBackupResults(bool render)
        {
            if (_registry == null) return;
            _results = UsageStatsBackupStore.LoadForScope(SelectedScopeId(), _registry.Instances);
            _modelsComplete = _results.All(u => u.ModelsComplete || !u.IsOk);
            if (!render) return;

            UsageReport rep = UsageStats.Aggregate(_results);
            SetBusy(false, FormatStatus(rep));
            Render();
            TxtUpdated.Text = L10n.T("备份更新于 ", "Backup viewed at ") + DateTime.Now.ToString("HH:mm:ss");
        }

        /// <summary>读取指定实例的实时源数据；调用方决定何时把结果合并到备份。</summary>
        private async Task<List<InstanceUsage>> LoadLiveSnapshotsAsync(List<InstanceDef> defs, CancellationToken ct)
        {
            var list = new List<InstanceUsage>();
            foreach (InstanceDef def in defs ?? new List<InstanceDef>())
            {
                ct.ThrowIfCancellationRequested();
                InstanceUsage light = await UsageStats.LoadInstanceAsync(
                    def, _registry.Settings, includeModels: false, Log, ct);
                if (light.IsOk)
                {
                    Log("[用量] " + def.Name + L10n.T("：正在解析会话日志（按模型 / 按天）…", ": parsing session logs (per model / per day)…"));
                    light = await UsageStats.LoadInstanceAsync(
                        def, _registry.Settings, includeModels: true, Log, ct);
                }
                list.Add(light);
            }
            return list;
        }

        private string FormatStatus(UsageReport rep)
        {
            var notes = new List<string>();
            if (rep.NotRunningCount > 0) notes.Add(L10n.T("发行版未运行 ", "distro off x") + rep.NotRunningCount);
            if (rep.FailedCount > 0) notes.Add(L10n.T("读取失败 ", "failed x") + rep.FailedCount);
            if (rep.EmptyCount > 0) notes.Add(L10n.T("无数据 ", "no data x") + rep.EmptyCount);
            return rep.Instances.Count + L10n.T(" 个实例 · ", " instances · ") + rep.SessionCount +
                L10n.T(" 个会话 · ", " sessions · ") + L10n.FmtTokens(rep.Totals.Total) + " tokens" +
                (notes.Count > 0 ? " · " + string.Join(" · ", notes) : "");
        }

        /// <summary>手动刷新：读取实时源 → 立即写应用目录快照 → 再从快照渲染页面。</summary>
        private async Task RefreshAsync()
        {
            if (_busy || _registry == null) return;
            _busy = true;
            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            CancellationToken ct = _cts.Token;
            SetBusy(true, L10n.T("正在读取并备份实例用量…", "Reading and backing up instance usage…"));
            try
            {
                // 手动刷新把所有当前实例都同步进快照；即使当前页面只选了一个实例，
                // 其他实例的本地备份也不会因此过期。
                List<InstanceDef> defs = _registry.Instances.ToList();
                if (defs.Count == 0)
                {
                    LoadBackupResults(render: true);
                    return;
                }

                List<InstanceUsage> live = await Task.Run(
                    () => LoadLiveSnapshotsAsync(defs, ct));
                UsageStatsBackupStore.Merge(live, Log);
                BtnWake.Visibility = live.Any(u => u.Status == InstanceDataStatus.DistroNotRunning)
                    ? Visibility.Visible : Visibility.Collapsed;

                // 展示数据统一从磁盘快照读取，避免“刷新前后”使用两套口径。
                RefreshInstances();
                LoadBackupResults(render: false);
                UsageReport rep = UsageStats.Aggregate(_results);
                SetBusy(false, FormatStatus(rep));
                Render();
                TxtUpdated.Text = L10n.T("备份更新于 ", "Backup updated at ") + DateTime.Now.ToString("HH:mm:ss");
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Log("[用量] 读取/备份失败: " + ex.Message);
                LoadBackupResults(render: true);
                SetBusy(false, L10n.T("读取失败: ", "Read failed: ") + ex.Message);
            }
            finally { _busy = false; }
        }

        /// <summary>WSL 关闭前备份：发行版可能随停止策略立即退出，必须抢在关闭前读取。</summary>
        private async Task OnInstanceClosingAsync(string id)
        {
            if (_registry == null || !_registry.TryGet(id, out InstanceDef def)) return;
            if (!def.IsWsl) return;
            Log("[用量] 实例关闭前：正在更新 " + def.Name + " 的本地备份…");
            await UsageStatsBackupStore.UpdateInstanceAsync(
                def, _registry.Settings, Log, CancellationToken.None).ConfigureAwait(false);
        }

        /// <summary>Windows 关闭后备份最终落盘内容，并刷新当前页面。</summary>
        private async Task OnInstanceClosedAsync(string id)
        {
            if (_registry == null || !_registry.TryGet(id, out InstanceDef def)) return;
            if (!def.IsWsl)
            {
                Log("[用量] 实例关闭：正在更新 " + def.Name + " 的本地备份…");
                await UsageStatsBackupStore.UpdateInstanceAsync(
                    def, _registry.Settings, Log, CancellationToken.None).ConfigureAwait(false);
            }
            if (_shutdown || _dq == null) return;
            _dq.TryEnqueue(() =>
            {
                if (_shutdown || _busy) return;
                RefreshInstances();
                LoadBackupResults(render: true);
            });
        }

        // ==================== 渲染 ====================

        private void Render()
        {
            UsageReport rep = UsageStats.Aggregate(_results);
            _lastReport = rep;
            SetSwatches();
            BuildHeatmap();
            RenderRangeSections();
            FillSessionRows();
            FillKpis();
            ApplyEmptyState(rep);
        }

        private static string Fmt(long v) => L10n.FmtTokens(v);

        /// <summary>
        /// 当前时间范围的聚合数据：KPI 卡与会话明细的口径（范围=全部时用 projcache
        /// 全量口径；筛选时按天数据（来自会话日志）聚合 + 会话按创建时间过滤）。
        /// </summary>
        private (TokenBuckets Totals, long Requests, int ActiveDays, List<string> Days, List<SessionUsage> Sessions)
            RangeData(UsageReport rep)
        {
            (string rs, string re) = RangeBounds();
            if (rs == null && re == null)
            {
                var allDays = rep.Daily.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
                return (rep.Totals, rep.RequestCount, rep.ActiveDays, allDays, rep.Sessions);
            }
            var totals = new TokenBuckets();
            long req = 0;
            var daySet = new SortedDictionary<string, long>(StringComparer.Ordinal);
            foreach (ModelUsage m in rep.Models)
            {
                foreach (var kv in m.Daily)
                {
                    if (!InRange(kv.Key, rs, re)) continue;
                    totals.Add(kv.Value);
                    if (m.DailyRequests.TryGetValue(kv.Key, out long r)) req += r;
                    daySet[kv.Key] = (daySet.TryGetValue(kv.Key, out long d) ? d : 0) + 1;
                }
            }
            var sessions = rep.Sessions.Where(s => s.CreatedAtMs > 0 && InRange(
                DateTimeOffset.FromUnixTimeMilliseconds(s.CreatedAtMs).ToLocalTime()
                    .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), rs, re)).ToList();
            return (totals, req, daySet.Count, daySet.Keys.ToList(), sessions);
        }

        // ---------------- KPI 条（Codex 风格：主值 + 副行明细，随时间范围） ----------------

        private void FillKpis()
        {
            UsageReport rep = _lastReport;
            if (rep == null) return;
            var (totals, requests, activeDays, days, sessions) = RangeData(rep);
            bool any = sessions.Count > 0 || totals.Total > 0;

            KpiTotal.Text = any ? Fmt(totals.Total) : "—";
            KpiTotalSub.Text = any
                ? L10n.T("输入 ", "In ") + Fmt(totals.UncachedInput) + L10n.T(" · 输出 ", " · Out ") + Fmt(totals.Output) +
                  L10n.T(" · 缓存读 ", " · Cache R ") + Fmt(totals.CacheRead)
                : "";

            KpiRequests.Text = _modelsComplete ? requests.ToString("N0") : "…";
            KpiRequestsSub.Text = _modelsComplete && rep.TopModel.Length > 0 ? L10n.T("最常用模型 ", "Top model ") + rep.TopModel : "";

            KpiSessions.Text = any ? sessions.Count.ToString("N0") : "—";
            KpiSessionsSub.Text = any
                ? L10n.T("轮次合计 ", "Total turns ") + sessions.Sum(s => s.Turns).ToString("N0") +
                  L10n.T(" · ", " · ") + rep.Instances.Count + L10n.T(" 个实例", " instances")
                : "";

            double hit = totals.CacheHitRate;
            KpiHitRate.Text = totals.CacheRead + totals.UncachedInput > 0
                ? (hit >= 0 ? (hit * 100).ToString("0.0") + "%" : "—")
                : "—";
            KpiHitRateSub.Text = totals.CacheRead + totals.UncachedInput > 0
                ? L10n.T("缓存读取 ", "Cache R ") + Fmt(totals.CacheRead) + L10n.T(" · 未缓存输入 ", " · Uncached In ") + Fmt(totals.UncachedInput)
                : "";

            if (_modelsComplete && activeDays > 0)
            {
                KpiActiveDays.Text = activeDays.ToString("N0") + L10n.T(" 天", " days");
                KpiActiveDaysSub.Text = days.Count >= 2
                    ? days[0].Substring(5) + " ~ " + days[days.Count - 1].Substring(5) + L10n.T(" · 跨度 ", " · span ")
                      + (DateTime.Parse(days[days.Count - 1]) - DateTime.Parse(days[0])).Days + L10n.T(" 天", " days")
                    : L10n.T("单日", "single day");
            }
            else
            {
                KpiActiveDays.Text = _modelsComplete ? "0 天" : "…";
                KpiActiveDaysSub.Text = "";
            }
        }

        // ==================== 界面语言 ====================

        /// <summary>按当前语言刷新本页全部静态文案；有数据时重建图表（数量单位/日期格式随语言）。</summary>
        public void ApplyL10n()
        {
            TxtPageTitle.Text = L10n.T("用量统计", "Usage");
            TxtPageSub.Text = L10n.T("汇总各实例 harness 会话的 token 消耗；平时读取应用目录本地备份。",
                                     "Aggregates harness session token usage; normally reads the local app-folder backup.");
            LblScope.Text = L10n.T("统计范围", "Scope");
            TxtBtnRefresh.Text = L10n.T("刷新", "Refresh");
            TxtBtnWake.Text = L10n.T("唤醒并读取", "Wake & read");
            BtnViewOverview.Content = L10n.T("总览", "Overview");
            BtnViewSessions.Content = L10n.T("会话明细", "Sessions");

            KpiLblTotal.Text = L10n.T("总 Token 数", "Total Tokens");
            KpiLblRequests.Text = L10n.T("总请求数", "Requests");
            KpiLblSessions.Text = L10n.T("会话", "Sessions");
            KpiLblHit.Text = L10n.T("缓存命中率", "Cache Hit");
            KpiLblActive.Text = L10n.T("活跃天数", "Active Days");

            LblRange.Text = L10n.T("时间范围", "Range");
            BtnRange7.Content = L10n.T("近 7 日", "7D");
            BtnRange30.Content = L10n.T("近 30 日", "30D");
            BtnRangeAll.Content = L10n.T("全部", "All");
            BtnRangeCustom.Content = L10n.T("自定义", "Custom");
            LblRangeStart.Text = L10n.T("开始", "Start");
            LblRangeEnd.Text = L10n.T("结束", "End");
            PickStart.PlaceholderText = L10n.T("选择日期", "Pick a date");
            PickEnd.PlaceholderText = L10n.T("选择日期", "Pick a date");

            TxtHeatTitle.Text = L10n.T("Token 活动", "Token Activity");
            TxtHeatHint.Text = L10n.T("每日 Token 总量 · 悬停查看当天明细 · 不受时间范围影响",
                                      "Daily tokens · hover a cell for details · ignores the range");
            TxtHeatMin.Text = L10n.T("少", "Less");
            TxtHeatMax.Text = L10n.T("多", "More");

            TxtTrendTitle.Text = L10n.T("每日 Token 趋势", "Daily Token Trend");
            TxtTrendHint.Text = L10n.T("悬停查看逐日剖面", "Hover for a daily profile");
            TxtModelTitle.Text = L10n.T("模型用量", "Model Usage");
            TxtDonutUnit.Text = "tokens";

            HdrSession.Text = L10n.T("会话", "Session");
            HdrInstance.Text = L10n.T("实例", "Instance");
            HdrCreated.Text = L10n.T("创建时间", "Created");
            HdrTurns.Text = L10n.T("轮次", "Turns");
            HdrIn.Text = L10n.T("未缓存输入", "Uncached In");
            HdrCacheRead.Text = L10n.T("缓存读取", "Cache Read");
            HdrCacheWrite.Text = L10n.T("缓存写入", "Cache Write");
            HdrOut.Text = L10n.T("输出", "Out");
            HdrTotal.Text = L10n.T("总计", "Total");

            RefreshInstances();
            if (_lastReport != null) Render();
        }

        // ---------------- 颜色 ----------------

        private Color BrandColor()
        {
            try
            {
                if (Application.Current.Resources.TryGetValue("BrandPrimaryColor", out object v) && v is Color c)
                    return c;
            }
            catch { }
            return Color.FromArgb(255, 57, 100, 254);
        }

        private Brush[] HeatBrushes()
        {
            Color b = BrandColor();
            return new Brush[]
            {
                new SolidColorBrush(Color.FromArgb(22, 128, 128, 128)),
                new SolidColorBrush(Color.FromArgb(60, b.R, b.G, b.B)),
                new SolidColorBrush(Color.FromArgb(110, b.R, b.G, b.B)),
                new SolidColorBrush(Color.FromArgb(175, b.R, b.G, b.B)),
                new SolidColorBrush(Color.FromArgb(255, b.R, b.G, b.B))
            };
        }

        private Color PaletteColor(int index)
        {
            string hex = index >= 0 ? ModelPalette[index % ModelPalette.Length] : OtherColor;
            byte r = byte.Parse(hex.Substring(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            byte g = byte.Parse(hex.Substring(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            byte b = byte.Parse(hex.Substring(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return Color.FromArgb(255, r, g, b);
        }

        private void SetSwatches()
        {
            Brush[] brushes = HeatBrushes();
            Border[] sw = { Swatch0, Swatch1, Swatch2, Swatch3, Swatch4 };
            for (int i = 0; i < sw.Length; i++) sw[i].Background = brushes[i];
        }

        // ---------------- 时间范围 ----------------

        private void BtnRange_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && b.Tag is string tag) SetRange(tag);
        }

        private void PickRange_DateChanged(object sender, CalendarDatePickerDateChangedEventArgs e)
        {
            if (_range == "custom") SafeRenderRange("custom date");
        }

        private void SetRange(string key, bool initial = false)
        {
            _range = key;
            SafeRenderRange("range=" + key + (initial ? " (init)" : ""));
        }

        /// <summary>切范围后重渲染受影响区块；任何异常都落到控制台，避免无声失效。</summary>
        private void SafeRenderRange(string reason)
        {
            Style active = (Style)Application.Current.Resources["BtnPrimary"];
            Style idle = (Style)Application.Current.Resources["BtnSecondary"];
            BtnRange7.Style = _range == "7" ? active : idle;
            BtnRange30.Style = _range == "30" ? active : idle;
            BtnRangeAll.Style = _range == "all" ? active : idle;
            BtnRangeCustom.Style = _range == "custom" ? active : idle;

            bool custom = _range == "custom";
            Visibility cv = custom ? Visibility.Visible : Visibility.Collapsed;
            RangeCustomRow.Visibility = cv;
            PickStart.Visibility = cv;
            PickEnd.Visibility = cv;
            LblRangeStart.Visibility = cv;
            LblRangeSep.Visibility = cv;
            LblRangeEnd.Visibility = cv;
            if (custom && !_pickersInitialized)
            {
                _pickersInitialized = true;
                PickStart.Date = new DateTimeOffset(DateTime.Today.AddDays(-29));
                PickEnd.Date = new DateTimeOffset(DateTime.Today);
            }

            if (_lastReport == null) return;
            try
            {
                var (rs, re) = RangeBounds();
                Log("[用量] " + L10n.T("时间范围 → ", "Range → ") + RangeLabel() +
                    (rs != null ? "（" + rs + " ~ " + re + "）" : "") +
                    L10n.T("，重渲染中", ", re-rendering"));
                RenderRangeSections();
                FillKpis();
                FillSessionRows();
            }
            catch (Exception ex)
            {
                Log("[用量] " + L10n.T("时间范围渲染失败: ", "Range render failed: ") + ex);
                SetBusy(false, L10n.T("时间范围渲染失败: ", "Range render failed: ") + ex.Message);
            }
        }

        private string RangeLabel()
        {
            return _range switch
            {
                "7" => L10n.T("近 7 日", "Last 7 days"),
                "30" => L10n.T("近 30 日", "Last 30 days"),
                "all" => L10n.T("全部", "All"),
                "custom" => L10n.T("自定义", "Custom"),
                _ => _range
            };
        }

        /// <summary>当前时间范围的日期边界（null = 不限）。</summary>
        private (string Start, string End) RangeBounds()
        {
            switch (_range)
            {
                case "7": return (DateTime.Today.AddDays(-6).ToString("yyyy-MM-dd"), DateTime.Today.ToString("yyyy-MM-dd"));
                case "30": return (DateTime.Today.AddDays(-29).ToString("yyyy-MM-dd"), DateTime.Today.ToString("yyyy-MM-dd"));
                case "custom":
                    DateTime s = PickStart.Date.HasValue ? PickStart.Date.Value.DateTime.Date : DateTime.Today.AddDays(-29);
                    DateTime en = PickEnd.Date.HasValue ? PickEnd.Date.Value.DateTime.Date : DateTime.Today;
                    if (s > en) (s, en) = (en, s);
                    return (s.ToString("yyyy-MM-dd"), en.ToString("yyyy-MM-dd"));
                default: return (null, null);
            }
        }

        private static bool InRange(string day, string start, string end)
        {
            if (start != null && string.CompareOrdinal(day, start) < 0) return false;
            if (end != null && string.CompareOrdinal(day, end) > 0) return false;
            return true;
        }

        // ---------------- 范围相关区块（趋势 + 模型用量） ----------------

        private void RenderRangeSections()
        {
            BuildTrend();
            BuildModelDonut();
        }

        private void BuildTrend()
        {
            TxtTrendTitle.Text = L10n.T("每日 Token 趋势", "Daily Token Trend") + "（" + RangeLabel() + "）";
            TrendHost.Children.Clear();
            _vLine = null; _vDot = null; _vCard = null;
            _trendPoints = null;
            UsageReport rep = _lastReport;
            if (rep == null) return;
            if (!rep.ModelsComplete || rep.Models.Count == 0)
            {
                TrendHost.Children.Add(new TextBlock
                {
                    Text = rep.ModelsComplete ? L10n.T("暂无按天数据", "No daily data yet") : L10n.T("正在解析会话日志…", "Parsing session logs…"),
                    Style = (Style)Application.Current.Resources["HelperText"],
                    HorizontalAlignment = HorizontalAlignment.Center
                });
                Canvas.SetTop(TrendHost.Children[0], 140);
                Canvas.SetLeft(TrendHost.Children[0], Math.Max(0, TrendHost.ActualWidth / 2 - 60));
                return;
            }

            double w = TrendHost.ActualWidth;
            if (w < 80) return;   // 未完成布局，SizeChanged 会再触发
            double h = TrendHost.Height;
            double padL = 10, padR = 10, padT = 14, padB = 26;
            double plotW = w - padL - padR, plotH = h - padT - padB;

            (string rs, string re) = RangeBounds();

            // 区间内每日总消耗（不分模型，单条线）
            var dayMap = new SortedDictionary<string, TokenBuckets>(StringComparer.Ordinal);
            var reqMap = new SortedDictionary<string, long>(StringComparer.Ordinal);
            long rangeTotal = 0, rangeReq = 0;
            foreach (ModelUsage m in rep.Models)
            {
                foreach (var kv in m.Daily)
                {
                    if (!InRange(kv.Key, rs, re)) continue;
                    if (!dayMap.TryGetValue(kv.Key, out var b))
                    {
                        b = new TokenBuckets();
                        dayMap[kv.Key] = b;
                    }
                    b.Add(kv.Value);
                    if (m.DailyRequests.TryGetValue(kv.Key, out long r))
                    {
                        reqMap[kv.Key] = (reqMap.TryGetValue(kv.Key, out long x) ? x : 0) + r;
                        rangeReq += r;
                    }
                    rangeTotal += kv.Value.Total;
                }
            }

            if (dayMap.Count == 0)
            {
                TrendHost.Children.Add(new TextBlock
                {
                    Text = L10n.T("所选时间范围内没有数据", "No data in the selected range"),
                    Style = (Style)Application.Current.Resources["HelperText"],
                    HorizontalAlignment = HorizontalAlignment.Center
                });
                Canvas.SetTop(TrendHost.Children[0], 140);
                return;
            }

            DateTime today = DateTime.Today;
            DateTime end = re != null && DateTime.Parse(re) < today ? DateTime.Parse(re) : today;
            DateTime start = rs != null ? DateTime.Parse(rs) : dayMap.Keys.Select(k => DateTime.Parse(k)).Min();
            if ((end - start).TotalDays > 400) start = end.AddDays(-399);
            int dayCount = (int)(end - start).TotalDays + 1;

            var vals = new List<double>();
            var reqs = new List<long>();
            var days = new List<string>();
            double max = 1;
            for (int d = 0; d < dayCount; d++)
            {
                string key = start.AddDays(d).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                double v = dayMap.TryGetValue(key, out var b) ? b.Total : 0;
                if (v > max) max = v;
                vals.Add(v);
                reqs.Add(reqMap.TryGetValue(key, out long r) ? r : 0);
                days.Add(key);
            }
            _trendVals = vals; _trendReq = reqs; _trendDays = days;
            _trendPadL = padL; _trendPlotW = plotW; _trendPadT = padT; _trendPlotH = plotH; _trendMax = max;

            // 虚线网格（4 条）
            Brush grid = new SolidColorBrush(Color.FromArgb(45, 128, 128, 128));
            for (int i = 1; i <= 4; i++)
            {
                double y = padT + plotH * i / 4.0;
                TrendHost.Children.Add(new Line
                {
                    X1 = padL, X2 = w - padR, Y1 = y, Y2 = y,
                    Stroke = grid,
                    StrokeDashArray = new DoubleCollection { 2, 3 },
                    StrokeThickness = 1
                });
            }

            // 总消耗平滑曲线（单条，品牌色）
            var pts = new List<Point>();
            for (int d = 0; d < dayCount; d++)
                pts.Add(new Point(padL + plotW * d / Math.Max(1, dayCount - 1),
                                  padT + plotH - (vals[d] / max) * plotH));
            _trendPoints = pts;
            var linePath = new Path
            {
                Stroke = new SolidColorBrush(BrandColor()),
                StrokeThickness = 2.5,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Data = SmoothGeometry(pts, padT, padT + plotH)
            };
            ToolTipService.SetToolTip(linePath, L10n.T("每日 Token 总量", "Daily tokens") + "\n" +
                    L10n.T("区间总量 ", "Range total ") + L10n.FmtTokens(rangeTotal) + " tokens · " + rangeReq + L10n.T(" 次请求", " requests"));
            TrendHost.Children.Add(linePath);

            // X 轴日期标签
            int step = Math.Max(1, (dayCount - 1) / 7);
            for (int d = 0; d < dayCount; d += step)
            {
                var label = new TextBlock
                {
                    Text = days[d].Substring(5).Replace("-", "/"),
                    FontSize = 10,
                    Foreground = (Brush)Application.Current.Resources["LabelSecondaryBrush"]
                };
                TrendHost.Children.Add(label);
                Canvas.SetLeft(label, Math.Min(w - 40, padL + plotW * d / Math.Max(1, dayCount - 1) - 14));
                Canvas.SetTop(label, h - 20);
            }

            // 悬停剖面元素（细竖线 + 圆点 + 数据卡），默认隐藏
            Color bc = BrandColor();
            _vLine = new Line
            {
                Stroke = new SolidColorBrush(Color.FromArgb(130, 128, 128, 128)),
                StrokeThickness = 1,
                Visibility = Visibility.Collapsed
            };
            _vDot = new Ellipse
            {
                Width = 10, Height = 10,
                Fill = new SolidColorBrush(bc),
                Stroke = new SolidColorBrush(Microsoft.UI.Colors.White),
                StrokeThickness = 2,
                Visibility = Visibility.Collapsed
            };
            _vCardDate = new TextBlock { FontSize = 11, Foreground = (Brush)Application.Current.Resources["LabelSecondaryBrush"] };
            _vCardTok = new TextBlock { FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = (Brush)Application.Current.Resources["LabelPrimaryBrush"] };
            _vCardReq = new TextBlock { FontSize = 10.5, Foreground = (Brush)Application.Current.Resources["LabelSecondaryBrush"] };
            var cardSp = new StackPanel { Spacing = 1 };
            cardSp.Children.Add(_vCardDate);
            cardSp.Children.Add(_vCardTok);
            cardSp.Children.Add(_vCardReq);
            _vCard = new Border
            {
                Background = (Brush)Application.Current.Resources["BgCardBrush"],
                BorderBrush = (Brush)Application.Current.Resources["BorderBrushToken"],
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10, 7, 10, 7),
                Child = cardSp,
                Visibility = Visibility.Collapsed
            };
            TrendHost.Children.Add(_vLine);
            TrendHost.Children.Add(_vDot);
            TrendHost.Children.Add(_vCard);
        }

        // ---------------- 趋势图悬停剖面 ----------------

        private void TrendHost_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (_trendVals == null || _trendVals.Count == 0 || _trendPoints == null || _vLine == null) return;
            Point p = e.GetCurrentPoint(TrendHost).Position;
            int n = _trendVals.Count;
            double f = (p.X - _trendPadL) / Math.Max(1, _trendPlotW) * (n - 1);
            f = Math.Max(0, Math.Min(n - 1, f));

            // 圆点直接评估与 Path 相同的 Bezier 曲线，避免“数据点之间”悬空。
            Point curvePoint = SmoothPoint(_trendPoints, f, _trendPadT, _trendPadT + _trendPlotH);
            double x = curvePoint.X;
            double y = curvePoint.Y;

            double lineX = x;
            _vLine.X1 = lineX; _vLine.X2 = lineX;
            _vLine.Y1 = _trendPadT; _vLine.Y2 = _trendPadT + _trendPlotH;
            _vLine.Visibility = Visibility.Visible;

            _vDot.Visibility = Visibility.Visible;
            Canvas.SetLeft(_vDot, x - 5);
            Canvas.SetTop(_vDot, y - 5);

            // 剖面数据卡：显示最近一天的数值
            int idx = (int)Math.Round(f);
            idx = Math.Max(0, Math.Min(n - 1, idx));
            _vCardDate.Text = L10n.IsEn
                ? DateTime.Parse(_trendDays[idx]).ToString("MMM d, yyyy", CultureInfo.InvariantCulture)
                : DateTime.Parse(_trendDays[idx]).ToString("yyyy年M月d日");
            _vCardTok.Text = Fmt((long)_trendVals[idx]) + " tokens";
            _vCardReq.Text = _trendReq[idx] + L10n.T(" 次请求", " requests");
            _vCard.Visibility = Visibility.Visible;
            double cw = 160;
            double cx = Math.Max(0, Math.Min(TrendHost.ActualWidth - cw, lineX + 14));
            Canvas.SetLeft(_vCard, cx);
            Canvas.SetTop(_vCard, _trendPadT + 2);
        }

        private void TrendHost_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (_vLine != null) _vLine.Visibility = Visibility.Collapsed;
            if (_vDot != null) _vDot.Visibility = Visibility.Collapsed;
            if (_vCard != null) _vCard.Visibility = Visibility.Collapsed;
        }

        /// <summary>带端点约束的平滑趋势线，控制点始终落在绘图区内。</summary>
        private static Geometry SmoothGeometry(List<Point> pts, double minY, double maxY)
        {
            var geo = new PathGeometry();
            var fig = new PathFigure { StartPoint = pts[0], IsClosed = false, IsFilled = false };
            if (pts.Count == 1)
            {
                geo.Figures.Add(fig);
                return geo;
            }
            if (pts.Count == 2)
            {
                fig.Segments.Add(new LineSegment { Point = pts[1] });
                geo.Figures.Add(fig);
                return geo;
            }
            var bez = new PolyBezierSegment();
            for (int i = 0; i < pts.Count - 1; i++)
            {
                Point p0 = pts[Math.Max(i - 1, 0)];
                Point p1 = pts[i];
                Point p2 = pts[i + 1];
                Point p3 = pts[Math.Min(i + 2, pts.Count - 1)];
                // X 轴按数据区间严格线性，保证鼠标位置可直接反求 f，
                // 同时只对 Y 使用 Catmull-Rom 的相邻斜率。
                Point c1 = new Point(p1.X + (p2.X - p1.X) / 3.0,
                                     Clamp(p1.Y + (p2.Y - p0.Y) / 6.0, minY, maxY));
                Point c2 = new Point(p2.X - (p2.X - p1.X) / 3.0,
                                     Clamp(p2.Y - (p3.Y - p1.Y) / 6.0, minY, maxY));
                bez.Points.Add(c1);
                bez.Points.Add(c2);
                bez.Points.Add(p2);
            }
            fig.Segments.Add(bez);
            geo.Figures.Add(fig);
            return geo;
        }

        /// <summary>在趋势线的第 f 个数据区间中评估同一组受约束 Bezier 控制点。</summary>
        private static Point SmoothPoint(List<Point> pts, double f, double minY, double maxY)
        {
            if (pts == null || pts.Count == 0) return new Point();
            if (pts.Count == 1) return pts[0];
            f = Math.Max(0, Math.Min(pts.Count - 1, f));
            int i = Math.Min(pts.Count - 2, (int)Math.Floor(f));
            double t = f - i;
            Point p0 = pts[Math.Max(i - 1, 0)];
            Point p1 = pts[i];
            Point p2 = pts[i + 1];
            Point p3 = pts[Math.Min(i + 2, pts.Count - 1)];
            Point c1 = new Point(p1.X + (p2.X - p1.X) / 3.0,
                                 Clamp(p1.Y + (p2.Y - p0.Y) / 6.0, minY, maxY));
            Point c2 = new Point(p2.X - (p2.X - p1.X) / 3.0,
                                 Clamp(p2.Y - (p3.Y - p1.Y) / 6.0, minY, maxY));
            return CubicBezier(p1, c1, c2, p2, t);
        }

        private static Point CubicBezier(Point p0, Point p1, Point p2, Point p3, double t)
        {
            double u = 1.0 - t;
            return new Point(
                u * u * u * p0.X + 3 * u * u * t * p1.X + 3 * u * t * t * p2.X + t * t * t * p3.X,
                u * u * u * p0.Y + 3 * u * u * t * p1.Y + 3 * u * t * t * p2.Y + t * t * t * p3.Y);
        }

        private static double Clamp(double value, double min, double max)
        {
            return Math.Max(min, Math.Min(max, value));
        }

        // ---------------- 模型用量（环图 + 图例） ----------------

        private void BuildModelDonut()
        {
            TxtModelTitle.Text = L10n.T("模型用量", "Model Usage") + "（" + RangeLabel() + "）";
            DonutHost.Children.Clear();
            ModelLegendHost.Children.Clear();
            UsageReport rep = _lastReport;
            if (rep == null) return;

            (string rs, string re) = RangeBounds();
            var inRange = new List<(ModelUsage M, TokenBuckets B, long Req)>();
            foreach (ModelUsage m in rep.Models)
            {
                var b = new TokenBuckets();
                long req = 0;
                foreach (var kv in m.Daily)
                {
                    if (!InRange(kv.Key, rs, re)) continue;
                    b.Add(kv.Value);
                    if (m.DailyRequests.TryGetValue(kv.Key, out long r)) req += r;
                }
                if (b.Total > 0) inRange.Add((m, b, req));
            }
            inRange = inRange.OrderByDescending(x => x.B.Total).ToList();

            double sum = inRange.Sum(x => x.B.Total);
            DonutTotal.Text = sum > 0 ? Fmt((long)sum) : "—";
            if (inRange.Count == 0)
            {
                // 范围内确无数据时给出明确提示，避免看起来像筛选失效
                ModelLegendHost.Children.Add(new TextBlock
                {
                    Text = L10n.T("所选时间范围内没有模型用量数据，试试切换更大的时间范围。",
                                  "No model usage in the selected range — try a wider one."),
                    Style = (Style)Application.Current.Resources["HelperText"],
                    TextWrapping = TextWrapping.Wrap
                });
                return;
            }

            var segs = inRange.Take(5).ToList();
            long otherTotal = (long)sum - segs.Sum(x => x.B.Total);
            bool hasOther = inRange.Count > 5 && otherTotal > 0;

            const double size = 220, cx = size / 2, cy = size / 2, radius = 74, thick = 40;
            double startAngle = -90;
            const double gapDeg = 2.5;
            var items = segs.Select((x, i) => (x.M, x.B, x.Req, Color: PaletteColor(i), Name: ModelName(x.M))).ToList();
            if (hasOther)
            {
                var ob = new TokenBuckets();
                foreach (var x in inRange.Skip(5)) ob.Add(x.B);
                items.Add((null, ob, inRange.Skip(5).Sum(x => x.Req), PaletteColor(-1),
                    L10n.T("其他 ", "Other ") + (inRange.Count - 5) + L10n.T(" 个模型", " models")));
            }

            for (int i = 0; i < items.Count; i++)
            {
                var it = items[i];
                double share = sum > 0 ? it.B.Total / sum : 0;
                double sweep = Math.Max(0.5, share * 360.0 - gapDeg);
                double a0 = startAngle + gapDeg / 2;
                double a1 = a0 + sweep;
                startAngle += share * 360.0;

                Point p1 = Polar(cx, cy, radius, a0);
                Point p2 = Polar(cx, cy, radius, a1);
                var fig = new PathFigure { StartPoint = p1, IsClosed = false, IsFilled = false };
                fig.Segments.Add(new ArcSegment
                {
                    Point = p2,
                    Size = new Size(radius, radius),
                    RotationAngle = 0,
                    IsLargeArc = sweep > 180,
                    SweepDirection = SweepDirection.Clockwise
                });
                var geo = new PathGeometry();
                geo.Figures.Add(fig);
                var path = new Path
                {
                    Stroke = new SolidColorBrush(it.Color),
                    StrokeThickness = thick,
                    Data = geo
                };
                ToolTipService.SetToolTip(path, it.Name + "\n" + it.B.Total.ToString("N0") + " tokens · " +
                    (share * 100).ToString("0.0") + "% · " + it.Req + L10n.T(" 次请求", " requests"));
                DonutHost.Children.Add(path);
            }

            // 图例（代码构建，避免 x:Bind 列表问题）
            foreach (var it in items)
            {
                double share = sum > 0 ? it.B.Total / sum : 0;
                var row = new Grid { ColumnSpacing = 10, Margin = new Thickness(0, 0, 0, 2) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });

                var dot = new Border
                {
                    Width = 10, Height = 10, CornerRadius = new CornerRadius(3),
                    Background = new SolidColorBrush(it.Color), VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(dot, 0);

                var nameTb = new TextBlock
                {
                    Text = it.Name, FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                    Foreground = (Brush)Application.Current.Resources["LabelPrimaryBrush"],
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                var subTb = new TextBlock
                {
                    Text = it.Req + L10n.T(" 次请求 · 输入 ", " requests · In ") + Fmt(it.B.UncachedInput) +
                           L10n.T(" · 缓存读 ", " · Cache R ") + Fmt(it.B.CacheRead) +
                           L10n.T(" · 输出 ", " · Out ") + Fmt(it.B.Output),
                    FontSize = 10.5,
                    Foreground = (Brush)Application.Current.Resources["LabelSecondaryBrush"],
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                var mid = new StackPanel { Spacing = 2 };
                mid.Children.Add(nameTb);
                mid.Children.Add(subTb);
                Grid.SetColumn(mid, 1);

                var right = new StackPanel { Spacing = 1 };
                right.Children.Add(new TextBlock
                {
                    Text = Fmt(it.B.Total), FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = (Brush)Application.Current.Resources["LabelPrimaryBrush"],
                    TextAlignment = TextAlignment.Right
                });
                right.Children.Add(new TextBlock
                {
                    Text = (share * 100).ToString("0.0") + "%", FontSize = 10.5,
                    Foreground = (Brush)Application.Current.Resources["LabelSecondaryBrush"],
                    TextAlignment = TextAlignment.Right
                });
                Grid.SetColumn(right, 2);

                row.Children.Add(dot);
                row.Children.Add(mid);
                row.Children.Add(right);
                ToolTipService.SetToolTip(row, it.Name + "\n总计 " + it.B.Total.ToString("N0") +
                    "\n未缓存输入 " + it.B.UncachedInput.ToString("N0") +
                    "\n缓存读取 " + it.B.CacheRead.ToString("N0") +
                    "\n缓存写入 " + it.B.CacheWrite.ToString("N0") +
                    "\n输出 " + it.B.Output.ToString("N0"));
                ModelLegendHost.Children.Add(row);
            }
        }

        private static Point Polar(double cx, double cy, double r, double angleDeg)
        {
            double rad = angleDeg * Math.PI / 180.0;
            return new Point(cx + r * Math.Cos(rad), cy + r * Math.Sin(rad));
        }

        /// <summary>图例与提示用名称：「提供方显示名称 | 模型名」（缺 provider 时回退模型名）。</summary>
        private static string ModelName(ModelUsage m)
        {
            if (m == null) return "其他";
            if (string.IsNullOrEmpty(m.Provider)) return m.Model;
            return ProviderDisplayName(m.Provider) + " | " + m.Model;
        }

        private static string ProviderDisplayName(string provider)
        {
            if (ProviderDisplayNames.TryGetValue(provider, out string dn)) return dn;
            // 回退：按 -/_ 分词并首字母大写
            var parts = provider.Split(new[] { '-', '_' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
                if (parts[i].Length > 0)
                    parts[i] = char.ToUpperInvariant(parts[i][0]) + parts[i].Substring(1);
            return string.Join(" ", parts);
        }

        // ---------------- Token 活动热力图（26 周满宽大格） ----------------

        private void BuildHeatmap()
        {
            SetSwatches();
            HeatMapHost.Children.Clear();
            HeatMapHost.ColumnDefinitions.Clear();
            HeatMapHost.RowDefinitions.Clear();
            UsageReport rep = _lastReport;
            if (rep == null || !rep.ModelsComplete) return;

            double w = HeatMapHost.ActualWidth;
            if (w < 100) return;   // 布局未完成，SizeChanged 会再触发
            const int gap = 3, labelH = 18;
            // 目标格宽 ≈ 满宽 26 周的 3/4；按宽度反推周数，空位与遮挡都由周数吸收
            double target = 29;
            int weeks = Math.Max(8, Math.Min(52, (int)Math.Round(w / (target + gap))));
            // 留 2px 布局舍入安全余量，避免最后一列被卡片边缘裁掉
            double cell = (w - gap * (weeks - 1) - 2) / weeks;
            if (cell < 10) cell = 10;

            for (int c = 0; c < weeks; c++)
                HeatMapHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(cell) });
            for (int r = 0; r < 7; r++)
                HeatMapHost.RowDefinitions.Add(new RowDefinition { Height = new GridLength(cell) });
            HeatMapHost.RowDefinitions.Add(new RowDefinition { Height = new GridLength(labelH) });
            HeatMapHost.ColumnSpacing = gap;
            HeatMapHost.RowSpacing = gap;

            Brush[] brushes = HeatBrushes();
            double max = rep.Daily.Count > 0 ? rep.Daily.Max(kv => kv.Value.Total) : 0;
            DateTime today = DateTime.Today;
            DateTime start = today.AddDays(-(weeks * 7 - 1));
            while (start.DayOfWeek != DayOfWeek.Sunday) start = start.AddDays(-1);
            int monthsShown = -1;

            for (int wk = 0; wk < weeks; wk++)
            {
                DateTime monthDay = start.AddDays(wk * 7);
                for (int dow = 0; dow < 7; dow++)
                {
                    DateTime day = start.AddDays(wk * 7 + dow);
                    var cellBorder = new Border
                    {
                        CornerRadius = new CornerRadius(Math.Min(8, Math.Max(3, cell / 5.0))),
                        Background = brushes[0]
                    };
                    if (day <= today)
                    {
                        string key = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                        rep.Daily.TryGetValue(key, out var buckets);
                        rep.DailyRequests.TryGetValue(key, out long req);
                        long total = buckets?.Total ?? 0;
                        double ratio = max > 0 ? total / (double)max : 0;
                        int level = total <= 0 ? 0 : ratio <= 0.25 ? 1 : ratio <= 0.5 ? 2 : ratio <= 0.75 ? 3 : 4;
                        if (level > 0) cellBorder.Background = brushes[level];
                        ToolTipService.SetToolTip(cellBorder,
                            (L10n.IsEn
                                ? day.ToString("MMM d, yyyy", CultureInfo.InvariantCulture)
                                : day.ToString("yyyy年M月d日"))
                            + "\n" + total.ToString("N0") + " tokens · " + req + L10n.T(" 次请求", " requests") +
                            (total > 0
                                ? "\n" + L10n.T("输入 ", "In ") + (buckets?.UncachedInput ?? 0).ToString("N0") +
                                  L10n.T(" · 缓存读 ", " · Cache R ") + (buckets?.CacheRead ?? 0).ToString("N0") +
                                  L10n.T(" · 输出 ", " · Out ") + (buckets?.Output ?? 0).ToString("N0")
                                : ""));
                    }
                    Grid.SetColumn(cellBorder, wk);
                    Grid.SetRow(cellBorder, dow);
                    HeatMapHost.Children.Add(cellBorder);
                }

                // 月份标签与格子放同一 Grid（列宽固定为 cell），逐列严格对齐
                if (monthDay.Month != monthsShown)
                {
                    monthsShown = monthDay.Month;
                    var label = new TextBlock
                    {
                        Text = L10n.IsEn
                            ? monthDay.ToString("MMM", CultureInfo.InvariantCulture)
                            : monthDay.ToString("M月", CultureInfo.InvariantCulture),
                        FontSize = 11,
                        HorizontalAlignment = HorizontalAlignment.Left,
                        VerticalAlignment = VerticalAlignment.Center,
                        Foreground = (Brush)Application.Current.Resources["LabelSecondaryBrush"]
                    };
                    Grid.SetColumn(label, wk);
                    Grid.SetColumnSpan(label, 2);
                    Grid.SetRow(label, 7);
                    HeatMapHost.Children.Add(label);
                }
            }
        }

        // ---------------- 表格 / 空态 ----------------

        private void FillSessionRows()
        {
            UsageReport rep = _lastReport;
            if (rep == null) return;
            var (_, _, _, _, sessions) = RangeData(rep);
            var rows = new List<UsageSessionRow>();
            foreach (SessionUsage s in sessions)
            {
                rows.Add(new UsageSessionRow
                {
                    Title = string.IsNullOrEmpty(s.Title) ? "(无标题会话)" : s.Title,
                    InstanceText = s.InstanceName ?? "",
                    CreatedText = s.CreatedAtMs > 0 ? FormatMs(s.CreatedAtMs) : "—",
                    TurnsText = s.Turns.ToString("N0"),
                    InText = Fmt(s.Totals.UncachedInput),
                    CacheReadText = Fmt(s.Totals.CacheRead),
                    CacheWriteText = Fmt(s.Totals.CacheWrite),
                    OutText = Fmt(s.Totals.Output),
                    TotalText = Fmt(s.Totals.Total),
                    ToolTip = (string.IsNullOrEmpty(s.Title) ? "(无标题会话)" : s.Title) +
                        "\n" + (s.Cwd ?? "") +
                        "\n总计 " + s.Totals.Total.ToString("N0") + " token"
                });
            }
            _sessionRows = rows;
            if (TableSessions.Visibility == Visibility.Visible)
                ListSessions.ItemsSource = rows;
        }

        private void ApplyEmptyState(UsageReport rep)
        {
            bool anyInstance = _results.Count > 0;
            bool anyOk = _results.Any(u => u.IsOk);
            if (!anyInstance)
            {
                ShowEmpty(L10n.T("没有可选实例。先在「Windows 实例 / WSL 实例」页创建或扫描实例。", "No instances yet. Create or scan one on the Windows/WSL page first."));
                return;
            }
            if (!anyOk)
            {
                ShowEmpty(L10n.T("暂无可读取的数据：", "Nothing readable yet: ") +
                    string.Join("; ", _results.Select(u => (u.Def?.Name ?? "?") + " — " + (u.StatusText ?? ""))));
                return;
            }
            if (rep.SessionCount == 0)
            {
                ShowEmpty(L10n.T("实例已读取，但还没有会话数据。启动 harness 开始对话后，点「刷新」。", "Instances read, but no sessions yet. Start a harness chat, then Refresh."));
                return;
            }
            EmptyState.Visibility = Visibility.Collapsed;
        }

        private static string FormatMs(long ms)
        {
            if (ms <= 0) return "—";
            return DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        }

        // ==================== 视图切换 ====================

        private void BtnView_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && b.Tag is string tag) SetView(tag);
        }

        private void SetView(string view)
        {
            _view = view;
            Style active = (Style)Application.Current.Resources["BtnPrimary"];
            Style idle = (Style)Application.Current.Resources["BtnSecondary"];
            BtnViewOverview.Style = view == "overview" ? active : idle;
            BtnViewSessions.Style = view == "sessions" ? active : idle;

            ViewOverview.Visibility = view == "overview" ? Visibility.Visible : Visibility.Collapsed;
            TableSessions.Visibility = view == "sessions" ? Visibility.Visible : Visibility.Collapsed;
            ListSessions.ItemsSource = view == "sessions" ? _sessionRows : null;

            // 切回总览后宽度可用，重建依赖 ActualWidth 的图
            if (view == "overview" && _lastReport != null)
            {
                BuildHeatmap();
                RenderRangeSections();
            }
        }
    }
}
