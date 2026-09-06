// ============================================================================
//  InstancePanel — 扫描发现与删除实例
//
//  扫描运行中实例、已装发行版询问添加、删除实例与其 HOME。
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
        // ==================== 手动扫描运行中实例（v0.5.1） ====================

        private bool _scanning;

        /// <summary>
        /// 手动扫描"正在运行但未注册"的本环境实例并加入列表（左栏「扫描」入口，
        /// 由 MainWindow 经 InstancePanel.RunScanAsync 转发；按钮态控制已随工具条
        /// 按钮移除，仅用 _scanning 防重入）。
        /// Windows：netstat → 进程命令行识别；WSL：发行版内 pgrep + /proc 解析
        /// （对 GUI 启动之后才在 WSL 终端手动启动的实例同样有效）。
        /// 另探测"已安装 dsh"的发行版：运行中的只读探测；未运行的先弹窗征求许可，
        /// 用户确认才拉起扫描（Core 探测完自动恢复关机）。
        /// 发现项不立即落盘（与启动时自动发现语义一致，编辑保存后持久化）。
        /// </summary>
        public async Task RunScanAsync()
        {
            if (_scanning || _closing) return;
            _scanning = true;
            PushLog("[扫描] 正在扫描…");
            try
            {
                List<InstanceDef> found = await Task.Run(() => InstanceDiscovery.Scan()).ConfigureAwait(true);
                // v0.5.1：去重键改为 (运行环境, 端口)——跨环境同端口是合法并存
                // 改版修复：发现结果跨环境全收（改版后 WSL 面板仅在存在 WSL 实例时可达，
                // 若只收本环境实例，WSL 实例清零后任何面板的扫描都永远找不回 WSL）。
                var known = new HashSet<(bool Wsl, int Port)>(
                    _registry.Instances.Select(d => (d.IsWsl, d.Port)));
                int added = 0;
                var backendDistros = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (InstanceDef d in found)
                {
                    if (d.IsWsl) backendDistros.Add(d.WslDistro ?? "");
                    if (known.Contains((d.IsWsl, d.Port))) continue; // 已注册（含本次扫描刚加入的）
                    known.Add((d.IsWsl, d.Port));
                    try
                    {
                        _registry.Add(d);
                        added++;
                        PushLog("[" + (d.IsWsl ? "WSL" : "WIN") + "] 发现运行中实例: " + d.Name +
                            "（端口 " + d.Port +
                            (d.IsWsl ? "，发行版 " + d.WslDistro +
                                (string.IsNullOrEmpty(d.WslHome) ? "" : "，DSH_HOME " + d.WslHome) : "") + "）");
                    }
                    catch (Exception ex)
                    {
                        // 理由: 扫描到的实例与现有清单冲突（id/端口重复）时跳过该条，不影响其余发现
                        PushLog("[扫描] 跳过一条发现结果：" + ex.Message);
                    }
                }

                // v0.6.1：追加"已安装 dsh 但未运行"的发行版——主实例不开机也能被扫描发现。
                // 改版修复：不再限定 WSL 面板才触发（可达性同上），任意面板的扫描都提供询问添加。
                // 运行中发行版只读探测；未运行的先弹窗征求许可，用户点"确认"才拉起（Core 探测完自动恢复关机）。
                List<InstanceDiscovery.DistroInstallInfo> installed =
                    await Task.Run(() => InstanceDiscovery.ScanInstalledDistros(backendDistros)).ConfigureAwait(true);
                var candidates = await Task.Run(() => InstanceDiscovery.OfflineBootCandidates(backendDistros)).ConfigureAwait(true);
                List<InstanceDiscovery.DistroInstallInfo> offlineFound = new List<InstanceDiscovery.DistroInstallInfo>();
                if (candidates.Count > 0 && await ConfirmBootWslScanAsync(candidates))
                    offlineFound = await Task.Run(() => InstanceDiscovery.ProbeOfflineDistrosAsync(candidates,
                        line => PushLog("[WSL] " + line))).ConfigureAwait(true);

                // 两批探测结果合并后统一询问添加（运行中 / 离线拉起的发行版按构造不相交）
                int installedAdded = await PromptAddInstalledDistrosAsync(installed.Concat(offlineFound).ToList());

                if (added > 0 || installedAdded > 0)
                {
                    RefreshInstanceList();
                    NotifyInstancesChanged();   // 变更汇聚点：刷新左栏/页脚，并经 EnsureWired 补齐跨面板事件接线
                    PushLog("[" + (IsWslPanel ? "WSL" : "WIN") + "] 扫描完成：新增 " + added +
                        " 个运行中实例、" + installedAdded + " 个已安装实例" +
                        (installedAdded > 0 ? "（新实例已入左栏，WSL 实例点选后进入 WSL 面板）" : ""));
                }
                else
                {
                    PushLog("[" + (IsWslPanel ? "WSL" : "WIN") + "] 扫描完成：未发现新的运行中实例，也没有检测到已安装 dsh 的发行版" +
                        "（运行中发现：请确认实例进程仍在运行——WSL 发行版闲置被系统回收时，其中的实例会一并停止；" +
                        "已安装发现：需发行版处于运行状态，且发行版内已安装 dsh）");
                }
            }
            catch (Exception ex)
            {
                PushLog("[" + (IsWslPanel ? "WSL" : "WIN") + "] 扫描失败: " + ex.Message);
            }
            finally
            {
                _scanning = false;
            }
        }

        /// <summary>
        /// 拉起"已注册但未运行"的 WSL 发行版前征求用户许可（拉起消耗系统资源，必须显式确认）。
        /// 返回 true = 用户同意；false = 拒绝或弹窗异常（一律不拉起）。
        /// </summary>
        private async Task<bool> ConfirmBootWslScanAsync(List<string> candidates)
        {
            var msg = new StringBuilder();
            foreach (string distro in candidates) msg.AppendLine("· " + distro);
            msg.AppendLine("它们当前未运行。确认后将逐个拉起扫描是否安装 dsh（每个几秒钟，扫描完自动恢复关闭）。");
            try
            {
                var dlg = new ContentDialog
                {
                    Title = "拉起 WSL 来扫描？",
                    Content = msg.ToString(),
                    PrimaryButtonText = "确认",
                    CloseButtonText = "不扫描WSL",
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = XamlRoot
                };
                if (await _dialogs.ShowAsync(dlg) != ContentDialogResult.Primary)
                {
                    PushLog("[WSL] 已跳过未运行发行版的拉起扫描（用户选择）。");   // 经 DialogService：竞态按拒绝处理
                    return false;
                }
                return true;
            }
            catch
            {
                // 理由: 弹窗竞态/宿主不可用时按用户拒绝处理——未经确认绝不擅自拉起发行版
                return false;
            }
        }

        /// <summary>
        /// 对"已安装 dsh 的发行版"探测结果（运行中只读探测 + 离线拉起探测，由调用方合并传入）
        /// 过滤后弹窗询问是否添加为（未运行的）实例。已注册同发行版实例的自动跳过。返回添加数量。
        /// 用户经弹窗明确确认的添加立即落盘（与静默自动发现的"不立即落盘"语义区分）。
        /// </summary>
        private async Task<int> PromptAddInstalledDistrosAsync(List<InstanceDiscovery.DistroInstallInfo> installed)
        {
            // 过滤：注册表里已有同发行版实例的不再建议（无论是否运行过）
            installed = installed.Where(i => !_registry.Instances.Any(d =>
                d.IsWsl && string.Equals(d.WslDistro, i.Distro, StringComparison.OrdinalIgnoreCase))).ToList();
            if (installed.Count == 0) return 0;

            var msg = new StringBuilder();
            msg.AppendLine("检测到以下发行版已安装 dsh（当前未运行）：");
            msg.AppendLine();
            foreach (InstanceDiscovery.DistroInstallInfo info in installed)
            {
                msg.AppendLine("· " + info.Distro +
                    (info.DshVersion.Length > 0 ? "（dsh v" + info.DshVersion + "）" : "") +
                    (info.HomeInitialized ? " · 默认 ~/.dsh 已初始化" : ""));
            }
            msg.AppendLine();
            msg.AppendLine("是否把它们添加为实例？（默认 ~/.dsh，端口自动分配；启动后由该实例自己的 HOME 隔离）");

            try
            {
                var dlg = new ContentDialog
                {
                    Title = "发现已安装的 dsh 发行版",
                    Content = msg.ToString(),
                    PrimaryButtonText = "全部添加",
                    CloseButtonText = "不添加",
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = XamlRoot
                };
                if (await _dialogs.ShowAsync(dlg) != ContentDialogResult.Primary) return 0;   // 经 DialogService：竞态按不添加处理
            }
            catch { return 0; }   // 理由: 弹窗竞态/宿主不可用时按"不添加"处理，不阻断扫描流程

            int added = 0;
            InstanceDef firstAdded = null;
            foreach (InstanceDiscovery.DistroInstallInfo info in installed)
            {
                // 实例 ID 仅允许字母/数字/_/-（IsValidId）：发行版名中的 '.' 一并转为 '_'
                string id = "auto-wslinst-" + new string(info.Distro.Select(
                    c => char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_').ToArray());
                int port = await PortAllocatorSuggestAsync(3081).ConfigureAwait(true);
                var def = new InstanceDef
                {
                    Id = id,
                    Name = "WSL " + info.Distro + "（默认安装）",
                    Host = "127.0.0.1",
                    Port = port > 0 ? port : 3081,
                    Workspace = "~/dsh-workspaces",
                    Runtime = "wsl",
                    WslDistro = info.Distro,
                    WslHome = "",                    // 空 = 发行版默认 ~/.dsh（与手动默认安装一致）
                    AutoOpenBrowser = true,
                    StopOnExit = true,
                    CreatedAt = DateTime.UtcNow
                };
                try
                {
                    _registry.Add(def);
                    added++;
                    if (firstAdded == null) firstAdded = def;
                    PushLog("[WSL] 发现已安装 dsh 的发行版: " + info.Distro +
                        (info.DshVersion.Length > 0 ? "（dsh v" + info.DshVersion + "）" : "") +
                        " → 已添加为实例（端口 " + def.Port + "，未运行）");
                }
                catch (Exception ex)
                {
                    PushLog("[WSL] 添加实例失败（" + info.Distro + "）: " + ex.Message);
                }
            }
            if (added > 0)
            {
                try { _registry.Save(); }   // 用户经弹窗明确确认的添加立即落盘，避免关窗即丢
                catch (Exception ex) { PushLog("[WSL] 实例清单落盘失败: " + ex.Message); }
            }
            if (firstAdded != null && firstAdded.IsWsl == IsWslPanel)
            {
                try { SelectInstance(firstAdded.Id); }
                catch { /* 理由: 选中新实例只是便利动作，失败不影响已添加的结果 */ }
            }
            else if (firstAdded != null)
            {
                PushLog("[WSL] 新实例已入左栏（" + firstAdded.Name + "），点选它即可进入 WSL 面板配置/启动。");
            }
            return added;
        }

        private CloneLevel SelectedCloneLevel(ComboBox cmbLevel)
        {
            if (cmbLevel != null && cmbLevel.SelectedItem is CreateLevelItem item)
                return item.Level;
            return CloneLevel.Standard;
        }

        /// <summary>
        /// 生成唯一实例 id。规则本身在可测的 InstancePlanFactory 里：
        /// 名称净化为合法字符，冲突才追加序号（旧实现一律加随机后缀，id 难读）。
        /// </summary>
        private string MakeUniqueId(string name)
        {
            var existing = new List<string>();
            foreach (InstanceDef d in _registry.Instances) existing.Add(d.Id);
            return InstancePlanFactory.MakeUniqueId(name, existing);
        }

        private async Task<int> PortAllocatorSuggestAsync(int preferred)
        {
            var allocator = new PortAllocator();
            IEnumerable<int> taken = _registry.Instances.Select(x => x.Port);
            return await allocator.SuggestAsync(preferred, taken);
        }

        private async void BtnDelete_Click(object sender, RoutedEventArgs e)
        {
            InstanceDef def = SelectedDef();
            if (def == null) return;

            string homeDesc = def.IsWsl
                ? (string.IsNullOrWhiteSpace(def.WslHome) ? "~/.dsh（发行版内）" : def.WslHome)
                : (string.IsNullOrEmpty(def.Home) ? "（默认 ~/.dsh）" : def.Home);
            bool ok = await ConfirmAsync(
                "实例：" + def.Name + "\nID：" + def.Id + "\n运行环境：" + (def.IsWsl ? "WSL2" : "Windows") +
                "\nHOME：" + homeDesc +
                "\n\n将停止该实例并删除数据。是否继续？",
                "删除实例");
            if (!ok) { PushLog("已取消删除实例。"); return; }

            try
            {
                BackendState st = _instanceMgr.For(def.Id).State;
                if (st == BackendState.Running || st == BackendState.Starting ||
                    st == BackendState.Stopping || st == BackendState.Restarting)
                {
                    await _instanceMgr.StopAsync(def.Id, killExternal: false);
                }

                _instanceMgr.For(def.Id).Dispose();
                _wired.Remove(def.Id);
                _registry.Remove(def.Id);
                _registry.Save();
                RefreshInstanceList();
                _selectedId = InstancesOfEnv().FirstOrDefault()?.Id ?? "";
                SelectInstance(_selectedId);

                if (!def.IsWsl && !string.IsNullOrEmpty(def.Home))
                {
                    string backup = "";
                    if (!_homeMgr.Delete(def.Home, keepBackup: false, out backup))
                        PushLog("删除 HOME 目录失败（可能已被占用或不存在）: " + def.Home);
                }
                NotifyInstancesChanged();
            }
            catch (Exception ex)
            {
                PushLog("删除实例失败: " + ex.Message);
            }
        }

        private void NotifyInstancesChanged()
        {
            try { _onInstancesChanged?.Invoke(); }
            catch { /* 理由: 页脚刷新失败不影响实例本身，下次变更会再通知 */ }
        }

        private StackPanel LabelledField(string label, UIElement input)
        {
            if (input is TextBox textBox && textBox.Style == null)
                textBox.Style = (Style)Application.Current.Resources["InputBox"];
            else if (input is ComboBox comboBox && comboBox.Style == null)
                comboBox.Style = (Style)Application.Current.Resources["InputCombo"];

            var sp = new StackPanel { Spacing = 4 };
            sp.Children.Add(new TextBlock
            {
                Text = label,
                Style = (Style)Application.Current.Resources["FieldLabel"]
            });
            sp.Children.Add(input);
            return sp;
        }

        private sealed class CreateSourceItem
        {
            public CreateSourceItem(string kind, string text)
            {
                Kind = kind;
                Value = kind.StartsWith("instance:", StringComparison.OrdinalIgnoreCase)
                    ? kind.Substring("instance:".Length) : "";
                Text = text;
            }
            public string Kind { get; }
            public string Value { get; }
            public string Text { get; }
            public override string ToString() { return Text; }
        }

        private sealed class CreateLevelItem
        {
            public CreateLevelItem(CloneLevel level, string text)
            {
                Level = level;
                Text = text;
            }
            public CloneLevel Level { get; }
            public string Text { get; }
            public override string ToString() { return Text; }
        }

    }
}
