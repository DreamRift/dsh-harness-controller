// ============================================================================
//  ProviderSyncMerge — 供应商同步的行级合并纯函数（API 页 llm-pi-ai 迁移轮）
//
//  · 权威目标（dsh 源码证据）：自定义提供方由 dsh-llm-pi-ai 适配器从
//    settings.yaml 的 `llm-pi-ai.providers.<key>` 读取；根级 `providers:` 无任何
//    消费方（历史误同步留下的死配置）；
//  · MergePiAiProviders：把渲染块并入 llm-pi-ai.providers.<key>，同 key 已存在时
//    做"增补式合并"——路由字段更新；模型按 id 匹配，contextWindow/maxTokens/name
//    以预设为准，compat 等未知字段与已有 reasoningEfforts 声明（含 false）原样
//    保留（对齐 dsh-thinking-efforts"用户声明不覆盖"），input 空数组视为未声明；
//  · SetLlmDeepseekApiKeyEnv：官方预设同步 = 只写 llm-deepseek.apiKeyEnv
//    （官方模型/思考档/多模态由实例原生适配器提供）；
//  · RemoveRootProvidersKey：迁移清理——删除根级 providers.<key> 旧块，段空则连段移除；
//  · 全部为纯函数（行级、非破坏性、幂等），可离线单测；文件 IO 与原子备份在
//    ProviderSyncWriter（失败绝不覆盖原配置）。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using static DshController.Core.ProviderYamlScan;

namespace DshController.Core
{
    public static class ProviderSyncMerge
    {
        // 渲染块经 IndentBlock(+4) 后与实例侧实际缩进一致：
        // key 4 / 路由字段 6 / models 6 / 模型 dash 8 / 模型字段 10 / reasoningEfforts 键值 12。

        /// <summary>把渲染块并入 settings 文本的 llm-pi-ai.providers.<key>。失败返回 null + error。</summary>
        public static string MergePiAiProviders(string original, string block, string key, out string error)
        {
            error = "";
            if (string.IsNullOrWhiteSpace(key)) { error = "provider key 为空"; return null; }
            List<string> lines = SplitLines(original);
            List<string> rendered = IndentBlock(block, 4);
            if (rendered.Count == 0) { error = "渲染块为空"; return null; }

            int nsIdx = FindRootKey(lines, "llm-pi-ai:");
            if (nsIdx < 0)
            {
                if (lines.Count > 0 && lines[lines.Count - 1].Trim().Length > 0) lines.Add("");
                lines.Add("llm-pi-ai:");
                lines.Add("  providers:");
                lines.AddRange(rendered);
                return JoinLines(lines);
            }
            int nsEnd = EndBelowIndent(lines, nsIdx + 1, lines.Count, 2);

            int provIdx = FindExactChild(lines, nsIdx + 1, nsEnd, 2, "providers:");
            if (provIdx < 0)
            {
                lines.InsertRange(nsEnd, Prepend(new List<string> { "  providers:" }, rendered));
                return JoinLines(lines);
            }
            int provEnd = EndBelowIndent(lines, provIdx + 1, nsEnd, 4);

            string keyLine = "    " + key + ":";
            int keyIdx = -1;
            for (int i = provIdx + 1; i < provEnd; i++)
            {
                if (lines[i].TrimEnd() == keyLine) { keyIdx = i; break; }
            }
            if (keyIdx < 0)
            {
                lines.InsertRange(provEnd, rendered);
                return JoinLines(lines);
            }

            int keyEnd = EndBelowIndent(lines, keyIdx + 1, provEnd, RouteIndent);
            var cur = lines.GetRange(keyIdx, keyEnd - keyIdx);
            var merged = MergeKeyBlock(cur, rendered, out error);
            if (merged == null) return null;
            lines.RemoveRange(keyIdx, keyEnd - keyIdx);
            lines.InsertRange(keyIdx, merged);
            return JoinLines(lines);
        }

