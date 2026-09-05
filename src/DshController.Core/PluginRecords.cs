// ============================================================================
//  PluginRecords — "通过插件市场安装"的记录（v0.6.0 插件市场）
//
//  用途：仅做来源标注与升级依据；已装插件的**真实状态**永远以实例 HOME 的
//  profiles/<profile>/package.json 为准（InstalledPlugins 黑盒读取），本记录
//  只是控制器侧的"这块插件是从市场装的"标记。
//  隔离：按实例 ID 分文件存 %LOCALAPPDATA%\DshController\plugin-records\，
//  与实例 HOME 的隔离一一对应；实例删除后记录文件残留无害（无实例引用即不显示）。
//  持久化：仿 InstanceRegistry 的原子写（tmp + Move overwrite），损坏回退空表。
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DshController.Core.Storage;

namespace DshController.Core
{
    /// <summary>一条市场安装记录。</summary>
    public sealed class PluginRecord
    {
        /// <summary>安装包名（dsh plugin add/remove 的目标名）。</summary>
        [JsonPropertyName("pkg")]
        public string Pkg { get; set; } = "";

        /// <summary>插件显示名（目录条目 name）。</summary>
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        /// <summary>来源仓库 owner/repo。</summary>
        [JsonPropertyName("repo")]
        public string Repo { get; set; } = "";

        /// <summary>安装时目录里看到的版本（可能为空）。</summary>
        [JsonPropertyName("version")]
        public string Version { get; set; } = "";

        /// <summary>安装目标 profile（默认 web）。</summary>
        [JsonPropertyName("profile")]
        public string Profile { get; set; } = "web";

        /// <summary>安装目标原文（npm 包名或 github:owner/repo）。</summary>
        [JsonPropertyName("target")]
        public string Target { get; set; } = "";

        [JsonPropertyName("installedAt")]
        public DateTime InstalledAt { get; set; } = DateTime.UtcNow;
    }

    public static class PluginRecords
    {
        /// <summary>自检可覆盖的存储根目录（null = 正式目录）。</summary>
        public static string OverrideDir;

        public static string Dir => OverrideDir ?? AppPaths.PluginRecordsDir;

        private static string PathFor(string instanceId)
        {
            string safe = string.Join("_", (instanceId ?? "unknown").Split(
                Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
            if (safe.Length == 0) safe = "unknown";
            return Path.Combine(Dir, safe + ".json");
        }

        /// <summary>读取某实例的全部市场安装记录；文件不存在/损坏返回空表。</summary>
        public static List<PluginRecord> Load(string instanceId)
        {
            try
            {
                string path = PathFor(instanceId);
                if (!File.Exists(path)) return new List<PluginRecord>();
                var parsed = JsonSerializer.Deserialize<List<PluginRecord>>(
                    File.ReadAllText(path, Encoding.UTF8), JsonOpts());
                return parsed ?? new List<PluginRecord>();
            }
            catch
            {
                return new List<PluginRecord>();
            }
        }

        /// <summary>保存某实例的全部记录（原子写）。</summary>
        public static void Save(string instanceId, List<PluginRecord> records)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                var opts = new JsonSerializerOptions { WriteIndented = true };
                string tmp = PathFor(instanceId) + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(records ?? new List<PluginRecord>(), opts),
                    new UTF8Encoding(false));
                File.Move(tmp, PathFor(instanceId), overwrite: true);
            }
            catch
            {
                // 理由: 记录保存失败不影响插件本身——已装插件的真实状态永远以实例 HOME 为准
            }
        }

        /// <summary>插入或更新（按 pkg 幂等）一条记录。</summary>
        public static void Upsert(string instanceId, PluginRecord record)
        {
            if (record == null || string.IsNullOrWhiteSpace(record.Pkg)) return;
            List<PluginRecord> list = Load(instanceId);
            list.RemoveAll(r => string.Equals(r.Pkg, record.Pkg, StringComparison.OrdinalIgnoreCase));
            list.Insert(0, record);
            Save(instanceId, list);
        }

        /// <summary>按包名移除记录；返回是否移除了至少一条。</summary>
        public static bool Remove(string instanceId, string pkg)
        {
            List<PluginRecord> list = Load(instanceId);
            int before = list.Count;
            list.RemoveAll(r => string.Equals(r.Pkg, pkg, StringComparison.OrdinalIgnoreCase));
            if (list.Count == before) return false;
            Save(instanceId, list);
            return true;
        }

        /// <summary>按包名查找（不分大小写）；找不到返回 null。</summary>
        public static PluginRecord Find(List<PluginRecord> records, string pkg)
        {
            return (records ?? new List<PluginRecord>()).FirstOrDefault(
                r => string.Equals(r.Pkg, pkg, StringComparison.OrdinalIgnoreCase));
        }

        private static JsonSerializerOptions JsonOpts()
        {
            return new JsonSerializerOptions
            {
                AllowTrailingCommas = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                PropertyNameCaseInsensitive = true
            };
        }
    }
}
