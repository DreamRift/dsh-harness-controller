// ============================================================================
//  ProviderSyncPlan — 同步预览计划（改版·供应商同步 / 同步预览窗）
//
//  · PlanFor：预设 × 目标实例（档案内存清单）→ 差异行（新增/更新/无变化 + 将写字段 + 降级 note）
//  · RenderYamlBlock：把映射后的 providers 条目渲染为将写入的规范文本（预览与写入
//    共用同一单源——预览"就是"将写入内容，写入引擎在「写入生效」小类按本渲染实现）；
//  · 差异计算只用内存数据（预设 + 档案清单 + 当前条目快照），不扫盘。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace DshController.Core
{
    /// <summary>同步计划一行：一个目标实例 × 该预设。</summary>
    public sealed class SyncPlanRow
    {
        public string InstanceId { get; set; } = "";
        public string InstanceLabel { get; set; } = "";
        public string Kind { get; set; } = "新增";          // 新增 | 更新 | 无变化
        public string EntryKey { get; set; } = "";
        public List<string> Fields { get; } = new List<string>();   // 将写入/变更的字段
        public List<string> Notes { get; } = new List<string>();    // 降级 note
    }

    public static class ProviderSyncPlan
    {
        /// <summary>对每个目标实例出计划行；currentOf(id) 返回该实例当前 providers 条目（null=无）。
        /// 目标清单必须来自内存（档案/预设），禁止扫盘。</summary>
        public static List<SyncPlanRow> PlanFor(
            ProviderPreset preset,
            IReadOnlyList<(string Id, string Label)> instances,
            Func<string, InstanceProviderEntry> currentOf)
        {
            var rows = new List<SyncPlanRow>();
            if (preset == null || instances == null) return rows;
            MappingResult mapped = ProviderConfigMapper.ToEntry(preset);
            foreach ((string id, string label) in instances)
            {
                InstanceProviderEntry cur = currentOf == null ? null : currentOf(id);
                var row = new SyncPlanRow { InstanceId = id, InstanceLabel = label, EntryKey = mapped.Entry.Key };
                row.Notes.AddRange(mapped.Notes);
                if (cur == null || !string.Equals(cur.Key, mapped.Entry.Key, StringComparison.OrdinalIgnoreCase))
                {
                    row.Kind = "新增";
                    row.Fields.Add("displayName");
                    row.Fields.Add("api");
                    if (mapped.Entry.ApiKeyEnv.Length > 0) row.Fields.Add("apiKeyEnv");
                    if (mapped.Entry.BaseUrl.Length > 0) row.Fields.Add("baseURL");
                    if (mapped.Entry.Models.Count > 0) row.Fields.Add("models");
                }
                else
                {
                    row.Kind = "无变化";
                    if (cur.DisplayName != mapped.Entry.DisplayName) AddField(row, "displayName");
                    if (cur.Api != mapped.Entry.Api) AddField(row, "api");
                    if (cur.ApiKeyEnv != mapped.Entry.ApiKeyEnv) AddField(row, "apiKeyEnv");
                    if (cur.BaseUrl != mapped.Entry.BaseUrl) AddField(row, "baseURL");
                    var curIds = cur.Models.Select(m => m.Id).OrderBy(x => x, StringComparer.Ordinal).ToList();
                    var newIds = mapped.Entry.Models.Select(m => m.Id).OrderBy(x => x, StringComparer.Ordinal).ToList();
                    if (!curIds.SequenceEqual(newIds)) AddField(row, "models");
                    row.Kind = row.Fields.Count > 0 ? "更新" : "无变化";
                }
                if (row.Kind == "无变化") row.Notes.Add("该实例当前配置已一致");
                rows.Add(row);
            }
            return rows;
        }

        private static void AddField(SyncPlanRow row, string field)
        {
            if (!row.Fields.Contains(field)) row.Fields.Add(field);
        }

        /// <summary>把 providers 条目渲染为规范 YAML 文本（预览/写入共用单源）。
        /// 宽松段（Extra）不渲染——由写入引擎按既有文件原样保留。</summary>
        public static string RenderYamlBlock(InstanceProviderEntry e)
        {
            if (e == null) return "";
            var sb = new StringBuilder();
            sb.Append(e.Key).Append(':').Append(Environment.NewLine);
            sb.Append("  displayName: ").Append(Quote(e.DisplayName)).Append(Environment.NewLine);
            sb.Append("  api: ").Append(e.Api).Append(Environment.NewLine);
            if (e.ApiKeyEnv.Length > 0) sb.Append("  apiKeyEnv: ").Append(e.ApiKeyEnv).Append(Environment.NewLine);
            if (e.BaseUrl.Length > 0) sb.Append("  baseURL: ").Append(Quote(e.BaseUrl)).Append(Environment.NewLine);
            if (e.Models.Count > 0)
            {
                sb.Append("  models:").Append(Environment.NewLine);
                foreach (ProviderModelConfig m in e.Models)
                {
                    sb.Append("    - id: ").Append(Quote(m.Id)).Append(Environment.NewLine);
                    if (m.Name.Length > 0) sb.Append("      name: ").Append(Quote(m.Name)).Append(Environment.NewLine);
                }
            }
            return sb.ToString().TrimEnd();
        }

        /// <summary>YAML 标量：含特殊字符才加引号（宽松，够用即可；写入引擎负责真正合并）。</summary>
        public static string Quote(string s)
        {
            if (s == null) return "''";
            string trimmed = (s ?? "").Trim();
            bool special = s.Length == 0
                || trimmed != s
                || s.IndexOfAny(":#&*!|>%@".ToCharArray()) >= 0
                || s.Contains("'")
                || s.Contains("\"");
            if (!special) return s;
            return "'" + s.Replace("'", "''") + "'";
        }
    }
}
