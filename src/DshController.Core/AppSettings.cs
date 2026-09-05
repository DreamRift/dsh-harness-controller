// ============================================================================
//  AppSettings — 全局设置（instances.json 的 settings 节，v0.3.0）
//
//  v0.2.0 之前全局字段都在 launcher.json 顶层；v0.3.0 起拆分为：
//    settings（全局：dsh 命令/报告目录/主题/实例目录根）
//    instances[]（每实例：host/port/workspace/home/行为开关）
//  保持字段级向后兼容：旧 launcher.json 由 InstanceRegistry.Load 自动迁移。
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Serialization;
using DshController.Core.Storage;

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

        /// <summary>新建实例默认工作区目录（v0.3.1 旧字段，仅作向后兼容兜底；v0.6.1 起按环境拆分）。</summary>
        [JsonPropertyName("newInstanceWorkspace")]
        public string NewInstanceWorkspace { get; set; } = "";

        /// <summary>新建 Windows 实例默认工作区（v0.6.1）；空 = 回退旧字段 NewInstanceWorkspace。</summary>
        [JsonPropertyName("newInstanceWorkspaceWin")]
        public string NewInstanceWorkspaceWin { get; set; } = "";

        /// <summary>新建 WSL 实例默认工作区（v0.6.1，发行版内路径如 ~/dsh-workspaces）；空 = 回退旧字段。</summary>
        [JsonPropertyName("newInstanceWorkspaceWsl")]
        public string NewInstanceWorkspaceWsl { get; set; } = "";

        /// <summary>
        /// 实例档案各分面的刷新间隔（重构 2.0）：token 用量 / 已装插件 / harness 版本
        /// 等各有各的节奏，见 RefreshPolicy。缺失时取默认值（向前兼容）。
        /// </summary>
        [JsonPropertyName("refresh")]
        public RefreshPolicy Refresh { get; set; } = new RefreshPolicy();

        /// <summary>
        /// 插件市场目录自动刷新间隔（小时，v0.6.1）：打开插件市场时数据超过该间隔自动
        /// 重新联网拉取；0 = 每次打开都刷新。默认 24。拉取失败始终回退本地缓存兜底。
        /// </summary>
        [JsonPropertyName("pluginAutoRefreshHours")]
        public int PluginAutoRefreshHours { get; set; } = 24;

        /// <summary>按环境取新建实例默认工作区：环境字段优先，回退旧字段（向后兼容）。</summary>
        public string EffectiveNewInstanceWorkspaceFor(bool wsl)
        {
            string v = wsl ? NewInstanceWorkspaceWsl : NewInstanceWorkspaceWin;
            if (!string.IsNullOrWhiteSpace(v)) return v;
            return NewInstanceWorkspace ?? "";
        }

        /// <summary>
        /// WSL 实例停止后的关闭策略（v0.5.0：在 WSL 标签页的"环境设置"中单独配置）：
        /// smart（默认：发行版内无其他 harness 实例 → 终止发行版；无其他发行版运行 → wsl --shutdown）
        /// | distroOnly（只终止发行版，不关 VM） | always（总是 wsl --shutdown） | never（都不关闭）。
        /// </summary>
        [JsonPropertyName("wslShutdownPolicy")]
        public string WslShutdownPolicy { get; set; } = "smart";

        /// <summary>自定义插件市场源 URL（v0.6.0）；空 = 不使用自定义源。内置来源在「插件市场 → 数据源」选择。</summary>
        [JsonPropertyName("pluginRegistryUrl")]
        public string PluginRegistryUrl { get; set; } = "";

        /// <summary>
        /// 启用的内置市场来源 ID 列表（v0.6.1：official / curated / github-live，可多选，
        /// 多选时合并去重）；null/空 = 默认 official + curated。自定义 URL 恒在（填了才生效）。
        /// </summary>
        [JsonPropertyName("pluginSources")]
        public List<string> PluginSources { get; set; }

        /// <summary>解析后的实例目录根（配置值优先，否则默认目录）。</summary>
        [JsonIgnore]
        public string EffectiveHomeRoot
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(HomeRoot)) return HomeRoot;
                return AppPaths.DefaultHomeRoot;
            }
        }
    }
}
