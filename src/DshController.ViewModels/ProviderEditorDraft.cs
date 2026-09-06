// ============================================================================
//  ProviderEditorDraft — 供应商编辑草稿（API 页照 dsh 模型页适配 / 详情区状态机）
//
//  · 语义对齐 dsh ProviderEditor + CustomProviderCard：
//    - 密钥只写不读：打开为空=保持已存；填了新值才替换（keyStored 占位语义）；
//    - Provider ID 仅创建时可选定（路由即身份，创建后不可改），校验/查重实时；
//    - 模型目录为草稿行（文本形态），容量 256K/1M 可解析，行内错误实时标注；
//    - SaveReady 即 dsh 的 ready 门：路由合法未占用 + 地址已填 + 目录非空 +
//      目录校验通过 + 密钥格式通过；HintText 指向下一个要补的字段；
//  · 纯状态对象（CommunityToolkit ObservableObject），不碰 UI 不碰网络；
//    探针发起由 App 层完成，结果经 ApplyFetched 回填（能拿到容量就自动填入）。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using DshController.Core;

namespace DshController.ViewModels
{
    /// <summary>模型目录草稿一行（文本形态；行内校验错误由草稿实时刷新；
    /// Multimodal 三态：null=未知、false=纯文本、true=图文，对应同步时的 input 字段）。</summary>
    public sealed partial class DraftModelRow : ObservableObject
    {
        [ObservableProperty] public partial string Id { get; set; } = "";
        [ObservableProperty] public partial string Name { get; set; } = "";
        [ObservableProperty] public partial string ContextWindowText { get; set; } = "";
        [ObservableProperty] public partial string MaxTokensText { get; set; } = "";
        [ObservableProperty] public partial bool? Multimodal { get; set; }
        [ObservableProperty] public partial string RowError { get; set; } = "";

        public event Action Changed;

        partial void OnIdChanged(string value) => Changed?.Invoke();
        partial void OnContextWindowTextChanged(string value) => Changed?.Invoke();
        partial void OnMaxTokensTextChanged(string value) => Changed?.Invoke();
        partial void OnMultimodalChanged(bool? value) => Changed?.Invoke();

        /// <summary>多模态状态文案（三态开关旁的说明）。</summary>
        public string MultimodalText => Multimodal.HasValue ? (Multimodal.Value ? "图文" : "文本") : "未知";
    }

    /// <summary>详情区一张编辑卡（新建=自定义提供方卡；编辑=既有预设卡）。</summary>
    public sealed partial class ProviderEditorDraft : ObservableObject
    {
        private readonly Func<string, bool> _routeTaken;   // 路由是否被其他预设占用
        private readonly string _originalApiKey = "";      // 编辑态已存密钥（空字段=保持）

        public bool IsNew { get; }
        public string PresetId { get; }
        public bool HasStoredKey { get; }

        [ObservableProperty] public partial string Route { get; set; } = "";
        [ObservableProperty] public partial string Name { get; set; } = "";
        [ObservableProperty] public partial string Protocol { get; set; } = "";
        [ObservableProperty] public partial string BaseUrl { get; set; } = "";
        [ObservableProperty] public partial string ApiKeyDraft { get; set; } = "";
        [ObservableProperty] public partial bool Enabled { get; set; } = true;

        [ObservableProperty] public partial bool SaveReady { get; set; }
        [ObservableProperty] public partial string HintText { get; set; } = "";
        [ObservableProperty] public partial string RouteError { get; set; } = "";
        [ObservableProperty] public partial string KeyMessage { get; set; } = "";
        [ObservableProperty] public partial string ModelsHint { get; set; } = "";
        [ObservableProperty] public partial string SaveError { get; set; } = "";

        [ObservableProperty] public partial bool FetchBusy { get; set; }
        [ObservableProperty] public partial bool FetchEnabled { get; set; }
        [ObservableProperty] public partial string FetchError { get; set; } = "";

        public ObservableCollection<DraftModelRow> Models { get; } = new ObservableCollection<DraftModelRow>();

        /// <summary>协议下拉取值（与 dsh pi-ai 适配器一致）。</summary>
        public IReadOnlyList<string> Protocols { get; } = ProviderProtocols.All;

        /// <summary>密钥占位（dsh keyStored / keyPlaceholderNative）。</summary>
        public string KeyPlaceholder => HasStoredKey ? "已配置——输入新值可替换" : "输入 API 密钥，或留空使用环境认证";

