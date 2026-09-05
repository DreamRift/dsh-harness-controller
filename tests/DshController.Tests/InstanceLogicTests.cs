// ============================================================================
//  实例设置校验与新建计划的离线单测（重构 2.0 / P3）
//
//  这两块逻辑原本埋在 InstancePanel 的控件读写里（TryReadSettings 与那个
//  307 行的新建/克隆对话框方法），改一行都得靠手工点界面验证。
// ============================================================================

using System.Collections.Generic;
using System.Linq;
using DshController.Core;
using DshController.ViewModels;
using Xunit;

namespace DshController.Tests
{
    public class InstanceSettingsValidatorTests
    {
        private static InstanceSettingsInput Valid() => new InstanceSettingsInput
        {
            Name = "我的实例",
            Host = "127.0.0.1",
            Port = "3080",
            Workspace = @"C:\work",
            HarnessVersion = ""
        };

        [Fact]
        public void 合法输入通过并归一化()
        {
            InstanceSettingsResult r = InstanceSettingsValidator.Validate(Valid());
            Assert.True(r.Ok);
            Assert.Equal(3080, r.Port);
            Assert.Equal("我的实例", r.Name);
        }

        [Theory]
        [InlineData("0")]
        [InlineData("65536")]
        [InlineData("abc")]
        [InlineData("")]
        public void 端口越界或非数字被拒绝(string port)
        {
            InstanceSettingsInput input = Valid();
            input.Port = port;
            Assert.False(InstanceSettingsValidator.Validate(input).Ok);
        }

        [Fact]
        public void 空名称被拒绝()
        {
            InstanceSettingsInput input = Valid();
            input.Name = "   ";
            Assert.Contains("名称", InstanceSettingsValidator.Validate(input).Error);
        }

        [Fact]
        public void 空主机回落回环地址()
        {
            InstanceSettingsInput input = Valid();
            input.Host = "";
            Assert.Equal("127.0.0.1", InstanceSettingsValidator.Validate(input).Host);
        }

        [Theory]
        [InlineData("v0.5.1", "0.5.1")]
        [InlineData("", "")]
        public void 版本号被规范化(string input, string expected)
        {
            InstanceSettingsInput s = Valid();
            s.HarnessVersion = input;
            InstanceSettingsResult r = InstanceSettingsValidator.Validate(s);
            Assert.True(r.Ok);
            Assert.Equal(expected, r.HarnessVersion);
        }

        [Fact]
        public void 非法版本号被拒绝()
        {
            InstanceSettingsInput s = Valid();
            s.HarnessVersion = "不是版本";
            Assert.False(InstanceSettingsValidator.Validate(s).Ok);
        }

        [Fact]
        public void WSL实例必须指定发行版()
        {
            InstanceSettingsInput s = Valid();
            s.IsWsl = true;
            Assert.False(InstanceSettingsValidator.Validate(s).Ok);
            s.WslDistro = "Ubuntu";
            Assert.True(InstanceSettingsValidator.Validate(s).Ok);
        }

        [Fact]
        public void trustedHosts按多种分隔符拆分并去重()
        {
            List<string> hosts = InstanceSettingsValidator.SplitTrustedHosts("a.com, b.com;c.com  a.com\nd.com");
            Assert.Equal(new[] { "a.com", "b.com", "c.com", "d.com" }, hosts);
            Assert.Empty(InstanceSettingsValidator.SplitTrustedHosts(""));
            Assert.Empty(InstanceSettingsValidator.SplitTrustedHosts(null));
        }
    }

    public class InstancePlanFactoryTests
    {
        [Theory]
        [InlineData("Web Dev", "web-dev")]
        [InlineData("  a--b  ", "a--b")]
        [InlineData("我的 实例", "我的-实例")]      // 中文属于 Letter，registry 也接受，保持原样
        [InlineData("", "instance")]
        [InlineData("!!!", "instance")]            // 无可用字符时回退默认种子
        public void 从名称生成合法id(string name, string expectedPrefix)
        {
            string id = InstancePlanFactory.MakeUniqueId(name, new string[0]);
            Assert.StartsWith(expectedPrefix, id);
            Assert.True(InstanceRegistry.IsValidId(id));
        }

        [Fact]
        public void id冲突时追加序号()
        {
            var existing = new[] { "web-dev", "web-dev-2" };
            Assert.Equal("web-dev-3", InstancePlanFactory.MakeUniqueId("Web Dev", existing));
        }

        [Fact]
        public void 新建Windows实例继承来源设置()
        {
            var source = new InstanceDef
            {
                Id = "src", Workspace = @"D:\ws", Host = "0.0.0.0",
                AutoOpenBrowser = false, StopOnExit = false
            };
            var input = new InstancePlanInput { Name = "New", Port = 3090, Source = source };

            InstanceDef def = InstancePlanFactory.Create(input, new AppSettings(), new[] { "src" });

            Assert.Equal("new", def.Id);
            Assert.Equal(3090, def.Port);
            Assert.Equal(@"D:\ws", def.Workspace);          // 工作区继承来源
            Assert.Equal("0.0.0.0", def.Host);
            Assert.False(def.AutoOpenBrowser);
            Assert.False(def.StopOnExit);
            Assert.False(def.IsWsl);
        }

        [Fact]
        public void 全局默认工作区优先于来源实例()
        {
            var settings = new AppSettings { NewInstanceWorkspaceWin = @"E:\default-ws" };
            var input = new InstancePlanInput
            {
                Name = "New", Port = 3090,
                Source = new InstanceDef { Workspace = @"D:\ws" }
            };

            Assert.Equal(@"E:\default-ws",
                InstancePlanFactory.Create(input, settings, new string[0]).Workspace);
        }

        [Fact]
        public void WSL实例使用发行版内默认HOME与工作区()
        {
            var input = new InstancePlanInput { Name = "wsl demo", IsWsl = true, WslDistro = "Ubuntu", Port = 3081 };

            InstanceDef def = InstancePlanFactory.Create(input, new AppSettings(), new string[0]);

            Assert.True(def.IsWsl);
            Assert.Equal("Ubuntu", def.WslDistro);
            Assert.Equal("~/dsh-instances/wsl-demo", def.WslHome);
            Assert.Equal("~/dsh-workspaces/wsl-demo", def.Workspace);
            Assert.Equal("", def.Home);                      // Windows 侧 HOME 不参与
        }

        [Fact]
        public void 显式输入优先于一切默认()
        {
            var input = new InstancePlanInput
            {
                Name = "x", Port = 3082, Workspace = @"C:\explicit", Home = @"C:\home"
            };
            var settings = new AppSettings { NewInstanceWorkspaceWin = @"E:\default" };

            InstanceDef def = InstancePlanFactory.Create(input, settings, new string[0]);

            Assert.Equal(@"C:\explicit", def.Workspace);
            Assert.Equal(@"C:\home", def.Home);
        }
    }
}
