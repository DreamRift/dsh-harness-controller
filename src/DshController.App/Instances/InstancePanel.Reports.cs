// ============================================================================
//  InstancePanel — 失败报告提示与路径操作
//
//  失败 InfoBar、报告打开/复制、端口推荐、工作区与 HOME 打开、确认框。
//  （重构 2.0 / P3：InstancePanel 按职责拆成多个 partial 文件，单文件不超过 400 行；
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
        // ==================== 失败报告（报告由核心层生成，此处只提示） ====================

        private void OnStartFailed(object sender, StartFailureContext ctx)
        {
            if (_closing) return;
            InstanceDef def = DefForSender(sender);
            string who = def != null ? "[" + (IsWslPanel ? "WSL·" : "WIN·") + def.Name + "] " : "";
            PushLog(who + "后端启动失败：" + ctx.FailureKind +
                (string.IsNullOrEmpty(ctx.Summary) ? "" : "。" + ctx.Summary));

            // 只为本面板的实例弹提示条（另一个环境的失败不打扰当前界面）
            if (def == null) return;

            _lastReportPath = ctx.ReportPath ?? "";
            FailBar.Title = "启动失败：" + (ctx.FailureKind ?? "未知") + "（" + def.Name + "）";
            FailBar.Message = (string.IsNullOrEmpty(ctx.Summary) ? "" : ctx.Summary + "\n") +
                (string.IsNullOrEmpty(_lastReportPath)
                    ? "⚠ 失败报告未生成，请检查报告目录是否可写（全局设置 → 报告目录）"
                    : "报错详情 + 实例信息 + 时间已写入报告：" + _lastReportPath);
            FailBar.Severity = InfoBarSeverity.Error;
            BtnOpenReport.IsEnabled = !string.IsNullOrEmpty(_lastReportPath);
            BtnCopyReportPath.IsEnabled = !string.IsNullOrEmpty(_lastReportPath);
            FailBar.IsOpen = true;

            if (!string.IsNullOrEmpty(_lastReportPath))
                PushLog(who + "已生成失败报告: " + _lastReportPath);
            else
                PushLog(who + "⚠ 失败报告未生成（请检查报告目录是否可写）");
        }

        private void BtnOpenReport_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_lastReportPath)) return;
            try { Process.Start(new ProcessStartInfo(_lastReportPath) { UseShellExecute = true }); }
            catch (Exception ex) { PushLog("打开报告失败: " + ex.Message); }
        }

        private void BtnOpenReportDir_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!string.IsNullOrEmpty(_lastReportPath) && File.Exists(_lastReportPath))
                {
                    Process.Start("explorer.exe", "/select,\"" + _lastReportPath + "\"");
                    return;
                }
                string dir = ReportDir();
                Directory.CreateDirectory(dir);
                Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
            }
            catch (Exception ex) { PushLog("打开报告目录失败: " + ex.Message); }
        }

        private void BtnCopyReportPath_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dp = new DataPackage();
                dp.SetText(string.IsNullOrEmpty(_lastReportPath) ? ReportDir() : _lastReportPath);
                Clipboard.SetContent(dp);
                PushLog("已复制报告路径到剪贴板。");
            }
            catch (Exception ex) { PushLog("复制失败: " + ex.Message); }
        }

        /// <summary>生效的报告目录（全局设置为空时用「我的文档\DshController\error-reports」）。</summary>
        private string ReportDir()
        {
            string dir = (_registry.Settings.ErrorReportDir ?? "").Trim();
            if (dir.Length > 0) return dir;
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "DshController", "error-reports");
        }

        private async void BtnSuggestPort_Click(object sender, RoutedEventArgs e)
        {
            BtnSuggestPort.IsEnabled = false;
            try
            {
                int suggested = await PortAllocatorSuggestAsync(IsWslPanel ? 3081 : 3080);
                if (suggested > 0)
                {
                    TxtPort.Text = suggested.ToString();
                    PushLog("推荐端口: " + suggested);
                }
                else PushLog("3080–3099 段内没有空闲端口，请手动指定。");
            }
            catch (Exception ex) { PushLog("端口推荐失败: " + ex.Message); }
            finally { BtnSuggestPort.IsEnabled = true; }
        }

        private void BtnOpenWs_Click(object sender, RoutedEventArgs e)
        {
            string ws = (TxtWorkspace.Text ?? "").Trim();
            if (ws.Length == 0) { PushLog("工作目录为空。"); return; }
            if (IsWslPanel && !WslTools.IsWindowsPath(ws))
            {
                // WSL 原生路径经 \\wsl$\<发行版>\ 打开
                string distro = CurrentDistro();
                if (distro.Length == 0) { PushLog("请先填写 WSL 发行版名称。"); return; }
                string unc = @"\\wsl$\" + distro + (ws.StartsWith("~", StringComparison.Ordinal)
                    ? @"\home" + ws.Substring(1).Replace('/', '\\')
                    : ws.Replace('/', '\\'));
                OpenPath(unc);
                return;
            }
            OpenPath(ws);
        }

        private void BtnOpenHome_Click(object sender, RoutedEventArgs e)
        {
            string home = (TxtHome.Text ?? "").Trim();
            if (home.Length == 0)
                home = AppPaths.DefaultDshHome;
            OpenPath(home);
        }

        private void OpenPath(string path)
        {
            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                PushLog("已打开: " + path);
            }
            catch (Exception ex) { PushLog("打开失败（" + path + "）: " + ex.Message); }
        }

        private async Task<bool> ConfirmAsync(string message, string title)
        {
            try
            {
                var dlg = new ContentDialog
                {
                    Title = title,
                    Content = message,
                    PrimaryButtonText = "确定",
                    CloseButtonText = "取消",
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = XamlRoot
                };
                return await _dialogs.ShowAsync(dlg) == ContentDialogResult.Primary;   // 经 DialogService：竞态按取消处理
            }
            catch
            {
                return false;
            }
        }

    }
}