        /// <summary>探针按钮文案（busy 换"正在询问提供方…"）。</summary>
        public string FetchLabel => FetchBusy ? "正在询问提供方…" : "获取可用模型";

        /// <summary>提交按钮文案（新建卡=创建提供方，编辑卡=保存；对齐 dsh EditorFooter）。</summary>
        public string SaveLabel => IsNew ? "创建提供方" : "保存";

        partial void OnFetchBusyChanged(bool value)
        {
            OnPropertyChanged(nameof(FetchLabel));
            RefreshDerived();
        }

        /// <summary>表单当前是否输入了新密钥（探针按"表单所填"发问）。</summary>
        public bool KeyDirty => (ApiKeyDraft ?? "").Trim().Length > 0;

        /// <summary>编辑态已存密钥（探针在表单未输新值时按它发问；新建恒为空串）。</summary>
        public string StoredApiKey => _originalApiKey;

        private ProviderEditorDraft(bool isNew, string presetId, ProviderPreset src, Func<string, bool> routeTaken)
        {
            IsNew = isNew;
            PresetId = presetId ?? "";
            _routeTaken = routeTaken ?? (_ => false);
            if (src != null)
            {
                _originalApiKey = src.ApiKey ?? "";
                HasStoredKey = _originalApiKey.Length > 0;
                Route = src.ProviderId ?? "";
                Name = src.Name ?? "";
                Protocol = string.IsNullOrWhiteSpace(src.Kind)
                    ? (ProviderProtocols.All.Length > 0 ? ProviderProtocols.All[0] : "")
                    : src.Kind;
                BaseUrl = src.BaseUrl ?? "";
                Enabled = src.Enabled;
                foreach (PresetModel m in src.Models ?? new List<PresetModel>())
                {
                    Models.Add(new DraftModelRow
                    {
                        Id = m.Id ?? "",
                        Name = m.Name ?? "",
                        ContextWindowText = m.ContextWindow.HasValue ? ProviderPresetRules.FormatCapacity(m.ContextWindow.Value) : "",
                        MaxTokensText = m.MaxTokens.HasValue ? ProviderPresetRules.FormatCapacity(m.MaxTokens.Value) : "",
                        Multimodal = m.Multimodal
                    });
                }
            }
            else
            {
                Protocol = ProviderProtocols.All.Length > 0 ? ProviderProtocols.All[0] : "";
            }
            foreach (DraftModelRow row in Models) row.Changed += RefreshDerived;
            RefreshDerived();
        }

        public static ProviderEditorDraft ForCreate(Func<string, bool> routeTaken)
        {
            return new ProviderEditorDraft(true, null, null, routeTaken);
        }

        public static ProviderEditorDraft ForEdit(ProviderPreset src, Func<string, bool> routeTaken)
        {
            if (src == null) throw new ArgumentNullException(nameof(src));
            return new ProviderEditorDraft(false, src.Id, src, routeTaken);
        }

        partial void OnRouteChanged(string value) => RefreshDerived();
        partial void OnNameChanged(string value) => RefreshDerived();
        partial void OnBaseUrlChanged(string value) => RefreshDerived();
        partial void OnApiKeyDraftChanged(string value) => RefreshDerived();

        public void AddModelRow()
        {
            var row = new DraftModelRow();
            row.Changed += RefreshDerived;
            Models.Add(row);
            RefreshDerived();
        }

        public void RemoveModelRow(DraftModelRow row)
        {
            if (row == null) return;
            row.Changed -= RefreshDerived;
            Models.Remove(row);
            RefreshDerived();
        }

