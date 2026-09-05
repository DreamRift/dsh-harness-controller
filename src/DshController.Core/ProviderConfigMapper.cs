// ============================================================================
//  ProviderConfigMapper — 供应商预设 ↔ 实例侧 settings.yaml providers 格式对齐
//  （改版·供应商同步 / 配置格式对齐）
//
//  · 实例侧权威形状（真实 ~/.dsh/settings.yaml 观察）：
//      providers.<key>: { displayName, apiKeyEnv, api, baseURL, models:[{id, name,
//      contextWindow, maxTokens, compat}] }
//  · 本类只做字段映射 + 归一 + 降级判定（纯函数，可离线单测）；不写 YAML——
//    YAML 的读出/合并/写入落在「同步预览窗 / 写入生效」小类，本类产出入形数据
//    与合并后的目标条目（含宽松段 Extra 保留）。
//  · 降级策略（明确，带 note）：
//      未知 kind → api=openai-completions；
//      预设带 ApiKey → 实例侧只写 apiKeyEnv 名（密钥需在实例环境注入），未注入即降级；
//      DefaultModel 空 → 省略 models 段；
//      BaseUrl 空 → 省略 baseURL（用默认端点）；
//      目标条目中的 contextWindow/maxTokens/compat 等预设不认识的宽松段 → 合并时原样保留。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace DshController.Core
{
    /// <summary>实例侧单个模型（宽松段保留在 Extra）。</summary>
    public sealed class ProviderModelConfig
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        /// <summary>contextWindow/maxTokens/compat 等预设不认识的宽松段。</summary>
        public Dictionary<string, object> Extra { get; } = new Dictionary<string, object>();
    }

    /// <summary>实例侧 providers 条目入形（key 外置；宽松段保留在 Extra）。</summary>
    public sealed class InstanceProviderEntry
    {
        public string Key { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string Api { get; set; } = "openai-completions";
        public string ApiKeyEnv { get; set; } = "";
        public string BaseUrl { get; set; } = "";
        public List<ProviderModelConfig> Models { get; } = new List<ProviderModelConfig>();
        public Dictionary<string, object> Extra { get; } = new Dictionary<string, object>();
    }

    /// <summary>映射结果：入形条目 + 降级 note 清单。</summary>
    public sealed class MappingResult
    {
        public InstanceProviderEntry Entry { get; set; } = new InstanceProviderEntry();
        public List<string> Notes { get; } = new List<string>();
    }

    public static class ProviderConfigMapper
    {
        /// <summary>宽松归一：去首尾空白 + 去尾斜杠 + scheme 小写。</summary>
        public static string NormalizeBaseUrl(string raw)
        {
            string s = (raw ?? "").Trim();
            if (s.Length == 0) return "";
            if (!Uri.TryCreate(s, UriKind.Absolute, out Uri uri)) return s;
            string path = uri.AbsolutePath == "/" ? "" : uri.AbsolutePath;
            return uri.Scheme.ToLowerInvariant() + "://" + uri.Authority + path;
        }

        /// <summary>预设 → 实例 providers 键（ASCII slug：仅 [a-z0-9]，其余折叠为 '-'；
        /// 中文等非 ASCII 不参与键防 YAML 键乱码——同名冲突以 displayName 区分）。</summary>
        public static string ProviderKey(string kind, string name)
        {
            string raw = ((kind ?? "") + "-" + (name ?? "")).ToLowerInvariant();
            var sb = new StringBuilder();
            bool lastDash = false;
            foreach (char c in raw)
            {
                bool ascii = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9');
                if (ascii) { sb.Append(c); lastDash = false; }
                else if (sb.Length > 0 && !lastDash) { sb.Append('-'); lastDash = true; }
            }
            string key = sb.ToString().Trim('-');
            return key.Length == 0 ? "preset" : key;
        }

        /// <summary>预设 kind → 实例 api 适配值（宽松段归一先例：认子串再回落）。</summary>
        public static string ApiAdapter(string kind)
        {
            string k = (kind ?? "").ToLowerInvariant();
            if (k.Length == 0) return "openai-completions";
            if (k.Contains("responses")) return "openai-responses";
            return "openai-completions";
        }

        /// <summary>密钥环境变量名（大写 slug；实例侧只存名不存值）。</summary>
        public static string ApiKeyEnvName(string providerKey)
        {
            return ("DSH_PRESET_" + (providerKey ?? "")).ToUpperInvariant().Replace('-', '_');
        }

        /// <summary>预设 → 实例入形条目（含降级 note）。</summary>
        public static MappingResult ToEntry(ProviderPreset preset)
        {
            var result = new MappingResult();
            if (preset == null) { result.Notes.Add("预设为空，无可同步内容"); return result; }
            string key = ProviderKey(preset.Kind, preset.Name);
            result.Entry.Key = key;
            result.Entry.DisplayName = (preset.Name ?? "").Trim();
            result.Entry.Api = ApiAdapter(preset.Kind);
            result.Entry.ApiKeyEnv = ApiKeyEnvName(key);
            result.Entry.BaseUrl = NormalizeBaseUrl(preset.BaseUrl);
            if (result.Entry.BaseUrl.Length == 0) result.Notes.Add("未设 BaseUrl：将使用实例默认端点");
            string modelId = (preset.DefaultModel ?? "").Trim();
            if (modelId.Length > 0)
            {
                result.Entry.Models.Add(new ProviderModelConfig { Id = modelId, Name = preset.Name });
            }
            else
            {
                result.Notes.Add("未设默认模型：省略 models 段，可在实例侧手动补模型");
            }
            if (!string.IsNullOrEmpty(preset.ApiKey))
                result.Notes.Add("预设含 API 密钥：实例侧仅写 apiKeyEnv 名，密钥需在实例环境注入该变量");
            return result;
        }

        /// <summary>把预设映射结果合并进既有条目：只覆盖映射字段，宽松段（Extra）与实例
        /// 扩展字段（contextWindow/maxTokens/compat）原样保留。</summary>
        public static void ApplyTo(InstanceProviderEntry target, MappingResult mapped)
        {
            if (target == null || mapped?.Entry == null) return;
            target.Key = mapped.Entry.Key;
            target.DisplayName = mapped.Entry.DisplayName;
            target.Api = mapped.Entry.Api;
            target.ApiKeyEnv = mapped.Entry.ApiKeyEnv;
            target.BaseUrl = mapped.Entry.BaseUrl;
            if (mapped.Entry.Models.Count > 0)
            {
                // 模型按 id 覆盖或追加；已存在模型的宽松段留在原位
                foreach (ProviderModelConfig m in mapped.Entry.Models)
                {
                    ProviderModelConfig existing = target.Models.FirstOrDefault(x =>
                        string.Equals(x.Id, m.Id, StringComparison.OrdinalIgnoreCase));
                    if (existing != null)
                    {
                        if (!string.IsNullOrEmpty(m.Name)) existing.Name = m.Name;
                    }
                    else
                    {
                        target.Models.Add(new ProviderModelConfig { Id = m.Id, Name = m.Name });
                    }
                }
            }
        }
    }
}
