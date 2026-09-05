// ============================================================================
//  ProviderPresetStore — 模型供应商预设全局台账（改版·API 预设页）
//
//  · 定稿：预设 = 全局存储（非实例级），后续小类（供应商编辑页 / 同步）在
//    此之上做 UI 与"写入实例 HOME 配置"的格式对齐（对齐属同步小类，此处不碰）；
//  · 数据模型含校验：名称必填且 ≤40、类别必填、BaseUrl 填了须 http(s)://；
//  · 落盘：路径单源 AppPaths.ProviderPresetsFile；读写经 JsonStore（原子写 +
//    损坏按空台账兜底）；构造可传 path 供离线测试重定向。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using DshController.Core.Storage;

namespace DshController.Core
{
    /// <summary>一条供应商预设（全局层形状；与实例侧配置的字段对齐属后续同步小类）。</summary>
    public sealed class ProviderPreset
    {
        /// <summary>持久键（App 生成短 Guid；全局唯一）。</summary>
        public string Id { get; set; } = "";

        /// <summary>显示名（必填，trim 后 1..40）。</summary>
        public string Name { get; set; } = "";

        /// <summary>供应商类别（必填，如 deepseek / openai-compatible）。</summary>
        public string Kind { get; set; } = "";

        /// <summary>端点；可选，填了须以 http:// 或 https:// 开头。</summary>
        public string BaseUrl { get; set; } = "";

        /// <summary>密钥；可选（落盘经 JsonStore，供后续同步写入实例配置）。</summary>
        public string ApiKey { get; set; } = "";

        /// <summary>默认模型名；可选。</summary>
        public string DefaultModel { get; set; } = "";

        public bool Enabled { get; set; } = true;

        public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

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
            error = "";
            return true;
        }

        private static bool IsHttpUrl(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri uri)) return false;
            return uri.Scheme == "http" || uri.Scheme == "https";
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

        /// <summary>从盘上载入（幂等；损坏/缺失=空台账）。</summary>
        public void Load()
        {
            List<ProviderPreset> doc = JsonStore.Read<List<ProviderPreset>>(FilePath, () => new List<ProviderPreset>());
            _presets = doc ?? new List<ProviderPreset>();
            _loaded = true;
        }

        private void EnsureLoaded() { if (!_loaded) Load(); }

        /// <summary>全部预设（新到旧）。</summary>
        public IReadOnlyList<ProviderPreset> All()
        {
            EnsureLoaded();
            return _presets.OrderByDescending(p => p.UpdatedAtUtc).ToList();
        }

        /// <summary>按 Id 取（大小写不敏感；不存在返回 null）。</summary>
        public ProviderPreset Get(string id)
        {
            EnsureLoaded();
            return _presets.FirstOrDefault(p => string.Equals(p.Id, id ?? "", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>新增（自动生成 Id；校验失败返回 false + 原因）。</summary>
        public bool TryAdd(ProviderPreset preset, out string id, out string error)
        {
            EnsureLoaded();
            id = "";
            if (!ProviderPreset.Validate(preset, out error)) return false;
            id = Guid.NewGuid().ToString("N").Substring(0, 12);
            preset.Id = id;
            preset.UpdatedAtUtc = DateTime.UtcNow;
            _presets.Add(preset);
            Save();
            return true;
        }

        /// <summary>整体替换指定 Id 的可编辑字段（Id/UpdatedAtUtc 由台账维护）；校验失败不改动。</summary>
        public bool TryUpdate(string id, ProviderPreset patch, out string error)
        {
            EnsureLoaded();
            ProviderPreset target = Get(id);
            if (target == null) { error = "预设不存在"; return false; }
            if (!ProviderPreset.Validate(patch, out error)) return false;
            target.Name = (patch.Name ?? "").Trim();
            target.Kind = (patch.Kind ?? "").Trim();
            target.BaseUrl = (patch.BaseUrl ?? "").Trim();
            target.ApiKey = patch.ApiKey ?? "";
            target.DefaultModel = patch.DefaultModel ?? "";
            target.Enabled = patch.Enabled;
            target.UpdatedAtUtc = DateTime.UtcNow;
            Save();
            return true;
        }

        /// <summary>删除；不存在返回 false。</summary>
        public bool Delete(string id)
        {
            EnsureLoaded();
            int before = _presets.Count;
            _presets.RemoveAll(p => string.Equals(p.Id, id ?? "", StringComparison.OrdinalIgnoreCase));
            if (_presets.Count == before) return false;
            Save();
            return true;
        }

        private void Save()
        {
            try { JsonStore.Write(FilePath, _presets); }
            catch { /* 理由: 预设写入失败不影响主流程，下次启动回退到上次保存的状态 */ }
        }
    }
}
