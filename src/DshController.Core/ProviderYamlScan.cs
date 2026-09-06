// ============================================================================
//  ProviderYamlScan — 供应商同步用 settings.yaml 行扫描工具（llm-pi-ai 迁移轮拆分）
//
//  从 ProviderSyncMerge 拆出的纯文本扫描层：行切分/缩进/字段名值/根键与子键定位/
//  模型子块（dash 行）解析。只认本应用写入的简单 YAML 形状（2 空格缩进层级），
//  不做完整 YAML 解析——宽松段（compat 等）一律按行原样保留，不解释。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;

namespace DshController.Core
{
    /// <summary>llm-pi-ai 合并所依赖的缩进层级（并入后与渲染块 +4 一致）。</summary>
    internal static class ProviderYamlScan
    {
        internal const int RouteIndent = 6, ModelDash = 8, ModelField = 10;

        internal struct SubRange
        {
            public int Start;
            public int End;
        }

        internal static List<string> SplitLines(string text)
        {
            string normalized = (text ?? "").Replace(((char)13).ToString(), "");
            var lines = new List<string>(normalized.Split((char)10));
            while (lines.Count > 0 && lines[lines.Count - 1].Length == 0) lines.RemoveAt(lines.Count - 1);
            return lines;
        }

        internal static string JoinLines(List<string> lines)
        {
            return lines.Count == 0 ? "" : string.Join(Environment.NewLine, lines) + Environment.NewLine;
        }

        internal static List<string> IndentBlock(string block, int spaces)
        {
            string pad = new string(' ', spaces);
            return (block ?? "").Replace(((char)13).ToString(), "")
                .Split((char)10)
                .Select(l => l.Length == 0 ? "" : pad + l)
                .ToList();
        }

        internal static List<string> Prepend(List<string> head, List<string> tail)
        {
            var all = new List<string>(head);
            all.AddRange(tail);
            return all;
        }

        internal static int IndentOf(string line)
        {
            int i = 0;
            while (i < line.Length && (line[i] == ' ' || line[i] == '\t')) i++;
            return i;
        }

        internal static bool IsBlank(string line) { return line.Trim().Length == 0; }

        internal static string FieldNameOf(string line)
        {
            int c = line.IndexOf(':');
            string name = c < 0 ? line : line.Substring(0, c);
            return name.Trim().TrimStart('-').Trim();
        }

        internal static string FieldValueOf(string line)
        {
            int c = line.IndexOf(':');
            return c < 0 ? "" : line.Substring(c + 1).Trim();
        }

        /// <summary>找 0 缩进根键行（精确匹配 "name:"）。</summary>
        internal static int FindRootKey(List<string> lines, string rootKey)
        {
            for (int i = 0; i < lines.Count; i++)
            {
                if (lines[i].Length == 0 || lines[i][0] == ' ' || lines[i][0] == '\t') continue;
                if (lines[i].TrimEnd() == rootKey) return i;
            }
            return -1;
        }

        /// <summary>从 start 起第一个缩进 &lt; indent 的非空行（段落终点），否则 hardEnd。</summary>
        internal static int EndBelowIndent(List<string> lines, int start, int hardEnd, int indent)
        {
            for (int i = start; i < hardEnd; i++)
            {
                if (IsBlank(lines[i])) continue;
                if (IndentOf(lines[i]) < indent) return i;
            }
            return hardEnd;
        }

        /// <summary>找指定缩进的精确子键行（如 "  providers:"）。</summary>
        internal static int FindExactChild(List<string> lines, int start, int end, int indent, string name)
        {
            string target = new string(' ', indent) + name;
            for (int i = start; i < end; i++)
            {
                if (IsBlank(lines[i])) continue;
                int ind = IndentOf(lines[i]);
                if (ind < indent) break;
                if (ind == indent && lines[i].TrimEnd() == target) return i;
            }
            return -1;
        }

