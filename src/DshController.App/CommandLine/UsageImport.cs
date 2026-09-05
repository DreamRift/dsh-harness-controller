// ============================================================================
//  --import-usage — 导入用量原型的 usage-backup.json（重构 2.0 / P2）
//
//  原型分支把快照写在它自己的 exe 目录旁，合并主线后那台机器上的历史用量
//  （含已删除实例）需要一条明确的导入路径。默认位置自动查找，也可显式给路径。
//
//  用法：DshController.exe --import-usage [<usage-backup.json 路径>]
// ============================================================================

using System;
using System.Text;
using DshController.Core;
using DshController.Core.Archive;
using DshController.Core.Usage;

namespace DshController.CommandLine
{
    internal static class UsageImport
    {
        public static int Run(string[] args)
        {
            string path = args.Length > 1 && !args[1].StartsWith("--", StringComparison.Ordinal) ? args[1] : null;
            var transcript = new StringBuilder();
            Action<string> Out = line =>
            {
                transcript.AppendLine(line);
                try { Console.WriteLine(line); } catch { /* 理由: 无控制台时静默，转录仍写 cli.log */ }
            };

            InstanceRegistry registry = InstanceRegistry.Load(discoverRunningInstances: false);
            var service = new ArchiveService();
            service.SyncFromRegistry(registry.Instances);

            Out("== 导入用量原型快照（--import-usage）==");
            UsageImportResult r = path != null
                ? UsageBackupImport.Import(path, service, registry.Instances)
                : UsageBackupImport.ImportDefault(service, registry.Instances);

            if (!r.FileFound)
            {
                Out("未找到 usage-backup.json。默认查找位置：");
                foreach (string c in UsageBackupImport.DefaultCandidates()) Out("    " + c);
                Out("可显式指定路径：--import-usage <文件路径>");
                Cli.WriteCliLogPublic(transcript);
                return 1;
            }
            if (r.Error.Length > 0)
            {
                Out("导入失败：" + r.Error);
                Cli.WriteCliLogPublic(transcript);
                return 1;
            }
            Out("导入 " + r.Imported + " 个实例的历史用量" +
                (r.Retired > 0 ? "（其中 " + r.Retired + " 个实例已不在清单，档案标记退役后永久保留）" : "") +
                (r.Skipped > 0 ? "，跳过 " + r.Skipped + " 个（档案里已有更新的用量数据）" : "") + "。");
            Out("档案目录：" + service.Store.Dir);
            Cli.WriteCliLogPublic(transcript);
            return 0;
        }
    }
}