        /// <summary>刷新派生态（就绪门/提示/行错误）——对齐 dsh 卡片的实时判定。</summary>
        public void RefreshDerived()
        {
            string route = (Route ?? "").Trim();
            bool routeInvalid = route.Length > 0 && !ProviderPresetRules.IsValidRoute(route);
            bool routeTaken = route.Length > 0 && _routeTaken(route);
            // 路由仅报错（空=留白，提示语由视图静态展示）
            RouteError = IsNew
                ? (routeInvalid ? PresetCopy.RouteInvalid
                    : routeTaken ? PresetCopy.RouteTaken : "")
                : "";

            KeyMessage = ProviderPresetRules.ApiKeyFailure(ApiKeyDraft, IsNew) ?? "";

            // 行内错误（首个违规行）：ID 必填/唯一 + 容量可解析
            var seen = new HashSet<string>(StringComparer.Ordinal);
            string firstError = "";
            foreach (DraftModelRow row in Models)
            {
                string id = (row.Id ?? "").Trim();
                string err = "";
                if (id.Length == 0) err = PresetCopy.ModelIdRequired;
                else if (!seen.Add(id)) err = PresetCopy.ModelIdDuplicate;
                else if (!ProviderPresetRules.TryParseCapacity(row.ContextWindowText, out long? _))
                    err = PresetCopy.ModelContextInvalid;
                else if (!ProviderPresetRules.TryParseCapacity(row.MaxTokensText, out long? _))
                    err = PresetCopy.ModelMaxTokensInvalid;
                row.RowError = err;
                if (err.Length > 0 && firstError.Length == 0) firstError = err;
            }

            string baseUrl = (BaseUrl ?? "").Trim();
            FetchEnabled = baseUrl.Length > 0 && !FetchBusy;

            bool nameOk = (Name ?? "").Trim().Length > 0 && (Name ?? "").Trim().Length <= 40;
            bool modelsOk = Models.Count > 0 && firstError.Length == 0;
            SaveReady = nameOk && KeyMessage.Length == 0 && modelsOk
                && (IsNew
                    ? route.Length > 0 && !routeInvalid && !routeTaken && baseUrl.Length > 0
                    : true);

            HintText = IsNew && SaveReady ? ""
                : IsNew && baseUrl.Length == 0 ? PresetCopy.NeedsBaseUrl
                : IsNew && Models.Count == 0 ? PresetCopy.NeedsModels
                : "";
            ModelsHint = Models.Count == 0
                ? "模型选择器中将不显示任何模型；目录外 ID 仍可直接发送。"
                : firstError;
        }

        /// <summary>把草稿物化为预设（try-build）：容量文本在此解析；编辑态密钥空=保持已存。</summary>
        public bool TryBuild(out ProviderPreset preset, out string error)
        {
            preset = null;
            error = "";
            if (!SaveReady)
            {
                // 失败原因按用户可行动的顺序命名：路由 → 密钥 → 地址/模型门
                error = RouteError.Length > 0 ? RouteError
                    : KeyMessage.Length > 0 ? KeyMessage
                    : HintText.Length > 0 ? HintText
                    : "还有未填好的字段";
                return false;
            }
            preset = new ProviderPreset
            {
                ProviderId = IsNew ? (Route ?? "").Trim() : "",
                Name = (Name ?? "").Trim(),
                Kind = (Protocol ?? "").Trim(),
                BaseUrl = (BaseUrl ?? "").Trim(),
                ApiKey = KeyDirty ? (ApiKeyDraft ?? "").Trim() : (IsNew ? "" : _originalApiKey),
                Enabled = Enabled
            };
            foreach (DraftModelRow row in Models)
            {
                var m = new PresetModel { Id = (row.Id ?? "").Trim(), Name = (row.Name ?? "").Trim(), Multimodal = row.Multimodal };
                if (ProviderPresetRules.TryParseCapacity(row.ContextWindowText, out long? cw) && cw.HasValue)
                    m.ContextWindow = cw.Value;
                if (ProviderPresetRules.TryParseCapacity(row.MaxTokensText, out long? mt) && mt.HasValue)
                    m.MaxTokens = mt.Value;
                preset.Models.Add(m);
            }
            return true;
        }

        /// <summary>采纳探针候选（dsh adoptPicked：按 id 合并，已有行原样保留——
        /// 容量可读的新行自动填入上下文/最大输出）。</summary>
        public void ApplyFetched(IEnumerable<DiscoveredModel> picked)
        {
            if (picked == null) return;
            var byId = new Dictionary<string, DraftModelRow>(StringComparer.Ordinal);
            foreach (DraftModelRow row in Models) byId[(row.Id ?? "").Trim()] = row;
            foreach (DiscoveredModel c in picked)
            {
                if (c == null || byId.ContainsKey(c.Id ?? "")) continue;
                var row = new DraftModelRow
                {
                    Id = c.Id ?? "",
                    Name = c.Name ?? "",
                    ContextWindowText = c.ContextWindow.HasValue ? ProviderPresetRules.FormatCapacity(c.ContextWindow.Value) : "",
                    MaxTokensText = c.MaxTokens.HasValue ? ProviderPresetRules.FormatCapacity(c.MaxTokens.Value) : "",
                    Multimodal = c.Multimodal
                };
                row.Changed += RefreshDerived;
                Models.Add(row);
                byId[row.Id] = row;
            }
            RefreshDerived();
        }
    }
}
