// ============================================================================
//  MainWindow — 主窗口交互（code-behind，无 MVVM；UI 状态由 InstanceManager 事件驱动）
//
//  关键交互：
//    ▶ 实例选择器（标题栏）/ 启动 / 重启（不拉浏览器，R4）/ 停止 / 打开界面
//    实例管理：新建空白实例、克隆现有实例或 ~/.dsh、删除实例
//    实例设置（选中实例）与全局设置（registry.Settings）、日志按实例切换
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
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;

namespace DshController
{
    public sealed partial class MainWindow : Window
    {
        private const int LogMaxChars = 200000;
        private const int RecentLogLines = 200;

        private readonly InstanceRegistry _registry;
        private readonly InstanceManager _instanceMgr;
        private readonly HomeManager _homeMgr = new HomeManager();
        private readonly DispatcherQueueTimer _timer;
        private readonly SemaphoreSlim _probeGate = new SemaphoreSlim(1, 1);
        private bool _loadingRegistryUI;

        private string _selectedId = "";
        private bool _autoScroll = true;
        private bool _closing;
        private bool _closeCleanupDone;
        private AppTheme _theme;
        private int _externalPidCache;     // 外部实例 PID 展示缓存
        private BackendState _uiState = BackendState.Stopped;
        private bool _uiMine;

        public MainWindow(InstanceRegistry registry)
        {
            InitializeComponent();
            _registry = registry;
            _registry.Settings.Theme = NormalizeTheme(_registry.Settings.Theme);
            _theme = _registry.Settings.Theme;
            ApplyTheme(_theme);

            // 窗口外观：尺寸 + 图标（Assets/app.ico 已随构建复制到输出目录）
            try
            {
                AppWindow.ResizeClient(new Windows.Graphics.SizeInt32(860, 760));
                string ico = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
                if (File.Exists(ico)) AppWindow.SetIcon(ico);
            }
            catch { }
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);

            // 多实例管理器 + 事件接线（事件 handler 内按选中实例过滤 sender）
            _instanceMgr = new InstanceManager(DispatcherQueue.GetForCurrentThread(), _registry);
            WireInstanceEvents();

            // 初始化实例选择器
            RefreshInstanceList();
            _selectedId = _registry.Instances.FirstOrDefault()?.Id ?? "";
            SelectInstance(_selectedId, refreshLog: false);

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
                SaveAllSettings();
                if (!string.IsNullOrEmpty(_selectedId))
                {
                    try
                    {
                        var t = _instanceMgr.StopAllOnExitAsync();
                        await Task.WhenAny(t, Task.Delay(15000));
                    }
                    catch { }
                }
                _registry.Save();
                Close(); // _closeCleanupDone 已置位，本次不再拦截
            };

