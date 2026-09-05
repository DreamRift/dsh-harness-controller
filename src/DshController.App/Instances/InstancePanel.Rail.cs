// ============================================================================
//  InstancePanel — 左栏选中入口（改版·详情操作主区）
//  两实例页合并为一后，环境行入口退役：左栏行选中经本方法落到对应环境面板，
//  Windows/WSL 差异在此内部消化（非本环境实例返回 false，由调用方决定去向）。
// ============================================================================

using DshController.Core;

namespace DshController
{
    public sealed partial class InstancePanel
    {
        /// <summary>左栏选中：属于本环境则选中它（驱动全套既有链路），否则返回 false。</summary>
        public bool SelectFromRail(string id)
        {
            if (string.IsNullOrEmpty(id) || !_registry.TryGet(id, out InstanceDef def) || def.IsWsl != IsWslPanel)
                return false;
            SelectInstance(id);
            return true;
        }
    }
}
