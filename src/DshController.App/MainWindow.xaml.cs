// ============================================================================
//  MainWindow — 主窗口交互（v0.5.1 侧边栏布局，code-behind，无 MVVM）
//
//  结构：
//    - 顶栏四页（实例/插件/档案/API）+ 过渡子页条；页面宿主切换在
//      MainWindow.PageHost.cs；PanelWin / PanelWsl（InstancePanel）等页面
//      常驻不销毁，切换页面只改可见性，实例选择、状态轮询与操作不中断；
//    - 本窗口负责：标题栏/主题、页面宿主、全局设置页、共享控制台坞
//      （可收起）、状态栏、关闭清理；
//    - 控制台为全局共享：两个面板的所有后端日志经回调汇入，带
//      [WIN·名称] / [WSL·名称] 前缀；
//    - 启动失败报告由核心层（BackendManager.FailStart → ErrorReporter）
//      生成到用户指定的报告目录，面板负责弹窗提示，本窗口提供
//      "打开报告目录"快捷按钮。
// ============================================================================

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using DshController.Core;
using DshController.Core.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Pickers;

namespace DshController
{
    public sealed partial class MainWindow : Window
    {
        private const int LogMaxLines = 2000;
        private const int PendingLogMaxLines = 20000;

        private readonly InstanceRegistry _registry;
        private readonly InstanceManager _instanceMgr;
        private readonly ArchiveHub _archive;   // 重构 2.0：实例档案（界面取数的唯一来源）
        private readonly ConcurrentQueue<string> _pendingLog = new ConcurrentQueue<string>();
        private readonly LogBuffer _logBuffer = new LogBuffer(LogMaxLines);
        private readonly ObservableCollection<string> _logLines = new ObservableCollection<string>();
        private DispatcherQueueTimer _logFlushTimer;
        private int _pendingLogCount;
        private int _droppedLogLines;
        private bool _autoScroll = true;
        private bool _closing;
        private bool _closeCleanupDone;
        private AppTheme _theme;
        private bool _consoleVisible = false;  // 控制台坞展开状态（v0.6.1：默认收起，日志后台照常记录）

        public MainWindow(InstanceRegistry registry)
        {
            InitializeComponent();
            _registry = registry;
            _registry.Settings.Theme = NormalizeTheme(_registry.Settings.Theme);
            _theme = _registry.Settings.Theme;
            ApplyTheme(_theme);

            // 窗口外观：默认尺寸对齐 ZCode 桌面窗口（外框 1530×960；
            // 实测窗口边框+标题栏占 18/47，客户区 1512×913 → 外框精确一致）
            try
            {
                AppWindow.ResizeClient(new Windows.Graphics.SizeInt32(1512, 913));
                if (AppWindow.Presenter is OverlappedPresenter op)
                {
                    op.PreferredMinimumWidth = 960;
                    op.PreferredMinimumHeight = 620;
                }
                string ico = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
                if (File.Exists(ico)) AppWindow.SetIcon(ico);
            }
            catch { /* 理由: 窗口尺寸/图标按平台能力设置，失败则退回默认尺寸与无图标，不影响窗口创建 */ }
            // 右侧工具区精确避开系统标题按钮（Win11 compact overlay 返回实际内边距，兜底 150px）
            try
            {
                var tb = AppWindow.TitleBar;
                if (tb != null && tb.RightInset > 0)
                    TitleTools.Margin = new Thickness(0, 0, tb.RightInset + 12, 0);
            }
            catch { /* 理由: 标题栏内边距读取失败，工具区退回默认边距，仅影响与系统按钮的间距 */ }
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);

            // 多实例管理器：所有实例（Windows + WSL）共享同一个 InstanceManager，
            // 面板只按运行环境过滤实例列表。
            _instanceMgr = new InstanceManager(DispatcherQueueUiDispatcher.ForCurrentThread(), _registry);
            // 实例档案：清单镜像 + 各分面按各自的刷新间隔在后台采集，界面只读档案
            _archive = new ArchiveHub(_registry);

