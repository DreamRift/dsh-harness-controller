// ============================================================================
//  --archive-check — 实例档案的现场体检（重构 2.0 / P1）
//
//  打印每个实例每个分面的新鲜度（状态 / 采到多久 / 耗时 / 来源 / 错误或跳过原因），
//  并把从未采集过的分面补采一次；--force 则全部重采。
//  离线断言在 dotnet test（176 条），这里负责"在真实机器上确认采集器真的能采到东西"。
//
//  用法：
//    DshController.exe --archive-check [--instance <id>] [--facet <name>] [--force]
// ============================================================================

using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using DshController.Core;
using DshController.Core.Archive;
using DshController.Core.Archive.Collectors;
using DshController.Core.Storage;

namespace DshController.CommandLine
{
    internal static class ArchiveCheck
    {
        public static int Run(string[] args)
        {
            string onlyInstance = null, onlyFacet = null;
            bool force = false;
            for (int i = 1; i < args.Length; i++)
            {
                if (args[i] == "--instance" && i + 1 < args.Length) onlyInstance = args[++i];
                else if (args[i] == "--facet" && i + 1 < args.Length) onlyFacet = FacetNames.Canonical(args[++i]);
                else if (args[i] == "--force") force = true;
            }

            var transcript = new StringBuilder();
            Action<string> Out = line =>
            {
                transcript.AppendLine(line);
                try { Console.WriteLine(line); } catch { /* 理由: 无控制台时静默，转录仍写 cli.log */ }
            };

            InstanceRegistry registry = InstanceRegistry.Load(discoverRunningInstances: false);
            var service = new ArchiveService();
            service.SyncFromRegistry(registry.Instances);

            var collectors = new List<IFacetCollector>
            {
                new LivenessCollector(), new HarnessCollector(), new PluginsCollector(),
                new UsageCollector(), new HomeCollector(), new WslEnvCollector()
            };

            Out("== 实例档案体检（--archive-check）==");
            Out("档案目录: " + service.Store.Dir);
            Out("状态目录: " + AppPaths.StateDir + (AppPaths.IsPortable ? "（便携模式）" : ""));
            Out("");

            int failures = 0, collected = 0;
            foreach (InstanceDef def in registry.Instances)
            {
                if (onlyInstance != null && !string.Equals(def.Id, onlyInstance, StringComparison.OrdinalIgnoreCase))
                    continue;

                Out("[" + def.Id + "] " + def.Name + "  " +
                    (def.IsWsl ? "wsl(" + (def.WslDistro ?? "?") + ")" : "windows") + "  :" + def.Port);

                // 改版·启动时间打点：identity 是清单镜像（无采集器），体检补一行，
                // 现场可见 lastStartedAt 的落档值。
                FacetSnapshot ident = service.Snapshot(def.Id, FacetNames.Identity);
                Out("    " + Pad(FacetNames.Identity, 10) + Pad(ident.Status, 9) + Pad(Age(ident), 12) + "  " + IdentityBrief(ident));

                var ctx = new InstanceContext { Def = def, Settings = registry.Settings };
                foreach (IFacetCollector c in collectors)
                {
                    if (onlyFacet != null && !string.Equals(c.Facet, onlyFacet, StringComparison.OrdinalIgnoreCase))
                        continue;

                    FacetSnapshot before = service.Snapshot(def.Id, c.Facet);
                    bool never = before.Status == FacetStatus.Never;
                    FacetSnapshot s = before;
                    if (force || never)
                    {
                        s = service.CollectAsync(ctx, c, ttl: null, force: true).GetAwaiter().GetResult();
                        collected++;
                        // liveness 结论要传给后续采集器（用量等分面按运行状态取不同节奏）
                        if (c.Facet == FacetNames.Liveness) ctx.IsRunning = IsRunning(s);
                    }
                    if (s.Status == FacetStatus.Failed) failures++;
                    Out("    " + Pad(c.Facet, 10) + Pad(s.Status, 9) + Pad(Age(s), 12) +
                        Pad(s.DurationMs + "ms", 8) + Describe(s));
                }
                Out("");
            }

            service.FlushDirty();

            IReadOnlyList<InstanceArchive> all = service.All();
            Out("档案总数: " + all.Count + "（其中已退役 " + CountRetired(all) + " 份，退役档案永久保留）");
            foreach (InstanceArchive a in all)
            {
                if (!a.IsRetired) continue;
                Out("    [已退役] " + a.ArchiveId + " · " + a.DisplayName +
                    " · 退役于 " + a.RetiredAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") +
                    " · 代际 " + a.Epochs.Count);
            }
            Out("");
            Out("本次采集 " + collected + " 个分面，失败 " + failures + " 个。");
            Out(failures == 0 ? "== 体检通过 ==" : "== 存在采集失败 ==");
            Cli.WriteCliLogPublic(transcript);
            return failures == 0 ? 0 : 1;
        }

