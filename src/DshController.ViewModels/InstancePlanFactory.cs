// ============================================================================
//  InstancePlanFactory — 新建/克隆实例的"决定逻辑"（重构 2.0 / P3）
//
//  原本埋在 InstancePanel 那个 307 行的对话框方法里：ID 生成、端口分配、
//  工作区继承、WSL 默认 HOME/工作区规则……与 UI 混在一起，无法验证。
//  这里只做决定，不碰控件：输入现状 + 用户选择，输出一个 InstanceDef。
// ============================================================================

using System;
using System.Collections.Generic;
using DshController.Core;

namespace DshController.ViewModels
{
    public sealed class InstancePlanInput
    {
        public string Name { get; set; } = "";
        public bool IsWsl { get; set; }
        public string WslDistro { get; set; } = "";
        public int Port { get; set; }
        public string Workspace { get; set; } = "";
        public string Home { get; set; } = "";
        public string HarnessVersion { get; set; } = "";

        /// <summary>被继承设置的来源实例（克隆或"新建时沿用当前实例"）。</summary>
        public InstanceDef Source { get; set; }
    }

    public static class InstancePlanFactory
    {
        /// <summary>WSL 实例的默认工作区/HOME 根（发行版内路径）。</summary>
        public const string WslWorkspaceRoot = "~/dsh-workspaces";
        public const string WslHomeRoot = "~/dsh-instances";

        /// <summary>
        /// 由已存在的 id 集合生成唯一 id：以名称为种子净化成合法字符，冲突则追加序号。
        /// </summary>
        public static string MakeUniqueId(string seed, IEnumerable<string> existingIds)
        {
            var used = new HashSet<string>(existingIds ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            var chars = new List<char>();
            foreach (char c in (seed ?? "").Trim())
            {
                if (char.IsLetterOrDigit(c) || c == '-' || c == '_') chars.Add(char.ToLowerInvariant(c));
                else if (chars.Count > 0 && chars[chars.Count - 1] != '-') chars.Add('-');
            }
            string baseId = new string(chars.ToArray()).Trim('-');
            if (baseId.Length == 0) baseId = "instance";
            if (baseId.Length > 48) baseId = baseId.Substring(0, 48);

            if (!used.Contains(baseId)) return baseId;
            for (int i = 2; i < 1000; i++)
            {
                string candidate = baseId + "-" + i;
                if (!used.Contains(candidate)) return candidate;
            }
            return baseId + "-" + Guid.NewGuid().ToString("N").Substring(0, 6);
        }

        /// <summary>
        /// 组装新实例定义。工作区回退顺序：显式输入 → 全局默认（按环境）→ 来源实例 → 环境默认。
        /// </summary>
        public static InstanceDef Create(InstancePlanInput input, AppSettings settings,
            IEnumerable<string> existingIds)
        {
            input = input ?? new InstancePlanInput();
            string id = MakeUniqueId(input.Name, existingIds);
            var def = new InstanceDef
            {
                Id = id,
                Name = string.IsNullOrWhiteSpace(input.Name) ? id : input.Name.Trim(),
                Runtime = input.IsWsl ? "wsl" : "windows",
                WslDistro = input.IsWsl ? (input.WslDistro ?? "").Trim() : "",
                Port = input.Port,
                HarnessVersion = (input.HarnessVersion ?? "").Trim(),
                CreatedAt = DateTime.UtcNow,
                AutoOpenBrowser = input.Source?.AutoOpenBrowser ?? true,
                StopOnExit = input.Source?.StopOnExit ?? true,
                Host = string.IsNullOrWhiteSpace(input.Source?.Host) ? "127.0.0.1" : input.Source.Host
            };

            string workspace = (input.Workspace ?? "").Trim();
            if (workspace.Length == 0)
                workspace = (settings?.EffectiveNewInstanceWorkspaceFor(input.IsWsl) ?? "").Trim();
            if (workspace.Length == 0 && input.Source != null) workspace = input.Source.Workspace ?? "";
            if (workspace.Length == 0 && input.IsWsl) workspace = WslWorkspaceRoot + "/" + id;

            if (input.IsWsl)
            {
                def.Workspace = workspace;
                def.WslHome = string.IsNullOrWhiteSpace(input.Home) ? WslHomeRoot + "/" + id : input.Home.Trim();
                def.Home = "";
            }
            else
            {
                def.Workspace = Config.SanitizePath(workspace);
                def.Home = Config.SanitizePath((input.Home ?? "").Trim());
                def.WslHome = "";
            }
            return def;
        }
    }
}
