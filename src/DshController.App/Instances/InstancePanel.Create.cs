// ============================================================================
//  InstancePanel — 新建与克隆实例对话框
//
//  对话框编排；ID/端口/工作区/HOME 的决定逻辑在 InstancePlanFactory。
//  （改版·分步流程：新建入口 = 左栏「＋ 新建实例」→ MainWindow 完成环境/发行版
//   选择 → InstancePanel.OpenCreateAsync 打开本表单，发行版预填值一次性消费；
//   版本下拉的异步填充在 InstancePanel.Create.Steps.cs。
//   重构 2.0 / P3：InstancePanel 按职责拆成多个 partial 文件，单文件不超过 400 行；
//   纯逻辑已抽到 ViewModels 层的 InstanceSettingsValidator / InstancePlanFactory。）
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DshController.Core;
using DshController.Core.Archive;
using DshController.Core.Storage;
using DshController.ViewModels;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;

namespace DshController
{
    public sealed partial class InstancePanel : UserControl
    {
        // ==================== 实例管理 ====================
        // （改版：新建入口改走 OpenCreateAsync（InstancePanel.xaml.cs），工具条「新建」按钮已移除。）

        private async void BtnClone_Click(object sender, RoutedEventArgs e)
        {
            await ShowCreateInstanceDialogAsync(cloneMode: true);
        }

