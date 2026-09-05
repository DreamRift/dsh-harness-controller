// ============================================================================
//  档案仓库与快照模型的离线单测（重构 2.0 / P1）
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using DshController.Core;
using DshController.Core.Archive;
using Xunit;

namespace DshController.Tests
{
    public class ArchiveStoreTests : IDisposable
    {
        private readonly string _dir;
        private readonly ArchiveStore _store;

        public ArchiveStoreTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "dsh-tests-arch-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            _store = new ArchiveStore(_dir);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }
            catch { /* 理由: 临时目录清理失败不应让测试结果失真 */ }
        }

        private static InstanceDef Def(string id = "a", bool wsl = false) => new InstanceDef
        {
            Id = id,
            Name = "实例 " + id,
            Port = 3080,
            Runtime = wsl ? "wsl" : "windows",
            WslDistro = wsl ? "Ubuntu" : ""
        };

        [Fact]
        public void 写入后可原样读回()
        {
            InstanceArchive a = InstanceArchive.CreateFor(Def(), DateTime.UtcNow);
            a.Facets[FacetNames.Harness] = new FacetSnapshot { Status = FacetStatus.Ok, Source = "windows" };

            Assert.True(_store.Write(a));
            InstanceArchive back = _store.Read("a");

            Assert.NotNull(back);
            Assert.Equal("a", back.ArchiveId);
            Assert.Equal("实例 a", back.DisplayName);
            Assert.Single(back.Epochs);
            Assert.Equal(FacetStatus.Ok, back.Facet(FacetNames.Harness).Status);
        }

        [Fact]
        public void 分面数据可强类型往返()
        {
            InstanceArchive a = InstanceArchive.CreateFor(Def(), DateTime.UtcNow);
            var service = new ArchiveService(_store, new FakeClock());
            service.ApplyResult(a, FacetNames.Harness,
                FacetResult.Ok(new Dictionary<string, object> { ["version"] = "0.1.1-rc.2" }, "windows"), 12);
            _store.Write(a);

            FacetSnapshot s = _store.Read("a").Facet(FacetNames.Harness);
            Assert.True(s.TryGetData(out Dictionary<string, string> data));
            Assert.Equal("0.1.1-rc.2", data["version"]);
            Assert.Equal(12, s.DurationMs);
        }

        [Fact]
        public void 文件损坏时按没有档案处理()
        {
            Directory.CreateDirectory(_dir);
            File.WriteAllText(Path.Combine(_dir, "broken.json"), "{ not json ");
            Assert.Null(_store.Read("broken"));
        }

        [Fact]
        public void 更高schema的档案按只读处理不解析()
        {
            Directory.CreateDirectory(_dir);
            File.WriteAllText(Path.Combine(_dir, "future.json"),
                "{\"schema\": 99, \"archiveId\": \"future\"}");
            Assert.Null(_store.Read("future"));
        }

        [Fact]
        public void 枚举与删除()
        {
            _store.Write(InstanceArchive.CreateFor(Def("b"), DateTime.UtcNow));
            _store.Write(InstanceArchive.CreateFor(Def("a"), DateTime.UtcNow));

            Assert.Equal(new[] { "a", "b" }, _store.ListIds());
            Assert.Equal(2, _store.ReadAll().Count);

            Assert.True(_store.Delete("a"));
            Assert.Single(_store.ListIds());
            Assert.True(_store.Delete("nonexistent"));   // 删不存在的档案视为成功
        }

        [Fact]
        public void 缺失档案返回空而不是抛()
        {
            Assert.Null(_store.Read("missing"));
            Assert.Empty(_store.ListIds());
            Assert.Empty(_store.ReadAll());
        }

        [Fact]
        public void 空快照的取数语义()
        {
            var s = new FacetSnapshot();
            Assert.Equal(FacetStatus.Never, s.Status);
            Assert.False(s.HasData);
            Assert.False(s.TryGetData(out Dictionary<string, string> _));
        }

        [Fact]
        public void 分面名清单包含全部已知分面()
        {
            Assert.Contains(FacetNames.Usage, FacetNames.All);
            Assert.True(FacetNames.IsKnown("HARNESS"));
            Assert.False(FacetNames.IsKnown("nope"));
        }
    }
}
