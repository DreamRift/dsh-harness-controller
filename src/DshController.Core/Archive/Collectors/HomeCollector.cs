// ============================================================================
//  HomeCollector — 实例 HOME 状态分面（重构 2.0 / P1）
//
//  回答三个界面上反复要用的问题：HOME 在哪、初始化了没（profiles/web/cordis.yml）、
//  占了多少空间。目录遍历较贵（node_modules 动辄上万文件），因此默认 24 小时一次，
//  且体积统计带上限保护：超过阈值即停手并标注"至少 N"，绝不为了一个提示卡住采集线程。
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DshController.Core.Storage;

namespace DshController.Core.Archive.Collectors
{
    public sealed class HomeCollector : IFacetCollector
    {
        /// <summary>体积统计的文件数上限：够用又不至于在超大 HOME 上空转。</summary>
        private const int MaxFilesScanned = 60000;

        /// <summary>体积统计的时间预算：超时即停手并标注 truncated（宁可少算也不拖住采集线程）。</summary>
        private static readonly TimeSpan ScanBudget = TimeSpan.FromMilliseconds(1500);

        public string Facet => FacetNames.Home;
        public bool Persist => true;
        public bool UsesWsl(InstanceContext ctx) => ctx != null && ctx.IsWsl;

        public bool CanRun(InstanceContext ctx, out string skipReason)
        {
            skipReason = "";
            if (ctx?.Def == null) { skipReason = "实例定义缺失"; return false; }
            if (ctx.IsWsl && string.IsNullOrWhiteSpace(ctx.Def.WslDistro))
            {
                skipReason = "WSL 实例未设置发行版";
                return false;
            }
            return true;
        }

        public async Task<FacetResult> CollectAsync(InstanceContext ctx, CancellationToken ct)
        {
            Config cfg = ctx.ToConfig();
            return ctx.IsWsl
                ? await CollectWslAsync(cfg, ct).ConfigureAwait(false)
                : CollectWindows(cfg, ct);
        }

        private static FacetResult CollectWindows(Config cfg, CancellationToken ct)
        {
            string home = string.IsNullOrWhiteSpace(cfg.Home) ? AppPaths.DefaultDshHome : cfg.Home;
            bool exists = Directory.Exists(home);
            bool initialized = exists && File.Exists(Path.Combine(home, "profiles", "web", "cordis.yml"));
            var profiles = new List<string>();
            if (exists)
            {
                try
                {
                    string profilesDir = Path.Combine(home, "profiles");
                    if (Directory.Exists(profilesDir))
                        foreach (string d in Directory.EnumerateDirectories(profilesDir))
                            profiles.Add(Path.GetFileName(d));
                }
                catch
                {
                    // 理由: profile 列表只是展示信息，读不到就留空，不影响其他字段
                }
            }

            long size = 0;
            bool truncated = false;
            if (exists)
            {
                int files = 0;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    foreach (string f in Directory.EnumerateFiles(home, "*", SearchOption.AllDirectories))
                    {
                        ct.ThrowIfCancellationRequested();
                        if (++files > MaxFilesScanned || sw.Elapsed > ScanBudget) { truncated = true; break; }
                        try { size += new FileInfo(f).Length; }
                        catch { /* 理由: 单个文件被占用/权限不足时跳过，不影响总量的量级判断 */ }
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch
                {
                    truncated = true;   // 理由: 目录树中途不可读，已统计部分仍有参考价值
                }
            }

            var data = new Dictionary<string, object>
            {
                ["path"] = home,
                ["exists"] = exists,
                ["initialized"] = initialized,
                ["profiles"] = profiles,
                ["sizeBytes"] = size,
                ["sizeTruncated"] = truncated
            };
            return exists ? FacetResult.Ok(data, "filesystem") : FacetResult.Empty(data, "filesystem");
        }

        private static async Task<FacetResult> CollectWslAsync(Config cfg, CancellationToken ct)
        {
            string distro = cfg.WslDistro ?? "";
            if (!await WslTools.IsDistroRunningAsync(distro).ConfigureAwait(false))
                return FacetResult.Skipped("WSL 发行版未运行（可手动唤醒后刷新）");

            string root = await WslTools.GetDistroHomeAsync(distro).ConfigureAwait(false);
            string home = WslTools.ResolveLinuxPath(cfg.WslHome ?? "", string.IsNullOrEmpty(root) ? "/root" : root);

            // 一次往返拿齐：是否存在 / 是否初始化 / profile 列表 / 体积
            string script =
                "H=" + WslTools.Shq(home) + "; " +
                "if [ -d \"$H\" ]; then echo EXISTS=1; else echo EXISTS=0; fi; " +
                "if [ -f \"$H/profiles/web/cordis.yml\" ]; then echo INIT=1; else echo INIT=0; fi; " +
                "ls -1 \"$H/profiles\" 2>/dev/null | sed 's/^/PROFILE=/'; " +
                "du -sb \"$H\" 2>/dev/null | cut -f1 | sed 's/^/SIZE=/'";
            var r = await WslTools.RunInDistroAsync(distro, script, 60000).ConfigureAwait(false);

            bool exists = false, initialized = false;
            long size = 0;
            var profiles = new List<string>();
            foreach (string line in WslTools.SplitLines(r.Output))
            {
                string s = line.Trim();
                if (s == "EXISTS=1") exists = true;
                else if (s == "INIT=1") initialized = true;
                else if (s.StartsWith("PROFILE=", StringComparison.Ordinal)) profiles.Add(s.Substring(8));
                else if (s.StartsWith("SIZE=", StringComparison.Ordinal))
                    long.TryParse(s.Substring(5), out size);
            }

            var data = new Dictionary<string, object>
            {
                ["path"] = home,
                ["exists"] = exists,
                ["initialized"] = initialized,
                ["profiles"] = profiles,
                ["sizeBytes"] = size,
                ["sizeTruncated"] = false
            };
            if (!r.Ok && !exists) return FacetResult.Failed("读取发行版内 HOME 失败：" + (r.Output ?? "").Trim());
            return exists ? FacetResult.Ok(data, "wsl") : FacetResult.Empty(data, "wsl");
        }
    }
}
