// ============================================================================
//  InstancePanel — WSL 环境设置（发行版扫描、关闭策略）
//
//  去版本化轮：版本下拉、npm 版本列表拉取、harness 版本检测
//  链路已整体移除——harness 版本一律跟随当前环境。本文件只保留
//  WSL 关闭策略下拉、发行版扫描与当前发行版读取。
//  （InstancePanel 按职责拆成多个 partial 文件，单文件不超过 400 行。）
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DshController.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DshController
{
    public sealed partial class InstancePanel : UserControl
    {
        private void InitWslPolicyCombo()
        {
            if (!IsWslPanel) return;
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

        private async void BtnListDistros_Click(object sender, RoutedEventArgs e)
        {
            BtnListDistros.IsEnabled = false;
            try
            {
                string keep = (CmbWslDistro.Text ?? "").Trim();
                var distros = await WslTools.ListDistrosAsync();
                CmbWslDistro.Items.Clear();
                foreach (string d in distros) CmbWslDistro.Items.Add(d);
                CmbWslDistro.Text = keep;
                    PushLog(distros.Count > 0
                    ? "已安装的 WSL 发行版: " + string.Join(", ", distros)
                    : "未检测到已安装的 WSL 发行版（可在管理员 PowerShell 执行 wsl --install -d <发行版>）");
            }
            catch (Exception ex) { PushLog("扫描发行版失败: " + ex.Message); }
            finally { BtnListDistros.IsEnabled = true; }
        }

        /// <summary>WSL 面板当前生效的发行版（优先输入框，其次选中实例配置）。</summary>
        private string CurrentDistro()
        {
            string typed = (CmbWslDistro.Text ?? "").Trim();
            if (typed.Length > 0) return typed;
            return (SelectedDef()?.WslDistro ?? "").Trim();
        }

    }
}
