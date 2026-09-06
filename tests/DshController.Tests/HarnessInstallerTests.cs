// ============================================================================
//  HarnessInstaller 离线单测：
//  只测纯函数 BuildInstallCommand（命令拼接 + 版本白名单）与两条通道的
//  入参守卫（非法版本/缺发行版在触碰 npm/网络之前就返回 false）。
//  绝不起真实 npm 进程、不联网、不进 WSL。
// ============================================================================

using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using DshController.Core;
using Xunit;

namespace DshController.Tests
{
    public class HarnessInstallerTests
    {
        /// <summary>合法安装命令的形状：npm install -g @deepseek-ai/dsh@&lt;白名单版本&gt;，别无其它。</summary>
        private static readonly Regex CmdShapeRx = new Regex(
            @"^npm install -g @deepseek-ai/dsh@[0-9A-Za-z.\-]+$", RegexOptions.Compiled);

        // ---------------- 正常拼接 ----------------

        [Fact]
        public void 正常版本_拼接安装命令()
        {
            string cmd = HarnessInstaller.BuildInstallCommand("0.1.1");
            Assert.Equal("npm install -g @deepseek-ai/dsh@0.1.1", cmd);
        }

        [Fact]
        public void 带rc预发布后缀_原样拼接()
        {
            string cmd = HarnessInstaller.BuildInstallCommand("0.1.1-rc.2");
            Assert.Equal("npm install -g @deepseek-ai/dsh@0.1.1-rc.2", cmd);
        }

        [Fact]
        public void 前缀v_规范化后拼接()
        {
            Assert.Equal("npm install -g @deepseek-ai/dsh@0.1.2", HarnessInstaller.BuildInstallCommand("v0.1.2"));
            Assert.Equal("npm install -g @deepseek-ai/dsh@1.2.3", HarnessInstaller.BuildInstallCommand(" V1.2.3 "));
        }

        [Fact]
        public void 带中文说明后缀_剥出纯版本()
        {
            // TryNormalizeVersion 既有语义：接受"版本号开头"的输入并剥出纯版本（下拉文案兼容）
            Assert.Equal("npm install -g @deepseek-ai/dsh@0.1.0-rc.7",
                HarnessInstaller.BuildInstallCommand("0.1.0-rc.7（当前环境主实例版本）"));
        }

        [Fact]
        public void 首尾空白_修剪后拼接()
        {
            Assert.Equal("npm install -g @deepseek-ai/dsh@0.2.0", HarnessInstaller.BuildInstallCommand("  0.2.0  "));
        }

        // ---------------- 非法版本：一律返回空串（不含安装命令） ----------------

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("a b")]                // 空白 → cmd 参数拆断面
        [InlineData("0.1.0 extra")]        // 垃圾尾巴（ASCII 说明）→ 整条拒绝
        [InlineData("a&b")]
        [InlineData("0.1.0&calc")]
        [InlineData("0.1.0;rm")]
        [InlineData("0.1.0|foo")]
        [InlineData("0.1.0>out")]
        [InlineData("0.1.0 & calc")]
        [InlineData("0.1.0 - rc.2")]
        [InlineData("0.1.0（当前）extra")] // "（说明）"后缀必须闭合收尾
        [InlineData("--help")]
        [InlineData("-rc.1")]
        [InlineData(".1.2.3")]
        [InlineData("abc")]
        [InlineData("默认")]
        [InlineData("跟随当前环境")]
        public void 非法版本_返回空串不含安装命令(string version)
        {
            string cmd = HarnessInstaller.BuildInstallCommand(version);
            Assert.Equal("", cmd);
            Assert.DoesNotContain("install", cmd ?? "");
        }

        // ---------------- 白名单形状：合法输出绝不含 cmd 元字符 ----------------

        [Theory]
        [InlineData("0.1.1")]
        [InlineData("0.1.1-rc.2")]
        [InlineData("10.20.30-beta.11.2")]
        public void 合法输出_完全落在白名单形状内(string version)
        {
            string cmd = HarnessInstaller.BuildInstallCommand(version);
            Assert.Matches(CmdShapeRx, cmd);
            Assert.DoesNotContain("&", cmd);
            Assert.DoesNotContain("|", cmd);
            Assert.DoesNotContain(";", cmd);
            Assert.DoesNotContain("<", cmd);
            Assert.DoesNotContain(">", cmd);
            Assert.DoesNotContain(" ", cmd.Replace("npm ", "npm").Replace("install ", "install")
                .Replace("-g ", "-g"));    // 去掉命令自身的三个分隔空格后不应再有空白
        }

        [Fact]
        public void 空格与元字符尾巴_整条拒绝不进命令()
        {
            // 版本号之后的任何垃圾尾巴（空格/&/;/|/>）都必须让整条输入被拒绝，
            // 绝不允许被"悄悄剥掉"后照常安装——注入面为零。
            Assert.Equal("", HarnessInstaller.BuildInstallCommand("a b"));
            Assert.Equal("", HarnessInstaller.BuildInstallCommand("0.1.0 & calc"));
            Assert.Equal("", HarnessInstaller.BuildInstallCommand("0.1.0&calc"));
            Assert.Equal("", HarnessInstaller.BuildInstallCommand("0.1.0;rm -rf /"));
            Assert.Equal("", HarnessInstaller.BuildInstallCommand("0.1.0 extra"));
        }

        // ---------------- 通道守卫：在触碰 npm/WSL 之前就拒绝（离线安全） ----------------

        [Fact]
        public async Task Windows通道_非法版本_直接失败不找npm()
        {
            string seen = "";
            bool ok = await HarnessInstaller.UpgradeWindowsAsync("a&b", l => seen += l + "\n");
            Assert.False(ok);
            Assert.Contains("升级失败", seen);
        }

        [Fact]
        public async Task Windows通道_空版本_直接失败()
        {
            var logs = new System.Collections.Generic.List<string>();
            bool ok = await HarnessInstaller.UpgradeWindowsAsync("", logs.Add);
            Assert.False(ok);
            Assert.Contains(logs, l => l.Contains("版本号为空"));
        }

        [Fact]
        public async Task Wsl通道_非法版本_直接失败不进发行版()
        {
            var logs = new System.Collections.Generic.List<string>();
            bool ok = await HarnessInstaller.UpgradeWslAsync("Ubuntu-24.04", "--help", logs.Add);
            Assert.False(ok);
            Assert.Contains(logs, l => l.Contains("升级失败"));
        }

        [Fact]
        public async Task Wsl通道_缺发行版_直接失败()
        {
            var logs = new System.Collections.Generic.List<string>();
            bool ok = await HarnessInstaller.UpgradeWslAsync("", "0.1.1", logs.Add);
            Assert.False(ok);
            Assert.Contains(logs, l => l.Contains("发行版"));
        }
    }
}
