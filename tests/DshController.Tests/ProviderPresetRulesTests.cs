// ============================================================================
//  ProviderPresetRulesTests — dsh 模型页校验规则移植的离线断言（API 页适配轮）
//  钉死：路由形、密钥判定、容量 K/M 解析与最短回写、模型目录校验。模式=正确性（C）。
// ============================================================================

using System.Collections.Generic;
using DshController.Core;
using Xunit;

namespace DshController.Tests
{
    public class ProviderPresetRulesTests
    {
        // ---- Provider ID 路由（dsh ROUTE_PATTERN） ----------------------------

        [Theory]
        [InlineData("deepseek")]
        [InlineData("acme-gateway")]
        [InlineData("a1-b2-c3")]
        [InlineData("m2")]
        public void 路由_合法形态(string route)
        {
            Assert.True(ProviderPresetRules.IsValidRoute(route));
        }

        [Theory]
        [InlineData("")]
        [InlineData("DeepSeek")]
        [InlineData("1abc")]
        [InlineData("-abc")]
        [InlineData("abc-")]
        [InlineData("acme_gateway")]
        [InlineData("acme gateway")]
        [InlineData("中文名")]
        public void 路由_非法形态(string route)
        {
            Assert.False(ProviderPresetRules.IsValidRoute(route));
        }

        [Fact]
        public void 路由_超长拒绝()
        {
            Assert.False(ProviderPresetRules.IsValidRoute(new string('a', 65)));
            Assert.True(ProviderPresetRules.IsValidRoute(new string('a', 64)));
        }

        // ---- API 密钥判定（dsh apiKeyFailure） --------------------------------

        [Fact]
        public void 密钥_空为非失败()
        {
            Assert.Null(ProviderPresetRules.ApiKeyFailure(null, forNew: false));
            Assert.Null(ProviderPresetRules.ApiKeyFailure("", forNew: false));
        }

        [Fact]
        public void 密钥_纯空白按编辑与新建给不同文案()
        {
            Assert.Equal(PresetCopy.KeyBlank, ProviderPresetRules.ApiKeyFailure("   ", forNew: false));
            Assert.Equal(PresetCopy.KeyBlankNew, ProviderPresetRules.ApiKeyFailure("   ", forNew: true));
        }

        [Fact]
        public void 密钥_环境变量形_引号包裹_非法字符都报格式错()
        {
            Assert.Equal(PresetCopy.KeyIllegal, ProviderPresetRules.ApiKeyFailure("DEEPSEEK_API_KEY=sk-x", forNew: false));
            Assert.Equal(PresetCopy.KeyIllegal, ProviderPresetRules.ApiKeyFailure("'sk-wrapped'", forNew: false));
            Assert.Equal(PresetCopy.KeyIllegal, ProviderPresetRules.ApiKeyFailure("\"sk-wrapped\"", forNew: false));
            Assert.Equal(PresetCopy.KeyIllegal, ProviderPresetRules.ApiKeyFailure("sk has space", forNew: false));
            Assert.Equal(PresetCopy.KeyIllegal, ProviderPresetRules.ApiKeyFailure("密钥sk", forNew: false));
        }

        [Fact]
        public void 密钥_可打印ASCII合法()
        {
            Assert.Null(ProviderPresetRules.ApiKeyFailure("sk-abc123_XY.-~!@#$%^&*()", forNew: false));
        }

        // ---- 容量解析与回写（dsh parseCapacity/formatCapacity，千进制） --------

        [Fact]
        public void 容量_空为继承()
        {
            Assert.True(ProviderPresetRules.TryParseCapacity("", out long? v1));
            Assert.Null(v1);
            Assert.True(ProviderPresetRules.TryParseCapacity("   ", out long? v2));
            Assert.Null(v2);
        }

        [Fact]
        public void 容量_K_M_与裸数字()
        {
            Assert.True(ProviderPresetRules.TryParseCapacity("256K", out long? v1));
            Assert.Equal(256000, v1);
            Assert.True(ProviderPresetRules.TryParseCapacity("1m", out long? v2));
            Assert.Equal(1000000, v2);
            Assert.True(ProviderPresetRules.TryParseCapacity("131072", out long? v3));
            Assert.Equal(131072, v3);
            Assert.True(ProviderPresetRules.TryParseCapacity("1.5K", out long? v4));
            Assert.Equal(1500, v4);
        }

        [Fact]
        public void 容量_非法文本拒绝()
        {
            Assert.False(ProviderPresetRules.TryParseCapacity("abc", out long? _));
            Assert.False(ProviderPresetRules.TryParseCapacity("-5", out long? _));
            Assert.False(ProviderPresetRules.TryParseCapacity("8 KB", out long? _));
        }

        [Fact]
        public void 容量_回写取最短可往返形式()
        {
            Assert.Equal("1M", ProviderPresetRules.FormatCapacity(1000000));
            Assert.Equal("256K", ProviderPresetRules.FormatCapacity(256000));
            Assert.Equal("131072", ProviderPresetRules.FormatCapacity(131072));
            Assert.Equal("0", ProviderPresetRules.FormatCapacity(0));
        }

        // ---- 模型目录校验（dsh validateDeepSeekModels） ------------------------

        [Fact]
        public void 模型_空目录与全空值合法()
        {
            Assert.Null(ProviderPresetRules.ValidateModels(null));
            Assert.Null(ProviderPresetRules.ValidateModels(new List<PresetModel>()));
        }

        [Fact]
        public void 模型_ID空与重复报首错()
        {
            var rows = new List<PresetModel> { new PresetModel { Id = "m1" }, new PresetModel { Id = "" } };
            var e1 = ProviderPresetRules.ValidateModels(rows);
            Assert.Equal(1, e1.Value.Key);
            Assert.Equal(PresetCopy.ModelIdRequired, e1.Value.Value);

            var dup = new List<PresetModel> { new PresetModel { Id = "m1" }, new PresetModel { Id = "m1" } };
            var e2 = ProviderPresetRules.ValidateModels(dup);
            Assert.Equal(1, e2.Value.Key);
            Assert.Equal(PresetCopy.ModelIdDuplicate, e2.Value.Value);
        }

        [Fact]
        public void 模型_名称空与容量非正报对应错()
        {
            var badName = new List<PresetModel> { new PresetModel { Id = "m1", Name = " " } };
            Assert.Equal(PresetCopy.ModelNameInvalid, ProviderPresetRules.ValidateModels(badName).Value.Value);

            var badCtx = new List<PresetModel> { new PresetModel { Id = "m1", ContextWindow = 0 } };
            Assert.Equal(PresetCopy.ModelContextInvalid, ProviderPresetRules.ValidateModels(badCtx).Value.Value);

            var badMax = new List<PresetModel> { new PresetModel { Id = "m1", MaxTokens = -1 } };
            Assert.Equal(PresetCopy.ModelMaxTokensInvalid, ProviderPresetRules.ValidateModels(badMax).Value.Value);
        }

        [Fact]
        public void 模型_合法目录通过()
        {
            var rows = new List<PresetModel>
            {
                new PresetModel { Id = "deepseek-chat", Name = "Chat", ContextWindow = 64000 },
                new PresetModel { Id = "deepseek-reasoner" }
            };
            Assert.Null(ProviderPresetRules.ValidateModels(rows));
        }
    }
}
