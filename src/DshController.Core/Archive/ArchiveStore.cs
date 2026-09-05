// ============================================================================
//  ArchiveStore — 档案文件仓库（重构 2.0 / P1）
//
//  布局：%LOCALAPPDATA%\DshController\archives\<archiveId>.json
//  每实例一个文件（并发写互不影响、单个损坏只波及一份、便于导出与人工查看）。
//  没有单独的索引文件——列表由目录枚举得到，少一个需要维护一致性的东西；
//  档案数量是"实例数量"量级（十几个），枚举成本可以忽略。
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using DshController.Core.Storage;

namespace DshController.Core.Archive
{
    public sealed class ArchiveStore
    {
        private readonly string _dir;

        /// <summary>dir 为 null 时使用 AppPaths.ArchivesDir（自检/单测传临时目录隔离）。</summary>
        public ArchiveStore(string dir = null)
        {
            _dir = dir;
        }

        public string Dir => _dir ?? AppPaths.ArchivesDir;

        public string PathFor(string archiveId)
        {
            return Path.Combine(Dir, SafeFileName(archiveId) + ".json");
        }

        /// <summary>读取一份档案；不存在或损坏返回 null（调用方决定是否新建）。</summary>
        public InstanceArchive Read(string archiveId)
        {
            string path = PathFor(archiveId);
            if (!File.Exists(path)) return null;
            InstanceArchive archive = JsonStore.Read<InstanceArchive>(path, () => null);
            if (archive == null) return null;
            // 未来 schema 更高的文件（用户装回旧版本）按只读处理：不解析、不覆盖
            if (archive.Schema > InstanceArchive.CurrentSchema) return null;
            if (string.IsNullOrEmpty(archive.ArchiveId)) archive.ArchiveId = archiveId;
            return archive.Normalize();
        }

        public bool Write(InstanceArchive archive)
        {
            if (archive == null || string.IsNullOrEmpty(archive.ArchiveId)) return false;
            return JsonStore.Write(PathFor(archive.ArchiveId), archive);
        }

        /// <summary>枚举全部档案 id（含已退役）。</summary>
        public IReadOnlyList<string> ListIds()
        {
            var ids = new List<string>();
            try
            {
                if (!Directory.Exists(Dir)) return ids;
                foreach (string file in Directory.EnumerateFiles(Dir, "*.json"))
                {
                    string name = Path.GetFileNameWithoutExtension(file);
                    if (!string.IsNullOrEmpty(name)) ids.Add(name);
                }
            }
            catch
            {
                // 理由: 目录不可读时按"没有历史档案"处理，不影响当前实例的运行
            }
            ids.Sort(StringComparer.OrdinalIgnoreCase);
            return ids;
        }

        /// <summary>读取全部档案（跳过损坏的）。</summary>
        public IReadOnlyList<InstanceArchive> ReadAll()
        {
            var list = new List<InstanceArchive>();
            foreach (string id in ListIds())
            {
                InstanceArchive a = Read(id);
                if (a != null) list.Add(a);
            }
            return list;
        }

        public bool Delete(string archiveId)
        {
            return JsonStore.Delete(PathFor(archiveId));
        }

        /// <summary>实例 id 已由 InstanceRegistry.IsValidId 限制字符集，这里仍做一次兜底净化。</summary>
        private static string SafeFileName(string id)
        {
            string safe = string.Join("_", (id ?? "unknown").Split(
                Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
            return safe.Length == 0 ? "unknown" : safe;
        }
    }
}
