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
    }
}
