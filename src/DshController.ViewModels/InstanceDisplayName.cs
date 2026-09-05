// ============================================================================
//  InstanceDisplayName — 显示名单源解析器（改版·环境端口命名）
//
//  规则（定稿）：
//    · 未设别名时显示名 =「环境:端口」——windows:3080 / wsl:3081；
//      WSL 形态不带发行版名（发行版收进 tooltip）；
//    · tooltip 呈现 instances.json 原名（WSL 附发行版），原名不丢失；
//    · 别名支路在「改名入口」小类接入（届时 先别名后本形式）；此处不设占位。
//  全应用凡列实例处（左栏行 / 三处实例选择器 / 用量卡与范围下拉 / 确认文案）
//  都只经本类取标签——改样式与语义只看这一处。
// ============================================================================

using System.Collections.Generic;
using DshController.Core;
using DshController.Core.Archive;

namespace DshController.ViewModels
{
    public static class InstanceDisplayName
    {
        private static readonly System.Collections.Generic.Dictionary<string, string> _aliases
            = new System.Collections.Generic.Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase);

        /// <summary>注册别名映射（App 层启动时注入全部别名，改名后即时更新）。</summary>
        public static void SetAlias(string archiveId, string alias)
        {
            if (string.IsNullOrEmpty(archiveId)) return;
            if (string.IsNullOrWhiteSpace(alias)) _aliases.Remove(archiveId);
            else _aliases[archiveId] = alias.Trim();
        }

        /// <summary>取别名（无别名返回空串）。</summary>
        public static string GetAlias(string archiveId) =>
            archiveId != null && _aliases.TryGetValue(archiveId, out string a) ? a ?? "" : "";
        /// <summary>环境段（单源字面）。</summary>
        public static string Env(InstanceDef def)
        {
            return def != null && def.IsWsl ? "wsl" : "windows";
        }

        /// <summary>清单实例的显示名：「环境:端口」；端口非法时降级为环境段。</summary>
        public static string For(InstanceDef def)
        {
            if (def == null) return "";
            string alias = GetAlias(def.Id);
            string env = def.IsWsl ? "wsl" : "windows";
            string defStr = def.Port > 0 ? env + ":" + def.Port : env;
            return string.IsNullOrEmpty(alias) ? defStr : alias + " (" + defStr + ")";
        }

        /// <summary>instances.json 原名（无名字时回落 Id）。</summary>
        public static string Original(InstanceDef def)
        {
            if (def == null) return "";
            return string.IsNullOrWhiteSpace(def.Name) ? def.Id : def.Name;
        }

        /// <summary>tooltip：原名 +（WSL）发行版细节。</summary>
        public static string TooltipFor(InstanceDef def)
        {
            if (def == null) return "";
            string extra = def.IsWsl && !string.IsNullOrWhiteSpace(def.WslDistro)
                ? " · " + def.WslDistro : "";
            return "原名 " + Original(def) + extra;
        }

        /// <summary>档案（含退役）的显示名：环境:端口，取自档案镜像的 runtime 与代际定义。</summary>
        public static string ForArchive(InstanceArchive a)
        {
            if (a == null) return "";
            string alias = GetAlias(a.ArchiveId);
            string env = a.Runtime == "wsl" ? "wsl" : "windows";
            int port = ArchivePort(a);
            string def = port > 0 ? env + ":" + port : env;
            return string.IsNullOrEmpty(alias) ? def : alias + " (" + def + ")";
        }

        /// <summary>档案 tooltip：原名（+退役标注由调用方按语境追加）。</summary>
        public static string TooltipForArchive(InstanceArchive a)
        {
            if (a == null) return "";
            return "原名 " + (string.IsNullOrWhiteSpace(a.DisplayName) ? a.ArchiveId : a.DisplayName);
        }

        /// <summary>档案端口：取最新一代定义（CurrentEpoch → 末代回落）。</summary>
        public static int ArchivePort(InstanceArchive a)
        {
            if (a == null) return 0;
            List<ArchiveEpoch> epochs = a.Epochs;
            if (epochs == null || epochs.Count == 0) return 0;
            InstanceDef def = epochs[epochs.Count - 1]?.Def;
            return def == null ? 0 : def.Port;
        }
    }
}
