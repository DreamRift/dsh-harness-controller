// ============================================================================
//  UpgradeIgnoreStore — 「忽略本次升级」持久台账（改版·插件升级治理）
//
//  语义（定稿）：忽略的是"到某个版本为止"——记录 (实例, 包, 被忽略目标版本)；
//    · 市场版本 ≤ 被忽略版本 → 抑制提示（IsSuppressed=true）；
//    · 市场出了更高版本（> 被忽略版本）→ 判定自动失效，恢复上报；
//    · 再次忽略同键 → 覆盖为新目标。
//  落盘：路径单源 AppPaths.UpgradeIgnoresFile；读写经 JsonStore（原子写 +
//  损坏按空台账兜底）。构造可传 path 供离线测试重定向（默认实时取 AppPaths）。
// ============================================================================

using System;
using System.Collections.Generic;
using DshController.Core.Storage;

namespace DshController.Core
{
    /// <summary>一条忽略记录（JSON 持久化形状）。</summary>
    public sealed class UpgradeIgnoreEntry
    {
        public string InstanceId { get; set; } = "";
        public string Pkg { get; set; } = "";
        public string IgnoredVersion { get; set; } = "";
        public DateTime IgnoredAtUtc { get; set; }
    }

    public sealed class UpgradeIgnoreStore
    {
        private readonly string _overridePath;   // null = 每次实时解析 AppPaths（可测重定向）
        private List<UpgradeIgnoreEntry> _entries = new List<UpgradeIgnoreEntry>();
        private bool _loaded;

        public UpgradeIgnoreStore(string path = null)
        {
            _overridePath = path;
        }

        private string FilePath { get { return string.IsNullOrEmpty(_overridePath) ? AppPaths.UpgradeIgnoresFile : _overridePath; } }

        /// <summary>从盘上载入（幂等；损坏/缺失=空台账）。</summary>
        public void Load()
        {
            IgnoreFile doc = JsonStore.Read<IgnoreFile>(FilePath, () => new IgnoreFile());
            _entries = doc != null && doc.Entries != null ? doc.Entries : new List<UpgradeIgnoreEntry>();
            _loaded = true;
        }

        private void EnsureLoaded() { if (!_loaded) Load(); }

        /// <summary>记录/覆盖 (实例,包) 的忽略目标版本并落盘。false=参数不全。</summary>
        public bool SetIgnored(string instanceId, string pkg, string version)
        {
            if (string.IsNullOrWhiteSpace(instanceId) || string.IsNullOrWhiteSpace(pkg) ||
                string.IsNullOrWhiteSpace(version)) return false;
            EnsureLoaded();
            UpgradeIgnoreEntry hit = Find(instanceId, pkg);
            if (hit == null)
                _entries.Add(new UpgradeIgnoreEntry
                {
                    InstanceId = instanceId.Trim(),
                    Pkg = pkg.Trim(),
                    IgnoredVersion = version.Trim(),
                    IgnoredAtUtc = DateTime.UtcNow
                });
            else
            {
                hit.IgnoredVersion = version.Trim();
                hit.IgnoredAtUtc = DateTime.UtcNow;
            }
            return Flush();
        }

        /// <summary>撤销 (实例,包) 的忽略并落盘。</summary>
        public bool Clear(string instanceId, string pkg)
        {
            UpgradeIgnoreEntry hit = Find(instanceId, pkg);
            if (hit == null) return false;
            _entries.Remove(hit);
            return Flush();
        }

        /// <summary>
        /// 该市场版本是否被"忽略到版本"抑制：存在记录且 market ≤ 被忽略版本。
        /// 市场出了更高版本 → false（判定自动失效、恢复上报）。
        /// </summary>
        public bool IsSuppressed(string instanceId, string pkg, string marketVersion)
        {
            if (string.IsNullOrWhiteSpace(marketVersion)) return false;
            UpgradeIgnoreEntry hit = Find(instanceId, pkg);
            if (hit == null || string.IsNullOrWhiteSpace(hit.IgnoredVersion)) return false;
            return PluginCompat.CompareVersions(marketVersion, hit.IgnoredVersion) <= 0;
        }

        /// <summary>当前忽略到的版本（无记录=空串）；UI 展示"已忽略 x.y.z"用。</summary>
        public string IgnoredUpTo(string instanceId, string pkg)
        {
            UpgradeIgnoreEntry hit = Find(instanceId, pkg);
            return hit == null ? "" : hit.IgnoredVersion ?? "";
        }

        private UpgradeIgnoreEntry Find(string instanceId, string pkg)
        {
            EnsureLoaded();
            string i = (instanceId ?? "").Trim();
            string p = (pkg ?? "").Trim();
            if (i.Length == 0 || p.Length == 0) return null;
            for (int k = 0; k < _entries.Count; k++)
            {
                UpgradeIgnoreEntry e = _entries[k];
                if (e == null) continue;
                if (string.Equals(e.InstanceId, i, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(e.Pkg, p, StringComparison.OrdinalIgnoreCase)) return e;
            }
            return null;
        }

        private bool Flush()
        {
            return JsonStore.Write(FilePath, new IgnoreFile { Entries = new List<UpgradeIgnoreEntry>(_entries) });
        }

        /// <summary>JSON 根结构（向前兼容：缺字段取默认）。</summary>
        public sealed class IgnoreFile
        {
            public int Version { get; set; } = 1;
            public List<UpgradeIgnoreEntry> Entries { get; set; } = new List<UpgradeIgnoreEntry>();
        }
    }
}
