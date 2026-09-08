// ============================================================================
//  ProviderConfigMapperTests — 格式对照/归一的离线断言（改版·供应商同步 / 配置格式对齐）
//  对照表见 docs/provider-sync-alignment.md；本类断言其中关键规则。
// ============================================================================

using System.Linq;
using DshController.Core;
using Xunit;

namespace DshController.Tests
{
    public class ProviderConfigMapperTests
    {
        private static ProviderPreset Preset()
        {
            return new ProviderPreset
            {
                Name = "DeepSeek 官方",
                Kind = "deepseek",
                BaseUrl = "https://api.deepseek.com/",
                ApiKey = "sk-x",
                DefaultModel = "deepseek-chat"
            };
        }

        [Fact]
        public void BaseUrl归一_去尾斜杠与scheme小写()
        {
            Assert.Equal("https://api.deepseek.com", ProviderConfigMapper.NormalizeBaseUrl("HTTPS://Api.DeepSeek.COM/"));
            Assert.Equal("", ProviderConfigMapper.NormalizeBaseUrl("  "));
        }

        [Fact]
        public void 键归一_宽松slug化_非ASCII不参与()
        {
            Assert.Equal("deepseek-deepseek", ProviderConfigMapper.ProviderKey("DeepSeek", "DeepSeek 官方"));
            Assert.Equal("preset", ProviderConfigMapper.ProviderKey("", ""));
            Assert.Equal("preset", ProviderConfigMapper.ProviderKey("中文类别", "中文名"));
        }

        [Fact]
        public void ApiAdapter_响应式与回落()
        {
            Assert.Equal("openai-responses", ProviderConfigMapper.ApiAdapter("openai-responses"));
            Assert.Equal("openai-responses", ProviderConfigMapper.ApiAdapter("Something-Responses"));
            Assert.Equal("openai-completions", ProviderConfigMapper.ApiAdapter("deepseek"));
            Assert.Equal("openai-completions", ProviderConfigMapper.ApiAdapter("openai-compatible"));
            Assert.Equal("openai-completions", ProviderConfigMapper.ApiAdapter(""));
        }

        [Fact]
        public void 密钥环境变量名_大写下划线()
        {
            Assert.Equal("DSH_PRESET_DEEPSEEK_DEEPSEEK", ProviderConfigMapper.ApiKeyEnvName("deepseek-deepseek"));
        }

        [Fact]
        public void ToEntry_完整映射()
        {
            MappingResult r = ProviderConfigMapper.ToEntry(Preset());
            Assert.Equal("deepseek-deepseek", r.Entry.Key);
            Assert.Equal("DeepSeek 官方", r.Entry.DisplayName);
            Assert.Equal("openai-completions", r.Entry.Api);
            Assert.Equal("https://api.deepseek.com", r.Entry.BaseUrl);
            Assert.Equal("DSH_PRESET_DEEPSEEK_DEEPSEEK", r.Entry.ApiKeyEnv);
            ProviderModelConfig m = r.Entry.Models.Single();
            Assert.Equal("deepseek-chat", m.Id);
            Assert.Equal("DeepSeek 官方", m.Name);
            Assert.Contains(r.Notes, n => n.Contains("apiKeyEnv"));
        }

        [Fact]
        public void ToEntry_无模型与无URL的降级note()
        {
            var p = Preset();
            p.DefaultModel = "";
            p.BaseUrl = "";
            MappingResult r = ProviderConfigMapper.ToEntry(p);
            Assert.Empty(r.Entry.Models);
            Assert.Contains(r.Notes, n => n.Contains("models"));
            Assert.Contains(r.Notes, n => n.Contains("默认端点"));
        }

    [Fact]
    public void ApplyTo_覆盖映射字段_宽松段保留()
    {
        // 既有实例条目：带 contextWindow/maxTokens/compat 等宽松段（模拟真实 settings.yaml）
        var target = new InstanceProviderEntry
        {
            Key = "deepseek-deepseek",
            DisplayName = "旧名",
            Api = "openai-completions",
            BaseUrl = "https://old.example.com",
            ApiKeyEnv = "OLD_ENV"
        };
        target.Models.Add(new ProviderModelConfig { Id = "old-model" });
        target.Models[0].Extra["contextWindow"] = 1000000L;
        target.Extra["comment"] = "keep me";

        ProviderConfigMapper.ApplyTo(target, ProviderConfigMapper.ToEntry(Preset()));

        Assert.Equal("DeepSeek 官方", target.DisplayName);
        Assert.Equal("https://api.deepseek.com", target.BaseUrl);
        Assert.Equal("DSH_PRESET_DEEPSEEK_DEEPSEEK", target.ApiKeyEnv);
        // 宽松段保留
        Assert.Equal(1000000L, target.Models.First(m => m.Id == "old-model").Extra["contextWindow"]);
        Assert.Equal("keep me", target.Extra["comment"]);
        // 新模型追加
        Assert.Contains(target.Models, m => m.Id == "deepseek-chat");
    }

