// ============================================================================
//  MainWindow — 主窗口交互（v0.5.1 侧边栏布局，code-behind，无 MVVM）
//
//  结构：
//    - NavigationView 侧边栏三个页面：Windows 实例 / WSL 实例 / 全局设置；
//      PanelWin / PanelWsl（InstancePanel）常驻不销毁，切换页面只改可见性，
//      实例选择、状态轮询与操作不中断；全局设置从 Expander 移入独立页面；
//    - 本窗口负责：标题栏/主题、侧边栏导航、全局设置页、共享控制台坞
//      （可收起）、状态栏、关闭清理；
//    - 控制台为全局共享：两个面板的所有后端日志经回调汇入，带
//      [WIN·名称] / [WSL·名称] 前缀；
//    - 启动失败报告由核心层（BackendManager.FailStart → ErrorReporter）
//      生成到用户指定的报告目录，面板负责弹窗提示，本窗口提供
//      "打开报告目录"快捷按钮。
// ============================================================================

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DshController.Core;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Pickers;

namespace DshController
{
    public sealed partial class MainWindow : Window
    {
        private const int LogMaxChars = 200000;

        private readonly InstanceRegistry _registry;
        private readonly InstanceManager _instanceMgr;
        private bool _autoScroll = true;
        private bool _closing;
        private bool _closeCleanupDone;
        private AppTheme _theme;
        private bool _consoleVisible = true;   // 控制台坞展开状态

        public MainWindow(InstanceRegistry registry)
        {
            InitializeComponent();
            _registry = registry;
            _registry.Settings.Theme = NormalizeTheme(_registry.Settings.Theme);
            _theme = _registry.Settings.Theme;
            ApplyTheme(_theme);

            // 窗口外观：默认尺寸（v0.5.1：侧边栏布局下加宽，高可略低）+ 最小尺寸防遮挡 + 图标
            try
            {
                AppWindow.ResizeClient(new Windows.Graphics.SizeInt32(1180, 800));
                if (AppWindow.Presenter is OverlappedPresenter op)
                {
                    op.PreferredMinimumWidth = 960;
                    op.PreferredMinimumHeight = 620;
                }
                string ico = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
                if (File.Exists(ico)) AppWindow.SetIcon(ico);
            }
            catch { }
            // 主题按钮精确避开系统标题按钮（Win11 compact overlay 返回实际内边距，兜底 150px）
            try
            {
                var tb = AppWindow.TitleBar;
                if (tb != null && tb.RightInset > 0)
                    BtnTheme.Margin = new Thickness(0, 0, tb.RightInset + 12, 0);
            }
            catch { }
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);

            // 多实例管理器：所有实例（Windows + WSL）共享同一个 InstanceManager，
            // 面板只按运行环境过滤实例列表。
            _instanceMgr = new InstanceManager(Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread(), _registry);

            // 两个独立面板（各自持有实例列表 / 状态 / 设置）
            PanelWin.Init(_registry, _instanceMgr, "windows", AppendLog, UpdateFooter);
            PanelWsl.Init(_registry, _instanceMgr, "wsl", AppendLog, UpdateFooter);

            LoadGlobalSettings();

            // 侧边栏默认选中 Windows 实例页（触发 Nav_SelectionChanged → 页面可见性）
            Nav.SelectedItem = NavWin;

            Closed += OnWindowClosed;
            // 关闭前清理：AppWindow.Closing 无 deferral（WASDK 1.5），用 取消+重关 模式，
            // 保证 stopOnExit 的进程树清理完成后窗口才真正销毁
            AppWindow.Closing += async (s, e) =>
            {
                if (_closeCleanupDone) return;
                _closeCleanupDone = true;
                e.Cancel = true;
                _closing = true;
                try { PanelWin.SilentSave(); } catch { }
                try { PanelWsl.SilentSave(); } catch { }
                try
                {
                    var t = _instanceMgr.StopAllOnExitAsync();
                    await Task.WhenAny(t, Task.Delay(15000));
                }
                catch { }
                _registry.Save();
                Close(); // _closeCleanupDone 已置位，本次不再拦截
            };

            AppendLog("DshController 已启动（v" + ErrorReporter.AppVersion + "）。" +
                      "Windows 与 WSL 实例分别在侧边栏两个页面管理，控制台可点击「隐藏」收起。");
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
            try { _registry.Save(); } catch { }
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
            catch { }
            return false;
        }

        // ==================== 侧边栏导航 ====================

        private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
        {
            string tag = (args.SelectedItemContainer?.Tag as string) ?? "win";
            ShowPage(tag);
        }

        /// <summary>按侧边栏标签切换页面可见性（面板常驻不销毁，仅隐藏/显示）。</summary>
        private void ShowPage(string tag)
        {
            PanelWin.Visibility = tag == "win" ? Visibility.Visible : Visibility.Collapsed;
            PanelWsl.Visibility = tag == "wsl" ? Visibility.Visible : Visibility.Collapsed;
            PageSettings.Visibility = tag == "settings" ? Visibility.Visible : Visibility.Collapsed;
        }

        // ==================== 控制台坞 ====================

