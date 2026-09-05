// ============================================================================
//  FacetNames / FacetStatus — 档案分面的名称与状态常量（重构 2.0 / P1）
//
//  分面（facet）= 档案里"一类信息"，每类有各自的采集成本与刷新间隔。
//  名称同时是 JSON 键与设置项键，因此集中在这里定义，避免魔法字符串扩散。
// ============================================================================

using System;
using System.Collections.Generic;

namespace DshController.Core.Archive
{
    public static class FacetNames
    {
        /// <summary>实例身份（名称/环境/端口/HOME），来自实例清单，零成本。</summary>
        public const string Identity = "identity";

        /// <summary>在线状态（运行/PID/URL），最高频、内存态。</summary>
        public const string Liveness = "liveness";

        /// <summary>实测 harness 版本。</summary>
        public const string Harness = "harness";

        /// <summary>已装插件与 bundle 归属。</summary>
        public const string Plugins = "plugins";

        /// <summary>token 用量（P2 并入）。</summary>
        public const string Usage = "usage";

        /// <summary>实例 HOME 状态（是否初始化、profile、体积）。</summary>
        public const string Home = "home";

        /// <summary>WSL 发行版环境（是否安装/运行、发行版内版本）。</summary>
        public const string WslEnv = "wslEnv";

        public static IReadOnlyList<string> All { get; } = new[]
        {
            Identity, Liveness, Harness, Plugins, Usage, Home, WslEnv
        };

        public static bool IsKnown(string facet)
        {
            return Canonical(facet) != null;
        }

        /// <summary>
        /// 把任意大小写的分面名归一为常量本身（未知返回 null）。
        /// 分面名同时是 JSON 键、设置项键与 CLI 参数，来源大小写不一，
        /// 一切按名比较的地方都必须先过这里——否则就会出现 "wslenv" != "wslEnv" 这种静默失配。
        /// </summary>
        public static string Canonical(string facet)
        {
            if (string.IsNullOrWhiteSpace(facet)) return null;
            string trimmed = facet.Trim();
            foreach (string f in All)
                if (string.Equals(f, trimmed, StringComparison.OrdinalIgnoreCase)) return f;
            return null;
        }
    }

    /// <summary>分面快照状态（落盘为字符串，便于人读与向前兼容）。</summary>
    public static class FacetStatus
    {
        /// <summary>从未采集过。</summary>
        public const string Never = "never";

        /// <summary>采集成功且有数据。</summary>
        public const string Ok = "ok";

        /// <summary>采集成功但没有内容（如 HOME 尚未初始化）。</summary>
        public const string Empty = "empty";

        /// <summary>采集失败（保留上一次的成功数据）。</summary>
        public const string Failed = "failed";

        /// <summary>条件不满足被跳过（如 WSL 发行版未运行）。</summary>
        public const string Skipped = "skipped";
    }
}
