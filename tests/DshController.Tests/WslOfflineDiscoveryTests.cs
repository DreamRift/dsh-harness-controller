// ============================================================================
//  WSL 离线发现纯逻辑单测（已注册未运行发行版扫描 / 离线拉起探测前置）
//
//  只测纯函数：PowerShell 注册表枚举输出解析（ParseRegisteredDistroLines）、
//  离线拉起候选差集（SubtractCandidates）。
//  绝不起 wsl.exe / powershell 进程、不读真实注册表、不联网。
// ============================================================================

using System;
using System.Collections.Generic;
using DshController.Core;
using Xunit;

namespace DshController.Tests
{
    /// <summary>ParseRegisteredDistroLines：每行 "名称|BasePath" → 发行版名列表。</summary>
    public class WslRegisteredDistroParseTests
    {
        [Fact]
        public void 正常两行解析出两个发行版名并保持顺序()
        {
            string output = "Ubuntu-26.04|C:\\yinyong\\WSL2\nDebian|C:\\wsl\\Debian\n";
            Assert.Equal(new[] { "Ubuntu-26.04", "Debian" }, WslTools.ParseRegisteredDistroLines(output));
        }

        [Fact]
        public void 空串与null返回空表()
        {
            Assert.Empty(WslTools.ParseRegisteredDistroLines(""));
            Assert.Empty(WslTools.ParseRegisteredDistroLines(null));
        }

        [Fact]
        public void 无竖线行与空行跳过()
        {
            // 模拟 PowerShell 报错噪声混进 stdout：无 '|' 的行一律不认
            string output = "\n   \nGet-ChildItem : 找不到路径\nWARNING: something\nUbuntu-26.04|C:\\x\n";
            Assert.Equal(new[] { "Ubuntu-26.04" }, WslTools.ParseRegisteredDistroLines(output));
        }

        [Fact]
        public void 容忍CRLF与BOM()
        {
            string output = "\uFEFFUbuntu-26.04|C:\\yinyong\\WSL2\r\nDebian|C:\\wsl\\Debian\r\n";
            Assert.Equal(new[] { "Ubuntu-26.04", "Debian" }, WslTools.ParseRegisteredDistroLines(output));
        }

        [Fact]
        public void 名称两侧空白被修剪()
        {
            Assert.Equal(new[] { "Ubuntu-26.04" }, WslTools.ParseRegisteredDistroLines("  Ubuntu-26.04 |C:\\x\r\n"));
        }

        [Fact]
        public void 空名与非字母数字开头的名称被过滤()
        {
            // LooksLikeDistroName 过滤：名称须以字母/数字开头（'|C:\x' 空名、'-x' 符号开头都是噪声）
            string output = "|C:\\noname\n-不是发行版|C:\\y\n_ok_but_no_pipe\n";
            Assert.Empty(WslTools.ParseRegisteredDistroLines(output));
        }
    }

    /// <summary>SubtractCandidates：离线拉起候选 = 已注册 − 正在运行 − 已有运行中后端（OrdinalIgnoreCase）。</summary>
    public class WslOfflineBootCandidateTests
    {
        [Fact]
        public void 减去正在运行的发行版()
        {
            List<string> r = InstanceDiscovery.SubtractCandidates(
                new List<string> { "A", "B", "C" }, new List<string> { "b" }, null);
            Assert.Equal(new[] { "A", "C" }, r);
        }

        [Fact]
        public void 减去已有后端的发行版且忽略大小写()
        {
            List<string> r = InstanceDiscovery.SubtractCandidates(
                new List<string> { "Ubuntu-26.04", "Debian" }, new List<string>(),
                new List<string> { "ubuntu-26.04" });
            Assert.Equal(new[] { "Debian" }, r);
        }

        [Fact]
        public void 顺序保持注册表枚举顺序不重排()
        {
            List<string> r = InstanceDiscovery.SubtractCandidates(
                new List<string> { "Zed", "Alpha", "Mid" }, new List<string>(), null);
            Assert.Equal(new[] { "Zed", "Alpha", "Mid" }, r);
        }

        [Fact]
        public void withBackend为null时按空集合处理()
        {
            List<string> r = InstanceDiscovery.SubtractCandidates(
                new List<string> { "A" }, new List<string>(), null);
            Assert.Single(r);
            Assert.Equal("A", r[0]);
        }

        [Fact]
        public void running为null时按空集合处理且比较忽略大小写()
        {
            List<string> kept = InstanceDiscovery.SubtractCandidates(
                new List<string> { "Debian" }, null, null);
            Assert.Single(kept);

            List<string> removed = InstanceDiscovery.SubtractCandidates(
                new List<string> { "Debian" }, new List<string> { "DEBIAN" }, null);
            Assert.Empty(removed);
        }

        [Fact]
        public void registered为null或全被排除时返回空表()
        {
            Assert.Empty(InstanceDiscovery.SubtractCandidates(null, new List<string>(), null));
            Assert.Empty(InstanceDiscovery.SubtractCandidates(
                new List<string> { "X", "Y" }, new List<string> { "x" }, new List<string> { "y" }));
        }
    }
}