        private void BtnToggleConsole_Click(object sender, RoutedEventArgs e)
        {
            _consoleVisible = !_consoleVisible;
            TxtLog.Visibility = _consoleVisible ? Visibility.Visible : Visibility.Collapsed;
            BtnToggleConsole.Content = _consoleVisible ? "隐藏" : "显示";
            if (_consoleVisible) ScrollLogToEnd();
        }

        // ==================== 全局设置 ====================

        private void LoadGlobalSettings()
        {
            TxtReportDir.Text = _registry.Settings.ErrorReportDir;
            TxtHomeRoot.Text = _registry.Settings.HomeRoot;
            TxtNewWs.Text = _registry.Settings.NewInstanceWorkspace;
            TxtDshCommand.Text = _registry.Settings.DshCommand;
        }

        private void BtnSaveGlobal_Click(object sender, RoutedEventArgs e)
        {
            _registry.Settings.ErrorReportDir = TxtReportDir.Text.Trim();
            _registry.Settings.HomeRoot = TxtHomeRoot.Text.Trim();
            _registry.Settings.NewInstanceWorkspace = TxtNewWs.Text.Trim();
            _registry.Settings.DshCommand = TxtDshCommand.Text.Trim();
            _registry.Settings.Theme = _theme;
            try { _registry.Save(); } catch { }
            Nav.SelectedItem = NavWin;
            AppendLog("全局设置已保存（报告目录: " +
                (string.IsNullOrEmpty(_registry.Settings.ErrorReportDir)
                    ? "默认" : _registry.Settings.ErrorReportDir) + "）");
            UpdateFooter();
        }

        private void BtnCancelGlobal_Click(object sender, RoutedEventArgs e)
        {
            LoadGlobalSettings();
            Nav.SelectedItem = NavWin;
            AppendLog("全局设置已取消");
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
            string dir = await PickFolderAsync("选择新建实例默认工作区目录");
            if (dir != null) TxtNewWs.Text = dir;
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
            AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + line + Environment.NewLine);
        }

        private void AppendText(string text)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    TxtLog.Text += text;
                    if (TxtLog.Text.Length > LogMaxChars)
                        TxtLog.Text = TxtLog.Text.Substring(TxtLog.Text.Length - 150000);
                    if (_autoScroll) ScrollLogToEnd();
                }
                catch { }
            });
        }

        /// <summary>把日志区滚动到底部（TextBox 无 ScrollToEnd，找内嵌 ScrollViewer 调 ChangeView）。</summary>
        private void ScrollLogToEnd()
        {
            var sv = FindDescendant<ScrollViewer>(TxtLog);
            if (sv != null) sv.ChangeView(null, sv.ScrollableHeight, null, disableAnimation: true);
        }

        private static T FindDescendant<T>(DependencyObject start) where T : class
        {
            int n = VisualTreeHelper.GetChildrenCount(start);
            for (int i = 0; i < n; i++)
            {
                var child = VisualTreeHelper.GetChild(start, i);
                if (child is T found) return found;
                var deeper = FindDescendant<T>(child);
                if (deeper != null) return deeper;
            }
            return null;
        }

        private void BtnClearLog_Click(object sender, RoutedEventArgs e)
        {
            TxtLog.Text = "";
        }

        private void BtnCopyLog_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (string.IsNullOrEmpty(TxtLog.Text))
                {
                    AppendLog("控制台为空，没有可复制的日志。");
                    return;
                }
                var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
                dp.SetText(TxtLog.Text);
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
            int win = _registry.Instances.Count(d => !d.IsWsl);
            int wsl = _registry.Instances.Count(d => d.IsWsl);
            string report = string.IsNullOrEmpty(_registry.Settings.ErrorReportDir)
                ? "默认目录" : _registry.Settings.ErrorReportDir;

            // 各环境检测到的 harness 主实例版本（面板异步探测后回调刷新）
            string vWin = "", vWsl = "";
            try { vWin = PanelWin.DetectedVersion ?? ""; } catch { }
            try { vWsl = PanelWsl.DetectedVersion ?? ""; } catch { }
            string vers = "";
            if (vWin.Length > 0) vers += " · WIN harness v" + vWin;
            if (vWsl.Length > 0) vers += " · WSL harness v" + vWsl;

            FooterText.Text = "Windows 实例 " + win + " 个 · WSL 实例 " + wsl + " 个" + vers +
                " · 报告目录: " + report + " · v" + ErrorReporter.AppVersion;

            // 侧边栏导航项实时显示各环境实例数与运行数（面板状态已与端口探测同步）
            int winRun = 0, wslRun = 0;
            try { winRun = PanelWin.RunningCount(); } catch { }
            try { wslRun = PanelWsl.RunningCount(); } catch { }
            NavWin.Content = "Windows 实例" + (win > 0 ? " · " + win + " 个" : "") +
                (winRun > 0 ? " · " + winRun + " 运行中" : "");
            NavWsl.Content = "WSL 实例" + (wsl > 0 ? " · " + wsl + " 个" : "") +
                (wslRun > 0 ? " · " + wslRun + " 运行中" : "");
        }

        // ==================== 关闭 ====================

        private void OnWindowClosed(object sender, WindowEventArgs args)
        {
            _closing = true;
            try { PanelWin.Shutdown(); } catch { }
            try { PanelWsl.Shutdown(); } catch { }
            try { _instanceMgr.DisposeAll(); } catch { }
        }
    }
}
