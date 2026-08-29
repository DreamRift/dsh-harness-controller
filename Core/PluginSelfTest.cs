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

namespace DshController.Core
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
            try { if (Directory.Exists(tmpRecords)) Directory.Delete(tmpRecords, true); } catch { }

            // ---------- 1) 目录解析 ----------
            Console.WriteLine("[1] 目录 JSON 解析");
            {
                string json = @"{
  ""version"": 1,
  ""updatedAt"": ""2026-08-29T03:00:00Z"",
  ""source"": ""awesome-dsh-plugin"",
  ""plugins"": [
    { ""name"": ""示例插件"", ""pkg"": ""@scope/dsh-demo"", ""repo"": ""scope/dsh-demo"",
      ""desc"": ""演示用插件"", ""category"": ""tool"", ""stars"": 120,
      ""verified"": true, ""dshBundle"": true, ""minHost"": "">=0.4.2"",
      ""tags"": [""demo""], ""brandNewField"": {""x"": 1} },
    { ""name"": ""浏览专用"", ""pkg"": """", ""repo"": ""scope/browse-only"",
      ""desc"": ""没有 bundle"", ""category"": ""ui"", ""stars"": 50, ""dshBundle"": false },
    { ""name"": ""坏数据无名称"", ""stars"": 1 }
  ]
}";
                CatalogFile file = PluginCatalog.Parse(json);
                Check(file != null && file.Plugins.Count == 3, "根对象形态解析", file == null ? "null" : file.Plugins.Count + " 条");
                Check(file.Plugins[0].Extra != null && file.Plugins[0].Extra.ContainsKey("brandNewField"),
                    "未知字段经 JsonExtensionData 保留");
                Check(file.Plugins[0].InstallTarget == "@scope/dsh-demo" && file.Plugins[0].Installable,
                    "npm 包名优先的安装目标");
                Check(!file.Plugins[1].Installable && file.Plugins[1].InstallTarget == "github:scope/browse-only",
                    "pkg 空时退回 github: 来源，dshBundle=false 不可装");

                CatalogFile arr = PluginCatalog.Parse(
                    @"[{""name"":""条目A"",""pkg"":""a""},{""name"":""条目B"",""pkg"":""b""}]");
                Check(arr != null && arr.Plugins.Count == 2, "根数组形态解析");
                Check(PluginCatalog.Parse("not json at all") == null, "坏数据返回 null 不抛异常");
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
                Check(installable.Count == 2 && installable.All(p => p.Installable), "仅可安装过滤");

                var verified = PluginCatalog.Filter(file, new PluginCatalog.FilterOptions { OnlyVerified = true });
                Check(verified.Count == 1 && verified[0].Name == "Beta", "仅 verified 过滤");
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
                    try { Directory.Delete(tmpRecords, true); } catch { }
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
