// ============================================================================
//  AppVersion — 应用版本单一事实来源（重构 2.0 / P0.4）
//
//  历史问题：版本号写死在三处（csproj <Version> / ErrorReporter.AppVersion /
//  build.ps1 的 zip 名），发版时必须同步改三处，漏一处就对不上。
//  现在：csproj <Version> 是唯一源——编译期生成 AssemblyInformationalVersion，
//  运行期由本类读取；build.ps1 也从 csproj 解析同一个值。
// ============================================================================

using System;
using System.Reflection;

namespace DshController.Core.Diagnostics
{
    public static class AppVersion
    {
        private static string _cached;

        /// <summary>入口程序集版本（如 "2.0.0"）；解析失败返回 "0.0.0"。</summary>
        public static string Current
        {
            get
            {
                if (_cached == null) _cached = Resolve();
                return _cached;
            }
        }

        private static string Resolve()
        {
            // 入口程序集 = DshController.exe；单测宿主下回退到 Core 自身，保证永不抛
            Assembly asm = Assembly.GetEntryAssembly() ?? typeof(AppVersion).Assembly;
            try
            {
                var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
                string v = info?.InformationalVersion;
                if (!string.IsNullOrWhiteSpace(v))
                {
                    // SourceLink 会追加 "+<commit>"，展示时去掉
                    int plus = v.IndexOf('+');
                    return (plus > 0 ? v.Substring(0, plus) : v).Trim();
                }
                Version av = asm.GetName().Version;
                if (av != null) return av.ToString(3);
            }
            catch
            {
                // 理由: 版本号仅用于展示与报告抬头，任何反射异常都不应影响功能
            }
            return "0.0.0";
        }
    }
}