        private async Task ShowCreateInstanceDialogAsync(bool cloneMode)
        {
            try
            {
                // 改版·分步流程：左栏「＋ 新建实例」由 MainWindow 先完成环境/发行版选择，
                // 预填值 _pendingCreateDistro 一次性消费（非空 = 预填并锁定发行版输入）。
                string prefillDistro = _pendingCreateDistro;
                _pendingCreateDistro = null;

                int suggested = await PortAllocatorSuggestAsync(IsWslPanel ? 3081 : 3080);
                var txtName = new TextBox { PlaceholderText = "实例名称，如 项目A" };
                var txtPort = new TextBox { Text = suggested > 0 ? suggested.ToString() : "自动分配", PlaceholderText = "0 或空 = 由 dsh 分配" };

                string inheritedWs = _registry.Settings.EffectiveNewInstanceWorkspaceFor(IsWslPanel)?.Trim();
                if (string.IsNullOrWhiteSpace(inheritedWs))
                    inheritedWs = SelectedDef()?.Workspace;
                if (IsWslPanel && !string.IsNullOrWhiteSpace(inheritedWs) && WslTools.IsWindowsPath(inheritedWs))
                    inheritedWs = null;                    // WSL 面板默认给 Linux 原生路径，避免默认就走 /mnt/c
                if (string.IsNullOrWhiteSpace(inheritedWs))
                    inheritedWs = IsWslPanel
                        ? "~/dsh-workspaces"
                        : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                var txtWorkspace = new TextBox
                {
                    Text = inheritedWs,
                    Style = (Style)Application.Current.Resources["InputBox"]
                };
                var btnBrowseWs = new Button
                {
                    Content = "浏览…",
                    Style = (Style)Application.Current.Resources["BtnCompact"]
                };
                btnBrowseWs.Click += async (_, __) =>
                {
                    string dir = await PickFolderAsync("选择实例工作目录");
                    if (dir != null) txtWorkspace.Text = dir;
                };

                var layout = new StackPanel { Spacing = 12, MinWidth = 440 };
                layout.Children.Add(LabelledField("名称", txtName));
                layout.Children.Add(LabelledField("端口", txtPort));

                var wsRow = new Grid { ColumnSpacing = 8 };
                wsRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
                wsRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                wsRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                wsRow.Children.Add(new TextBlock
                {
                    Text = "工作目录",
                    Style = (Style)Application.Current.Resources["FieldLabel"],
                    VerticalAlignment = VerticalAlignment.Center
                });
                Grid.SetColumn(txtWorkspace, 1);
                wsRow.Children.Add(txtWorkspace);
                Grid.SetColumn(btnBrowseWs, 2);
                wsRow.Children.Add(btnBrowseWs);
                layout.Children.Add(wsRow);

                // WSL 面板：发行版 + Linux 侧 DSH_HOME（环境由面板决定，无需运行环境下拉）
                TextBox txtWslDistro = null;
                TextBox txtWslHome = null;
                if (IsWslPanel)
                {
                    // 分步流程：MainWindow 已选定发行版（prefillDistro 非空）时预填并禁用，
                    // 表单内不再允许改环境/发行版；从面板直接进入（克隆等）时仍可手填。
                    txtWslDistro = new TextBox
                    {
                        PlaceholderText = "如 Ubuntu-26.04",
                        Text = string.IsNullOrEmpty(prefillDistro) ? CurrentDistro() : prefillDistro,
                        IsEnabled = string.IsNullOrEmpty(prefillDistro),
                        Style = (Style)Application.Current.Resources["InputBox"]
                    };
                    txtWslHome = new TextBox
                    {
                        PlaceholderText = "留空 = ~/.dsh；建议 ~/dsh-instances/<名称>",
                        Style = (Style)Application.Current.Resources["InputBox"]
                    };
                    layout.Children.Add(LabelledField("WSL 发行版", txtWslDistro));
                    layout.Children.Add(LabelledField("WSL DSH_HOME", txtWslHome));
                }

                // harness 版本（改版·分步流程）：首项固定"跟随当前环境"（值为空串）；
                // npm 已发布版本列表在弹窗出现后异步填充（Create.Steps.cs，不阻塞弹窗；
                // 获取失败时只剩首项并可手动输入，保留 IsEditable 手输能力）。
                var cmbVersion = new ComboBox
                {
                    Width = 320,
                    IsEditable = true,
                    Style = (Style)Application.Current.Resources["InputCombo"]
                };
                var verDefault = new ComboBoxItem { Content = "跟随当前环境", Tag = "" };
                cmbVersion.Items.Add(verDefault);
                cmbVersion.SelectedItem = verDefault;      // 默认 = 跟随当前环境主实例版本
                var verHint = new TextBlock
                {
                    Text = "默认跟随当前环境主实例版本；也可指定版本（经 npx 拉取该版本启动）",
                    Style = (Style)Application.Current.Resources["FooterText"],
                    TextWrapping = TextWrapping.Wrap
                };
                var verRow = new StackPanel { Spacing = 4 };
                verRow.Children.Add(new TextBlock
                {
                    Text = "harness 版本",
                    Style = (Style)Application.Current.Resources["FieldLabel"]
                });
                verRow.Children.Add(cmbVersion);
                verRow.Children.Add(verHint);
                layout.Children.Add(verRow);
                _ = FillCreateVersionComboAsync(cmbVersion, verDefault, verHint);   // 异步填充，不阻塞弹窗

                ComboBox cmbSource = null;
                ComboBox cmbLevel = null;
                if (cloneMode)
                {
                    cmbSource = new ComboBox { Width = 320 };
                    cmbSource.Items.Add(new CreateSourceItem("blank", "空白沙箱"));
                    if (!IsWslPanel)
                        cmbSource.Items.Add(new CreateSourceItem("default", "克隆 ~/.dsh（默认主目录）"));
                    foreach (InstanceDef other in InstancesOfEnv().Where(x => x.Id != _selectedId))
                        cmbSource.Items.Add(new CreateSourceItem("instance:" + other.Id, "克隆现有实例：" + other.Name));
                    cmbSource.SelectedIndex = 0;
                    layout.Children.Add(LabelledField("克隆来源", cmbSource));

                    cmbLevel = new ComboBox { Width = 320 };
                    cmbLevel.Items.Add(new CreateLevelItem(CloneLevel.Blank, "Blank（仅空目录）"));
                    cmbLevel.Items.Add(new CreateLevelItem(CloneLevel.Standard, "Standard（配置/技能）"));
                    cmbLevel.Items.Add(new CreateLevelItem(CloneLevel.Full, "Full（完整复制）"));
                    cmbLevel.SelectedIndex = 1;
                    layout.Children.Add(LabelledField("克隆档位", cmbLevel));

                    cmbSource.SelectionChanged += (_, __) =>
                    {
                        if (cmbSource.SelectedItem is CreateSourceItem si &&
                            si.Kind == "instance" && _registry.TryGet(si.Value, out InstanceDef src))
                        {
                            string sv = (src.HarnessVersion ?? "").Trim();
                            var match = cmbVersion.Items.OfType<ComboBoxItem>()
                                .FirstOrDefault(i => (i.Tag as string) == sv);
                            if (sv.Length > 0)
                            {
                                if (match == null)
                                {
                                    match = new ComboBoxItem { Content = sv + "（继承自源实例）", Tag = sv };
                                    cmbVersion.Items.Add(match);
                                }
                                cmbVersion.SelectedItem = match;
                            }
                            else
                            {
                                cmbVersion.SelectedItem = verDefault;
                            }
                        }
                    };
                }

                // 新建页同步勾选（改版·供应商同步）：默认勾选；记忆策略 = 每次默认勾选，
                // 不持久记忆（避免对真实 ~/.dsh 意外静默写入）；克隆同样适用（写入仍幂等）。
                var chkSyncPresets = new CheckBox
                {
                    Content = "同步供应商预设（创建后把启用的全局预设写入该实例 settings.yaml）",
                    IsChecked = true
                };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(chkSyncPresets, "ChkSyncPresets");
                layout.Children.Add(chkSyncPresets);

                var dlg = new ContentDialog
                {
                    Title = (cloneMode ? "克隆实例 · " : "新建实例 · ") + (IsWslPanel ? "WSL2 环境" : "Windows 环境"),
                    Content = layout,
                    PrimaryButtonText = "创建",
                    CloseButtonText = "取消",
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = XamlRoot
                };
                var result = await _dialogs.ShowAsync(dlg);   // 经 DialogService：竞态时按取消处理
                if (result != ContentDialogResult.Primary) return;

                string name = txtName.Text.Trim();
                if (string.IsNullOrEmpty(name))
                {
                    PushLog("实例名称不能为空，未创建。");
                    return;
                }

                string id = MakeUniqueId(name);
                string portText = txtPort.Text.Trim();
                int port = 0;
                if (!string.IsNullOrEmpty(portText) && portText != "自动分配")
                {
                    if (!int.TryParse(portText, out port) || port < 1 || port > 65535)
                    {
                        PushLog("端口无效，未创建实例。");
                        return;
                    }
                }

                string workspace = string.IsNullOrWhiteSpace(txtWorkspace.Text)
                    ? DefaultWorkspace()
                    : txtWorkspace.Text.Trim();

                // 版本：下拉选中项 Tag 非空 → 指定版本；否则解析用户手输文本
                string pinnedVersion = "";
                if (cmbVersion.SelectedItem is ComboBoxItem vitm && vitm.Tag is string vtag && vtag.Length > 0 &&
                    string.Equals((cmbVersion.Text ?? "").Trim(), (vitm.Content as string ?? "").Trim(), StringComparison.Ordinal))
                {
                    pinnedVersion = vtag;
                }
                else
                {
                    string typed = (cmbVersion.Text ?? "").Trim();
                    string normalized;
                    if (!HarnessVersion.TryNormalizeVersion(typed, out normalized))
                    {
                        PushLog("harness 版本格式无效（需形如 0.1.0-rc.7），未创建实例。");
                        return;
                    }
                    pinnedVersion = normalized;
                }

                string home = "";
                if (!IsWslPanel)
                {
                    home = _homeMgr.NewHomePath(_registry.Settings.EffectiveHomeRoot, id);
                }
                string srcHome = "";
                CreateSourceItem source = null;
                if (cmbSource != null && cmbSource.SelectedItem is CreateSourceItem sel) source = sel;

                if (IsWslPanel)
                {
                    home = ""; // WSL 实例无需 Windows 侧 HOME
                }
                else if (source is { Kind: "blank" })
                {
                    _homeMgr.CreateBlank(home);
                }
                else if (source is { Kind: "default" })
                {
                    string defaultHome = AppPaths.DefaultDshHome;
                    if (!Directory.Exists(defaultHome))
                    {
                        PushLog("默认 ~/.dsh 不存在，克隆已改为空白目录。");
                        _homeMgr.CreateBlank(home);
                    }
                    else
                    {
                        _homeMgr.Clone(defaultHome, home, SelectedCloneLevel(cmbLevel));
                    }
                }
                else if (source is { Kind: "instance" } && _registry.TryGet(source.Value, out InstanceDef srcDef))
                {
                    srcHome = string.IsNullOrEmpty(srcDef.Home) ? AppPaths.DefaultDshHome : srcDef.Home;
                    if (!Directory.Exists(srcHome))
                    {
                        PushLog("源实例 HOME 目录不存在，克隆已改为空白目录。");
                        _homeMgr.CreateBlank(home);
                    }
                    else
                    {
                        _homeMgr.Clone(srcHome, home, SelectedCloneLevel(cmbLevel));
                    }
                }
                else
                {
                    _homeMgr.CreateBlank(home);
                }

                // WSL 实例默认给每个实例独立的 Linux 侧 DSH_HOME 与工作区（留空会共用 ~/.dsh，失去隔离）
                string wslHomeInput = IsWslPanel ? (txtWslHome?.Text.Trim() ?? "") : "";
                if (IsWslPanel && wslHomeInput.Length == 0) wslHomeInput = "~/dsh-instances/" + id;
                if (IsWslPanel && workspace.TrimEnd('/') == "~/dsh-workspaces")
                    workspace = "~/dsh-workspaces/" + id;

                var def = new InstanceDef
                {
                    Id = id,
                    Name = name,
                    Home = home,
                    Host = "127.0.0.1",
                    Port = port == 0 ? (suggested > 0 ? suggested : (IsWslPanel ? 3081 : 3080)) : port,
                    TrustedHosts = new List<string>(),
                    Workspace = workspace,
                    AutoOpenBrowser = true,
                    StopOnExit = true,
                    CreatedAt = DateTime.UtcNow,
                    Runtime = IsWslPanel ? "wsl" : "windows",
                    WslDistro = IsWslPanel ? (txtWslDistro?.Text.Trim() ?? "") : "",
                    WslHome = wslHomeInput,
                    HarnessVersion = pinnedVersion
                };
                _registry.Add(def);
                _registry.Save();
                WireInstance(def);
                RefreshInstanceList();
                SelectInstance(def.Id);
                PushLog("已创建" + (IsWslPanel ? " WSL" : " Windows") + "实例: " + def.Name +
                    "（" + def.Id + "，端口 " + def.Port + "，" +
                    (pinnedVersion.Length > 0 ? "harness 指定 v" + pinnedVersion : "harness 跟随当前环境") + "）");

                // 新建页同步勾选：勾选 = 把启用的全局预设写入新实例 settings.yaml（失败不阻断创建）
                // 非官方 → llm-pi-ai.providers.<key>（含思考档/多模态）；官方 → 仅送 llm-deepseek 密钥引用；
                // WSL 实例 → 发行版 Linux 侧 HOME（N7：路径解析一次，发行版未运行时命令会自然唤醒）
                if (chkSyncPresets?.IsChecked == true)
                {
                    int written = 0;
                    var syncStore = new ProviderPresetStore();
                    syncStore.Load();
                    var writer = new ProviderSyncWriter();
                    string wslSettingsPath = "";
                    if (IsWslPanel)
                    {
                        WslSyncResult resolve = await writer.ResolveWslSettingsPathAsync(def.WslDistro, wslHomeInput);
                        if (resolve.Path == null)
                            PushLog("  预设同步跳过：" + resolve.Error);
                        else
                            wslSettingsPath = resolve.Path;
                    }
                    foreach (ProviderPreset preset in syncStore.All())
                    {
                        if (!preset.Enabled) continue;
                        bool ok; string werr = "";
                        if (IsWslPanel)
                        {
                            if (wslSettingsPath.Length == 0) { continue; }
                            WslSyncResult applied = await writer.ApplyWslPathAsync(def.WslDistro, wslSettingsPath, ProviderSyncWrite.For(preset));
                            ok = applied.Ok; werr = applied.Error;
                        }
                        else if (home.Length > 0)
                        {
                            ok = writer.Apply(Path.Combine(home, "settings.yaml"), ProviderSyncWrite.For(preset), out werr);
                        }
                        else { continue; }
                        if (ok)
                        {
                            written++;
                            PushLog("  已同步预设「" + preset.Name + "」→ " + (IsWslPanel ? wslSettingsPath : Path.Combine(home, "settings.yaml")));
                        }
                        else
                        {
                            PushLog("  预设「" + preset.Name + "」同步失败（跳过）: " + werr);
                        }
                    }
                    PushLog(written > 0
                        ? "新实例已带 " + written + " 项供应商预设（见 settings.yaml llm-pi-ai.providers）。"
                        : "无启用的供应商预设可同步。");
                }
                NotifyInstancesChanged();
            }
            catch (Exception ex)
            {
                PushLog("创建/克隆实例失败: " + ex.Message);
            }
        }

    }
}
