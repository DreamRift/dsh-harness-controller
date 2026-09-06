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
using System.Globalization;
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
        /// 目标清单必须来自内存（档案/预设），禁止扫盘。
        /// 官方预设仅同步密钥引用（llm-deepseek.apiKeyEnv）；非官方写入 llm-pi-ai.providers.<key>。</summary>
        public static List<SyncPlanRow> PlanFor(
            ProviderPreset preset,
            IReadOnlyList<(string Id, string Label)> instances,
            Func<string, InstanceProviderEntry> currentOf)
        {
            var rows = new List<SyncPlanRow>();
            if (preset == null || instances == null) return rows;
            if (preset.IsBuiltin)
            {
                foreach ((string id, string label) in instances)
                {
                    var row = new SyncPlanRow { InstanceId = id, InstanceLabel = label, EntryKey = preset.ProviderId };
                    row.Notes.Add("官方预设仅同步密钥引用（llm-deepseek.apiKeyEnv）：官方模型、思考档与多模态由实例原生适配器提供");
                    if ((preset.ApiKey ?? "").Trim().Length == 0)
                    {
                        row.Kind = "无变化";
                        row.Notes.Add("预设未填密钥：官方同步无内容可写，填密钥后再同步");
                    }
                    else
                    {
                        row.Kind = "新增";
                        row.Fields.Add("apiKeyEnv（llm-deepseek）");
                    }
                    row.Notes.Add("旧根级 providers." + preset.ProviderId + " 旧块将一并清除");
                    row.Notes.Add("密钥由 DshController 启动实例时注入环境变量；手动启动的实例需自行设置");
                    rows.Add(row);
                }
                return rows;
            }
            MappingResult mapped = ProviderConfigMapper.ToEntry(preset);
            bool anyInput = mapped.Entry.Models.Any(m => m.InputModalities != null && m.InputModalities.Count > 0);
            bool anyEfforts = mapped.Entry.Models.Any(m => m.WriteReasoningEfforts);
            foreach ((string id, string label) in instances)
            {
                InstanceProviderEntry cur = currentOf == null ? null : currentOf(id);
                var row = new SyncPlanRow { InstanceId = id, InstanceLabel = label, EntryKey = mapped.Entry.Key };
                row.Notes.AddRange(mapped.Notes);
                if (anyEfforts) row.Notes.Add("自动补思考档四档（off/low/high/max，非官方来源；实例侧已有声明不覆盖）");
                if (anyInput) row.Notes.Add("已知多模态写入 input 字段（对齐 dsh pi-ai 模型条目）");
                row.Notes.Add("写入 llm-pi-ai.providers." + mapped.Entry.Key + "（dsh 实际读取的段）；旧根级 providers 块将一并清除");
                if (cur == null || !string.Equals(cur.Key, mapped.Entry.Key, StringComparison.OrdinalIgnoreCase))
                {
                    row.Kind = "新增";
                    row.Fields.Add("displayName");
                    row.Fields.Add("api");
                    if (mapped.Entry.ApiKeyEnv.Length > 0) row.Fields.Add("apiKeyEnv");
                    if (mapped.Entry.BaseUrl.Length > 0) row.Fields.Add("baseURL");
                    if (mapped.Entry.Models.Count > 0) row.Fields.Add("models");
                    if (anyInput) row.Fields.Add("input");
                    if (anyEfforts) row.Fields.Add("reasoningEfforts");
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
                    // 思考档/多模态是逐模型字段：实例侧任一模型缺失才列为将写差异
                    if (anyEfforts && cur.Models.Any(m => !m.WriteReasoningEfforts)) AddField(row, "reasoningEfforts");
                    if (anyInput && cur.Models.Any(m => m.InputModalities == null || m.InputModalities.Count == 0))
                        AddField(row, "input");
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
                    AppendNumberField(sb, m.Extra, "contextWindow");
                    AppendNumberField(sb, m.Extra, "maxTokens");
                    AppendInputModalities(sb, m);
                    AppendReasoningEfforts(sb, m);
                }
            }
            return sb.ToString().TrimEnd();
        }

        /// <summary>渲染模型输入模态（dsh pi-ai 条目字段 input；flow 序列，空=不渲染=未声明）。</summary>
        private static void AppendInputModalities(StringBuilder sb, ProviderModelConfig m)
        {
            if (m.InputModalities == null || m.InputModalities.Count == 0) return;
            sb.Append("      input: [").Append(string.Join(", ", m.InputModalities)).Append(']')
              .Append(Environment.NewLine);
        }

        /// <summary>渲染四档思考档（对齐 dsh-thinking-efforts v0.2.0：off 档发 null=线上省略）。</summary>
        private static void AppendReasoningEfforts(StringBuilder sb, ProviderModelConfig m)
        {
            if (!m.WriteReasoningEfforts) return;
            sb.Append("      reasoningEfforts:").Append(Environment.NewLine);
            sb.Append("        off: null").Append(Environment.NewLine);
            sb.Append("        low: low").Append(Environment.NewLine);
            sb.Append("        high: high").Append(Environment.NewLine);
            sb.Append("        max: max").Append(Environment.NewLine);
        }

        /// <summary>渲染模型容量等数值宽松段（contextWindow/maxTokens；数字不加引号）。</summary>
        private static void AppendNumberField(StringBuilder sb, Dictionary<string, object> extra, string key)
        {
            if (extra == null || !extra.TryGetValue(key, out object v) || v == null) return;
            if (v is sbyte || v is byte || v is short || v is ushort || v is int || v is uint
                || v is long || v is ulong)
            {
                sb.Append("      ").Append(key).Append(": ").Append(Convert.ToString(v, CultureInfo.InvariantCulture))
                  .Append(Environment.NewLine);
                return;
            }
            if (v is string s && long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long n))
            {
                sb.Append("      ").Append(key).Append(": ").Append(n.ToString(CultureInfo.InvariantCulture))
                  .Append(Environment.NewLine);
            }
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
