// ============================================================================
//  启动时间打点（改版·实例页）：identity 镜像字段、启动成功路径打点规则、
//  旧清单缺字段迁移（默认空值 + 序列化往返保留）。
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using DshController.Core;
using DshController.Core.Archive;
using Xunit;

namespace DshController.Tests
{
    public class InstanceStartStampTests : IDisposable
    {
        private readonly string _dir;
        private readonly ArchiveStore _store;
        private readonly FakeClock _clock = new FakeClock();
        private readonly ArchiveService _svc;

        public InstanceStartStampTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "dsh-tests-stamp-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            _store = new ArchiveStore(_dir);
            _svc = new ArchiveService(_store, _clock);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }
            catch { /* 理由: 临时目录清理失败不应让测试结果失真 */ }
        }

        private static InstanceDef Win(string home = "C:\\dsh\\h") =>
            new InstanceDef { Id = "w", Name = "W", Home = home, Runtime = "windows" };
        private static InstanceDef Wsl() =>
            new InstanceDef { Id = "s", Name = "S", Runtime = "wsl", WslDistro = "Ubuntu" };

        // ---------------- identity 镜像 ----------------

        [Fact]
        public void 镜像身份带出最后启动时间ISO()
        {
            InstanceDef def = Win();
            def.LastStartedAt = new DateTime(2026, 9, 4, 10, 20, 30, DateTimeKind.Utc);
            _svc.MirrorIdentity(def);

            Assert.True(_svc.Snapshot("w", FacetNames.Identity).TryGetData(out Dictionary<string, object> d));
            Assert.Equal(def.LastStartedAt.Value.ToString("O"), d["lastStartedAt"].ToString());
        }

        [Fact]
        public void 未启动过的实例镜像为空串()
        {
            _svc.MirrorIdentity(Win());

            Assert.True(_svc.Snapshot("w", FacetNames.Identity).TryGetData(out Dictionary<string, object> d));
            Assert.Equal("", d["lastStartedAt"].ToString());
        }

        // ---------------- 打点规则（启动成功路径） ----------------

        [Fact]
        public void 打点_Windows有HOME启动成功()
        {
            InstanceDef def = Win();
            DateTime now = new DateTime(2026, 9, 4, 1, 2, 3, DateTimeKind.Utc);

            Assert.True(InstanceManager.ApplyStartStamp(def, true, now));
            Assert.Equal(now, def.LastStartedAt);
        }

        [Fact]
        public void 打点_WSL成功也打_两类实例同轴可排序()
        {
            InstanceDef def = Wsl();
            DateTime now = new DateTime(2026, 9, 4, 1, 2, 3, DateTimeKind.Utc);

            Assert.True(InstanceManager.ApplyStartStamp(def, true, now));
            Assert.Equal(now, def.LastStartedAt);
        }

        [Fact]
        public void 不打点_启动失败或Windows无HOME()
        {
            InstanceDef a = Win();
            InstanceDef b = Win(home: "");
            DateTime now = new DateTime(2026, 9, 4, 1, 2, 3, DateTimeKind.Utc);

            Assert.False(InstanceManager.ApplyStartStamp(a, false, now));
            Assert.Null(a.LastStartedAt);
            Assert.False(InstanceManager.ApplyStartStamp(b, true, now));
            Assert.Null(b.LastStartedAt);
        }

        [Fact]
        public void 失败不动旧值()
        {
            InstanceDef def = Win();
            DateTime first = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
            def.LastStartedAt = first;

            InstanceManager.ApplyStartStamp(def, false, DateTime.UtcNow);
            Assert.Equal(first, def.LastStartedAt);
        }

        // ---------------- 清单迁移（向前兼容） ----------------

        [Fact]
        public void 旧清单缺字段读默认且往返保留()
        {
            string legacyJson = "{\"version\":2,\"instances\":[{\"id\":\"a\",\"name\":\"旧\",\"port\":3080,\"runtime\":\"windows\"}]}";
            InstancesFile parsed = JsonSerializer.Deserialize<InstancesFile>(legacyJson);
            Assert.Null(parsed.Instances[0].LastStartedAt);            // 缺字段 → 默认（不炸）

            parsed.Instances[0].LastStartedAt = new DateTime(2026, 9, 4, 8, 0, 0, DateTimeKind.Utc);
            InstancesFile back = JsonSerializer.Deserialize<InstancesFile>(JsonSerializer.Serialize(parsed));
            Assert.Equal(parsed.Instances[0].LastStartedAt.Value.ToString("O"),
                         back.Instances[0].LastStartedAt.Value.ToString("O"));   // 往返不丢精度
        }
    }
}
