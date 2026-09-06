// ============================================================================
//  ProviderPresetsViewModel — API 页供应商预设（照 dsh 模型页逻辑适配：主从状态机）
//
//  · 数据只来自全局 ProviderPresetStore（AppPaths+JsonStore），页面零直读实例（铁律一）；
//  · 左栏行（ProviderPresetRow）：名称 + 路由/协议/模型数/启用态副行 + 密钥状态点
//    （对齐 dsh 行的 configured/missing 圆点，只有确认的密钥态可见）；
//  · 选中 → Editor 草稿（ProviderEditorDraft：dsh 编辑卡/新建卡语义，保存即写台账）；
//  · 探针：网络在 Core（ProviderModelProbe）由 App 层发起；本类只持有候选与勾选态，
//    采纳按 id 合并回草稿（容量可读即自动填入），对齐 dsh"应答只是候选，绝不直接写配置"；
//  · 弹窗由 App 层经 DialogService 呈现；本类不碰 UI。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using DshController.Core;

namespace DshController.ViewModels
{
    /// <summary>左栏一行（展示层形状；ApiKey 只给状态不给值）。</summary>
    public sealed class ProviderPresetRow
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Route { get; set; } = "";
        public string Kind { get; set; } = "";
        public string BaseUrl { get; set; } = "";
        public string DefaultModel { get; set; } = "";
        public bool Enabled { get; set; } = true;
        public bool HasKey { get; set; } = false;
        public int ModelCount { get; set; }
        /// <summary>内置官方提供方（不可删除；删除按钮对其禁用）。</summary>
        public bool IsBuiltin { get; set; }

        /// <summary>来源标签（对齐 dsh：官方路由无标签由徽标示别，自定义卡带"自定义"）。</summary>
        public string TagText { get { return IsBuiltin ? "官方" : "自定义"; } }

        /// <summary>密钥状态点（dsh：只透出确认的密钥态）。</summary>
        public string KeyStateText { get { return HasKey ? PresetCopy.KeyConfigured : PresetCopy.KeyMissing; } }
        public bool ConfiguredVisible { get { return HasKey; } }
        public bool MissingVisible { get { return !HasKey; } }

