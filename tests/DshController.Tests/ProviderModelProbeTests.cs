// ============================================================================
//  ProviderModelProbeTests — 模型目录探针解析离线断言（API 页适配轮）
//  钉死：端点拼接、OpenAI data 形/裸数组、容量别名提取（含 OpenRouter 嵌套）。
// ============================================================================

using System.Linq;
using DshController.Core;
using Xunit;

namespace DshController.Tests
{
    public class ProviderModelProbeTests
    {
        [Fact]
        public void 端点拼接_去尾斜杠加models()
        {
            Assert.Equal("https://api.x.com/models", ProviderModelProbe.ModelsUrl("https://api.x.com"));
            Assert.Equal("https://api.x.com/v1/models", ProviderModelProbe.ModelsUrl("https://api.x.com/v1/"));
            Assert.Equal("https://api.x.com/v1/models", ProviderModelProbe.ModelsUrl(" https://api.x.com/v1 "));
            Assert.Equal("", ProviderModelProbe.ModelsUrl("   "));
        }

        [Fact]
        public void 解析_OpenAIdata数组()
        {
            string json = "{\"object\":\"list\",\"data\":[" +
                "{\"id\":\"deepseek-chat\",\"object\":\"model\"}," +
                "{\"id\":\"deepseek-reasoner\",\"name\":\"Reasoner\"}]}";
            var models = ProviderModelProbe.Parse(json);
            Assert.Equal(2, models.Count);
            Assert.Equal("deepseek-chat", models[0].Id);
            Assert.Equal("", models[0].Name);
            Assert.Null(models[0].ContextWindow);
            Assert.Equal("deepseek-reasoner", models[1].Id);
            Assert.Equal("Reasoner", models[1].Name);
        }

        [Fact]
        public void 解析_裸数组与容量别名()
        {
            string json = "[{\"id\":\"m1\",\"context_length\":64000,\"max_output_tokens\":8192}," +
                "{\"id\":\"m2\",\"contextWindow\":\"128000\",\"display_name\":\"M2\"}]";
            var models = ProviderModelProbe.Parse(json);
            Assert.Equal(2, models.Count);
            Assert.Equal(64000, models[0].ContextWindow);
            Assert.Equal(8192, models[0].MaxTokens);
            Assert.Equal(128000, models[1].ContextWindow);
            Assert.Equal("M2", models[1].Name);
        }

        [Fact]
        public void 解析_OpenRouter嵌套top_provider()
        {
            string json = "{\"data\":[{\"id\":\"or/m\",\"context_length\":1000000," +
                "\"top_provider\":{\"completion_max_tokens\":64000}}]}";
            var models = ProviderModelProbe.Parse(json);
            Assert.Single(models);
            Assert.Equal(1000000, models[0].ContextWindow);
            Assert.Equal(64000, models[0].MaxTokens);
        }

        [Fact]
        public void 解析_跳过无id条目与非法json()
        {
            Assert.Empty(ProviderModelProbe.Parse("not-json"));
            Assert.Empty(ProviderModelProbe.Parse("{\"data\":[{\"name\":\"no-id\"},\"str\",123]}"));
            var only = ProviderModelProbe.Parse("{\"data\":[{\"id\":\"ok\"}]}");
            Assert.Single(only);
            Assert.Equal("ok", only.Single().Id);
        }

        [Fact]
        public void 解析_非正数容量视为未提供()
        {
            var models = ProviderModelProbe.Parse("[{\"id\":\"m\",\"context_length\":0,\"max_output_tokens\":-8192}]");
            Assert.Single(models);
            Assert.Null(models[0].ContextWindow);
            Assert.Null(models[0].MaxTokens);
        }

        // ---- llm-pi-ai 迁移轮：多模态信息三态解析 ---------------------------------

        [Fact]
        public void 多模态_输入模态数组含image为true仅text为false()
        {
            string json = "[{\"id\":\"a\",\"input_modalities\":[\"text\",\"image\"]}," +
                "{\"id\":\"b\",\"inputModalities\":[\"text\"]}]";
            var models = ProviderModelProbe.Parse(json);
            Assert.Equal(2, models.Count);
            Assert.True(models[0].SupportImage);
            Assert.False(models[1].SupportImage);
        }

        [Fact]
        public void 多模态_OpenRouter嵌套architecture()
        {
            string json = "{\"data\":[{\"id\":\"or/vlm\"," +
                "\"architecture\":{\"input_modalities\":[\"text\",\"image\"]}}]}";
            var models = ProviderModelProbe.Parse(json);
            Assert.Single(models);
            Assert.True(models[0].SupportImage);
        }

        [Fact]
        public void 多模态_input别名数组与逗号串()
        {
            string json = "[{\"id\":\"a\",\"input\":[\"text\",\"image\"]}," +
                "{\"id\":\"b\",\"modalities\":\"text,image\"}]";
            var models = ProviderModelProbe.Parse(json);
            Assert.True(models[0].SupportImage);
            Assert.True(models[1].SupportImage);
        }

        [Fact]
        public void 多模态_布尔别名支持视觉()
        {
            string json = "[{\"id\":\"a\",\"supports_vision\":true}," +
                "{\"id\":\"b\",\"supportsImage\":false}]";
            var models = ProviderModelProbe.Parse(json);
            Assert.True(models[0].SupportImage);
            Assert.False(models[1].SupportImage);
        }

        [Fact]
        public void 多模态_无信息与空列表为未知()
        {
            string json = "[{\"id\":\"a\"},{\"id\":\"b\",\"input\":[]},{\"id\":\"c\",\"modalities\":[]}," +
                "{\"id\":\"d\",\"input\":[\"audio\"]}]";
            var models = ProviderModelProbe.Parse(json);
            Assert.Null(models[0].SupportImage);
            Assert.Null(models[1].SupportImage);   // 空列表=未声明
            Assert.Null(models[2].SupportImage);
            Assert.Null(models[3].SupportImage);   // 只有audio，没有明确说明image，所以为null
            Assert.True(models[3].SupportAudio);    // audio明确为true
        }
    }
}
