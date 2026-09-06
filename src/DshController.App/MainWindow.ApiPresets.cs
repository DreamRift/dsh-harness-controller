// ============================================================================
//  MainWindow.ApiPresets — API 页接线（照 dsh 模型页适配：左栏提供方 + 详情编辑卡）
//  与 MainWindow.PageHost 拆分以守住 ≤400 行门禁；弹窗一律走 DialogService；
//  探针网络在 Core（ProviderModelProbe），此处仅发起与呈现候选勾选窗。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using DshController.Core;
using DshController.Core.Storage;
using DshController.ViewModels;

namespace DshController
{
    public sealed partial class MainWindow
    {
        private ProviderPresetStore _presetStore; // 预设全局台账
        private ProviderPresetsViewModel _presetVm; // API 页主从状态机

        /// <summary>API 页初始化（构造里调用一次）。</summary>
        private void ApiPresetsInit()
        {
            _presetStore = new ProviderPresetStore();
            _presetStore.Load();
            _presetVm = new ProviderPresetsViewModel(_presetStore);
            _presetVm.Changed += OnPresetVmChanged;
            RailApi.Bind(_presetVm);
            RailApi.AddRequested += () => _presetVm.BeginCreate();
            PanelApiPresets.Bind(_presetVm);
            PanelApiPresets.AddRequested += () => _presetVm.BeginCreate();
            PanelApiPresets.FetchRequested += async () => await FetchModelsAsync();
            PanelApiPresets.SyncRequested += async id => await SyncPreviewAsync(id);
            PanelApiPresets.DeleteRequested += async id => await DeletePresetAsync(id);
            OnPresetVmChanged();
        }

        /// <summary>摘要与左栏选中回写（Changed 单源）。</summary>
        private void OnPresetVmChanged()
        {
            if (ApiCaption != null) ApiCaption.Text = "供应商 " + _presetVm.Rows.Count + " 家";
            RailApi?.SyncSelection(_presetVm.SelectedId);
        }

        /// <summary>获取可用模型（dsh：按表单当前所填端点/密钥发问；候选勾选后采纳回草稿，
        /// 端点能给出上下文/最大输出时自动填入）。写入动作只发生在保存，探针零写配置。</summary>
        private async Task FetchModelsAsync()
        {
            ProviderEditorDraft draft = _presetVm?.Editor;
            if (draft == null || draft.FetchBusy) return;
            if (!_presetVm.TryGetProbeTarget(out string baseUrl, out string apiKey, out string blocked))
            {
                if (blocked.Length > 0 && _presetVm.Editor != null) _presetVm.Editor.FetchError = blocked;
                return;
            }
            draft.FetchBusy = true;
            draft.FetchError = "";
            ProbeResult r = await ProviderModelProbe.DiscoverAsync(baseUrl, apiKey);
            draft.FetchBusy = false;    // 旧草稿引用复位（若已丢弃则无副作用）
            draft = _presetVm.Editor;   // await 期间卡片可能已被关闭/切换
            if (draft == null) return;
            if (!r.Ok)
            {
                draft.FetchError = r.Error;
                AppendLog("[预设] 获取模型失败：" + r.Error);
                return;
            }
            AppendLog("[预设] 探测到 " + r.Models.Count + " 个模型（" + baseUrl + "）");
            _presetVm.SetCandidates(r.Models);
            await FetchPickDialogAsync();
        }

