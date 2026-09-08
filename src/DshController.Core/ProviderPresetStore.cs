// ============================================================================
//  ProviderPresetStore — 模型供应商预设全局台账（改版·API 预设页 / dsh 模型页适配）
//
//  · 定稿：预设 = 全局存储（非实例级）；同步写入实例 HOME 配置由 Mapper/Plan/Writer 完成；
//  · 数据模型含校验：名称必填且 ≤40、类别（=API 协议）必填、BaseUrl 填了须 http(s)://、
//    Provider ID 路由形合法且全局唯一（dsh customRouteTaken）；
//  · 模型目录：Models 数组（id/name/contextWindow/maxTokens），对齐实例侧 providers 形状；
//    DefaultModel 为兼容字段（=Models[0].Id，旧档案载入时反向播种）；
//  · ProviderId 为稳定路由（dsh 语义：创建时选定后不可改，改名不影响同步键）；
//    旧档案载入时回填 = ProviderKey(Kind, Name)，与既有同步键保持一致；
//  · 落盘：路径单源 AppPaths.ProviderPresetsFile；读写经 JsonStore（原子写 +
//    损坏按空台账兜底）；构造可传 path 供离线测试重定向。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using DshController.Core.Storage;

namespace DshController.Core
{
    /// <summary>模型目录一行（形状对齐实例侧 providers.*.models[]；容量 null=继承默认；
    /// 模态支持三态：null=未知（同步省略该模态）、false=明确不支持、true=支持）。</summary>
    public sealed class PresetModel
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public long? ContextWindow { get; set; }
        public long? MaxTokens { get; set; }
        public bool? SupportImage { get; set; }
        public bool? SupportVideo { get; set; }
        public bool? SupportAudio { get; set; }
    }

    /// <summary>一条供应商预设（全局层形状；字段语义对齐 dsh 自定义提供方 profile）。</summary>
    public sealed class ProviderPreset
    {
        /// <summary>持久键（App 生成短 Guid；全局唯一）。</summary>
        public string Id { get; set; } = "";

        /// <summary>Provider ID 路由（稳定标识，创建后不变；同步键与凭据名由它派生）。</summary>
        public string ProviderId { get; set; } = "";

        /// <summary>显示名（必填，trim 后 1..40）。</summary>
        public string Name { get; set; } = "";

        /// <summary>API 协议（必填，取值见 ProviderProtocols.All；兼容旧"类别"自由文本）。</summary>
        public string Kind { get; set; } = "";

        /// <summary>端点；可选，填了须以 http:// 或 https:// 开头。</summary>
        public string BaseUrl { get; set; } = "";

        /// <summary>密钥；可选（落盘经 JsonStore，同步时实例侧只写 apiKeyEnv 名）。</summary>
        public string ApiKey { get; set; } = "";

        /// <summary>默认模型名（兼容字段 = Models[0].Id；旧档案载入时据此播种 Models）。</summary>
        public string DefaultModel { get; set; } = "";

        /// <summary>模型目录（对齐 dsh：空目录=用适配器默认模型，条目即替换默认目录）。</summary>
        public List<PresetModel> Models { get; set; } = new List<PresetModel>();

        public bool Enabled { get; set; } = true;

        public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

        /// <summary>内置官方提供方的稳定路由（对齐 dsh：官方 DeepSeek 路由常驻目录）。</summary>
        public const string BuiltinRoute = "deepseek-official";

        /// <summary>内置官方提供方的稳定台账 Id（旧播种文件 Id 为空时由 Normalize 回填）。</summary>
        public const string BuiltinId = "builtin";

        /// <summary>是否内置官方提供方（判定看路由，不随改名失效）。</summary>
        public bool IsBuiltin
        {
            get { return string.Equals(ProviderId, BuiltinRoute, StringComparison.OrdinalIgnoreCase); }
        }

        /// <summary>内置"DeepSeek 官方"出厂默认（对齐 dsh-llm-deepseek 适配器预留信息：
        /// 官方端点 + v4 模型目录 1M 上下文/384K 输出，per-model 输出上限取官方
        /// pi-ai 目录数据；可编辑；删除后由 Load 重新播种恢复默认）。</summary>
        public static ProviderPreset CreateBuiltinDefault()
        {
            return new ProviderPreset
            {
                Id = BuiltinId,
                ProviderId = BuiltinRoute,
                Name = "DeepSeek 官方",
                Kind = "openai-completions",
                BaseUrl = "https://api.deepseek.com",
                ApiKey = "",
                Models =
                {
                    new PresetModel { Id = "deepseek-v4-flash", Name = "DeepSeek-V4-Flash", ContextWindow = 1000000, MaxTokens = 384000 },
                    new PresetModel { Id = "deepseek-v4-pro", Name = "DeepSeek-V4-Pro", ContextWindow = 1000000, MaxTokens = 384000 },
                    new PresetModel { Id = "deepseek-v4-flash-vision-exp", Name = "DeepSeek-V4-Flash-Vision-Exp", ContextWindow = 1000000, MaxTokens = 384000, SupportImage = true }
                },
                Enabled = true
            };
        }

        /// <summary>校验规则（纯函数，可离线单测）：通过返回 true。</summary>
        public static bool Validate(ProviderPreset p, out string error)
        {
            if (p == null) { error = "预设不能为空"; return false; }
            string name = (p.Name ?? "").Trim();
            if (name.Length == 0) { error = "预设名不能为空"; return false; }
            if (name.Length > 40) { error = "预设名过长（最多 40 字）"; return false; }
            if (string.IsNullOrWhiteSpace(p.Kind)) { error = "供应商类别不能为空"; return false; }
            string url = (p.BaseUrl ?? "").Trim();
            if (url.Length > 0 && !IsHttpUrl(url)) { error = "BaseUrl 须以 http:// 或 https:// 开头"; return false; }
            if (p.ProviderId.Length > 0 && !ProviderPresetRules.IsValidRoute(p.ProviderId))
            { error = PresetCopy.RouteInvalid; return false; }
            error = "";
            return true;
        }

        private static bool IsHttpUrl(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri uri)) return false;
            return uri.Scheme == "http" || uri.Scheme == "https";
        }

        /// <summary>归一（载入/写入前）：字段 trim、旧档案播种（无路由回填 slug、
        /// DefaultModel→Models、Models[0]→DefaultModel、内置缺 Id 回填），幂等。</summary>
        internal static void Normalize(ProviderPreset p)
        {
            if (p == null) return;
            p.Name = (p.Name ?? "").Trim();
            p.Kind = (p.Kind ?? "").Trim();
            p.BaseUrl = (p.BaseUrl ?? "").Trim();
            p.ApiKey = p.ApiKey ?? "";
            p.DefaultModel = (p.DefaultModel ?? "").Trim();
            p.ProviderId = (p.ProviderId ?? "").Trim().ToLowerInvariant();
            if (p.ProviderId.Length == 0) p.ProviderId = ProviderConfigMapper.ProviderKey(p.Kind, p.Name);
            if (p.IsBuiltin && p.Id.Length == 0) p.Id = BuiltinId;
            if (p.Models == null) p.Models = new List<PresetModel>();
            p.Models.RemoveAll(m => m == null);
            if (p.Models.Count == 0 && p.DefaultModel.Length > 0)
                p.Models.Add(new PresetModel { Id = p.DefaultModel, Name = p.Name });
            if (p.Models.Count > 0) p.DefaultModel = (p.Models[0].Id ?? "").Trim();
        }
    }

    /// <summary>预设台账：增删改查 + 校验，每次变更即落盘（JsonStore 原子写）。</summary>
    public sealed class ProviderPresetStore
    {
        private readonly string _overridePath;   // null = 每次实时解析 AppPaths（可测重定向）
        private List<ProviderPreset> _presets = new List<ProviderPreset>();
        private bool _loaded;

        public ProviderPresetStore(string path = null)
        {
            _overridePath = path;
        }

        private string FilePath { get { return string.IsNullOrEmpty(_overridePath) ? AppPaths.ProviderPresetsFile : _overridePath; } }

        /// <summary>从盘上载入（幂等；损坏/缺失=空台账；旧档案经 Normalize 播种升级；
        /// 官方 DeepSeek 提供方缺失即播种，保证默认常驻）。</summary>
        public void Load()
        {
            List<ProviderPreset> doc = JsonStore.Read<List<ProviderPreset>>(FilePath, () => new List<ProviderPreset>());
            _presets = doc ?? new List<ProviderPreset>();
            foreach (ProviderPreset p in _presets) ProviderPreset.Normalize(p);
            if (!_presets.Any(p => p.IsBuiltin))
            {
                ProviderPreset builtin = ProviderPreset.CreateBuiltinDefault();
                ProviderPreset.Normalize(builtin);
                _presets.Add(builtin);
                Save();
            }
            _loaded = true;
        }

        private void EnsureLoaded() { if (!_loaded) Load(); }

        /// <summary>全部预设（内置官方置顶，其余新到旧）。</summary>
        public IReadOnlyList<ProviderPreset> All()
        {
            EnsureLoaded();
            List<ProviderPreset> rest = _presets
                .Where(p => !p.IsBuiltin)
                .OrderByDescending(p => p.UpdatedAtUtc).ToList();
            List<ProviderPreset> builtin = _presets.Where(p => p.IsBuiltin).ToList();
            builtin.AddRange(rest);
            return builtin;
        }

        /// <summary>按 Id 取（大小写不敏感；不存在返回 null）。</summary>
        public ProviderPreset Get(string id)
        {
            EnsureLoaded();
            return _presets.FirstOrDefault(p => string.Equals(p.Id, id ?? "", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>新增（自动生成 Id；校验失败或路由已被占用返回 false + 原因）。</summary>
        public bool TryAdd(ProviderPreset preset, out string id, out string error)
        {
            EnsureLoaded();
            id = "";
            if (preset == null) { error = "预设不能为空"; return false; }
            ProviderPreset.Normalize(preset);
            if (!ProviderPreset.Validate(preset, out error)) return false;
            string conflict = RouteConflict(preset.ProviderId, null);
            if (conflict != null) { error = conflict; return false; }
            id = Guid.NewGuid().ToString("N").Substring(0, 12);
            preset.Id = id;
            preset.UpdatedAtUtc = DateTime.UtcNow;
            _presets.Add(preset);
            Save();
            return true;
        }

        /// <summary>整体替换指定 Id 的可编辑字段（Id/ProviderId/UpdatedAtUtc 由台账维护——
        /// 路由创建后不可改，对齐 dsh；校验失败不改动）。</summary>
        public bool TryUpdate(string id, ProviderPreset patch, out string error)
        {
            EnsureLoaded();
            ProviderPreset target = Get(id);
            if (target == null) { error = "预设不存在"; return false; }
            if (patch == null) { error = "预设不能为空"; return false; }
            ProviderPreset.Normalize(patch);
            if (!ProviderPreset.Validate(patch, out error)) return false;
            string conflict = RouteConflict(target.ProviderId, id);
            if (conflict != null) { error = conflict; return false; }
            target.Name = patch.Name;
            target.Kind = patch.Kind;
            target.BaseUrl = patch.BaseUrl;
            target.ApiKey = patch.ApiKey;
            target.DefaultModel = patch.DefaultModel;
            target.Models = patch.Models;
            target.Enabled = patch.Enabled;
            target.UpdatedAtUtc = DateTime.UtcNow;
            Save();
            return true;
        }

        /// <summary>路由占用检查（大小写不敏感；excludeId 非空=更新时排除自身）。null=无冲突。</summary>
        private string RouteConflict(string route, string excludeId)
        {
            foreach (ProviderPreset p in _presets)
            {
                if (excludeId != null && string.Equals(p.Id, excludeId, StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(p.ProviderId, route ?? "", StringComparison.OrdinalIgnoreCase))
                    return PresetCopy.RouteTaken;
            }
            return null;
        }

        /// <summary>删除；不存在或为内置官方提供方（默认常驻，不可删）返回 false。</summary>
        public bool Delete(string id)
        {
            EnsureLoaded();
            ProviderPreset target = Get(id);
            if (target == null || target.IsBuiltin) return false;
            int before = _presets.Count;
            _presets.RemoveAll(p => string.Equals(p.Id, id ?? "", StringComparison.OrdinalIgnoreCase));
            if (_presets.Count == before) return false;
            Save();
            return true;
        }

        /// <summary>启动注入用的凭据环境变量对（纯函数）：Enabled 且填了密钥的预设 →
        /// (DSH_PRESET_* env 名, 密钥值)。同步只往实例写 env 名，值由启动器在拉起
        /// 子进程时注入——密钥不落 settings.yaml，也不进实例配置文件。</summary>
        public List<(string Name, string Value)> CredentialEnvPairs()
        {
            var pairs = new List<(string, string)>();
            foreach (ProviderPreset p in All())
            {
                string key = (p.ApiKey ?? "").Trim();
                if (!p.Enabled || key.Length == 0) continue;
                pairs.Add((ProviderConfigMapper.ApiKeyEnvName(p.ProviderId), key));
            }
            return pairs;
        }

        private void Save()
        {
            try { JsonStore.Write(FilePath, _presets); }
            catch { /* 理由: 预设写入失败不影响主流程，下次启动回退到上次保存的状态 */ }
        }
    }
}