            // 【2026-09 改版小修·当场】重构 2.0 把日志改成虚拟化列表后，ItemsSource 接线
            // 一直没接上（_logLines 在写、LogList 不看）——控制台显示空。补单源绑定。
            LogList.ItemsSource = _logLines;

            // 高密度 stdout/stderr 不再逐行触发 TextBox 重排；统一在 UI 线程批量提交。
            _logFlushTimer = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
            _logFlushTimer.Interval = TimeSpan.FromMilliseconds(100);
            _logFlushTimer.Tick += (s, e) => FlushPendingLogs();
            _logFlushTimer.Start();

            // 两个独立面板（各自持有实例列表 / 状态 / 设置）
            PanelWin.Init(_registry, _instanceMgr, "windows", AppendLog, UpdateFooter, _archive);
            PanelWsl.Init(_registry, _instanceMgr, "wsl", AppendLog, UpdateFooter, _archive);
            // 左栏「＋ 新建实例 / 扫描」按钮接线：事件由 InstancesRailView 暴露（并行代理新增），MainWindow 统一路由
            RailWin.NewInstanceRequested += () => _ = OnRailNewInstanceAsync();
            RailWin.ScanRequested += () => _ = OnRailScanAsync();
            // 插件市场 / 插件管理（页面惰性加载：首次切过去才拉目录 / 读实例 HOME）
            PanelMarket.Init(_registry, _instanceMgr, AppendLog, _archive);
            PanelPlugins.Init(_registry, _instanceMgr, AppendLog, _archive);
            // 用量页：新架构样板（View + ViewModel + x:Bind，页面自己不碰数据源）
            PanelUsage.Init(new DshController.ViewModels.UsageViewModel(_archive, AppendLog));
            _archive.Start();
            if (!string.IsNullOrEmpty(_archive.ImportNotice)) AppendLog(_archive.ImportNotice);

            LoadGlobalSettings();
            ApplyConsoleVisibility();   // v0.6.1：控制台默认收起（右侧按钮展开）

            // 开发态设计台：仅 --dev 启动时出现在顶栏工具区（改样式时的单页检查面）
            BtnDevDesk.Visibility = DshController.Views.GalleryView.IsDevMode
                ? Visibility.Visible : Visibility.Collapsed;

            // 左栏实例列表（启动序列表）：档案启动后接视图模型
            RailInit();
            ArchRailInit();   // 改版·含退役列表：档案页左栏（总计/活跃/退役）
            ApiPresetsInit(); // 改版·供应商编辑页：API 页预设 CRUD

            // 默认落实例页（Windows 子页）；宿主切换逻辑在 MainWindow.PageHost.cs
            ApplyPage();

            Closed += OnWindowClosed;
            // 关闭前清理：AppWindow.Closing 无 deferral（WASDK 1.5），用 取消+重关 模式，
            // 保证 stopOnExit 的进程树清理完成后窗口才真正销毁
            AppWindow.Closing += async (s, e) =>
            {
                if (_closeCleanupDone) return;
                _closeCleanupDone = true;
                e.Cancel = true;
                _closing = true;
                try { PanelWin.SilentSave(); } catch { /* 理由: 退出路径尽力保存 Windows 面板状态，失败不阻断关闭，面板状态下次启动重建 */ }
                try { PanelWsl.SilentSave(); } catch { /* 理由: 退出路径尽力保存 WSL 面板状态，失败无碍关闭，状态以内存态为准 */ }
                try { _archive.Dispose(); }
                catch { /* 理由: 退出路径，档案已尽力落盘，失败不阻断关闭 */ }
                try
                {
                    var t = _instanceMgr.StopAllOnExitAsync();
                    await Task.WhenAny(t, Task.Delay(15000));
                }
                catch { /* 理由: 停止实例进程树已设 15 秒兜底，失败多为实例已退出，不应再影响窗口销毁 */ }
                _registry.Save();
                Close(); // _closeCleanupDone 已置位，本次不再拦截
            };

