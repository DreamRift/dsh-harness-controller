// ============================================================================
//  InstancePanel — 实例设置区「升级」卡（dsh 本体升级到指定版本）
//
//  语义：把选定版本的 @deepseek-ai/dsh 真装进本实例的运行环境
//  （Windows 实例 → Windows 全局 npm；WSL 实例 → 发行版内全局 npm），
//  成功后 def.HarnessVersion 置空（跟随环境）并落盘，提示重启实例生效。
//  版本下拉按实例环境拉 npm 已发布版本（新→旧），并以"当前环境已装版本"旁注；
//  执行通道在 Core/HarnessInstaller（Win/WSL 各一条，互不影响）。
//  触发时机：卡片 Loaded 首次填充 + 点开下拉时若列表为空则重拉；
//  RefreshUpgradeCard 是给外部（切实例等时机）预留的同步刷新入口（UI 线程调用）。
//  （InstancePanel 按职责拆成多个 partial 文件，单文件不超过 400 行。）
// ============================================================================

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DshController.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DshController
{
    public sealed partial class InstancePanel
    {
        private bool _upgradeLoading;              // 版本列表拉取中（防并发重拉）
        private int _upgradeSeq;                   // 拉取序号（过期回填直接丢弃）
        private const string FailPrefix = "dsh 升级失败：";   // Core 失败总结行的前缀（提示行里去掉避免重复）

        // ==================== 刷新入口与触发时机 ====================

        /// <summary>升级卡刷新入口（同步、幂等，UI 线程调用）：拉版本列表并刷新"已装版本"旁注。</summary>
        public void RefreshUpgradeCard()
        {
            if (_closing || _registry == null) return;
            _ = PopulateUpgradeComboAsync();
        }

        /// <summary>卡片首次加载即填充（Init/SelectInstance 在别的 partial 文件，这里用 Loaded 自驱动）。</summary>
        private void UpgradeCard_Loaded(object sender, RoutedEventArgs e)
        {
            RefreshUpgradeCard();
        }

        /// <summary>点开下拉时若列表为空（首次离线失败等）则重拉一次。</summary>
        private void CmbUpgradeVersion_DropDownOpened(object sender, object e)
        {
            if (CmbUpgradeVersion.Items.Count == 0 && !_upgradeLoading) RefreshUpgradeCard();
        }

        // ==================== 版本下拉填充 ====================

        /// <summary>
        /// 按实例环境拉取 @deepseek-ai/dsh 可用版本（新→旧）填入下拉，
        /// 并探测"当前环境已装版本"作旁注（失败不阻塞，保留手输能力）。
        /// </summary>
        private async Task PopulateUpgradeComboAsync()
        {
            if (_closing || _registry == null || _upgradeLoading) return;
            InstanceDef def = SelectedDef();
            int seq = ++_upgradeSeq;
            _upgradeLoading = true;
            try
            {
                CmbUpgradeVersion.Items.Clear();
                TxtUpgradeHint.Text = "正在获取 @deepseek-ai/dsh 可用版本…";
                List<string> versions = IsWslPanel
                    ? await HarnessVersion.ListVersionsWslAsync(CurrentDistro()).ConfigureAwait(true)
                    : await HarnessVersion.ListVersionsWindowsAsync().ConfigureAwait(true);
                if (_closing || seq != _upgradeSeq) return;

                if (versions.Count == 0)
                {
                    TxtUpgradeHint.Text = "版本列表获取失败（离线或 npm 不可用），可直接输入版本号后点「升级」。";
                    return;
                }
                foreach (string v in versions)
                    CmbUpgradeVersion.Items.Add(new ComboBoxItem { Content = v, Tag = v });

                // 旁注：当前环境已装版本（package.json 优先，失败/未装显示通用文案，不阻塞）
                string installed = "";
                if (def != null)
                {
                    installed = IsWslPanel
                        ? await HarnessVersion.ResolveWslAsync(def.WslDistro ?? "").ConfigureAwait(true)
                        : await HarnessVersion.ResolveWindowsAsync(def.ToConfig(_registry.Settings)).ConfigureAwait(true);
                    if (_closing || seq != _upgradeSeq) return;
                }

                CmbUpgradeVersion.SelectedIndex = 0;   // 默认预选最新版
                TxtUpgradeHint.Text = (installed.Length > 0 ? "当前环境已装 v" + installed + "；" : "未检测到当前环境已装版本；")
                    + "共 " + versions.Count + " 个可用版本（新→旧），也可直接输入版本号。";
            }
            catch (Exception ex)
            {
                // 理由: 版本列表是增强信息（离线/npm 缺失常见），失败降级为手输提示，不阻塞升级卡使用
                if (!_closing && seq == _upgradeSeq)
                    TxtUpgradeHint.Text = "版本列表获取失败：" + ex.Message + "（可直接输入版本号后点「升级」）。";
            }
            finally
            {
                _upgradeLoading = false;
            }
        }

        // ==================== 升级执行 ====================

        private async void BtnUpgradeInstance_Click(object sender, RoutedEventArgs e)
        {
            if (_closing || _registry == null) return;
            InstanceDef def = SelectedDef();
            if (def == null)
            {
                TxtUpgradeHint.Text = "请先在左栏选择一个实例。";
                return;
            }

            string raw = (CmbUpgradeVersion.Text ?? "").Trim();
            if (raw.Length == 0)
            {
                TxtUpgradeHint.Text = "请先选择或输入要升级到的版本号。";
                return;
            }
            if (!HarnessVersion.TryNormalizeVersion(raw, out string ver) || ver.Length == 0)
            {
                TxtUpgradeHint.Text = "版本号格式不正确（应为 x.y.z 或 x.y.z-rc.n，如 0.1.1-rc.2）。";
                return;
            }

            if (_instanceMgr.For(def.Id).State == BackendState.Running)
            {
                bool go = await _dialogs.ConfirmAsync(
                    "实例运行中，升级后需重启实例生效。是否继续？（建议先停止实例再升级）",
                    "升级 dsh", "继续升级", "取消");
                if (!go) return;
            }

            var sink = new List<string>();             // 过程输出留底（取失败原因用）
            BtnUpgradeInstance.IsEnabled = false;
            TxtUpgradeHint.Text = "升级中…（npm install -g " + HarnessInstaller.Package + "@" + ver + "）";
            try
            {
                bool ok = await Task.Run(() => IsWslPanel
                    ? HarnessInstaller.UpgradeWslAsync(def.WslDistro ?? "", ver, l => CaptureUpgradeLog(sink, l))
                    : HarnessInstaller.UpgradeWindowsAsync(ver, l => CaptureUpgradeLog(sink, l))).ConfigureAwait(true);
                if (_closing) return;
                if (ok)
                {
                    def.HarnessVersion = "";           // 跟随环境（刚装的就是环境全局版本）
                    _registry.Save();
                    TxtUpgradeHint.Text = "升级完成，重启实例生效。";
                    PushLog("[升级] 已把 " + HarnessInstaller.Package + "@" + ver + " 装入 " +
                            (IsWslPanel ? "发行版 " + CurrentDistro() + " 全局 npm" : "Windows 全局 npm") +
                            "；实例已改为跟随环境版本，重启实例生效。");
                }
                else
                {
                    string err = LastErrorLine(sink);
                    TxtUpgradeHint.Text = "升级失败：" + (err.Length > 0 ? err : "详见运行日志");
                    PushLog("[升级] " + HarnessInstaller.Package + "@" + ver + " 安装失败" +
                            (err.Length > 0 ? "：" + err : ""));
                }
            }
            finally
            {
                BtnUpgradeInstance.IsEnabled = true;
            }
        }

        /// <summary>升级过程输出：逐行进运行日志，同时留底供失败原因摘取。</summary>
        private void CaptureUpgradeLog(List<string> sink, string line)
        {
            if (string.IsNullOrEmpty(line)) return;
            PushLog(line);
            sink.Add(line);
            if (sink.Count > 200) sink.RemoveAt(0);    // 只留尾部，npm 长输出不撑内存
        }

        /// <summary>失败原因 = 留底日志的最后一行（Core 的失败总结行），去掉与提示行重复的前缀。</summary>
        private static string LastErrorLine(List<string> sink)
        {
            for (int i = sink.Count - 1; i >= 0; i--)
            {
                string line = sink[i].Trim();
                if (line.Length == 0) continue;
                return line.StartsWith(FailPrefix, StringComparison.Ordinal)
                    ? line.Substring(FailPrefix.Length)
                    : line;
            }
            return "";
        }
    }
}
