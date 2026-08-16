// ============================================================================
//  AppSettings — 全局设置（instances.json 的 settings 节，v0.3.0）
//
//  v0.2.0 之前全局字段都在 launcher.json 顶层；v0.3.0 起拆分为：
//    settings（全局：dsh 命令/报告目录/主题/实例目录根）
//    instances[]（每实例：host/port/workspace/home/行为开关）
//  保持字段级向后兼容：旧 launcher.json 由 InstanceRegistry.Load 自动迁移。
// ============================================================================

using System;
using System.IO;
using System.Text.Json.Serialization;

namespace DshController.Core
{
    public sealed class AppSettings
    {
        [JsonPropertyName("dshCommand")]
        public string DshCommand { get; set; } = "";

        [JsonPropertyName("errorReportDir")]
        public string ErrorReportDir { get; set; } = "";

        /// <summary>界面主题（全局默认，实例可继承）。</summary>
        [JsonPropertyName("theme")]
        [JsonConverter(typeof(JsonStringEnumConverterEx))]
        public AppTheme Theme { get; set; } = AppTheme.System;

        /// <summary>新实例 DSH_HOME 的存放根目录；空 = 默认 %LOCALAPPDATA%\DshController\instances。</summary>
        [JsonPropertyName("homeRoot")]
        public string HomeRoot { get; set; } = "";

        /// <summary>解析后的实例目录根（配置值优先，否则默认目录）。</summary>
        [JsonIgnore]
        public string EffectiveHomeRoot
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(HomeRoot)) return HomeRoot;
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "DshController", "instances");
            }
        }
    }
}