        /// <summary>identity 分面摘要：runtime/port + 最后启动时间（本地化显示）。</summary>
        private static string IdentityBrief(FacetSnapshot s)
        {
            if (!s.HasData || !s.TryGetData(out Dictionary<string, System.Text.Json.JsonElement> d)) return s.Source;
            string rt = d.TryGetValue("runtime", out System.Text.Json.JsonElement rte) ? rte.ToString() : "?";
            string port = d.TryGetValue("port", out System.Text.Json.JsonElement pe) ? pe.ToString() : "?";
            string ls = "未启动过";
            if (d.TryGetValue("lastStartedAt", out System.Text.Json.JsonElement le) &&
                le.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                string raw = le.GetString();
                DateTime t;
                if (!string.IsNullOrEmpty(raw) &&
                    DateTime.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.RoundtripKind, out t))
                    ls = t.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") + " UTC";
                else if (!string.IsNullOrEmpty(raw)) ls = raw;
            }
            return "runtime=" + rt + " port=" + port + " lastStartedAt=" + ls;
        }

        private static bool IsRunning(FacetSnapshot s)
        {
            return s.TryGetData(out Dictionary<string, System.Text.Json.JsonElement> d) &&
                   d.TryGetValue("running", out System.Text.Json.JsonElement el) &&
                   el.ValueKind == System.Text.Json.JsonValueKind.True;
        }

        private static int CountRetired(IReadOnlyList<InstanceArchive> all)
        {
            int n = 0;
            foreach (InstanceArchive a in all) if (a.IsRetired) n++;
            return n;
        }

        private static string Age(FacetSnapshot s)
        {
            if (!s.CollectedAt.HasValue) return "从未采集";
            TimeSpan age = DateTime.UtcNow - s.CollectedAt.Value;
            if (age < TimeSpan.FromMinutes(1)) return (int)age.TotalSeconds + " 秒前";
            if (age < TimeSpan.FromHours(1)) return (int)age.TotalMinutes + " 分钟前";
            if (age < TimeSpan.FromDays(1)) return (int)age.TotalHours + " 小时前";
            return (int)age.TotalDays + " 天前";
        }

        private static string Describe(FacetSnapshot s)
        {
            if (s.Status == FacetStatus.Failed) return "错误: " + s.Error;
            if (s.Status == FacetStatus.Skipped) return "跳过: " + s.SkipReason;
            if (!s.HasData) return s.Source;
            return (s.Source.Length > 0 ? s.Source + " · " : "") + Summarize(s);
        }

        /// <summary>各分面挑一两个关键字段展示，避免整段 JSON 刷屏。</summary>
        private static string Summarize(FacetSnapshot s)
        {
            if (!s.TryGetData(out Dictionary<string, System.Text.Json.JsonElement> d)) return "";
            var parts = new List<string>();
            foreach (string key in new[] { "version", "running", "listenerPid", "count", "profile",
                                           "initialized", "sizeBytes", "distro", "linuxHome", "dshAvailable",
                                           "sessionCount", "requestCount", "modelsComplete", "scannedFiles" })
            {
                if (!d.TryGetValue(key, out System.Text.Json.JsonElement el)) continue;
                string v = el.ToString();
                if (v.Length == 0) continue;
                parts.Add(key + "=" + v);
            }
            return string.Join(" ", parts);
        }

        private static string Pad(string s, int width)
        {
            s = s ?? "";
            return s.Length >= width ? s + " " : s.PadRight(width);
        }
    }
}
