// ============================================================================
//  InstalledPlugins — 已装插件的黑盒读取（v0.6.0 插件市场）
//
//  与 DshController 一贯的"dsh 黑盒"边界一致：不解析 dsh 内部协议、不跑额外
//  的插件管理命令，已装状态直接读实例 HOME 文件系统：
//    - <HOME>/profiles/<profile>/package.json
//        dependencies 的 key = 插件包名；dsh.profile.bundles = 生效 bundle 列表
//    - <HOME>/profiles/<profile>/node_modules/<pkg>/package.json 的 version
//      字段 = 已装版本（junction/软链会自动穿透；pnpm 语义下也可能取不到 → 空）
//  官方基础包（@deepseek-ai/*）与第三方插件同表展示，来源徽标由市场安装记录
//  （PluginRecords）匹配得出。
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace DshController.Core
{
    /// <summary>实例某个 profile 下已装的一个包。</summary>
    public sealed class InstalledPlugin
    {
        /// <summary>包名（package.json dependencies 的 key，dsh plugin remove 用它）。</summary>
        public string Pkg { get; set; } = "";

        /// <summary>已装版本；空 = 未能从 node_modules 读到。</summary>
        public string Version { get; set; } = "";

        /// <summary>是否出现在 dsh.profile.bundles（bundle 生效清单）。</summary>
        public bool InBundles { get; set; }

        /// <summary>是否 DSH 官方基础包（@deepseek-ai/ 前缀；禁止卸载）。</summary>
        public bool IsOfficial { get; set; }

        /// <summary>dependencies 里的原始依赖声明（^x.y.z / file:... / link:...）。</summary>
        public string DepRef { get; set; } = "";

        /// <summary>本地链接插件（file:/link: 依赖，多为手工开发装配）。</summary>
        public bool LocalLink => (DepRef ?? "").StartsWith("file:", StringComparison.OrdinalIgnoreCase)
                              || (DepRef ?? "").StartsWith("link:", StringComparison.OrdinalIgnoreCase);

        // ---- 以下字段来自市场安装记录匹配（空/缺省 = 非市场来源） ----

        /// <summary>"market" = 通过插件市场安装；"" = 手动/其他来源。</summary>
        public string SourceMark { get; set; } = "";

        public string Repo { get; set; } = "";

        public string MarketName { get; set; } = "";

        public DateTime? InstalledAt { get; set; }

        /// <summary>UI 来源徽标文案。</summary>
        public string SourceText
        {
            get
            {
                if (SourceMark == "market")
                    return "市场安装" + (InstalledAt.HasValue
                        ? " · " + InstalledAt.Value.ToLocalTime().ToString("yyyy-MM-dd") : "");
                if (IsOfficial) return "官方基础包";
                if (LocalLink) return "本地链接";
                return "手动安装";
            }
        }
    }

    public static class InstalledPlugins
    {
        private static readonly Regex VerRx = new Regex(
            @"""version""\s*:\s*""(?<v>[^""]+)""", RegexOptions.Compiled);

        /// <summary>
        /// 读取 Windows 实例已装插件。home 为解析后的 DSH_HOME 绝对路径
        /// （实例 home 为空时调用方传 %USERPROFILE%\.dsh）。
        /// </summary>
        public static async Task<List<InstalledPlugin>> ReadWindowsAsync(string home, string profile,
            List<PluginRecord> records)
        {
            var result = new List<InstalledPlugin>();
            if (string.IsNullOrWhiteSpace(home)) return result;

            string pkgJson = Path.Combine(home, "profiles", profile ?? "web", "package.json");
            if (!File.Exists(pkgJson)) return result;
            string json;
            try { json = File.ReadAllText(pkgJson, Encoding.UTF8); }
            catch { return result; }

            List<InstalledPlugin> parsed = ParseProfilePackage(json);
            foreach (InstalledPlugin p in parsed)
                p.Version = ReadVersionWindows(home, profile ?? "web", p.Pkg);
            Decorate(parsed, records, profile ?? "web");
            return parsed;
        }

        /// <summary>
        /// 读取 WSL 实例已装插件。linuxHome 为发行版内 DSH_HOME 绝对路径
        /// （实例 wslHome 为空时可不注入 DSH_HOME——本方法接受 null 直接跳过读取）。
        /// </summary>
        public static async Task<List<InstalledPlugin>> ReadWslAsync(string distro, string linuxHome,
            string profile, List<PluginRecord> records)
        {
            var result = new List<InstalledPlugin>();
            if (string.IsNullOrWhiteSpace(distro) || string.IsNullOrWhiteSpace(linuxHome)) return result;

            string pkgJsonPath = linuxHome.TrimEnd('/') + "/profiles/" + (profile ?? "web") + "/package.json";
            string json = await WslTools.ReadDistroFileAsync(distro, pkgJsonPath).ConfigureAwait(false);
            if (string.IsNullOrEmpty(json)) return result;

            List<InstalledPlugin> parsed = ParseProfilePackage(json);
            if (parsed.Count > 0)
            {
                Dictionary<string, string> versions = await ReadVersionsWslAsync(distro, linuxHome, profile ?? "web",
                    parsed.Select(p => p.Pkg).ToList()).ConfigureAwait(false);
                foreach (InstalledPlugin p in parsed)
                    p.Version = versions.TryGetValue(p.Pkg, out string v) ? v : "";
            }
            Decorate(parsed, records, profile ?? "web");
            return parsed;
        }

        // ---------------- 解析 profile package.json（自检共用） ----------------

        /// <summary>
        /// 解析 profile package.json：dependencies 全量（含官方基础包）+ bundles 归属。
        /// 解析失败返回空表。
        /// </summary>
        public static List<InstalledPlugin> ParseProfilePackage(string json)
        {
            var list = new List<InstalledPlugin>();
            if (string.IsNullOrWhiteSpace(json)) return list;
            try
            {
                using (var doc = JsonDocument.Parse(json))
                {
                    var root = doc.RootElement;
                    var deps = new List<KeyValuePair<string, string>>();
                    if (root.TryGetProperty("dependencies", out var depEl) &&
                        depEl.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var kv in depEl.EnumerateObject())
                        {
                            deps.Add(new KeyValuePair<string, string>(
                                kv.Name,
                                kv.Value.ValueKind == JsonValueKind.String ? kv.Value.GetString() ?? "" : ""));
                        }
                    }

                    var bundles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    if (root.TryGetProperty("dsh", out var dshEl) &&
                        dshEl.ValueKind == JsonValueKind.Object &&
                        dshEl.TryGetProperty("profile", out var profEl) &&
                        profEl.ValueKind == JsonValueKind.Object &&
                        profEl.TryGetProperty("bundles", out var bunEl) &&
                        bunEl.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var b in bunEl.EnumerateArray())
                            if (b.ValueKind == JsonValueKind.String)
                                bundles.Add(b.GetString() ?? "");
                    }

                    foreach (var kv in deps)
                    {
                        if (string.IsNullOrWhiteSpace(kv.Key)) continue;
                        list.Add(new InstalledPlugin
                        {
                            Pkg = kv.Key,
                            DepRef = kv.Value,
                            InBundles = bundles.Contains(kv.Key),
                            IsOfficial = kv.Key.StartsWith("@deepseek-ai/", StringComparison.OrdinalIgnoreCase)
                        });
                    }
                }
            }
            catch
            {
                return new List<InstalledPlugin>();
            }
            // 包名排序展示（官方基础包排前面）
            list.Sort((a, b) =>
            {
                int c = b.IsOfficial.CompareTo(a.IsOfficial);   // true 在前 = 官方包优先
                if (c != 0) return c;
                return string.Compare(a.Pkg, b.Pkg, StringComparison.OrdinalIgnoreCase);
            });
            return list;
        }

        private static void Decorate(List<InstalledPlugin> list, List<PluginRecord> records, string profile)
        {
            if (records == null || records.Count == 0) return;
            foreach (InstalledPlugin p in list)
            {
                // 记录按 profile 过滤（同一实例可能装在多个 profile）
                PluginRecord rec = records.FirstOrDefault(r =>
                    string.Equals(r.Pkg, p.Pkg, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals((r.Profile ?? "web"), profile, StringComparison.OrdinalIgnoreCase))
                    ?? records.FirstOrDefault(r =>
                        string.Equals(r.Pkg, p.Pkg, StringComparison.OrdinalIgnoreCase));
                if (rec == null) continue;
                p.SourceMark = "market";
                p.Repo = rec.Repo ?? "";
                p.MarketName = rec.Name ?? "";
                p.InstalledAt = rec.InstalledAt;
            }
        }

        // ---------------- 版本读取 ----------------

        private static string ReadVersionWindows(string home, string profile, string pkg)
        {
            try
            {
                string pkgPath = PkgToRelPath(pkg);
                string file = Path.Combine(home, "profiles", profile, "node_modules", pkgPath, "package.json");
                if (!File.Exists(file)) return "";
                string text = File.ReadAllText(file, Encoding.UTF8);
                Match m = VerRx.Match(text);
                return m.Success ? m.Groups["v"].Value : "";
            }
            catch
            {
                return "";
            }
        }

        /// <summary>一次 wsl 调用批量读多个包的版本行；返回 pkg → version（读不到不出现）。</summary>
        private static async Task<Dictionary<string, string>> ReadVersionsWslAsync(string distro,
            string linuxHome, string profile, List<string> pkgs)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (pkgs == null || pkgs.Count == 0) return map;

            var sb = new StringBuilder();
            sb.Append("for d in");
            foreach (string p in pkgs) sb.Append(' ').Append(WslTools.Shq(p));
            string baseDir = ShqWrap(linuxHome.TrimEnd('/') + "/profiles/" + profile + "/node_modules");
            sb.Append("; do printf '=%s\\n' \"$d\"; cat ")
              .Append(baseDir).Append("/\"$d\"/package.json 2>/dev/null | head -c 4000; echo; done");
            var r = await WslTools.RunInDistroAsync(distro, sb.ToString(), 60000).ConfigureAwait(false);
            if (r == null || !r.Ok) return map;

            string current = null;
            foreach (string raw in (r.Output ?? "").Replace("\r", "").Split('\n'))
            {
                string line = raw.TrimEnd();
                if (line.StartsWith("=", StringComparison.Ordinal))
                {
                    current = line.Substring(1).Trim();
                    continue;
                }
                if (current == null || line.Trim().Length == 0) continue;
                Match m = VerRx.Match(line);
                if (m.Success && !map.ContainsKey(current))
                    map[current] = m.Groups["v"].Value;
                current = null; // 每个 cat 块只取第一个 version 命中
            }
            return map;
        }

        /// <summary>bash 单引号包装（复用 WslTools.Shq 的语义，避免调用处忘了包装）。</summary>
        private static string ShqWrap(string s) => WslTools.Shq(s);

        /// <summary>@scope/name → @scope\name（Windows node_modules 子路径）。</summary>
        private static string PkgToRelPath(string pkg)
        {
            return (pkg ?? "").Replace('/', Path.DirectorySeparatorChar);
        }
    }
}
