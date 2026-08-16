// ============================================================================
//  MainWindow — 主窗口交互（code-behind，无 MVVM；UI 状态由 BackendManager 事件驱动）
//
//  关键交互：
//    ▶ 启动 / ⟳ 重启（不拉浏览器，R4）/ ⏹ 停止（外部实例先确认）/ 🌐 打开界面
//    设置（含错误报告目录，R3）、明暗主题三态切换、日志批量刷新 + 截断
// ============================================================================

using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DshController.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using Windows.UI;

namespace DshController
{
    public sealed partial class MainWindow : Window
    {
        private const int LogMaxChars = 200000;

        private readonly Config _cfg;
        private readonly BackendManager _backend;
        private readonly DispatcherQueueTimer _timer;
        private readonly SemaphoreSlim _probeGate = new SemaphoreSlim(1, 1);

        private bool _autoScroll = true;
        private bool _closing;
        private bool _closeCleanupDone;
        private AppTheme _theme;
        private int _externalPidCache;     // 外部实例 PID 展示缓存
        private BackendState _uiState = BackendState.Stopped;
        private bool _uiMine;

        public MainWindow(Config cfg)
        {
            InitializeComponent();
            _cfg = cfg;
            _theme = cfg.Theme;
            ApplyTheme(_theme);

            // 窗口外观：尺寸 + 图标（Assets/app.ico 已随构建复制到输出目录）
            try
            {
                AppWindow.ResizeClient(new Windows.Graphics.SizeInt32(720, 700));
                string ico = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
                if (File.Exists(ico)) AppWindow.SetIcon(ico);
            }
            catch { }
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);

            // 后端管理器 + 事件接线
            _backend = new BackendManager(DispatcherQueue.GetForCurrentThread());
            _backend.Log += (s, line) => AppendLog(line);
            _backend.OutputBatched += (s, e) => AppendOutput(e.Lines);
            _backend.StateChanged += (s, e) => UpdateUiState(e.State, e.Mine, e.Pid);
            _backend.Ready += OnBackendReady;
            _backend.StartFailed += OnStartFailed;
            _backend.AnnouncedUrlChanged += (s, url) => UpdateFooter();

            // 初始化控件值
            TxtHost.Text = _cfg.Host;
            TxtPort.Text = _cfg.Port.ToString();
            TxtWorkspace.Text = _cfg.Workspace;
            TxtReportDir.Text = _cfg.ErrorReportDir;
            SwAutoOpen.IsOn = _cfg.AutoOpenBrowser;
            SwStopOnExit.IsOn = _cfg.StopOnExit;
            UrlLink.Content = PortTools.Url(_cfg.Host, _cfg.Port);
            UrlLink.NavigateUri = new Uri(PortTools.Url(_cfg.Host, _cfg.Port));

            // 状态轮询（1s；后台探测，UI 零阻塞）
            _timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
            _timer.Interval = TimeSpan.FromSeconds(1);
            _timer.Tick += async (s, e) => await ProbeTickAsync();
            _timer.Start();

            Closed += OnWindowClosed;
            // 关闭前清理：AppWindow.Closing 无 deferral（WASDK 1.5），用 取消+重关 模式，
            // 保证 stopOnExit 的进程树清理完成后窗口才真正销毁
            AppWindow.Closing += async (s, e) =>
            {
                if (_closeCleanupDone) return;
                _closeCleanupDone = true;
                e.Cancel = true;
                _closing = true;
                _timer.Stop();
                TryReadSettings(showErrors: false); // 静默保存（修 legacy D10：关窗不再弹无效警告）
                SaveSettingsSilently();
                if (_cfg.StopOnExit && _backend.State == BackendState.Running && _backend.IsMine)
                {
                    try
                    {
                        var t = _backend.StopAsync(_cfg, killExternal: false);
                        await Task.WhenAny(t, Task.Delay(15000));
                    }
                    catch { }
                }
                Close(); // _closeCleanupDone 已置位，本次不再拦截
            };

            AppendLog("DshController 已启动（v" + ErrorReporter.AppVersion + "）。");
            AppendLog("dsh 命令: " + _backend.DescribeDsh(_cfg));
            AppendLog("错误报告目录: " + _cfg.EffectiveErrorReportDir);
            UpdateUiState(BackendState.Stopped, false, 0);
            _ = ProbeTickAsync();
        }

