// ============================================================================
//  用量页与档案详情页共用的行视图模型（用量改版方案定稿后扩展）
//
//  纯展示对象：把 Core 的统计结构翻译成可直接 x:Bind 的字符串与比例，
//  界面里不再出现任何格式化逻辑。交互命令（钻取/展开）由 VM 装配。
//  2026-09-06 二次改版：UsageScopeItem / UsageInstanceCardRow 随实例范围
//  切换一起废除；同一套行对象现由用量看板与 ArchiveMetaViewModel 共用。
// ============================================================================

using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DshController.Core.Usage;

namespace DshController.ViewModels
{
    public sealed class UsageModelRow
    {
        public string Model { get; set; } = "";
        public string Provider { get; set; } = "";
        public string RequestsText { get; set; } = "";
        public string TotalText { get; set; } = "";
        public string InputText { get; set; } = "";
        public string OutputText { get; set; } = "";
        public string CacheText { get; set; } = "";
        public double SharePercent { get; set; }
        public double ShareBarW { get; set; }          // 占比条像素宽（条区最大 64px）
        public string ShareText { get; set; } = "";
        public string ToolTip { get; set; } = "";

        public static UsageModelRow From(UsageModelStat m, long grandTotal)
        {
            long total = m.Totals.Total;
            double share = grandTotal > 0 ? (double)total / grandTotal * 100 : 0;
            return new UsageModelRow
            {
                Model = m.DisplayName,
                Provider = string.IsNullOrEmpty(m.Provider) ? "(未知来源)" : m.Provider,
                RequestsText = m.Requests.ToString("N0") + " 次",
                TotalText = UsageQuery.FormatTokens(total),
                InputText = UsageQuery.FormatTokens(m.Totals.UncachedInput),
                OutputText = UsageQuery.FormatTokens(m.Totals.Output),
                CacheText = UsageQuery.FormatTokens(m.Totals.CacheRead + m.Totals.CacheWrite),
                SharePercent = share,
                ShareBarW = 64 * share / 100,
                ShareText = share.ToString("0.#") + "%",
                ToolTip = m.FullName + "\n未缓存输入 " + m.Totals.UncachedInput.ToString("N0") +
                          " · 缓存读 " + m.Totals.CacheRead.ToString("N0") +
                          " · 缓存写 " + m.Totals.CacheWrite.ToString("N0") +
                          " · 输出 " + m.Totals.Output.ToString("N0") +
                          "\n占比 " + share.ToString("0.##") + "% · 来自会话日志按请求解析"
            };
        }
    }

    public sealed partial class UsageSessionRow : ObservableObject
    {
        public string Title { get; set; } = "";
        public string CreatedText { get; set; } = "";
        public string TurnsText { get; set; } = "";
        public string TotalText { get; set; } = "";
        public string Cwd { get; set; } = "";
        public string SessionId { get; set; } = "";
        public string ExactDetailText { get; set; } = "";
        public string ToolTip { get; set; } = "";
        public IRelayCommand ExpandCommand { get; set; }

        [ObservableProperty] public partial bool IsExpanded { get; set; }

        public static UsageSessionRow From(UsageSessionStat s)
        {
            string when = s.CreatedAtMs > 0
                ? DateTimeOffset.FromUnixTimeMilliseconds(s.CreatedAtMs).ToLocalTime().ToString("MM-dd HH:mm")
                : "—";
            string title = string.IsNullOrWhiteSpace(s.Title) ? "(未命名会话)" : s.Title;
            return new UsageSessionRow
            {
                Title = title,
                CreatedText = when,
                TurnsText = s.Turns > 0 ? s.Turns + " 轮" : "—",
                TotalText = UsageQuery.FormatTokens(s.Totals.Total),
                Cwd = s.Cwd ?? "",
                SessionId = s.SessionId ?? "",
                ExactDetailText = "未缓存输入 " + s.Totals.UncachedInput.ToString("N0") +
                                  " · 缓存读 " + s.Totals.CacheRead.ToString("N0") +
                                  " · 缓存写 " + s.Totals.CacheWrite.ToString("N0") +
                                  " · 输出 " + s.Totals.Output.ToString("N0") +
                                  (string.IsNullOrEmpty(s.Cwd) ? "" : "\nCWD: " + s.Cwd),
                ToolTip = title + "\n" + when + "\n" + (s.Cwd ?? "") + "\n" +
                          "未缓存输入 " + UsageQuery.FormatTokens(s.Totals.UncachedInput) +
                          " · 缓存读 " + UsageQuery.FormatTokens(s.Totals.CacheRead) +
                          " · 缓存写 " + UsageQuery.FormatTokens(s.Totals.CacheWrite) +
                          " · 输出 " + UsageQuery.FormatTokens(s.Totals.Output)
            };
        }
    }

    /// <summary>按天柱状图的一根柱子：可点击钻取（ToggleCommand 由 VM 装配）。</summary>
    public sealed partial class UsageDayBar : ObservableObject
    {
        public string Day { get; set; } = "";
        public string ShortDay { get; set; } = "";
        public long Total { get; set; }
        public double HeightRatio { get; set; }
        public double BarHeight { get; set; }
        public string ToolTip { get; set; } = "";
        public IRelayCommand ToggleCommand { get; set; }

        [ObservableProperty] public partial bool IsSelected { get; set; }
    }
}