        /// <summary>副行小字：路由 · 协议 · 模型数 · 启用态。</summary>
        public string MetaText
        {
            get
            {
                List<string> parts = new List<string>();
                if (!string.IsNullOrEmpty(Route)) parts.Add(Route);
                if (!string.IsNullOrEmpty(Kind)) parts.Add(Kind);
                parts.Add(ModelCount + " 模型");
                parts.Add(Enabled ? "启用" : "停用");
                return string.Join(" · ", parts);
            }
        }
    }

    public partial class ProviderPresetsViewModel : ObservableObject
    {
        private readonly ProviderPresetStore _store;

        /// <summary>宿主用它刷新页头/左栏摘要（Rows.Count）。</summary>
        public event Action Changed;

        public ObservableCollection<ProviderPresetRow> Rows { get; } = new ObservableCollection<ProviderPresetRow>();

        /// <summary>当前选中行 Id（左栏选中事件转发）。</summary>
        public string SelectedId { get; private set; } = "";

        /// <summary>详情区编辑草稿；null=空态（未选中任何提供方）。</summary>
        [ObservableProperty] public partial ProviderEditorDraft Editor { get; set; }

        /// <summary>详情区派生可见态（x:Bind 用）。</summary>
        public bool IsEmpty { get { return Editor == null; } }
        public bool HasEditor { get { return Editor != null; } }
        public bool HasSelection { get { return SelectedId.Length > 0; } }
        /// <summary>选中行可否删除（内置官方提供方不可删，删除按钮禁用）。</summary>
        public bool CanDeleteSelected
        {
            get
            {
                ProviderPresetRow row = RowById(SelectedId);
                return row != null && !row.IsBuiltin;
            }
        }

        partial void OnEditorChanged(ProviderEditorDraft value)
        {
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(HasEditor));
            Changed?.Invoke();
        }

        private void NotifySelection()
        {
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(CanDeleteSelected));
            Changed?.Invoke();
        }

        /// <summary>探针候选与勾选态（App 层弹勾选窗读取/操作）。</summary>
        public IReadOnlyList<DiscoveredModel> FetchCandidates { get; private set; } = new List<DiscoveredModel>();
        public HashSet<string> PickedIds { get; } = new HashSet<string>();

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
                    Route = p.ProviderId,
                    Kind = p.Kind,
                    BaseUrl = p.BaseUrl,
                    DefaultModel = p.DefaultModel,
                    Enabled = p.Enabled,
                    HasKey = !string.IsNullOrEmpty(p.ApiKey),
                    ModelCount = p.Models?.Count ?? 0,
                    IsBuiltin = p.IsBuiltin
                });
            }
            if (SelectedId.Length > 0 && !Rows.Any(r => r.Id == SelectedId)) SelectedId = "";
            Changed?.Invoke();
        }

        /// <summary>选中一行并打开其编辑卡；返回是否变化。</summary>
        public bool Select(string id)
        {
            if (string.IsNullOrEmpty(id) || SelectedId == id) return false;
            if (!Rows.Any(r => r.Id == id)) return false;
            SelectedId = id;
            BeginEdit(id);
            NotifySelection();
            return true;
        }

        public ProviderPresetRow RowById(string id) => Rows.FirstOrDefault(r => r.Id == id);

        // ---- 编辑卡（详情区） ------------------------------------------------

        /// <summary>打开新建卡（dsh 自定义提供方卡：路由/协议/地址/密钥/模型目录）。</summary>
        public void BeginCreate()
        {
            SelectedId = "";
            Editor = ProviderEditorDraft.ForCreate(RouteTakenExcept(null));
            NotifySelection();
        }

        /// <summary>打开既有预设的编辑卡。</summary>
        public void BeginEdit(string presetId)
        {
            ProviderPreset p = _store.Get(presetId);
            if (p == null) { Editor = null; return; }
            Editor = ProviderEditorDraft.ForEdit(p, RouteTakenExcept(presetId));
        }

        /// <summary>关闭卡片并清选中（丢弃未保存草稿，对齐 dsh 取消语义）。</summary>
        public void CancelEditor()
        {
            Editor = null;
            SelectedId = "";
            NotifySelection();
        }

        /// <summary>保存当前草稿（新建→TryAdd；编辑→TryUpdate）；失败草稿保留并回显错误。</summary>
        public bool TrySaveEditor(out string error)
        {
            error = "";
            ProviderEditorDraft draft = Editor;
            if (draft == null) return false;
            if (!draft.TryBuild(out ProviderPreset preset, out error))
            {
                draft.SaveError = error;
                return false;
            }
            bool ok;
            if (draft.IsNew)
            {
                ok = _store.TryAdd(preset, out string newId, out error);
                if (ok) SelectedId = newId;
            }
            else
            {
                ok = _store.TryUpdate(draft.PresetId, preset, out error);
            }
            if (!ok)
            {
                draft.SaveError = error;
                Refresh();
                return false;
            }
            Editor = null;
            Refresh();
            NotifySelection();
            // 保存后以已存值重开编辑卡：主从布局下行保持选中、动作（同步/删除）仍可达
            BeginEdit(SelectedId);
            return true;
        }

        private Func<string, bool> RouteTakenExcept(string presetId)
        {
            return route => _store.All().Any(p =>
                !string.Equals(p.Id, presetId ?? "", StringComparison.OrdinalIgnoreCase)
                && string.Equals(p.ProviderId, route ?? "", StringComparison.OrdinalIgnoreCase));
        }

        // ---- 探针（获取可用模型） ---------------------------------------------

        /// <summary>探针目标 = 表单当前所填（dsh：含未保存的密钥；编辑态空密钥用已存值）。</summary>
        public bool TryGetProbeTarget(out string baseUrl, out string apiKey, out string blocked)
        {
            baseUrl = apiKey = blocked = "";
            ProviderEditorDraft draft = Editor;
            if (draft == null) return false;
            baseUrl = (draft.BaseUrl ?? "").Trim();
            if (baseUrl.Length == 0) { blocked = PresetCopy.FetchNeedsBaseUrl; return false; }
            apiKey = draft.KeyDirty ? (draft.ApiKeyDraft ?? "").Trim() : draft.StoredApiKey;
            return true;
        }

        /// <summary>装入候选并预勾选目录里还没有的 id（dsh：已知模型默认不重复勾）。</summary>
        public void SetCandidates(List<DiscoveredModel> models)
        {
            FetchCandidates = models ?? new List<DiscoveredModel>();
            PickedIds.Clear();
            var known = new HashSet<string>(StringComparer.Ordinal);
            if (Editor != null)
            {
                foreach (DraftModelRow r in Editor.Models) known.Add((r.Id ?? "").Trim());
            }
            foreach (DiscoveredModel m in FetchCandidates)
            {
                if (m != null && !known.Contains((m.Id ?? "").Trim())) PickedIds.Add(m.Id);
            }
        }

        public void TogglePick(string id)
        {
            if (string.IsNullOrEmpty(id)) return;
            if (!PickedIds.Remove(id)) PickedIds.Add(id);
        }

        public void SetAllPicked(bool all)
        {
            PickedIds.Clear();
            if (!all) return;
            foreach (DiscoveredModel m in FetchCandidates)
            {
                if (m != null) PickedIds.Add(m.Id);
            }
        }

        /// <summary>采纳勾选候选回草稿（按 id 合并，已有行保留；容量自动填入）。</summary>
        public bool AdoptPicked()
        {
            if (Editor == null || FetchCandidates.Count == 0) return false;
            List<DiscoveredModel> picked = FetchCandidates.Where(m => m != null && PickedIds.Contains(m.Id)).ToList();
            Editor.ApplyFetched(picked);
            FetchCandidates = new List<DiscoveredModel>();
            PickedIds.Clear();
            return true;
        }

        // ---- 兼容面（旧直加/直改入口；新 UI 走 Editor 草稿） -------------------

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
            if (Editor != null && Editor.PresetId == id) Editor = null;
            Refresh();
            NotifySelection();
            return true;
        }
    }
}
