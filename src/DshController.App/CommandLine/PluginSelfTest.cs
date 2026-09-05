// ============================================================================
//  PluginSelfTest — 插件市场核心链路无头自检（--selftest-plugins）
//
//  覆盖：目录 JSON 解析（对象/数组/未知字段）→ 过滤排序 → 版本兼容判定 →
//  市场记录读写 → profile package.json 解析 → 安装命令拼装与目标校验。
//  全部离线：不联网、不起真 dsh、不触碰真实实例 HOME（记录用临时目录）。
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DshController.Core;

namespace DshController.CommandLine
{
    internal static class PluginSelfTest
    {
        private static int _pass;
        private static int _fail;

        private static void Check(bool cond, string name, string detail = "")
        {
            if (cond) { _pass++; Console.WriteLine("  PASS  " + name + (detail == "" ? "" : "  (" + detail + ")")); }
            else { _fail++; Console.WriteLine("  FAIL  " + name + (detail == "" ? "" : "  (" + detail + ")")); }
        }

        public static int Run(string[] args)
        {
            Console.WriteLine("== DshController 插件市场自检 ==");
            string oldOverride = PluginRecords.OverrideDir;
            string tmpRecords = Path.Combine(Path.GetTempPath(), "dsh-plugin-records-selftest");
            try { if (Directory.Exists(tmpRecords)) Directory.Delete(tmpRecords, true); } catch { /* 理由: 清理上次自检残留的记录目录，删除失败不影响本次自检 */ }

            // ---------- 1) 目录解析（真实来源 schema 自适应） ----------
            Console.WriteLine("[1] 目录 JSON 解析（四种真实形态）");
            {
                // ① 官方全量快照（awesome-dsh-plugin.com/plugins.json 实测结构）
                string officialJson = @"{
  ""name"": ""awesome-dsh-plugin"", ""updated"": ""2026-08-28"", ""count"": 2,
  ""categories"": { ""ui"": { ""en"": ""UI Enhancements"", ""zh"": ""UI 增强"" } },
  ""plugins"": [
    { ""name"": ""dsh-status-rotator"", ""owner"": ""01Virex"",
      ""url"": ""https://github.com/01Virex/dsh-status-rotator"",
      ""category"": ""ui"",
      ""description"": { ""en"": ""Rotating status phrases."", ""zh"": ""把回合状态替换成轮换文案。"" },
      ""npm"": ""dsh-status-rotator"", ""stars"": 55, ""downloads"": 2436,
      ""install"": ""dsh plugin --profile web add dsh-status-rotator"" },
    { ""name"": ""no-npm-plugin"", ""owner"": ""o"",
      ""url"": ""https://github.com/o/no-npm-plugin"", ""category"": ""tools"",
      ""description"": ""plain string desc"", ""stars"": 3 }
  ]
}";
                List<CatalogEntry> official = PluginCatalog.ParseSourceJson(
                    officialJson, MarketSourceKind.JsonCatalog, out string err1);
                Check(official != null && official.Count == 2, "官方快照解析", official == null ? err1 : official.Count + " 条");
                Check(official[0].Pkg == "dsh-status-rotator" && official[0].Repo == "01Virex/dsh-status-rotator",
                    "官方快照: npm 包名 + 从 url 提取 owner/repo");
                Check(official[0].Desc == "把回合状态替换成轮换文案。" && official[0].DescEn.Length > 0,
                    "官方快照: 中英双语简介");
                Check(official[0].Installable && official[0].Verified && official[0].DshBundle,
                    "官方快照: 有 npm 即可安装且已审核");
                Check(official[0].UpdatedAt == "2026-08-28", "官方快照: 顶层 updated 透传");
                Check(PluginCatalog.CategoryLabel("ui") == "UI 增强",
                    "分类动态映射: 官方 categories.zh 进动态表", PluginCatalog.CategoryLabel("ui"));
                Check(PluginCatalog.CategoryLabel("tools") == "工具", "分类内置映射: tools→工具");
                Check(PluginCatalog.CategoryLabel("totally-new-cat") == "totally-new-cat",
                    "分类未知代码原样显示不编造");

                // ② 精选快照（market.json 实测结构）
                string curatedJson = @"{
  ""schema_version"": 1, ""generated_at"": ""2026-08-29T02:00:43Z"",
  ""entries"": [
    { ""id"": 1, ""full_name"": ""yjh051108/dsh-routing-suite"",
      ""description"": ""injector + router kit"",
      ""stargazers_count"": 6929, ""pushed_at"": ""2026-08-28T19:03:44Z"",
      ""category"": ""developer-tools"", ""category_zh"": ""开发者工具"" }
  ]
}";
                List<CatalogEntry> curated = PluginCatalog.ParseSourceJson(
                    curatedJson, MarketSourceKind.JsonCatalog, out _);
                Check(curated != null && curated.Count == 1 && curated[0].Repo == "yjh051108/dsh-routing-suite",
                    "精选快照: full_name 入库");
                Check(curated[0].InstallTarget == "github:yjh051108/dsh-routing-suite" && curated[0].Installable,
                    "精选快照: github: 安装目标");
                Check(curated[0].CategoryLabel == "开发者工具", "精选快照: category_zh 中文分类");