    // ---- dsh 模型页适配轮：模型目录映射 / 协议透传 / 容量渲染 -----------------

    [Fact]
    public void ToEntry_模型目录按行映射_稳定路由为键_容量入宽松段()
    {
        var p = Preset();
        p.ProviderId = "acme";
        p.Models = new System.Collections.Generic.List<PresetModel>
        {
            new PresetModel { Id = "acme-chat", Name = "Chat", ContextWindow = 128000, MaxTokens = 8192 },
            new PresetModel { Id = "acme-mini" }
        };
        MappingResult r = ProviderConfigMapper.ToEntry(p);
        Assert.Equal("acme", r.Entry.Key);                       // 稳定路由优先于 kind+name slug
        Assert.Equal("DSH_PRESET_ACME", r.Entry.ApiKeyEnv);
        Assert.Equal(2, r.Entry.Models.Count);
        Assert.Equal(128000L, r.Entry.Models[0].Extra["contextWindow"]);
        Assert.Equal(8192L, r.Entry.Models[0].Extra["maxTokens"]);
        Assert.False(r.Entry.Models[1].Extra.ContainsKey("contextWindow"));
    }

    [Fact]
    public void ToEntry_无ProviderId回落slug键()
    {
        MappingResult r = ProviderConfigMapper.ToEntry(Preset());   // 未设 ProviderId
        Assert.Equal("deepseek-deepseek", r.Entry.Key);
    }

    [Fact]
    public void ApiAdapter_已知协议精确透传()
    {
        Assert.Equal("openai-codex-responses", ProviderConfigMapper.ApiAdapter("openai-codex-responses"));
        Assert.Equal("anthropic-messages", ProviderConfigMapper.ApiAdapter("anthropic-messages"));
        Assert.Equal("azure-openai-responses", ProviderConfigMapper.ApiAdapter("azure-openai-responses"));
    }

    [Fact]
    public void 规范渲染_模型容量数值行()
    {
        var p = Preset();
        p.ProviderId = "acme";
        p.Models = new System.Collections.Generic.List<PresetModel>
        {
            new PresetModel { Id = "acme-chat", Name = "Chat", ContextWindow = 128000, MaxTokens = 8192 }
        };
        string text = ProviderSyncPlan.RenderYamlBlock(ProviderConfigMapper.ToEntry(p).Entry);
        Assert.Contains("acme:", text);
        Assert.Contains("- id: acme-chat", text);
        Assert.Contains("name: Chat", text);
        Assert.Contains("contextWindow: 128000", text);          // 数字不加引号
        Assert.Contains("maxTokens: 8192", text);
    }

    [Fact]
    public void 规范渲染_无容量模型不产生容量行()
    {
        var entry = new InstanceProviderEntry { Key = "k", Api = "openai-completions" };
        entry.Models.Add(new ProviderModelConfig { Id = "m" });
        string text = ProviderSyncPlan.RenderYamlBlock(entry);
        Assert.DoesNotContain("contextWindow", text);
        Assert.DoesNotContain("maxTokens", text);
    }

    // ---- llm-pi-ai 迁移轮：多模态三态映射 / 思考档官方豁免 ----------------------

    [Fact]
    public void ToEntry_多模态三态映射到input字段()
    {
        var p = Preset();
        p.ProviderId = "acme";
        p.Models = new System.Collections.Generic.List<PresetModel>
        {
            new PresetModel { Id = "vlm", SupportImage = true },
            new PresetModel { Id = "txt", SupportImage = false },
            new PresetModel { Id = "unk" }
        };
        var models = ProviderConfigMapper.ToEntry(p).Entry.Models;
        Assert.Equal(new[] { "text", "image" }, models[0].InputModalities);
        Assert.Equal(new[] { "text" }, models[1].InputModalities);
        Assert.Null(models[2].InputModalities);
    }

    [Fact]
    public void ToEntry_非官方补思考档_官方豁免()
    {
        var custom = Preset();
        custom.ProviderId = "acme";
        custom.Models = new System.Collections.Generic.List<PresetModel> { new PresetModel { Id = "m" } };
        var cm = ProviderConfigMapper.ToEntry(custom).Entry.Models.Single();
        Assert.True(cm.WriteReasoningEfforts);       // 非官方一律自动补四档

        var official = ProviderPreset.CreateBuiltinDefault();
        var om = ProviderConfigMapper.ToEntry(official).Entry.Models;
        Assert.All(om, m => Assert.False(m.WriteReasoningEfforts));   // 官方由实例原生适配器供档位
        Assert.Equal("deepseek-official", ProviderConfigMapper.ToEntry(official).Entry.Key);
        Assert.True(om.First(m => m.Id == "deepseek-v4-flash-vision-exp").InputModalities != null);
    }
}
}
