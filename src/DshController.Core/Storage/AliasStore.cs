// ============================================================================
//  AliasStore — 实例别名持久台账（改版·改名入口）
//  · 格式：{ "archiveId": "别名", … }
//  · 落盘：路径单源 AppPaths.AliasesFile；读写经 JsonStore（原子写 + 损坏兜底）。
//  · 构造可传 path 供离线测试重定向（默认实时取 AppPaths）。
// ============================================================================

using System;
using System.Collections.Generic;
using DshController.Core.Storage;

namespace DshController.Core.Storage
{
    public sealed class AliasStore
    {
        private readonly string _overridePath;
        private Dictionary<string, string> _aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private bool _loaded;

        public AliasStore(string path = null)
        {
            _overridePath = path;
        }

        private string FilePath => string.IsNullOrEmpty(_overridePath) ? AppPaths.AliasesFile : _overridePath;

        /// <summary>从盘上载入（幂等；损坏/缺失=空台账）。</summary>
        public void Load()
        {
            var doc = JsonStore.Read<Dictionary<string, string>>(FilePath, () => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
            _aliases = doc ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _loaded = true;
        }

        private void EnsureLoaded() { if (!_loaded) Load(); }

        /// <summary>取别名（无别名返回空串）。</summary>
        public string Get(string archiveId)
        {
            EnsureLoaded();
            return _aliases.TryGetValue(archiveId ?? "", out string a) ? a ?? "" : "";
        }

        /// <summary>设置别名（空串=清除别名）。</summary>
        public void Set(string archiveId, string alias)
        {
            EnsureLoaded();
            if (string.IsNullOrEmpty(archiveId)) return;
            if (string.IsNullOrWhiteSpace(alias))
            {
                _aliases.Remove(archiveId);
            }
            else
            {
                _aliases[archiveId] = alias.Trim();
            }
            Save();
        }

        /// <summary>锁死所有键（用于全量覆盖）。</summary>
        public IReadOnlyDictionary<string, string> All() { EnsureLoaded(); return _aliases; }

        private void Save()
        {
            try { JsonStore.Write(FilePath, _aliases); }
            catch { /* 理由: 别名写入失败不影响主流程，下次启动回退到上次保存的状态 */ }
        }
    }
}
