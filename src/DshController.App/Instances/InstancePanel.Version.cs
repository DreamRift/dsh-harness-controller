// ============================================================================
//  InstancePanel — harness 版本与 WSL 环境设置
//
//  版本下拉、npm 版本列表拉取、发行版扫描、关闭策略。
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
        private void UpdateVersionText(InstanceDef def)
        {
            string pinned = (def?.HarnessVersion ?? "").Trim();
            if (def == null)
            {
                VersionText.Text = "harness 版本未知";
                LaunchModeText.Text = "";
                return;
            }

            if (pinned.Length > 0)
            {
                VersionText.Text = "harness v" + pinned + " · 指定版本";
                LaunchModeText.Text = IsWslPanel
                    ? "启动方式: 发行版内 npx --yes @deepseek-ai/dsh@" + pinned + "（首次拉取需联网）"
                    : "启动方式: npx --yes @deepseek-ai/dsh@" + pinned + "（首次拉取需联网）";
            }
            else
            {
                VersionText.Text = _detectedVersion.Length > 0
                    ? "harness v" + _detectedVersion + " · 当前环境"
                    : "harness 版本未知 · 当前环境";
                LaunchModeText.Text = IsWslPanel
                    ? "启动方式: 发行版内已安装的 dsh（跟随当前环境主实例版本）"
                    : "启动方式: 本机已安装的 dsh（跟随当前环境主实例版本）";
            }
        }

        // ==================== harness 版本（v0.5.0） ====================

        /// <summary>版本下拉：① 跟随当前环境（默认）② 当前已指定的版本 ③ 拉取到的已发布版本。</summary>
        private void PopulateVersionCombo(InstanceDef def)
        {
            _loadingSettings = true;
            try
            {
                CmbVersion.Items.Clear();
                var defItem = new ComboBoxItem
                {
                    Content = _detectedVersion.Length > 0
                        ? "跟随当前环境（v" + _detectedVersion + "）"
                        : "跟随当前环境",
                    Tag = ""
                };
                CmbVersion.Items.Add(defItem);

                string pinned = (def?.HarnessVersion ?? "").Trim();
                if (pinned.Length > 0)
                    CmbVersion.Items.Add(new ComboBoxItem { Content = pinned + "（当前指定）", Tag = pinned });

                // 当前环境版本也提供「显式指定」入口（与「跟随」区分：显式 = 走 npx 拉取该版本）
                if (_detectedVersion.Length > 0 && _detectedVersion != pinned)
                    CmbVersion.Items.Add(new ComboBoxItem
                    {
                        Content = _detectedVersion + "（指定为当前环境版本）",
                        Tag = _detectedVersion
                    });

                foreach (string v in _publishedVersions)
                {
                    if (v == pinned || v == _detectedVersion) continue;
                    CmbVersion.Items.Add(new ComboBoxItem { Content = v, Tag = v });
                }

                CmbVersion.SelectedItem = pinned.Length > 0
                    ? CmbVersion.Items.OfType<ComboBoxItem>().First(i => (i.Tag as string) == pinned)
                    : defItem;
            }
            finally { _loadingSettings = false; }
        }

        /// <summary>
        /// 读取版本下拉当前值：返回规范化后的版本号；空串 = 跟随当前环境。
        /// 输入非法（不是 x.y.z[-预发布]）时返回 null，由调用方提示并放弃保存。
        /// </summary>
        private string ReadVersionCombo()
        {
            if (CmbVersion.SelectedItem is ComboBoxItem itm && itm.Tag is string tag)
            {
                // 选中项就是权威值（下拉项文案带中文说明，不能按文本解析）
                string sel = (CmbVersion.Text ?? "").Trim();
                string selContent = (itm.Content as string ?? "").Trim();
                if (sel.Length == 0 || sel == selContent) return tag;
            }
            string typed = (CmbVersion.Text ?? "").Trim();
            string normalized;
            if (!HarnessVersion.TryNormalizeVersion(typed, out normalized)) return null;
            return normalized;
        }

        private void InitWslPolicyCombo()
        {
            if (!IsWslPanel) return;
            _loadingSettings = true;
            try
            {
                CmbWslPolicy.Items.Clear();
                CmbWslPolicy.Items.Add(new ComboBoxItem { Content = "smart（推荐：按需关发行版/VM）", Tag = "smart" });
                CmbWslPolicy.Items.Add(new ComboBoxItem { Content = "distroOnly（只终止发行版）", Tag = "distroOnly" });
                CmbWslPolicy.Items.Add(new ComboBoxItem { Content = "always（总是 wsl --shutdown）", Tag = "always" });
                CmbWslPolicy.Items.Add(new ComboBoxItem { Content = "never（都不关闭）", Tag = "never" });
                string cur = (_registry.Settings.WslShutdownPolicy ?? "smart").Trim();
                CmbWslPolicy.SelectedItem = CmbWslPolicy.Items.OfType<ComboBoxItem>()
                    .FirstOrDefault(i => string.Equals(i.Tag as string, cur, StringComparison.OrdinalIgnoreCase))
                    ?? CmbWslPolicy.Items[0];
            }
            finally { _loadingSettings = false; }
        }

        private async void BtnListDistros_Click(object sender, RoutedEventArgs e)
        {
            BtnListDistros.IsEnabled = false;
            try
            {
                string keep = (CmbWslDistro.Text ?? "").Trim();
                var distros = await WslTools.ListDistrosAsync();
                _loadingSettings = true;
                try
                {
                    CmbWslDistro.Items.Clear();
                    foreach (string d in distros) CmbWslDistro.Items.Add(d);
                    CmbWslDistro.Text = keep;
                }
                finally { _loadingSettings = false; }
                PushLog(distros.Count > 0
                    ? "已安装的 WSL 发行版: " + string.Join(", ", distros)
                    : "未检测到已安装的 WSL 发行版（可在管理员 PowerShell 执行 wsl --install -d <发行版>）");
            }
            catch (Exception ex) { PushLog("扫描发行版失败: " + ex.Message); }
            finally { BtnListDistros.IsEnabled = true; }
        }

        private async void BtnFetchVersions_Click(object sender, RoutedEventArgs e)
        {
            BtnFetchVersions.IsEnabled = false;
            string old = BtnFetchVersions.Content as string;
            BtnFetchVersions.Content = "拉取中…";
            try
            {
                List<string> versions;
                if (IsWslPanel)
                {
                    string distro = CurrentDistro();
                    if (distro.Length == 0)
                    {
                        PushLog("⚠ 请先填写 WSL 发行版名称，再拉取版本列表");
                        return;
                    }
                    versions = await HarnessVersion.ListVersionsWslAsync(distro);
                }
                else
                {
                    versions = await HarnessVersion.ListVersionsWindowsAsync();
                }

                if (versions.Count == 0)
                {
                    PushLog("未能从 npm registry 拉取版本列表（检查网络/npm 是否可用）；可直接手动输入版本号");
                    return;
                }
                _publishedVersions = versions;
                PopulateVersionCombo(SelectedDef());
                PushLog("已拉取 " + versions.Count + " 个已发布版本，最新: " + versions[0]);
            }
            catch (Exception ex) { PushLog("拉取版本列表失败: " + ex.Message); }
            finally
            {
                BtnFetchVersions.Content = old ?? "拉取版本列表";
                BtnFetchVersions.IsEnabled = true;
            }
        }

        /// <summary>WSL 面板当前生效的发行版（优先输入框，其次选中实例配置）。</summary>
        private string CurrentDistro()
        {
            string typed = (CmbWslDistro.Text ?? "").Trim();
            if (typed.Length > 0) return typed;
            return (SelectedDef()?.WslDistro ?? "").Trim();
        }

        private async void BtnDetectVersion_Click(object sender, RoutedEventArgs e)
        {
            BtnDetectVersion.IsEnabled = false;
            try { await DetectVersionAsync(show: true, force: true); }
            finally { BtnDetectVersion.IsEnabled = true; }
        }

        private async Task DetectVersionAsync(bool show, bool force = false)
        {
            string envKey = IsWslPanel ? "wsl:" + CurrentDistro() : "windows";
            if (!force && _versionDetectDone && _detectedFor == envKey) return;

            string v = "";
            try
            {
                if (IsWslPanel)
                {
                    string distro = CurrentDistro();
                    if (distro.Length == 0)
                    {
                        if (show) PushLog("⚠ 请先在实例设置中填写发行版名称，再检测 WSL 内 harness 版本");
                        _versionDetectDone = true;
                        return;
                    }
                    v = await HarnessVersion.ResolveWslAsync(distro).ConfigureAwait(true);
                }
                else
                {
                    Config cfg = SelectedDef()?.ToConfig(_registry.Settings) ?? new Config();
                    v = await HarnessVersion.ResolveWindowsAsync(cfg).ConfigureAwait(true);
                }
            }
            catch { v = ""; }

            _detectedVersion = v;
            _versionDetectDone = true;
            _detectedFor = envKey;
            if (show)
            {
                PushLog(v.Length > 0
                    ? (IsWslPanel ? "WSL " + CurrentDistro() + " 内" : "Windows 环境") + " harness 主实例版本: v" + v
                    : "未检测到当前环境 harness 版本（请确认 dsh 已安装）");
            }
            RefreshSelectedControls();
            NotifyInstancesChanged();
        }

    }
}