        // ==================== 主题 ====================

        private void BtnTheme_Click(object sender, RoutedEventArgs e)
        {
            // 三态循环：跟随系统 → 浅色 → 深色
            _theme = _theme == AppTheme.System ? AppTheme.Light
                   : _theme == AppTheme.Light ? AppTheme.Dark
                   : AppTheme.System;
            ApplyTheme(_theme);
            _cfg.Theme = _theme;
            SaveSettingsSilently();
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
                // 注册表判断系统应用模式（未设置时按浅色处理）
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

        // ==================== 状态刷新 ====================

        private async Task ProbeTickAsync()
        {
            if (_closing || !_probeGate.Wait(0)) return;
            try
            {
                // 忙态（Starting/Stopping/Restarting）由状态机事件驱动，跳过探测
                BackendState s = _backend.State;
                if (s == BackendState.Starting || s == BackendState.Stopping || s == BackendState.Restarting)
                    return;

                bool up = await PortTools.ProbeAsync(_cfg.Host, _cfg.Port);
                int pid = 0;
                bool mine = _backend.IsMine && up;
                if (up)
                {
                    pid = mine ? _backend.ChildPid : await PortTools.FindListenerPidAsync(_cfg.Port);
                    _externalPidCache = pid;
                }
                else _externalPidCache = 0;

                var newState = up ? BackendState.Running : BackendState.Stopped;
                DispatcherQueue.TryEnqueue(() => UpdateUiState(newState, mine, pid));
            }
            catch { }
            finally { _probeGate.Release(); }
        }

        private void UpdateUiState(BackendState state, bool mine, int pid)
        {
            if (_closing) return;
            _uiState = state; _uiMine = mine;

            string label;
            Brush dotBrush;
            switch (state)
            {
                case BackendState.Starting:
                    label = "启动中…"; dotBrush = StateBrush("StateStartingColor"); break;
                case BackendState.Stopping:
                    label = "正在停止…"; dotBrush = StateBrush("StateStartingColor"); break;
                case BackendState.Restarting:
                    label = "重启中（不打开浏览器）…"; dotBrush = StateBrush("StateStartingColor"); break;
                case BackendState.Running:
                    label = mine ? "运行中 · 本程序启动" : "运行中 · 外部进程";
                    dotBrush = StateBrush("StateRunColor"); break;
                default:
                    label = "已停止"; dotBrush = StateBrush("StateStopColor"); break;
            }
            StatusText.Text = label;
            StatusDot.Background = dotBrush;

            if (state == BackendState.Running && !mine && pid == 0) pid = _externalPidCache;
            PidText.Text = state == BackendState.Running ? (mine ? "本程序 " + pid : (pid > 0 ? "外部 " + pid : "外部")) : "—";

            bool busy = state == BackendState.Starting || state == BackendState.Stopping || state == BackendState.Restarting;
            BtnStart.IsEnabled = state == BackendState.Stopped;
            BtnRestart.IsEnabled = state == BackendState.Running;
            BtnStop.IsEnabled = state == BackendState.Running || state == BackendState.Starting; // 启动中可取消（修 legacy D9）
            BtnOpen.IsEnabled = true;
            UpdateFooter();
        }

        /// <summary>按当前生效主题从 ThemeDictionaries 取色构造刷子（程序化取值不吃 ThemeResource 解析）。</summary>
        private Brush StateBrush(string colorKey)
        {
            try
            {
                string key = Root.ActualTheme == ElementTheme.Dark ? "Dark" : "Light";
                foreach (ResourceDictionary md in Application.Current.Resources.MergedDictionaries)
                {
                    if (md.ThemeDictionaries.ContainsKey(key))
                    {
                        var td = md.ThemeDictionaries[key] as ResourceDictionary;
                        if (td != null && td.ContainsKey(colorKey))
                        {
                            var c = (Windows.UI.Color)td[colorKey];
                            return new SolidColorBrush(c);
                        }
                    }
                }
            }
            catch { }
            return new SolidColorBrush(Windows.UI.Color.FromArgb(255, 129, 133, 140));
        }

        private void UpdateFooter()
        {
            string url = PortTools.Url(_cfg.Host, _cfg.Port);
            string announced = _backend == null ? "" : _backend.AnnouncedUrl;
            FooterText.Text = "dsh: " + _backend.DescribeDsh(_cfg) +
                " · v" + ErrorReporter.AppVersion +
                (string.IsNullOrEmpty(announced) || announced == url ? "" : " · 公告: " + announced);
        }

        // ==================== 操作 ====================

        private async void BtnStart_Click(object sender, RoutedEventArgs e)
        {
            if (!TryReadSettings(showErrors: true)) return;
            SaveSettingsSilently();
            await RunOpAsync(() => _backend.StartAsync(_cfg));
        }

        private async void BtnRestart_Click(object sender, RoutedEventArgs e)
        {
            if (_uiState != BackendState.Running) return;
            if (!TryReadSettings(showErrors: true)) return;

            bool mine = _uiMine;
            if (!mine)
            {
                // 外部实例：结束前确认（绝不静默误杀）
                int pid = _externalPidCache;
                bool ok = await ConfirmAsync(
                    "检测到后端由外部进程" + (pid > 0 ? "（PID " + pid + "）" : "") + "提供。\n\n" +
                    "重启将结束该进程并由本程序重新启动后端。\n浏览器不会自动打开；现有页面刷新即可重连。是否继续？",
                    "确认重启外部后端");
                if (!ok) { AppendLog("已取消重启外部后端。"); return; }
            }
            SaveSettingsSilently();
            await RunOpAsync(() => _backend.RestartAsync(_cfg));
        }

        private async void BtnStop_Click(object sender, RoutedEventArgs e)
        {
            if (_uiState != BackendState.Running && _uiState != BackendState.Starting) return;

            bool mine = _uiMine;
            bool up = await PortTools.ProbeAsync(_cfg.Host, _cfg.Port);
            bool killExternal = false;
            if (!mine && up)
            {
                int pid = _externalPidCache;
                killExternal = await ConfirmAsync(
                    "检测到后端由外部进程" + (pid > 0 ? "（PID " + pid + "）" : "") + "提供。\n\n是否结束该进程及其子进程来停止后端？",
                    "确认停止外部后端");
                if (!killExternal) { AppendLog("已取消停止外部进程。"); return; }
            }
            SaveSettingsSilently();
            await RunOpAsync(() => _backend.StopAsync(_cfg, killExternal));
        }

        private void BtnOpen_Click(object sender, RoutedEventArgs e)
        {
            OpenBrowser(PortTools.Url(_cfg.Host, _cfg.Port));
        }

        private async Task RunOpAsync(Func<Task<bool>> op)
        {
            try
            {
                UpdateUiStateBusySafe();
                await op();
            }
            catch (Exception ex)
            {
                AppendLog("操作异常: " + ex.Message);
                try { ErrorReporter.WriteCrash(ex, "op", _cfg); } catch { }
            }
        }

        private void UpdateUiStateBusySafe()
        {
            BtnStart.IsEnabled = false;
            BtnRestart.IsEnabled = false;
            BtnStop.IsEnabled = false;
        }

        private void OnBackendReady(object sender, ReadyEventArgs e)
        {
            if (_closing) return;
            if (e.SuppressAutoOpen)
            {
                AppendLog("后端已就绪（重启路径：未打开浏览器）。浏览器中的旧页面刷新即可重连。");
            }
            else if (SwAutoOpen.IsOn)
            {
                OpenBrowser(e.Url);
            }
        }

        private void OpenBrowser(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                AppendLog("已在默认浏览器打开: " + url);
            }
            catch (Exception ex)
            {
                AppendLog("打开浏览器失败: " + ex.Message);
            }
        }