        /// <summary>候选勾选窗（dsh fetchTitle/全选/取消全选/添加所选）。</summary>
        private async Task FetchPickDialogAsync()
        {
            var checks = new List<CheckBox>();
            var list = new StackPanel { Spacing = 2 };
            foreach (DiscoveredModel m in _presetVm.FetchCandidates)
            {
                var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                panel.Children.Add(new TextBlock { Text = m.Id, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 12 });
                string caps = CapacityText(m);
                if (caps.Length > 0)
                    panel.Children.Add(new TextBlock { Text = caps, FontSize = 11, VerticalAlignment = VerticalAlignment.Center,
                        Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["LabelTertiaryBrush"] });
                var cb = new CheckBox { Content = panel, IsChecked = _presetVm.PickedIds.Contains(m.Id) };
                cb.Checked += (s, e) => { if (!_presetVm.PickedIds.Contains(m.Id)) _presetVm.TogglePick(m.Id); };
                cb.Unchecked += (s, e) => { if (_presetVm.PickedIds.Contains(m.Id)) _presetVm.TogglePick(m.Id); };
                checks.Add(cb);
                list.Children.Add(cb);
            }
            var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var btnAll = new Button { Content = "全选", Style = (Style)Application.Current.Resources["BtnCompact"] };
            var btnNone = new Button { Content = "取消全选", Style = (Style)Application.Current.Resources["BtnCompact"] };
            btnAll.Click += (s, e) => { _presetVm.SetAllPicked(true); foreach (CheckBox c in checks) c.IsChecked = true; };
            btnNone.Click += (s, e) => { _presetVm.SetAllPicked(false); foreach (CheckBox c in checks) c.IsChecked = false; };
            tools.Children.Add(btnAll);
            tools.Children.Add(btnNone);
            var root = new StackPanel { Spacing = 10, MinWidth = 440 };
            root.Children.Add(new TextBlock
            {
                Text = "以下是模型提供方的可用模型，勾选要添加的模型。",
                TextWrapping = TextWrapping.Wrap, FontSize = 12.5
            });
            root.Children.Add(tools);
            root.Children.Add(new ScrollViewer { Content = list, MaxHeight = 380 });
            var dlg = new ContentDialog
            {
                Title = "选择要添加的模型",
                Content = root,
                PrimaryButtonText = "添加所选",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary
            };
            if (await _dialogService.ShowAsync(dlg) == ContentDialogResult.Primary)
            {
                if (_presetVm.AdoptPicked())
                    AppendLog("[预设] 已把勾选模型加入目录（容量可得即自动填入）");
            }
        }

        private static string CapacityText(DiscoveredModel m)
        {
            var parts = new List<string>();
            if (m.Multimodal == true) parts.Add("多模态");
            if (m.ContextWindow.HasValue) parts.Add("上下文 " + ProviderPresetRules.FormatCapacity(m.ContextWindow.Value));
            if (m.MaxTokens.HasValue) parts.Add("最大输出 " + ProviderPresetRules.FormatCapacity(m.MaxTokens.Value));
            return string.Join(" · ", parts);
        }

        /// <summary>同步目标段描述（日志与预览共用）。</summary>
        private static string SyncTargetText(ProviderPreset preset)
        {
            return preset.IsBuiltin
                ? "llm-deepseek.apiKeyEnv（官方密钥引用）"
                : "llm-pi-ai.providers." + preset.ProviderId;
        }

