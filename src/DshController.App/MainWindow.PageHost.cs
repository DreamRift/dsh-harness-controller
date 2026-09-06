// ============================================================================
//  MainWindow.PageHost — 顶栏四页按钮与页面宿主切换（改版·顶栏页按钮组）
//  规则：
//    - 顶栏四按钮 = 一级页：实例 / 插件 / 档案 / API；
//      档案页暂由宿主挂用量视图（正式形态在「档案页与显示名」大类交付），
//      API 页为空宿主（内容在「API 预设页」大类交付）；
//    - 页面控件常驻不销毁：切页只改 Visibility，实例选择、状态轮询、
//      日志流与插件页惰性加载（OnShown）全部延续旧侧边栏语义；
//    - 四页统一套 SplitPageShell 两栏壳（Views\SplitPageShell）；旧环境/插件
//      入口（Win/WSL、市场/管理）现为各页左边栏的 BtnRail 行，待后续小类
//      （启动序列表 / 管理实例树）升级为真实上下文列表。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
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
        private string _primary = "inst";     // inst | plug | arch | api | settings | gallery
        private string _instSub = "win";      // win | wsl（详情主区：由左栏选中驱动，不再有入口分叉）
        private string _plugSub = "market";   // market | plugins
        private InstancesRailViewModel _rail; // 改版·启动序列表：左栏实例行（数据只读档案）
        private ArchivesRailViewModel _archRail; // 改版·含退役列表：档案页左栏（总计/活跃/退役）
        private ArchiveMetaViewModel _archMeta; // 改版·元信息一览：选中档案的镜像字段
        private AliasStore _aliasStore; // 改版·改名入口：实例别名台账
        private DialogService _dialogService; // 改名/预设弹窗（MainWindow 级，跟随窗根）
        private Microsoft.UI.Dispatching.DispatcherQueueTimer _railTimer;

        /// <summary>左栏实例列表初始化（构造里在档案启动后调用一次）。</summary>
        private void RailInit()
        {
            _rail = new InstancesRailViewModel(_archive);
            RailWin.Bind(_rail);
            RailWin.RowSelected += row =>
            {
                // 详情主区：行选中 = 定环境子页 + 面板选中该实例（入口合一，无分叉）
                _rail.Select(row.Id);
                _instSub = InstancesRailViewModel.SubPageOf(row);
                _primary = "inst";
                ApplyPage();
            };
            // liveness 结论由调度器随前台节奏写档案；行状态按同一节奏重读结论
            _railTimer = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
            _railTimer.Interval = TimeSpan.FromMilliseconds(2500);
            _railTimer.Tick += (s, e) => { if (_primary == "inst") ApplyPage(); };

        }

        /// <summary>档案页左栏初始化（构造里在档案启动后调用一次）：行点击落选中态并联动详情主区。</summary>
        private void ArchRailInit()
        {
            _archRail = new ArchivesRailViewModel(_archive);
            RailArch.Bind(_archRail);
            _aliasStore = new AliasStore();
            _aliasStore.Load();
            _dialogService = new DialogService(() => Root?.XamlRoot);   // MainWindow 是 Window，XamlRoot 取自根 Grid
            foreach (var kv in _aliasStore.All()) InstanceDisplayName.SetAlias(kv.Key, kv.Value);
            _archMeta = new ArchiveMetaViewModel(_archive, m => AppendLog(m));
            PanelArchiveMeta.Bind(_archMeta);
            PanelArchiveMeta.RenameRequested += async id => await RenameArchiveAsync(id);
            RailArch.RowSelected += row => { _archRail.Select(row.Id); ShowArchiveMeta(row.Id); };
        }

        /// <summary>档案页主区互斥（2026-09-06 二次改版）：选中真实档案 → 单实例详情
        /// （元信息 + 完整用量）；总计行/未知 id → 用量看板（全部实例合并口径）。
        /// 不在这里调 PanelUsage.OnShown()：行点击不改聚合数据，ApplyPage 进页时统一重渲染，
        /// 免得首次进页 Reload 跑两遍（旧语义行点击也不触发 Reload）。</summary>
        private void ShowArchiveMeta(string id)
        {
            if (_archMeta == null || PanelArchiveMeta == null || PanelUsage == null) return;
            PanelArchiveMeta.Show(id);
            bool meta = _archMeta.HasArchive;
            Show(PanelArchiveMeta, meta);
            Show(PanelUsage, _primary == "arch" && !meta);
        }

        /// <summary>改名弹窗：输入别名 → 落台账 + 更新全局别名表 + 全应用刷新（原名进 tooltip）。</summary>
        private async Task RenameArchiveAsync(string archiveId)
        {
            if (string.IsNullOrEmpty(archiveId)) return;
            string currentAlias = InstanceDisplayName.GetAlias(archiveId);
            var tb = new TextBox { PlaceholderText = "输入别名（留空清除）", Text = currentAlias, MinWidth = 220 };
            AutomationProperties.SetAutomationId(tb, "RenameInput");
            var dlg = new ContentDialog
            {
                Title = "改名",
                Content = tb,
                PrimaryButtonText = "保存",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary
            };
            if (await _dialogService.ShowAsync(dlg) == ContentDialogResult.Primary)
            {
                string alias = (tb.Text ?? "").Trim();
                _aliasStore.Set(archiveId, alias);
                InstanceDisplayName.SetAlias(archiveId, alias);
                _archRail.Refresh();
                RailArch.SyncSelection(_archRail.SelectedId);
                ShowArchiveMeta(archiveId);
                AppendLog("[改名] " + archiveId + " → " + (alias.Length > 0 ? alias : "（清除别名）"));
            }
        }

        private void BtnPageInst_Click(object sender, RoutedEventArgs e) => GoPrimary("inst");
        private void BtnPagePlug_Click(object sender, RoutedEventArgs e) => GoPrimary("plug");
        private void BtnPageArch_Click(object sender, RoutedEventArgs e) => GoPrimary("arch");
        private void BtnPageApi_Click(object sender, RoutedEventArgs e) => GoPrimary("api");
        private void BtnSettings_Click(object sender, RoutedEventArgs e) => GoPrimary("settings");
        private void BtnGallery_Click(object sender, RoutedEventArgs e) => GoPrimary("gallery");

        // ==================== 插件页左边栏入口 ====================

        private void BtnSubMarket_Click(object sender, RoutedEventArgs e) { _plugSub = "market"; GoPrimary("plug"); }
        private void BtnSubManage_Click(object sender, RoutedEventArgs e) { _plugSub = "plugins"; GoPrimary("plug"); }

        private void GoPrimary(string key)
        {
            _primary = key;
            ApplyPage();
        }

        /// <summary>按当前状态重排选中态与宿主可见性（面板常驻，仅隐藏/显示）。</summary>
        private void ApplyPage()
        {
            // 实例页前置：重排行 → 选中行（缺省第一行）→ 由其决定环境子页
            InstanceRailRow selRow = null;
            if (_primary == "inst" && _rail != null)
            {
                _rail.Refresh();
                selRow = _rail.Rows.FirstOrDefault(r => r.Id == _rail.SelectedId) ?? _rail.Rows.FirstOrDefault();
                if (selRow != null)
                {
                    _rail.Select(selRow.Id);
                    _instSub = InstancesRailViewModel.SubPageOf(selRow);
                }
                else
                {
                    _rail.Select(null);
                }
            }

            // 档案页前置：左栏三类行重排（总计/活跃/退役）并恢复选中（缺省/回落总计）
            if (_primary == "arch" && _archRail != null)
            {
                _archRail.Refresh();
                RailArch.SyncSelection(_archRail.SelectedId);
                ShowArchiveMeta(_archRail.SelectedId);
            }

            bool inst = _primary == "inst";
            bool plug = _primary == "plug";
            SetPageSelected(BtnPageInst, inst);
            SetPageSelected(BtnPagePlug, plug);
            SetPageSelected(BtnPageArch, _primary == "arch");
            SetPageSelected(BtnPageApi, _primary == "api");
            SetToolSelected(BtnSettingsPage, _primary == "settings");
            SetToolSelected(BtnDevDesk, _primary == "gallery");
            Show(ShellInst, inst);
            Show(ShellPlug, plug);
            Show(ShellArch, _primary == "arch");
            Show(ShellApi, _primary == "api");
            if (plug)
            {
                SetRailSelected(BtnSubMarket, _plugSub == "market");
                SetRailSelected(BtnSubManage, _plugSub == "plugins");
            }

            Show(PanelWin, inst && _instSub == "win");
            Show(PanelWsl, inst && _instSub == "wsl");
            Show(PanelMarket, plug && _plugSub == "market");
            Show(PanelPlugins, plug && _plugSub == "plugins");
            // 档案页主区互斥：详情卡优先（ShowArchiveMeta 已按左栏选中定态并刷新 _archMeta.HasArchive），
            // 只有总计/无真实档案时看板可见；这里兜住"从其它页切回"的路径
            Show(PanelUsage, _primary == "arch" && (_archMeta == null || !_archMeta.HasArchive));
            Show(PageSettings, _primary == "settings");
            Show(PanelGallery, _primary == "gallery");

            // 惰性动作与旧侧边栏一致：首次进入拉数据，再次进入刷新状态
            if (PanelWin.Visibility == Visibility.Visible) { PanelWin.OnShown(); if (selRow != null) PanelWin.SelectFromRail(selRow.Id); }
            if (PanelWsl.Visibility == Visibility.Visible) { PanelWsl.OnShown(); if (selRow != null) PanelWsl.SelectFromRail(selRow.Id); }
            if (PanelMarket.Visibility == Visibility.Visible) PanelMarket.OnShown();
            if (PanelPlugins.Visibility == Visibility.Visible) PanelPlugins.OnShown();
            if (PanelUsage.Visibility == Visibility.Visible) PanelUsage.OnShown();
            if (inst)
            {
                RailWin.SyncSelection(_rail == null ? "" : _rail.SelectedId);
                _railTimer?.Start();               // 进实例页才跟 liveness（只读档案，零探测）
            }
            else _railTimer?.Stop();
        }

        private static void Show(FrameworkElement el, bool visible)
        {
            el.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>顶栏钮选中态成对切换（BtnPage/BtnPageActive 定义在 DshTheme.xaml，随主题走）。</summary>
        private static void SetPageSelected(Button btn, bool active)
        {
            btn.Style = (Style)Application.Current.Resources[active ? "BtnPageActive" : "BtnPage"];
        }

        /// <summary>左栏行选中态成对切换（BtnRail/BtnRailActive，同上）。</summary>
        private static void SetRailSelected(Button btn, bool active)
        {
            btn.Style = (Style)Application.Current.Resources[active ? "BtnRailActive" : "BtnRail"];
        }

        /// <summary>顶栏工具区钮选中态成对切换（BtnTool/BtnToolActive）。</summary>
        private static void SetToolSelected(Button btn, bool active)
        {
            btn.Style = (Style)Application.Current.Resources[active ? "BtnToolActive" : "BtnTool"];
        }
    }
}