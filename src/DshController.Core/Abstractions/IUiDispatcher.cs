// ============================================================================
//  IUiDispatcher — UI 线程投递抽象（重构 2.0 / P0）
//
//  Core 是纯逻辑层，不允许引用 WinUI（Microsoft.UI.Dispatching）。
//  后端事件仍需回到 UI 线程，因此把"投递"这一个动作抽成接口：
//    - App 侧用 DispatcherQueue 实现（DispatcherQueueUiDispatcher）；
//    - CLI / 自检 / 单元测试用 InlineUiDispatcher（当场执行）。
//  语义与旧 BackendManager.RunOnUi 完全一致：投递失败或无调度器时就地执行，
//  且回调自身的异常不得冒泡到调用线程（后端线程不能因 UI 回调崩掉）。
// ============================================================================

using System;

namespace DshController.Core.Abstractions
{
    /// <summary>把回调投递到 UI 线程（无 UI 环境下就地执行）。</summary>
    public interface IUiDispatcher
    {
        /// <summary>投递到 UI 线程执行；实现方须吞掉回调自身异常，不得抛回调用线程。</summary>
        void Post(Action action);
    }

    /// <summary>无 UI 环境（CLI / 自检 / 单测）：当场执行。</summary>
    public sealed class InlineUiDispatcher : IUiDispatcher
    {
        public static readonly InlineUiDispatcher Instance = new InlineUiDispatcher();

        public void Post(Action action)
        {
            if (action == null) return;
            try { action(); }
            catch { /* 理由: 与 UI 投递语义一致——回调异常不得影响后端线程 */ }
        }
    }
}