        /// <summary>在 [start,end) 找指定缩进、指定字段名的行，返回索引（-1=无）。</summary>
        internal static int FindField(List<string> lines, int start, int end, int indent, string field)
        {
            for (int i = start; i < end; i++)
            {
                if (IsBlank(lines[i])) continue;
                int ind = IndentOf(lines[i]);
                if (ind < indent) break;
                if (ind == indent && FieldNameOf(lines[i]) == field) return i;
            }
            return -1;
        }

        internal static string FindFieldLine(List<string> lines, int start, int end, int indent, string field)
        {
            int idx = FindField(lines, start, end, indent, field);
            return idx < 0 ? null : lines[idx];
        }

        internal static string FieldValueAt(List<string> lines, int start, int end, string field)
        {
            int idx = FindField(lines, start, end, ModelField, field);
            return idx < 0 ? null : FieldValueOf(lines[idx]);
        }

        /// <summary>解析 models 列表下的模型子块（dash 行起，止于下一个 dash 或列表终点）。</summary>
        internal static List<SubRange> ParseSubs(List<string> lines, int modelsIdx, int end, int dashIndent)
        {
            var subs = new List<SubRange>();
            int cur = -1;
            for (int i = modelsIdx + 1; i < end; i++)
            {
                string line = lines[i];
                if (IsBlank(line)) continue;
                int ind = IndentOf(line);
                if (ind < dashIndent) break;
                bool isDash = ind == dashIndent && line.TrimStart().StartsWith("- ");
                if (isDash)
                {
                    if (cur >= 0) subs.Add(new SubRange { Start = cur, End = i });
                    cur = i;
                }
            }
            if (cur >= 0) subs.Add(new SubRange { Start = cur, End = end });
            return subs;
        }

        /// <summary>子块 id：dash 行上的 "- id: x" 或子块内的 "id: x" 字段行。</summary>
        internal static string SubId(List<string> lines, SubRange sub)
        {
            int dashIndent = IndentOf(lines[sub.Start]);
            string dash = lines[sub.Start].TrimStart();
            if (dash.StartsWith("- ", StringComparison.Ordinal))
            {
                string head = dash.Substring(2).Trim();
                if (head.StartsWith("id:", StringComparison.Ordinal))
                    return Unquote(head.Substring(3).Trim());
            }
            for (int i = sub.Start + 1; i < sub.End; i++)
            {
                if (IsBlank(lines[i])) continue;
                int ind = IndentOf(lines[i]);
                if (ind <= dashIndent) break;
                if (FieldNameOf(lines[i]) == "id") return Unquote(FieldValueOf(lines[i]));
            }
            return "";
        }

        internal static string Unquote(string s)
        {
            string t = (s ?? "").Trim();
            if (t.Length >= 2 && t[0] == '\'' && t[t.Length - 1] == '\'') return t.Substring(1, t.Length - 2).Replace("''", "'");
            if (t.Length >= 2 && t[0] == '"' && t[t.Length - 1] == '"') return t.Substring(1, t.Length - 2);
            return t;
        }

        /// <summary>在既有子块里按 id 找（大小写不敏感）；找不到返回 Start=-1。</summary>
        internal static SubRange FindSubById(List<string> lines, int modelsIdx, int end, string id)
        {
            foreach (SubRange s in ParseSubs(lines, modelsIdx, end, ModelDash))
            {
                if (string.Equals(SubId(lines, s), id, StringComparison.OrdinalIgnoreCase)) return s;
            }
            return new SubRange { Start = -1, End = -1 };
        }

        /// <summary>models 列表终点（下一个缩进 &lt; dash 的非空行），新模型子块插到这之前。</summary>
        internal static int ModelsListEnd(List<string> lines, int modelsIdx, int end)
        {
            for (int i = modelsIdx + 1; i < end; i++)
            {
                if (IsBlank(lines[i])) continue;
                if (IndentOf(lines[i]) < ModelDash) return i;
            }
            return end;
        }
    }
}
