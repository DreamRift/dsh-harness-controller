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

        /// <summary>DSH_HOME 目录（v0.3.0 多实例隔离）；空 = 不注入（使用默认 ~/.dsh，兼容旧行为）。</summary>
        [JsonPropertyName("home")]
        public string Home { get; set; } = "";

        /// <summary>额外浏览器信任来源（--trusted-host，可重复）。</summary>
        [JsonPropertyName("trustedHosts")]
        public string[] TrustedHosts { get; set; } = Array.Empty<string>();

        /// <summary>运行环境：windows（默认，cmd 直接拉起）| wsl（wsl.exe 拉起，P:WSL 支持）。</summary>
        [JsonPropertyName("runtime")]
        public string Runtime { get; set; } = "windows";

        /// <summary>WSL 发行版名称（Runtime=wsl 时有效，如 Ubuntu-26.04）。</summary>
        [JsonPropertyName("wslDistro")]
        public string WslDistro { get; set; } = "";

        /// <summary>Linux 侧 DSH_HOME（Runtime=wsl 时有效；~ 前缀展开，空 = ~/.dsh）。</summary>
        [JsonPropertyName("wslHome")]
        public string WslHome { get; set; } = "";

        /// <summary>WSL 实例停止后的关闭策略：smart（默认）| always | distroOnly。</summary>
        [JsonPropertyName("wslShutdownPolicy")]
        public string WslShutdownPolicy { get; set; } = "smart";

        /// <summary>实例 ID（多实例；失败报告按实例归档，v0.5.0）。</summary>
        [JsonPropertyName("instanceId")]
        public string InstanceId { get; set; } = "";

        /// <summary>实例显示名（失败报告展示，v0.5.0）。</summary>
        [JsonPropertyName("instanceName")]
        public string InstanceName { get; set; } = "";

        /// <summary>
        /// harness 版本锁定（v0.5.0）：空 = 使用当前环境主实例版本；
        /// 非空 = 经 npx 拉取 @deepseek-ai/dsh@&lt;版本&gt; 启动。
        /// </summary>
        [JsonPropertyName("harnessVersion")]
        public string HarnessVersion { get; set; } = "";

        [JsonIgnore]
        public bool IsWsl => Runtime != null && Runtime.Equals("wsl", StringComparison.OrdinalIgnoreCase);

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
