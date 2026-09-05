// ============================================================================
//  PluginManagePanel — 升级入口与详情弹窗（改版·升级详情弹窗）
//
//  入口规则：卸载按钮旁的「升级」钮只在 目录找到来源 ∧ 市场版本更高 ∧ 未被
//  忽略到该版本 时出现（四态判定=PluginUpgrade，抑制=UpgradeIgnoreStore）。
//  探测结果是面板级 _hints 缓存（按 pkg 注入 ManageItem）——渲染重建不丢结果。
//  详情窗：当前版本→新版本 + changelog（GitHub releases latest，失败/缺失则
//  「市场未提供更新说明」占位）；三选项 = 升级 / 取消 / 忽略本次升级。
//  「升级」确认后复用既有 ops 通道（执行完成度验收在「升级执行」小类）。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DshController.Core;
using DshController.ViewModels;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DshController
{
    /// <summary>一次升级探测的结果（RenderList 建行时注入 ManageItem；探索结果不随行重建丢失）。</summary>
    public sealed class ManageUpgradeHint
    {
        public string MarketVersion { get; set; } = "";
        public string IgnoredUpTo { get; set; } = "";
        public bool Ignored { get; set; }
        public string Changelog { get; set; } = "";
    }

    public sealed partial class PluginManagePanel
    {
        private List<CatalogEntry> _catalog;              // 市场目录（缓存优先，静默）
        private readonly Dictionary<string, string> _latestVerCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, ManageUpgradeHint> _hints =
            new Dictionary<string, ManageUpgradeHint>(StringComparer.OrdinalIgnoreCase);   // 探测结果（按 pkg）
        private int _probeSeq;
        private bool _suppressProbe;                      // 探测刷新列表时防自循环

        /// <summary>RenderList 建行时注入探测结果（无结果=空 hint，按钮隐藏）。</summary>
        private ManageUpgradeHint HintFor(string pkg)
        {
            if (string.IsNullOrEmpty(pkg)) return new ManageUpgradeHint();
            return _hints.TryGetValue(pkg, out ManageUpgradeHint h) ? h : new ManageUpgradeHint();
        }

        /// <summary>行内「升级」点击 → 详情弹窗（三选项）。</summary>
        private async void BtnUpgradeDetails_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is ManageItem item) await ShowUpgradeDetailsAsync(item);
        }

        private async Task ShowUpgradeDetailsAsync(ManageItem item)
        {
            if (_target.IsBusy || item?.Plugin == null) return;
            InstanceDef def = _target.SelectedInstance;
            if (def == null || item.MarketVersion.Length == 0) return;

            string changelog = item.Changelog ?? "";
            if (changelog.Length == 0)
            {
                CatalogEntry entry = PluginUpgrade.FindSource(item.Plugin, _catalog);
                string repo = (entry != null && (entry.Repo ?? "").Trim().Length > 0) ? entry.Repo : item.Plugin.Repo;
                changelog = await FetchChangelogSafeAsync(repo);
                item.Changelog = changelog;
            }

            var body = new StackPanel { Spacing = 10, MaxWidth = 560 };
            body.Children.Add(new TextBlock
            {
                Text = InstanceDisplayName.For(def) + "  ·  " + item.Plugin.Pkg,
                FontSize = 13, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap
            });
            body.Children.Add(new TextBlock
            {
                Text = "当前版本 v" + (string.IsNullOrEmpty(item.Plugin.Version) ? "?" : item.Plugin.Version) +
                       "  →  新版本 v" + item.MarketVersion,
                FontSize = 13, TextWrapping = TextWrapping.Wrap
            });
            body.Children.Add(new TextBlock { Text = "更新内容", FontSize = 11, FontWeight = FontWeights.SemiBold });
            body.Children.Add(new ScrollViewer
            {
                MaxHeight = 260,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = new TextBlock
                {
                    Text = changelog.Length > 0 ? changelog
                          : "市场未提供更新说明（版本更新详情以来源仓库为准）。",
                    FontSize = 12, TextWrapping = TextWrapping.Wrap
                }
            });
            if (item.IgnoredUpTo.Length > 0)
                body.Children.Add(new TextBlock { Text = "提示：你曾忽略过到 v" + item.IgnoredUpTo + " 的更新。", FontSize = 11 });

            var dlg = new ContentDialog
            {
                Title = "升级到 v" + item.MarketVersion,
                Content = body,
                PrimaryButtonText = "升级",
                CloseButtonText = "取消",
                SecondaryButtonText = "忽略本次升级",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot
            };
            ContentDialogResult r = await _dialogs.ShowAsync(dlg);
            if (r == ContentDialogResult.Secondary)
            {
                _ignores.SetIgnored(def.Id, item.Plugin.Pkg, item.MarketVersion);
                _hints.Remove(item.Plugin.Pkg);            // 忽略后清除提示 → 重建后按钮消失
                PushLog("[升级] 已忽略 " + item.Plugin.Pkg + " 到 v" + item.MarketVersion +
                        " 的更新；市场出更高版本时会再次提示。");
                await _target.ReloadAsync();               // 重建行 → 按钮随之消失
            }
            if (r == ContentDialogResult.Primary)
                await RunUpgradeAsync(def, item);
        }

        /// <summary>升级执行：ops 通道 + 目标带新版本号（入口与确认链是本小类，完成度验收在「升级执行」）。</summary>
        private async Task RunUpgradeAsync(InstanceDef def, ManageItem item)
        {
            string profile = _target.EffectiveProfile;
            if (profile == null)
            {
                await _dialogs.InfoAsync("profile 只允许字母、数字、-、_、.（默认 web）。", "profile 非法");
                return;
            }
            string target = item.Plugin.Pkg + "@" + item.MarketVersion;
            _target.IsBusy = true;
            bool ok = false;
            try { ok = await _ops.RunAsync(def, PluginOp.Update, profile, target); }
            finally { _target.IsBusy = false; }
            if (!ok) { PushLog("[升级] 升级命令未成功（详见运行日志）"); return; }
            PushLog("[升级] 已执行：dsh plugin " + PluginOpText.Verb(PluginOp.Update) + " " + target +
                    "（" + item.Plugin.Pkg + " v" + item.Plugin.Version + " → v" + item.MarketVersion + "）");
            _archive.OnPluginsChanged(def.Id);
            _hints.Remove(item.Plugin.Pkg);                // 升级后清提示（等新版本）
            await _target.ReloadAsync(force: true);
        }

        // ==================== 升级提示后台探测 ====================

        private async Task ProbeUpgradeHintsAsync()
        {
            int seq = ++_probeSeq;
            bool changed = false;
            try
            {
                if (_registry == null || _target == null) return;
                if (_catalog == null)
                {
                    MarketLoadResult res = null;
                    try { res = await PluginCatalog.LoadAllAsync(_registry.Settings, force: false); }
                    catch { return; }   // 理由: 目录不可用时升级提示整体静默缺失，不打扰管理页主功能
                    _catalog = res != null && res.Catalog != null ? res.Catalog.Plugins : new List<CatalogEntry>();
                    if (_catalog.Count == 0) return;
                }
                if (seq != _probeSeq || _closing) return;

                string instId = _target.SelectedInstance != null ? _target.SelectedInstance.Id : "";
                var rows = ListPlugins.ItemsSource as IEnumerable<ManageItem>;
                if (rows == null) return;

                foreach (ManageItem m in rows.ToList())
                {
                    if (m.Plugin == null || m.Plugin.IsOfficial) continue;
                    CatalogEntry entry = PluginUpgrade.FindSource(m.Plugin, _catalog);
                    if (entry == null) continue;                       // 来源找不到 → 无提示
                    string latest = await LatestVersionSafeAsync(entry);
                    if (seq != _probeSeq || _closing) return;
                    string target;
                    UpgradeState st = PluginUpgrade.JudgeVersion(m.Plugin.Version, latest, out target);
                    if (st != UpgradeState.Upgradable) continue;      // 同版本/市场更低/无法比较 → 无提示
                    string ign = _ignores.IgnoredUpTo(instId, m.Plugin.Pkg);
                    if (_ignores.IsSuppressed(instId, m.Plugin.Pkg, target))
                    {
                        // 按钮可见性=MarketVersion 非空：被忽略时不写市场版本（保留 IgnoredUpTo 供弹窗“曾忽略”提示）
                        _hints[m.Plugin.Pkg] = new ManageUpgradeHint
                        { MarketVersion = "", IgnoredUpTo = ign, Ignored = true };
                        continue;
                    }
                    _hints[m.Plugin.Pkg] = new ManageUpgradeHint { MarketVersion = target, IgnoredUpTo = ign };
                    changed = true;
                }
                if (changed && seq == _probeSeq && !_closing) RebindRows();
            }
            catch { /* 理由: 升级提示是锦上添花，任何失败都静默降级为“无提示” */ }
        }

        private void RebindRows()
        {
            _suppressProbe = true;
            try { RenderList(); }
            finally { _suppressProbe = false; }
        }

        private async Task<string> LatestVersionSafeAsync(CatalogEntry entry)
        {
            string pkg = (entry.Pkg ?? "").Trim();
            if (pkg.Length == 0) return "";
            lock (_latestVerCache)
            {
                string cached;
                if (_latestVerCache.TryGetValue(pkg, out cached)) return cached ?? "";
            }
            string v = "";
            try { v = ExtractNpmVersion(await HttpFetch.GetStringAsync(
                    "https://registry.npmjs.org/" + Uri.EscapeDataString(pkg) + "/latest", 12000)); }
            catch { /* 理由: 单包最新版查询失败=该包不提示，绝不影响列表 */ }
            lock (_latestVerCache) _latestVerCache[pkg] = v;
            return v;
        }

        /// <summary>从 npm registry latest 元数据 JSON 提取 version 字段。</summary>
        public static string ExtractNpmVersion(string json)
        {
            if (string.IsNullOrEmpty(json)) return "";
            try
            {
                using (var doc = System.Text.Json.JsonDocument.Parse(json))
                {
                    if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object &&
                        doc.RootElement.TryGetProperty("version", out System.Text.Json.JsonElement v))
                        return (v.GetString() ?? "").Trim();
                }
            }
            catch { /* 理由: 非法 JSON 按无版本处理，判定链会保守不提示 */ }
            return "";
        }

        private async Task<string> FetchChangelogSafeAsync(string repo)
        {
            repo = (repo ?? "").Trim().TrimStart('/');
            if (repo.Length == 0 || !repo.Contains('/')) return "";
            try
            {
                string json = await HttpFetch.GetStringAsync(
                    "https://api.github.com/repos/" + repo.Trim('/') + "/releases/latest", 12000);
                if (string.IsNullOrEmpty(json)) return "";
                using (var doc = System.Text.Json.JsonDocument.Parse(json))
                {
                    var root = doc.RootElement;
                    string name = root.TryGetProperty("name", out System.Text.Json.JsonElement n) ? (n.GetString() ?? "") : "";
                    string text = root.TryGetProperty("body", out System.Text.Json.JsonElement b) ? (b.GetString() ?? "").Trim() : "";
                    text = (name.Length > 0 ? name + "\n" : "") + text;
                    if (text.Length > 1400) text = text.Substring(0, 1400) + "…";
                    return text == "\n" ? "" : text;
                }
            }
            catch { /* 理由: changelog 取不到按定稿显示占位文案，不影响主链路 */ }
            return "";
        }
    }
}
