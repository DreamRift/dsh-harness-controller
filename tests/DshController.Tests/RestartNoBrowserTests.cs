// ============================================================================
//  R4 回归单测：重启绝不拉浏览器——子进程侧 --no-open
//
//  根因与链路见 docs/audit-restart-browser.md：dsh web 默认就绪即自开浏览器，
//  控制器侧 SuppressAutoOpen 拦不住，须给子进程传 --no-open。
//  覆盖两个组装点：BackendManager.BuildWebTailArgs（Windows 三分支共用尾参数）、
//  WslLaunch.BuildLaunchScript（发行版内启动脚本）。不起进程、不触端口。
// ============================================================================

using System;
using DshController.Core;
using Xunit;

namespace DshController.Tests
{
    public class RestartNoBrowserTests
    {
        private static Config Cfg(params string[] trusted)
        {
            return new Config { Host = "127.0.0.1", Port = 3185, TrustedHosts = trusted ?? Array.Empty<string>() };
        }

        [Fact]
        public void 重启抑制时尾参数含noOpen()
        {
            string tail = BackendManager.BuildWebTailArgs(Cfg(), suppressAutoOpen: true);
            Assert.Equal(" web --no-open --host 127.0.0.1 --port 3185", tail);
        }

        [Fact]
        public void 正常启动尾参与历史字节一致()
        {
            string tail = BackendManager.BuildWebTailArgs(Cfg(), suppressAutoOpen: false);
            Assert.Equal(" web --host 127.0.0.1 --port 3185", tail);
            Assert.DoesNotContain("--no-open", tail);
        }

        [Fact]
        public void trustedHosts重复传且保持尾置()
        {
            string tail = BackendManager.BuildWebTailArgs(Cfg("10.0.0.2", "corp:8123"), suppressAutoOpen: true);
            Assert.EndsWith(" --trusted-host \"10.0.0.2\" --trusted-host \"corp:8123\"", tail);
            Assert.Contains("web --no-open", tail);
        }

        [Fact]
        public void Wsl脚本跟随环境形态含noOpen()
        {
            string script = WslLaunch.BuildLaunchScript(3185, "/home/u/ws", "/usr/local/bin/dsh",
                Array.Empty<string>(), "", "", noOpen: true);
            int i = script.LastIndexOf("exec ", StringComparison.Ordinal);
            Assert.True(i >= 0);
            string execLine = script.Substring(i).Trim();
            Assert.Equal("exec '/usr/local/bin/dsh' web --no-open --host 127.0.0.1 --port 3185", execLine);
        }

        [Fact]
        public void Wsl脚本指定版本形态含noOpen()
        {
            string script = WslLaunch.BuildLaunchScript(3185, "/home/u/ws", "npx",
                Array.Empty<string>(), "0.1.1-rc.2", "/usr/bin", noOpen: true);
            Assert.Contains("exec npx --yes @deepseek-ai/dsh@0.1.1-rc.2 web --no-open --host 127.0.0.1", script);
        }

        [Fact]
        public void Wsl脚本缺省与历史一致()
        {
            string script = WslLaunch.BuildLaunchScript(3185, "/tmp/ws", "/usr/local/bin/dsh", null);
            Assert.DoesNotContain("--no-open", script);
            Assert.Contains("web --host 127.0.0.1 --port 3185", script);
        }

        [Fact]
        public void Wsl停止模式不受noOpen影响()
        {
            // StopHarnessInDistroAsync 的 pkill 模式与 ps args 校验依赖 --port N（WslLaunch.cs:108/124）
            string script = WslLaunch.BuildLaunchScript(3185, "/tmp/ws", "/usr/local/bin/dsh",
                Array.Empty<string>(), "0.1.1-rc.2", "/usr/bin", noOpen: true);
            int i = script.LastIndexOf("exec ", StringComparison.Ordinal);
            string execLine = script.Substring(i);
            Assert.Contains("--port 3185", execLine);
            Assert.Matches(@"(@deepseek-ai/[d]sh|[d]sh).*--port 3185", execLine);
        }
    }
}