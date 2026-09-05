// ============================================================================
//  UsageCollector — token 用量分面（重构 2.0 / P2）
//
//  两个源合起来才完整：
//    · <HOME>/storages/session_projcache.json —— 权威总量与会话明细（轻、快）；
//    · <HOME>/sessions/**/session.jsonl.zstd —— 按模型/按天明细（重，可能失败）。
//  日志解析失败不影响总量：ModelsComplete=false 如实标注，界面据此提示，
//  而不是让整个分面变成"失败"。这也是"失败不覆盖成功数据"的一部分。
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DshController.Core.Storage;
using DshController.Core.Usage;

namespace DshController.Core.Archive.Collectors
{
    public sealed class UsageCollector : IFacetCollector
    {
        /// <summary>WSL 取内容时每批文件数（命令行长度与单次往返耗时的折中）。</summary>
        private const int WslBatchSize = 40;

        public string Facet => FacetNames.Usage;
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
                : await CollectWindowsAsync(cfg, ct).ConfigureAwait(false);
        }

        private static async Task<FacetResult> CollectWindowsAsync(Config cfg, CancellationToken ct)
        {
            string home = string.IsNullOrWhiteSpace(cfg.Home) ? AppPaths.DefaultDshHome : cfg.Home;
            if (!Directory.Exists(home))
                return FacetResult.Empty(new UsageFacetData { Home = home }, "filesystem");

            string projJson = null;
            string projPath = Path.Combine(home, "storages", "session_projcache.json");
            try { if (File.Exists(projPath)) projJson = File.ReadAllText(projPath, Encoding.UTF8); }
            catch (Exception ex) { return FacetResult.Failed("读取 session_projcache.json 失败：" + ex.Message); }

            var data = new UsageFacetData { Home = home };
            string failure = FillSessions(data, projJson);
            if (failure != null) return FacetResult.Failed(failure);

            try
            {
                UsageScanResult scan = await UsageScanner.ScanWindowsAsync(home, ct).ConfigureAwait(false);
                ApplyScan(data, scan);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                data.ModelsComplete = false;
                data.ModelScanError = ex.Message;
            }
            return Finish(data, "filesystem");
        }

        private static async Task<FacetResult> CollectWslAsync(Config cfg, CancellationToken ct)
        {
            string distro = cfg.WslDistro ?? "";
            if (!await WslTools.IsDistroRunningAsync(distro).ConfigureAwait(false))
                return FacetResult.Skipped("WSL 发行版未运行（可手动唤醒后刷新）");

            string root = await WslTools.GetDistroHomeAsync(distro).ConfigureAwait(false);
            string home = WslTools.ResolveLinuxPath(cfg.WslHome ?? "", string.IsNullOrEmpty(root) ? "/root" : root);
            string projJson = await WslTools
                .ReadDistroFileAsync(distro, home.TrimEnd('/') + "/storages/session_projcache.json")
                .ConfigureAwait(false);

            var data = new UsageFacetData { Home = distro + " 内 " + home };
            string failure = FillSessions(data, projJson);
            if (failure != null) return FacetResult.Failed(failure);

            try
            {
                UsageScanResult scan = await UsageScanner
                    .ScanWslAsync(distro, home, WslBatchSize, ct).ConfigureAwait(false);
                ApplyScan(data, scan);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                data.ModelsComplete = false;
                data.ModelScanError = ex.Message;
            }
            return Finish(data, "wsl");
        }

        /// <summary>把 projcache 内容填进分面数据；返回非 null 表示解析失败的原因。</summary>
        private static string FillSessions(UsageFacetData data, string projJson)
        {
            if (string.IsNullOrWhiteSpace(projJson)) return null;   // 没有总账文件 = 还没产生用量
            if (!UsageParser.TryParseProjCache(projJson, out List<UsageSessionStat> sessions, out string error))
                return "session_projcache.json 解析失败（可能正被 harness 写入）：" + error;

            sessions.Sort((x, y) => y.CreatedAtMs.CompareTo(x.CreatedAtMs));
            data.SessionCount = sessions.Count;
            foreach (UsageSessionStat s in sessions) data.Totals.Add(s.Totals);
            // 明细只留最近 N 条，汇总量不受影响——防止长期使用后档案文件无限膨胀
            data.Sessions = sessions.Take(UsageFacetData.MaxSessions).ToList();
            return null;
        }

        private static void ApplyScan(UsageFacetData data, UsageScanResult scan)
        {
            data.Models = scan.Models ?? new List<UsageModelStat>();
            data.ScannedFiles = scan.Files;
            data.ModelsComplete = scan.Complete;
            data.ModelScanError = scan.Error ?? "";
            data.RequestCount = data.Models.Sum(m => m.Requests);
        }

        private static FacetResult Finish(UsageFacetData data, string source)
        {
            bool empty = data.SessionCount == 0 && data.Models.Count == 0;
            return empty ? FacetResult.Empty(data, source) : FacetResult.Ok(data, source);
        }
    }
}
