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
        private string _cachedSelectedId = "";  // 上次切换的实例 ID，用于临时竞态检测
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
            string instancesCount = _registry.Instances.Count.ToString();
            AppendLog($"刷新实例列表：共{instancesCount}个实例");
            _loadingRegistryUI = true;
            try
            {
                CmbInstance.ItemsSource = _registry.Instances.ToList();
                
                // 打印每个实例的 ID/名称，方便调试
                var sb = new System.Text.StringBuilder("实例详情: ");
                foreach (var inst in _registry.Instances)
                {
                    if (sb.Length > 20) sb.Append(", ");
                    sb.Append(inst.Name).Append("(").Append(inst.Id).Append(")");
                }
                AppendLog(sb.ToString());
            }
            finally { _loadingRegistryUI = false; }
        }

        private void CmbInstance_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loadingRegistryUI) return;
            InstanceDef def = CmbInstance.SelectedItem as InstanceDef;
            string selectedIdBefore = _selectedId;
            
            if (def != null && def.Id != _selectedId)
            {
                AppendLog($"用户选择实例：当前 '{_selectedId}' → 目标 '{def.Id}'（名称：{def.Name}）");
                SelectInstance(def.Id, refreshLog: true);
                
                // 记录切换后结果，用于验证切换是否成功
                string selectedIdAfter = _selectedId;
                InstanceDef uiDef = CmbInstance.SelectedItem as InstanceDef;
                AppendLog($"切换结果：代码选中='{selectedIdAfter}', UI 选中={uiDef?.Name ?? "(null)"}");
                
                // 如果代码与 UI 不一致，记录警告
                if (string.Equals(selectedIdAfter, uiDef?.Id, StringComparison.OrdinalIgnoreCase) == false)
                {
                    AppendLog("⚠️警告：代码选中与 UI 选中不一致！");
                }
            }
        }

        private void SelectInstance(string id, bool refreshLog)
        {
            // 切换前记录旧 ID（用于诊断）
            string prevSelectedId = _selectedId;

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
                try
                {
                    _loadingRegistryUI = true;

                    // 1. 尝试用引用匹配设置 SelectedItem（正常情况）
                    CmbInstance.SelectedItem = def;

                    // 2. 回退：如果 SelectedItem 没更新到目标实例，改用 SelectedIndex（保底方案）。
                    //    WinUI ComboBox 的 SelectedItem 通过 Equals 匹配；InstanceDef 默认引用比较。
                    //    若 ItemsSource 引用问题或竞态导致匹配失败，用索引定位更可靠。
                    int matchedIdx = -1;
                    if (CmbInstance.ItemsSource is System.Collections.IList items)
                    {
                        for (int i = 0; i < items.Count; i++)
                        {
                            if (items[i] is InstanceDef itemDef &&
                                string.Equals(itemDef.Id, _selectedId, StringComparison.OrdinalIgnoreCase))
                            {
                                matchedIdx = i;
                                break;
                            }
                        }
                    }
                    var curSel = CmbInstance.SelectedItem as InstanceDef;
                    if (matchedIdx >= 0 && (curSel == null || curSel.Id != def.Id))
                    {
                        CmbInstance.SelectedIndex = matchedIdx;
                    }
                }
                finally { _loadingRegistryUI = false; }
            }

            if (refreshLog)
            {
                TxtLog.Text = "";
                BackendManager mgr = string.IsNullOrEmpty(_selectedId) ? null : _instanceMgr.For(_selectedId);
                if (mgr != null)
                {
                    AppendLog("实例切换: '" + prevSelectedId + "' → '" + _selectedId + "' (" + def?.Name + ")");
                    foreach (string line in mgr.RecentOutput(RecentLogLines))
                        AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + line + Environment.NewLine);
                }
                else
                {
                    AppendLog("实例切换: '" + prevSelectedId + "' → '" + _selectedId + "'（无实例）");
                }
            }

            // 切换时清空 PID 缓存与选中标识，避免跨实例状态串台
            _externalPidCache = 0;
            _cachedSelectedId = _selectedId;

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
            
            // 增加详细日志记录（仅首次或少量关键调用），避免过多日志噪声
            // static bool s_refreshCount = 0; ++refreshCount < 10 && AppendLog($"刷新选中控件：{def?.Id ?? "(null)"}");
            
            if (def == null)
            {
                TxtHost.Text = "";
                TxtPort.Text = "";
                TxtWorkspace.Text = "";
                TxtHome.Text = "";
                TxtTrustedHosts.Text = "";
                TxtWslDistro.Text = "";
                TxtWslHome.Text = "";
                SwAutoOpen.IsOn = false;
                SwStopOnExit.IsOn = false;
                UrlLink.Content = "—";
                UrlLink.NavigateUri = null;
                HomeText.Text = "DSH_HOME: —";
                StatusText.Text = "无实例，请新建";
                PidText.Text = "—";
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
            TxtNewWs.Text = _registry.Settings.NewInstanceWorkspace;
            TxtDshCommand.Text = _registry.Settings.DshCommand;
            // v0.4.0 WSL 运行环境
            TxtWslDistro.Text = def.WslDistro ?? "";
            TxtWslHome.Text = def.WslHome ?? "";
            SelectRuntimeOption(def.IsWsl);
            UpdateHomeLabel(def.Home);
            UpdateUrlAndFooter(def);
            UpdateUiState(CurrentStateFor(def), CurrentMineFor(def), CurrentPidFor(def));
        }

        /// <summary>按实例运行环境选中 CmbRuntime 并切换 WSL 专属行的可见性。</summary>
        private void SelectRuntimeOption(bool isWsl)
        {
            int idx = isWsl ? 1 : 0;
            if (CmbRuntime.SelectedIndex != idx) CmbRuntime.SelectedIndex = idx;
            ApplyRuntimeVisibility(isWsl);
        }

        private void ApplyRuntimeVisibility(bool isWsl)
        {
            RowWslHome.Visibility = isWsl ? Visibility.Visible : Visibility.Collapsed;
        }

        private void CmbRuntime_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ApplyRuntimeVisibility(IsRuntimeWslSelected());
        }

        private bool IsRuntimeWslSelected()
        {
            if (CmbRuntime.SelectedItem is ComboBoxItem item && item.Tag is string tag)
                return tag.Equals("wsl", StringComparison.OrdinalIgnoreCase);
            return false;
        }

        private void UpdateHomeLabel(string home)
        {
            InstanceDef def = SelectedDef();
            if (def != null && def.IsWsl)
            {
                string linuxHome = string.IsNullOrWhiteSpace(def.WslHome) ? "~/.dsh" : def.WslHome.Trim();
                string distro = string.IsNullOrWhiteSpace(def.WslDistro) ? "(发行版未配置)" : def.WslDistro;
                HomeText.Text = "WSL 实例 · 发行版 " + distro + " · DSH_HOME(Linux): " + linuxHome;
                return;
            }
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
                string probeId = def.Id;
                BackendState s = _instanceMgr.For(probeId).State;
                if (s == BackendState.Starting || s == BackendState.Stopping || s == BackendState.Restarting)
                    return;

                bool up = await PortTools.ProbeAsync(def.Host, def.Port);
                int pid = 0;
                BackendManager mgr = _instanceMgr.For(probeId);
                bool mine = mgr.IsMine && up;
                if (up)
                {
                    pid = mine ? mgr.ChildPid : await PortTools.FindListenerPidAsync(def.Port);
                    // 仅当当前选中实例仍是探测开始时那个实例，才更新 PID 缓存
                    if (_cachedSelectedId == probeId)
                        _externalPidCache = pid;
                }
                else if (_cachedSelectedId == probeId)
                {
                    _externalPidCache = 0;
                }

                var newState = up ? BackendState.Running : BackendState.Stopped;
                DispatcherQueue.TryEnqueue(() =>
                {
                    // 双重验证：探测期间实例未切换
                    if (_cachedSelectedId == probeId &&
                        ReferenceEquals(mgr, _instanceMgr.For(_selectedId)))
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
            if (_closing) return;
            // 就绪动作按"触发该事件的实例自身配置"执行，而不是当前选中实例——
            // 用户启动 A 后切到 B，A 就绪仍应按 A 的 autoOpenBrowser 打开浏览器。
            bool autoOpen = SwAutoOpen.IsOn;
            foreach (InstanceDef d in _registry.Instances)
            {
                if (ReferenceEquals(_instanceMgr.For(d.Id), sender))
                {
                    autoOpen = d.AutoOpenBrowser;
                    break;
                }
            }
            if (e.SuppressAutoOpen)
            {
                AppendLog("后端已就绪（重启路径：未打开浏览器）。浏览器中的旧页面刷新即可重连。");
            }
            else if (autoOpen)
            {
                OpenBrowser(e.Url);
            }
            else
            {
                AppendLog("后端已就绪: " + e.Url + "（按实例设置未自动打开浏览器）");
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
            if (_closing) return;
            // 失败提示按触发失败的实例显示，不受"当前选中实例"过滤——
            // 用户启动 A 后切到 B，A 的启动失败也必须弹窗提示。
            InstanceDef def = null;
            foreach (InstanceDef d in _registry.Instances)
            {
                if (ReferenceEquals(_instanceMgr.For(d.Id), sender))
                {
                    def = d;
                    break;
                }
            }
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
                // v0.3.1：默认工作目录优先级：全局"新建实例默认工作区" > 当前选中实例工作目录 > "我的文档"兜底
                string inheritedWs = _registry.Settings.NewInstanceWorkspace?.Trim();
                if (string.IsNullOrWhiteSpace(inheritedWs))
                    inheritedWs = SelectedDef()?.Workspace;
                if (string.IsNullOrWhiteSpace(inheritedWs))
                    inheritedWs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                var txtWorkspace = new TextBox
                {
                    Text = inheritedWs
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

                // v0.4.0 运行环境选择（Windows / WSL2）
                var cmbRuntime = new ComboBox { Width = 320 };
                cmbRuntime.Items.Add(new ComboBoxItem { Content = "Windows（本机）", Tag = "windows" });
                cmbRuntime.Items.Add(new ComboBoxItem { Content = "WSL2（在发行版内运行）", Tag = "wsl" });
                cmbRuntime.SelectedIndex = 0;
                layout.Children.Add(LabelledField("运行环境", cmbRuntime));

                var txtWslDistro = new TextBox { PlaceholderText = "如 Ubuntu-26.04" };
                var txtWslHome = new TextBox { PlaceholderText = "留空 = ~/.dsh" };
                var wslPanel = new StackPanel { Spacing = 12, Visibility = Visibility.Collapsed };
                wslPanel.Children.Add(LabelledField("WSL 发行版", txtWslDistro));
                wslPanel.Children.Add(LabelledField("WSL DSH_HOME", txtWslHome));
                layout.Children.Add(wslPanel);
                cmbRuntime.SelectionChanged += (_, __) =>
                {
                    bool w = (cmbRuntime.SelectedItem as ComboBoxItem)?.Tag?.ToString() == "wsl";
                    wslPanel.Visibility = w ? Visibility.Visible : Visibility.Collapsed;
                };
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

                bool wantWsl = (cmbRuntime.SelectedItem as ComboBoxItem)?.Tag?.ToString() == "wsl";
                string home = "";
                if (!wantWsl)
                {
                    home = _homeMgr.NewHomePath(_registry.Settings.EffectiveHomeRoot, id);
                }
                string srcHome = "";
                CreateSourceItem source = null;
                List<object> items = cmbSource?.Items.ToList() ?? new List<object>();
                if (items.Count > 0 && cmbSource.SelectedItem is CreateSourceItem si)
                    source = si;

                if (wantWsl)
                {
                    // WSL 实例不需要 Windows 侧 HOME（DSH_HOME 在 Linux 内，运行环境隔离）
                    home = "";
                }
                else if (source is { Kind: "blank" })
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
                    CreatedAt = DateTime.UtcNow,
                    // v0.4.0 WSL 运行环境
                    Runtime = wantWsl ? "wsl" : "windows",
                    WslDistro = wantWsl ? txtWslDistro.Text.Trim() : "",
                    WslHome = wantWsl ? txtWslHome.Text.Trim() : ""
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

        // v0.3.1：Expander Save/Cancel 按钮处理
        private void BtnSaveInstance_Click(object sender, RoutedEventArgs e)
        {
            // TryReadSettings 返回 false → 用户输入无效（端口、主机等）
            if (!TryReadSettings(showErrors: true)) return;
            SaveAllSettings();
            
            // 保存成功并收起 Expander
            ExpInstanceSettings.IsExpanded = false;
            AppendLog("实例设置已保存");
        }

        private void BtnCancelInstance_Click(object sender, RoutedEventArgs e)
        {
            // 从注册表恢复字段值到 UI，放弃用户未保存的修改
            RefreshSelectedControls();
            
            // 收起 Expander
            ExpInstanceSettings.IsExpanded = false;
            AppendLog("实例设置已取消");
        }

        private void BtnSaveGlobal_Click(object sender, RoutedEventArgs e)
        {
            // 读取全局设置字段
            _registry.Settings.ErrorReportDir = TxtReportDir.Text.Trim();
            _registry.Settings.HomeRoot = TxtHomeRoot.Text.Trim();
            _registry.Settings.NewInstanceWorkspace = TxtNewWs.Text.Trim();
            _registry.Settings.DshCommand = TxtDshCommand.Text.Trim();
            _registry.Settings.Theme = _theme;
            
            SaveAllSettings();
            ExpGlobalSettings.IsExpanded = false;
            AppendLog("全局设置已保存");
        }

        private void BtnCancelGlobal_Click(object sender, RoutedEventArgs e)
        {
            // 从注册表恢复全局字段到 UI
            TxtReportDir.Text = _registry.Settings.ErrorReportDir;
            TxtHomeRoot.Text = _registry.Settings.HomeRoot;
            TxtNewWs.Text = _registry.Settings.NewInstanceWorkspace;
            TxtDshCommand.Text = _registry.Settings.DshCommand;
            
            ExpGlobalSettings.IsExpanded = false;
            AppendLog("全局设置已取消");
        }

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
            // v0.4.0 WSL 运行环境
            bool wsl = IsRuntimeWslSelected();
            def.Runtime = wsl ? "wsl" : "windows";
            def.WslDistro = TxtWslDistro.Text.Trim();
            def.WslHome = TxtWslHome.Text.Trim();

            _registry.Settings.ErrorReportDir = TxtReportDir.Text.Trim();
            _registry.Settings.HomeRoot = TxtHomeRoot.Text.Trim();
            _registry.Settings.NewInstanceWorkspace = TxtNewWs.Text.Trim();
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

        // v0.3.1：新建实例默认工作区浏览按钮
        private async void BtnBrowseNewWs_Click(object sender, RoutedEventArgs e)
        {
            string dir = await PickFolderAsync("选择新建实例默认工作区目录");
            if (dir != null) TxtNewWs.Text = dir;
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