            AppendLog("DshController 已启动（v" + ErrorReporter.AppVersion + "）。" +
                      "实例、插件、档案、API 经顶栏切换；控制台默认收起，可点「展开」。");
            UpdateFooter();
        }

        private AppTheme NormalizeTheme(AppTheme theme)
        {
            return theme == AppTheme.System || theme == AppTheme.Light || theme == AppTheme.Dark
                ? theme
                : AppTheme.System;
        }

        // ==================== 主题 ====================

        private void BtnTheme_Click(object sender, RoutedEventArgs e)
        {
            // 三态循环：跟随系统 → 浅色 → 深色
            _theme = _theme == AppTheme.System ? AppTheme.Light
                   : _theme == AppTheme.Light ? AppTheme.Dark
                   : AppTheme.System;
            ApplyTheme(_theme);
            _registry.Settings.Theme = _theme;
            try { _registry.Save(); }
            catch (Exception ex) { AppendLog("主题设置保存失败（本次窗口内生效）：" + ex.Message); }
        }

        private void ApplyTheme(AppTheme theme)
        {
            Root.RequestedTheme = theme == AppTheme.Light ? ElementTheme.Light
                                : theme == AppTheme.Dark ? ElementTheme.Dark
                                : ElementTheme.Default;
            // 标题栏鲸鱼随主题换色（黑/白），保证 Mica 背景上可见
            bool dark = theme == AppTheme.Dark ||
                        (theme == AppTheme.System && IsSystemDark());
            TitleBarLogoSource.UriSource = new Uri(
                "ms-appx:///Assets/" + (dark ? "whale-white.svg" : "whale.svg"));
        }

