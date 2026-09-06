// ============================================================================
//  ProviderPresetRules — 供应商预设校验规则（API 页照 dsh 官方模型设置页适配）
//
//  规则与文案逐条对齐 @deepseek-ai/dsh-client-ui-settings-models（0.1.1-rc.2）：
//    - Provider ID 路由形：^[a-z][a-z0-9]*(-[a-z0-9]+)*$（customRouteInvalid）；
//    - API 密钥判定：空=保持已存（非失败）、纯空白=keyBlank、
//      环境变量形/引号包裹/非可打印 ASCII=keyIllegalCharacters；
//    - 容量：空=继承，数字可带 K/M 后缀（千进制 1K=1e3、1M=1e6），
//      回写取最短可往返形式；
//    - 模型目录：ID 必填且唯一、显示名称非空、上下文/最大输出须正整数。
//  全部纯函数，离线单测钉死。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace DshController.Core
{
    /// <summary>适配器可配置的传输协议（与 dsh pi-ai 适配器的协议门一致，按序呈现）。</summary>
    public static class ProviderProtocols
    {
        public static readonly string[] All =
        {
            "openai-completions", "openai-responses", "azure-openai-responses",
            "openai-codex-responses", "anthropic-messages"
        };
    }

    /// <summary>校验文案（键名对齐 dsh locales，便于对照）。</summary>
    public static class PresetCopy
    {
        public const string RouteHint = "以小写字母开头的标识，在请求中唯一标识该提供方，并用于派生凭据名。";
        public const string RouteInvalid = "需以小写字母开头，之后可用小写字母、数字和短横线。";
        public const string RouteTaken = "已有提供方使用了这个 ID。";
        public const string NeedsBaseUrl = "自定义提供方需要填写 API 地址。";
        public const string NeedsModels = "自定义提供方至少需要一个模型。";
        public const string KeyBlank = "请输入 API 密钥；留空则保持已存储的密钥。";
        public const string KeyBlankNew = "请输入 API 密钥；若该提供方以其他方式鉴权，可以留空。";
        public const string KeyIllegal = "该 API 密钥格式错误，请检查。";
        public const string ModelIdRequired = "模型 ID 不能为空。";
        public const string ModelIdDuplicate = "模型 ID 不能重复。";
        public const string ModelNameInvalid = "显示名称不能为空。";
        public const string ModelContextInvalid = "上下文窗口必须是正数，例如 131072、256K 或 1M。";
        public const string ModelMaxTokensInvalid = "最大输出 token 数必须是正数，例如 8192、64K 或 1M。";
        public const string FetchNeedsBaseUrl = "请先填写 API 地址，再获取。";
        public const string FetchEmpty = "该提供方没有列出任何模型，请手动添加。";
        public const string KeyConfigured = "API 密钥已配置";
        public const string KeyMissing = "API 密钥缺失";
    }

    /// <summary>dsh 模型页的浏览器侧判定规则移植（纯函数）。</summary>
    public static class ProviderPresetRules
    {
        private static readonly Regex RoutePattern = new Regex(
            "^[a-z][a-z0-9]*(?:-[a-z0-9]+)*$", RegexOptions.Compiled);
        private static readonly Regex EnvLine = new Regex(
            "^[A-Z][A-Z0-9_]*=[^=]", RegexOptions.Compiled);
        private static readonly Regex LegalApiKey = new Regex(
            "^[\x21-\x7E]+$", RegexOptions.Compiled);
        private static readonly Regex CapacityPattern = new Regex(
            "^(\\d+(?:\\.\\d+)?)([km])?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private const long ScaleK = 1000, ScaleM = 1000000;

        /// <summary>Provider ID 路由是否合法（dsh ROUTE_PATTERN；上限 64 字符）。</summary>
        public static bool IsValidRoute(string route)
        {
            string r = (route ?? "").Trim();
            return r.Length > 0 && r.Length <= 64 && RoutePattern.IsMatch(r);
        }

        /// <summary>密钥输入判定（dsh apiKeyFailure）：空串=null（保持已存，非失败）；
        /// 纯空白=keyBlank；环境变量形/引号包裹/含不可打印 ASCII 之外字符=keyIllegal。</summary>
        /// <returns>失败文案；null=允许提交。</returns>
        public static string ApiKeyFailure(string draft, bool forNew)
        {
            if (string.IsNullOrEmpty(draft)) return null;
            string value = draft.Trim();
            if (value.Length == 0) return forNew ? PresetCopy.KeyBlankNew : PresetCopy.KeyBlank;
            if (EnvLine.IsMatch(value) || IsQuoted(value) || !LegalApiKey.IsMatch(value))
                return PresetCopy.KeyIllegal;
            return null;
        }

        /// <summary>是否被成对引号包裹（dsh isQuoted：换行粘贴的包装文本按格式错误报）。</summary>
        private static bool IsQuoted(string value)
        {
            char first = value[0];
            if (first != '"' && first != '\'' && first != '`') return false;
            return value.Length > 1 && value[value.Length - 1] == first;
        }

        /// <summary>容量解析（dsh parseCapacity）：空=null（继承提供方默认）；
        /// 支持 256K / 1M 后缀（千进制）；不可读返回 false。</summary>
        public static bool TryParseCapacity(string text, out long? value)
        {
            value = null;
            string trimmed = (text ?? "").Trim();
            if (trimmed.Length == 0) return true;
            Match m = CapacityPattern.Match(trimmed);
            if (!m.Success) return false;
            if (!double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double num))
                return false;
            long scale = 1;
            string suffix = m.Groups[2].Value.ToLowerInvariant();
            if (suffix == "k") scale = ScaleK;
            else if (suffix == "m") scale = ScaleM;
            double scaled = num * scale;
            double rounded = Math.Round(scaled);
            value = Math.Abs(scaled - rounded) < 1e-6 ? (long)rounded : (long)scaled;
            return true;
        }

        /// <summary>容量回写（dsh formatCapacity）：整千/整百万取最短 K/M 形式。</summary>
        public static string FormatCapacity(long value)
        {
            if (value <= 0) return value.ToString(CultureInfo.InvariantCulture);
            if (value % ScaleM == 0) return (value / ScaleM).ToString(CultureInfo.InvariantCulture) + "M";
            if (value % ScaleK == 0) return (value / ScaleK).ToString(CultureInfo.InvariantCulture) + "K";
            return value.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>模型目录校验（dsh validateDeepSeekModels）：返回首个违规行的
        /// (下标, 文案)；全部合法返回 null。</summary>
        public static KeyValuePair<int, string>? ValidateModels(IEnumerable<PresetModel> models)
        {
            if (models == null) return null;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            int index = -1;
            foreach (PresetModel model in models)
            {
                index++;
                if (model == null) continue;
                string id = (model.Id ?? "").Trim();
                if (id.Length == 0) return new KeyValuePair<int, string>(index, PresetCopy.ModelIdRequired);
                if (!seen.Add(id)) return new KeyValuePair<int, string>(index, PresetCopy.ModelIdDuplicate);
                string name = (model.Name ?? "").Trim();
                if (name.Length == 0 && (model.Name ?? "").Length > 0)
                    return new KeyValuePair<int, string>(index, PresetCopy.ModelNameInvalid);
                if (model.ContextWindow.HasValue && model.ContextWindow.Value <= 0)
                    return new KeyValuePair<int, string>(index, PresetCopy.ModelContextInvalid);
                if (model.MaxTokens.HasValue && model.MaxTokens.Value <= 0)
                    return new KeyValuePair<int, string>(index, PresetCopy.ModelMaxTokensInvalid);
            }
            return null;
        }
    }
}
