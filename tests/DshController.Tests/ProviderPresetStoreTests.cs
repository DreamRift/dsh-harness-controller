// ============================================================================
//  ProviderPresetStoreTests — 供应商预设台账离线单测（改版·API 预设页 / 预设存储）
//  覆盖：增删改查、校验规则、落盘路径、损坏/空文件边界。模式=正确性（C）。
// ============================================================================

using System;
using System.IO;
using System.Linq;
using DshController.Core;
using DshController.Core.Storage;
using Xunit;

namespace DshController.Tests
{
    public class ProviderPresetStoreTests : IDisposable
    {
        private readonly string _file = Path.Combine(Path.GetTempPath(), "dsh-preset-test-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".json");

        public void Dispose()
        {
            try { File.Delete(_file); }
            catch (Exception) { /* 理由: 测试临时文件清理失败可忽略 */ }
        }

        private static ProviderPreset ValidPreset()
        {
            return new ProviderPreset
            {
                Name = "DeepSeek 官方",
                Kind = "deepseek",
                BaseUrl = "https://api.deepseek.com",
                ApiKey = "sk-test",
                DefaultModel = "deepseek-chat"
            };
        }

        [Fact]
        public void 新增_校验通过落盘可读回()
        {
            var s1 = new ProviderPresetStore(_file);
            Assert.True(s1.TryAdd(ValidPreset(), out string id, out string err));
            Assert.False(string.IsNullOrEmpty(id));
            var s2 = new ProviderPresetStore(_file);
            ProviderPreset p = s2.Get(id);
            Assert.NotNull(p);
            Assert.Equal("DeepSeek 官方", p.Name);
            Assert.Equal("deepseek", p.Kind);
            Assert.Equal("https://api.deepseek.com", p.BaseUrl);
        }

        [Fact]
        public void 校验_名称空与过长()
        {
            var s = new ProviderPresetStore(_file);
            Assert.False(s.TryAdd(ValidPreset() is var ok ? null : ok, out _, out string e1));
            Assert.Equal("预设不能为空", e1);
            var noName = ValidPreset(); noName.Name = "  ";
            Assert.False(s.TryAdd(noName, out _, out string e2));
            Assert.Equal("预设名不能为空", e2);
            var longName = ValidPreset(); longName.Name = new string('名', 41);
            Assert.False(s.TryAdd(longName, out _, out string e3));
            Assert.Equal("预设名过长（最多 40 字）", e3);
            Assert.Single(s.All());   // 仅内置官方默认
        }

        [Fact]
        public void 校验_类别必填_BaseUrl须http()
        {
            var s = new ProviderPresetStore(_file);
            var noKind = ValidPreset(); noKind.Kind = "";
            Assert.False(s.TryAdd(noKind, out _, out string e1));
            Assert.Equal("供应商类别不能为空", e1);
            var badUrl = ValidPreset(); badUrl.BaseUrl = "ftp://x";
            Assert.False(s.TryAdd(badUrl, out _, out string e2));
            Assert.Equal("BaseUrl 须以 http:// 或 https:// 开头", e2);
            var relUrl = ValidPreset(); relUrl.BaseUrl = "api.deepseek.com";
            Assert.False(s.TryAdd(relUrl, out _, out string e3));
            Assert.Single(s.All());   // 仅内置官方默认
        }

        [Fact]
        public void 更新_字段生效与时间戳刷新()
        {
            var s = new ProviderPresetStore(_file);
            Assert.True(s.TryAdd(ValidPreset(), out string id, out _));
            var before = s.Get(id).UpdatedAtUtc;
            var patch = ValidPreset();
            patch.Name = "改名后";
            patch.DefaultModel = "deepseek-reasoner";
            Assert.True(s.TryUpdate(id, patch, out string err), "err=" + err);
            ProviderPreset after = s.Get(id);
            Assert.Equal("改名后", after.Name);
            Assert.Equal("deepseek-reasoner", after.DefaultModel);
            Assert.Equal("deepseek", after.Kind);
            Assert.True(after.UpdatedAtUtc >= before);
        }

        [Fact]
        public void 更新_不存在id与校验失败不改动()
        {
            var s = new ProviderPresetStore(_file);
            Assert.False(s.TryUpdate("nope", ValidPreset(), out string e1));
            Assert.Equal("预设不存在", e1);
            Assert.True(s.TryAdd(ValidPreset(), out string id, out _));
            var badPatch = ValidPreset(); badPatch.Name = "";
            Assert.False(s.TryUpdate(id, badPatch, out string e2));
            Assert.Equal("预设名不能为空", e2);
            Assert.Equal("DeepSeek 官方", s.Get(id).Name);   // 原值未动
        }

        [Fact]
        public void 删除_生效与不存在返回false()
        {
            var s = new ProviderPresetStore(_file);
            Assert.True(s.TryAdd(ValidPreset(), out string id, out _));
            Assert.True(s.Delete(id));
            Assert.Null(s.Get(id));
            Assert.False(s.Delete(id));
            var s2 = new ProviderPresetStore(_file);
            Assert.Single(s2.All());   // 仅内置官方默认
        }

        [Fact]
        public void Get_大小写不敏感()
        {
            var s = new ProviderPresetStore(_file);
            Assert.True(s.TryAdd(ValidPreset(), out string id, out _));
            Assert.NotNull(s.Get(id.ToUpperInvariant()));
            Assert.Null(s.Get("nope"));
        }

        [Fact]
        public void 损坏文件_兜底出内置官方()
        {
            File.WriteAllText(_file, "{not-json");
            var s = new ProviderPresetStore(_file);
            Assert.Single(s.All());
            Assert.True(s.All()[0].IsBuiltin);
        }

        [Fact]
        public void 缺文件_兜底出内置官方()
        {
            var s = new ProviderPresetStore(_file);
            Assert.Single(s.All());
            Assert.True(s.All()[0].IsBuiltin);
        }

        [Fact]
        public void 落盘路径经AppPaths可复核()
        {
            Assert.Equal("api-presets.json", Path.GetFileName(AppPaths.ProviderPresetsFile));
            Assert.Equal(AppPaths.StateDir, Path.GetDirectoryName(AppPaths.ProviderPresetsFile));
        }

        // ---- dsh 模型页适配轮：路由身份 + 模型目录 + 旧档案兼容 ------------------

        [Fact]
        public void 旧档案_载入播种Models并回填路由()
        {
            // 旧版只写 DefaultModel，无 ProviderId/Models 字段
            const string legacy = "[{\"Id\":\"old1\",\"Name\":\"DeepSeek 官方\",\"Kind\":\"deepseek\"," +
                "\"BaseUrl\":\"https://api.deepseek.com\",\"ApiKey\":\"sk-x\",\"DefaultModel\":\"deepseek-chat\"," +
                "\"Enabled\":true,\"UpdatedAtUtc\":\"2026-09-01T00:00:00Z\"}]";
            File.WriteAllText(_file, legacy);
            var s = new ProviderPresetStore(_file);
            ProviderPreset p = s.All().First(x => x.Id == "old1");
            Assert.Equal("deepseek-chat", p.Models.Single().Id);          // DefaultModel → Models[0]
            Assert.Equal("DeepSeek 官方", p.Models.Single().Name);
            Assert.Equal("deepseek-deepseek", p.ProviderId);              // 回填 kind+name slug（同步键不变）
            Assert.Equal("deepseek-chat", p.DefaultModel);                // 兼容字段保持同相
        }

        [Fact]
        public void 新档案_模型目录与容量往返()
        {
            var s1 = new ProviderPresetStore(_file);
            var p = ValidPreset();
            p.ProviderId = "acme";
            p.Models = new System.Collections.Generic.List<PresetModel>
            {
                new PresetModel { Id = "acme-chat", Name = "Chat", ContextWindow = 128000, MaxTokens = 8192 },
                new PresetModel { Id = "acme-mini" }
            };
            Assert.True(s1.TryAdd(p, out string id, out string err), "err=" + err);
            ProviderPreset back = new ProviderPresetStore(_file).Get(id);
            Assert.Equal("acme", back.ProviderId);
            Assert.Equal(2, back.Models.Count);
            Assert.Equal(128000, back.Models[0].ContextWindow);
            Assert.Equal(8192, back.Models[0].MaxTokens);
            Assert.Null(back.Models[1].ContextWindow);
            Assert.Equal("acme-chat", back.DefaultModel);                 // 兼容字段 = Models[0].Id
        }

        [Fact]
        public void 路由_占用拒绝_更新排除自身_非法拒()
        {
            var s = new ProviderPresetStore(_file);
            var a = ValidPreset(); a.ProviderId = "acme";
            var b = ValidPreset(); b.Name = "其他"; b.ProviderId = "acme";   // 不同名同路由
            Assert.True(s.TryAdd(a, out string idA, out _));
            Assert.False(s.TryAdd(b, out _, out string e1));
            Assert.Equal(PresetCopy.RouteTaken, e1);
            // 更新自身（路由不变，改名）不触发占用
            var patch = ValidPreset(); patch.ProviderId = "acme"; patch.Name = "改名";
            Assert.True(s.TryUpdate(idA, patch, out string e2), "err=" + e2);
            // 非法路由在新增时拒
            var bad = ValidPreset(); bad.ProviderId = "Bad Route";
            Assert.False(s.TryAdd(bad, out _, out string e3));
            Assert.Equal(PresetCopy.RouteInvalid, e3);
        }

        // ---- 内置 DeepSeek 官方提供方（默认常驻） -----------------------------

        [Fact]
        public void 首载播种DeepSeek官方默认()
        {
            var s = new ProviderPresetStore(_file);
            ProviderPreset b = s.All().Single();
            Assert.True(b.IsBuiltin);
            Assert.Equal(ProviderPreset.BuiltinRoute, b.ProviderId);
            Assert.Equal("DeepSeek 官方", b.Name);
            Assert.Equal("https://api.deepseek.com", b.BaseUrl);
            Assert.Equal("openai-completions", b.Kind);
            // 官方 v4 阵容（对齐 dsh-llm-deepseek DEFAULT_MODELS + pi-ai 目录数据）
            Assert.Equal(new[] { "deepseek-v4-flash", "deepseek-v4-pro", "deepseek-v4-flash-vision-exp" },
                b.Models.Select(m => m.Id).ToArray());
            Assert.Equal(new[] { "DeepSeek-V4-Flash", "DeepSeek-V4-Pro", "DeepSeek-V4-Flash-Vision-Exp" },
                b.Models.Select(m => m.Name).ToArray());
            Assert.All(b.Models, m =>
            {
                Assert.Equal(1000000, m.ContextWindow);
                Assert.Equal(384000, m.MaxTokens);
            });
            Assert.Equal("", b.ApiKey);
            Assert.Equal("deepseek-v4-flash", b.DefaultModel);   // 兼容字段同相
        }

        [Fact]
        public void 再载入不重复_用户改动保留()
        {
            var s1 = new ProviderPresetStore(_file);
            ProviderPreset b = s1.All().Single(p => p.IsBuiltin);
            b.ApiKey = "sk-user";
            Assert.True(s1.TryUpdate(b.Id, b, out string err), "err=" + err);
            var s2 = new ProviderPresetStore(_file);
            Assert.Single(s2.All());
            Assert.Equal("sk-user", s2.All()[0].ApiKey);
        }

        [Fact]
        public void 内置不可删除_官方路由被占用()
        {
            var s = new ProviderPresetStore(_file);
            ProviderPreset b = s.All().Single(p => p.IsBuiltin);
            Assert.False(s.Delete(b.Id));
            Assert.Single(s.All());
            var shadow = ValidPreset();
            shadow.ProviderId = ProviderPreset.BuiltinRoute;
            Assert.False(s.TryAdd(shadow, out _, out string err));
            Assert.Equal(PresetCopy.RouteTaken, err);
        }

        // ---- llm-pi-ai 迁移轮：多模态字段往返 + 内置 vision + 凭据注入对 ----------

        [Fact]
        public void 多模态字段_落盘往返与旧档缺省()
        {
            var s1 = new ProviderPresetStore(_file);
            var p = ValidPreset();
            p.ProviderId = "acme";
            p.Models = new System.Collections.Generic.List<PresetModel>
            {
                new PresetModel { Id = "vlm", Multimodal = true },
                new PresetModel { Id = "txt", Multimodal = false },
                new PresetModel { Id = "unk" }
            };
            Assert.True(s1.TryAdd(p, out string id, out string err), "err=" + err);
            ProviderPreset back = new ProviderPresetStore(_file).Get(id);
            Assert.True(back.Models[0].Multimodal);
            Assert.False(back.Models[1].Multimodal);
            Assert.Null(back.Models[2].Multimodal);   // 旧档案缺字段 = 未知
        }

        [Fact]
        public void 内置vision_exp出厂即多模态()
        {
            var s = new ProviderPresetStore(_file);
            ProviderPreset b = s.All().Single(p => p.IsBuiltin);
            PresetModel vision = b.Models.Single(m => m.Id == "deepseek-v4-flash-vision-exp");
            Assert.True(vision.Multimodal);
            Assert.All(b.Models.Where(m => m.Id != vision.Id), m => Assert.Null(m.Multimodal));
        }

        [Fact]
        public void 凭据环境对_仅启用且填密的预设()
        {
            var s = new ProviderPresetStore(_file);
            var a = ValidPreset(); a.ProviderId = "acme"; a.ApiKey = "sk-a";
            var b = ValidPreset(); b.Name = "其他"; b.ProviderId = "beta"; b.ApiKey = "";
            Assert.True(s.TryAdd(a, out _, out _));
            Assert.True(s.TryAdd(b, out _, out _));   // 无密钥 → 不注入
            var pairs = s.CredentialEnvPairs();
            Assert.Equal(new[] { "DSH_PRESET_ACME" }, pairs.Select(p => p.Name).ToArray());
            Assert.Equal("sk-a", pairs.Single().Value);
        }
    }
}
