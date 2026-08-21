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
                catch { }
            };
            TaskScheduler.UnobservedTaskException += (s, e) =>
            {
                try { ErrorReporter.WriteCrash(e.Exception, "task", _registrySnapshot); } catch { }
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
                catch { }
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
