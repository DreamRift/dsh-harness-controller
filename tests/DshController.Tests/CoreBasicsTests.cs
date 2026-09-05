// ============================================================================
//  Core 基础行为的离线单测（重构 2.0 / P0.7 首批）
//
//  只依赖 DshController.Core：不起进程、不联网、不碰真实实例 HOME。
//  用途：把原本只能靠 CLI 自检（需真实 dsh/端口/WSL）覆盖的纯逻辑，
//  前移到毫秒级、可在任何机器上重复运行的回路。
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using DshController.Core;
using Xunit;

namespace DshController.Tests
{
    public class ConfigSanitizeTests
    {
        [Fact]
        public void 多重反斜杠收敛为单个()
        {
            string polluted = "C:" + new string('\\', 6) + "Users" + new string('\\', 2) + "test";
            Assert.Equal(@"C:\Users\test", Config.SanitizePath(polluted));
        }

        [Fact]
        public void UNC前导双反斜杠保留()
        {
            string polluted = new string('\\', 4) + "server" + new string('\\', 2) + "share";
            Assert.Equal(@"\\server\share", Config.SanitizePath(polluted));
        }

        [Fact]
        public void 干净路径与空值原样返回()
        {
            Assert.Equal(@"C:\Users\test", Config.SanitizePath(@"C:\Users\test"));
            Assert.Equal("", Config.SanitizePath(""));
            Assert.Null(Config.SanitizePath(null));
        }
    }

    public class HarnessVersionParseTests
    {
        [Theory]
        [InlineData("0.5.1", "0.5.1")]
        [InlineData("dsh 0.5.1", "0.5.1")]
        [InlineData("0.1.1-rc.2", "0.1.1-rc.2")]
        [InlineData("no version here", "")]
        [InlineData("", "")]
        public void 从任意输出提取语义化版本(string input, string expected)
        {
            Assert.Equal(expected, HarnessVersion.Parse(input));
        }

        [Theory]
        [InlineData("v0.5.1", true, "0.5.1")]
        [InlineData(" 0.5.1 ", true, "0.5.1")]
        [InlineData("", true, "")]
        [InlineData("默认（跟随环境）", true, "")]
        [InlineData("0.5.1（当前环境主实例版本）", true, "0.5.1")]
        [InlineData("abc", false, "")]
        [InlineData("dsh 0.5.1", false, "")]
        public void 规范化用户输入的版本号(string input, bool ok, string normalized)
        {
            Assert.Equal(ok, HarnessVersion.TryNormalizeVersion(input, out string actual));
            Assert.Equal(normalized, actual);
        }
    }

    public class InstanceDefTests
    {
        [Fact]
        public void ToConfig对越界与空值做防御性归一()
        {
            var def = new InstanceDef { Id = "x", Name = "X", Port = 70000, Host = "", Workspace = "" };
            Config cfg = def.ToConfig(new AppSettings());

            Assert.Equal(3080, cfg.Port);
            Assert.Equal("127.0.0.1", cfg.Host);
            Assert.False(string.IsNullOrWhiteSpace(cfg.Workspace));
            Assert.Empty(cfg.TrustedHosts);
        }

        [Fact]
        public void ToConfig回填全局设置()
        {
            var def = new InstanceDef { Id = "x", Runtime = "wsl", WslDistro = "Ubuntu" };
            var settings = new AppSettings { DshCommand = @"C:\tools\dsh.cmd", WslShutdownPolicy = "never" };
            Config cfg = def.ToConfig(settings);

            Assert.True(cfg.IsWsl);
            Assert.Equal(@"C:\tools\dsh.cmd", cfg.DshCommand);
            Assert.Equal("never", cfg.WslShutdownPolicy);
            Assert.Equal("x", cfg.InstanceId);
        }

        [Fact]
        public void IsWsl忽略大小写()
        {
            Assert.True(new InstanceDef { Runtime = "WSL" }.IsWsl);
            Assert.False(new InstanceDef { Runtime = "windows" }.IsWsl);
        }

        [Fact]
        public void 新建实例默认工作区按环境回退旧字段()
        {
            var s = new AppSettings { NewInstanceWorkspace = "legacy", NewInstanceWorkspaceWin = "win" };
            Assert.Equal("win", s.EffectiveNewInstanceWorkspaceFor(wsl: false));
            Assert.Equal("legacy", s.EffectiveNewInstanceWorkspaceFor(wsl: true));
        }
    }

    public class PluginRecordsTests : IDisposable
    {
        private readonly string _dir;

        public PluginRecordsTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "dsh-tests-records-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            PluginRecords.OverrideDir = _dir;
        }

        public void Dispose()
        {
            PluginRecords.OverrideDir = null;
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }
            catch { /* 理由: 临时目录清理失败不应让测试结果失真 */ }
        }

        [Fact]
        public void Upsert按包名幂等且新记录置顶()
        {
            PluginRecords.Upsert("inst", new PluginRecord { Pkg = "a", Version = "1.0.0" });
            PluginRecords.Upsert("inst", new PluginRecord { Pkg = "b", Version = "2.0.0" });
            PluginRecords.Upsert("inst", new PluginRecord { Pkg = "a", Version = "1.1.0" });

            List<PluginRecord> list = PluginRecords.Load("inst");
            Assert.Equal(2, list.Count);
            Assert.Equal("a", list[0].Pkg);
            Assert.Equal("1.1.0", PluginRecords.Find(list, "A").Version);
        }

        [Fact]
        public void 记录按实例分文件互不串台()
        {
            PluginRecords.Upsert("inst-1", new PluginRecord { Pkg = "only-in-1" });
            Assert.Single(PluginRecords.Load("inst-1"));
            Assert.Empty(PluginRecords.Load("inst-2"));
        }

        [Fact]
        public void Remove幂等且返回是否命中()
        {
            PluginRecords.Upsert("inst", new PluginRecord { Pkg = "a" });
            Assert.True(PluginRecords.Remove("inst", "A"));
            Assert.False(PluginRecords.Remove("inst", "a"));
            Assert.Empty(PluginRecords.Load("inst"));
        }

        [Fact]
        public void 损坏的记录文件回退空表而不抛()
        {
            Directory.CreateDirectory(_dir);
            File.WriteAllText(Path.Combine(_dir, "broken.json"), "{ this is not json ");
            Assert.Empty(PluginRecords.Load("broken"));
        }
    }
}
