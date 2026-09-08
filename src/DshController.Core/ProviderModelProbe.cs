// ============================================================================
//  ProviderModelProbe — 模型目录探针（API 页照 dsh 模型页"获取可用模型"适配）
//
//  · 语义对齐 dsh ModelListEditor：向"表单当前所填"的端点发问（含未保存的密钥），
//    应答只是候选清单，绝不越过用户直接写配置；失败显示在可手动补行的旁边；
//  · 端点：<baseUrl>/models（OpenAI 兼容列表；baseUrl 归一去尾斜杠）；
//  · 容量提取：应答模型条目里常见的上下文/最大输出别名（含 OpenRouter 的
//    context_length 与 top_provider.completion_max_tokens 嵌套）——能拿到就随
//    候选带回，采纳时自动填入（拿不到就留空=继承提供方默认）；
//  · HTTP 在 Core（HttpFetch），解析为纯函数可离线单测；页面经 VM/App 调用。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Threading.Tasks;

namespace DshController.Core
{
    /// <summary>探针发现的一个候选模型（容量 null=端点未提供，采纳后保持继承；
    /// 模态支持三态：null=端点未披露、false=明确不支持、true=支持该模态）。</summary>
    public sealed class DiscoveredModel
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public long? ContextWindow { get; set; }
        public long? MaxTokens { get; set; }
        public bool? SupportImage { get; set; }
        public bool? SupportVideo { get; set; }
        public bool? SupportAudio { get; set; }
    }

    /// <summary>探针结果：Ok=true 时 Models 可能为空列表（视为 fetchEmpty）。</summary>
    public sealed class ProbeResult
    {
        public bool Ok { get; set; }
        public List<DiscoveredModel> Models { get; set; } = new List<DiscoveredModel>();
        public string Error { get; set; } = "";
    }

    public static class ProviderModelProbe
    {
        private static readonly string[] ContextAliases =
            { "context_length", "context_window", "contextWindow", "context_size", "max_context_length" };
        private static readonly string[] MaxTokensAliases =
            { "max_output_tokens", "maxOutputTokens", "max_completion_tokens", "completion_max_tokens", "output_token_limit", "max_tokens" };
        /// <summary>输入模态列表的常见字段别名（值形如 ["text","image"] 或 "text,image"）。</summary>
        private static readonly string[] InputModalityAliases =
            { "input_modalities", "inputModalities", "input", "modalities", "input_modality", "supported_modalities" };
        /// <summary>输入模态布尔位的常见字段别名（true=支持图片输入）。</summary>
        private static readonly string[] MultimodalFlagAliases =
            { "supports_vision", "supportsVision", "supports_image", "supportsImage", "vision", "image_input" };

        /// <summary>模型列表端点；baseUrl 空返回空串（调用方按 fetchNeedsBaseUrl 拦下）。</summary>
        public static string ModelsUrl(string baseUrl)
        {
            string b = (baseUrl ?? "").Trim().TrimEnd('/');
            return b.Length == 0 ? "" : b + "/models";
        }

        /// <summary>向表单当前端点询问可用模型（dsh：含未保存的密钥；无密钥则匿名问）。</summary>
        public static async Task<ProbeResult> DiscoverAsync(string baseUrl, string apiKey, int timeoutMs = 8000)
        {
            string url = ModelsUrl(baseUrl);
            if (url.Length == 0)
                return new ProbeResult { Ok = false, Error = PresetCopy.FetchNeedsBaseUrl };
            string body = await HttpFetch.GetStringAsync(url, timeoutMs, (apiKey ?? "").Trim())
                .ConfigureAwait(false);
            if (body == null)
                return new ProbeResult { Ok = false, Error = "无法访问该 API 地址（网络、超时或非 2xx）。" };
            List<DiscoveredModel> models = Parse(body);
            if (models.Count == 0)
                return new ProbeResult { Ok = false, Error = PresetCopy.FetchEmpty };
            return new ProbeResult { Ok = true, Error = "" , Models = models };
        }

        /// <summary>解析应答（纯函数）：OpenAI 形 {"data":[…]} 或裸数组；无 id 的条目跳过。</summary>
        public static List<DiscoveredModel> Parse(string json)
        {
            var result = new List<DiscoveredModel>();
            if (string.IsNullOrWhiteSpace(json)) return result;
            JsonElement root;
            try
            {
                using JsonDocument doc = JsonDocument.Parse(json);
                root = doc.RootElement.Clone();
            }
            catch
            {
                return result;   // 理由: 非法 JSON 按空候选处理，错误由调用方文案承接
            }
            JsonElement.ArrayEnumerator items;
            if (root.ValueKind == JsonValueKind.Array) items = root.EnumerateArray();
            else if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("data", out JsonElement data)
                && data.ValueKind == JsonValueKind.Array) items = data.EnumerateArray();
            else return result;

            foreach (JsonElement item in items)
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (!item.TryGetProperty("id", out JsonElement idEl) || idEl.ValueKind != JsonValueKind.String) continue;
                string id = idEl.GetString();
                if (string.IsNullOrWhiteSpace(id)) continue;
                var m = new DiscoveredModel { Id = id.Trim() };
                if (TryString(item, "name") is string n && n.Length > 0) m.Name = n;
                else if (TryString(item, "display_name") is string dn && dn.Length > 0) m.Name = dn;
                m.ContextWindow = FirstNumber(item, ContextAliases);
                m.MaxTokens = FirstNumber(item, MaxTokensAliases);
                var modalities = ModalitiesOf(item);
                m.SupportImage = modalities.Image;
                m.SupportVideo = modalities.Video;
                m.SupportAudio = modalities.Audio;
                result.Add(m);
            }
            return result;
        }

        private static string TryString(JsonElement obj, string prop)
        {
            if (obj.ValueKind != JsonValueKind.Object) return null;
            if (!obj.TryGetProperty(prop, out JsonElement el)) return null;
            if (el.ValueKind == JsonValueKind.String) return el.GetString();
            if (el.ValueKind == JsonValueKind.Number) return el.GetRawText();
            return null;
        }

        /// <summary>按别名顺序取首个可读数值；整数直接取，数字字符串按 long 解。</summary>
        private static long? FirstNumber(JsonElement obj, string[] aliases)
        {
            if (obj.ValueKind != JsonValueKind.Object) return null;
            foreach (string alias in aliases)
            {
                if (!obj.TryGetProperty(alias, out JsonElement el)) continue;
                long? v = NumberOf(el);
                if (v.HasValue) return v;
            }
            // 嵌套兜底：OpenRouter top_provider.completion_max_tokens
            if (obj.TryGetProperty("top_provider", out JsonElement tp) && tp.ValueKind == JsonValueKind.Object)
            {
                foreach (string alias in MaxTokensAliases)
                {
                    if (!tp.TryGetProperty(alias, out JsonElement el)) continue;
                    long? v = NumberOf(el);
                    if (v.HasValue) return v;
                }
            }
            return null;
        }

        private static long? NumberOf(JsonElement el)
        {
            if (el.ValueKind == JsonValueKind.Number)
            {
                if (el.TryGetInt64(out long n) && n > 0) return n;
                return null;
            }
            if (el.ValueKind == JsonValueKind.String)
            {
                string s = (el.GetString() ?? "").Trim();
                if (s.Length > 0 && long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long n) && n > 0)
                    return n;
            }
            return null;
        }

        /// <summary>模态支持结果。</summary>
        public struct ModalitySupport
        {
            public bool? Image;
            public bool? Video;
            public bool? Audio;
        }

        /// <summary>模态判定（纯函数）：从输入模态列表/布尔位中提取图片/视频/音频支持。
        /// 什么都不说= null（对齐插件"不做模型知识库推测"的取向）。空列表视为未声明。
        /// 别名覆盖 OpenRouter 的 architecture.input_modalities 嵌套。</summary>
        public static ModalitySupport ModalitiesOf(JsonElement item)
        {
            var result = new ModalitySupport();
            if (item.ValueKind != JsonValueKind.Object) return result;

            // 尝试从 input_modalities 等字段读取
            foreach (string alias in InputModalityAliases)
            {
                if (!item.TryGetProperty(alias, out JsonElement el)) continue;
                var support = ParseModalityList(el);
                if (support.Image.HasValue || support.Video.HasValue || support.Audio.HasValue)
                    return support;
            }

            // 尝试从 architecture.input_modalities 读取
            if (item.TryGetProperty("architecture", out JsonElement arch) && arch.ValueKind == JsonValueKind.Object)
            {
                foreach (string alias in InputModalityAliases)
                {
                    if (!arch.TryGetProperty(alias, out JsonElement el)) continue;
                    var support = ParseModalityList(el);
                    if (support.Image.HasValue || support.Video.HasValue || support.Audio.HasValue)
                        return support;
                }
            }

            // 尝试布尔标记位（仅能判断图片支持）
            foreach (string alias in MultimodalFlagAliases)
            {
                if (!item.TryGetProperty(alias, out JsonElement el)) continue;
                if (el.ValueKind == JsonValueKind.True)
                {
                    result.Image = true;
                    return result;
                }
                if (el.ValueKind == JsonValueKind.False)
                {
                    result.Image = false;
                    return result;
                }
            }

            return result;
        }

        /// <summary>从模态声明解析各模态支持：提取 image/video/audio。</summary>
        private static ModalitySupport ParseModalityList(JsonElement el)
        {
            var result = new ModalitySupport();
            List<string> tokens = ModalityTokens(el);
            if (tokens.Count == 0) return result;

            bool hasImage = false, hasVideo = false, hasAudio = false, hasText = false;

            foreach (string t in tokens)
            {
                string s = t.Trim().ToLowerInvariant();
                if (s == "image" || s == "vision" || s == "image_url") hasImage = true;
                else if (s == "video") hasVideo = true;
                else if (s == "audio") hasAudio = true;
                else if (s == "text") hasText = true;
            }

            // 只有声明了才给出明确的值
            if (hasImage) result.Image = true;
            else if (hasText && tokens.Count > 0) result.Image = false;  // 明确只支持文本

            if (hasVideo) result.Video = true;
            else if (hasText && tokens.Count > 0) result.Video = false;

            if (hasAudio) result.Audio = true;
            else if (hasText && tokens.Count > 0) result.Audio = false;

            return result;
        }

        /// <summary>模态声明收词：数组取字符串元素；字符串按逗号/空白拆分；其余不认。</summary>
        private static List<string> ModalityTokens(JsonElement el)
        {
            var tokens = new List<string>();
            if (el.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement e in el.EnumerateArray())
                {
                    if (e.ValueKind == JsonValueKind.String) tokens.Add(e.GetString() ?? "");
                }
            }
            else if (el.ValueKind == JsonValueKind.String)
            {
                tokens.AddRange((el.GetString() ?? "").Split(',', ' ', ';'));
            }
            return tokens;
        }
    }
}
