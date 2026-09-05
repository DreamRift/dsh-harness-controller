// ============================================================================
//  AppPaths — 所有落盘位置的单一事实来源（重构 2.0 / P0.4 + P0.5）
//
//  历史问题：
//    - instances.json 写在 exe 旁（AppContext.BaseDirectory），于是每个构建产物
//      / 每份拷贝都各有一套实例清单，换个目录运行就"实例全没了"（本机实测存在
//      5 份互不相通的 instances.json）；
//    - 默认报告目录、默认 HOME 根、~/.dsh 等常量在 6+ 处各写一遍。
//  现在：
//    - 状态统一落在 %LOCALAPPDATA%\DshController\（StateDir）；
//    - exe 旁存在 portable.marker 时回到"便携模式"（状态跟着 exe 走）；
//    - 首次运行自动从 exe 旁导入旧的 instances.json / launcher.json（只复制不删除）。
//  自检与单测经 OverrideStateDir 隔离，绝不触碰用户真实状态。
// ============================================================================

using System;
using System.IO;

namespace DshController.Core.Storage
{
    public static class AppPaths
    {
        /// <summary>便携模式标记文件名（放在 exe 旁即启用）。</summary>
        public const string PortableMarker = "portable.marker";

        /// <summary>自检/单测隔离用：非空时一切状态路径都落到该目录。</summary>
        public static string OverrideStateDir;

        public static string ExeDir => AppContext.BaseDirectory;

        /// <summary>便携模式：状态跟随 exe 目录（U 盘/免安装场景）。</summary>
        public static bool IsPortable
        {
            get
            {
                if (!string.IsNullOrEmpty(OverrideStateDir)) return false;
                try { return File.Exists(Path.Combine(ExeDir, PortableMarker)); }
                catch { return false; /* 理由: 探测失败时按非便携处理，用户级目录始终可写 */ }
            }
        }

        /// <summary>状态根目录：覆盖 &gt; 便携 &gt; %LOCALAPPDATA%\DshController。</summary>
        public static string StateDir
        {
            get
            {
                if (!string.IsNullOrEmpty(OverrideStateDir)) return OverrideStateDir;
                if (IsPortable) return ExeDir;
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "DshController");
            }
        }

        public static string InstancesFile => Path.Combine(StateDir, "instances.json");
        public static string LegacyLauncherFile => Path.Combine(StateDir, "launcher.json");
        public static string ArchivesDir => Path.Combine(StateDir, "archives");
        public static string PluginRecordsDir => Path.Combine(StateDir, "plugin-records");
        /// <summary>升级忽略台账（改版·插件升级治理）：记录 (实例, 包, 被忽略的目标版本)。</summary>
        public static string UpgradeIgnoresFile => Path.Combine(StateDir, "upgrade-ignores.json");
        /// <summary>实例别名台账（改版·改名入口）：记录 (archiveId, 别名) 映射。</summary>
        public static string AliasesFile => Path.Combine(StateDir, "aliases.json");
        /// <summary>模型供应商预设台账（改版·API 预设页）：全局预设，非实例级。</summary>
        public static string ProviderPresetsFile => Path.Combine(StateDir, "api-presets.json");
        public static string PluginCacheDir => Path.Combine(StateDir, "plugin-cache");
        public static string LogsDir => Path.Combine(StateDir, "logs");

        /// <summary>报告写入失败时的兜底目录。</summary>
        public static string ReportsFallbackDir => Path.Combine(StateDir, "reports");

