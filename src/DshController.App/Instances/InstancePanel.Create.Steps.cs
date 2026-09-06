// ============================================================================
//  InstancePanel — 新建实例弹窗的版本下拉异步填充（改版·分步流程）
//
//  环境/发行版选择已由 MainWindow 左栏流程完成；本文件只负责新建弹窗里
//  harness 版本下拉的异步填充：npm 已发布版本（新→旧），失败/离线时保留
//  "跟随当前环境"并提示手动输入，不阻塞弹窗出现、不阻断创建。
//  （单文件不超过 400 行约定：从 InstancePanel.Create.cs 拆出。）
// ============================================================================

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DshController.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DshController
{
    public sealed partial class InstancePanel : UserControl
    {
        /// <summary>
        /// 打开新建弹窗后异步填充版本下拉（不阻塞弹窗出现）：
        /// Windows 面板经本机 npm view、WSL 面板经发行版内 npm view 拉取已发布版本。
        /// 成功 = 首项"跟随当前环境" + 版本倒序列表；失败 = 只剩首项并提示手输。
        /// </summary>
        private async Task FillCreateVersionComboAsync(ComboBox cmbVersion, ComboBoxItem verDefault, TextBlock verHint)
        {
            try
            {
                List<string> versions = IsWslPanel
                    ? await HarnessVersion.ListVersionsWslAsync(CurrentDistro()).ConfigureAwait(true)
                    : await HarnessVersion.ListVersionsWindowsAsync().ConfigureAwait(true);
                if (_closing) return;

                if (versions.Count == 0)
                {
                    _publishedVersions = new List<string>();
                    verHint.Text = "版本列表获取失败，可手动输入版本号（如 0.1.0-rc.7），该实例经 npx 拉取指定版本启动";
                    return;
                }

                _publishedVersions = versions;
                // 用户若已手输版本则不打断（IsEditable 下手输文本与选中项互斥，填充后恢复手输）
                string typed = (cmbVersion.Text ?? "").Trim();
                bool userTyped = typed.Length > 0 && typed != (verDefault.Content as string ?? "");
                cmbVersion.Items.Clear();
                cmbVersion.Items.Add(verDefault);
                foreach (string v in versions)
                    cmbVersion.Items.Add(new ComboBoxItem { Content = v, Tag = v });
                if (userTyped) cmbVersion.Text = typed;
                else cmbVersion.SelectedItem = verDefault;
                verHint.Text = "默认跟随当前环境主实例版本；已载入 npm 最新 " + versions.Count +
                    " 个版本，也可直接输入版本号（经 npx 拉取）";
            }
            catch (Exception ex)
            {
                // 理由: 版本列表是增强信息（离线/npm 不在时常见），失败只降级为手输，不阻断建实例
                verHint.Text = "版本列表获取失败，可手动输入版本号（如 0.1.0-rc.7），该实例经 npx 拉取指定版本启动";
                PushLog("[版本] 拉取已发布版本列表失败: " + ex.Message);
            }
        }
    }
}
