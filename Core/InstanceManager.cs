// ============================================================================
//  InstanceManager — N 个 BackendManager 的集合与路由（v0.3.0）
//
//  职责：
//    - 按实例 id 惰性创建/持有 BackendManager（每实例一个状态机）；
//    - 对指定实例执行 Start/Stop/Restart（内部把 InstanceDef 转成 Config）；
//    - 实例 HOME 文件锁（<home>\.dsh-instance.lock 写 PID）：防止同一 HOME
//      被两个实例/两个入口同时拉起（锁内 PID 存活 → 拒绝二次启动）；
//    - 退出时按各实例 stopOnExit 清理本程序启动的后端。
//  空 home 的实例（如迁移出的 default，使用 ~/.dsh）不做锁（兼容旧行为）。
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;

namespace DshController.Core
{
    public sealed class InstanceManager : IDisposable
    {
        private readonly DispatcherQueue _dq;
        private readonly InstanceRegistry _registry;
        private readonly Dictionary<string, BackendManager> _managers =
            new Dictionary<string, BackendManager>(StringComparer.OrdinalIgnoreCase);
        private readonly object _lock = new object();

        public InstanceManager(DispatcherQueue dq, InstanceRegistry registry)
        {
            _dq = dq;
            _registry = registry;
        }

        public IReadOnlyDictionary<string, BackendManager> All
        {
            get
            {
                lock (_lock) return new Dictionary<string, BackendManager>(_managers);
            }
        }

        public InstanceRegistry Registry { get { return _registry; } }

        /// <summary>取（或惰性创建）指定实例的管理器。</summary>
        public BackendManager For(string id)
        {
            lock (_lock)
            {
                if (_managers.TryGetValue(id, out BackendManager m)) return m;
                m = new BackendManager(_dq);
                _managers[id] = m;
                return m;
            }
        }

        public Config ConfigFor(string id)
        {
            return _registry.Get(id).ToConfig(_registry.Settings);
        }

        // ==================== 对指定实例操作 ====================

        public async Task<bool> StartAsync(string id)
        {
            InstanceDef def = _registry.Get(id);
            if (IsLocked(def, out int lockedPid))
            {
                BackendManager mgr = For(id);
                mgr.LogLine("实例 HOME 已被占用（PID " + lockedPid +
                    "，锁文件 " + LockPath(def) + "），拒绝重复启动。");
                return false;
            }
            Config cfg = def.ToConfig(_registry.Settings);
            bool ok = await For(id).StartAsync(cfg).ConfigureAwait(false);
            if (ok && !string.IsNullOrEmpty(def.Home) && !def.IsWsl)
            {
                def.LastStartedAt = DateTime.UtcNow;
                WriteLock(def, For(id).ChildPid);
                _registry.Save();
            }
            return ok;
        }

        public async Task<bool> StopAsync(string id, bool killExternal)
        {
            InstanceDef def = _registry.Get(id);
            bool ok = await For(id).StopAsync(def.ToConfig(_registry.Settings), killExternal).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(def.Home) && !def.IsWsl) DeleteLock(def);
            return ok;
        }

        public async Task<bool> RestartAsync(string id)
        {
            InstanceDef def = _registry.Get(id);
            if (string.IsNullOrEmpty(def.Home) || def.IsWsl) DeleteLock(def);
            Config cfg = def.ToConfig(_registry.Settings);
            bool ok = await For(id).RestartAsync(cfg).ConfigureAwait(false);
            if (ok && !string.IsNullOrEmpty(def.Home) && !def.IsWsl)
            {
                def.LastStartedAt = DateTime.UtcNow;
                WriteLock(def, For(id).ChildPid);
                _registry.Save();
            }
            return ok;
        }

        // ==================== 实例 HOME 文件锁 ====================

        public bool IsLocked(string id, out int pid)
        {
            pid = 0;
            if (!_registry.TryGet(id, out InstanceDef def) || string.IsNullOrEmpty(def.Home)) return false;
            if (def.IsWsl) return false; // WSL 实例：锁由发行版内 pidfile 守护（Linux 路径在 Windows 侧无意义）
            return IsLocked(def, out pid);
        }

        private static bool IsLocked(InstanceDef def, out int pid)
        {
            pid = 0;
            try
            {
                string path = LockPath(def);
                if (!File.Exists(path)) return false;
                string s = File.ReadAllText(path).Trim();
                // 锁文件内容异常 → 保守视为锁定（提示用户手动检查）
                if (!int.TryParse(s, out pid) || pid <= 0) return true;
                return PortTools.IsAlive(pid);
            }
            catch { return true; }
        }

        private static string LockPath(InstanceDef def)
        {
            return Path.Combine(def.Home, ".dsh-instance.lock");
        }

        private static void WriteLock(InstanceDef def, int pid)
        {
            try
            {
                Directory.CreateDirectory(def.Home);
                File.WriteAllText(LockPath(def), pid.ToString());
            }
            catch { /* 锁写入失败不阻断启动（锁是防重复的保护，非必需） */ }
        }

        private static void DeleteLock(InstanceDef def)
        {
            try { if (File.Exists(LockPath(def))) File.Delete(LockPath(def)); } catch { }
        }

        // ==================== 退出清理 ====================

        /// <summary>逐实例执行 stopOnExit=true 且本程序启动的停止（串行）。</summary>
        public async Task StopAllOnExitAsync()
        {
            foreach (KeyValuePair<string, BackendManager> kv in All)
            {
                if (_registry.TryGet(kv.Key, out InstanceDef def) &&
                    def.StopOnExit && kv.Value.State == BackendState.Running && kv.Value.IsMine)
                {
                    try
                    {
                        await kv.Value.StopAsync(def.ToConfig(_registry.Settings), killExternal: false)
                            .ConfigureAwait(false);
                    }
                    catch { }
                }
            }
        }

        public void DisposeAll()
        {
            foreach (BackendManager m in All.Values)
            {
                try { m.Dispose(); } catch { }
            }
            lock (_lock) _managers.Clear();
        }

        public void Dispose() { DisposeAll(); }
    }
}