        private static bool IsSystemDark()
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    if (k != null)
                    {
                        object v = k.GetValue("AppsUseLightTheme");
                        if (v is int i) return i == 0;
                    }
                }
            }
            catch { /* 理由: 读取系统深色主题注册表可能被安全策略拒绝，失败按浅色处理，仅影响默认主题颜色 */ }
            return false;
        }

        // ==================== 页面导航 ====================
        // 侧边栏导航（Nav_SelectionChanged / ShowPage）已由顶栏四页替代，
        // 宿主切换与选中态见 MainWindow.PageHost.cs。

        // ==================== 控制台坞 ====================

        /// <summary>右侧常驻的收起/展开按钮（v0.6.1：默认收起，日志后台照常记录）。</summary>
        private void BtnConsoleToggle_Click(object sender, RoutedEventArgs e)
        {
            _consoleVisible = !_consoleVisible;
            ApplyConsoleVisibility();
        }

        private void ApplyConsoleVisibility()
        {
            // 收起 = 只藏日志区；标题与按钮条常驻（高度压到按钮本身大小）
            Visibility logVisibility = _consoleVisible ? Visibility.Visible : Visibility.Collapsed;
            LogList.Visibility = logVisibility;
            ConsoleFrame.Visibility = logVisibility;
            TxtConsoleToggle.Text = _consoleVisible ? "收起" : "展开";
            IconConsoleToggle.Glyph = _consoleVisible ? "\uE70D" : "\uE70E";   // 收起▼ / 展开▲
            if (_consoleVisible) ScrollLogToEnd();
        }

        // ==================== 应用设置 ====================

        private void LoadGlobalSettings()
        {
            TxtReportDir.Text = _registry.Settings.ErrorReportDir;
            TxtHomeRoot.Text = _registry.Settings.HomeRoot;
            TxtNewWsWin.Text = _registry.Settings.NewInstanceWorkspaceWin;
            TxtNewWsWsl.Text = _registry.Settings.NewInstanceWorkspaceWsl;
            TxtCatalogRefresh.Text = Math.Max(0, _registry.Settings.PluginAutoRefreshHours).ToString();
            TxtDshCommand.Text = _registry.Settings.DshCommand;
            TxtRegistry.Text = _registry.Settings.PluginRegistryUrl;

            RefreshPolicy rp = _registry.Settings.Refresh ?? new RefreshPolicy();
            SwAutoRefresh.IsOn = rp.AutoRefreshEnabled;
            TxtRefHarness.Text = rp.HarnessHours.ToString();
            TxtRefPlugins.Text = rp.PluginsHours.ToString();
            TxtRefUsageRun.Text = rp.UsageRunningMinutes.ToString();
            TxtRefHome.Text = rp.HomeHours.ToString();
            TxtRefWslEnv.Text = rp.WslEnvHours.ToString();
            UpdateArchiveInfo();
        }

        private void BtnSaveGlobal_Click(object sender, RoutedEventArgs e)
        {
            _registry.Settings.ErrorReportDir = TxtReportDir.Text.Trim();
            _registry.Settings.HomeRoot = TxtHomeRoot.Text.Trim();
            _registry.Settings.NewInstanceWorkspaceWin = TxtNewWsWin.Text.Trim();
            _registry.Settings.NewInstanceWorkspaceWsl = TxtNewWsWsl.Text.Trim();
            // 刷新间隔：非法输入保持原值；负数按 0（每次打开都刷新）处理
            if (int.TryParse(TxtCatalogRefresh.Text.Trim(), out int hours))
                _registry.Settings.PluginAutoRefreshHours = Math.Max(0, hours);
            _registry.Settings.DshCommand = TxtDshCommand.Text.Trim();
            _registry.Settings.PluginRegistryUrl = TxtRegistry.Text.Trim();
            _registry.Settings.Theme = _theme;

            // 实例档案的分面刷新间隔（非法输入保持原值，负数按 0 = 只手动处理）
            RefreshPolicy rp = _registry.Settings.Refresh ?? new RefreshPolicy();
            rp.AutoRefreshEnabled = SwAutoRefresh.IsOn;
            rp.HarnessHours = ReadInterval(TxtRefHarness, rp.HarnessHours);
            rp.PluginsHours = ReadInterval(TxtRefPlugins, rp.PluginsHours);
            rp.UsageRunningMinutes = ReadInterval(TxtRefUsageRun, rp.UsageRunningMinutes);
            rp.HomeHours = ReadInterval(TxtRefHome, rp.HomeHours);
            rp.WslEnvHours = ReadInterval(TxtRefWslEnv, rp.WslEnvHours);
            _registry.Settings.Refresh = rp;

            try { _registry.Save(); }
            catch (Exception ex) { AppendLog("警告：应用设置保存失败，本次会话内生效、重启后回退：" + ex.Message); }
            UpdateArchiveInfo();
            GoPrimary("inst");
            AppendLog("应用设置已保存（报告目录: " +
                (string.IsNullOrEmpty(_registry.Settings.ErrorReportDir)
                    ? "默认" : _registry.Settings.ErrorReportDir) + "）");
            UpdateFooter();
        }

        private static int ReadInterval(TextBox box, int fallback)
        {
            return int.TryParse((box.Text ?? "").Trim(), out int v) ? Math.Max(0, v) : fallback;
        }

        /// <summary>设置页底部的档案概况：存放位置 + 档案份数（含已退役）。</summary>
        private void UpdateArchiveInfo()
        {
            if (_archive == null) return;
            try
            {
                IReadOnlyList<DshController.Core.Archive.InstanceArchive> all = _archive.Service.All();
                int retired = 0;
                foreach (DshController.Core.Archive.InstanceArchive a in all) if (a.IsRetired) retired++;
                TxtArchiveInfo.Text = "档案目录：" + _archive.Service.Store.Dir +
                                      "　共 " + all.Count + " 份（已删除实例保留 " + retired +
                                      " 份，档案不会随实例删除而消失）";
            }
            catch (Exception ex)
            {
                TxtArchiveInfo.Text = "档案概况读取失败：" + ex.Message;
            }
        }

        private void BtnCancelGlobal_Click(object sender, RoutedEventArgs e)
        {
            LoadGlobalSettings();
            GoPrimary("inst");
            AppendLog("应用设置已取消");
        }

        private async void BtnBrowseRd_Click(object sender, RoutedEventArgs e)
        {
            string dir = await PickFolderAsync("选择错误报告保存目录");
            if (dir != null) TxtReportDir.Text = dir;
        }

        private async void BtnBrowseRoot_Click(object sender, RoutedEventArgs e)
        {
            string dir = await PickFolderAsync("选择新实例 DSH_HOME 的根目录");
            if (dir != null) TxtHomeRoot.Text = dir;
        }

        private async void BtnBrowseNewWs_Click(object sender, RoutedEventArgs e)
        {
            string dir = await PickFolderAsync("选择新建 Windows 实例默认工作区目录");
            if (dir != null) TxtNewWsWin.Text = dir;
        }

        private async Task<string> PickFolderAsync(string title)
        {
            try
            {
                var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
                picker.FileTypeFilter.Add("*");
                WinRT.Interop.InitializeWithWindow.Initialize(picker,
                    WinRT.Interop.WindowNative.GetWindowHandle(this));
                var folder = await picker.PickSingleFolderAsync();
                return folder == null ? null : folder.Path;
            }
            catch (Exception ex)
            {
                AppendLog("选择目录失败: " + ex.Message);
                return null;
            }
        }

        // ==================== 控制台 ====================

        public void NotifyCrash(string reportPath)
        {
            AppendLog("发生未处理异常" + (reportPath != null ? "，崩溃报告: " + reportPath : "（报告写入失败）"));
        }

        private void AppendLog(string line)
        {
            if (_closing) return;
            if (System.Threading.Interlocked.Increment(ref _pendingLogCount) > PendingLogMaxLines)
            {
                System.Threading.Interlocked.Decrement(ref _pendingLogCount);
                System.Threading.Interlocked.Increment(ref _droppedLogLines);
                return;
            }
            _pendingLog.Enqueue(line ?? "");
        }

        /// <summary>
        /// 在 UI 线程按行批量追加（每轮最多 500 行）：虚拟化列表下追加是 O(1)，
        /// 不再像旧实现那样每 100ms 重建整段 20 万字符的文本。
        /// </summary>
        private void FlushPendingLogs()
        {
            if (_closing || LogList == null) return;
            try
            {
                int dropped = System.Threading.Interlocked.Exchange(ref _droppedLogLines, 0);
                if (dropped > 0) AddLogLine("[系统] 日志过于密集，已丢弃 " + dropped + " 行未显示的输出。");

                int added = 0;
                while (added < 500 && _pendingLog.TryDequeue(out string line))
                {
                    System.Threading.Interlocked.Decrement(ref _pendingLogCount);
                    AddLogLine(line);
                    added++;
                }
                if (added == 0 && dropped == 0) return;
                if (_autoScroll) ScrollLogToEnd();
            }
            catch
            {
                // 理由: 日志渲染失败绝不能影响后端操作；下一轮批次会继续尝试
            }
        }

        private void AddLogLine(string line)
        {
            _logLines.Add(_logBuffer.Append(line, DateTime.Now));
            while (_logLines.Count > LogMaxLines) _logLines.RemoveAt(0);
        }

        /// <summary>把日志区滚动到底部。</summary>
        private void ScrollLogToEnd()
        {
            if (_logLines.Count == 0) return;
            try { LogList.ScrollIntoView(_logLines[_logLines.Count - 1]); }
            catch { /* 理由: 列表尚未完成布局时滚动会抛，忽略即可（下一批还会滚） */ }
        }

        private void BtnClearLog_Click(object sender, RoutedEventArgs e)
        {
            while (_pendingLog.TryDequeue(out _)) { }
            System.Threading.Interlocked.Exchange(ref _pendingLogCount, 0);
            System.Threading.Interlocked.Exchange(ref _droppedLogLines, 0);
            _logBuffer.Clear();
            _logLines.Clear();
        }

        private void BtnCopyLog_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // 复制前先提交队列，避免用户在 100ms 批处理窗口内点击时漏掉最新输出。
                FlushPendingLogs();
                string text = _logBuffer.Text();
                if (string.IsNullOrEmpty(text))
                {
                    AppendLog("控制台为空，没有可复制的日志。");
                    return;
                }
                var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
                dp.SetText(text);
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
                AppendLog("已复制全部日志到剪贴板。");
            }
            catch (Exception ex)
            {
                AppendLog("复制日志失败: " + ex.Message);
            }
        }

        private void BtnAutoScroll_Click(object sender, RoutedEventArgs e)
        {
            _autoScroll = !_autoScroll;
            BtnAutoScroll.Content = _autoScroll ? "滚动：开" : "滚动：关";
        }

        private void BtnOpenReportDir_Click(object sender, RoutedEventArgs e)
        {
            string dir = _registry.Settings.ErrorReportDir?.Trim();
            if (string.IsNullOrEmpty(dir))
                dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "DshController", "error-reports");
            try
            {
                Directory.CreateDirectory(dir);
                ProcessStart(dir);
            }
            catch (Exception ex)
            {
                AppendLog("打开报告目录失败: " + ex.Message);
            }
        }

        private static void ProcessStart(string path)
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }

        private void UpdateFooter()
        {
            // 实例增删改都会走到这里：顺带把清单镜像进档案（内部有节流），
            // 被删掉的实例在这里被标记退役——档案文件本身永久保留。
            try { _archive?.SyncRegistry(); }
            catch { /* 理由: 档案同步失败不能影响页脚与主流程，下一次变更会再试 */ }

            int win = _registry.Instances.Count(d => !d.IsWsl);
            int wsl = _registry.Instances.Count(d => d.IsWsl);
            string report = string.IsNullOrEmpty(_registry.Settings.ErrorReportDir)
                ? "默认目录" : _registry.Settings.ErrorReportDir;

            // 去版本化轮：页脚不再显示 harness 版本段（实例页版本链路已整体移除）
            FooterText.Text = "Windows 实例 " + win + " 个 · WSL 实例 " + wsl + " 个 · 报告目录: " + report + " · v" + ErrorReporter.AppVersion;

            _rail?.Refresh();   // 改版·启动序列表：清单与打点变化即时重排左栏（UpdateFooter 是变更汇聚点）
            // 跨面板新增实例后的接线补齐：任一面板扫描发现的实例（含对方环境）都在这里补上事件接线
            try { PanelWin.EnsureWired(); } catch { /* 理由: 面板未就绪时跳过，下一次清单变更会再试 */ }
            try { PanelWsl.EnsureWired(); } catch { /* 理由: 同上 */ }
        }

        // ==================== 左栏新建 / 扫描路由（启动序列表头部按钮） ====================

        /// <summary>
        /// 左栏「＋ 新建实例」：本机没有 WSL 发行版时直接开 Windows 新建向导；
        /// 有发行版先弹环境选择，选 WSL 再按发行版数量决定是否二次选择。
        /// </summary>
        private async Task OnRailNewInstanceAsync()
        {
            try
            {
                // 1) 枚举本机 WSL 发行版：离线清单优先（不起 wsl.exe 进程），失败当无 WSL
                List<string> distros = new List<string>();
                try { distros = WslTools.ListRegisteredDistrosOffline() ?? new List<string>(); }
                catch (Exception ex)
                {
                    // 理由: 离线清单读取失败（未装 WSL/清单缺失）按无发行版处理，不阻断新建流程
                    AppendLog("[新建] 离线发行版清单读取失败: " + ex.Message);
                }
                if (distros.Count == 0)
                {
                    // 离线为空再问一次 wsl.exe（await 异步等结果，不在 UI 线程同步阻塞）；仍失败当无 WSL
                    try { distros = await WslTools.ListDistrosAsync(); }
                    catch (Exception ex)
                    {
                        // 理由: wsl.exe 探测失败视为本机无 WSL，直接走 Windows 新建
                        AppendLog("[新建] WSL 发行版探测失败: " + ex.Message);
                    }
                }

                // 2) 本机无 WSL → 不弹选择，直接开 Windows 新建
                if (distros.Count == 0) { await PanelWin.OpenCreateAsync(); return; }

                // 3) 环境选择：Windows / WSL / 取消
                var envDlg = new ContentDialog
                {
                    Title = "新建实例",
                    Content = "要在哪个环境创建实例？",
                    PrimaryButtonText = "Windows 环境",
                    SecondaryButtonText = "WSL 环境",
                    CloseButtonText = "取消",
                    DefaultButton = ContentDialogButton.Primary
                };
                ContentDialogResult envPick = await _dialogService.ShowAsync(envDlg);
                if (envPick == ContentDialogResult.Primary) { await PanelWin.OpenCreateAsync(); return; }
                if (envPick != ContentDialogResult.Secondary)
                {
                    AppendLog("[新建] 已取消。");
                    return;
                }

                // 4) WSL：单发行版直开；多发行版先选目标发行版
                if (distros.Count == 1) { await PanelWsl.OpenCreateAsync(distros[0]); return; }

                var cmb = new ComboBox { MinWidth = 240, PlaceholderText = "选择发行版" };
                foreach (string d in distros) cmb.Items.Add(d);
                cmb.SelectedIndex = 0;
                AutomationProperties.SetAutomationId(cmb, "RailDistroCombo");
                var distroDlg = new ContentDialog
                {
                    Title = "选择 WSL 发行版",
                    Content = cmb,
                    PrimaryButtonText = "确认",
                    CloseButtonText = "取消",
                    DefaultButton = ContentDialogButton.Primary
                };
                if (await _dialogService.ShowAsync(distroDlg) == ContentDialogResult.Primary
                    && cmb.SelectedItem is string sel)
                {
                    await PanelWsl.OpenCreateAsync(sel);
                }
            }
            catch (Exception ex)
            {
                // 理由: 本方法经 fire-and-forget 事件接线调用（_ = ...），异常必须就地承接，避免未观察任务异常
                AppendLog("[新建] 打开新建向导失败: " + ex.Message);
            }
        }

        /// <summary>左栏「扫描」：详情主区当前在 WSL 子页就扫 WSL，否则扫 Windows。</summary>
        private async Task OnRailScanAsync()
        {
            try
            {
                if (PanelWsl.Visibility == Microsoft.UI.Xaml.Visibility.Visible) await PanelWsl.RunScanAsync();
                else await PanelWin.RunScanAsync();
            }
            catch (Exception ex)
            {
                // 理由: fire-and-forget 调用，扫描失败就地记日志，不影响界面与后续操作
                AppendLog("[扫描] 失败: " + ex.Message);
            }
        }

        // ==================== 关闭 ====================

        private void OnWindowClosed(object sender, WindowEventArgs args)
        {
            _closing = true;
            try { if (_logFlushTimer != null) _logFlushTimer.Stop(); } catch { /* 理由: 日志定时器可能在关闭流程中已停止，再 Stop 抛错视为已停止，不阻塞销毁 */ }
            try { PanelWin.Shutdown(); } catch { /* 理由: 关闭阶段面板清理尽力而为，失败不影响其余退出清理步骤 */ }
            try { PanelWsl.Shutdown(); } catch { /* 理由: 同上，WSL 面板清理失败不阻断后续退出步骤 */ }
            try { PanelMarket.Shutdown(); } catch { /* 理由: 市场面板可能从未初始化，Shutdown 失败视为无需清理，继续退出 */ }
            try { PanelPlugins.Shutdown(); } catch { /* 理由: 插件管理面板可能从未加载，Shutdown 失败无副作用，继续退出清理 */ }
            try { PluginInstaller.KillAll(); } catch { /* 理由: 终止遗留插件命令进程尽力而为，失败可能残留命令，不影响主退出流程 */ }   // 终止仍在进行的插件命令（Windows 侧）
            try { _instanceMgr.DisposeAll(); } catch { /* 理由: 实例管理器释放为退出收尾，失败不阻断窗口关闭，资源由进程退出兜底回收 */ }
        }
    }
}
