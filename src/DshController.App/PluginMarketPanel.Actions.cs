// ============================================================================
//  PluginMarketPanel · 安装与工具按钮（partial 分部；纯搬移，逐字一致）
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using DshController.Core;
using DshController.Core.Archive;
using DshController.Core.Storage;
using DshController.ViewModels;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DshController
{
    public sealed partial class PluginMarketPanel
    {
        // ==================== 安装 ====================

        private async void BtnInstall_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            if (!((sender as FrameworkElement)?.Tag is MarketItem item)) return;
            await InstallEntryAsync(item);
        }

        /// <summary>安装入口（卡片按钮与详情弹窗共用）：确认 → 官方命令 → 记录 → 重启提示。</summary>
        private async Task InstallEntryAsync(MarketItem item)
        {
            if (_busy) return;
            InstanceDef def = SelectedDef();
            if (def == null)
            {
                await InfoAsync("请先在上方选择要安装到的实例。", "未选择实例");
                return;
            }
            string profile = _target.EffectiveProfile;
            if (profile == null)
            {
                await InfoAsync("profile 只允许字母、数字、-、_、.（默认 web）。", "profile 非法");
                return;
            }
            string target = item.Entry.InstallTarget;
            string invalid = PluginInstaller.ValidateTarget(target);
            if (invalid != null)
            {
                await InfoAsync(invalid, "安装目标不合法");
                return;
            }

            Config cfg = def.ToConfig(_registry.Settings);
            bool shared = PluginUiHelper.UsesSharedDefaultHome(cfg);
            bool initialized = await PluginUiHelper.HomeInitializedAsync(cfg);

            var msg = new StringBuilder();
            msg.AppendLine("插件：" + item.Title);
            msg.AppendLine("安装目标：" + target);
            msg.AppendLine("实例：" + DshController.ViewModels.InstanceDisplayName.For(def) +
                    "（" + DshController.ViewModels.InstanceDisplayName.Original(def) + " · profile " + profile + "）");
            msg.AppendLine("方式：dsh plugin --profile " + profile + " add " + target + "（DSH 官方方式）");
            if (item.Entry.MinHost.Trim().Length > 0)
                msg.AppendLine("支持版本：" + PluginCompat.Judge(item.Entry, _target.HarnessVersion).Text);
            if (shared)
                msg.AppendLine().AppendLine("⚠ 该实例未配置独立 HOME，插件将装入默认 ~/.dsh，与其他默认实例共享、无法隔离。");
            if (!initialized)
                msg.AppendLine().AppendLine("⚠ 该实例 HOME 尚未初始化（还没启动过 dsh），建议先在实例页启动一次再安装。");
            if (item.Entry.Unverified)
                msg.AppendLine().AppendLine("⚠ 该条目来自 GitHub 实时搜索，未经人工审核，请确认插件来源可信后再安装。");
            msg.AppendLine().AppendLine("bundle 插件安装后需重启实例才会生效。确认安装？");
            if (!await ConfirmAsync(msg.ToString(), "安装插件到「" + def.Name + "」")) return;

            await RunInstallAsync(def, cfg, profile, target, item);
        }

        private async Task RunInstallAsync(InstanceDef def, Config cfg, string profile, string target, MarketItem item)
        {
            SetBusy(true, "正在安装 " + item.Title + " …（输出见控制台）");
            ListPlugins.IsEnabled = false;
            try
            {
                var before = new HashSet<string>(_target.InstalledPkgs, StringComparer.OrdinalIgnoreCase);
                PluginOpResult result = def.IsWsl
                    ? await PluginInstaller.RunWslAsync(cfg, PluginOp.Add, profile, target, PushLog)
                    : await PluginInstaller.RunWindowsAsync(cfg, PluginOp.Add, profile, target, PushLog);

                if (!result.Ok)
                {
                    PushLog("[市场] 安装失败" + (result.Error.Trim().Length > 0 ? "：" + result.Error.Trim() : ""));
                    await InfoAsync("安装失败。\n\n命令：" + result.Command + "\n\n" +
                                    (result.Error.Trim().Length > 0 ? result.Error.Trim() : "详见控制台输出。"),
                                    "安装失败");
                    return;
                }

                // 记录新增包（github: 安装后真实包名以实例 HOME 中新出现的依赖为准）
                await _target.ReloadAfterPluginOpAsync();   // 装完必须重扫，不能吃档案缓存
                List<string> added = _target.InstalledPkgs.Except(before, StringComparer.OrdinalIgnoreCase).ToList();
                if (added.Count == 0)
                {
                    PushLog("[市场] 未检测到新增依赖（可能此前已安装），不写市场记录。");
                }
                foreach (string pkg in added)
                {
                    string ver = _target.Installed
                        .FirstOrDefault(p => string.Equals(p.Pkg, pkg, StringComparison.OrdinalIgnoreCase))?.Version ?? "";
                    PluginRecords.Upsert(def.Id, new PluginRecord
                    {
                        Pkg = pkg,
                        Name = item.Title,
                        Repo = item.Entry.Repo ?? "",
                        Version = ver,
                        Profile = profile,
                        Target = target,
                        InstalledAt = DateTime.UtcNow
                    });
                    PushLog("[市场] 已标记为市场安装：" + pkg + (ver.Length > 0 ? "（v" + ver + "）" : ""));
                }
                if (added.Count > 0)
                {
                    // 市场记录刚写完：再采一次，让档案里的"来源=市场"标注与真实状态一致
                    await _target.ReloadAfterPluginOpAsync();
                }
                RebuildList();
                await PromptRestartAsync(def, "安装", target, force: false);
            }
            finally
            {
                ListPlugins.IsEnabled = true;
                SetBusy(false, "");
            }
        }

        /// <summary>插件命令成功后的重启提示：实例运行中 → 一键重启；未运行 → 提示下次启动生效。</summary>
        private async Task PromptRestartAsync(InstanceDef def, string verb, string target, bool force)
        {
            await _ops.PromptRestartAsync(def, verb, target);
        }
        // ==================== 工具按钮 ====================

        private async void BtnRefreshCatalog_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            // 手动刷新 = 强制重新联网拉取目录 + 重新探测 harness 版本 + 刷新已装状态
            InstanceDef cur = SelectedDef();
            if (cur != null)
            {
                PluginUiHelper.InvalidateVersion(cur.Id);
                _archive?.Service.Invalidate(cur.Id, FacetNames.Harness, FacetNames.Plugins);
            }
            await LoadCatalogAsync(force: true);
            if (_closing) return;
            await _target.ForceRefreshAsync();
        }

        private void BtnOpenHome_Click(object sender, RoutedEventArgs e)
        {
            InstanceDef def = SelectedDef();
            if (def == null) return;
            Config cfg = def.ToConfig(_registry.Settings);
            try
            {
                if (def.IsWsl)
                {
                    PushLog("[市场] WSL 实例 HOME：" + PluginUiHelper.WslHomeDisplay(cfg) +
                            "（发行版内路径，可在终端执行 wsl -d " + (cfg.WslDistro ?? "?") + " 查看）");
                    return;
                }
                string home = string.IsNullOrWhiteSpace(cfg.Home) ? AppPaths.DefaultDshHome : cfg.Home;
                Directory.CreateDirectory(home);
                Process.Start(new ProcessStartInfo(home) { UseShellExecute = true });
                PushLog("[市场] 已打开实例 HOME：" + home);
            }
            catch (Exception ex)
            {
                PushLog("[市场] 打开 HOME 失败：" + ex.Message);
            }
        }
    }
}
