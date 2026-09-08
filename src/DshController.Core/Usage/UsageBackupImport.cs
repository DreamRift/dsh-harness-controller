// ============================================================================
//  UsageBackupImport — 导入原型分支的 usage-backup.json（重构 2.0 / P2）
//
//  用量原型（test-usage-stats 分支）把快照写在 exe 旁的 usage-backup.json 里，
//  其中包含**已删除实例**的历史用量。合并进主线时不能让这些数据凭空消失：
//  这里把它一次性导入实例档案的 usage 分面（已有 usage 数据的档案不覆盖），
//  并为清单里已不存在的实例建立"已退役"档案——正是档案设计要保住的东西。
//  导入是幂等的：源文件保留，重复导入不会重复写。
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using DshController.Core.Archive;
using DshController.Core.Storage;

namespace DshController.Core.Usage
{
    /// <summary>旧备份文件的最小读取视图（字段名与原型一致，大小写不敏感匹配）。</summary>
    public sealed class LegacyUsageBackupFile
    {
        [JsonPropertyName("version")]
        public int Version { get; set; }

        [JsonPropertyName("instances")]
        public List<LegacyUsageBackupInstance> Instances { get; set; } = new List<LegacyUsageBackupInstance>();
    }

    public sealed class LegacyUsageBackupInstance
    {
        [JsonPropertyName("definition")]
        public InstanceDef Definition { get; set; }

        [JsonPropertyName("homeDisplay")]
        public string HomeDisplay { get; set; } = "";

        [JsonPropertyName("modelsComplete")]
        public bool ModelsComplete { get; set; }

        [JsonPropertyName("modelScanError")]
        public string ModelScanError { get; set; } = "";

        [JsonPropertyName("snapshotAtUtc")]
        public DateTime SnapshotAtUtc { get; set; }

        [JsonPropertyName("sessions")]
        public List<UsageSessionStat> Sessions { get; set; } = new List<UsageSessionStat>();

        [JsonPropertyName("models")]
        public List<UsageModelStat> Models { get; set; } = new List<UsageModelStat>();
    }

    public sealed class UsageImportResult
    {
        public bool FileFound { get; set; }
        public int Imported { get; set; }
        public int Skipped { get; set; }
        public int Retired { get; set; }
        public string Error { get; set; } = "";
    }

    public static class UsageBackupImport
    {
        public const string FileName = "usage-backup.json";

        /// <summary>默认查找位置：exe 旁（原型的落盘位置）与状态目录。</summary>
        public static IEnumerable<string> DefaultCandidates()
        {
            yield return Path.Combine(AppPaths.ExeDir, FileName);
            yield return Path.Combine(AppPaths.StateDir, FileName);
        }

        /// <summary>在默认位置找到备份就导入（幂等）。</summary>
        public static UsageImportResult ImportDefault(ArchiveService service, IEnumerable<InstanceDef> liveDefs)
        {
            foreach (string path in DefaultCandidates())
            {
                if (!File.Exists(path)) continue;
                return Import(path, service, liveDefs);
            }
            return new UsageImportResult();
        }

        /// <summary>
        /// 导入指定备份文件。规则：
        ///   · 档案里已有 usage 数据的实例跳过（现采的数据比历史快照新）；
        ///   · 清单里已不存在的实例照样建档并标记退役（历史用量因此可查）。
        /// </summary>
        public static UsageImportResult Import(string path, ArchiveService service,
            IEnumerable<InstanceDef> liveDefs)
        {
            var result = new UsageImportResult();
            if (service == null || string.IsNullOrEmpty(path) || !File.Exists(path)) return result;
            result.FileFound = true;

            LegacyUsageBackupFile file = JsonStore.Read<LegacyUsageBackupFile>(path, () => null);
            if (file == null)
            {
                result.Error = "备份文件无法解析";
                return result;
            }

            var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (InstanceDef d in liveDefs ?? new List<InstanceDef>())
                if (d != null && !string.IsNullOrEmpty(d.Id)) live.Add(d.Id);

            foreach (LegacyUsageBackupInstance inst in file.Instances ?? new List<LegacyUsageBackupInstance>())
            {
                InstanceDef def = inst?.Definition;
                if (def == null || string.IsNullOrEmpty(def.Id)) continue;

                FacetSnapshot existing = service.Snapshot(def.Id, FacetNames.Usage);
                if (existing.HasData) { result.Skipped++; continue; }

                var data = new UsageFacetData
                {
                    Home = inst.HomeDisplay ?? "",
                    Sessions = (inst.Sessions ?? new List<UsageSessionStat>())
                        .Where(s => s != null && s.HasTokenUsage).ToList(),
                    Models = inst.Models ?? new List<UsageModelStat>(),
                    ModelsComplete = inst.ModelsComplete,
                    ModelScanError = inst.ModelScanError ?? ""
                };
                data.SessionCount = data.Sessions.Count;
                foreach (UsageSessionStat s in data.Sessions) data.Totals.Add(s.Totals);
                foreach (UsageModelStat m in data.Models) data.RequestCount += m.Requests;

                InstanceArchive archive = service.GetOrCreate(def);
                if (archive == null) continue;
                service.ApplyResult(archive, FacetNames.Usage,
                    FacetResult.Ok(data, "usage-backup.json（原型快照导入）"), 0);
                result.Imported++;

                if (!live.Contains(def.Id))
                {
                    service.Retire(def.Id);   // 清单里已经没有它了：档案退役但永久保留
                    result.Retired++;
                }
            }
            service.FlushDirty();
            return result;
        }
    }
}
