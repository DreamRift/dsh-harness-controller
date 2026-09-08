// ============================================================================
//  ProviderSyncWriterTests — 写入引擎离线单测（llm-pi-ai 迁移轮）
//  目标=实例 settings.yaml 的 llm-pi-ai.providers.<key>（dsh 实际读取）；
//  官方预设仅写 llm-deepseek.apiKeyEnv；根级 providers 旧块迁移清理。
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DshController.Core;
using Xunit;

namespace DshController.Tests
{
    public class ProviderSyncWriterTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "dsh-writer-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        private readonly string _file;

        public ProviderSyncWriterTests()
        {
            Directory.CreateDirectory(_dir);
            _file = Path.Combine(_dir, "settings.yaml");
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); }
            catch (Exception) { /* 理由: 测试临时目录清理失败可忽略 */ }
        }

        private static string JoinLines(params string[] ls) => string.Join(Environment.NewLine, ls);

        private static ProviderPreset AcmePreset()
        {
            return new ProviderPreset
            {
                ProviderId = "acme",
                Name = "Acme",
                Kind = "openai-completions",
                BaseUrl = "https://api.acme.com/v1",
                ApiKey = "sk-1",
                Models =
                {
                    new PresetModel { Id = "acme-chat", Name = "Chat", ContextWindow = 128000, MaxTokens = 8192, SupportImage = true },
                    new PresetModel { Id = "acme-plain", SupportImage = false }
                }
            };
        }

        [Fact]
        public void 无llm_pi_ai段_文件尾追加命名空间与块()
        {
            File.WriteAllText(_file, "version: 1");
            Assert.True(new ProviderSyncWriter().Apply(_file, ProviderSyncWrite.For(AcmePreset()), out string err), "err=" + err);
            string content = File.ReadAllText(_file);
            Assert.StartsWith("version: 1", content);
            Assert.Contains("llm-pi-ai:", content);
            Assert.Contains("  providers:", content);
            Assert.Contains("    acme:", content);
            Assert.Contains("      displayName: Acme", content);
            // 非官方自动补思考档四档 + 多模态 input
            Assert.Contains("          input: [text, image]", content);
            Assert.Contains("          reasoningEfforts:", content);
            Assert.Contains("            off: null", content);
            Assert.Contains("            max: max", content);
        }

        [Fact]
        public void 新增key_同层兄弟与其余内容保留()
        {
            File.WriteAllText(_file, JoinLines(
                "version: 1",
                "",
                "llm-pi-ai:",
                "  providers:",
                "    xinyunspace:",
                "      displayName: xinyun",
                "      api: openai-completions",
                "tail: keep"));
            Assert.True(new ProviderSyncWriter().Apply(_file, ProviderSyncWrite.For(AcmePreset()), out string err), "err=" + err);
            string content = File.ReadAllText(_file);
            Assert.Contains("    acme:", content);
            Assert.Contains("    xinyunspace:", content);
            Assert.Contains("tail: keep", content);
        }

        [Fact]
        public void 同key增补_路由字段更新_未知字段保留()
        {
            File.WriteAllText(_file, JoinLines(
                "llm-pi-ai:",
                "  providers:",
                "    acme:",
                "      displayName: old",
                "      api: openai-responses",
                "      models:",
                "        - id: acme-chat",
                "          compat:",
                "            chatTemplateKwargs: {}"));
            Assert.True(new ProviderSyncWriter().Apply(_file, ProviderSyncWrite.For(AcmePreset()), out string err), "err=" + err);
            string content = File.ReadAllText(_file);
            Assert.Contains("      displayName: Acme", content);
            Assert.DoesNotContain("displayName: old", content);
            Assert.Contains("compat:", content);                          // 宽松段保留
            Assert.Contains("chatTemplateKwargs: {}", content);
            Assert.Contains("      baseURL: 'https://api.acme.com/v1'", content);
        }

        [Fact]
        public void 同key增补_已有思考档不覆盖_预设新模型补四档()
        {
            File.WriteAllText(_file, JoinLines(
                "llm-pi-ai:",
                "  providers:",
                "    acme:",
                "      displayName: Acme",
                "      api: openai-completions",
                "      models:",
                "        - id: acme-chat",
                "          reasoningEfforts: false",
                "        - id: acme-legacy"));
            Assert.True(new ProviderSyncWriter().Apply(_file, ProviderSyncWrite.For(AcmePreset()), out string err), "err=" + err);
            string content = File.ReadAllText(_file);
            Assert.Contains("          reasoningEfforts: false", content);   // 用户显式声明保留
            int falseCount = content.Split("reasoningEfforts: false").Length - 1;
            Assert.Equal(1, falseCount);
            // 实例侧多出的 acme-legacy 原样保留（预设只管自己的模型，不做思考档注入）
            Assert.Contains("        - id: acme-legacy", content);
            // 预设模型 acme-plain 是新增 → 规范渲染含四档
            Assert.Contains("        - id: acme-plain", content);
            Assert.Contains("            off: null", content);
            Assert.Contains("            max: max", content);
        }

        [Fact]
        public void 同key增补_input空数组覆盖_非空声明保留()
        {
            File.WriteAllText(_file, JoinLines(
                "llm-pi-ai:",
                "  providers:",
                "    acme:",
                "      displayName: Acme",
                "      api: openai-completions",
                "      models:",
                "        - id: acme-chat",
                "          input: []",
                "        - id: acme-plain",
                "          input: [text, image]"));
            Assert.True(new ProviderSyncWriter().Apply(_file, ProviderSyncWrite.For(AcmePreset()), out string err), "err=" + err);
            string content = File.ReadAllText(_file);
            // 预设 acme-chat=true：[] 视为未声明 → 覆盖为 [text, image]
            Assert.Contains("          input: [text, image]", content);
            Assert.DoesNotContain("input: []", content);
            // 实例侧 acme-plain 已显式 [text, image]，预设 false（[text]）不覆盖既有非空声明
            Assert.Contains("        - id: acme-plain", content);
        }

        [Fact]
        public void 同key增补_实例多出模型保留_新模型追加()
        {
            File.WriteAllText(_file, JoinLines(
                "llm-pi-ai:",
                "  providers:",
                "    acme:",
                "      displayName: Acme",
                "      api: openai-completions",
                "      models:",
                "        - id: acme-chat",
                "        - id: acme-extra",
                "          name: Extra"));
            Assert.True(new ProviderSyncWriter().Apply(_file, ProviderSyncWrite.For(AcmePreset()), out string err), "err=" + err);
            string content = File.ReadAllText(_file);
            Assert.Contains("        - id: acme-extra", content);    // 实例多出的模型保留
            Assert.Contains("          name: Extra", content);
            Assert.Contains("        - id: acme-plain", content);    // 预设新模型追加
        }

        [Fact]
        public void 官方预设_只写llm_deepseek密钥引用_不建llm_pi_ai块()
        {
            ProviderPreset official = ProviderPreset.CreateBuiltinDefault();
            official.ApiKey = "sk-official";
            File.WriteAllText(_file, JoinLines(
                "providers:",
                "  deepseek-official:",
                "    displayName: DeepSeek 官方",
                "    api: openai-completions",
                "    apiKeyEnv: DSH_PRESET_DEEPSEEK_OFFICIAL",
                "    baseURL: 'https://api.deepseek.com'",
                "    models:",
                "      - id: deepseek-v4-flash",
                "llm-deepseek:",
                "  models:",
                "    - id: deepseek-v4-flash"));
            Assert.True(new ProviderSyncWriter().Apply(_file, ProviderSyncWrite.For(official), out string err), "err=" + err);
            string content = File.ReadAllText(_file);
            Assert.Contains("llm-deepseek:", content);
            Assert.Contains("  apiKeyEnv: DSH_PRESET_DEEPSEEK_OFFICIAL", content);
            Assert.Contains("    - id: deepseek-v4-flash", content);   // 官方 models 不动
            Assert.DoesNotContain("llm-pi-ai:", content);              // 官方不建自定义提供方块
            Assert.DoesNotContain("providers:", content);              // 根级旧块连同空段一起清除
        }

        [Fact]
        public void 官方未填密钥_写计划为空不落盘()
        {
            ProviderPreset official = ProviderPreset.CreateBuiltinDefault();
            official.ApiKey = "";
            File.WriteAllText(_file, "original-content");
            Assert.False(new ProviderSyncWriter().Apply(_file, ProviderSyncWrite.For(official), out string err));
            Assert.Contains("写计划为空", err);
            Assert.Equal("original-content", File.ReadAllText(_file).TrimEnd());
        }

        [Fact]
        public void 同步清除根级旧块_段非空时只删本key()
        {
            File.WriteAllText(_file, JoinLines(
                "providers:",
                "  acme:",
                "    displayName: old-acme",
                "  other-preset:",
                "    displayName: other",
                "llm-pi-ai:",
                "  providers:"));
            Assert.True(new ProviderSyncWriter().Apply(_file, ProviderSyncWrite.For(AcmePreset()), out string err), "err=" + err);
            string[] lines = File.ReadAllLines(_file);
            Assert.Contains("  other-preset:", lines);                       // 同层其他键保留
            Assert.DoesNotContain("  acme:", lines);                          // 根级旧块清除（逐行比对，
            Assert.Contains("    acme:", lines);                              // 不受 llm-pi-ai 4 缩进块干扰）
        }

        [Fact]
        public void 只读目标_失败且原配置不动()
        {
            File.WriteAllText(_file, "original-content");
            File.SetAttributes(_file, FileAttributes.ReadOnly);
            try
            {
                bool ok = new ProviderSyncWriter().Apply(_file, ProviderSyncWrite.For(AcmePreset()), out string err);
                Assert.False(ok);
                Assert.Contains("写入失败", err);
            }
            finally
            {
                File.SetAttributes(_file, FileAttributes.Normal);
            }
            Assert.Equal("original-content", File.ReadAllText(_file).TrimEnd());
        }

        [Fact]
        public void 幂等_二次写入无变化不落盘()
        {
            File.WriteAllText(_file, "version: 1");
            var write = ProviderSyncWrite.For(AcmePreset());
            Assert.True(new ProviderSyncWriter().Apply(_file, write, out string err), "err=" + err);
            string afterFirst = File.ReadAllText(_file);
            Assert.Single(Directory.GetFiles(_dir, "*.bak-*"));            // 首写有备份
            Assert.True(new ProviderSyncWriter().Apply(_file, write, out err), "err=" + err);
            Assert.Equal(afterFirst, File.ReadAllText(_file));
            Assert.Single(Directory.GetFiles(_dir, "*.bak-*"));            // 无变化不产生新备份
        }
    }
}
