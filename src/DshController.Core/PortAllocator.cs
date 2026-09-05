// ============================================================================
//  PortAllocator — 多实例端口分配（v0.3.0）
//
//  设计决策：
//    1. 默认探测函数直接复用 PortTools.ProbeAsync（127.0.0.1，1.2s 超时），
//       避免本类重复实现 TCP 探测。
//    2. 用户段固定在 3080..3099。preferred 超出范围时仍从 3080 开始，
//       保证多实例候选端口不会滑出约定段。
//    3. SuggestAsync 优先以调用方传入的 takenPorts 排除配置中已占用的端口，
//       再执行真实探测；返回 0 表示让 dsh 使用 OS 分配端口。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DshController.Core
{
    public sealed class PortAllocator
    {
        private const int MinPort = 3080;
        private const int MaxPort = 3099;

        /// <summary>端口探测函数：true = 端口被占用。</summary>
        private readonly Func<int, Task<bool>> _probe;

        public PortAllocator()
            : this(port => PortTools.ProbeAsync("127.0.0.1", port))
        {
        }

        /// <summary>允许测试或调用方注入探测函数，便于做无网络自检。</summary>
        public PortAllocator(Func<int, Task<bool>> probe)
        {
            _probe = probe ?? (port => PortTools.ProbeAsync("127.0.0.1", port));
        }

        /// <summary>端口是否未占用：不在 takenPorts 中，且真实探测为空闲。</summary>
        public async Task<bool> IsFreeAsync(int port, IEnumerable<int> takenPorts)
        {
            if (port < MinPort || port > MaxPort)
                return false;

            if (takenPorts != null && takenPorts.Contains(port))
                return false;

            return !await _probe(port).ConfigureAwait(false);
        }

        /// <summary>
        /// 从 preferred 起向上（含）在 3080..3099 内找第一个空闲端口。
        /// 找不到返回 0（=让 dsh 用 OS 分配端口）。
        /// </summary>
        public async Task<int> SuggestAsync(int preferred, IEnumerable<int> takenPorts)
        {
            foreach (int candidate in CandidateRange(preferred))
            {
                if (await IsFreeAsync(candidate, takenPorts).ConfigureAwait(false))
                    return candidate;
            }
            return 0;
        }

        /// <summary>
        /// 生成候选端口序列：优先从 preferred 到 MaxPort，再补 MinPort 到 preferred-1。
        /// preferred 超出 3080..3099 时按包含边界回落到 MinPort。
        /// </summary>
        private static IEnumerable<int> CandidateRange(int preferred)
        {
            int start = preferred;
            if (start < MinPort) start = MinPort;
            if (start > MaxPort) start = MinPort;

            for (int port = start; port <= MaxPort; port++)
                yield return port;

            for (int port = MinPort; port < start; port++)
                yield return port;
        }
    }
}
