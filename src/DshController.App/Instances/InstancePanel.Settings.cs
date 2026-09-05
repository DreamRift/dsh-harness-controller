// ============================================================================
//  InstancePanel — 实例设置表单的读写
//
//  校验规则本身在可测的 InstanceSettingsValidator（端口边界、主机空值、版本号
//  规范化、trusted-hosts 拆分）；这里只做"控件 ↔ 领域对象"的搬运。
//  （重构 2.0 / P3：InstancePanel 按职责拆成多个 partial 文件，单文件不超过 400 行。）
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
        // ==================== 设置读写 ====================

        private void BtnSaveInstance_Click(object sender, RoutedEventArgs e)
        {
            if (!TryReadSettings(showErrors: true)) return;
            SaveAllSettings();
            ExpInstanceSettings.IsExpanded = false;
            InstanceDef def = SelectedDef();
            PushLog("实例设置已保存: " + (def?.Name ?? ""));
        }

        private void BtnCancelInstance_Click(object sender, RoutedEventArgs e)
        {
            RefreshSelectedControls();
            ExpInstanceSettings.IsExpanded = false;
            PushLog("实例设置已取消");
        }

        /// <summary>
        /// 读取设置表单并写回实例定义。校验规则本身在可测的 InstanceSettingsValidator，
        /// 这里只做"控件 ↔ 领域对象"的搬运与 WSL 专属字段处理。
        /// </summary>
        private bool TryReadSettings(bool showErrors)
        {
            InstanceDef def = SelectedDef();
            if (def == null)
            {
                if (showErrors) PushLog("没有选中实例，未保存设置。");
                return false;
            }

            string version = ReadVersionCombo();
            if (version == null)
            {
                if (showErrors)
                    PushLog("harness 版本格式无效（需形如 0.1.0 或 0.1.0-rc.7；留空/选「跟随当前环境」= 用当前环境版本），未保存设置。");
                return false;
            }

            string distroText = IsWslPanel ? (CmbWslDistro.Text ?? "").Trim() : "";
            var input = new InstanceSettingsInput
            {
                Name = string.IsNullOrWhiteSpace(def.Name) ? def.Id : def.Name,
                Host = TxtHost.Text,
                Port = TxtPort.Text,
                Workspace = string.IsNullOrWhiteSpace(TxtWorkspace.Text.Trim())
                    ? DefaultWorkspace() : TxtWorkspace.Text,
                Home = IsWslPanel ? "" : TxtHome.Text,
                TrustedHosts = TxtTrustedHosts.Text,
                HarnessVersion = version,
                IsWsl = IsWslPanel,
                // 发行版留空只警告不阻断（用户可能稍后补），校验器那关用占位符通过
                WslDistro = IsWslPanel ? (distroText.Length == 0 ? "-" : distroText) : "",
                WslHome = IsWslPanel ? TxtWslHome.Text : "",
                AutoOpenBrowser = SwAutoOpen.IsOn,
                StopOnExit = SwStopOnExit.IsOn
            };
            if (IsWslPanel && distroText.Length == 0 && showErrors)
                PushLog("⚠ WSL 实例未填写发行版名称，启动前请补齐（可点「扫描发行版」）。");

            InstanceSettingsResult result = InstanceSettingsValidator.Validate(input);
            if (!result.Ok)
            {
                if (showErrors) PushLog(result.Error + " 未保存设置。");
                return false;
            }

            if (IsWslPanel)
            {
                def.WslDistro = distroText;
                def.WslHome = result.WslHome;
                // WSL 关闭策略属于 WSL 环境公共设置，随实例设置一起保存
                if (CmbWslPolicy.SelectedItem is ComboBoxItem pol && pol.Tag is string polTag)
                    _registry.Settings.WslShutdownPolicy = polTag;
            }
            else
            {
                def.WslDistro = "";
                def.WslHome = "";
            }

            def.Host = result.Host;
            def.Port = result.Port;
            def.Workspace = result.Workspace;
            def.Home = IsWslPanel ? "" : result.Home;
            def.TrustedHosts = result.TrustedHosts;
            _archive?.OnInstanceSettingsChanged(def.Id);   // 设置变了：版本/HOME/发行版分面立即失效
            def.AutoOpenBrowser = SwAutoOpen.IsOn;
            def.StopOnExit = SwStopOnExit.IsOn;
            def.Runtime = IsWslPanel ? "wsl" : "windows";
            def.HarnessVersion = version;

            UpdateHomeLabel(def);
            UpdateUrl(def);
            UpdateVersionText(def);
            RefreshInstanceList();
            SyncPickerSelection();
            return true;
        }

        /// <summary>本环境的默认工作目录（WSL 用 Linux 家目录相对路径，Windows 用我的文档）。</summary>
        private string DefaultWorkspace()
        {
            return IsWslPanel
                ? "~/"
                : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        }

        private void SaveAllSettings()
        {
            try { _registry.Save(); }
            catch { /* 理由: 保存失败不影响运行中的实例；下次改动会再写一次 */ }
        }

        private async void BtnBrowseWs_Click(object sender, RoutedEventArgs e)
        {
            string dir = await PickFolderAsync("选择 dsh 工作目录（默认 workspace 根目录）");
            if (dir != null) TxtWorkspace.Text = dir;
        }

        private async void BtnBrowseHome_Click(object sender, RoutedEventArgs e)
        {
            string dir = await PickFolderAsync("选择实例 DSH_HOME 目录（留空表示不注入）");
            if (dir != null) TxtHome.Text = dir;
        }

        private async Task<string> PickFolderAsync(string title)
        {
            try
            {
                var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
                picker.FileTypeFilter.Add("*");
                WinRT.Interop.InitializeWithWindow.Initialize(picker,
                    WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
                var folder = await picker.PickSingleFolderAsync();
                return folder == null ? null : folder.Path;
            }
            catch (Exception ex)
            {
                PushLog("选择目录失败: " + ex.Message);
                return null;
            }
        }

        private void BtnCopyUrl_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dp = new DataPackage();
                dp.SetText(UrlLink.Content as string ?? "");
                Clipboard.SetContent(dp);
                PushLog("已复制地址到剪贴板。");
            }
            catch (Exception ex)
            {
                PushLog("复制失败: " + ex.Message);
            }
        }
    }
}
