// ============================================================================
//  InstancePanel — 单个运行环境（Windows / WSL）的实例面板（v0.5.0）
//
//  MainWindow 的 TabView 各挂一个 InstancePanel：
//    - 实例列表按运行环境过滤（Windows 标签只列 windows 实例，WSL 标签只列 wsl）；
//    - 每面板独立记忆选中实例、独立状态轮询、独立设置字段（WSL 显示发行版/
//      WSL DSH_HOME，Windows 显示 DSH_HOME）；
//    - harness 版本：实例默认跟随当前环境主实例版本，也可指定任意版本
//      （经 npx 拉取该版本启动）；新建实例默认跟随当前环境检测到的版本；
//    - 启动失败报告由核心层生成（BackendManager.FailStart → ErrorReporter），
//      本面板只负责把报告路径打到控制台并弹窗让用户打开/定位。
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
    public sealed partial class InstancePanel : UserControl
    {
        private readonly HomeManager _homeMgr = new HomeManager();
        private readonly HashSet<string> _wired = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly SemaphoreSlim _probeGate = new SemaphoreSlim(1, 1);

        private InstanceRegistry _registry;
        private InstanceManager _instanceMgr;
        private string _env = "windows";               // "windows" | "wsl"
        private Action<string> _console;               // 控制台回调（MainWindow 统一加时间戳）
        private Action _onInstancesChanged;            // 实例增删后通知 MainWindow 刷新页脚
        private Microsoft.UI.Dispatching.DispatcherQueue _dq;
        private DispatcherQueueTimer _timer;

        private string _selectedId = "";
        private bool _loadingList;
        private bool _loadingSettings;
        private bool _closing;
        private int _externalPidCache;
        private string _cachedSelectedId = "";
        private BackendState _uiState = BackendState.Stopped;
        private bool _uiMine;
        private string _detectedVersion = "";          // 当前环境 harness 主实例版本（检测缓存）
        private bool _versionDetectDone;               // 是否已执行过一次版本检测
        private string _detectedFor = "";              // 版本检测对应的环境键（windows / wsl:<发行版>）
        private List<string> _publishedVersions = new List<string>();  // npm registry 拉取到的版本
        private string _lastReportPath = "";           // 最近一次失败报告路径（InfoBar 按钮用）

        public bool IsWslPanel { get { return _env == "wsl"; } }
        public string EnvironmentName { get { return _env; } }
        public string SelectedId { get { return _selectedId; } }

        /// <summary>本环境检测到的 harness 主实例版本（供 MainWindow 页脚展示）。</summary>
        public string DetectedVersion { get { return _detectedVersion; } }

        /// <summary>本环境当前处于运行状态的实例数（供 MainWindow 标签页头显示）。</summary>
        public int RunningCount()
        {
            int n = 0;
            try
            {
                foreach (InstanceDef def in InstancesOfEnv())
                {
                    BackendState s = _instanceMgr.For(def.Id).State;
                    if (s == BackendState.Running || s == BackendState.Starting || s == BackendState.Restarting)
                        n++;
                }
            }
            catch { }
            return n;
        }

        public InstancePanel()
        {
            InitializeComponent();
        }

        /// <summary>MainWindow 在构造后调用：注入依赖并启动轮询。</summary>
        public void Init(InstanceRegistry registry, InstanceManager manager, string env, Action<string> console,
            Action onInstancesChanged = null)
        {
            _registry = registry;
            _instanceMgr = manager;
            _env = env;
            _console = console;
            _onInstancesChanged = onInstancesChanged;
            _dq = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

            // 环境专属字段可见性（Windows / WSL 两个界面分别设置）
            RowWinHome.Visibility = IsWslPanel ? Visibility.Collapsed : Visibility.Visible;
            RowWslDistro.Visibility = IsWslPanel ? Visibility.Visible : Visibility.Collapsed;
            RowWslHome.Visibility = IsWslPanel ? Visibility.Visible : Visibility.Collapsed;
            RowWslPolicy.Visibility = IsWslPanel ? Visibility.Visible : Visibility.Collapsed;
            EnvBadgeText.Text = IsWslPanel ? "WSL2" : "WINDOWS";
            EmptyHintText.Text = IsWslPanel
                ? "本环境暂无 WSL 实例，点击「新建实例」创建（需已安装 WSL2 发行版并在其中装好 node/npm）"
                : "本环境暂无 Windows 实例，点击「新建实例」创建";
            TxtWorkspaceHint.Text = IsWslPanel
                ? "填 ~/xxx 或 /xxx = 发行版内原生路径（完全隔离）；填 Windows 路径（C:\\…）= 经 /mnt/c 按需共享"
                : "";
            InitWslPolicyCombo();

            WireAll();
            RefreshInstanceList();
            _selectedId = InstancesOfEnv().FirstOrDefault()?.Id ?? "";
            SelectInstance(_selectedId, refreshLog: false);

            _timer = _dq.CreateTimer();
            _timer.Interval = TimeSpan.FromSeconds(1);
            _timer.Tick += async (s, e) => await ProbeTickAsync();
            _timer.Start();

            _ = DetectVersionAsync(show: false);
            _ = ProbeTickAsync();
        }

        /// <summary>窗口关闭时停轮询。</summary>
        public void Shutdown()
        {
            _closing = true;
            try { if (_timer != null) _timer.Stop(); } catch { }
        }

        /// <summary>关窗前静默保存设置字段（无效输入跳过，不弹警告）。</summary>
        public void SilentSave()
        {
            try { TryReadSettings(showErrors: false); } catch { }
        }

        // ==================== 实例过滤与事件接线 ====================

        private IEnumerable<InstanceDef> InstancesOfEnv()
        {
            return _registry.Instances.Where(d => d.IsWsl == IsWslPanel);
        }

        private void WireAll()
        {
            foreach (InstanceDef def in InstancesOfEnv()) WireInstance(def);
        }

        private void WireInstance(InstanceDef def)
        {
            if (!_wired.Add(def.Id)) return;
            var mgr = _instanceMgr.For(def.Id);
            mgr.Log += (s, line) => PushLog(PrefixFor(s) + line);
            mgr.OutputBatched += (s, e) =>
            {
                string prefix = PrefixFor(s);
                foreach (string line in e.Lines) PushLog(prefix + line);
            };
            mgr.StateChanged += (s, e) => { if (IsSelectedManager(s)) UpdateUiState(e.State, e.Mine, e.Pid); };
            mgr.Ready += OnBackendReady;
            mgr.StartFailed += OnStartFailed;
            mgr.AnnouncedUrlChanged += (s, url) => { if (IsSelectedManager(s)) UpdateAnnounced(url); };
        }

        private bool IsSelectedManager(object sender)
        {
            if (_closing || string.IsNullOrEmpty(_selectedId)) return false;
            return ReferenceEquals(sender, _instanceMgr.For(_selectedId));
        }

        private InstanceDef DefForSender(object sender)
        {
            foreach (InstanceDef d in InstancesOfEnv())
            {
                if (ReferenceEquals(_instanceMgr.For(d.Id), sender)) return d;
            }
            return null;
        }

        private string PrefixFor(object sender)
        {
            InstanceDef def = DefForSender(sender);
            return def == null ? "" : "[" + (IsWslPanel ? "WSL·" : "WIN·") + def.Name + "] ";
        }

        private void PushLog(string line)
        {
            if (_closing) return;
            try { _console?.Invoke(line); } catch { }
        }

        private InstanceDef SelectedDef()
        {
            if (!_registry.TryGet(_selectedId, out InstanceDef def)) return null;
            if (def.IsWsl != IsWslPanel) return null;
            return def;
        }

        // ==================== 实例选择器 ====================

        private void RefreshInstanceList()
        {
            _loadingList = true;
            try
            {
                CmbInstance.ItemsSource = InstancesOfEnv().ToList();
                string ver = _detectedVersion.Length > 0
                    ? " · 当前环境 harness v" + _detectedVersion
                    : (_versionDetectDone ? " · 当前环境未检测到 harness" : "");
                TxtInstanceHint.Text = (IsWslPanel
                    ? "共 " + InstancesOfEnv().Count() + " 个 WSL 实例 · 在发行版内运行"
                    : "共 " + InstancesOfEnv().Count() + " 个 Windows 实例 · 本机直接运行") + ver;
            }
            finally { _loadingList = false; }
        }

        private void CmbInstance_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loadingList || _loadingSettings || _closing) return;
            InstanceDef def = CmbInstance.SelectedItem as InstanceDef;
            if (def != null && !string.Equals(def.Id, _selectedId, StringComparison.OrdinalIgnoreCase))
                SelectInstance(def.Id, refreshLog: true);
        }

        private void SelectInstance(string id, bool refreshLog)
        {
            if (string.IsNullOrEmpty(id) || !_registry.TryGet(id, out InstanceDef def) || def.IsWsl != IsWslPanel)
                _selectedId = "";
            else
                _selectedId = id;

            // 同步 ComboBox 选中项（引用匹配 + 索引兜底）
            SyncPickerSelection();

            _externalPidCache = 0;
            _cachedSelectedId = _selectedId;
            RefreshSelectedControls();
            _ = ProbeTickAsync();
        }

        /// <summary>把实例下拉的选中项对齐到 _selectedId（引用匹配优先，索引兜底）。</summary>
        private void SyncPickerSelection()
        {
            _loadingSettings = true;
            try
            {
                InstanceDef target = SelectedDef();
                if (target != null) CmbInstance.SelectedItem = target;
                if (CmbInstance.SelectedItem is InstanceDef cur && string.Equals(cur.Id, _selectedId, StringComparison.OrdinalIgnoreCase))
                {
                    /* 已匹配 */
                }
                else if (CmbInstance.ItemsSource is System.Collections.IList items)
                {
                    for (int i = 0; i < items.Count; i++)
                    {
                        if (items[i] is InstanceDef itemDef &&
                            string.Equals(itemDef.Id, _selectedId, StringComparison.OrdinalIgnoreCase))
                        {
                            CmbInstance.SelectedIndex = i;
                            break;
                        }
                    }
                }
            }
            finally { _loadingSettings = false; }
        }

        private void RefreshSelectedControls()
        {
            InstanceDef def = SelectedDef();
            _loadingSettings = true;
            try
            {
                if (def == null)
                {
                    TxtHost.Text = "";
                    TxtPort.Text = "";
                    TxtWorkspace.Text = "";
                    TxtHome.Text = "";
                    TxtTrustedHosts.Text = "";
                    CmbWslDistro.Text = "";
                    TxtWslHome.Text = "";
                    SwAutoOpen.IsOn = false;
                    SwStopOnExit.IsOn = false;
                    UrlLink.Content = "—";
                    try { UrlLink.NavigateUri = null; } catch { }
                    HomeText.Text = IsWslPanel ? "WSL 实例 · DSH_HOME(Linux): —" : "DSH_HOME: —";
                    StatusText.Text = "无实例";
                    PidText.Text = "—";
                    VersionText.Text = "harness 版本未知";
                    LaunchModeText.Text = "";
                    TxtSettingsSubtitle.Text = "";
                    TxtAnnounced.Visibility = Visibility.Collapsed;
                    TxtAnnounced.Text = "";
                    PopulateVersionCombo(null);
                    UpdateUiState(BackendState.Stopped, false, 0);
                    EmptyHint.Visibility = InstancesOfEnv().Count() == 0 ? Visibility.Visible : Visibility.Collapsed;
                    return;
                }

                TxtHost.Text = def.Host;
                TxtPort.Text = def.Port.ToString();
                TxtWorkspace.Text = def.Workspace;
                TxtHome.Text = def.Home ?? "";
                TxtTrustedHosts.Text = def.TrustedHosts == null ? "" : string.Join(", ", def.TrustedHosts);
                SwAutoOpen.IsOn = def.AutoOpenBrowser;
                SwStopOnExit.IsOn = def.StopOnExit;
                CmbWslDistro.Text = def.WslDistro ?? "";
                TxtWslHome.Text = def.WslHome ?? "";
                TxtSettingsSubtitle.Text = "· " + (def.Name ?? "") + "（" + def.Id + "）";
                EmptyHint.Visibility = Visibility.Collapsed;
                PopulateVersionCombo(def);
                UpdateHomeLabel(def);
                UpdateUrl(def);
                UpdateVersionText(def);
                UpdateUiState(CurrentStateFor(def), CurrentMineFor(def), CurrentPidFor(def));
            }
            finally { _loadingSettings = false; }
        }

        private void UpdateHomeLabel(InstanceDef def)
        {
            if (def.IsWsl)
            {
                string linuxHome = string.IsNullOrWhiteSpace(def.WslHome) ? "~/.dsh" : def.WslHome.Trim();
                string distro = string.IsNullOrWhiteSpace(def.WslDistro) ? "(发行版未配置)" : def.WslDistro;
                HomeText.Text = "WSL 实例 · 发行版 " + distro + " · DSH_HOME(Linux): " + linuxHome;
            }
            else if (string.IsNullOrWhiteSpace(def.Home))
                HomeText.Text = "DSH_HOME: ~/.dsh(默认,不注入)";
            else
                HomeText.Text = "DSH_HOME: " + def.Home.Trim();
        }

        private void UpdateUrl(InstanceDef def)
        {
            string url = PortTools.Url(def.Host, def.Port);
            UrlLink.Content = url;
            try { UrlLink.NavigateUri = new Uri(url); } catch { }
            UpdateAnnounced(def != null && !string.IsNullOrEmpty(def.Id)
                ? _instanceMgr.For(def.Id).AnnouncedUrl : "");
        }

        private void UpdateAnnounced(string announced)
        {
            InstanceDef def = SelectedDef();
            string url = def == null ? "" : PortTools.Url(def.Host, def.Port);
            bool show = def != null && !string.IsNullOrEmpty(announced) &&
                        !string.Equals(announced, url, StringComparison.OrdinalIgnoreCase);
            TxtAnnounced.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (show) TxtAnnounced.Text = "公告: " + announced;
        }

        private void UpdateVersionText(InstanceDef def)
        {
            string pinned = (def?.HarnessVersion ?? "").Trim();
            if (def == null)
            {
                VersionText.Text = "harness 版本未知";
                LaunchModeText.Text = "";
                return;
            }

            if (pinned.Length > 0)
            {
                VersionText.Text = "harness v" + pinned + " · 指定版本";
                LaunchModeText.Text = IsWslPanel
                    ? "启动方式: 发行版内 npx --yes @deepseek-ai/dsh@" + pinned + "（首次拉取需联网）"
                    : "启动方式: npx --yes @deepseek-ai/dsh@" + pinned + "（首次拉取需联网）";
            }
            else
            {
                VersionText.Text = _detectedVersion.Length > 0
                    ? "harness v" + _detectedVersion + " · 当前环境"
                    : "harness 版本未知 · 当前环境";
                LaunchModeText.Text = IsWslPanel
                    ? "启动方式: 发行版内已安装的 dsh（跟随当前环境主实例版本）"
                    : "启动方式: 本机已安装的 dsh（跟随当前环境主实例版本）";
            }
        }

        // ==================== harness 版本（v0.5.0） ====================

        /// <summary>版本下拉：① 跟随当前环境（默认）② 当前已指定的版本 ③ 拉取到的已发布版本。</summary>
        private void PopulateVersionCombo(InstanceDef def)
        {
            _loadingSettings = true;
            try
            {
                CmbVersion.Items.Clear();
                var defItem = new ComboBoxItem
                {
                    Content = _detectedVersion.Length > 0
                        ? "跟随当前环境（v" + _detectedVersion + "）"
                        : "跟随当前环境",
                    Tag = ""
                };
                CmbVersion.Items.Add(defItem);

                string pinned = (def?.HarnessVersion ?? "").Trim();
                if (pinned.Length > 0)
                    CmbVersion.Items.Add(new ComboBoxItem { Content = pinned + "（当前指定）", Tag = pinned });

                // 当前环境版本也提供「显式指定」入口（与「跟随」区分：显式 = 走 npx 拉取该版本）
                if (_detectedVersion.Length > 0 && _detectedVersion != pinned)
                    CmbVersion.Items.Add(new ComboBoxItem
                    {
                        Content = _detectedVersion + "（指定为当前环境版本）",
                        Tag = _detectedVersion
                    });

                foreach (string v in _publishedVersions)
                {
                    if (v == pinned || v == _detectedVersion) continue;
                    CmbVersion.Items.Add(new ComboBoxItem { Content = v, Tag = v });
                }

                CmbVersion.SelectedItem = pinned.Length > 0
                    ? CmbVersion.Items.OfType<ComboBoxItem>().First(i => (i.Tag as string) == pinned)
                    : defItem;
            }
            finally { _loadingSettings = false; }
        }

        /// <summary>
        /// 读取版本下拉当前值：返回规范化后的版本号；空串 = 跟随当前环境。
        /// 输入非法（不是 x.y.z[-预发布]）时返回 null，由调用方提示并放弃保存。
        /// </summary>
        private string ReadVersionCombo()
        {
            if (CmbVersion.SelectedItem is ComboBoxItem itm && itm.Tag is string tag)
            {
                // 选中项就是权威值（下拉项文案带中文说明，不能按文本解析）
                string sel = (CmbVersion.Text ?? "").Trim();
                string selContent = (itm.Content as string ?? "").Trim();
                if (sel.Length == 0 || sel == selContent) return tag;
            }
            string typed = (CmbVersion.Text ?? "").Trim();
            string normalized;
            if (!HarnessVersion.TryNormalizeVersion(typed, out normalized)) return null;
            return normalized;
        }

        private void InitWslPolicyCombo()
        {
            if (!IsWslPanel) return;
            _loadingSettings = true;
            try
            {
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
            finally { _loadingSettings = false; }
        }

        private async void BtnListDistros_Click(object sender, RoutedEventArgs e)
        {
            BtnListDistros.IsEnabled = false;
            try
            {
                string keep = (CmbWslDistro.Text ?? "").Trim();
                var distros = await WslTools.ListDistrosAsync();
                _loadingSettings = true;
                try
                {
                    CmbWslDistro.Items.Clear();
                    foreach (string d in distros) CmbWslDistro.Items.Add(d);
                    CmbWslDistro.Text = keep;
                }
                finally { _loadingSettings = false; }
                PushLog(distros.Count > 0
                    ? "已安装的 WSL 发行版: " + string.Join(", ", distros)
                    : "未检测到已安装的 WSL 发行版（可在管理员 PowerShell 执行 wsl --install -d <发行版>）");
            }
            catch (Exception ex) { PushLog("扫描发行版失败: " + ex.Message); }
            finally { BtnListDistros.IsEnabled = true; }
        }

        private async void BtnFetchVersions_Click(object sender, RoutedEventArgs e)
        {
            BtnFetchVersions.IsEnabled = false;
            string old = BtnFetchVersions.Content as string;
            BtnFetchVersions.Content = "拉取中…";
            try
            {
                List<string> versions;
                if (IsWslPanel)
                {
                    string distro = CurrentDistro();
                    if (distro.Length == 0)
                    {
                        PushLog("⚠ 请先填写 WSL 发行版名称，再拉取版本列表");
                        return;
                    }
                    versions = await HarnessVersion.ListVersionsWslAsync(distro);
                }
                else
                {
                    versions = await HarnessVersion.ListVersionsWindowsAsync();
                }

                if (versions.Count == 0)
                {
                    PushLog("未能从 npm registry 拉取版本列表（检查网络/npm 是否可用）；可直接手动输入版本号");
                    return;
                }
                _publishedVersions = versions;
                PopulateVersionCombo(SelectedDef());
                PushLog("已拉取 " + versions.Count + " 个已发布版本，最新: " + versions[0]);
            }
            catch (Exception ex) { PushLog("拉取版本列表失败: " + ex.Message); }
            finally
            {
                BtnFetchVersions.Content = old ?? "拉取版本列表";
                BtnFetchVersions.IsEnabled = true;
            }
        }

        /// <summary>WSL 面板当前生效的发行版（优先输入框，其次选中实例配置）。</summary>
        private string CurrentDistro()
        {
            string typed = (CmbWslDistro.Text ?? "").Trim();
            if (typed.Length > 0) return typed;
            return (SelectedDef()?.WslDistro ?? "").Trim();
        }

        private async void BtnDetectVersion_Click(object sender, RoutedEventArgs e)
        {
            BtnDetectVersion.IsEnabled = false;
            try { await DetectVersionAsync(show: true, force: true); }
            finally { BtnDetectVersion.IsEnabled = true; }
        }

        private async Task DetectVersionAsync(bool show, bool force = false)
        {
            string envKey = IsWslPanel ? "wsl:" + CurrentDistro() : "windows";
            if (!force && _versionDetectDone && _detectedFor == envKey) return;

            string v = "";
            try
            {
                if (IsWslPanel)
                {
                    string distro = CurrentDistro();
                    if (distro.Length == 0)
                    {
                        if (show) PushLog("⚠ 请先在实例设置中填写发行版名称，再检测 WSL 内 harness 版本");
                        _versionDetectDone = true;
                        return;
                    }
                    v = await HarnessVersion.ResolveWslAsync(distro).ConfigureAwait(true);
                }
                else
                {
                    Config cfg = SelectedDef()?.ToConfig(_registry.Settings) ?? new Config();
                    v = await HarnessVersion.ResolveWindowsAsync(cfg).ConfigureAwait(true);
                }
            }
            catch { v = ""; }

            _detectedVersion = v;
            _versionDetectDone = true;
            _detectedFor = envKey;
            if (show)
            {
                PushLog(v.Length > 0
                    ? (IsWslPanel ? "WSL " + CurrentDistro() + " 内" : "Windows 环境") + " harness 主实例版本: v" + v
                    : "未检测到当前环境 harness 版本（请确认 dsh 已安装）");
            }
            RefreshSelectedControls();
            NotifyInstancesChanged();
        }

        // ==================== 状态刷新 ====================

        private BackendState CurrentStateFor(InstanceDef def) { return _instanceMgr.For(def.Id).State; }
        private bool CurrentMineFor(InstanceDef def) { return _instanceMgr.For(def.Id).IsMine; }
        private int CurrentPidFor(InstanceDef def) { return _instanceMgr.For(def.Id).ChildPid; }

        private async Task ProbeTickAsync()
        {
            if (_closing || !_probeGate.Wait(0)) return;
            try
            {
                InstanceDef def = SelectedDef();
                if (def == null) return;

                string probeId = def.Id;
                BackendState s = _instanceMgr.For(probeId).State;
                if (s == BackendState.Starting || s == BackendState.Stopping || s == BackendState.Restarting)
                    return;

                bool up = await PortTools.ProbeAsync(def.Host, def.Port).ConfigureAwait(true);
                int pid = 0;
                BackendManager mgr = _instanceMgr.For(probeId);
                bool mine = mgr.IsMine && up;
                if (up)
                {
                    pid = mine ? mgr.ChildPid : await PortTools.FindListenerPidAsync(def.Port).ConfigureAwait(true);
                    if (_cachedSelectedId == probeId) _externalPidCache = pid;
                }
                else if (_cachedSelectedId == probeId)
                {
                    _externalPidCache = 0;
                }

                var newState = up ? BackendState.Running : BackendState.Stopped;
                _dq.TryEnqueue(() =>
                {
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
            bool changed = _uiState != state || _uiMine != mine;
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
            PidText.Text = state == BackendState.Running
                ? (mine ? "本程序 " + pid : (pid > 0 ? "外部 " + pid : "外部"))
                : "—";

            bool busy = state == BackendState.Starting || state == BackendState.Stopping || state == BackendState.Restarting;
            bool hasInstance = SelectedDef() != null;
            BtnStart.IsEnabled = hasInstance && state == BackendState.Stopped;
            BtnRestart.IsEnabled = hasInstance && state == BackendState.Running;
            BtnStop.IsEnabled = hasInstance && (state == BackendState.Running || state == BackendState.Starting);
            BtnOpen.IsEnabled = hasInstance;

            // 状态变化时同步标签页头（实例数/运行数）与页脚
            if (changed) NotifyInstancesChanged();
        }

        private Brush StateBrush(string colorKey)
        {
            try
            {
                string key = ActualTheme == ElementTheme.Dark ? "Dark" : "Light";
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

        // ==================== 操作 ====================

        private async void BtnStart_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_selectedId)) return;
            if (!TryReadSettings(showErrors: true)) return;
            SaveAllSettings();
            FailBar.IsOpen = false;                       // 新的一次启动：清掉上次失败提示
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
                int pid = _externalPidCache;
                bool ok = await ConfirmAsync(
                    "检测到后端由外部进程" + (pid > 0 ? "（PID " + pid + "）" : "") + "提供。\n\n" +
                    "重启将结束该进程并由本程序重新启动后端。\n浏览器不会自动打开；现有页面刷新即可重连。是否继续？",
                    "确认重启外部后端");
                if (!ok) { PushLog("已取消重启外部后端。"); return; }
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
                if (!killExternal) { PushLog("已取消停止外部进程。"); return; }
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
                PushLog("操作异常: " + ex.Message);
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
            // 就绪动作按"触发该事件的实例自身配置"执行，而不是当前选中实例
            bool autoOpen = SwAutoOpen.IsOn;
            InstanceDef def = DefForSender(sender);
            if (def != null) autoOpen = def.AutoOpenBrowser;
            if (e.SuppressAutoOpen)
            {
                PushLog("后端已就绪（重启路径：未打开浏览器）。浏览器中的旧页面刷新即可重连。");
            }
            else if (autoOpen)
            {
                OpenBrowser(e.Url);
            }
            else
            {
                PushLog("后端已就绪: " + e.Url + "（按实例设置未自动打开浏览器）");
            }
        }

        private void OpenBrowser(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                PushLog("已在默认浏览器打开: " + url);
            }
            catch (Exception ex)
            {
                PushLog("打开浏览器失败: " + ex.Message);
            }
        }

        // ==================== 失败报告（报告由核心层生成，此处只提示） ====================

        private void OnStartFailed(object sender, StartFailureContext ctx)
        {
            if (_closing) return;
            InstanceDef def = DefForSender(sender);
            string who = def != null ? "[" + (IsWslPanel ? "WSL·" : "WIN·") + def.Name + "] " : "";
            PushLog(who + "后端启动失败：" + ctx.FailureKind +
                (string.IsNullOrEmpty(ctx.Summary) ? "" : "。" + ctx.Summary));

            // 只为本面板的实例弹提示条（另一个环境的失败不打扰当前界面）
            if (def == null) return;

            _lastReportPath = ctx.ReportPath ?? "";
            FailBar.Title = "启动失败：" + (ctx.FailureKind ?? "未知") + "（" + def.Name + "）";
            FailBar.Message = (string.IsNullOrEmpty(ctx.Summary) ? "" : ctx.Summary + "\n") +
                (string.IsNullOrEmpty(_lastReportPath)
                    ? "⚠ 失败报告未生成，请检查报告目录是否可写（全局设置 → 报告目录）"
                    : "报错详情 + 实例信息 + 时间已写入报告：" + _lastReportPath);
            FailBar.Severity = InfoBarSeverity.Error;
            BtnOpenReport.IsEnabled = !string.IsNullOrEmpty(_lastReportPath);
            BtnCopyReportPath.IsEnabled = !string.IsNullOrEmpty(_lastReportPath);
            FailBar.IsOpen = true;

            if (!string.IsNullOrEmpty(_lastReportPath))
                PushLog(who + "已生成失败报告: " + _lastReportPath);
            else
                PushLog(who + "⚠ 失败报告未生成（请检查报告目录是否可写）");
        }

        private void BtnOpenReport_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_lastReportPath)) return;
            try { Process.Start(new ProcessStartInfo(_lastReportPath) { UseShellExecute = true }); }
            catch (Exception ex) { PushLog("打开报告失败: " + ex.Message); }
        }

        private void BtnOpenReportDir_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!string.IsNullOrEmpty(_lastReportPath) && File.Exists(_lastReportPath))
                {
                    Process.Start("explorer.exe", "/select,\"" + _lastReportPath + "\"");
                    return;
                }
                string dir = ReportDir();
                Directory.CreateDirectory(dir);
                Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
            }
            catch (Exception ex) { PushLog("打开报告目录失败: " + ex.Message); }
        }

        private void BtnCopyReportPath_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dp = new DataPackage();
                dp.SetText(string.IsNullOrEmpty(_lastReportPath) ? ReportDir() : _lastReportPath);
                Clipboard.SetContent(dp);
                PushLog("已复制报告路径到剪贴板。");
            }
            catch (Exception ex) { PushLog("复制失败: " + ex.Message); }
        }

        /// <summary>生效的报告目录（全局设置为空时用「我的文档\DshController\error-reports」）。</summary>
        private string ReportDir()
        {
            string dir = (_registry.Settings.ErrorReportDir ?? "").Trim();
            if (dir.Length > 0) return dir;
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "DshController", "error-reports");
        }

        private async void BtnSuggestPort_Click(object sender, RoutedEventArgs e)
        {
            BtnSuggestPort.IsEnabled = false;
            try
            {
                int suggested = await PortAllocatorSuggestAsync(IsWslPanel ? 3081 : 3080);
                if (suggested > 0)
                {
                    TxtPort.Text = suggested.ToString();
                    PushLog("推荐端口: " + suggested);
                }
                else PushLog("3080–3099 段内没有空闲端口，请手动指定。");
            }
            catch (Exception ex) { PushLog("端口推荐失败: " + ex.Message); }
            finally { BtnSuggestPort.IsEnabled = true; }
        }

        private void BtnOpenWs_Click(object sender, RoutedEventArgs e)
        {
            string ws = (TxtWorkspace.Text ?? "").Trim();
            if (ws.Length == 0) { PushLog("工作目录为空。"); return; }
            if (IsWslPanel && !WslTools.IsWindowsPath(ws))
            {
                // WSL 原生路径经 \\wsl$\<发行版>\ 打开
                string distro = CurrentDistro();
                if (distro.Length == 0) { PushLog("请先填写 WSL 发行版名称。"); return; }
                string unc = @"\\wsl$\" + distro + (ws.StartsWith("~", StringComparison.Ordinal)
                    ? @"\home" + ws.Substring(1).Replace('/', '\\')
                    : ws.Replace('/', '\\'));
                OpenPath(unc);
                return;
            }
            OpenPath(ws);
        }

        private void BtnOpenHome_Click(object sender, RoutedEventArgs e)
        {
            string home = (TxtHome.Text ?? "").Trim();
            if (home.Length == 0)
                home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh");
            OpenPath(home);
        }

        private void OpenPath(string path)
        {
            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                PushLog("已打开: " + path);
            }
            catch (Exception ex) { PushLog("打开失败（" + path + "）: " + ex.Message); }
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
                    XamlRoot = XamlRoot
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
                // 新建实例默认 = 当前环境 harness 主实例版本：先确保版本检测完成
                if (!_versionDetectDone) await DetectVersionAsync(show: false);

                int suggested = await PortAllocatorSuggestAsync(IsWslPanel ? 3081 : 3080);
                var txtName = new TextBox { PlaceholderText = "实例名称，如 项目A" };
                var txtPort = new TextBox { Text = suggested > 0 ? suggested.ToString() : "自动分配", PlaceholderText = "0 或空 = 由 dsh 分配" };

                string inheritedWs = _registry.Settings.NewInstanceWorkspace?.Trim();
                if (string.IsNullOrWhiteSpace(inheritedWs))
                    inheritedWs = SelectedDef()?.Workspace;
                if (IsWslPanel && !string.IsNullOrWhiteSpace(inheritedWs) && WslTools.IsWindowsPath(inheritedWs))
                    inheritedWs = null;                    // WSL 面板默认给 Linux 原生路径，避免默认就走 /mnt/c
                if (string.IsNullOrWhiteSpace(inheritedWs))
                    inheritedWs = IsWslPanel
                        ? "~/dsh-workspaces"
                        : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                var txtWorkspace = new TextBox { Text = inheritedWs };
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

                var layout = new StackPanel { Spacing = 12, MinWidth = 440 };
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

                // WSL 面板：发行版 + Linux 侧 DSH_HOME（环境由面板决定，无需运行环境下拉）
                TextBox txtWslDistro = null;
                TextBox txtWslHome = null;
                if (IsWslPanel)
                {
                    txtWslDistro = new TextBox
                    {
                        PlaceholderText = "如 Ubuntu-26.04",
                        Text = CurrentDistro()
                    };
                    txtWslHome = new TextBox { PlaceholderText = "留空 = ~/.dsh；建议 ~/dsh-instances/<名称>" };
                    layout.Children.Add(LabelledField("WSL 发行版", txtWslDistro));
                    layout.Children.Add(LabelledField("WSL DSH_HOME", txtWslHome));
                }

                // harness 版本：默认跟随当前环境主实例版本（可改为任意指定版本）
                var cmbVersion = new ComboBox { Width = 320, IsEditable = true };
                var verDefault = new ComboBoxItem
                {
                    Content = _detectedVersion.Length > 0
                        ? "跟随当前环境（v" + _detectedVersion + "）"
                        : "跟随当前环境",
                    Tag = ""
                };
                cmbVersion.Items.Add(verDefault);
                if (_detectedVersion.Length > 0)
                    cmbVersion.Items.Add(new ComboBoxItem
                    {
                        Content = _detectedVersion + "（指定为当前环境版本）",
                        Tag = _detectedVersion
                    });
                foreach (string v in _publishedVersions)
                {
                    if (v == _detectedVersion) continue;
                    cmbVersion.Items.Add(new ComboBoxItem { Content = v, Tag = v });
                }
                cmbVersion.SelectedItem = verDefault;      // 默认 = 当前环境主实例版本
                var verRow = new StackPanel { Spacing = 4 };
                verRow.Children.Add(new TextBlock
                {
                    Text = "harness 版本（默认跟随当前环境主实例版本，可改为指定版本）",
                    Style = (Style)Application.Current.Resources["FieldLabel"]
                });
                verRow.Children.Add(cmbVersion);
                verRow.Children.Add(new TextBlock
                {
                    Text = _detectedVersion.Length > 0
                        ? "当前环境检测到 v" + _detectedVersion + "；填写其他版本号则该实例经 npx 拉取指定版本启动"
                        : "未检测到当前环境版本；可直接填写版本号（如 0.1.0-rc.7）由 npx 拉取",
                    Style = (Style)Application.Current.Resources["FooterText"],
                    TextWrapping = TextWrapping.Wrap
                });
                layout.Children.Add(verRow);

                ComboBox cmbSource = null;
                ComboBox cmbLevel = null;
                if (cloneMode)
                {
                    cmbSource = new ComboBox { Width = 320 };
                    cmbSource.Items.Add(new CreateSourceItem("blank", "空白沙箱"));
                    if (!IsWslPanel)
                        cmbSource.Items.Add(new CreateSourceItem("default", "克隆 ~/.dsh（默认主目录）"));
                    foreach (InstanceDef other in InstancesOfEnv().Where(x => x.Id != _selectedId))
                        cmbSource.Items.Add(new CreateSourceItem("instance:" + other.Id, "克隆现有实例：" + other.Name));
                    cmbSource.SelectedIndex = 0;
                    layout.Children.Add(LabelledField("克隆来源", cmbSource));

                    cmbLevel = new ComboBox { Width = 320 };
                    cmbLevel.Items.Add(new CreateLevelItem(CloneLevel.Blank, "Blank（仅空目录）"));
                    cmbLevel.Items.Add(new CreateLevelItem(CloneLevel.Standard, "Standard（配置/技能）"));
                    cmbLevel.Items.Add(new CreateLevelItem(CloneLevel.Full, "Full（完整复制）"));
                    cmbLevel.SelectedIndex = 1;
                    layout.Children.Add(LabelledField("克隆档位", cmbLevel));

                    cmbSource.SelectionChanged += (_, __) =>
                    {
                        if (cmbSource.SelectedItem is CreateSourceItem si &&
                            si.Kind == "instance" && _registry.TryGet(si.Value, out InstanceDef src))
                        {
                            string sv = (src.HarnessVersion ?? "").Trim();
                            var match = cmbVersion.Items.OfType<ComboBoxItem>()
                                .FirstOrDefault(i => (i.Tag as string) == sv);
                            if (sv.Length > 0)
                            {
                                if (match == null)
                                {
                                    match = new ComboBoxItem { Content = sv + "（继承自源实例）", Tag = sv };
                                    cmbVersion.Items.Add(match);
                                }
                                cmbVersion.SelectedItem = match;
                            }
                            else
                            {
                                cmbVersion.SelectedItem = verDefault;
                            }
                        }
                    };
                }

                var dlg = new ContentDialog
                {
                    Title = (cloneMode ? "克隆实例 · " : "新建实例 · ") + (IsWslPanel ? "WSL2 环境" : "Windows 环境"),
                    Content = layout,
                    PrimaryButtonText = "创建",
                    CloseButtonText = "取消",
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = XamlRoot
                };
                var result = await dlg.ShowAsync();
                if (result != ContentDialogResult.Primary) return;

                string name = txtName.Text.Trim();
                if (string.IsNullOrEmpty(name))
                {
                    PushLog("实例名称不能为空，未创建。");
                    return;
                }

                string id = MakeUniqueId(name);
                string portText = txtPort.Text.Trim();
                int port = 0;
                if (!string.IsNullOrEmpty(portText) && portText != "自动分配")
                {
                    if (!int.TryParse(portText, out port) || port < 1 || port > 65535)
                    {
                        PushLog("端口无效，未创建实例。");
                        return;
                    }
                }

                string workspace = string.IsNullOrWhiteSpace(txtWorkspace.Text)
                    ? DefaultWorkspace()
                    : txtWorkspace.Text.Trim();

                // 版本：下拉选中项 Tag 非空 → 指定版本；否则解析用户手输文本
                string pinnedVersion = "";
                if (cmbVersion.SelectedItem is ComboBoxItem vitm && vitm.Tag is string vtag && vtag.Length > 0 &&
                    string.Equals((cmbVersion.Text ?? "").Trim(), (vitm.Content as string ?? "").Trim(), StringComparison.Ordinal))
                {
                    pinnedVersion = vtag;
                }
                else
                {
                    string typed = (cmbVersion.Text ?? "").Trim();
                    string normalized;
                    if (!HarnessVersion.TryNormalizeVersion(typed, out normalized))
                    {
                        PushLog("harness 版本格式无效（需形如 0.1.0-rc.7），未创建实例。");
                        return;
                    }
                    pinnedVersion = normalized;
                }

                string home = "";
                if (!IsWslPanel)
                {
                    home = _homeMgr.NewHomePath(_registry.Settings.EffectiveHomeRoot, id);
                }
                string srcHome = "";
                CreateSourceItem source = null;
                if (cmbSource != null && cmbSource.SelectedItem is CreateSourceItem sel) source = sel;

                if (IsWslPanel)
                {
                    home = ""; // WSL 实例无需 Windows 侧 HOME
                }
                else if (source is { Kind: "blank" })
                {
                    _homeMgr.CreateBlank(home);
                }
                else if (source is { Kind: "default" })
                {
                    string defaultHome = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh");
                    if (!Directory.Exists(defaultHome))
                    {
                        PushLog("默认 ~/.dsh 不存在，克隆已改为空白目录。");
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
                    if (!Directory.Exists(srcHome))
                    {
                        PushLog("源实例 HOME 目录不存在，克隆已改为空白目录。");
                        _homeMgr.CreateBlank(home);
                    }
                    else
                    {
                        _homeMgr.Clone(srcHome, home, SelectedCloneLevel(cmbLevel));
                    }
                }
                else
                {
                    _homeMgr.CreateBlank(home);
                }

                // WSL 实例默认给每个实例独立的 Linux 侧 DSH_HOME 与工作区（留空会共用 ~/.dsh，失去隔离）
                string wslHomeInput = IsWslPanel ? (txtWslHome?.Text.Trim() ?? "") : "";
                if (IsWslPanel && wslHomeInput.Length == 0) wslHomeInput = "~/dsh-instances/" + id;
                if (IsWslPanel && workspace.TrimEnd('/') == "~/dsh-workspaces")
                    workspace = "~/dsh-workspaces/" + id;

                var def = new InstanceDef
                {
                    Id = id,
                    Name = name,
                    Home = home,
                    Host = "127.0.0.1",
                    Port = port == 0 ? (suggested > 0 ? suggested : (IsWslPanel ? 3081 : 3080)) : port,
                    TrustedHosts = new List<string>(),
                    Workspace = workspace,
                    AutoOpenBrowser = true,
                    StopOnExit = true,
                    CreatedAt = DateTime.UtcNow,
                    Runtime = IsWslPanel ? "wsl" : "windows",
                    WslDistro = IsWslPanel ? (txtWslDistro?.Text.Trim() ?? "") : "",
                    WslHome = wslHomeInput,
                    HarnessVersion = pinnedVersion
                };
                _registry.Add(def);
                _registry.Save();
                WireInstance(def);
                RefreshInstanceList();
                SelectInstance(def.Id, refreshLog: true);
                PushLog("已创建" + (IsWslPanel ? " WSL" : " Windows") + "实例: " + def.Name +
                    "（" + def.Id + "，端口 " + def.Port + "，" +
                    (pinnedVersion.Length > 0 ? "harness 指定 v" + pinnedVersion : "harness 跟随当前环境") + "）");
                NotifyInstancesChanged();
            }
            catch (Exception ex)
            {
                PushLog("创建/克隆实例失败: " + ex.Message);
            }
        }

        // ==================== 手动扫描运行中实例（v0.5.1） ====================

        private bool _scanning;

        /// <summary>
        /// 手动扫描"正在运行但未注册"的本环境实例并加入列表。
        /// Windows：netstat → 进程命令行识别；WSL：发行版内 pgrep + /proc 解析
        /// （对 GUI 启动之后才在 WSL 终端手动启动的实例同样有效）。
        /// 发现项不立即落盘（与启动时自动发现语义一致，编辑保存后持久化）。
        /// </summary>
        private async void BtnScan_Click(object sender, RoutedEventArgs e)
        {
            if (_scanning || _closing) return;
            _scanning = true;
            BtnScan.IsEnabled = false;
            TxtScanLabel.Text = "扫描中…";
            PushLog("[" + (IsWslPanel ? "WSL" : "WIN") + "] 正在扫描运行中但未注册的实例…");
            try
            {
                List<InstanceDef> found = await Task.Run(() => InstanceDiscovery.Scan()).ConfigureAwait(true);
                // v0.5.1：去重键改为 (运行环境, 端口)——跨环境同端口是合法并存
                var known = new HashSet<(bool Wsl, int Port)>(
                    _registry.Instances.Select(d => (d.IsWsl, d.Port)));
                int added = 0;
                foreach (InstanceDef d in found)
                {
                    if (known.Contains((d.IsWsl, d.Port))) continue; // 已注册（含本次扫描刚加入的）
                    known.Add((d.IsWsl, d.Port));
                    if (d.IsWsl != IsWslPanel) continue;            // 只收本环境的实例
                    try
                    {
                        _registry.Add(d);
                        WireInstance(d);
                        added++;
                        PushLog("[" + (IsWslPanel ? "WSL" : "WIN") + "] 发现运行中实例: " + d.Name +
                            "（端口 " + d.Port +
                            (d.IsWsl ? "，发行版 " + d.WslDistro +
                                (string.IsNullOrEmpty(d.WslHome) ? "" : "，DSH_HOME " + d.WslHome) : "") + "）");
                    }
                    catch { /* id 冲突等：跳过该条 */ }
                }
                if (added > 0)
                {
                    RefreshInstanceList();
                    NotifyInstancesChanged();
                    PushLog("[" + (IsWslPanel ? "WSL" : "WIN") + "] 扫描完成：新增 " + added + " 个实例");
                }
                else
                {
                    PushLog("[" + (IsWslPanel ? "WSL" : "WIN") + "] 扫描完成：未发现新的运行中实例");
                }
            }
            catch (Exception ex)
            {
                PushLog("[" + (IsWslPanel ? "WSL" : "WIN") + "] 扫描失败: " + ex.Message);
            }
            finally
            {
                _scanning = false;
                BtnScan.IsEnabled = true;
                TxtScanLabel.Text = "扫描";
            }
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
                SelectInstance(_selectedId, refreshLog: true);

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
            try { _onInstancesChanged?.Invoke(); } catch { }
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

        // ==================== 设置读写 ====================

        private void BtnSaveInstance_Click(object sender, RoutedEventArgs e)
        {
            if (!TryReadSettings(showErrors: true)) return;
            SaveAllSettings();
            ExpInstanceSettings.IsExpanded = false;
            InstanceDef def = SelectedDef();
            PushLog("实例设置已保存: " + (def?.Name ?? ""));
        }

        private void BtnCancelInstance_Click(object sender, RoutedEventArgs e)
        {
            RefreshSelectedControls();
            ExpInstanceSettings.IsExpanded = false;
            PushLog("实例设置已取消");
        }

        private bool TryReadSettings(bool showErrors)
        {
            int port;
            if (!int.TryParse(TxtPort.Text.Trim(), out port) || port < 1 || port > 65535)
            {
                if (showErrors) PushLog("端口无效（需 1–65535），未保存设置。");
                return false;
            }
            string host = TxtHost.Text.Trim();
            if (string.IsNullOrEmpty(host))
            {
                if (showErrors) PushLog("主机不能为空，未保存设置。");
                return false;
            }

            InstanceDef def = SelectedDef();
            if (def == null)
            {
                if (showErrors) PushLog("没有选中实例，未保存设置。");
                return false;
            }

            string version = ReadVersionCombo();
            if (version == null)
            {
                if (showErrors)
                    PushLog("harness 版本格式无效（需形如 0.1.0 或 0.1.0-rc.7；留空/选「跟随当前环境」= 用当前环境版本），未保存设置。");
                return false;
            }

            if (IsWslPanel)
            {
                string distro = (CmbWslDistro.Text ?? "").Trim();
                if (distro.Length == 0 && showErrors)
                    PushLog("⚠ WSL 实例未填写发行版名称，启动前请补齐（可点「扫描发行版」）。");
                def.WslDistro = distro;
                def.WslHome = TxtWslHome.Text.Trim();

                // WSL 关闭策略属于 WSL 环境公共设置，随实例设置一起保存
                if (CmbWslPolicy.SelectedItem is ComboBoxItem pol && pol.Tag is string polTag)
                    _registry.Settings.WslShutdownPolicy = polTag;
            }
            else
            {
                def.WslDistro = "";
                def.WslHome = "";
            }

            def.Host = host;
            def.Port = port;
            def.Workspace = string.IsNullOrWhiteSpace(TxtWorkspace.Text.Trim())
                ? DefaultWorkspace()
                : TxtWorkspace.Text.Trim();
            def.Home = IsWslPanel ? "" : TxtHome.Text.Trim();
            def.TrustedHosts = SplitTrustedHosts(TxtTrustedHosts.Text);
            def.AutoOpenBrowser = SwAutoOpen.IsOn;
            def.StopOnExit = SwStopOnExit.IsOn;
            def.Runtime = IsWslPanel ? "wsl" : "windows";
            def.HarnessVersion = version;

            UpdateHomeLabel(def);
            UpdateUrl(def);
            UpdateVersionText(def);
            RefreshInstanceList();
            SyncPickerSelection();
            return true;
        }

        /// <summary>本环境的默认工作目录（WSL 用 Linux 家目录相对路径，Windows 用我的文档）。</summary>
        private string DefaultWorkspace()
        {
            return IsWslPanel
                ? "~/"
                : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
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

        private async Task<string> PickFolderAsync(string title)
        {
            try
            {
                var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
                picker.FileTypeFilter.Add("*");
                WinRT.Interop.InitializeWithWindow.Initialize(picker,
                    WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
                var folder = await picker.PickSingleFolderAsync();
                return folder == null ? null : folder.Path;
            }
            catch (Exception ex)
            {
                PushLog("选择目录失败: " + ex.Message);
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
                PushLog("已复制地址到剪贴板。");
            }
            catch (Exception ex)
            {
                PushLog("复制失败: " + ex.Message);
            }
        }
    }
}