            AppendLog("DshController 已启动（v" + ErrorReporter.AppVersion + "）。");
            RefreshSelectedControls();
            UpdateUiState(BackendState.Stopped, false, 0);
            _ = ProbeTickAsync();
        }

        private void WireInstanceEvents()
        {
            foreach (InstanceDef def in _registry.Instances)
            {
                var mgr = _instanceMgr.For(def.Id);
                mgr.Log += (s, line) => OnManagerLog(s, line);
                mgr.OutputBatched += (s, e) => { if (IsSelectedManager(s)) AppendOutput(e.Lines); };
                mgr.StateChanged += (s, e) => { if (IsSelectedManager(s)) UpdateUiState(e.State, e.Mine, e.Pid); };
                mgr.Ready += OnBackendReady;
                mgr.StartFailed += OnStartFailed;
                mgr.AnnouncedUrlChanged += (s, url) => { if (IsSelectedManager(s)) UpdateFooter(); };
            }
        }

        private bool IsSelectedManager(object sender)
        {
            if (_closing || string.IsNullOrEmpty(_selectedId)) return false;
            return ReferenceEquals(sender, _instanceMgr.For(_selectedId));
        }

        private InstanceDef SelectedDef()
        {
            if (!_registry.TryGet(_selectedId, out InstanceDef def))
                return null;
            return def;
        }

        private AppTheme NormalizeTheme(AppTheme theme)
        {
            return theme == AppTheme.System || theme == AppTheme.Light || theme == AppTheme.Dark
                ? theme
                : AppTheme.System;
        }

        // ==================== 实例选择器 ====================

        private void RefreshInstanceList()
        {
            _loadingRegistryUI = true;
            try
            {
                CmbInstance.ItemsSource = _registry.Instances.ToList();
            }
            finally { _loadingRegistryUI = false; }
        }

        private void CmbInstance_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loadingRegistryUI) return;
            if (CmbInstance.SelectedItem is InstanceDef def && def.Id != _selectedId)
            {
                SelectInstance(def.Id, refreshLog: true);
            }
        }

        private void SelectInstance(string id, bool refreshLog)
        {
            if (string.IsNullOrEmpty(id) || !_registry.TryGet(id, out _))
            {
                _selectedId = "";
            }
            else
            {
                _selectedId = id;
            }

            InstanceDef def = SelectedDef();
            if (def != null)
            {
                _loadingRegistryUI = true;
                CmbInstance.SelectedItem = def;
                _loadingRegistryUI = false;
            }

            if (refreshLog)
            {
                TxtLog.Text = "";
                BackendManager mgr = string.IsNullOrEmpty(_selectedId) ? null : _instanceMgr.For(_selectedId);
                if (mgr != null)
                {
                    AppendLog("已切换到实例: " + def?.Name + "（" + _selectedId + "）");
                    foreach (string line in mgr.RecentOutput(RecentLogLines))
                        AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + line + Environment.NewLine);
                }
            }

            RefreshSelectedControls();
            ProbeAsyncAfterSwitch();
        }

        private async void ProbeAsyncAfterSwitch()
        {
            try { await ProbeTickAsync(); }
            catch { }
        }

        private void RefreshSelectedControls()
        {
            InstanceDef def = SelectedDef();
            if (def == null)
            {
                TxtHost.Text = "";
                TxtPort.Text = "";
                TxtWorkspace.Text = "";
                TxtHome.Text = "";
                TxtTrustedHosts.Text = "";
                SwAutoOpen.IsOn = false;
                SwStopOnExit.IsOn = false;
                UrlLink.Content = "—";
                UrlLink.NavigateUri = null;
                HomeText.Text = "DSH_HOME: —";
                return;
            }

            TxtHost.Text = def.Host;
            TxtPort.Text = def.Port.ToString();
            TxtWorkspace.Text = def.Workspace;
            TxtHome.Text = def.Home ?? "";
            TxtTrustedHosts.Text = def.TrustedHosts == null ? "" : string.Join(", ", def.TrustedHosts);
            SwAutoOpen.IsOn = def.AutoOpenBrowser;
            SwStopOnExit.IsOn = def.StopOnExit;
            TxtReportDir.Text = _registry.Settings.ErrorReportDir;
            TxtHomeRoot.Text = _registry.Settings.HomeRoot;
            TxtDshCommand.Text = _registry.Settings.DshCommand;
            UpdateHomeLabel(def.Home);
            UpdateUrlAndFooter(def);
            UpdateUiState(CurrentStateFor(def), CurrentMineFor(def), CurrentPidFor(def));
        }

        private void UpdateHomeLabel(string home)
        {
            if (string.IsNullOrWhiteSpace(home))
                HomeText.Text = "DSH_HOME: ~/.dsh(默认,不注入)";
            else
                HomeText.Text = "DSH_HOME: " + home.Trim();
        }

        private BackendState CurrentStateFor(InstanceDef def)
        {
            return _instanceMgr.For(def.Id).State;
        }

        private bool CurrentMineFor(InstanceDef def)
        {
            return _instanceMgr.For(def.Id).IsMine;
        }

        private int CurrentPidFor(InstanceDef def)
        {
            return _instanceMgr.For(def.Id).ChildPid;
        }

        private void UpdateUrlAndFooter(InstanceDef def)
        {
            string url = PortTools.Url(def.Host, def.Port);
            UrlLink.Content = url;
            try { UrlLink.NavigateUri = new Uri(url); } catch { }
            UpdateFooter();
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
            SaveAllSettings();
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
                InstanceDef def = SelectedDef();
                if (def == null) return;

                // 忙态（Starting/Stopping/Restarting）由状态机事件驱动，跳过探测
                BackendState s = _instanceMgr.For(def.Id).State;
                if (s == BackendState.Starting || s == BackendState.Stopping || s == BackendState.Restarting)
                    return;

                bool up = await PortTools.ProbeAsync(def.Host, def.Port);
                int pid = 0;
                BackendManager mgr = _instanceMgr.For(def.Id);
                bool mine = mgr.IsMine && up;
                if (up)
                {
                    pid = mine ? mgr.ChildPid : await PortTools.FindListenerPidAsync(def.Port);
                    _externalPidCache = pid;
                }
                else _externalPidCache = 0;

                var newState = up ? BackendState.Running : BackendState.Stopped;
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (ReferenceEquals(mgr, _instanceMgr.For(_selectedId)))
                        UpdateUiState(newState, mine, pid);
                });
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
            if (SelectedDef() == null)
            {
                label = "无实例，请新建";
                dotBrush = StateBrush("StateStopColor");
            }
            StatusText.Text = label;
            StatusDot.Background = dotBrush;

            if (state == BackendState.Running && !mine && pid == 0) pid = _externalPidCache;
            PidText.Text = state == BackendState.Running ? (mine ? "本程序 " + pid : (pid > 0 ? "外部 " + pid : "外部")) : "—";

            bool busy = state == BackendState.Starting || state == BackendState.Stopping || state == BackendState.Restarting;
            bool hasInstance = SelectedDef() != null;
            BtnStart.IsEnabled = hasInstance && state == BackendState.Stopped;
            BtnRestart.IsEnabled = hasInstance && state == BackendState.Running;
            BtnStop.IsEnabled = hasInstance && (state == BackendState.Running || state == BackendState.Starting); // 启动中可取消（修 legacy D9）
            BtnOpen.IsEnabled = hasInstance;
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
            InstanceDef def = SelectedDef();
            if (def == null)
            {
                FooterText.Text = "dsh: — · v" + ErrorReporter.AppVersion;
                return;
            }
            string url = PortTools.Url(def.Host, def.Port);
            BackendManager mgr = _instanceMgr.For(def.Id);
            string announced = mgr.AnnouncedUrl;
            FooterText.Text = "dsh: " + mgr.DescribeDsh(_instanceMgr.ConfigFor(def.Id)) +
                " · v" + ErrorReporter.AppVersion +
                (string.IsNullOrEmpty(announced) || announced == url ? "" : " · 公告: " + announced);
        }

        // ==================== 操作 ====================

        private async void BtnStart_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_selectedId)) return;
            if (!TryReadSettings(showErrors: true)) return;
            SaveAllSettings();
            await RunOpAsync(() => _instanceMgr.StartAsync(_selectedId));
        }

        private async void BtnRestart_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_selectedId) || _uiState != BackendState.Running) return;
            if (!TryReadSettings(showErrors: true)) return;

            bool mine = _uiMine;
            InstanceDef def = SelectedDef();
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
            SaveAllSettings();
            await RunOpAsync(() => _instanceMgr.RestartAsync(_selectedId));
        }

        private async void BtnStop_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_selectedId)) return;
            if (_uiState != BackendState.Running && _uiState != BackendState.Starting) return;

            InstanceDef def = SelectedDef();
            bool mine = _uiMine;
            bool up = await PortTools.ProbeAsync(def.Host, def.Port);
            bool killExternal = false;
            if (!mine && up)
            {
                int pid = _externalPidCache;
                killExternal = await ConfirmAsync(
                    "检测到后端由外部进程" + (pid > 0 ? "（PID " + pid + "）" : "") + "提供。\n\n是否结束该进程及其子进程来停止后端？",
                    "确认停止外部后端");
                if (!killExternal) { AppendLog("已取消停止外部进程。"); return; }
            }
            SaveAllSettings();
            await RunOpAsync(() => _instanceMgr.StopAsync(_selectedId, killExternal));
        }

        private void BtnOpen_Click(object sender, RoutedEventArgs e)
        {
            InstanceDef def = SelectedDef();
            if (def == null) return;
            OpenBrowser(PortTools.Url(def.Host, def.Port));
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
                try
                {
                    var cfg = _instanceMgr.ConfigFor(_selectedId);
                    ErrorReporter.WriteCrash(ex, "op", cfg);
                }
                catch { }
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
            if (_closing || !IsSelectedManager(sender)) return;
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

        // ==================== 失败报告 ====================

        private async void OnStartFailed(object sender, StartFailureContext ctx)
        {
            if (_closing || !IsSelectedManager(sender)) return;
            InstanceDef def = SelectedDef();
            if (def != null)
            {
                ctx.InstanceId = def.Id;
                ctx.InstanceHome = def.Home;
            }
            string path = null;
            try { path = ErrorReporter.WriteStartFailure(ctx); } catch { }
            if (path != null)
            {
                AppendLog("已生成失败报告: " + path);
                await ShowReportDialogAsync(path, ctx.FailureKind);
            }
            else
            {
                AppendLog("失败报告写入失败！目录: " + (def?.ToConfig(_registry.Settings).EffectiveErrorReportDir ?? "（未知）"));
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

        // ==================== 实例管理 ====================

        private async void BtnNew_Click(object sender, RoutedEventArgs e)
        {
            await ShowCreateInstanceDialogAsync(cloneMode: false);
        }

        private async void BtnClone_Click(object sender, RoutedEventArgs e)
        {
            await ShowCreateInstanceDialogAsync(cloneMode: true);
        }

        private async Task ShowCreateInstanceDialogAsync(bool cloneMode)
        {
            try
            {
                int suggested = await PortAllocatorSuggestAsync(3081);
                var txtName = new TextBox { PlaceholderText = "实例名称，如 项目A" };
                var txtPort = new TextBox { Text = suggested > 0 ? suggested.ToString() : "自动分配", PlaceholderText = "0 或空 = 由 dsh 分配" };
                var txtWorkspace = new TextBox
                {
                    Text = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
                };
                var btnBrowseWs = new Button
                {
                    Content = "浏览…",
                    Style = (Style)Application.Current.Resources["BtnCompact"]
                };
                btnBrowseWs.Click += async (_, __) =>
                {
                    string dir = await PickFolderAsync("选择实例工作目录");
                    if (dir != null) txtWorkspace.Text = dir;
                };

                var layout = new StackPanel { Spacing = 12, MinWidth = 420 };
                layout.Children.Add(LabelledField("名称", txtName));
                layout.Children.Add(LabelledField("端口", txtPort));

                var wsRow = new Grid { ColumnSpacing = 8 };
                wsRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
                wsRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                wsRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                wsRow.Children.Add(new TextBlock
                {
                    Text = "工作目录",
                    Style = (Style)Application.Current.Resources["FieldLabel"],
                    VerticalAlignment = VerticalAlignment.Center
                });
                Grid.SetColumn(txtWorkspace, 1);
                wsRow.Children.Add(txtWorkspace);
                Grid.SetColumn(btnBrowseWs, 2);
                wsRow.Children.Add(btnBrowseWs);
                layout.Children.Add(wsRow);

                var cmbSource = new ComboBox { Width = 320 };
                ComboBox cmbLevel = null;
                if (cloneMode)
                {
                    cmbSource.Items.Add(new CreateSourceItem("blank", "空白沙箱"));
                    cmbSource.Items.Add(new CreateSourceItem("default", "克隆 ~/.dsh（默认主目录）"));
                    foreach (InstanceDef other in _registry.Instances.Where(x => x.Id != _selectedId))
                        cmbSource.Items.Add(new CreateSourceItem("instance:" + other.Id, "克隆现有实例：" + other.Name));
                    cmbSource.SelectedIndex = 0;
                    layout.Children.Add(LabelledField("克隆来源", cmbSource));

                    cmbLevel = new ComboBox { Width = 320 };
                    cmbLevel.Items.Add(new CreateLevelItem(CloneLevel.Blank, "Blank（仅空目录）"));
                    cmbLevel.Items.Add(new CreateLevelItem(CloneLevel.Standard, "Standard（配置/技能）"));
                    cmbLevel.Items.Add(new CreateLevelItem(CloneLevel.Full, "Full（完整复制）"));
                    cmbLevel.SelectedIndex = 1;
                    layout.Children.Add(LabelledField("克隆档位", cmbLevel));
                }

                var dlg = new ContentDialog
                {
                    Title = cloneMode ? "克隆实例" : "新建实例",
                    Content = layout,
                    PrimaryButtonText = "创建",
                    CloseButtonText = "取消",
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = Root.XamlRoot
                };
                var result = await dlg.ShowAsync();
                if (result != ContentDialogResult.Primary) return;

                string name = txtName.Text.Trim();
                if (string.IsNullOrEmpty(name))
                {
                    AppendLog("实例名称不能为空，未创建。");
                    return;
                }

                string id = MakeUniqueId(name);
                string portText = txtPort.Text.Trim();
                int port = 0;
                if (!string.IsNullOrEmpty(portText) && portText != "自动分配")
                {
                    if (!int.TryParse(portText, out port) || port < 1 || port > 65535)
                    {
                        AppendLog("端口无效，未创建实例。");
                        return;
                    }
                }

                string workspace = string.IsNullOrWhiteSpace(txtWorkspace.Text)
                    ? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
                    : txtWorkspace.Text.Trim();

                string home = _homeMgr.NewHomePath(_registry.Settings.EffectiveHomeRoot, id);
                string srcHome = "";
                CreateSourceItem source = null;
                List<object> items = cmbSource?.Items.ToList() ?? new List<object>();
                if (items.Count > 0 && cmbSource.SelectedItem is CreateSourceItem si)
                    source = si;

                if (source is { Kind: "blank" })
                {
                    _homeMgr.CreateBlank(home);
                }
                else if (source is { Kind: "default" })
                {
                    string defaultHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh");
                    if (!Directory.Exists(defaultHome))
                    {
                        AppendLog("默认 ~/.dsh 不存在，克隆已改为空白目录。");
                        _homeMgr.CreateBlank(home);
                    }
                    else
                    {
                        _homeMgr.Clone(defaultHome, home, SelectedCloneLevel(cmbLevel));
                    }
                }
                else if (source is { Kind: "instance" } && _registry.TryGet(source.Value, out InstanceDef srcDef))
                {
                    srcHome = string.IsNullOrEmpty(srcDef.Home)
                        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh")
                        : srcDef.Home;
                    _homeMgr.Clone(srcHome, home, SelectedCloneLevel(cmbLevel));
                }
                else
                {
                    _homeMgr.CreateBlank(home);
                }

                var def = new InstanceDef
                {
                    Id = id,
                    Name = name,
                    Home = home,
                    Host = "127.0.0.1",
                    Port = port == 0 ? 3081 : port,
                    TrustedHosts = new List<string>(),
                    Workspace = workspace,
                    AutoOpenBrowser = true,
                    StopOnExit = true,
                    CreatedAt = DateTime.UtcNow
                };
                _registry.Add(def);
                _registry.Save();
                WireNewInstance(def);
                RefreshInstanceList();
                SelectInstance(def.Id, refreshLog: true);
                AppendLog("已创建实例: " + def.Name + "（" + def.Id + "，端口 " + def.Port + "，HOME " + def.Home + "）");
            }
            catch (Exception ex)
            {
                AppendLog("创建/克隆实例失败: " + ex.Message);
            }
        }

        private void WireNewInstance(InstanceDef def)
        {
            var mgr = _instanceMgr.For(def.Id);
            mgr.Log += (s, line) => OnManagerLog(s, line);
            mgr.OutputBatched += (s, e) => { if (IsSelectedManager(s)) AppendOutput(e.Lines); };
            mgr.StateChanged += (s, e) => { if (IsSelectedManager(s)) UpdateUiState(e.State, e.Mine, e.Pid); };
            mgr.Ready += OnBackendReady;
            mgr.StartFailed += OnStartFailed;
            mgr.AnnouncedUrlChanged += (s, url) => { if (IsSelectedManager(s)) UpdateFooter(); };
        }

        private CloneLevel SelectedCloneLevel(ComboBox cmbLevel)
        {
            if (cmbLevel != null && cmbLevel.SelectedItem is CreateLevelItem item)
                return item.Level;
            return CloneLevel.Standard;
        }

        private string MakeUniqueId(string name)
        {
            var sb = new StringBuilder();
            foreach (char c in name.ToLowerInvariant())
            {
                if (char.IsLetterOrDigit(c) || c == '_' || c == '-')
                    sb.Append(c);
                else
                    sb.Append('-');
            }
            string baseId = sb.ToString().Trim('-');
            if (string.IsNullOrEmpty(baseId)) baseId = "instance";
            string suffix = Guid.NewGuid().ToString("N").Substring(0, 6);
            string candidate = baseId + "-" + suffix;
            if (candidate.Length > 64) candidate = baseId.Substring(0, Math.Min(50, baseId.Length)) + "-" + suffix;
            while (!InstanceRegistry.IsValidId(candidate) || _registry.TryGet(candidate, out _))
                candidate = baseId + "-" + Guid.NewGuid().ToString("N").Substring(0, 6);
            return candidate;
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

            bool ok = await ConfirmAsync(
                "实例：" + def.Name + "\nID：" + def.Id + "\nHOME：" + (string.IsNullOrEmpty(def.Home) ? "（默认 ~/.dsh）" : def.Home) +
                "\n\n将停止该实例并删除数据。是否继续？",
                "删除实例");
            if (!ok) { AppendLog("已取消删除实例。"); return; }

            try
            {
                if (_instanceMgr.For(def.Id).State == BackendState.Running ||
                    _instanceMgr.For(def.Id).State == BackendState.Starting ||
                    _instanceMgr.For(def.Id).State == BackendState.Stopping ||
                    _instanceMgr.For(def.Id).State == BackendState.Restarting)
                {
                    await _instanceMgr.StopAsync(def.Id, killExternal: false);
                }

                _instanceMgr.For(def.Id).Dispose();
                _registry.Remove(def.Id);
                _registry.Save();
                RefreshInstanceList();
                _selectedId = _registry.Instances.FirstOrDefault()?.Id ?? "";
                SelectInstance(_selectedId, refreshLog: true);

                if (!string.IsNullOrEmpty(def.Home))
                {
                    string backup = "";
                    if (!_homeMgr.Delete(def.Home, keepBackup: false, out backup))
                        AppendLog("删除 HOME 目录失败（可能已被占用或不存在）: " + def.Home);
                }
            }
            catch (Exception ex)
            {
                AppendLog("删除实例失败: " + ex.Message);
            }
        }

        private StackPanel LabelledField(string label, UIElement input)
        {
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
                Value = kind.StartsWith("instance:", StringComparison.OrdinalIgnoreCase) ? kind.Substring("instance:".Length) : "";
                Text = text;
            }
            public string Kind { get; }
            public string Value { get; }
            public string Text { get; }
            public override string ToString() => Text;
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
            public override string ToString() => Text;
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

            InstanceDef def = SelectedDef();
            if (def == null)
            {
                if (showErrors) AppendLog("没有选中实例，未保存设置。");
                return false;
            }

            def.Host = host;
            def.Port = port;
            def.Workspace = string.IsNullOrWhiteSpace(TxtWorkspace.Text.Trim())
                ? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
                : TxtWorkspace.Text.Trim();
            def.Home = TxtHome.Text.Trim();
            def.TrustedHosts = SplitTrustedHosts(TxtTrustedHosts.Text);
            def.AutoOpenBrowser = SwAutoOpen.IsOn;
            def.StopOnExit = SwStopOnExit.IsOn;

            _registry.Settings.ErrorReportDir = TxtReportDir.Text.Trim();
            _registry.Settings.HomeRoot = TxtHomeRoot.Text.Trim();
            _registry.Settings.DshCommand = TxtDshCommand.Text.Trim();
            _registry.Settings.Theme = _theme;

            UpdateHomeLabel(def.Home);
            UrlLink.Content = PortTools.Url(def.Host, def.Port);
            try { UrlLink.NavigateUri = new Uri(PortTools.Url(def.Host, def.Port)); } catch { }
            return true;
        }

        private static List<string> SplitTrustedHosts(string text)
        {
            var list = new List<string>();
            if (string.IsNullOrWhiteSpace(text)) return list;
            foreach (string part in text.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string v = part.Trim();
                if (!string.IsNullOrEmpty(v) && !list.Contains(v, StringComparer.OrdinalIgnoreCase))
                    list.Add(v);
            }
            return list;
        }

        private void SaveAllSettings()
        {
            try { _registry.Save(); } catch { }
        }

        private async void BtnBrowseWs_Click(object sender, RoutedEventArgs e)
        {
            string dir = await PickFolderAsync("选择 dsh 工作目录（默认 workspace 根目录）");
            if (dir != null) TxtWorkspace.Text = dir;
        }

        private async void BtnBrowseHome_Click(object sender, RoutedEventArgs e)
        {
            string dir = await PickFolderAsync("选择实例 DSH_HOME 目录（留空表示不注入）");
            if (dir != null) TxtHome.Text = dir;
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

        private void OnManagerLog(object sender, string line)
        {
            if (IsSelectedManager(sender)) AppendLog(line);
        }

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
            try { _instanceMgr.DisposeAll(); } catch { }
        }
    }
}
