// ============================================================================
//  RefreshPolicy — 各类信息的刷新间隔（重构 2.0 / P1）
//
//  用户诉求原文："对于不同的信息他需要不同的刷新间隔设置，比如 token 的消耗和
//  已安装的三方插件以及实例的版本，都需要不同的刷新间隔"。
//  这里就是那份设置：每个分面一个间隔，0 = 关闭自动刷新（只在手动/事件时采集）。
//  存于 instances.json 的 settings.refresh 节，缺字段取默认值（向前兼容）。
// ============================================================================

using System;
using System.Text.Json.Serialization;
using DshController.Core.Archive;

namespace DshController.Core
{
    public sealed class RefreshPolicy
    {
        /// <summary>总开关：关掉后只剩手动刷新与事件触发（省电/离线场景）。</summary>
        [JsonPropertyName("autoRefreshEnabled")]
        public bool AutoRefreshEnabled { get; set; } = true;

        /// <summary>在线状态（当前可见页面）：秒。</summary>
        [JsonPropertyName("livenessForegroundSeconds")]
        public int LivenessForegroundSeconds { get; set; } = 2;

        /// <summary>在线状态（非可见页面/窗口最小化）：秒。</summary>
        [JsonPropertyName("livenessBackgroundSeconds")]
        public int LivenessBackgroundSeconds { get; set; } = 20;

        /// <summary>harness 版本：小时（版本很少变，事件触发为主）。</summary>
        [JsonPropertyName("harnessHours")]
        public int HarnessHours { get; set; } = 24;

        /// <summary>已装插件：小时（插件操作后会立即失效重采）。</summary>
        [JsonPropertyName("pluginsHours")]
        public int PluginsHours { get; set; } = 6;

        /// <summary>token 用量（实例运行中）：分钟。</summary>
        [JsonPropertyName("usageRunningMinutes")]
        public int UsageRunningMinutes { get; set; } = 30;

        /// <summary>token 用量（实例已停止）：小时。</summary>
        [JsonPropertyName("usageStoppedHours")]
        public int UsageStoppedHours { get; set; } = 6;

        /// <summary>HOME 状态与体积：小时（目录遍历较贵）。</summary>
        [JsonPropertyName("homeHours")]
        public int HomeHours { get; set; } = 24;

        /// <summary>WSL 发行版环境：小时。</summary>
        [JsonPropertyName("wslEnvHours")]
        public int WslEnvHours { get; set; } = 1;

        /// <summary>同时进行的采集任务上限。</summary>
        [JsonPropertyName("maxConcurrent")]
        public int MaxConcurrent { get; set; } = 2;

        /// <summary>其中"需要进 WSL 发行版"的任务上限（wsl.exe 往返昂贵，默认串行）。</summary>
        [JsonPropertyName("wslMaxConcurrent")]
        public int WslMaxConcurrent { get; set; } = 1;

        /// <summary>
        /// 取某分面的自动刷新间隔；返回 null = 不自动刷新（仅手动或事件驱动）。
        /// running/foreground 让同一分面按情境取不同节奏（用量、在线状态）。
        /// </summary>
        public TimeSpan? TtlFor(string facet, bool running, bool foreground)
        {
            if (!AutoRefreshEnabled) return null;
            switch (FacetNames.Canonical(facet))
            {
                case FacetNames.Identity: return TimeSpan.Zero;                    // 永远新鲜（清单变更即镜像）
                case FacetNames.Liveness:
                    return FromSeconds(foreground ? LivenessForegroundSeconds : LivenessBackgroundSeconds);
                case FacetNames.Harness: return FromHours(HarnessHours);
                case FacetNames.Plugins: return FromHours(PluginsHours);
                case FacetNames.Usage:
                    return running ? FromMinutes(UsageRunningMinutes) : FromHours(UsageStoppedHours);
                case FacetNames.Home: return FromHours(HomeHours);
                case FacetNames.WslEnv: return FromHours(WslEnvHours);
                default: return null;
            }
        }

        public int EffectiveMaxConcurrent => MaxConcurrent < 1 ? 1 : MaxConcurrent;
        public int EffectiveWslMaxConcurrent => WslMaxConcurrent < 1 ? 1 : WslMaxConcurrent;

        private static TimeSpan? FromSeconds(int v) => v <= 0 ? (TimeSpan?)null : TimeSpan.FromSeconds(v);
        private static TimeSpan? FromMinutes(int v) => v <= 0 ? (TimeSpan?)null : TimeSpan.FromMinutes(v);
        private static TimeSpan? FromHours(int v) => v <= 0 ? (TimeSpan?)null : TimeSpan.FromHours(v);
    }
}