                // ③ GitHub 实时搜索（items）
                string githubJson = @"{ ""total_count"": 1, ""items"": [
    { ""full_name"": ""someone/dsh-brand-new"", ""html_url"": ""https://github.com/someone/dsh-brand-new"",
      ""description"": ""new plugin"", ""stargazers_count"": 7,
      ""pushed_at"": ""2026-08-29T00:00:00Z"" } ] }";
                List<CatalogEntry> gh = PluginCatalog.ParseSourceJson(
                    githubJson, MarketSourceKind.GithubTopic, out _);
                Check(gh != null && gh.Count == 1 && gh[0].Unverified && !gh[0].Verified && gh[0].Installable,
                    "GitHub 搜索: 未审核标记 + 可安装（装前确认）");

                // ④ 旧接口规范（自定义源兼容）
                string specJson = @"{ ""plugins"": [
    { ""name"": ""示例"", ""pkg"": ""@scope/dsh-demo"", ""repo"": ""scope/dsh-demo"",
      ""desc"": ""演示"", ""category"": ""tool"", ""stars"": 120, ""verified"": true,
      ""dshBundle"": true, ""minHost"": "">=0.4.2"" } ] }";
                List<CatalogEntry> spec = PluginCatalog.ParseSourceJson(
                    specJson, MarketSourceKind.JsonCatalog, out _);
                Check(spec != null && spec.Count == 1 && spec[0].Pkg == "@scope/dsh-demo" &&
                    spec[0].MinHost == ">=0.4.2" && spec[0].Verified,
                    "旧接口规范（自定义源）解析");
                Check(PluginCatalog.ParseSourceJson("not json at all", MarketSourceKind.JsonCatalog, out _) == null,
                    "坏数据返回 null 不抛异常");

                // 合并去重：官方（npm 包名）与精选（仓库）收同一插件 → 合并为一条
                var srcA = new List<CatalogEntry>
                {
                    new CatalogEntry { Name = "demo", Pkg = "dsh-demo", Repo = "a/dsh-demo",
                        Stars = 100, Verified = true, Desc = "", SourceName = "官方全量" }
                };
                var srcB = new List<CatalogEntry>
                {
                    new CatalogEntry { Name = "a/dsh-demo", Repo = "a/dsh-demo",
                        Stars = 90, Verified = true, Desc = "中文简介", SourceName = "GitHub精选" }
                };
                List<CatalogEntry> merged = PluginCatalog.Merge(new[] { srcA, srcB });
                Check(merged.Count == 1 && merged[0].Sources.Count == 2 && merged[0].Variants.Count == 2,
                    "合并去重: 同一插件（pkg 与 repo 同指）只留一条", "来源=" + string.Join("+", merged[0].Sources));
                Check(merged[0].Pkg == "dsh-demo" && merged[0].Stars == 100,
                    "合并取最优变体（npm 包名优先保留，其次 star）");

                var srcC = new List<CatalogEntry> { new CatalogEntry { Name = "b/other", Repo = "b/other" } };
                Check(PluginCatalog.Merge(new[] { srcA, srcC }).Count == 2, "不同插件不合并且不丢条目");
            }

            // ---------- 2) 过滤与排序 ----------
            Console.WriteLine("[2] 过滤与排序");
            {
                var file = new CatalogFile
                {
                    Plugins = new List<CatalogEntry>
                    {
                        new CatalogEntry { Name = "Alpha", Pkg = "pkg-alpha", Category = "tool", Stars = 10, DshBundle = true },
                        new CatalogEntry { Name = "Beta", Pkg = "pkg-beta", Category = "ui", Stars = 99, DshBundle = true, Verified = true, Desc = "中文描述关键词" },
                        new CatalogEntry { Name = "Gamma", Pkg = "", Repo = "g/gamma", Category = "tool", Stars = 50, DshBundle = false }
                    }
                };
                var all = PluginCatalog.Filter(file, new PluginCatalog.FilterOptions());
                Check(all.Count == 3 && all[0].Name == "Beta", "默认按 star 降序");

                var kw = PluginCatalog.Filter(file, new PluginCatalog.FilterOptions { Keyword = "关键词" });
                Check(kw.Count == 1 && kw[0].Name == "Beta", "关键字命中中文描述");

                var cat = PluginCatalog.Filter(file, new PluginCatalog.FilterOptions { Category = "tool" });
                Check(cat.Count == 2, "分类过滤");

                var installable = PluginCatalog.Filter(file, new PluginCatalog.FilterOptions { OnlyInstallable = true });
                Check(installable.Count == 3 && installable.All(p => p.Installable),
                    "仅可安装过滤（v0.6.1: 有 npm 或仓库来源即可装）");

                var verified = PluginCatalog.Filter(file, new PluginCatalog.FilterOptions { OnlyVerified = true });
                Check(verified.Count == 1 && verified[0].Name == "Beta", "仅 verified 过滤");

                var cats = PluginCatalog.DistinctCategories(file);
                Check(cats.Count == 2 && cats[0].Key == "tool", "分类统计（下拉构建用）按数量降序");
            }

            // ---------- 2.5) GitHub 仓库 URL 提取与 WSL 安装探测解析 ----------
            Console.WriteLine("[2.5] RepoFromUrl 与安装探测解析");
            {
                Check(PluginCatalog.RepoFromUrl("https://github.com/o/r") == "o/r", "RepoFromUrl: 标准 URL");
                Check(PluginCatalog.RepoFromUrl("https://github.com/o/r.git/") == "o/r", "RepoFromUrl: .git 与尾斜杠");
                Check(PluginCatalog.RepoFromUrl("https://gitlab.com/o/r") == "", "RepoFromUrl: 非 GitHub 返回空");

                var info = InstanceDiscovery.ParseInstallProbeOutput("Ubuntu-26.04",
                    "DSHINST|dsh 版本 0.1.1-rc.2 (x64)|yes|/home/x/bin/dsh");
                Check(info != null && info.Distro == "Ubuntu-26.04" && info.DshVersion == "0.1.1-rc.2" && info.HomeInitialized,
                    "安装探测输出解析: 版本 + HOME 状态");
                Check(InstanceDiscovery.ParseInstallProbeOutput("D", "") == null, "安装探测: 未安装（无输出）返回 null");
            }

            // ---------- 3) 版本兼容判定 ----------
            Console.WriteLine("[3] 版本兼容判定");
            {
                Check(PluginCompat.CompareVersions("0.5.1", "0.4.2") > 0, "比较: 0.5.1 > 0.4.2");
                Check(PluginCompat.CompareVersions("1.0.0", "1.0.0") == 0, "比较: 相等");
                Check(PluginCompat.CompareVersions("1.2.3", "1.10.0") < 0, "比较: 逐段数值（1.2 < 1.10）");
                Check(PluginCompat.CompareVersions("1.0.0-rc.1", "1.0.0") < 0, "比较: 预发布 < 正式");
                Check(PluginCompat.FloorOfRange("^0.4.2") == "0.4.2", "范围下界: ^0.4.2");
                Check(PluginCompat.FloorOfRange(">=0.4.0 <0.5.0") == "0.4.0", "范围下界: >=0.4.0 <0.5.0");
                Check(PluginCompat.ExtractHostRangeFloor(
                    @"{""peerDependencies"":{""@deepseek-ai/dsh"":""^0.4.2""}}") == "0.4.2",
                    "包元数据: peerDependencies 下界");
                Check(PluginCompat.ExtractHostRangeFloor(
                    @"{""engines"":{""node"":""^22.0.0""}}") == "", "包元数据: 无关引擎不命中");

                var e = new CatalogEntry { MinHost = ">=0.4.2" };
                var ok = PluginCompat.Judge(e, "0.5.1");
                Check(ok.State == CompatState.Compatible && ok.Text.Contains("0.4.2") && ok.Text.Contains("仓库声明"),
                    "判定: 实例版本满足声明", ok.Text);
                var bad = PluginCompat.Judge(e, "0.3.9");
                Check(bad.State == CompatState.Incompatible && bad.Text.Contains("低于要求"), "判定: 实例版本低于声明", bad.Text);
                var unknown = PluginCompat.Judge(e, "");
                Check(unknown.State == CompatState.UnknownInstance, "判定: 实例版本未检测");
                var none = PluginCompat.Judge(new CatalogEntry { Name = "n" }, "0.5.1");
                Check(none.State == CompatState.NotDeclared && none.Text.Contains("未声明"),
                    "判定: 未声明版本原样展示不推测", none.Text);
            }

            // ---------- 4) 市场记录读写 ----------
            Console.WriteLine("[4] 市场安装记录");
            {
                PluginRecords.OverrideDir = tmpRecords;
                try
                {
                    var rec = new PluginRecord
                    {
                        Pkg = "@scope/dsh-demo",
                        Name = "示例插件",
                        Repo = "scope/dsh-demo",
                        Version = "1.2.0",
                        Profile = "web",
                        Target = "@scope/dsh-demo",
                        InstalledAt = new DateTime(2026, 8, 29, 3, 0, 0, DateTimeKind.Utc)
                    };
                    PluginRecords.Upsert("inst-a", rec);
                    PluginRecords.Upsert("inst-a", new PluginRecord { Pkg = "other", Profile = "web" });

                    var list = PluginRecords.Load("inst-a");
                    Check(list.Count == 2 && list[0].Pkg == "other", "Upsert 幂等（按 pkg 去重，新记录置顶）");

                    var recB = PluginRecords.Load("inst-b");
                    Check(recB.Count == 0, "实例隔离：记录按实例分文件");

                    var found = PluginRecords.Find(list, "@SCOPE/DSH-DEMO");
                    Check(found != null && found.Repo == "scope/dsh-demo" && found.InstalledAt.Year == 2026,
                        "按包名查找（不分大小写）+ 字段往返");

                    Check(PluginRecords.Remove("inst-a", "other"), "Remove 命中");
                    Check(!PluginRecords.Remove("inst-a", "other"), "Remove 幂等");
                    Check(PluginRecords.Load("inst-a").Count == 1, "Remove 后剩余正确");

                    // 损坏文件回退空表
                    File.WriteAllText(Path.Combine(tmpRecords, "corrupt.json"), "{ not json");
                    Check(PluginRecords.Load("corrupt").Count == 0, "损坏记录文件回退空表");
                }
                finally
                {
                    PluginRecords.OverrideDir = oldOverride;
                    try { Directory.Delete(tmpRecords, true); } catch { /* 理由: 清理临时记录目录，删除失败不影响自检结果 */ }
                }
            }

            // ---------- 5) profile package.json 解析 ----------
            Console.WriteLine("[5] 已装插件解析（profile package.json）");
            {
                string json = @"{
  ""name"": ""profile-web"",
  ""dependencies"": {
    ""@deepseek-ai/dsh-base"": ""^0.5.0"",
    ""@scope/dsh-demo"": ""link:D:\\instances\\a\\packages\\demo"",
    ""@man/dsh-manual"": ""^1.0.0""
  },
  ""dsh"": { ""profile"": { ""bundles"": [""base"", ""web-app"", ""@scope/dsh-demo""] } }
}";
                var list = InstalledPlugins.ParseProfilePackage(json);
                Check(list.Count == 3, "依赖全量解析", list.Count + " 条");
                Check(list[0].Pkg == "@deepseek-ai/dsh-base" && list[0].IsOfficial, "官方基础包排前 + IsOfficial");
                var demo = list.First(p => p.Pkg == "@scope/dsh-demo");
                Check(demo.InBundles && demo.LocalLink && demo.DepRef.StartsWith("link:"), "bundle 归属 + 本地链接识别");
                Check(list.First(p => p.Pkg == "@man/dsh-manual").InBundles == false, "未在 bundles 的包 InBundles=false");
                Check(InstalledPlugins.ParseProfilePackage("broken").Count == 0, "坏文件回退空表");
            }

            // ---------- 6) 命令拼装与目标校验 ----------
            Console.WriteLine("[6] 安装命令拼装与目标校验");
            {
                var (f1, a1) = PluginInstaller.BuildWindowsCommand(
                    new DshCommand { Kind = "cmd", Path1 = @"C:\npm\dsh.cmd" },
                    PluginOp.Add, "web", "@scope/dsh-demo");
                Check(f1 == "cmd.exe" && a1 == @"/d /s /c """"C:\npm\dsh.cmd"" plugin --profile web add @scope/dsh-demo""",
                    "cmd 档命令拼装", a1);

                var (f2, a2) = PluginInstaller.BuildWindowsCommand(
                    new DshCommand { Kind = "node", Path1 = @"C:\node\node.exe", Path2 = @"C:\npm\dsh\lib\bin.js" },
                    PluginOp.Remove, "web", "@scope/dsh-demo");
                Check(f2 == @"C:\node\node.exe" &&
                    a2 == @"""C:\npm\dsh\lib\bin.js"" plugin --profile web remove @scope/dsh-demo",
                    "node 档命令拼装", a2);

                var (f3, a3) = PluginInstaller.BuildWindowsCommand(
                    new DshCommand { Kind = "npx", Path1 = @"C:\npm\npx.cmd", Path2 = "0.5.1" },
                    PluginOp.Update, "web", "@scope/dsh-demo");
                Check(f3 == "cmd.exe" &&
                    a3 == @"/d /s /c """"C:\npm\npx.cmd"" --yes @deepseek-ai/dsh@0.5.1 plugin --profile web update @scope/dsh-demo""",
                    "npx 档（锁定版本）命令拼装", a3);

                Check(PluginInstaller.ValidateTarget("@scope/dsh-demo") == null, "校验: npm 包名通过");
                Check(PluginInstaller.ValidateTarget("github:scope/dsh-demo") == null, "校验: github 来源通过");
                Check(PluginInstaller.ValidateTarget(@"C:\Users\a b\plugin") != null,
                    "校验: 含空格本地路径被拒（pnpm/cmd 拆断坑）");
                Check(PluginInstaller.ValidateTarget("pkg && calc") != null, "校验: cmd 元字符被拒");
                Check(PluginInstaller.ValidateTarget("") != null, "校验: 空目标被拒");
                Check(PluginInstaller.ValidateTarget("github:bad-format") != null, "校验: github 格式不完整被拒");
            }

            Console.WriteLine("== 插件市场自检结束：PASS " + _pass + " / FAIL " + _fail + " ==");
            return _fail == 0 ? 0 : 1;
        }
    }
}
