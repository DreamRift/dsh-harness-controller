// ============================================================================
//  ArchiveService 的四条不变量（重构 2.0 / P1）
//    1 单飞  2 失败不覆盖成功数据  3 删除不销毁档案  4 落盘去抖
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DshController.Core;
using DshController.Core.Archive;
using Xunit;

namespace DshController.Tests
{
    public class ArchiveServiceTests : IDisposable
    {
        private readonly string _dir;
        private readonly ArchiveStore _store;
        private readonly FakeClock _clock = new FakeClock();
        private readonly ArchiveService _svc;

        public ArchiveServiceTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "dsh-tests-svc-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            _store = new ArchiveStore(_dir);
            _svc = new ArchiveService(_store, _clock);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }
            catch { /* 理由: 临时目录清理失败不应让测试结果失真 */ }
        }

        private static InstanceDef Def(string id = "a", string name = "实例A") =>
            new InstanceDef { Id = id, Name = name, Port = 3080 };

        private InstanceContext Ctx(InstanceDef def = null) =>
            new InstanceContext { Def = def ?? Def(), Settings = new AppSettings() };

        // ---------------- 身份镜像与代际 ----------------

        [Fact]
        public void 镜像身份写入identity分面且零成本()
        {
            _svc.MirrorIdentity(Def());
            FacetSnapshot s = _svc.Snapshot("a", FacetNames.Identity);

            Assert.Equal(FacetStatus.Ok, s.Status);
            Assert.Equal("registry", s.Source);
            Assert.True(s.TryGetData(out Dictionary<string, object> data));
            Assert.Equal("a", data["id"].ToString());
        }

        [Fact]
        public void 删除实例只写退役时间档案文件保留()
        {
            _svc.MirrorIdentity(Def());
            _svc.FlushDirty();
            _clock.Advance(TimeSpan.FromMinutes(5));

            _svc.Retire("a");
            _svc.FlushDirty();

            InstanceArchive back = _store.Read("a");
            Assert.NotNull(back);                       // 文件还在
            Assert.True(back.IsRetired);
            Assert.NotNull(back.Epochs[0].To);          // 这一代已收尾
        }

        [Fact]
        public void 同id重建追加新一代并保留历史()
        {
            _svc.MirrorIdentity(Def(name: "旧名字"));
            _svc.Retire("a");
            _clock.Advance(TimeSpan.FromHours(1));

            _svc.MirrorIdentity(Def(name: "新名字"));
            InstanceArchive a = _svc.Get("a");

            Assert.False(a.IsRetired);
            Assert.Equal(2, a.Epochs.Count);
            Assert.Equal("旧名字", a.Epochs[0].Def.Name);
            Assert.Equal("新名字", a.CurrentEpoch.Def.Name);
        }

        [Fact]
        public void 清单同步会把消失的实例标记退役()
        {
            _svc.MirrorIdentity(Def("a"));
            _svc.MirrorIdentity(Def("b"));
            _svc.FlushDirty();

            _svc.SyncFromRegistry(new[] { Def("a") });

            Assert.False(_svc.Get("a").IsRetired);
            Assert.True(_svc.Get("b").IsRetired);
        }

        // ---------------- 新鲜度 ----------------

        [Fact]
        public async Task 新鲜期内不重复采集过期后才重采()
        {
            var collector = new FakeCollector(FacetNames.Harness);
            TimeSpan ttl = TimeSpan.FromHours(24);

            await _svc.CollectAsync(Ctx(), collector, ttl, force: false);
            await _svc.CollectAsync(Ctx(), collector, ttl, force: false);
            Assert.Equal(1, collector.Calls);

            _clock.Advance(TimeSpan.FromHours(25));
            await _svc.CollectAsync(Ctx(), collector, ttl, force: false);
            Assert.Equal(2, collector.Calls);
        }

        [Fact]
        public async Task 强制刷新忽略新鲜度()
        {
            var collector = new FakeCollector(FacetNames.Harness);
            await _svc.CollectAsync(Ctx(), collector, TimeSpan.FromHours(24), force: false);
            await _svc.CollectAsync(Ctx(), collector, TimeSpan.FromHours(24), force: true);
            Assert.Equal(2, collector.Calls);
        }

        [Fact]
        public void 到期判定的三种边界()
        {
            Assert.True(_svc.IsDue("a", FacetNames.Harness, TimeSpan.FromHours(1)));   // 从未采集
            Assert.False(_svc.IsDue("a", FacetNames.Harness, null));                   // 关闭自动刷新
            _svc.ApplyResult(_svc.GetOrCreate(Def()), FacetNames.Identity, FacetResult.Ok(null), 0);
            Assert.False(_svc.IsDue("a", FacetNames.Identity, TimeSpan.Zero));         // 永远新鲜
        }

        // ---------------- 失败不覆盖 ----------------

        [Fact]
        public async Task 采集失败保留上一次的成功数据()
        {
            var ok = new FakeCollector(FacetNames.Plugins,
                () => FacetResult.Ok(new Dictionary<string, object> { ["count"] = 3 }, "profile"));
            await _svc.CollectAsync(Ctx(), ok, null, force: true);
            _clock.Advance(TimeSpan.FromMinutes(10));

            var bad = new FakeCollector(FacetNames.Plugins, () => FacetResult.Failed("HOME 不可读"));
            FacetSnapshot s = await _svc.CollectAsync(Ctx(), bad, null, force: true);

            Assert.Equal(FacetStatus.Failed, s.Status);
            Assert.Equal("HOME 不可读", s.Error);
            Assert.True(s.HasData);                                   // 旧数据仍在
            Assert.True(s.TryGetData(out Dictionary<string, int> d));
            Assert.Equal(3, d["count"]);
            Assert.True(s.LastGoodAt < s.CollectedAt);                // "上次成功于"保留
        }

        [Fact]
        public async Task 空结果是有效结论会覆盖旧数据()
        {
            var ok = new FakeCollector(FacetNames.Plugins,
                () => FacetResult.Ok(new Dictionary<string, object> { ["count"] = 3 }));
            await _svc.CollectAsync(Ctx(), ok, null, force: true);

            var empty = new FakeCollector(FacetNames.Plugins,
                () => FacetResult.Empty(new Dictionary<string, object> { ["count"] = 0 }));
            FacetSnapshot s = await _svc.CollectAsync(Ctx(), empty, null, force: true);

            Assert.Equal(FacetStatus.Empty, s.Status);
            Assert.True(s.TryGetData(out Dictionary<string, int> d));
            Assert.Equal(0, d["count"]);                              // 卸完插件后不再显示旧列表
        }

        [Fact]
        public async Task 前置条件不满足记录跳过原因且不算失败()
        {
            var collector = new FakeCollector(FacetNames.WslEnv) { Runnable = false, SkipReason = "发行版未运行" };
            FacetSnapshot s = await _svc.CollectAsync(Ctx(), collector, null, force: true);

            Assert.Equal(FacetStatus.Skipped, s.Status);
            Assert.Equal("发行版未运行", s.SkipReason);
            Assert.Equal("", s.Error);
            Assert.Equal(0, collector.Calls);                          // 没有真的去跑
        }

        [Fact]
        public async Task 采集器抛异常被收敛为失败状态()
        {
            var collector = new FakeCollector(FacetNames.Harness, () => throw new InvalidOperationException("炸了"));
            FacetSnapshot s = await _svc.CollectAsync(Ctx(), collector, null, force: true);

            Assert.Equal(FacetStatus.Failed, s.Status);
            Assert.Contains("炸了", s.Error);
        }

        // ---------------- 单飞与落盘 ----------------

        [Fact]
        public async Task 并发请求合并为一次真实采集()
        {
            var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var collector = new FakeCollector(FacetNames.Harness) { Gate = gate };

            Task<FacetSnapshot> t1 = _svc.CollectAsync(Ctx(), collector, null, force: true);
            Task<FacetSnapshot> t2 = _svc.CollectAsync(Ctx(), collector, null, force: true);
            gate.SetResult(true);
            await Task.WhenAll(t1, t2);

            Assert.Equal(1, collector.Calls);
        }

        [Fact]
        public async Task 只有持久化分面才产生落盘()
        {
            var volatileCollector = new FakeCollector(FacetNames.Liveness, persist: false);
            await _svc.CollectAsync(Ctx(), volatileCollector, null, force: true);
            Assert.False(File.Exists(_store.PathFor("a")) && _svc.HasPendingWrites);

            var persistent = new FakeCollector(FacetNames.Harness);
            await _svc.CollectAsync(Ctx(), persistent, null, force: true);
            Assert.True(_svc.HasPendingWrites);
            Assert.Equal(1, _svc.FlushDirty());
            Assert.True(File.Exists(_store.PathFor("a")));
            Assert.False(_svc.HasPendingWrites);
        }

        [Fact]
        public async Task 失效让分面立即到期但保留数据()
        {
            var collector = new FakeCollector(FacetNames.Plugins);
            await _svc.CollectAsync(Ctx(), collector, TimeSpan.FromHours(6), force: false);
            Assert.False(_svc.IsDue("a", FacetNames.Plugins, TimeSpan.FromHours(6)));

            _svc.Invalidate("a", FacetNames.Plugins);

            Assert.True(_svc.IsDue("a", FacetNames.Plugins, TimeSpan.FromHours(6)));
            Assert.True(_svc.Snapshot("a", FacetNames.Plugins).HasData);   // 新数据到达前仍显示旧值
        }

        [Fact]
        public async Task 变更事件带上实例与分面()
        {
            string got = null;
            _svc.Changed += (s, e) => got = e.ArchiveId + "/" + e.Facet + "/" + e.Snapshot.Status;
            await _svc.CollectAsync(Ctx(), new FakeCollector(FacetNames.Harness), null, force: true);

            Assert.Equal("a/harness/ok", got);
        }

        [Fact]
        public void 全部档案包含磁盘历史与内存新建()
        {
            _store.Write(InstanceArchive.CreateFor(Def("disk", "磁盘档案"), _clock.UtcNow));
            _svc.MirrorIdentity(Def("mem", "内存档案"));

            var all = _svc.All();
            Assert.Equal(2, all.Count);
        }
    }
}
