// ============================================================================
//  PluginOpsService — 插件命令的统一编排（重构 2.0 / P3）
//
//  安装/升级/卸载此前在两个面板里各写一遍：跑命令 → 判失败 → 弹错误 → 让档案失效
//  → 提示重启。现在收敛到这里，两个面板只负责各自的确认文案与列表刷新。
// ============================================================================

using System;
using System.Threading.Tasks;
using DshController.Core;
using Microsoft.UI.Xaml.Controls;

namespace DshController
{
    public sealed class PluginOpsService
    {
        private readonly InstanceRegistry _registry;
        private readonly InstanceManager _instanceMgr;
        private readonly ArchiveHub _archive;
        private readonly DialogService _dialogs;
        private readonly Action<string> _log;

        public PluginOpsService(InstanceRegistry registry, InstanceManager instanceMgr, ArchiveHub archive,
            DialogService dialogs, Action<string> log)
        {
            _registry = registry;
            _instanceMgr = instanceMgr;
            _archive = archive;
            _dialogs = dialogs;
            _log = log;
        }

        /// <summary>执行官方插件命令；失败时弹出错误详情并返回 false。</summary>
        public async Task<bool> RunAsync(InstanceDef def, PluginOp op, string profile, string target)
        {
            if (def == null) return false;
            Config cfg = def.ToConfig(_registry.Settings);
            string verb = PluginOpText.Label(op);
            PluginOpResult result = def.IsWsl
                ? await PluginInstaller.RunWslAsync(cfg, op, profile, target, _log)
                : await PluginInstaller.RunWindowsAsync(cfg, op, profile, target, _log);

            if (result.Ok)
            {
                _archive?.OnPluginsChanged(def.Id);   // 真实状态变了：档案立即失效
                return true;
            }

            _log?.Invoke("[插件] " + verb + "失败" +
                         (result.Error.Trim().Length > 0 ? "：" + result.Error.Trim() : ""));
            await _dialogs.InfoAsync(verb + "失败。\n\n命令：" + result.Command + "\n\n" +
                                     (result.Error.Trim().Length > 0 ? result.Error.Trim() : "详见控制台输出。"),
                                     verb + "失败");
            return false;
        }

        /// <summary>命令成功后的重启提示：实例运行中 → 一键重启；未运行 → 下次启动生效。</summary>
        public async Task PromptRestartAsync(InstanceDef def, string verb, string target)
        {
            if (def == null) return;
            BackendManager mgr = _instanceMgr.For(def.Id);
            bool running = mgr.State == BackendState.Running || mgr.State == BackendState.Starting ||
                           mgr.State == BackendState.Restarting;

            var dialog = new ContentDialog
            {
                Title = verb + "完成",
                Content = running
                    ? "已" + verb + " " + target + "。\nbundle 插件需重启实例后生效，是否立即重启实例「" + def.Name + "」？"
                    : "已" + verb + " " + target + "。\n实例当前未运行，下次启动时生效。",
                PrimaryButtonText = running ? "立即重启" : "知道了",
                CloseButtonText = running ? "稍后" : "",
                DefaultButton = ContentDialogButton.Primary
            };
            ContentDialogResult r = await _dialogs.ShowAsync(dialog);
            if (!running || r != ContentDialogResult.Primary) return;

            _log?.Invoke("[插件] 重启实例 " + def.Name + " …");
            bool ok = await _instanceMgr.RestartAsync(def.Id);
            _log?.Invoke("[插件] 重启" + (ok ? "完成。" : "未完成，请到实例页查看状态。"));
            _archive?.OnInstanceStarted(def.Id);
        }
    }
}