        // ==================== 失败报告（R3） ====================

        private async void OnStartFailed(object sender, StartFailureContext ctx)
        {
            if (_closing) return;
            string path = null;
            try { path = ErrorReporter.WriteStartFailure(ctx); } catch { }
            if (path != null)
            {
                AppendLog("已生成失败报告: " + path);
                await ShowReportDialogAsync(path, ctx.FailureKind);
            }
            else
            {
                AppendLog("失败报告写入失败！目录: " + _cfg.EffectiveErrorReportDir);
            }
        }

        public void NotifyCrash(string reportPath)
        {
            AppendLog("发生未处理异常" + (reportPath != null ? "，崩溃报告: " + reportPath : "（报告写入失败）"));
        }

        private async Task ShowReportDialogAsync(string path, string kind)
        {
            try
            {
                var dlg = new ContentDialog
                {
                    Title = "启动失败：" + kind,
                    Content = "已生成详细错误报告：\n" + path + "\n\n是否打开查看？",
                    PrimaryButtonText = "打开报告",
                    SecondaryButtonText = "打开目录",
                    CloseButtonText = "关闭",
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = Root.XamlRoot
                };
                var r = await dlg.ShowAsync();
                if (r == ContentDialogResult.Primary)
                    Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                else if (r == ContentDialogResult.Secondary)
                    Process.Start("explorer.exe", "/select,\"" + path + "\"");
            }
            catch (Exception ex)
            {
                AppendLog("报告对话框失败: " + ex.Message);
            }
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
                    XamlRoot = Root.XamlRoot
                };
                return await dlg.ShowAsync() == ContentDialogResult.Primary;
            }
            catch
            {
                return false;
            }
        }

        // ==================== 设置读写 ====================

        private bool TryReadSettings(bool showErrors)
        {
            int port;
            if (!int.TryParse(TxtPort.Text.Trim(), out port) || port < 1 || port > 65535)
            {
                if (showErrors) AppendLog("端口无效（需 1–65535），未保存设置。");
                return false;
            }
            string host = TxtHost.Text.Trim();
            if (string.IsNullOrEmpty(host))
            {
                if (showErrors) AppendLog("主机不能为空，未保存设置。");
                return false;
            }
            _cfg.Host = host;
            _cfg.Port = port;
            _cfg.Workspace = string.IsNullOrWhiteSpace(TxtWorkspace.Text.Trim())
                ? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
                : TxtWorkspace.Text.Trim();
            _cfg.ErrorReportDir = TxtReportDir.Text.Trim();
            _cfg.AutoOpenBrowser = SwAutoOpen.IsOn;
            _cfg.StopOnExit = SwStopOnExit.IsOn;
            _cfg.Theme = _theme;
            UrlLink.Content = PortTools.Url(_cfg.Host, _cfg.Port);
            try { UrlLink.NavigateUri = new Uri(PortTools.Url(_cfg.Host, _cfg.Port)); } catch { }
            return true;
        }

        private void SaveSettingsSilently()
        {
            try { _cfg.Save(); } catch { }
        }

        private async void BtnBrowseWs_Click(object sender, RoutedEventArgs e)
        {
            string dir = await PickFolderAsync("选择 dsh 工作目录（默认 workspace 根目录）");
            if (dir != null) TxtWorkspace.Text = dir;
        }

        private async void BtnBrowseRd_Click(object sender, RoutedEventArgs e)
        {
            string dir = await PickFolderAsync("选择错误报告保存目录");
            if (dir != null) TxtReportDir.Text = dir;
        }

        private async Task<string> PickFolderAsync(string title)
        {
            try
            {
                var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
                picker.FileTypeFilter.Add("*");
                // unpackaged 窗口必须显式关联 HWND
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

        private void BtnCopyUrl_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dp = new DataPackage();
                dp.SetText(UrlLink.Content as string ?? "");
                Clipboard.SetContent(dp);
                AppendLog("已复制地址到剪贴板。");
            }
            catch (Exception ex)
            {
                AppendLog("复制失败: " + ex.Message);
            }
        }

        // ==================== 日志 ====================

        private void AppendOutput(string[] lines)
        {
            if (lines == null || lines.Length == 0 || _closing) return;
            var sb = new StringBuilder();
            DateTime now = DateTime.Now;
            string ts = now.ToString("HH:mm:ss");
            foreach (string l in lines) sb.Append(ts).Append("  ").AppendLine(l);
            AppendText(sb.ToString());
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
            int n = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(start);
            for (int i = 0; i < n; i++)
            {
                var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(start, i);
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

        private void BtnAutoScroll_Click(object sender, RoutedEventArgs e)
        {
            _autoScroll = !_autoScroll;
            BtnAutoScroll.Content = _autoScroll ? "滚动：开" : "滚动：关";
        }

        // ==================== 关闭 ====================

        private void OnWindowClosed(object sender, WindowEventArgs args)
        {
            _closing = true;
            _timer.Stop();
            try { _backend.Dispose(); } catch { }
        }
    }
}
