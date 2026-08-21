// ============================================================================
//  InstanceDef — 实例定义（instances.json 的 instances[] 元素，v0.3.0）
//
//  一个实例 = 独立 DSH_HOME（home）+ 端口 + workspace。
//  home 为空表示"不注入 DSH_HOME"，使用默认 ~/.dsh（迁移出的 default 实例
//  保持 v0.2.0 行为不变）。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace DshController.Core
{
    public sealed class InstanceDef
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("home")]
        public string Home { get; set; } = "";

        [JsonPropertyName("host")]
        public string Host { get; set; } = "127.0.0.1";

        [JsonPropertyName("port")]
        public int Port { get; set; } = 3080;

        [JsonPropertyName("trustedHosts")]
        public List<string> TrustedHosts { get; set; } = new List<string>();

        [JsonPropertyName("workspace")]
        public string Workspace { get; set; } = "";

        [JsonPropertyName("autoOpenBrowser")]
        public bool AutoOpenBrowser { get; set; } = true;

        [JsonPropertyName("stopOnExit")]
        public bool StopOnExit { get; set; } = true;

        [JsonPropertyName("createdAt")]
        public DateTime? CreatedAt { get; set; }

        [JsonPropertyName("lastStartedAt")]
        public DateTime? LastStartedAt { get; set; }

        /// <summary>运行环境：windows（默认）| wsl（v0.4.0 WSL 实例）。</summary>
        [JsonPropertyName("runtime")]
        public string Runtime { get; set; } = "windows";

        /// <summary>WSL 发行版名称（Runtime=wsl 时有效）。</summary>
        [JsonPropertyName("wslDistro")]
        public string WslDistro { get; set; } = "";

        /// <summary>Linux 侧 DSH_HOME（Runtime=wsl 时有效；~ 前缀展开，空 = ~/.dsh）。</summary>
        [JsonPropertyName("wslHome")]
        public string WslHome { get; set; } = "";

        /// <summary>
        /// harness 指定版本（v0.5.0）：空 = 跟随当前环境主实例版本；
        /// 非空 = 经 npx 拉取 @deepseek-ai/dsh@&lt;版本&gt; 启动。
        /// 新建实例默认填入当前环境检测到的版本，可手动修改。
        /// </summary>
        [JsonPropertyName("harnessVersion")]
        public string HarnessVersion { get; set; } = "";

        [JsonIgnore]
        public bool IsWsl => Runtime != null && Runtime.Equals("wsl", StringComparison.OrdinalIgnoreCase);

        /// <summary>界面显示名（WSL 实例带 [WSL] 标识）。</summary>
        [JsonIgnore]
        public string DisplayName
        {
            get
            {
                string n = string.IsNullOrWhiteSpace(Name) ? Id : Name;
                return IsWsl ? n + "  [WSL " + (string.IsNullOrWhiteSpace(WslDistro) ? "?" : WslDistro) + "]"
                             : n + "  [WIN]";
            }
        }

        /// <summary>
        /// 实例下拉列表用的一行标签（v0.5.0 双界面：环境已由标签页区分，
        /// 这里只补端口与指定版本信息，便于同环境多实例快速辨认）。
        /// </summary>
        [JsonIgnore]
        public string PickerLabel
        {
            get
            {
                string n = string.IsNullOrWhiteSpace(Name) ? Id : Name;
                string tail = " · :" + Port;
                string v = (HarnessVersion ?? "").Trim();
                if (v.Length > 0) tail += " · v" + v;
                if (IsWsl) tail += " · " + (string.IsNullOrWhiteSpace(WslDistro) ? "发行版未设置" : WslDistro);
                return n + tail;
            }
        }

        /// <summary>转换为 BackendManager 使用的运行时配置（填充全局设置）。</summary>
        public Config ToConfig(AppSettings settings)
        {
            string ws = string.IsNullOrWhiteSpace(Workspace)
                ? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
                : Workspace;
            return new Config
            {
                Host = string.IsNullOrEmpty(Host) ? "127.0.0.1" : Host,
                Port = Port < 1 || Port > 65535 ? 3080 : Port,
                Workspace = ws,
                Home = Home ?? "",
                TrustedHosts = TrustedHosts == null || TrustedHosts.Count == 0
                    ? Array.Empty<string>()
                    : TrustedHosts.ToArray(),
                DshCommand = settings?.DshCommand ?? "",
                ErrorReportDir = settings?.ErrorReportDir ?? "",
                AutoOpenBrowser = AutoOpenBrowser,
                StopOnExit = StopOnExit,
                Theme = settings?.Theme ?? AppTheme.System,
                Runtime = IsWsl ? "wsl" : "windows",
                WslDistro = WslDistro ?? "",
                WslHome = WslHome ?? "",
                WslShutdownPolicy = string.IsNullOrWhiteSpace(settings?.WslShutdownPolicy)
                    ? "smart" : settings.WslShutdownPolicy.Trim(),
                InstanceId = Id ?? "",
                InstanceName = Name ?? "",
                HarnessVersion = (HarnessVersion ?? "").Trim()
            };
        }
    }
}
