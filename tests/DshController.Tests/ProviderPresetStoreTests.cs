// ============================================================================
//  ProviderPresetStoreTests — 供应商预设台账离线单测（改版·API 预设页 / 预设存储）
//  覆盖：增删改查、校验规则、落盘路径、损坏/空文件边界。模式=正确性（C）。
// ============================================================================

using System;
using System.IO;
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
            Assert.Empty(s.All());
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
            Assert.Empty(s.All());
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
            Assert.Empty(s2.All());
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
        public void 损坏文件_空台账不抛()
        {
            File.WriteAllText(_file, "{not-json");
            var s = new ProviderPresetStore(_file);
            Assert.Empty(s.All());
        }

        [Fact]
        public void 缺文件_空台账不抛()
        {
            var s = new ProviderPresetStore(_file);
            Assert.Empty(s.All());
        }

        [Fact]
        public void 落盘路径经AppPaths可复核()
        {
            Assert.Equal("api-presets.json", Path.GetFileName(AppPaths.ProviderPresetsFile));
            Assert.Equal(AppPaths.StateDir, Path.GetDirectoryName(AppPaths.ProviderPresetsFile));
        }
    }
}
