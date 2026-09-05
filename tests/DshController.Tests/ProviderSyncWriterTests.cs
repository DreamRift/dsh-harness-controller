// ============================================================================
//  ProviderSyncWriterTests — 写入引擎离线单测（改版·供应商同步 / 写入生效）
// ============================================================================

using System;
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

        private static InstanceProviderEntry Entry()
        {
            MappingResult m = ProviderConfigMapper.ToEntry(new ProviderPreset
            {
                Name = "DeepSeek",
                Kind = "deepseek",
                BaseUrl = "https://api.deepseek.com",
                DefaultModel = "deepseek-chat"
            });
            return m.Entry;
        }

        private static string Render(InstanceProviderEntry e) => ProviderSyncPlan.RenderYamlBlock(e);

        [Fact]
        public void 无providers段_追加根段()
        {
            File.WriteAllText(_file, "version: 1");
            Assert.True(new ProviderSyncWriter().Apply(_file, Entry(), out string err), "err=" + err);
            string content = File.ReadAllText(_file);
            Assert.Contains("providers:", content);
            Assert.Contains("deepseek-deepseek:", content);
            Assert.StartsWith("version: 1", content);
        }

        [Fact]
        public void 新增key_保留其他providers与其余内容()
        {
            File.WriteAllText(_file, JoinLines(
                "version: 1",
                "",
                "providers:",
                "  xinyunspace:",
                "    displayName: xinyun",
                "    api: openai-completions",
                "    baseURL: https://api.xinyunspace.com/v1",
                "tail: keep"));
            Assert.True(new ProviderSyncWriter().Apply(_file, Entry(), out string err), "err=" + err);
            string content = File.ReadAllText(_file);
            Assert.Contains("deepseek-deepseek:", content);
            Assert.Contains("xinyunspace:", content);
            Assert.Contains("tail: keep", content);
        }

        [Fact]
        public void 更新key_原位替换_其他键保留()
        {
            File.WriteAllText(_file, JoinLines(
                "providers:",
                "  deepseek-deepseek:",
                "    displayName: old",
                "    api: openai-completions",
                "  other:",
                "    displayName: other"));
            Assert.True(new ProviderSyncWriter().Apply(_file, Entry(), out string err), "err=" + err);
            string content = File.ReadAllText(_file);
            Assert.Contains("displayName: DeepSeek", content);
            Assert.DoesNotContain("displayName: old", content);
            Assert.Contains("other:", content);
        }

        [Fact]
        public void 只读目标_失败且原配置不动()
        {
            File.WriteAllText(_file, "original-content");
            File.SetAttributes(_file, FileAttributes.ReadOnly);
            try
            {
                bool ok = new ProviderSyncWriter().Apply(_file, Entry(), out string err);
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
        public void 读回一致_内容等于渲染块()
        {
            File.WriteAllText(_file, JoinLines("providers:", "  keep:", "    api: openai-completions"));
            InstanceProviderEntry e = Entry();
            Assert.True(new ProviderSyncWriter().Apply(_file, e, out _));
            char CR = (char)13;
            string[] lines = File.ReadAllText(_file).Split((char)10).Select(l => l.TrimEnd(CR)).ToArray();
            int start = -1;
            for (int i = 0; i < lines.Length; i++) if (lines[i].TrimEnd() == "  deepseek-deepseek:") { start = i; break; }
            Assert.True(start >= 0);
            var got = new System.Collections.Generic.List<string>();
            for (int i = start; i < lines.Length; i++)
            {
                string l = lines[i];
                if (i > start && l.StartsWith("  ") && l.Length >= 2 && l[2] != (char)32 && l[2] != (char)9) break;
                if (l.StartsWith("  ")) got.Add(l.Substring(2));
            }
            string actual = string.Join(Environment.NewLine, got).TrimEnd();
            Assert.Equal(Render(e), actual);
        }
    }
}
