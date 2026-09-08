// ============================================================================
//  ProviderEditorDraftTests — API 页编辑卡状态机离线断言（照 dsh 模型页适配轮）
//  钉死：就绪门（路由/地址/模型/密钥）、密钥只写语义、容量文本解析进模型行、
//  探针候选采纳（按 id 合并、已有行保留、容量自动填入）、VM 主从状态机。
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DshController.Core;
using DshController.ViewModels;
using Xunit;

namespace DshController.Tests
{
    public class ProviderEditorDraftTests : IDisposable
    {
        private readonly string _file = Path.Combine(Path.GetTempPath(), "dsh-draft-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".json");

        public void Dispose()
        {
            try { File.Delete(_file); }
            catch (Exception) { /* 理由: 测试临时文件清理失败可忽略 */ }
        }

        private static ProviderPreset Stored()
        {
            return new ProviderPreset
            {
                Id = "abc123",
                ProviderId = "acme",
                Name = "Acme",
                Kind = "openai-completions",
                BaseUrl = "https://api.acme.com/v1",
                ApiKey = "sk-stored",
                Models = new List<PresetModel>
                {
                    new PresetModel { Id = "acme-chat", Name = "Chat", ContextWindow = 128000 },
                    new PresetModel { Id = "acme-mini" }
                }
            };
        }

        private static DiscoveredModel Disc(string id, long? ctx = null, long? max = null, string name = null)
        {
            return new DiscoveredModel { Id = id, Name = name, ContextWindow = ctx, MaxTokens = max };
        }

        // ---- 新建卡（dsh CustomProviderCard 门） -------------------------------

        [Fact]
        public void 新建卡_初始不可保存_填齐即就绪()
        {
            var d = ProviderEditorDraft.ForCreate(_ => false);
            Assert.False(d.SaveReady);
            d.Route = "acme";
            d.Name = "Acme";
            d.BaseUrl = "https://api.acme.com/v1";
            Assert.False(d.SaveReady);                       // 还差模型
            Assert.Equal(PresetCopy.NeedsModels, d.HintText);
            d.AddModelRow();
            d.Models[0].Id = "acme-chat";
            Assert.True(d.SaveReady);
            Assert.Equal("", d.HintText);
        }

        [Fact]
        public void 新建卡_缺地址提示_路由非法与占用报错()
        {
            var d = ProviderEditorDraft.ForCreate(_ => false);
            d.Name = "A";
            d.AddModelRow();
            d.Models[0].Id = "m";
            Assert.Equal(PresetCopy.NeedsBaseUrl, d.HintText);
            d.Route = "Bad_Route";
            Assert.False(d.SaveReady);
            Assert.Equal(PresetCopy.RouteInvalid, d.RouteError);
            d.Route = "acme";
            Assert.Equal("", d.RouteError);
            var taken = ProviderEditorDraft.ForCreate(r => r == "acme");
            taken.Name = "A";
            taken.BaseUrl = "https://x.com";
            taken.AddModelRow();
            taken.Models[0].Id = "m";
            taken.Route = "acme";
            Assert.Equal(PresetCopy.RouteTaken, taken.RouteError);
            Assert.False(taken.SaveReady);
        }

        [Fact]
        public void 新建卡_密钥格式不过关不就绪()
        {
            var d = ProviderEditorDraft.ForCreate(_ => false);
            d.Route = "acme";
            d.Name = "A";
            d.BaseUrl = "https://x.com";
            d.AddModelRow();
            d.Models[0].Id = "m";
            d.ApiKeyDraft = "sk ok space";
            Assert.False(d.SaveReady);
            Assert.Equal(PresetCopy.KeyIllegal, d.KeyMessage);
            d.ApiKeyDraft = "sk-ok";
            Assert.True(d.SaveReady);
        }

        // ---- 编辑卡（密钥只写；容量文本） ---------------------------------------

        [Fact]
        public void 编辑卡_空密钥保持已存_新值替换()
        {
            var edit = ProviderEditorDraft.ForEdit(Stored(), _ => false);
            Assert.True(edit.HasStoredKey);
            Assert.Equal("已配置——输入新值可替换", edit.KeyPlaceholder);
            Assert.True(edit.SaveReady);                     // 既有完整预设打开即可保存
            Assert.True(edit.TryBuild(out ProviderPreset p1, out _));
            Assert.Equal("sk-stored", p1.ApiKey);            // 空字段=保持
            edit.ApiKeyDraft = "  ";                          // 纯空白=keyBlank 拦保存门
            Assert.False(edit.SaveReady);
            edit.ApiKeyDraft = "sk-new";
            Assert.True(edit.TryBuild(out ProviderPreset p2, out _));
            Assert.Equal("sk-new", p2.ApiKey);
        }

        [Fact]
        public void 编辑卡_容量文本进模型行并可编辑保留()
        {
            var edit = ProviderEditorDraft.ForEdit(Stored(), _ => false);
            Assert.Equal("128K", edit.Models[0].ContextWindowText);   // 最短形式回写
            Assert.Equal("", edit.Models[1].ContextWindowText);
            edit.Models[0].MaxTokensText = "8192";
            Assert.True(edit.TryBuild(out ProviderPreset p, out _));
            Assert.Equal(2, p.Models.Count);
            Assert.Equal(128000, p.Models[0].ContextWindow);
            Assert.Equal(8192, p.Models[0].MaxTokens);
            Assert.Null(p.Models[1].ContextWindow);
        }

        [Fact]
        public void 编辑卡_行错误阻断保存()
        {
            var edit = ProviderEditorDraft.ForEdit(Stored(), _ => false);
            edit.Models[0].ContextWindowText = "abc";
            Assert.False(edit.SaveReady);
            Assert.Equal(PresetCopy.ModelContextInvalid, edit.ModelsHint);
            Assert.Equal(PresetCopy.ModelContextInvalid, edit.Models[0].RowError);
        }

        // ---- 探针候选采纳（dsh adoptPicked 语义） -------------------------------

        [Fact]
        public void 采纳_按id合并_已有行保留_新行容量自动填入()
        {
            var edit = ProviderEditorDraft.ForEdit(Stored(), _ => false);
            edit.ApplyFetched(new List<DiscoveredModel>
            {
                Disc("acme-chat", ctx: 999, max: 1, name: "NewName"),   // 已有：原样保留
                Disc("acme-plus", ctx: 64000, max: 8192, name: "Plus")  // 新：自动填容量
            });
            Assert.Equal(3, edit.Models.Count);
            Assert.Equal("128K", edit.Models[0].ContextWindowText);   // 未被候选覆盖
            Assert.Equal("", edit.Models[0].MaxTokensText);
            Assert.Equal("Chat", edit.Models[0].Name);
            DraftModelRow plus = edit.Models.Single(m => m.Id == "acme-plus");
            Assert.Equal("64K", plus.ContextWindowText);
            Assert.Equal("8192", plus.MaxTokensText);
            Assert.Equal("Plus", plus.Name);
        }

        // ---- llm-pi-ai 迁移轮：多模态三态进出草稿 --------------------------------

        [Fact]
        public void 多模态_编辑卡带出_物化回写()
        {
            var stored = Stored();
            stored.Models[0].SupportImage = true;
            stored.Models[1].SupportImage = false;
            var edit = ProviderEditorDraft.ForEdit(stored, _ => false);
            Assert.True(edit.Models[0].SupportImage);
            Assert.False(edit.Models[1].SupportImage);
            edit.Models[0].SupportImage = null;                 // 三态可改回未知
            Assert.True(edit.TryBuild(out ProviderPreset p, out _));
            Assert.Null(p.Models[0].SupportImage);
            Assert.False(p.Models[1].SupportImage);
        }

        [Fact]
        public void 多模态_探针候选采纳带出三态()
        {
            var edit = ProviderEditorDraft.ForEdit(Stored(), _ => false);
            edit.ApplyFetched(new List<DiscoveredModel>
            {
                new DiscoveredModel { Id = "acme-vlm", SupportImage = true },
                new DiscoveredModel { Id = "acme-txt", SupportImage = false },
                new DiscoveredModel { Id = "acme-q" }
            });
            Assert.True(edit.Models.Single(m => m.Id == "acme-vlm").SupportImage);
            Assert.False(edit.Models.Single(m => m.Id == "acme-txt").SupportImage);
            Assert.Null(edit.Models.Single(m => m.Id == "acme-q").SupportImage);
        }

        // ---- VM 主从状态机 ------------------------------------------------------

        [Fact]
        public void VM_新建保存进台账_选中与行同步()
        {
            var vm = new ProviderPresetsViewModel(new ProviderPresetStore(_file));
            vm.BeginCreate();
            Assert.NotNull(vm.Editor);
            Assert.True(vm.Editor.IsNew);
            vm.Editor.Route = "acme";
            vm.Editor.Name = "Acme";
            vm.Editor.BaseUrl = "https://api.acme.com/v1";
            vm.Editor.ApiKeyDraft = "sk-1";
            vm.Editor.AddModelRow();
            vm.Editor.Models[0].Id = "acme-chat";
            Assert.True(vm.TrySaveEditor(out string err), "err=" + err);
            // 保存后以已存值重开编辑卡（主从布局不留死端）
            Assert.NotNull(vm.Editor);
            Assert.False(vm.Editor.IsNew);
            Assert.Equal("acme", vm.Editor.Route);
            Assert.Equal(2, vm.Rows.Count);                  // 内置官方置顶 + 新行
            ProviderPresetRow saved = vm.RowById(vm.SelectedId);
            Assert.Equal("acme", saved.Route);
            Assert.True(saved.HasKey);
            Assert.Contains("启用", saved.MetaText);
        }

        [Fact]
        public void VM_选中打开编辑卡_取消清选中()
        {
            var vm = new ProviderPresetsViewModel(new ProviderPresetStore(_file));
            vm.TryAdd("Acme", "openai-completions", "https://x.com/v1", "sk-1", "", true, out string id, out _);
            vm.CancelEditor();                       // TryAdd 已自动选中，先清再走 Select 路径
            Assert.True(vm.Select(id));
            Assert.NotNull(vm.Editor);
            Assert.False(vm.Editor.IsNew);
            Assert.Equal("openai-completions-acme", vm.Editor.Route);   // 旧档案回填 slug（kind+name）
            vm.CancelEditor();
            Assert.Null(vm.Editor);
            Assert.Equal("", vm.SelectedId);
            Assert.False(vm.HasEditor);
        }

        [Fact]
        public void VM_探针目标取表单当前值_无地址被拦()
        {
            var vm = new ProviderPresetsViewModel(new ProviderPresetStore(_file));
            Assert.False(vm.TryGetProbeTarget(out _, out _, out _));    // 无编辑卡
            vm.TryAdd("Acme", "openai-completions", "https://x.com/v1", "sk-stored", "", true, out string id, out _);
            vm.CancelEditor();
            vm.Select(id);
            Assert.True(vm.TryGetProbeTarget(out string url, out string key, out string blocked));
            Assert.Equal("", blocked);
            Assert.Equal("https://x.com/v1", url);
            Assert.Equal("sk-stored", key);                             // 未输新值=用已存
            vm.Editor.BaseUrl = "";
            Assert.False(vm.TryGetProbeTarget(out _, out _, out string blocked2));
            Assert.Equal(PresetCopy.FetchNeedsBaseUrl, blocked2);
            vm.Editor.BaseUrl = "https://y.com";
            vm.Editor.ApiKeyDraft = "sk-typed";
            Assert.True(vm.TryGetProbeTarget(out _, out string key2, out _));
            Assert.Equal("sk-typed", key2);                             // 表单当前所填优先（dsh 语义）
        }

        [Fact]
        public void VM_候选预勾选未知项_采纳回草稿()
        {
            var vm = new ProviderPresetsViewModel(new ProviderPresetStore(_file));
            vm.TryAdd("Acme", "openai-completions", "https://x.com/v1", "", "acme-chat", true, out string id, out _);
            vm.CancelEditor();
            vm.Select(id);
            vm.SetCandidates(new List<DiscoveredModel> { Disc("acme-chat"), Disc("acme-new", ctx: 1000) });
            Assert.Equal(new[] { "acme-new" }, vm.PickedIds.OrderBy(x => x).ToArray());
            vm.TogglePick("acme-new");
            Assert.Empty(vm.PickedIds);
            vm.TogglePick("acme-new");
            vm.AdoptPicked();
            Assert.Equal(2, vm.Editor.Models.Count);
            Assert.Equal("1K", vm.Editor.Models[1].ContextWindowText);
        }

        [Fact]
        public void VM_内置官方默认常驻且不可删()
        {
            var vm = new ProviderPresetsViewModel(new ProviderPresetStore(_file));
            Assert.Single(vm.Rows);
            ProviderPresetRow b = vm.Rows[0];
            Assert.True(b.IsBuiltin);
            Assert.Equal("DeepSeek 官方", b.Name);
            Assert.Equal("官方", b.TagText);
            Assert.False(vm.TryDelete(b.Id));
            Assert.Single(vm.Rows);
            Assert.True(vm.Select(b.Id));
            Assert.NotNull(vm.Editor);
            Assert.Equal(ProviderPreset.BuiltinRoute, vm.Editor.Route);
            Assert.True(vm.Editor.SaveReady);                          // 出厂即完整可保存
            Assert.False(vm.CanDeleteSelected);                        // 删除按钮禁用
            vm.CancelEditor();
        }

        [Fact]
        public void VM_保存失败草稿保留并回显错误()
        {
            var vm = new ProviderPresetsViewModel(new ProviderPresetStore(_file));
            vm.BeginCreate();
            vm.Editor.Route = "dup";
            vm.Editor.Name = "First";
            vm.Editor.BaseUrl = "https://x.com";
            vm.Editor.AddModelRow();
            vm.Editor.Models[0].Id = "m";
            Assert.True(vm.TrySaveEditor(out _));
            // 再建同路由 → 就绪门实时拦下（路由占用显示在卡内）
            vm.BeginCreate();
            vm.Editor.Route = "dup";
            vm.Editor.Name = "Second";
            vm.Editor.BaseUrl = "https://x.com";
            vm.Editor.AddModelRow();
            vm.Editor.Models[0].Id = "m";
            Assert.False(vm.Editor.SaveReady);
            Assert.Equal(PresetCopy.RouteTaken, vm.Editor.RouteError);
            Assert.False(vm.TrySaveEditor(out string err));
            Assert.Equal(PresetCopy.RouteTaken, err);
            Assert.Equal(PresetCopy.RouteTaken, vm.Editor.SaveError);
            Assert.NotNull(vm.Editor);                                  // 草稿保留
            Assert.Equal("First", vm.Rows.Last().Name);                 // 原台账未动
        }
    }
}
