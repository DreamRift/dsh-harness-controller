// ============================================================================
//  IClock — 时间抽象（重构 2.0 / P1）
//
//  档案的新鲜度判定完全依赖"现在几点"。若直接用 DateTime.UtcNow，
//  TTL 相关逻辑就只能靠 Thread.Sleep 测试（慢且不稳）。抽出接口后，
//  单测用 FakeClock 拨表，毫秒内覆盖"刚采集/将过期/已过期"三态。
// ============================================================================

using System;

namespace DshController.Core.Abstractions
{
    public interface IClock
    {
        DateTime UtcNow { get; }
    }

    public sealed class SystemClock : IClock
    {
        public static readonly SystemClock Instance = new SystemClock();
        public DateTime UtcNow => DateTime.UtcNow;
    }
}
