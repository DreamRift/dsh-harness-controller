// ============================================================================
//  L10n — 轻量界面语言切换（test-usage-stats v1.1.0 测试版）
//
//  覆盖范围：主窗口框架（侧边栏/标题栏/控制台坞/页脚）+ 用量统计页全量文案。
//  其余页面（实例面板/插件市场/插件管理/应用设置）暂为中文，后续按需迁移。
//
//  用法：L10n.T("中文", "English")；当前语言由 AppSettings.Language 持久化
//  （zh = 中文默认，en = English），切换后立即生效（代码后置直接改元素）。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;

namespace DshController.Core
{
    public static class L10n
    {
        /// <summary>当前语言：zh（默认）| en。</summary>
        public static string Current { get; private set; } = "zh";

        public static bool IsEn => Current == "en";

        /// <summary>从设置加载语言（非法值回退 zh）。</summary>
        public static void Load(string language)
        {
            Current = string.Equals(language, "en", StringComparison.OrdinalIgnoreCase) ? "en" : "zh";
        }

        /// <summary>按当前语言取文案。</summary>
        public static string T(string zh, string en) => IsEn ? en : zh;

        /// <summary>大数紧凑格式：zh 用 亿/万，en 用 B/M/K。</summary>
        public static string FmtTokens(long v)
        {
            if (IsEn)
            {
                double a = Math.Abs((double)v);
                if (a >= 1e9) return ((double)v / 1e9).ToString("0.##") + "B";
                if (a >= 1e6) return ((double)v / 1e6).ToString("0.##") + "M";
                if (a >= 1e3) return ((double)v / 1e3).ToString("0.##") + "K";
                return v.ToString("N0", CultureInfo.InvariantCulture);
            }
            double az = Math.Abs((double)v);
            if (az >= 1e8) return ((double)v / 1e8).ToString("0.##") + "亿";
            if (az >= 1e4) return ((double)v / 1e4).ToString("0.##") + "万";
            return v.ToString("N0");
        }
    }
}
