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
        private readonly HomeManager _homeMgr = new HomeManager();
        private readonly HashSet<string> _wired = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly SemaphoreSlim _probeGate = new SemaphoreSlim(1, 1);

        private InstanceRegistry _registry;
        private InstanceManager _instanceMgr;
        private string _env = "windows";               // "windows" | "wsl"
        private Action<string> _console;               // 控制台回调（MainWindow 统一加时间戳）
        private Action _onInstancesChanged;            // 实例增删后通知 MainWindow 刷新页脚
        private ArchiveHub _archive;                   // 实例档案（设置变更后让相关分面失效）
        private DialogService _dialogs;                // 弹窗统一走 DialogService（双弹窗竞态异常兜底，防未处理崩溃）
        private Microsoft.UI.Dispatching.DispatcherQueue _dq;


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
            catch { /* 理由: 统计运行中实例数只用于页脚展示，取不到就按已数到的返回 */ }
            return n;
        }

        public InstancePanel()
        {
            InitializeComponent();
        }

        /// <summary>MainWindow 在构造后调用：注入依赖并启动轮询。</summary>
        public void Init(InstanceRegistry registry, InstanceManager manager, string env, Action<string> console,
            Action onInstancesChanged = null, ArchiveHub archive = null)
        {
            _registry = registry;
            _instanceMgr = manager;
            _env = env;
            _console = console;
            _onInstancesChanged = onInstancesChanged;
            _archive = archive;
            _dialogs = new DialogService(() => XamlRoot);
            _dq = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

            // 环境专属字段可见性（Windows / WSL 两个界面分别设置）
            TxtPageEyebrow.Text = IsWslPanel ? "WSL RUNTIME WORKSPACE" : "WINDOWS RUNTIME WORKSPACE";
            TxtPageTitle.Text = IsWslPanel ? "WSL 实例" : "Windows 实例";
            RowWinHome.Visibility = IsWslPanel ? Visibility.Collapsed : Visibility.Visible;
            RowWslDistro.Visibility = IsWslPanel ? Visibility.Visible : Visibility.Collapsed;
            RowWslHome.Visibility = IsWslPanel ? Visibility.Visible : Visibility.Collapsed;
            RowWslPolicy.Visibility = IsWslPanel ? Visibility.Visible : Visibility.Collapsed;
            EnvBadgeText.Text = IsWslPanel ? "WSL2" : "WINDOWS";
            EmptyHintText.Text = IsWslPanel
                ? "本环境暂无 WSL 实例。点击「新建实例」创建；点「扫描」可发现发行版内正在运行的 dsh web（含终端手动启动），以及已安装 dsh 但未运行的发行版（询问后添加）"
                : "本环境暂无 Windows 实例。点击「新建实例」创建；若本机已有运行中的 dsh web，点「扫描」即可加入列表";
            TxtWorkspaceHint.Text = IsWslPanel
                ? "填 ~/xxx 或 /xxx = 发行版内原生路径（完全隔离）；填 Windows 路径（C:\\…）= 经 /mnt/c 按需共享"
                : "";
            InitWslPolicyCombo();

            WireAll();
            RefreshInstanceList();
            _selectedId = InstancesOfEnv().FirstOrDefault()?.Id ?? "";
            SelectInstance(_selectedId);

            // 重构 2.0：删除常驻 1 秒轮询定时器（两个面板各一个、页面隐藏也照跑）。
            // 在线状态改由实例档案的 liveness 分面驱动——统一调度器按"可见 2s / 后台 20s"
            // 采集，这里只订阅结果；用户操作后仍会立即主动探一次，反馈不变慢。
            if (_archive != null) _archive.Service.Changed += OnArchiveFacetChanged;

            _ = DetectVersionAsync(show: false);
            _ = ProbeTickAsync();
        }

        /// <summary>MainWindow 切到本页：把可见实例告诉调度器（liveness 走前台节奏）。</summary>
        public void OnShown()
        {
            if (_closing) return;
            _archive?.SetVisible(_selectedId);
            _ = ProbeTickAsync();
        }

        /// <summary>窗口关闭时退订档案事件。</summary>
        public void Shutdown()
        {
            _closing = true;
            if (_archive != null) _archive.Service.Changed -= OnArchiveFacetChanged;
        }

        /// <summary>档案的 liveness 分面更新 → 刷新本页状态卡（跨线程，需回 UI 队列）。</summary>
        private void OnArchiveFacetChanged(object sender, FacetChangedEventArgs e)
        {
            if (_closing || e == null || e.Facet != FacetNames.Liveness) return;
            if (!string.Equals(e.ArchiveId, _selectedId, StringComparison.OrdinalIgnoreCase)) return;
            if (!e.Snapshot.TryGetData(out Dictionary<string, System.Text.Json.JsonElement> data)) return;

            bool running = data.TryGetValue("running", out System.Text.Json.JsonElement r) &&
                           r.ValueKind == System.Text.Json.JsonValueKind.True;
            int pid = data.TryGetValue("listenerPid", out System.Text.Json.JsonElement p) &&
                      p.ValueKind == System.Text.Json.JsonValueKind.Number ? p.GetInt32() : 0;
            BackendManager mgr = _instanceMgr.For(_selectedId);
            if (mgr.State == BackendState.Starting || mgr.State == BackendState.Stopping ||
                mgr.State == BackendState.Restarting) return;   // 过渡态由状态机自己驱动

            _dq.TryEnqueue(() => UpdateUiState(running ? BackendState.Running : BackendState.Stopped,
                mgr.IsMine && running, pid));
        }

        /// <summary>关窗前静默保存设置字段（无效输入跳过，不弹警告）。</summary>
        public void SilentSave()
        {
            try { TryReadSettings(showErrors: false); }
            catch { /* 理由: 关窗路径：控件可能已析构，静默降级不弹窗（v0.2.0 起的约定） */ }
        }


        // ==================== 实例选择器 ====================

        private void RefreshInstanceList()
        {
            List<InstanceDef> instances = InstancesOfEnv().ToList();
            _loadingList = true;
            try
            {
                CmbInstance.ItemsSource = instances;
                string ver = _detectedVersion.Length > 0
                    ? " · 当前环境 harness v" + _detectedVersion
                    : (_versionDetectDone ? " · 当前环境未检测到 harness" : "");
                TxtInstanceHint.Text = (IsWslPanel
                    ? "共 " + instances.Count + " 个 WSL 实例 · 在发行版内运行"
                    : "共 " + instances.Count + " 个 Windows 实例 · 本机直接运行") + ver;
            }
            finally { _loadingList = false; }
        }

        private void CmbInstance_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loadingList || _loadingSettings || _closing) return;
            InstanceDef def = CmbInstance.SelectedItem as InstanceDef;
            if (def != null && !string.Equals(def.Id, _selectedId, StringComparison.OrdinalIgnoreCase))
                SelectInstance(def.Id);
        }

        private void SelectInstance(string id)
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
                    try { UrlLink.NavigateUri = null; }
                    catch { /* 理由: 空实例态下清空链接，赋值失败不影响其它字段 */ }
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
            try { UrlLink.NavigateUri = new Uri(url); }
            catch { /* 理由: 主机名异常导致 URI 非法时，文本仍展示地址，只是不可点击 */ }
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

    }
}