        /// <summary>默认错误报告目录（用户可在设置中改）。</summary>
        public static string DefaultReportDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "DshController", "error-reports");

        /// <summary>新建实例 DSH_HOME 的默认存放根。</summary>
        public static string DefaultHomeRoot => Path.Combine(StateDir, "instances");

        /// <summary>不注入 DSH_HOME 时 harness 实际使用的默认 HOME。</summary>
        public static string DefaultDshHome => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh");

        public static string EnsureDir(string path)
        {
            try { if (!string.IsNullOrEmpty(path)) Directory.CreateDirectory(path); }
            catch
            {
                // 理由: 目录创建失败由具体写入方决定降级策略（如报告落兜底目录）
            }
            return path;
        }

        // ---------------- 首次运行的状态迁移 ----------------

        public sealed class MigrationResult
        {
            public bool Migrated;
            public string From = "";
            public string Error = "";
        }

        private static bool _migrationDone;

        /// <summary>
        /// 首次运行把 exe 旁的旧状态复制到 StateDir（只复制不删除，旧文件留作回退）。
        /// 已存在新状态、便携模式、或 exe 旁没有旧文件时都是空操作。线程内幂等。
        /// </summary>
        public static MigrationResult MigrateFromExeDirIfNeeded()
        {
            if (_migrationDone || IsPortable || !string.IsNullOrEmpty(OverrideStateDir))
                return new MigrationResult();
            _migrationDone = true;
            return MigrateFrom(ExeDir, StateDir);
        }

        /// <summary>
        /// 迁移的纯逻辑实现（可离线单测）：把 sourceDir 里的旧状态复制到 stateDir。
        /// 规则：目标已有 instances.json → 不动；否则 instances.json 优先，
        /// 只有 launcher.json 时复制它（后续由 InstanceRegistry 做 v1→v2 迁移）；
        /// 源文件一律保留，失败只记录不抛。
        /// </summary>
        public static MigrationResult MigrateFrom(string sourceDir, string stateDir)
        {
            var result = new MigrationResult();
            try
            {
                if (string.IsNullOrEmpty(sourceDir) || string.IsNullOrEmpty(stateDir)) return result;
                string targetInstances = Path.Combine(stateDir, "instances.json");
                string legacyInstances = Path.Combine(sourceDir, "instances.json");
                string legacyLauncher = Path.Combine(sourceDir, "launcher.json");

                if (File.Exists(targetInstances))
                {
                    // 常规情况：已迁移过就不再动。
                    // 例外（真实踩坑场景）：先跑了一次全新构建产物 → 目标是"空清单"，
                    // 之后才把新版本部署到旧目录旁；此时若一律跳过，用户的实例清单
                    // 就永远导不进来。仅当"目标空且源非空"时接管，并先备份目标。
                    if (CountInstances(targetInstances) != 0 || CountInstances(legacyInstances) <= 0) return result;
                    File.Copy(targetInstances,
                        targetInstances + ".replaced-" + DateTime.Now.ToString("yyyyMMddHHmmss"), overwrite: true);
                    File.Delete(targetInstances);
                }
                if (!File.Exists(legacyInstances) && !File.Exists(legacyLauncher)) return result;

                Directory.CreateDirectory(stateDir);
                if (File.Exists(legacyInstances))
                {
                    File.Copy(legacyInstances, targetInstances, overwrite: false);
                    result.From = legacyInstances;
                }
                else
                {
                    string targetLauncher = Path.Combine(stateDir, "launcher.json");
                    if (File.Exists(targetLauncher)) return result;
                    File.Copy(legacyLauncher, targetLauncher, overwrite: false);
                    result.From = legacyLauncher;
                }
                result.Migrated = true;
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;   // 迁移失败不阻断启动：按"全新状态"继续
            }
            return result;
        }

        /// <summary>测试专用：重置迁移一次性标记。</summary>
        public static void ResetMigrationFlagForTest() { _migrationDone = false; }

        /// <summary>
        /// 数一个 instances.json 里的实例条数：文件缺失 -1，解析失败 -2（按"有内容"保守处理），
        /// 否则返回条数。用于判断"目标是否是空清单"。
        /// </summary>
        public static int CountInstances(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return -1;
                using (var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path)))
                {
                    if (doc.RootElement.TryGetProperty("instances", out var arr) &&
                        arr.ValueKind == System.Text.Json.JsonValueKind.Array)
                        return arr.GetArrayLength();
                }
                return -2;
            }
            catch
            {
                return -2;   // 理由: 损坏文件按"有内容"处理，绝不覆盖用户可能仍需修复的清单
            }
        }
    }
}
