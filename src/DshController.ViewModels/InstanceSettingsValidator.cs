// ============================================================================
//  InstanceSettingsValidator — 实例设置的校验与归一（重构 2.0 / P3）
//
//  原本长在 InstancePanel.TryReadSettings 里（直接读 TxtPort.Text 这类控件），
//  于是端口边界、主机空值、trusted-hosts 拆分、版本号规范化这些规则完全无法测试。
//  这里抽成纯函数：输入文本，输出"校验结果 + 归一化后的值"。
// ============================================================================

using System;
using System.Collections.Generic;
using DshController.Core;

namespace DshController.ViewModels
{
    public sealed class InstanceSettingsInput
    {
        public string Name { get; set; } = "";
        public string Host { get; set; } = "";
        public string Port { get; set; } = "";
        public string Workspace { get; set; } = "";
        public string Home { get; set; } = "";
        public string TrustedHosts { get; set; } = "";
        public string HarnessVersion { get; set; } = "";
        public bool IsWsl { get; set; }
        public string WslDistro { get; set; } = "";
        public string WslHome { get; set; } = "";
        public bool AutoOpenBrowser { get; set; } = true;
        public bool StopOnExit { get; set; } = true;
    }

    public sealed class InstanceSettingsResult
    {
        public bool Ok => Error.Length == 0;
        public string Error { get; set; } = "";

        /// <summary>校验通过时的归一化值（直接可写回 InstanceDef）。</summary>
        public string Name { get; set; } = "";
        public string Host { get; set; } = "";
        public int Port { get; set; }
        public string Workspace { get; set; } = "";
        public string Home { get; set; } = "";
        public List<string> TrustedHosts { get; set; } = new List<string>();
        public string HarnessVersion { get; set; } = "";
        public string WslDistro { get; set; } = "";
        public string WslHome { get; set; } = "";
    }

    public static class InstanceSettingsValidator
    {
        public const int MinPort = 1;
        public const int MaxPort = 65535;

        public static InstanceSettingsResult Validate(InstanceSettingsInput input)
        {
            var r = new InstanceSettingsResult();
            if (input == null) { r.Error = "没有可保存的设置。"; return r; }

            r.Name = (input.Name ?? "").Trim();
            if (r.Name.Length == 0) { r.Error = "实例名称不能为空。"; return r; }

            r.Host = (input.Host ?? "").Trim();
            if (r.Host.Length == 0) r.Host = "127.0.0.1";

            if (!int.TryParse((input.Port ?? "").Trim(), out int port) || port < MinPort || port > MaxPort)
            {
                r.Error = "端口必须是 " + MinPort + "–" + MaxPort + " 之间的整数。";
                return r;
            }
            r.Port = port;

            if (!HarnessVersionText.TryNormalize(input.HarnessVersion, out string version))
            {
                r.Error = "harness 版本格式不对，应形如 0.5.1 或 0.5.1-rc.2（留空 = 跟随当前环境）。";
                return r;
            }
            r.HarnessVersion = version;

            if (input.IsWsl)
            {
                r.WslDistro = (input.WslDistro ?? "").Trim();
                if (r.WslDistro.Length == 0) { r.Error = "WSL 实例必须指定发行版。"; return r; }
                r.WslHome = (input.WslHome ?? "").Trim();
            }

            r.Workspace = Config.SanitizePath((input.Workspace ?? "").Trim());
            r.Home = Config.SanitizePath((input.Home ?? "").Trim());
            r.TrustedHosts = SplitTrustedHosts(input.TrustedHosts);
            return r;
        }

        /// <summary>trusted-hosts 拆分：逗号/分号/空白/换行都算分隔符，去空去重保持顺序。</summary>
        public static List<string> SplitTrustedHosts(string text)
        {
            var list = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string part in (text ?? "").Split(new[] { ',', ';', ' ', '\t', '\r', '\n' },
                         StringSplitOptions.RemoveEmptyEntries))
            {
                string host = part.Trim();
                if (host.Length == 0 || !seen.Add(host)) continue;
                list.Add(host);
            }
            return list;
        }
    }

    /// <summary>版本号文本的规范化（包装 Core 的实现，便于视图模型层直接引用）。</summary>
    public static class HarnessVersionText
    {
        public static bool TryNormalize(string input, out string normalized)
        {
            return HarnessVersion.TryNormalizeVersion(input, out normalized);
        }
    }
}