        /// <summary>官方预设：写/更新 llm-deepseek 段的 apiKeyEnv（段缺失则在文件尾追加）。</summary>
        public static string SetLlmDeepseekApiKeyEnv(string original, string envName, out string error)
        {
            error = "";
            if (string.IsNullOrWhiteSpace(envName)) { error = "apiKeyEnv 名为空"; return null; }
            List<string> lines = SplitLines(original);
            string line = "  apiKeyEnv: " + envName.Trim();
            int nsIdx = FindRootKey(lines, "llm-deepseek:");
            if (nsIdx < 0)
            {
                if (lines.Count > 0 && lines[lines.Count - 1].Trim().Length > 0) lines.Add("");
                lines.Add("llm-deepseek:");
                lines.Add(line);
                return JoinLines(lines);
            }
            int nsEnd = EndBelowIndent(lines, nsIdx + 1, lines.Count, 2);
            for (int i = nsIdx + 1; i < nsEnd; i++)
            {
                if (IsBlank(lines[i])) continue;
                int ind = IndentOf(lines[i]);
                if (ind < 2) break;
                if (ind == 2 && FieldNameOf(lines[i]) == "apiKeyEnv") { lines[i] = line; return JoinLines(lines); }
            }
            lines.Insert(nsIdx + 1, line);
            return JoinLines(lines);
        }

        /// <summary>迁移清理：删除根级 providers.<key> 旧块；段空则连 providers: 段一起移除。</summary>
        public static string RemoveRootProvidersKey(string original, string key, out string error)
        {
            error = "";
            if (string.IsNullOrWhiteSpace(key)) { error = "provider key 为空"; return null; }
            List<string> lines = SplitLines(original);
            int provIdx = FindRootKey(lines, "providers:");
            if (provIdx < 0) return JoinLines(lines);
            int provEnd = EndBelowIndent(lines, provIdx + 1, lines.Count, 2);

            string sibling = "  " + key.Trim() + ":";
            int keyIdx = -1;
            for (int i = provIdx + 1; i < provEnd; i++)
            {
                if (IsBlank(lines[i])) continue;
                int ind = IndentOf(lines[i]);
                if (ind < 2) break;
                if (ind == 2 && lines[i].TrimEnd() == sibling) { keyIdx = i; break; }
            }
            if (keyIdx < 0) return JoinLines(lines);
            int keyEnd = EndBelowIndent(lines, keyIdx + 1, provEnd, 4);
            lines.RemoveRange(keyIdx, keyEnd - keyIdx);

            int remainEnd = provEnd - (keyEnd - keyIdx);
            bool anyChild = false;
            for (int i = provIdx + 1; i < remainEnd && i < lines.Count; i++)
            {
                if (!IsBlank(lines[i])) { anyChild = true; break; }
            }
            if (!anyChild)
            {
                lines.RemoveRange(provIdx, Math.Min(remainEnd, lines.Count) - provIdx);
                while (provIdx > 0 && provIdx < lines.Count
                    && IsBlank(lines[provIdx]) && IsBlank(lines[provIdx - 1]))
                    lines.RemoveAt(provIdx);
            }
            return JoinLines(lines);
        }

        // ---------------- 增补式合并：单个 provider key 块 ----------------

