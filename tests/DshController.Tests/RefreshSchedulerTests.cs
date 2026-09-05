// ============================================================================
//  RefreshPolicy 与 RefreshScheduler 的离线单测（重构 2.0 / P1）
//
//  这两个类决定"什么时候去采集什么"，是"不同信息不同刷新间隔"这条需求的落点，
//  也是取代两个常驻 1 秒轮询定时器的东西，因此必须能被确定性验证。
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
    public class RefreshPolicyTests
    {
        [Fact]
        public void 各分面取到各自的间隔()
        {
            var p = new RefreshPolicy();
            Assert.Equal(TimeSpan.FromSeconds(2), p.TtlFor(FacetNames.Liveness, false, foreground: true));
            Assert.Equal(TimeSpan.FromSeconds(20), p.TtlFor(FacetNames.Liveness, false, foreground: false));
            Assert.Equal(TimeSpan.FromHours(24), p.TtlFor(FacetNames.Harness, false, true));
            Assert.Equal(TimeSpan.FromHours(6), p.TtlFor(FacetNames.Plugins, false, true));
            Assert.Equal(TimeSpan.FromHours(24), p.TtlFor(FacetNames.Home, false, true));
            Assert.Equal(TimeSpan.FromHours(1), p.TtlFor(FacetNames.WslEnv, false, true));
            Assert.Equal(TimeSpan.Zero, p.TtlFor(FacetNames.Identity, false, true));
        }

        [Fact]
        public void 用量按实例是否运行取不同节奏()
        {
            var p = new RefreshPolicy();
            Assert.Equal(TimeSpan.FromMinutes(30), p.TtlFor(FacetNames.Usage, running: true, foreground: true));
            Assert.Equal(TimeSpan.FromHours(6), p.TtlFor(FacetNames.Usage, running: false, foreground: true));
        }

        [Fact]
        public void 间隔设为0表示只手动刷新()
        {
            var p = new RefreshPolicy { PluginsHours = 0 };
            Assert.Null(p.TtlFor(FacetNames.Plugins, false, true));
        }

        [Fact]
        public void 总开关关闭后一切分面都不自动刷新()
        {
            var p = new RefreshPolicy { AutoRefreshEnabled = false };
            foreach (string facet in FacetNames.All)
                Assert.Null(p.TtlFor(facet, true, true));
        }

        [Fact]
        public void 未知分面不参与自动刷新()
        {
            Assert.Null(new RefreshPolicy().TtlFor("nope", false, true));
        }

        [Fact]
        public void 并发上限至少为1()
        {
            var p = new RefreshPolicy { MaxConcurrent = 0, WslMaxConcurrent = -3 };
            Assert.Equal(1, p.EffectiveMaxConcurrent);
            Assert.Equal(1, p.EffectiveWslMaxConcurrent);
        }
    }

    public class RefreshSchedulerTests : IDisposable
    {
        private readonly string _dir;
        private readonly FakeClock _clock = new FakeClock();
        private readonly ArchiveService _svc;
        private readonly List<InstanceDef> _defs = new List<InstanceDef>();
        private readonly AppSettings _settings = new AppSettings();

        public RefreshSchedulerTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "dsh-tests-sched-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            _svc = new ArchiveService(new ArchiveStore(_dir), _clock);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }
            catch { /* 理由: 临时目录清理失败不应让测试结果失真 */ }
        }

        private RefreshScheduler Build(params IFacetCollector[] collectors)
        {
            return new RefreshScheduler(_svc, () => _defs, () => _settings, collectors, _clock);
        }

        private InstanceDef AddInstance(string id)
        {
            var def = new InstanceDef { Id = id, Name = id, Port = 3080 };
            _defs.Add(def);
            _svc.MirrorIdentity(def);
            return def;
        }

        [Fact]
        public async Task 只采集到期的分面()
        {
            AddInstance("a");
            var harness = new FakeCollector(FacetNames.Harness);
            RefreshScheduler s = Build(harness);

            Assert.Equal(1, await s.RunOnceAsync());     // 从未采集 → 到期
            Assert.Equal(1, harness.Calls);

            Assert.Equal(0, await s.RunOnceAsync());     // 24h 内不再采
            Assert.Equal(1, harness.Calls);

            _clock.Advance(TimeSpan.FromHours(25));
            Assert.Equal(1, await s.RunOnceAsync());
            Assert.Equal(2, harness.Calls);
        }

        [Fact]
        public async Task 关闭自动刷新后一轮也不采()
        {
            AddInstance("a");
            _settings.Refresh = new RefreshPolicy { AutoRefreshEnabled = false };
            var harness = new FakeCollector(FacetNames.Harness);

            Assert.Equal(0, await Build(harness).RunOnceAsync());
            Assert.Equal(0, harness.Calls);
        }

        [Fact]
        public async Task 可见实例优先采集()
        {
            AddInstance("bg");
            AddInstance("visible");
            var log = new List<string>();
            var harness = new FakeCollector(FacetNames.Harness) { CallLog = log };
            _settings.Refresh = new RefreshPolicy { MaxConcurrent = 1 };

            RefreshScheduler s = Build(harness);
            s.VisibleInstanceId = "visible";
            await s.RunOnceAsync();

            Assert.Equal(new[] { "visible/harness", "bg/harness" }, log);
        }

        [Fact]
        public async Task WSL采集串行不超预算()
        {
            AddInstance("a");
            AddInstance("b");
            int concurrent = 0, peak = 0;
            var probe = new ProbeCollector(FacetNames.WslEnv, async () =>
            {
                int now = Interlocked.Increment(ref concurrent);
                peak = Math.Max(peak, now);
                await Task.Delay(30).ConfigureAwait(false);
                Interlocked.Decrement(ref concurrent);
            })
            { Wsl = true };
            _settings.Refresh = new RefreshPolicy { MaxConcurrent = 4, WslMaxConcurrent = 1 };

            await Build(probe).RunOnceAsync();

            Assert.Equal(2, probe.Calls);
            Assert.Equal(1, peak);            // 两个 WSL 任务从未同时在跑
        }

        [Fact]
        public async Task 手动刷新忽略新鲜度并落盘()
        {
            AddInstance("a");
            var harness = new FakeCollector(FacetNames.Harness);
            RefreshScheduler s = Build(harness);

            await s.RunOnceAsync();
            FacetSnapshot snap = await s.RefreshNowAsync("a", FacetNames.Harness);

            Assert.Equal(2, harness.Calls);
            Assert.Equal(FacetStatus.Ok, snap.Status);
            Assert.False(_svc.HasPendingWrites);          // 手动刷新后立即落盘
        }

        [Fact]
        public async Task 事件失效后下一轮立即重采()
        {
            AddInstance("a");
            var plugins = new FakeCollector(FacetNames.Plugins);
            RefreshScheduler s = Build(plugins);

            await s.RunOnceAsync();
            Assert.Equal(0, await s.RunOnceAsync());

            s.Invalidate("a", FacetNames.Plugins);        // 例如刚装完一个插件

            Assert.Equal(1, await s.RunOnceAsync());
            Assert.Equal(2, plugins.Calls);
        }

        [Fact]
        public async Task 未知实例或分面的手动刷新安全返回()
        {
            AddInstance("a");
            RefreshScheduler s = Build(new FakeCollector(FacetNames.Harness));

            Assert.Equal(FacetStatus.Never, (await s.RefreshNowAsync("nope", FacetNames.Harness)).Status);
            Assert.Equal(FacetStatus.Never, (await s.RefreshNowAsync("a", "nope")).Status);
        }

        [Fact]
        public async Task 跳过的分面也计时不会每轮空转()
        {
            AddInstance("a");
            var skipped = new FakeCollector(FacetNames.WslEnv) { Runnable = false };
            RefreshScheduler s = Build(skipped);

            Assert.Equal(1, await s.RunOnceAsync());
            Assert.Equal(FacetStatus.Skipped, _svc.Snapshot("a", FacetNames.WslEnv).Status);
            Assert.Equal(0, await s.RunOnceAsync());      // 跳过同样记时间，不再每秒重试
        }
    }

    /// <summary>可观察并发度的采集器。</summary>
    internal sealed class ProbeCollector : IFacetCollector
    {
        private readonly Func<Task> _body;

        public ProbeCollector(string facet, Func<Task> body)
        {
            Facet = facet;
            _body = body;
        }

        public string Facet { get; }
        public bool Persist => true;
        public bool Wsl { get; set; }
        public int Calls;

        public bool UsesWsl(InstanceContext ctx) => Wsl;
        public bool CanRun(InstanceContext ctx, out string skipReason) { skipReason = ""; return true; }

        public async Task<FacetResult> CollectAsync(InstanceContext ctx, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            await _body().ConfigureAwait(false);
            return FacetResult.Ok(new Dictionary<string, object> { ["ok"] = true });
        }
    }
}
