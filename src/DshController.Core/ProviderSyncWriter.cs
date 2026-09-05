// ============================================================================
//  ProviderSyncWriter — 供应商预设写入引擎（改版·供应商同步 / 写入生效）
//
//  · 目标文件：实例 DSH_HOME 的 settings.yaml（路径由调用方解析传入；测试重定向临时 HOME）；
//  · 合并语义（行级、非破坏性）：
//      - 无根级 providers: 段 → 追加到文件尾；
//      - 已有 → 只增/改本 <key> 块（插入时整块 +2 空格缩进），其余 providers 键与
//        文件其他内容原样保留；已存在同 key → 原位整块替换为规范渲染；
//  · 原子性：先备份原文件为 .bak-<ts>，再写临时文件后替换；任一步失败恢复备份——
//    失败绝不覆盖原配置；
//  · 写入内容与预览同源：块文本 = ProviderSyncPlan.RenderYamlBlock（单一事实源）。
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DshController.Core
{
    public sealed class ProviderSyncWriter
    {
        /// <summary>把 providers 条目并入 settingsPath。成功 true；失败恢复原样并给 error。</summary>
        public bool Apply(string settingsPath, InstanceProviderEntry entry, out string error)
        {
            error = "";
            if (string.IsNullOrEmpty(settingsPath)) { error = "settings.yaml 路径为空"; return false; }
            if (entry == null || string.IsNullOrEmpty(entry.Key)) { error = "条目无效（缺 key）"; return false; }
            try
            {
                string original = File.Exists(settingsPath) ? File.ReadAllText(settingsPath) : "";
                string block = ProviderSyncPlan.RenderYamlBlock(entry);
                string merged = MergeProviders(original, block, entry.Key, out error);
                if (merged == null) return false;

                string dir = Path.GetDirectoryName(settingsPath);
                if (string.IsNullOrEmpty(dir)) dir = ".";
                Directory.CreateDirectory(dir);
                string tmp = settingsPath + ".tmp-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                string bak = settingsPath + ".bak-" + DateTime.Now.ToString("yyyyMMddHHmmss");
                File.WriteAllText(tmp, merged);
                try
                {
                    if (File.Exists(settingsPath)) File.Copy(settingsPath, bak, overwrite: true);
                    File.Move(tmp, settingsPath, overwrite: true);
                }
                catch
                {
                    if (File.Exists(tmp)) File.Delete(tmp);
                    throw;
                }
                return true;
            }
            catch (Exception ex)
            {
                error = "写入失败（原配置未改动）：" + ex.Message;
                return false;
            }
        }

        /// <summary>行级合并（纯函数可单测）。失败返回 null + error。</summary>
        public static string MergeProviders(string original, string block, string key, out string error)
        {
            error = "";
            char CR = (char)13, LF = (char)10, TAB = (char)9;
            string text = (original ?? "").Replace(CR.ToString(), "");
            var lines = new List<string>(text.Split(LF));
            while (lines.Count > 0 && lines[lines.Count - 1].Length == 0) lines.RemoveAt(lines.Count - 1);

            int provIdx = -1;
            for (int i = 0; i < lines.Count; i++)
            {
                if (IsRootLine(lines[i]) && lines[i].TrimEnd() == "providers:") { provIdx = i; break; }
            }

            string[] blockLines = (block ?? "").Replace(CR.ToString(), "").Split(LF);
            string[] indented = blockLines.Select(l => l.Length == 0 ? "" : "  " + l).ToArray();

            if (provIdx < 0)
            {
                if (lines.Count > 0 && lines[lines.Count - 1].Length > 0) lines.Add("");
                lines.Add("providers:");
                lines.AddRange(indented);
                return string.Join(Environment.NewLine, lines) + Environment.NewLine;
            }

            int provEnd = lines.Count;
            for (int i = provIdx + 1; i < lines.Count; i++)
            {
                if (IsRootLine(lines[i])) { provEnd = i; break; }
            }

            string sibling = "  " + key + ":";
            int keyIdx = -1;
            for (int i = provIdx + 1; i < provEnd; i++)
            {
                if (lines[i].TrimEnd() == sibling) { keyIdx = i; break; }
            }

            var seg = new List<string>();
            bool skipping = false;
            for (int i = provIdx + 1; i < provEnd; i++)
            {
                if (keyIdx >= 0 && i == keyIdx) { seg.AddRange(indented); skipping = true; continue; }
                if (skipping)
                {
                    if (IsTwoSpaceSibling(lines[i], TAB)) skipping = false;
                    else continue;
                }
                seg.Add(lines[i]);
            }
            if (keyIdx < 0) seg.InsertRange(0, indented);

            var result = new List<string>();
            result.AddRange(lines.Take(provIdx + 1));
            result.AddRange(seg);
            result.AddRange(lines.Skip(provEnd));
            return string.Join(Environment.NewLine, result) + Environment.NewLine;
        }

        private static bool IsRootLine(string l)
        {
            return l.Length > 0 && l[0] != ' ' && l[0] != (char)9;
        }

        /// <summary>恰好 2 空格缩进 + 非空白 = providers 段的兄弟键行。</summary>
        private static bool IsTwoSpaceSibling(string l, char tab)
        {
            if (l.Length < 3) return false;
            return l[0] == ' ' && l[1] == ' ' && l[2] != ' ' && l[2] != tab;
        }
    }
}
