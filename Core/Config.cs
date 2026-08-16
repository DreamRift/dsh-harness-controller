// ============================================================================
//  Config — launcher.json 配置（v0.2.0）
//
//  相比 legacy 版（手写 JSON 转义，曾把反斜杠翻倍污染文件）：
//    1. 读写全部走 System.Text.Json（.NET 内置），转义由库保证；
//    2. Load 时对 v0.1.0 历史污染值做一次性净化迁移（折叠多重反斜杠）；
//    3. 新增字段：errorReportDir（错误报告目录）、theme（界面主题）。
//  文件格式与 v0.1.0 保持字段级兼容（只增不改，未知字段忽略）。
// ============================================================================

using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace DshController.Core
{
    public enum AppTheme { System, Light, Dark }

    public sealed class Config
    {
        [JsonPropertyName("host")]
        public string Host { get; set; } = "127.0.0.1";

        [JsonPropertyName("port")]
        public int Port { get; set; } = 3080;

        [JsonPropertyName("workspace")]
        public string Workspace { get; set; } = "";

        [JsonPropertyName("dshCommand")]
        public string DshCommand { get; set; } = "";

        [JsonPropertyName("autoOpenBrowser")]
        public bool AutoOpenBrowser { get; set; } = true;

        [JsonPropertyName("stopOnExit")]
        public bool StopOnExit { get; set; } = true;

        /// <summary>启动失败/崩溃报告的输出目录；空 = 默认（我的文档\DshController\error-reports）。</summary>
        [JsonPropertyName("errorReportDir")]
        public string ErrorReportDir { get; set; } = "";

        /// <summary>界面主题：跟随系统 / 浅色 / 深色。</summary>
        [JsonPropertyName("theme")]
        [JsonConverter(typeof(JsonStringEnumConverterEx))]
        public AppTheme Theme { get; set; } = AppTheme.System;

        [JsonIgnore]
        public static string FilePath
        {
            get { return Path.Combine(AppContext.BaseDirectory, "launcher.json"); }
        }

        /// <summary>报告目录解析：配置值优先，否则默认目录。</summary>
        [JsonIgnore]
        public string EffectiveErrorReportDir
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(ErrorReportDir)) return ErrorReportDir;
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "DshController", "error-reports");
            }
        }

        public static Config Load()
        {
            var c = new Config();
            try
            {
                if (File.Exists(FilePath))
                {
                    // File.ReadAllText 检测并剥离 BOM；容忍 v0.1.0 手写文件的尾逗号
                    string json = File.ReadAllText(FilePath, Encoding.UTF8);
                    var opts = new JsonSerializerOptions
                    {
                        AllowTrailingCommas = true,
                        ReadCommentHandling = JsonCommentHandling.Skip,
                        PropertyNameCaseInsensitive = true
                    };
                    var parsed = JsonSerializer.Deserialize<Config>(json, opts);
                    if (parsed != null) c = parsed;
                }
            }
            catch
            {
                // 配置损坏时回退默认值（与 legacy 行为一致），不中断启动
                c = new Config();
            }

            c.Workspace = SanitizePath(c.Workspace);
            c.DshCommand = SanitizePath(c.DshCommand);
            c.ErrorReportDir = SanitizePath(c.ErrorReportDir);

            if (string.IsNullOrEmpty(c.Workspace))
                c.Workspace = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (c.Port < 1 || c.Port > 65535) c.Port = 3080;
            if (string.IsNullOrEmpty(c.Host)) c.Host = "127.0.0.1";
            return c;
        }

        public void Save()
        {
            try
            {
                var opts = new JsonSerializerOptions { WriteIndented = true };
                File.WriteAllText(FilePath, JsonSerializer.Serialize(this, opts), new UTF8Encoding(false));
            }
            catch
            {
                // 保存失败不影响运行（与 legacy 行为一致）
            }
        }

        /// <summary>净化 v0.1.0 转义 bug 造成的多重反斜杠污染。
        /// 规则：若原值恰好是存在的路径则原样保留（最安全）；
        /// 否则把 2 个以上连续反斜杠收敛为单个，但保留 UNC 开头的双反斜杠。</summary>
        internal static string SanitizePath(string v)
        {
            if (string.IsNullOrEmpty(v) || !v.Contains("\\\\")) return v;
            try { if (Directory.Exists(v) || File.Exists(v)) return v; } catch { }
            if (v.StartsWith("\\\\", StringComparison.Ordinal))
            {
                // UNC 路径无论污染成多少条前导反斜杠，都保留标准的两条。
                string rest = v.Substring(2).TrimStart('\\');
                return "\\\\" + Regex.Replace(rest, @"\\{2,}", @"\");
            }
            return Regex.Replace(v, @"\\{2,}", @"\");
        }
    }

    /// <summary>camelCase 枚举转换（theme: "system"/"light"/"dark"），未知值回退默认。</summary>
    public sealed class JsonStringEnumConverterEx : JsonConverter<AppTheme>
    {
        public override AppTheme Read(ref Utf8JsonReader reader, Type t, JsonSerializerOptions o)
        {
            try
            {
                string s = reader.GetString();
                AppTheme r;
                if (Enum.TryParse(s, ignoreCase: true, out r)) return r;
            }
            catch { }
            return AppTheme.System;
        }

        public override void Write(Utf8JsonWriter writer, AppTheme value, JsonSerializerOptions o)
        {
            writer.WriteStringValue(value.ToString().ToLowerInvariant());
        }
    }
}
