// ============================================================================
//  显示名单源解析器（改版·环境端口命名）：环境:端口 形态、WSL 不带发行版名、
//  端口缺省（旧清单无 port 字段 → 默认 3080）、tooltip 原名、档案形态。
// ============================================================================

using System;
using System.Text.Json;
using DshController.Core;
using DshController.Core.Archive;
using DshController.ViewModels;
using Xunit;

namespace DshController.Tests
{
    public class InstanceDisplayNameTests
    {
        [Fact]
        public void Windows形态_环境冒号端口()
        {
            var def = new InstanceDef { Id = "a", Name = "主实例", Port = 3080, Runtime = "windows" };
            Assert.Equal("windows:3080", InstanceDisplayName.For(def));
        }

        [Fact]
        public void Wsl形态不带发行版名()
        {
            var def = new InstanceDef { Id = "s", Name = "Ubuntu 实例", Port = 3081, Runtime = "wsl", WslDistro = "Ubuntu-26.04" };
            Assert.Equal("wsl:3081", InstanceDisplayName.For(def));
            Assert.DoesNotContain("Ubuntu", InstanceDisplayName.For(def));
        }

        [Fact]
        public void 端口缺省_旧清单无port字段落默认3080()
        {
            var parsed = JsonSerializer.Deserialize<InstanceDef>("{\"id\":\"old\",\"name\":\"旧\",\"runtime\":\"windows\"}");
            Assert.Equal(3080, parsed.Port);                                  // InstanceDef 默认值兜住缺省
            Assert.Equal("windows:3080", InstanceDisplayName.For(parsed));
        }

        [Fact]
        public void 端口非法时降级只留环境段()
        {
            Assert.Equal("windows", InstanceDisplayName.For(new InstanceDef { Id = "z", Port = 0 }));
            Assert.Equal("wsl", InstanceDisplayName.For(new InstanceDef { Id = "z", Port = -1, Runtime = "wsl" }));
        }

        [Fact]
        public void 空与脏输入不炸()
        {
            Assert.Equal("", InstanceDisplayName.For(null));
            Assert.Equal("", InstanceDisplayName.TooltipFor(null));
            Assert.Equal("", InstanceDisplayName.ForArchive(null));
        }

        [Fact]
        public void Tooltip带原名_WSL附发行版_无名回落Id()
        {
            var win = new InstanceDef { Id = "a", Name = "主实例", Port = 3080 };
            Assert.Equal("原名 主实例", InstanceDisplayName.TooltipFor(win));
            var wsl = new InstanceDef { Id = "s", Name = "", Port = 3081, Runtime = "wsl", WslDistro = "Ubuntu" };
            Assert.Equal("原名 s · Ubuntu", InstanceDisplayName.TooltipFor(wsl));
            Assert.Equal("s", InstanceDisplayName.Original(wsl));             // 名字空 → Id
        }

        [Fact]
        public void 档案侧同形态_取最新代际端口与原名()
        {
            var def = new InstanceDef { Id = "w", Name = "主实例", Port = 3080, Runtime = "windows" };
            var a = InstanceArchive.CreateFor(def, new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc));
            Assert.Equal("windows:3080", InstanceDisplayName.ForArchive(a));
            Assert.Equal("原名 主实例", InstanceDisplayName.TooltipForArchive(a));
        }

        [Fact]
        public void 档案无代际时只回环境段()
        {
            var a = new InstanceArchive { ArchiveId = "x", Runtime = "wsl" };
            Assert.Equal("wsl", InstanceDisplayName.ForArchive(a));
        }

        [Fact]
        public void 别名_覆盖清单与档案显示名()
        {
            try
            {
                InstanceDisplayName.SetAlias("al", "工作机");
                var def = new InstanceDef { Id = "al", Name = "原名", Port = 3080, Runtime = "windows" };
                Assert.Equal("工作机 (windows:3080)", InstanceDisplayName.For(def));
                var a = InstanceArchive.CreateFor(def, DateTime.UtcNow);
                Assert.Equal("工作机 (windows:3080)", InstanceDisplayName.ForArchive(a));
                // tooltip 仍保原名
                Assert.Equal("原名 原名", InstanceDisplayName.TooltipForArchive(a));
            }
            finally
            {
                InstanceDisplayName.SetAlias("al", "");   // 静态表清干净，不污染其他测试
            }
        }

        [Fact]
        public void 别名清除后回落环境端口()
        {
            InstanceDisplayName.SetAlias("al2", "临时");
            var def = new InstanceDef { Id = "al2", Name = "x", Port = 3080, Runtime = "windows" };
            Assert.Equal("临时 (windows:3080)", InstanceDisplayName.For(def));
            InstanceDisplayName.SetAlias("al2", "");
            Assert.Equal("windows:3080", InstanceDisplayName.For(def));
        }

        [Fact]
        public void 别名大小写不敏感()
        {
            try
            {
                InstanceDisplayName.SetAlias("MIX", "大写的");
                var def = new InstanceDef { Id = "mix", Port = 3080, Runtime = "windows" };
                Assert.Equal("大写的 (windows:3080)", InstanceDisplayName.For(def));
            }
            finally
            {
                InstanceDisplayName.SetAlias("MIX", "");
            }
        }
    }
}
