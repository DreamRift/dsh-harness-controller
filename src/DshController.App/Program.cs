// ============================================================================
//  Program — 自定义入口（GenerateProgramFile=false）
//
//  CLI 自检参数（--check 等）在 WinUI 引导之前处理：无窗口、零 XAML 开销；
//  其余情况正常启动 WinUI 3 GUI。全局异常钩子在 App 侧（见 App.xaml.cs）。
// ============================================================================

using System;
using DshController.CommandLine;
using DshController.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using WinRT;

namespace DshController
{
    public static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            int cliCode;
            try
            {
                if (Cli.TryRun(args, out cliCode)) return cliCode;
            }
            catch (Exception ex)
            {
                try { ErrorReporter.WriteCrash(ex, "cli"); }
                catch { /* 理由: 崩溃报告本身失败时无处可报，退出码 1 已传达失败 */ }
                return 1;
            }

            ComWrappersSupport.InitializeComWrappers();
            Application.Start(_ =>
            {
                var context = new DispatcherQueueSynchronizationContext(
                    DispatcherQueue.GetForCurrentThread());
                System.Threading.SynchronizationContext.SetSynchronizationContext(context);
                new App();
            });
            return Environment.ExitCode;
        }
    }
}
