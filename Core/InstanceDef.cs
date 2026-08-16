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
                Theme = settings?.Theme ?? AppTheme.System
            };
        }
    }
}
