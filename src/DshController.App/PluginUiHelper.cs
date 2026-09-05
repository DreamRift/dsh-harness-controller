// ============================================================================
//  PluginUiHelper — 插件市场 / 插件管理两个面板的共享 UI 辅助（v0.6.0）
//
//  职责：
//    - 全部实例（Windows + WSL）的版本探测缓存（实例 HOME 版本对插件市场
//      仅用于兼容性提示，检测一次缓存即可，实例设置变更后由面板主动失效）；
//    - 实例 HOME 的展示与解析（Windows 绝对路径 / WSL 发行版内路径）；
//    - 判断实例 HOME 是否已初始化（避免给未初始化的 HOME 直接装插件）。
//  无状态静态类，两个面板共用，避免复制粘贴漂移。
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using DshController.Core;
using DshController.Core.Storage;

namespace DshController
{
    internal static class PluginUiHelper
    {
        // ---------------- 实例版本缓存 ----------------

        private static readonly Dictionary<string, string> VersionCache =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>实例 harness 版本（探测一次缓存；空串 = 未检测到）。</summary>
        public static async Task<string> DetectVersionAsync(InstanceRegistry registry, InstanceDef def)
        {
            if (def == null) return "";
            if (VersionCache.TryGetValue(def.Id, out string cached)) return cached;
            string ver = "";
            try
            {
                Config cfg = def.ToConfig(registry.Settings);
                ver = def.IsWsl
                    ? await HarnessVersion.ResolveWslAsync(def.WslDistro ?? "").ConfigureAwait(true)
                    : await HarnessVersion.ResolveWindowsAsync(cfg).ConfigureAwait(true);
            }
            catch
            {
                // 理由: 版本探测仅用于兼容性提示且结果已缓存，解析失败时该实例记为未检测到（返回空串），不阻塞市场加载。
            }
            VersionCache[def.Id] = ver ?? "";
            return VersionCache[def.Id];
        }

        public static void InvalidateVersion(string instanceId)
        {
            if (!string.IsNullOrEmpty(instanceId)) VersionCache.Remove(instanceId);
        }

        // ---------------- HOME 展示与解析 ----------------

        /// <summary>Windows 实例 HOME 展示文本（空 = 默认 ~/.dsh）。</summary>
        public static string WindowsHomeDisplay(Config cfg)
        {
            return string.IsNullOrWhiteSpace(cfg.Home)
                ? AppPaths.DefaultDshHome + "（默认）"
                : cfg.Home;
        }

        /// <summary>WSL 实例 HOME 展示文本（空 = 发行版默认 ~/.dsh）。</summary>
        public static string WslHomeDisplay(Config cfg)
        {
            return string.IsNullOrWhiteSpace(cfg.WslHome)
                ? "~/.dsh（发行版默认）"
                : cfg.WslHome + "（发行版 " + (cfg.WslDistro ?? "?") + " 内）";
        }

        /// <summary>实例是否与"默认 ~/.dsh"共享插件目录（隔离缺口，安装前警告）。</summary>
        public static bool UsesSharedDefaultHome(Config cfg)
        {
            return cfg.IsWsl
                ? string.IsNullOrWhiteSpace(cfg.WslHome)
                : string.IsNullOrWhiteSpace(cfg.Home);
        }

        /// <summary>
        /// 解析插件命令执行用的 DSH_HOME（Windows 绝对路径 / WSL 发行版内路径）。
        /// 返回 null = 使用默认 ~/.dsh（不注入）。WSL 解析需要一次发行版调用。
        /// </summary>
        public static async Task<string> ResolveEffectiveHomeAsync(Config cfg)
        {
            if (cfg.IsWsl)
            {
                if (string.IsNullOrWhiteSpace(cfg.WslHome)) return null;
                string root = await WslTools.GetDistroHomeAsync(cfg.WslDistro ?? "").ConfigureAwait(true);
                return WslTools.ResolveLinuxPath(cfg.WslHome, string.IsNullOrEmpty(root) ? "/root" : root);
            }
            return string.IsNullOrWhiteSpace(cfg.Home) ? null : cfg.Home;
        }

        /// <summary>实例 HOME 是否已初始化（存在 profiles/web/cordis.yml）。未初始化的 HOME 装插件会先被 dsh 拒绝或生成空 profile。</summary>
        public static async Task<bool> HomeInitializedAsync(Config cfg)
        {
            try
            {
                if (cfg.IsWsl)
                {
                    string home = await ResolveEffectiveHomeAsync(cfg).ConfigureAwait(true);
                    if (home == null)
                    {
                        string root = await WslTools.GetDistroHomeAsync(cfg.WslDistro ?? "").ConfigureAwait(true);
                        home = WslTools.ResolveLinuxPath("", string.IsNullOrEmpty(root) ? "/root" : root);
                    }
                    string yml = await WslTools.ReadDistroFileAsync(cfg.WslDistro ?? "",
                        home.TrimEnd('/') + "/profiles/web/cordis.yml").ConfigureAwait(true);
                    return !string.IsNullOrEmpty(yml);
                }
                string homeWin = string.IsNullOrWhiteSpace(cfg.Home)
                    ? AppPaths.DefaultDshHome
                    : cfg.Home;
                return File.Exists(Path.Combine(homeWin, "profiles", "web", "cordis.yml"));
            }
            catch
            {
                return false;
            }
        }

        /// <summary>插件操作与记录的统一 profile 归一化（空 → web）。</summary>
        public static string NormalizeProfile(string profile)
        {
            string p = (profile ?? "").Trim();
            if (p.Length == 0) p = "web";
            // profile 名会拼进文件系统路径（Windows）与 bash 单引号串（WSL），收紧到安全字符
            foreach (char c in p)
            {
                if (!(char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.'))
                    return null;   // 含非法字符
            }
            return p;
        }
    }
}
