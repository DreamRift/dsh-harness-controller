// ============================================================================
//  ProviderSyncPlanTests — 同步预览计划离线单测（改版·供应商同步 / 同步预览窗）
// ============================================================================

using System.Collections.Generic;
using System.Linq;
using DshController.Core;
using Xunit;

namespace DshController.Tests
{
    public class ProviderSyncPlanTests
    {
        private static readonly (string, string)[] Instances = { ("a1", "windows:3080"), ("a2", "windows:3081") };

        private static ProviderPreset Preset()
        {
            return new ProviderPreset
            {
                Name = "DeepSeek",
                Kind = "deepseek",
                BaseUrl = "https://api.deepseek.com",
                DefaultModel = "deepseek-chat",
                ApiKey = "sk-1"
            };
        }

        [Fact]
        public void 全部新增_字段与note齐()
        {
            var rows = ProviderSyncPlan.PlanFor(Preset(), Instances, id => null);
            Assert.Equal(2, rows.Count);
            Assert.All(rows, r =>
            {
                Assert.Equal("新增", r.Kind);
                Assert.Contains("displayName", r.Fields);
                Assert.Contains("api", r.Fields);
                Assert.Contains("models", r.Fields);
                Assert.Contains(r.Notes, n => n.Contains("apiKeyEnv"));
            });
            Assert.Equal("windows:3080", rows[0].InstanceLabel);
        }

        [Fact]
        public void 更新_只列差异字段()
        {
            var cur = ProviderConfigMapper.ToEntry(Preset()).Entry;
            cur.DisplayName = "旧名字";   // 只有 displayName 不同
            var rows = ProviderSyncPlan.PlanFor(Preset(), Instances, id => id == "a1" ? cur : null);
            SyncPlanRow r1 = rows[0];
            Assert.Equal("更新", r1.Kind);
            Assert.Equal(new[] { "displayName" }, r1.Fields.ToArray());
            Assert.Equal("新增", rows[1].Kind);
        }

        [Fact]
        public void 完全一致_无变化()
        {
            var cur = ProviderConfigMapper.ToEntry(Preset()).Entry;
            var rows = ProviderSyncPlan.PlanFor(Preset(), Instances, id => cur);
            Assert.All(rows, r =>
            {
                Assert.Equal("无变化", r.Kind);
                Assert.Contains(r.Notes, n => n.Contains("已一致"));
            });
        }

        [Fact]
        public void 空预设与空清单_空计划()
        {
            Assert.Empty(ProviderSyncPlan.PlanFor(null, Instances, null));
            Assert.Empty(ProviderSyncPlan.PlanFor(Preset(), new List<(string, string)>(), null));
        }

        [Fact]
        public void 规范渲染_含键与字段()
        {
            MappingResult m = ProviderConfigMapper.ToEntry(Preset());
            string text = ProviderSyncPlan.RenderYamlBlock(m.Entry);
            Assert.Contains("deepseek-deepseek:", text);
            Assert.Contains("displayName: DeepSeek", text);
            Assert.Contains("api: openai-completions", text);
            Assert.Contains("apiKeyEnv: DSH_PRESET_DEEPSEEK_DEEPSEEK", text);
            Assert.Contains("baseURL: 'https://api.deepseek.com'", text);
            Assert.Contains("models:", text);
            Assert.Contains("- id: deepseek-chat", text);
        }

        [Fact]
        public void 引号宽松_特殊字符才加引号()
        {
            Assert.Equal("plain", ProviderSyncPlan.Quote("plain"));
            Assert.Equal("'a: b'", ProviderSyncPlan.Quote("a: b"));
            Assert.Equal("'it''s'", ProviderSyncPlan.Quote("it's"));
            Assert.Equal("''", ProviderSyncPlan.Quote(""));
        }

        // ---- llm-pi-ai 迁移轮：新字段渲染 / 官方预设计划行 --------------------------

        [Fact]
        public void 规范渲染_非官方模型带input与思考档四档()
        {
            var p = Preset();
            p.ProviderId = "acme";
            p.Models = new System.Collections.Generic.List<PresetModel>
            {
                new PresetModel { Id = "vlm", SupportImage = true }
            };
            string text = ProviderSyncPlan.RenderYamlBlock(ProviderConfigMapper.ToEntry(p).Entry);
            Assert.Contains("input: [text, image]", text);
            Assert.Contains("reasoningEfforts:", text);
            Assert.Contains("off: null", text);
            Assert.Contains("low: low", text);
            Assert.Contains("high: high", text);
            Assert.Contains("max: max", text);
        }

        [Fact]
        public void 规范渲染_官方模型无思考档_多模态保留input()
        {
            string text = ProviderSyncPlan.RenderYamlBlock(
                ProviderConfigMapper.ToEntry(ProviderPreset.CreateBuiltinDefault()).Entry);
            Assert.DoesNotContain("reasoningEfforts", text);
            Assert.Contains("input: [text, image]", text);   // vision-exp 出厂即多模态
            Assert.DoesNotContain("input: [text]", text);    // 纯文本模型未知模态 → 省略
        }

        [Fact]
        public void 官方预设计划_仅密钥引用与清理note()
        {
            var official = ProviderPreset.CreateBuiltinDefault();
            official.ApiKey = "sk-x";
            var rows = ProviderSyncPlan.PlanFor(official, Instances, id => null);
            Assert.Equal(2, rows.Count);
            Assert.All(rows, r =>
            {
                Assert.Equal("新增", r.Kind);
                Assert.Contains("apiKeyEnv（llm-deepseek）", r.Fields);
                Assert.DoesNotContain("models", r.Fields);
                Assert.Contains(r.Notes, n => n.Contains("llm-deepseek"));
                Assert.Contains(r.Notes, n => n.Contains("旧根级 providers.deepseek-official"));
            });
        }

        [Fact]
        public void 官方预设未填密钥_计划为无变化()
        {
            var official = ProviderPreset.CreateBuiltinDefault();
            official.ApiKey = "";
            var rows = ProviderSyncPlan.PlanFor(official, Instances, id => null);
            Assert.All(rows, r =>
            {
                Assert.Equal("无变化", r.Kind);
                Assert.Empty(r.Fields);
            });
        }
    }
}
