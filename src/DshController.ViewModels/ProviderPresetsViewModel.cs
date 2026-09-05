// ============================================================================
//  ProviderPresetsViewModel — API 页供应商预设编辑（改版·API 预设页 / 供应商编辑页）
//
//  · 数据只来自全局 ProviderPresetStore（AppPaths+JsonStore），页面零直读实例（铁律一）；
//  · 列表行 + 增/改/删：校验规则在 ProviderPreset.Validate（Core），本类只做形状映射；
//  · 弹窗由 App 层（PageHost）经 DialogService 呈现，本类不碰 UI；
//  · 变更后 Changed 事件供宿主刷新页头摘要。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using DshController.Core;

namespace DshController.ViewModels
{
    /// <summary>编辑页列表一行（展示层形状，ApiKey 只给掩码）。</summary>
    public sealed class ProviderPresetRow
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Kind { get; set; } = "";
        public string BaseUrl { get; set; } = "";
        public string DefaultModel { get; set; } = "";
        public bool Enabled { get; set; } = true;
        /// <summary>副行小字：端点/默认模型/启用态。</summary>
        public string MetaText
        {
            get
            {
                List<string> parts = new List<string>();
                if (!string.IsNullOrEmpty(BaseUrl)) parts.Add(BaseUrl);
                if (!string.IsNullOrEmpty(DefaultModel)) parts.Add("默认模型 " + DefaultModel);
                parts.Add(Enabled ? "启用" : "停用");
                return string.Join(" · ", parts);
            }
        }
    }

    public sealed class ProviderPresetsViewModel
    {
        private readonly ProviderPresetStore _store;

        /// <summary>宿主用它刷新页头/左栏摘要（Rows.Count）。</summary>
        public event Action Changed;

        public ObservableCollection<ProviderPresetRow> Rows { get; } = new ObservableCollection<ProviderPresetRow>();

        /// <summary>当前选中行 Id（App 层选中事件转发）。</summary>
        public string SelectedId { get; private set; } = "";

        public ProviderPresetsViewModel(ProviderPresetStore store)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            Refresh();
        }

        /// <summary>从台账重建列表（新到旧）。</summary>
        public void Refresh()
        {
            Rows.Clear();
            foreach (ProviderPreset p in _store.All())
            {
                Rows.Add(new ProviderPresetRow
                {
                    Id = p.Id,
                    Name = p.Name,
                    Kind = p.Kind,
                    BaseUrl = p.BaseUrl,
                    DefaultModel = p.DefaultModel,
                    Enabled = p.Enabled
                });
            }
            if (SelectedId.Length > 0 && !Rows.Any(r => r.Id == SelectedId)) SelectedId = "";
            Changed?.Invoke();
        }

        /// <summary>选中一行；返回是否变化。</summary>
        public bool Select(string id)
        {
            if (string.IsNullOrEmpty(id) || SelectedId == id) return false;
            if (!Rows.Any(r => r.Id == id)) return false;
            SelectedId = id;
            return true;
        }

        public ProviderPresetRow RowById(string id) => Rows.FirstOrDefault(r => r.Id == id);

        private static ProviderPreset Build(string name, string kind, string baseUrl, string apiKey, string model, bool enabled)
        {
            return new ProviderPreset
            {
                Name = (name ?? "").Trim(),
                Kind = (kind ?? "").Trim(),
                BaseUrl = (baseUrl ?? "").Trim(),
                ApiKey = (apiKey ?? "").Trim(),
                DefaultModel = (model ?? "").Trim(),
                Enabled = enabled
            };
        }

        /// <summary>新增；成功返回新行 id。</summary>
        public bool TryAdd(string name, string kind, string baseUrl, string apiKey, string model, bool enabled,
            out string id, out string error)
        {
            id = "";
            if (!_store.TryAdd(Build(name, kind, baseUrl, apiKey, model, enabled), out id, out error)) return false;
            SelectedId = id;
            Refresh();
            return true;
        }

        /// <summary>整体改；成功保持选中。</summary>
        public bool TryUpdate(string id, string name, string kind, string baseUrl, string apiKey, string model, bool enabled,
            out string error)
        {
            if (!_store.TryUpdate(id, Build(name, kind, baseUrl, apiKey, model, enabled), out error)) return false;
            Refresh();
            return true;
        }

        /// <summary>删除；返回是否删到。</summary>
        public bool TryDelete(string id)
        {
            if (!_store.Delete(id)) return false;
            if (SelectedId == id) SelectedId = "";
            Refresh();
            return true;
        }
    }
}
