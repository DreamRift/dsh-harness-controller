// ============================================================================
//  MainWindow.ApiPresets — API 页供应商预设编辑 + 同步预览（改版·API 预设页 / 供应商同步）
//  与 MainWindow.PageHost 拆分以守住 ≤400 行门禁；弹窗一律走 DialogService。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using DshController.Core;
using DshController.Core.Storage;
using DshController.ViewModels;

namespace DshController
{
    public sealed partial class MainWindow
    {
        private ProviderPresetStore _presetStore; // 改版·预设存储：全局台账
        private ProviderPresetsViewModel _presetVm; // 改版·供应商编辑页：API 页列表

        /// <summary>API 页供应商预设编辑初始化（构造里调用一次）。</summary>
        private void ApiPresetsInit()
        {
            _presetStore = new ProviderPresetStore();
            _presetStore.Load();
            _presetVm = new ProviderPresetsViewModel(_presetStore);
            _presetVm.Changed += () => { if (ApiCaption != null) ApiCaption.Text = "供应商 " + _presetVm.Rows.Count + " 家"; };
            PanelApiPresets.Bind(_presetVm);
            PanelApiPresets.AddRequested += async () => await EditPresetDialogAsync(null);
            PanelApiPresets.EditRequested += async id => await EditPresetDialogAsync(id);
            PanelApiPresets.DeleteRequested += async id => await DeletePresetAsync(id);
            PanelApiPresets.SyncRequested += async id => await SyncPreviewAsync(id);
        }

        /// <summary>新建/编辑预设表单（DialogService；校验失败弹提示不改动）。</summary>
        private async Task EditPresetDialogAsync(string presetId)
        {
            bool isNew = string.IsNullOrEmpty(presetId);
            ProviderPreset src = isNew ? null : _presetStore.Get(presetId);
            var tbName = PresetField("PresetName", "预设名（必填）", src?.Name);
            var tbKind = PresetField("PresetKind", "类别（deepseek / openai-compatible）", src?.Kind);
            var tbUrl = PresetField("PresetBaseUrl", "BaseUrl（留空=默认端点）", src?.BaseUrl);
            var tbKey = PresetField("PresetApiKey", "API 密钥（可选）", src?.ApiKey);
            var tbModel = PresetField("PresetModel", "默认模型（可选）", src?.DefaultModel);
            var chk = new CheckBox { Content = "启用" };
            chk.IsChecked = src == null || src.Enabled;
            AutomationProperties.SetAutomationId(chk, "PresetEnabled");
            var panel = new StackPanel { Spacing = 8, MinWidth = 300 };
            panel.Children.Add(tbName);
            panel.Children.Add(tbKind);
            panel.Children.Add(tbUrl);
            panel.Children.Add(tbKey);
            panel.Children.Add(tbModel);
            panel.Children.Add(chk);
            var dlg = new ContentDialog
            {
                Title = isNew ? "新建供应商预设" : "编辑供应商预设",
                Content = panel,
                PrimaryButtonText = "保存",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary
            };
            if (await _dialogService.ShowAsync(dlg) != ContentDialogResult.Primary) return;
            bool enabled = chk.IsChecked ?? true;
            string error;
            bool ok = isNew
                ? _presetVm.TryAdd(tbName.Text, tbKind.Text, tbUrl.Text, tbKey.Text, tbModel.Text, enabled,
                    out string newId, out error)
                : _presetVm.TryUpdate(presetId, tbName.Text, tbKind.Text, tbUrl.Text, tbKey.Text, tbModel.Text,
                    enabled, out error);
            if (!ok)
            {
                await _dialogService.InfoAsync("保存未通过校验：" + error, "预设校验");
                return;
            }
            PanelApiPresets.SyncSelection(isNew ? _presetVm.SelectedId : presetId);
            AppendLog("[预设] " + (isNew ? "新建 " : "编辑 ") + tbName.Text.Trim());
        }

        private static TextBox PresetField(string automationId, string placeholder, string text)
        {
            var tb = new TextBox { PlaceholderText = placeholder, Text = text ?? "", MinWidth = 260 };
            AutomationProperties.SetAutomationId(tb, automationId);
            return tb;
        }

        /// <summary>同步预览窗：目标实例 × 差异 + 将写入块（与写入同源）；确认记录 / 取消零写入。
        /// 实际写入引擎在「写入生效」小类接线。</summary>
        private async Task SyncPreviewAsync(string presetId)
        {
            ProviderPreset preset = _presetStore?.Get(presetId);
            if (preset == null) return;
            var targets = new List<(string Id, string Label, string HomeDir)>();
            if (_archive.Instances != null)
            {
                foreach (InstanceDef d in _archive.Instances)
                {
                    string home = string.IsNullOrWhiteSpace(d.Home)
                        ? AppPaths.DefaultDshHome : d.Home;
                    targets.Add((d.Id, InstanceDisplayName.For(d), home));
                }
            }
            if (targets.Count == 0)
            {
                await _dialogService.InfoAsync("当前没有可同步的活跃实例。", "同步预览");
                return;
            }
            var targetPairs = new List<(string Id, string Label)>();
            foreach ((string id, string label, _) in targets) targetPairs.Add((id, label));
            List<SyncPlanRow> rows = ProviderSyncPlan.PlanFor(preset, targetPairs, id => null);
            MappingResult mapped = ProviderConfigMapper.ToEntry(preset);
            var sb = new StringBuilder();
            sb.AppendLine("将同步预设「" + preset.Name + "」到 " + targets.Count + " 台实例：");
            foreach (SyncPlanRow r in rows)
            {
                sb.AppendLine(" · " + r.InstanceLabel + "  [" + r.Kind + "] providers." + r.EntryKey +
                    (r.Fields.Count > 0 ? "（" + string.Join("/", r.Fields) + "）" : ""));
            }
            foreach (string n in mapped.Notes) sb.AppendLine(" ! " + n);
            sb.AppendLine();
            sb.AppendLine("--- 将写入块（预览 = 写入内容，同源 RenderYamlBlock）---");
            sb.AppendLine(ProviderSyncPlan.RenderYamlBlock(mapped.Entry));
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
                // 真实写入：每台目标实例 settings.yaml 并入 providers.<key>（同源渲染；失败不覆盖）
                int okCount = 0;
                var failLines = new System.Collections.Generic.List<string>();
                var writer = new ProviderSyncWriter();
                foreach ((string id, string label, string home) in targets)
                {
                    string settingsPath = System.IO.Path.Combine(home, "settings.yaml");
                    if (writer.Apply(settingsPath, mapped.Entry, out string werr))
                    {
                        okCount++;
                        AppendLog("[同步] 已写入 " + label + " · settings.yaml（providers." + mapped.Entry.Key + "）");
                    }
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

        /// <summary>删除预设（确认后从全局台账移除）。</summary>
        private async Task DeletePresetAsync(string presetId)
        {
            var row = _presetVm?.RowById(presetId);
            if (row == null) return;
            bool confirmed = await _dialogService.ConfirmAsync(
                "删除供应商预设「" + row.Name + "」？只影响本机全局预设，不影响实例已同步配置。",
                "删除预设", "删除", "取消");
            if (!confirmed) return;
            if (_presetVm.TryDelete(presetId)) AppendLog("[预设] 已删除 " + row.Name);
        }


    }
}
