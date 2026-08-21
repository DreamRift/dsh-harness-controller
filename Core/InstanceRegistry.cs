// ============================================================================
//  InstanceRegistry — instances.json 读写 / CRUD / v1→v2 迁移（v0.3.0）
//
//  迁移规则：
//    1. instances.json 已存在 → 直接加载（跳过迁移）；
//    2. 仅 launcher.json 存在 → 读取 v1 字段生成 instances[0]（id="default"，
//       home 留空 = 不注入 DSH_HOME，行为与 v0.2.0 完全一致），全局字段进
//       settings；原文件改名 launcher.json.v1.bak 保留现场；
//    3. 文件损坏/迁移失败 → 回退默认 registry（不中断启动，与 v0.2.0
//       "配置损坏不中断" 语义一致）。
//  保存为原子写（临时文件 + Move overwrite）。
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace DshController.Core
{
    /// <summary>instances.json 根结构。</summary>
    public sealed class InstancesFile
    {
        [JsonPropertyName("version")]
        public int Version { get; set; } = 2;

        [JsonPropertyName("settings")]
        public AppSettings Settings { get; set; } = new AppSettings();

        [JsonPropertyName("instances")]
        public List<InstanceDef> Instances { get; set; } = new List<InstanceDef>();
    }

    public sealed class InstanceRegistry
    {
        public static string FilePath
        {
            get { return Path.Combine(AppContext.BaseDirectory, "instances.json"); }
        }

        public static string LegacyFilePath
        {
            get { return Path.Combine(AppContext.BaseDirectory, "launcher.json"); }
        }

        private readonly InstancesFile _file;

        public IReadOnlyList<InstanceDef> Instances { get { return _file.Instances; } }
        public AppSettings Settings { get { return _file.Settings; } }

        private InstanceRegistry(InstancesFile file) { _file = file; }

        public static InstanceRegistry Load()
        {
            var file = new InstancesFile();
            try
            {
                if (File.Exists(FilePath))
                {
                    var parsed = JsonSerializer.Deserialize<InstancesFile>(
                        File.ReadAllText(FilePath, Encoding.UTF8), JsonOpts());
                    if (parsed != null) file = parsed;
                }
                else if (File.Exists(LegacyFilePath))
                {
                    // v1 → v2 迁移：只读旧文件，不修改；原文件备份为 .v1.bak
                    var legacy = JsonSerializer.Deserialize<Config>(
                        File.ReadAllText(LegacyFilePath, Encoding.UTF8), JsonOpts());
                    if (legacy != null)
                    {
                        file = new InstancesFile();
                        file.Settings.DshCommand = legacy.DshCommand;
                        file.Settings.ErrorReportDir = legacy.ErrorReportDir;
                        file.Settings.Theme = legacy.Theme;
                        file.Instances.Add(new InstanceDef
                        {
                            Id = "default",
                            Name = "主实例",
                            Home = "",
                            Host = legacy.Host,
                            Port = legacy.Port,
                            Workspace = legacy.Workspace,
                            AutoOpenBrowser = legacy.AutoOpenBrowser,
                            StopOnExit = legacy.StopOnExit,
                            CreatedAt = DateTime.UtcNow
                        });
                        try
                        {
                            File.Move(LegacyFilePath, LegacyFilePath + ".v1.bak", overwrite: true);
                        }
                        catch { /* 备份失败不阻断迁移（instances.json 已包含全部字段） */ }

                        // 迁移后立即落盘：launcher.json 已改名，若此时崩溃/未保存，
                        // 新配置会丢失——立即写 instances.json 保证迁移原子完成。
                        try
                        {
                            var opts = new JsonSerializerOptions { WriteIndented = true };
                            File.WriteAllText(FilePath,
                                JsonSerializer.Serialize(file, opts), new UTF8Encoding(false));
                        }
                        catch { /* 写盘失败不阻断启动（下次 Save 再补） */ }
                    }
                }
            }
            catch
            {
                // 配置损坏时回退默认值（与 legacy 行为一致），不中断启动
                file = new InstancesFile();
            }

            // 净化（与 v0.2.0 Config.Load 同样的路径净化语义）
            file.Settings.DshCommand = Config.SanitizePath(file.Settings.DshCommand);
            file.Settings.ErrorReportDir = Config.SanitizePath(file.Settings.ErrorReportDir);
            file.Settings.HomeRoot = Config.SanitizePath(file.Settings.HomeRoot);

            // v0.5.0：自动发现"正在运行但未注册"的实例——
            // 换目录/发布目录/清单丢失后，仍能看到并管理仍在运行的后端
            // （尤其 WSL 实例，端口经 wslrelay 还在，但本地清单为空）。
            // 仅当存在"未注册的监听端口"或"正在运行的 WSL 发行版"时才做完整探测；
            // 日常启动（清单完整、WSL 未运行）只花一次 netstat 的毫秒级成本。
            // v0.5.1 补充发行版条件：新版 WSL 下 wsl.exe 宿主不留存命令行，
            // 且转发端口可能因网络模式差异不在 netstat 呈现，需进发行版内探测。
            // v0.5.1 修复：探测整体放线程池执行——Load() 在 UI 线程被 OnLaunched
            // 同步调用，而 UI 线程装有 DispatcherQueueSynchronizationContext，
            // WslTools 异步续体会投递回 UI 队列；若在 UI 线程直接 GetResult()
            // 阻塞等待，续体永远无法执行 → 启动死锁（窗口不出现）。
            // 线程池上无 SyncContext，GetResult() 安全；UI 线程仅等最终结果。
            var discovered = new List<InstanceDef>();
            try
            {
                // v0.5.1：去重键改为 (运行环境, 端口)——WSL 与 Windows 是独立网络空间，
                // 两边各跑一个 3080 是合法并存的两实例，不能按端口一票否决。
                var known = new HashSet<(bool Wsl, int Port)>();
                foreach (InstanceDef d in file.Instances) known.Add((d.IsWsl, d.Port));
                bool needScan = Task.Run(() =>
                    InstanceDiscovery.HasUnregisteredListener(known.Select(k => k.Port).ToList()) ||
                    InstanceDiscovery.HasRunningWslDistro()).GetAwaiter().GetResult();
                if (needScan)
                {
                    foreach (InstanceDef d in Task.Run(() => InstanceDiscovery.Scan()).GetAwaiter().GetResult())
                    {
                        if (!known.Contains((d.IsWsl, d.Port)))
                        {
                            discovered.Add(d);
                            known.Add((d.IsWsl, d.Port));
                        }
                    }
                }
            }
            catch { /* 发现失败不阻断启动 */ }

            // 兜底：仅当"既没有可迁移清单、也没有发现到任何运行中实例"时，
            // 预置一个 default 实例（home 空 = 不注入 DSH_HOME，行为与 v0.2.0 一致），
            // 保证 GUI 打开即有可操作的实例。
            if (discovered.Count > 0)
            {
                file.Instances.AddRange(discovered);
            }
            else if (file.Instances.Count == 0)
            {
                file.Instances.Add(new InstanceDef
                {
                    Id = "default",
                    Name = "主实例",
                    Home = "",
                    Host = "127.0.0.1",
                    Port = 3080,
                    Workspace = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    AutoOpenBrowser = true,
                    StopOnExit = true,
                    CreatedAt = DateTime.UtcNow
                });
            }

            foreach (InstanceDef d in file.Instances)
            {
                d.Workspace = Config.SanitizePath(d.Workspace);
                d.Home = Config.SanitizePath(d.Home);
                if (string.IsNullOrWhiteSpace(d.Workspace))
                    d.Workspace = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                if (string.IsNullOrEmpty(d.Host)) d.Host = "127.0.0.1";
                if (d.Port < 1 || d.Port > 65535) d.Port = 3080;
                if (string.IsNullOrEmpty(d.Id))
                    d.Id = Guid.NewGuid().ToString("N").Substring(0, 8);
                if (string.IsNullOrEmpty(d.Name)) d.Name = d.Id;
            }
            return new InstanceRegistry(file);
        }

        public void Save()
        {
            try
            {
                var opts = new JsonSerializerOptions { WriteIndented = true };
                string tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(_file, opts), new UTF8Encoding(false));
                File.Move(tmp, FilePath, overwrite: true);
            }
            catch
            {
                // 保存失败不影响运行（与 legacy 行为一致）
            }
        }

        public InstanceDef Get(string id)
        {
            InstanceDef d = _file.Instances.FirstOrDefault(
                x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
            if (d == null) throw new InvalidOperationException("实例不存在: " + id);
            return d;
        }

        public bool TryGet(string id, out InstanceDef def)
        {
            def = _file.Instances.FirstOrDefault(
                x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
            return def != null;
        }

        public void Add(InstanceDef def)
        {
            if (!IsValidId(def.Id))
                throw new ArgumentException("实例 ID 非法（仅允许字母/数字/_/-，1–64 字符）: " + def.Id);
            if (TryGet(def.Id, out _))
                throw new ArgumentException("实例 ID 已存在: " + def.Id);
            _file.Instances.Add(def);
        }

        public void Update(InstanceDef def)
        {
            int idx = _file.Instances.FindIndex(
                x => string.Equals(x.Id, def.Id, StringComparison.OrdinalIgnoreCase));
            if (idx < 0) throw new InvalidOperationException("实例不存在: " + def.Id);
            _file.Instances[idx] = def;
        }

        public bool Remove(string id)
        {
            return _file.Instances.RemoveAll(
                x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase)) > 0;
        }

        public static bool IsValidId(string id)
        {
            if (string.IsNullOrEmpty(id) || id.Length > 64) return false;
            foreach (char c in id)
            {
                if (!(char.IsLetterOrDigit(c) || c == '_' || c == '-')) return false;
            }
            return true;
        }

        private static JsonSerializerOptions JsonOpts()
        {
            return new JsonSerializerOptions
            {
                AllowTrailingCommas = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                PropertyNameCaseInsensitive = true
            };
        }
    }
}
