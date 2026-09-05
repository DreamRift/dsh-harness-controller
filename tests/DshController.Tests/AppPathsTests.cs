// ============================================================================
//  AppPaths / AppVersion 的离线单测（重构 2.0 / P0.4 + P0.5）
//
//  这两个类是"所有落盘位置"与"版本号"的单一事实来源，一旦回归会同时影响
//  实例清单、插件记录、报告与档案，因此优先补齐测试。
// ============================================================================

using System;
using System.IO;
using System.Text.RegularExpressions;
using DshController.Core.Diagnostics;
using DshController.Core.Storage;
using Xunit;

namespace DshController.Tests
{
    /// <summary>
    /// AppPaths.OverrideStateDir 是进程级静态状态，同类内的测试由 xUnit 顺序执行，
    /// 因此把所有涉及它的用例放在同一个类里，避免并行串台。
    /// </summary>
    public class AppPathsTests : IDisposable
    {
        private readonly string _tmp;
        private readonly string _prevOverride;

        public AppPathsTests()
        {
            _prevOverride = AppPaths.OverrideStateDir;
            _tmp = Path.Combine(Path.GetTempPath(), "dsh-tests-paths-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(_tmp);
        }

        public void Dispose()
        {
            AppPaths.OverrideStateDir = _prevOverride;
            try { if (Directory.Exists(_tmp)) Directory.Delete(_tmp, true); }
            catch { /* 理由: 临时目录清理失败不应让测试结果失真 */ }
        }

        [Fact]
        public void 覆盖状态目录后所有子路径跟随()
        {
            string state = Path.Combine(_tmp, "state");
            AppPaths.OverrideStateDir = state;

            Assert.Equal(state, AppPaths.StateDir);
            Assert.Equal(Path.Combine(state, "instances.json"), AppPaths.InstancesFile);
            Assert.Equal(Path.Combine(state, "launcher.json"), AppPaths.LegacyLauncherFile);
            Assert.Equal(Path.Combine(state, "archives"), AppPaths.ArchivesDir);
            Assert.Equal(Path.Combine(state, "plugin-records"), AppPaths.PluginRecordsDir);
            Assert.Equal(Path.Combine(state, "plugin-cache"), AppPaths.PluginCacheDir);
            Assert.Equal(Path.Combine(state, "logs"), AppPaths.LogsDir);
            Assert.Equal(Path.Combine(state, "instances"), AppPaths.DefaultHomeRoot);
            Assert.False(AppPaths.IsPortable);   // 覆盖状态下便携判定必须让位
        }

        [Fact]
        public void 默认状态目录在用户级LocalAppData下()
        {
            AppPaths.OverrideStateDir = null;
            string expect = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DshController");
            // 便携模式（exe 旁有 portable.marker）下不适用，测试环境不会有该标记
            if (!AppPaths.IsPortable) Assert.Equal(expect, AppPaths.StateDir);
        }

        [Fact]
        public void 默认DSH_HOME与报告目录不随状态目录漂移()
        {
            AppPaths.OverrideStateDir = Path.Combine(_tmp, "state");
            Assert.Equal(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh"),
                AppPaths.DefaultDshHome);
            Assert.EndsWith(Path.Combine("DshController", "error-reports"), AppPaths.DefaultReportDir);
        }

        [Fact]
        public void 迁移复制instances并保留源文件()
        {
            string src = Path.Combine(_tmp, "exe");
            string dst = Path.Combine(_tmp, "state");
            Directory.CreateDirectory(src);
            File.WriteAllText(Path.Combine(src, "instances.json"), "{\"version\":2}");

            AppPaths.MigrationResult r = AppPaths.MigrateFrom(src, dst);

            Assert.True(r.Migrated);
            Assert.Equal("", r.Error);
            Assert.True(File.Exists(Path.Combine(dst, "instances.json")));
            Assert.True(File.Exists(Path.Combine(src, "instances.json")));   // 源文件保留，可回退
        }

        [Fact]
        public void 目标已存在时迁移是空操作()
        {
            string src = Path.Combine(_tmp, "exe");
            string dst = Path.Combine(_tmp, "state");
            Directory.CreateDirectory(src);
            Directory.CreateDirectory(dst);
            File.WriteAllText(Path.Combine(src, "instances.json"), "SOURCE");
            File.WriteAllText(Path.Combine(dst, "instances.json"), "TARGET");

            Assert.False(AppPaths.MigrateFrom(src, dst).Migrated);
            Assert.Equal("TARGET", File.ReadAllText(Path.Combine(dst, "instances.json")));
        }

        [Fact]
        public void 只有旧版launcher时迁移它()
        {
            string src = Path.Combine(_tmp, "exe");
            string dst = Path.Combine(_tmp, "state");
            Directory.CreateDirectory(src);
            File.WriteAllText(Path.Combine(src, "launcher.json"), "{}");

            Assert.True(AppPaths.MigrateFrom(src, dst).Migrated);
            Assert.True(File.Exists(Path.Combine(dst, "launcher.json")));
            Assert.False(File.Exists(Path.Combine(dst, "instances.json")));
        }

        [Fact]
        public void 无旧文件时不产生任何东西()
        {
            string src = Path.Combine(_tmp, "exe");
            string dst = Path.Combine(_tmp, "state");
            Directory.CreateDirectory(src);

            AppPaths.MigrationResult r = AppPaths.MigrateFrom(src, dst);

            Assert.False(r.Migrated);
            Assert.False(Directory.Exists(dst));
        }
    }

    public class AppVersionTests
    {
        [Fact]
        public void 版本号形如语义化版本且非空()
        {
            string v = AppVersion.Current;
            Assert.False(string.IsNullOrWhiteSpace(v));
            Assert.Matches(new Regex(@"^\d+\.\d+\.\d+"), v);
        }

        [Fact]
        public void 版本号带缓存且稳定()
        {
            Assert.Equal(AppVersion.Current, AppVersion.Current);
        }
    }
}
