// ============================================================================
//  App — 应用入口 + 全局异常兜底（触发点 6：崩溃也生成详细报告，需求 R3）
// ============================================================================

using System;
using System.Linq;
using System.Threading.Tasks;
using DshController.Core;
using Microsoft.UI.Xaml;

namespace DshController
{
    public partial class App : Application
    {
        public static MainWindow MainWindow { get; private set; }

        public App()
        {
            InitializeComponent();
        }

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            // ---- 全局异常钩子：任何未处理异常都留下崩溃报告（含环境上下文） ----
            UnhandledException += (s, e) =>
            {
                try
                {
                    string path = ErrorReporter.WriteCrash(e.Exception, "xaml", _registrySnapshot);
                    e.Handled = true;
                    if (MainWindow != null) MainWindow.NotifyCrash(path);
                }
                catch { /* 理由: 崩溃报告写入失败不得在处理器内再抛（避免递归触发），原异常交回系统策略处理 */ }
            };
            TaskScheduler.UnobservedTaskException += (s, e) =>
            {
                try { ErrorReporter.WriteCrash(e.Exception, "task", _registrySnapshot); } catch { /* 理由: 任务异常已由 SetObserved 固定，报告写入失败仅丢失该次报告，不影响稳定性 */ }
                e.SetObserved();
            };
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                try
                {
                    var ex = e.ExceptionObject as Exception ??
                             new Exception(e.ExceptionObject == null ? "(null)" : e.ExceptionObject.ToString());
                    ErrorReporter.WriteCrash(ex, "domain", _registrySnapshot);
                }
                catch { /* 理由: 域级未捕获异常进程即将终止，报告写入失败已无可挽回，仅尽力留下现场 */ }
            };

            InstanceRegistry registry = InstanceRegistry.Load();
            _registrySnapshot = registry.Instances.FirstOrDefault()?.ToConfig(registry.Settings) ?? new Config();
            MainWindow = new MainWindow(registry);
            MainWindow.Activate();
        }

        // 供崩溃报告标注用户自定义的报告目录（尽力而为的快照）
        private static Config _registrySnapshot;
    }
}