        /// <summary>同步预览窗：目标实例 × 差异 + 将写入块（与写入同源）；确认记录 / 取消零写入。
        /// 非官方写 llm-pi-ai.providers.<key>（含思考档四档与多模态 input）；官方仅送密钥引用。</summary>
        private async Task SyncPreviewAsync(string presetId)
        {
            ProviderPreset preset = _presetStore?.Get(presetId);
            if (preset == null) return;
            if (preset.IsBuiltin && (preset.ApiKey ?? "").Trim().Length == 0)
            {
                await _dialogService.InfoAsync(
                    "官方预设未填密钥：官方同步只把密钥引用（llm-deepseek.apiKeyEnv）送到实例，请先在编辑卡填入密钥。",
                    "同步预览");
                return;
            }
            var targets = new List<(string Id, string Label, string HomeDir, bool IsWsl, string WslDistro, string WslHome)>();
            if (_archive.Instances != null)
            {
                foreach (InstanceDef d in _archive.Instances)
                {
                    string home = string.IsNullOrWhiteSpace(d.Home)
                        ? AppPaths.DefaultDshHome : d.Home;
                    targets.Add((d.Id, InstanceDisplayName.For(d), home, d.IsWsl, d.WslDistro ?? "", d.WslHome ?? ""));
                }
            }
            if (targets.Count == 0)
            {
                await _dialogService.InfoAsync("当前没有可同步的活跃实例。", "同步预览");
                return;
            }
            var targetPairs = new List<(string Id, string Label)>();
            foreach ((string id, string label, _, _, _, _) in targets) targetPairs.Add((id, label));
            List<SyncPlanRow> rows = ProviderSyncPlan.PlanFor(preset, targetPairs, id => null);
            ProviderSyncWrite write = ProviderSyncWrite.For(preset);
            var sb = new StringBuilder();
            sb.AppendLine("将同步预设「" + preset.Name + "」到 " + targets.Count + " 台实例：");
            foreach (SyncPlanRow r in rows)
            {
                bool wsl = targets.Any(t => t.Id == r.InstanceId && t.IsWsl);
                sb.AppendLine(" · " + r.InstanceLabel + (wsl ? "（WSL:写入发行版 Linux 侧 HOME）" : "") +
                    "  [" + r.Kind + "] " + SyncTargetText(preset) +
                    (r.Fields.Count > 0 ? "（" + string.Join("/", r.Fields) + "）" : ""));
            }
            if (rows.Count > 0)
            {
                foreach (string n in rows[0].Notes) sb.AppendLine(" ! " + n);
            }
            sb.AppendLine();
            sb.AppendLine("--- 将写入内容（预览 = 写入内容，同源渲染）---");
            sb.AppendLine(write.Entry != null
                ? ProviderSyncPlan.RenderYamlBlock(write.Entry)
                : "llm-deepseek:" + Environment.NewLine + "  apiKeyEnv: " + write.OfficialApiKeyEnv);
            var body = new TextBlock { Text = sb.ToString(), TextWrapping = TextWrapping.Wrap, FontSize = 12 };
            var scroll = new ScrollViewer { Content = body, MaxHeight = 380 };
            var dlg = new ContentDialog
            {
                Title = "同步预览：确认写入",
                Content = scroll,
                PrimaryButtonText = "确认写入",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close
            };
            ContentDialogResult res = await _dialogService.ShowAsync(dlg);
            if (res == ContentDialogResult.Primary)
            {
                // 真实写入：Windows 直写 settings.yaml；WSL 走发行版 Linux 侧 HOME（N7）。
                // 一次同步只涉及一个预设：WSL 实例解析一次路径后写入（失败不覆盖）。
                int okCount = 0;
                var failLines = new System.Collections.Generic.List<string>();
                var writer = new ProviderSyncWriter();
                foreach ((string id, string label, string home, bool isWsl, string wslDistro, string wslHome) in targets)
                {
                    bool ok; string werr = "";
                    if (isWsl)
                    {
                        WslSyncResult resolve = await writer.ResolveWslSettingsPathAsync(wslDistro, wslHome);
                        WslSyncResult applied = resolve.Path == null
                            ? resolve
                            : await writer.ApplyWslPathAsync(wslDistro, resolve.Path, write);
                        ok = applied.Ok; werr = applied.Error;
                        if (ok) AppendLog("[同步] 已写入 " + label + " · " + resolve.Path + "（" + SyncTargetText(preset) + "）");
                    }
                    else
                    {
                        string settingsPath = System.IO.Path.Combine(home, "settings.yaml");
                        ok = writer.Apply(settingsPath, write, out werr);
                        if (ok) AppendLog("[同步] 已写入 " + label + " · settings.yaml（" + SyncTargetText(preset) + "）");
                    }
                    if (ok) okCount++;
                    else
                    {
                        failLines.Add(label + "：" + werr);
                        AppendLog("[同步] 写入失败 " + label + " · " + werr);
                    }
                }
                string summary = "已写入 " + okCount + "/" + targets.Count + " 台实例（失败不覆盖原配置）。";
                if (failLines.Count > 0) summary += Environment.NewLine + string.Join(Environment.NewLine, failLines);
                await _dialogService.InfoAsync(summary, "同步结果");
            }
        }

        /// <summary>删除预设（dsh 删除确认文案：预设+密钥一起移除，不影响实例已同步配置）。</summary>
        private async Task DeletePresetAsync(string presetId)
        {
            var row = _presetVm?.RowById(presetId);
            if (row == null) return;
            bool confirmed = await _dialogService.ConfirmAsync(
                "删除「" + row.Name + "」会从全局台账移除该预设及其存储的 API 密钥；不影响实例已同步的配置。",
                "删除「" + row.Name + "」？", "删除", "取消");
            if (!confirmed) return;
            if (_presetVm.TryDelete(presetId)) AppendLog("[预设] 已删除 " + row.Name);
        }
    }
}
