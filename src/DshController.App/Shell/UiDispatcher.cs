// ============================================================================
//  DispatcherQueueUiDispatcher — IUiDispatcher 的 WinUI 实现（重构 2.0 / P0）
//
//  Core 只认 IUiDispatcher；本类是 App 侧唯一把它接到 DispatcherQueue 的地方。
//  TryEnqueue 在队列已关闭（窗口销毁）时返回 false 而非抛异常，正是我们要的语义。
// ============================================================================

using System;
using DshController.Core.Abstractions;
using Microsoft.UI.Dispatching;

namespace DshController
{
    public sealed class DispatcherQueueUiDispatcher : IUiDispatcher
    {
        private readonly DispatcherQueue _queue;

        public DispatcherQueueUiDispatcher(DispatcherQueue queue) { _queue = queue; }

        /// <summary>取当前线程的调度器；不在 UI 线程时返回就地执行的兜底实现。</summary>
        public static IUiDispatcher ForCurrentThread()
        {
            DispatcherQueue q = DispatcherQueue.GetForCurrentThread();
            return q == null ? (IUiDispatcher)InlineUiDispatcher.Instance : new DispatcherQueueUiDispatcher(q);
        }

        public void Post(Action action)
        {
            if (action == null) return;
            if (_queue == null) { InlineUiDispatcher.Instance.Post(action); return; }
            try
            {
                _queue.TryEnqueue(() =>
                {
                    try { action(); }
                    catch { /* 理由: UI 回调异常不得冒泡回后端线程，全局钩子另有兜底 */ }
                });
            }
            catch { /* 理由: 队列已随窗口销毁时 TryEnqueue 可能抛，等价于"没人再关心这个事件" */ }
        }
    }
}
