// ============================================================================
//  PluginCompat — 插件与 harness 的版本兼容判定与展示（v0.6.0 插件市场）
//
//  原则（与目录数据规范一致）：插件仓库明确声明了支持版本就原样展示，绝不替
//  插件推测。声明来源两级：
//    ① 目录条目 minHost 字段（来源标注"仓库声明"）；
//    ② minHost 缺失时查 npm registry 包元数据 peerDependencies/engines 中对
//       @deepseek-ai/dsh 的范围声明，取其下界（来源标注"包元数据"）；
//    ③ 两者都没有 → "未声明支持的 DSH 版本"。
//  与实例 harness 版本（HarnessVersion.Resolve* 探测值）比对给出三态：
//  兼容 / 实例版本低于要求 / 实例版本未检测。
// ============================================================================

using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace DshController.Core
{
    public enum CompatState
    {
        Compatible,      // 实例版本 ≥ 声明版本
        Incompatible,    // 实例版本 < 声明版本
        UnknownInstance, // 有声明，但实例版本未检测到
        NotDeclared      // 仓库未声明支持版本
    }

    /// <summary>一次兼容判定的结果（UI 直接展示 Text，禁止再加工）。</summary>
    public sealed class CompatInfo
    {
        public CompatState State { get; set; }
        public string Text { get; set; } = "";
        public string Declared { get; set; } = "";   // 声明值原样（如 ">=0.4.2" 归一为 0.4.2）
        public string Source { get; set; } = "";     // "仓库声明" | "包元数据" | ""
    }

    public static class PluginCompat
    {
        /// <summary>npm registry 包元数据查询缓存（pkg → 声明版本下界；空串 = 查过但没有）。</summary>
        private static readonly ConcurrentDictionary<string, string> MetaCache =
            new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private static readonly Regex SemverRx = new Regex(
            @"^(?<v>\d+\.\d+\.\d+(?:-[0-9A-Za-z][0-9A-Za-z.-]*)?)",
            RegexOptions.Compiled);

        /// <summary>在任意字符串（版本范围声明、README 片段等）中找第一个 semver，不锚定开头。</summary>
        private static readonly Regex SemverFindRx = new Regex(
            @"(?<v>\d+\.\d+\.\d+(?:-[0-9A-Za-z][0-9A-Za-z.-]*)?)",
            RegexOptions.Compiled);

        // ---------------- 版本比较（供判定与自检） ----------------

        /// <summary>
        /// semver 比较：a&lt;b 返回负数，相等 0，a&gt;b 返回正数。解析失败按未知处理
        /// （双方都失败返回 0；单方失败视为该方"更小"）。预发布版 &lt; 同号正式版。
        /// </summary>
        public static int CompareVersions(string a, string b)
        {
            bool oa = TryParseSemver(a, out int am, out int an, out int ap, out string apre);
            bool ob = TryParseSemver(b, out int bm, out int bn, out int bp, out string bpre);
            if (!oa && !ob) return 0;
            if (!oa) return -1;
            if (!ob) return 1;
            int c = am.CompareTo(bm); if (c != 0) return c;
            c = an.CompareTo(bn); if (c != 0) return c;
            c = ap.CompareTo(bp); if (c != 0) return c;
            // 预发布 < 正式；同为预发布按段比较（数字段按数值，文本段按字典序）
            if (apre.Length == 0 && bpre.Length == 0) return 0;
            if (apre.Length == 0) return 1;
            if (bpre.Length == 0) return -1;
            return string.CompareOrdinal(apre, bpre);
        }

        private static bool TryParseSemver(string s, out int maj, out int min, out int pat, out string pre)
        {
            maj = 0; min = 0; pat = 0; pre = "";
            string v = (s ?? "").Trim();
            if (v.StartsWith("v", StringComparison.OrdinalIgnoreCase)) v = v.Substring(1);
            Match m = SemverRx.Match(v);
            if (!m.Success) return false;
            string core = m.Groups["v"].Value;
            int dash = core.IndexOf('-');
            if (dash >= 0) { pre = core.Substring(dash + 1); core = core.Substring(0, dash); }
            string[] parts = core.Split('.');
            if (parts.Length != 3) return false;
            if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out maj)) return false;
            if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out min)) return false;
            if (!int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out pat)) return false;
            return true;
        }

        /// <summary>从版本范围声明（^0.4.2 / >=0.4.0 <0.5.0 / ~1.2.3 等）提取第一个 semver 下界。</summary>
        public static string FloorOfRange(string range)
        {
            Match m = SemverFindRx.Match(range ?? "");
            return m.Success ? m.Groups["v"].Value : "";
        }

        // ---------------- 判定 ----------------

        /// <summary>
        /// 同步判定：只用目录条目自带的 minHost（不联网）。instanceVersion 为实例
        /// harness 探测版本，空 = 未检测。
        /// </summary>
        public static CompatInfo Judge(CatalogEntry entry, string instanceVersion)
        {
            string declared = FloorOfRange(entry?.MinHost);
            if (declared.Length == 0)
            {
                return new CompatInfo
                {
                    State = CompatState.NotDeclared,
                    Text = "未声明支持的 DSH 版本"
                };
            }
            return JudgeDeclared(declared, "仓库声明", instanceVersion);
        }

        /// <summary>
        /// 完整判定：minHost 缺失时联网查 npm 包元数据（带缓存，失败静默降级为未声明）。
        /// </summary>
        public static async Task<CompatInfo> JudgeDetailedAsync(CatalogEntry entry, string instanceVersion)
        {
            CompatInfo sync = Judge(entry, instanceVersion);
            if (sync.State != CompatState.NotDeclared) return sync;

            string pkg = (entry?.Pkg ?? "").Trim();
            if (pkg.Length == 0) return sync;

            string floor = await DeclaredFloorFromNpmAsync(pkg).ConfigureAwait(false);
            if (floor.Length == 0)
                return sync; // 保持"未声明"
            return JudgeDeclared(floor, "包元数据", instanceVersion);
        }

        private static CompatInfo JudgeDeclared(string declared, string source, string instanceVersion)
        {
            string inst = HarnessVersion.Parse(instanceVersion ?? "");
            if (inst.Length == 0)
            {
                return new CompatInfo
                {
                    State = CompatState.UnknownInstance,
                    Declared = declared,
                    Source = source,
                    Text = "支持 DSH ≥ " + declared + "（" + source + "）· 实例版本未检测"
                };
            }
            bool ok = CompareVersions(inst, declared) >= 0;
            return new CompatInfo
            {
                State = ok ? CompatState.Compatible : CompatState.Incompatible,
                Declared = declared,
                Source = source,
                Text = "支持 DSH ≥ " + declared + "（" + source + "）· 实例 v" + inst +
                       (ok ? " 兼容" : " 低于要求")
            };
        }

        /// <summary>查 npm registry latest 元数据中 @deepseek-ai/dsh 的范围声明下界（失败/没有返回空串）。</summary>
        public static async Task<string> DeclaredFloorFromNpmAsync(string pkg)
        {
            string key = (pkg ?? "").Trim();
            if (key.Length == 0) return "";
            if (MetaCache.TryGetValue(key, out string cached)) return cached ?? "";

            string url = "https://registry.npmjs.org/" + Uri.EscapeDataString(key) + "/latest";
            string json = await HttpFetch.GetStringAsync(url, 12000).ConfigureAwait(false);
            string floor = "";
            if (!string.IsNullOrEmpty(json))
            {
                floor = ExtractHostRangeFloor(json);
            }
            MetaCache[key] = floor;
            return floor;
        }

        /// <summary>从 npm 包元数据 JSON 中提取对 @deepseek-ai/dsh 的声明下界（peerDependencies/engines）。</summary>
        public static string ExtractHostRangeFloor(string packageJson)
        {
            try
            {
                using (var doc = System.Text.Json.JsonDocument.Parse(packageJson))
                {
                    string floor = SearchHostRange(doc.RootElement, "peerDependencies");
                    if (floor.Length == 0) floor = SearchHostRange(doc.RootElement, "engines");
                    return floor;
                }
            }
            catch
            {
                return "";
            }
        }

        private static string SearchHostRange(System.Text.Json.JsonElement root, string section)
        {
            try
            {
                if (root.ValueKind != System.Text.Json.JsonValueKind.Object) return "";
                if (!root.TryGetProperty(section, out var sec) ||
                    sec.ValueKind != System.Text.Json.JsonValueKind.Object) return "";
                foreach (var kv in sec.EnumerateObject())
                {
                    string name = kv.Name;
                    // 命中 @deepseek-ai/dsh / dsh 等对宿主的声明；其余（node 等）跳过
                    if (name.IndexOf("dsh", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (kv.Value.ValueKind == System.Text.Json.JsonValueKind.String)
                    {
                        string floor = FloorOfRange(kv.Value.GetString());
                        if (floor.Length > 0) return floor;
                    }
                }
            }
            catch { }
            return "";
        }
    }
}