        private static List<string> MergeKeyBlock(List<string> cur, List<string> rendered, out string error)
        {
            error = "";
            ApplyRouteFields(cur, rendered);

            int renModels = FindField(rendered, 1, rendered.Count, RouteIndent, "models");
            if (renModels < 0) return cur;   // 预设无模型目录：实例侧目录原样保留

            int curModels = FindField(cur, 1, cur.Count, RouteIndent, "models");
            if (curModels < 0)
            {
                for (int i = renModels; i < rendered.Count; i++) cur.Add(rendered[i]);
                return cur;
            }

            foreach (SubRange rs in ParseSubs(rendered, renModels, rendered.Count, ModelDash))
            {
                string id = SubId(rendered, rs);
                if (id.Length == 0) continue;
                // 每个模型都重新定位（前面的插入会让索引漂移），换取实现简单与稳定；
                // 起点跳过 key 行（0 号位缩进更浅会让 FindField 提前 break）
                int cm = FindField(cur, 1, cur.Count, RouteIndent, "models");
                SubRange cs = FindSubById(cur, cm, cur.Count, id);
                if (cs.Start < 0)
                {
                    int insertAt = ModelsListEnd(cur, cm, cur.Count);
                    var chunk = new List<string>();
                    for (int i = rs.Start; i < rs.End; i++) chunk.Add(rendered[i]);
                    cur.InsertRange(insertAt, chunk);
                }
                else
                {
                    MergeSubFields(cur, cs, rendered, rs);
                }
            }
            return cur;
        }

        /// <summary>路由级字段（displayName/api/apiKeyEnv/baseURL）：按规范顺序替换或插入；
        /// 实例侧自有字段原样保留。</summary>
        private static void ApplyRouteFields(List<string> cur, List<string> rendered)
        {
            string[] order = { "displayName", "api", "apiKeyEnv", "baseURL" };
            int anchor = 0;   // cur[0] = key 行
            foreach (string field in order)
            {
                string renLine = FindFieldLine(rendered, 1, rendered.Count, RouteIndent, field);
                int ex = FindField(cur, 1, cur.Count, RouteIndent, field);
                if (renLine != null)
                {
                    string line = new string(' ', RouteIndent) + renLine.TrimStart();
                    if (ex >= 0) { cur[ex] = line; anchor = ex; }
                    else { cur.Insert(anchor + 1, line); anchor++; }
                }
                else if (ex >= 0)
                {
                    anchor = ex;   // 预设未给（如 BaseUrl 空）：保留实例侧现值
                }
            }
        }

        /// <summary>模型子块字段级合并：容量/名称预设权威；reasoningEfforts 已有（含 false）
        /// 不覆盖；input 非空既有声明保留、`[]` 视为未声明可写入。</summary>
        private static void MergeSubFields(List<string> cur, SubRange cs, List<string> rendered, SubRange rs)
        {
            int subEnd = cs.End;   // 本子块终点（插入后随行数增长）
            for (int i = rs.Start + 1; i < rs.End; )
            {
                string line = rendered[i];
                if (IsBlank(line)) { i++; continue; }
                int ind = IndentOf(line);
                if (ind != ModelField) { i++; continue; }
                string field = FieldNameOf(line);
                if (field == "reasoningEfforts")
                {
                    int j = i + 1;
                    while (j < rs.End && (IsBlank(rendered[j]) || IndentOf(rendered[j]) > ModelField)) j++;
                    int has = FindField(cur, cs.Start + 1, subEnd, ModelField, "reasoningEfforts");
                    if (has < 0)
                    {
                        var chunk = new List<string>();
                        for (int k = i; k < j; k++) chunk.Add(rendered[k]);
                        cur.InsertRange(subEnd, chunk);
                        subEnd += chunk.Count;
                    }
                    i = j;
                    continue;
                }
                if (field == "input")
                {
                    string curVal = FieldValueAt(cur, cs.Start + 1, subEnd, "input");
                    bool undeclared = curVal == null || curVal == "[]" || curVal.Length == 0;
                    if (undeclared)
                    {
                        int ex = FindField(cur, cs.Start + 1, subEnd, ModelField, "input");
                        if (ex >= 0) cur[ex] = line;
                        else { cur.Insert(subEnd, line); subEnd++; }
                    }
                    i++;
                    continue;
                }
                // name/contextWindow/maxTokens：预设权威，替换或插入
                int exists = FindField(cur, cs.Start + 1, subEnd, ModelField, field);
                if (exists >= 0) cur[exists] = line;
                else { cur.Insert(subEnd, line); subEnd++; }
                i++;
            }
        }

    }
}
