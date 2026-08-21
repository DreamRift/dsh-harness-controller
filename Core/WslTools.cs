// ============================================================================
//  WslTools — WSL2 互操作层（v0.4.0 从 DshWslCtrl 验证版移植）
//
//  封装 wsl.exe 调用、UTF-16LE 输出解码、发行版管理、路径转换、
//  发行版内命令执行与文件上传（经 /mnt/c 拷贝，规避引号转义与 UNC 时序问题）。
//  纯 .NET 标准库，无 WinUI 依赖。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DshController.Core
{
    /// <summary>一次 wsl.exe 调用的结果。</summary>
    public sealed class WslResult
    {
        public int ExitCode { get; init; }
        public string Output { get; init; } = "";
        public string Error { get; init; } = "";
        public bool TimedOut { get; init; }
        public bool Ok => ExitCode == 0 && !TimedOut;

        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.Append("exit=").Append(ExitCode);
            if (TimedOut) sb.Append(" (超时)");
            if (Output.Length > 0) sb.AppendLine().Append(Output.TrimEnd());
            if (Error.Length > 0) sb.AppendLine("[stderr] ").Append(Error.TrimEnd());
            return sb.ToString();
        }
    }

    public static class WslTools
    {
        private static bool? _installedCache;

        // ---------------- 基础执行 ----------------

        /// <summary>
        /// 执行 wsl.exe 并收集输出（原始字节读取 + 编码探测解码）。
        /// 信息类命令在旧版 wsl.exe 上输出 UTF-16LE，新版在 WSL_UTF8=1 下输出 UTF-8，
        /// 发行版内命令输出恒为 UTF-8。
        /// </summary>
        public static async Task<WslResult> ExecAsync(IReadOnlyList<string> args, int timeoutMs = 30000,
            IDictionary<string, string> env = null)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "wsl.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            if (env != null)
                foreach (var kv in env)
                    psi.EnvironmentVariables[kv.Key] = kv.Value;
            psi.EnvironmentVariables["WSL_UTF8"] = "1"; // 新版生效；stub 忽略，靠字节探测兜底

            try
            {
                using var proc = new Process { StartInfo = psi };
                using var cts = new CancellationTokenSource(timeoutMs);
                using var stdoutMs = new MemoryStream();
                using var stderrMs = new MemoryStream();
                proc.Start();
                var tOut = proc.StandardOutput.BaseStream.CopyToAsync(stdoutMs, cts.Token);
                var tErr = proc.StandardError.BaseStream.CopyToAsync(stderrMs, cts.Token);
                try
                {
                    await proc.WaitForExitAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    TryKill(proc);
                    return new WslResult
                    {
                        ExitCode = -1,
                        TimedOut = true,
                        Output = Decode(stdoutMs.ToArray()),
                        Error = Decode(stderrMs.ToArray())
                    };
                }
                try { await Task.WhenAll(tOut, tErr).WaitAsync(TimeSpan.FromSeconds(5)); }
                catch { }

                return new WslResult
                {
                    ExitCode = proc.ExitCode,
                    Output = Decode(stdoutMs.ToArray()),
                    Error = Decode(stderrMs.ToArray())
                };
            }
            catch (Exception ex)
            {
                return new WslResult { ExitCode = -1, Error = ex.Message };
            }
        }

        private static void TryKill(Process proc)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
        }

        // ---------------- 编码与文本处理 ----------------

        /// <summary>解码 wsl.exe 输出：优先严格 UTF-8；失败或含 NUL 时按 UTF-16LE 解码。</summary>
        public static string Decode(byte[] raw)
        {
            if (raw == null || raw.Length == 0) return "";
            try
            {
                var strict = new UTF8Encoding(false, throwOnInvalidBytes: true);
                var s = strict.GetString(raw);
                if (!s.Contains('\0')) return s;
            }
            catch (DecoderFallbackException) { }

            if (raw.Length % 2 == 0)
            {
                int oddNulls = 0;
                for (int i = 1; i < raw.Length; i += 2)
                    if (raw[i] == 0) oddNulls++;
                if (oddNulls >= Math.Max(1, raw.Length / 16))
                    return Encoding.Unicode.GetString(raw);
            }
            return Encoding.UTF8.GetString(raw);
        }

        /// <summary>按行拆分（清理 BOM、\0、空白行）。</summary>
        public static List<string> SplitLines(string s)
        {
            var result = new List<string>();
            foreach (var rawLine in (s ?? "").Replace("\0", "").Split('\n'))
            {
                var line = rawLine.Trim('\r', ' ', '\t', '\uFEFF');
                if (line.Length > 0) result.Add(line);
            }
            return result;
        }

        /// <summary>bash 单引号安全包装：it's → 'it'\''s'</summary>
        public static string Shq(string s) =>
            s.Contains('\'') ? "'" + s.Replace("'", "'\\''") + "'" : "'" + s + "'";

        // ---------------- WSL 安装与发行版管理 ----------------

        public static async Task<bool> IsInstalledAsync()
        {
            if (_installedCache is bool cached) return cached;
            var r = await ExecAsync(new[] { "--status" }, 20000);
            bool installed = r.ExitCode == 0
                && !ContainsNotInstalled(r.Output) && !ContainsNotInstalled(r.Error);
            _installedCache = installed;
            return installed;
        }

        private static bool ContainsNotInstalled(string text) =>
            text.Contains("未安装") || text.Contains("没有安装")
            || text.Contains("not installed", StringComparison.OrdinalIgnoreCase)
            || text.Contains("install the windows subsystem", StringComparison.OrdinalIgnoreCase);

        /// <summary>已安装发行版列表。</summary>
        public static async Task<List<string>> ListDistrosAsync()
        {
            if (!await IsInstalledAsync()) return new List<string>();
            var r = await ExecAsync(new[] { "--list", "--quiet" }, 30000);
            var names = new List<string>();
            if (r.Ok)
                foreach (var line in SplitLines(r.Output))
                    if (LooksLikeDistroName(line)) names.Add(line.TrimStart('*'));
            if (names.Count == 0)
            {
                var full = await ExecAsync(new[] { "--list", "--verbose" }, 30000);
                foreach (var line in SplitLines(full.Output))
                {
                    var tokens = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (tokens.Length >= 2 && LooksLikeDistroName(tokens[0])
                        && (tokens.Any(t => t == "Running" || t == "Stopped" || t == "已停止" || t == "运行中")))
                        names.Add(tokens[0].TrimStart('*'));
                }
            }
            return names;
        }

        /// <summary>当前正在运行的发行版列表。</summary>
        public static async Task<List<string>> ListRunningDistrosAsync()
        {
            if (!await IsInstalledAsync()) return new List<string>();
            var r = await ExecAsync(new[] { "--list", "--running", "--quiet" }, 30000);
            var names = new List<string>();
            if (r.Ok)
                foreach (var line in SplitLines(r.Output))
                    if (LooksLikeDistroName(line)) names.Add(line.TrimStart('*'));
            if (names.Count == 0)
            {
                var full = await ExecAsync(new[] { "--list", "--running" }, 30000);
                foreach (var line in SplitLines(full.Output))
                {
                    var tokens = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (tokens.Length >= 2 && LooksLikeDistroName(tokens[0])
                        && (tokens.Any(t => t == "Running" || t == "运行中")))
                        names.Add(tokens[0].TrimStart('*'));
                }
            }
            return names;
        }

        private static bool LooksLikeDistroName(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            char c = s[0];
            return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '*';
        }

        /// <summary>指定发行版是否正在运行。</summary>
        public static async Task<bool> IsDistroRunningAsync(string distro)
        {
            var running = await ListRunningDistrosAsync();
            return running.Contains(distro, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>终止指定发行版（wsl -t，强制断电语义）。</summary>
        public static Task<WslResult> TerminateDistroAsync(string distro) =>
            ExecAsync(new[] { "--terminate", distro }, 60000);

        /// <summary>关闭整个 WSL2 VM（所有发行版，立即释放 vmmem）。</summary>
        public static Task<WslResult> ShutdownVmAsync() =>
            ExecAsync(new[] { "--shutdown" }, 60000);

        // ---------------- 发行版内命令执行 ----------------

        /// <summary>在发行版内执行 bash -lc 命令（登录 shell，加载 ~/.profile 以获得 node PATH）。</summary>
        public static Task<WslResult> RunInDistroAsync(string distro, string bashCommand, int timeoutMs = 120000) =>
            ExecAsync(new[] { "-d", distro, "--exec", "bash", "-lc", bashCommand }, timeoutMs);

        /// <summary>发行版默认用户名（空 = 无响应；root = 首次初始化未完成）。</summary>
        public static async Task<string> GetDistroUserAsync(string distro)
        {
            var r = await RunInDistroAsync(distro, "id -un", 120000);
            return r.Ok ? r.Output.Trim() : "";
        }

        /// <summary>发行版内 $HOME 绝对路径。</summary>
        public static async Task<string> GetDistroHomeAsync(string distro)
        {
            var r = await RunInDistroAsync(distro, "echo $HOME", 120000);
            return r.Ok ? r.Output.Trim() : "";
        }

        /// <summary>发行版内 pgrep -f 匹配的 PID 列表（无匹配返回空）。</summary>
        public static async Task<List<int>> PgrepAsync(string distro, string pattern, int timeoutMs = 60000)
        {
            var r = await RunInDistroAsync(distro, $"pgrep -f {Shq(pattern)} 2>/dev/null || true", timeoutMs);
            var pids = new List<int>();
            if (!r.Ok) return pids;
            foreach (var token in r.Output.Split(new[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries))
                if (int.TryParse(token, out var pid) && pid > 0 && !pids.Contains(pid))
                    pids.Add(pid);
            return pids;
        }

        /// <summary>读取发行版内文件内容（cat）。不存在返回 null。</summary>
        public static async Task<string> ReadDistroFileAsync(string distro, string linuxPath)
        {
            var r = await RunInDistroAsync(distro, $"cat {Shq(linuxPath)} 2>/dev/null || true", 60000);
            return r.Ok ? r.Output : null;
        }

        /// <summary>
        /// 向发行版写入文本文件：先写 Windows 侧临时目录（LF、无 BOM），再经 /mnt/c 用 cp 拷入。
        /// 规避 UNC 共享时序与 wsl.exe 引号转义两类问题。localDir 为 Windows 侧可写临时目录。
        /// </summary>
        public static async Task<bool> WriteDistroFileAsync(string distro, string linuxPath, string content, string localDir)
        {
            try
            {
                Directory.CreateDirectory(localDir);
                string fileName = "upload-" + Path.GetFileName(linuxPath);
                string local = Path.Combine(localDir, fileName);
                await File.WriteAllTextAsync(local, content, new UTF8Encoding(false));
                try
                {
                    string wslLocal = ManualWinToWsl(local);
                    var r = await RunInDistroAsync(distro,
                        $"cp {Shq(wslLocal)} {Shq(linuxPath)} && rm -f {Shq(wslLocal)}", 120000);
                    return r.Ok;
                }
                finally
                {
                    try { File.Delete(local); } catch { }
                }
            }
            catch { return false; }
        }

        // ---------------- 路径与网络 ----------------

        /// <summary>Windows 路径转 WSL 路径：优先 wslpath -a，失败退回字符串转换。</summary>
        public static async Task<string> WindowsToWslPathAsync(string distro, string windowsPath)
        {
            var r = await RunInDistroAsync(distro, $"wslpath -a {Shq(windowsPath)} 2>/dev/null || true", 120000);
            var converted = r.Output.Trim();
            if (r.Ok && converted.StartsWith('/')) return converted;
            return ManualWinToWsl(windowsPath);
        }

        /// <summary>离线转换：C:\Users\x → /mnt/c/Users/x（默认挂载布局）。</summary>
        public static string ManualWinToWsl(string winPath)
        {
            var p = (winPath ?? "").Trim().TrimEnd('\\');
            if (p.Length >= 2 && p[1] == ':')
            {
                char drive = char.ToLowerInvariant(p[0]);
                string rest = p.Substring(2).Replace('\\', '/');
                return "/mnt/" + drive + rest;
            }
            return (winPath ?? "").Replace('\\', '/');
        }

        /// <summary>TCP 端口探测（不依赖 HTTP 语义与系统代理）。</summary>
        public static async Task<bool> ProbeTcpAsync(string host, int port, int timeoutMs = 1200)
        {
            using var client = new TcpClient();
            try
            {
                await client.ConnectAsync(host, port).WaitAsync(TimeSpan.FromMilliseconds(timeoutMs));
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>发行版内 TCP 探测（/dev/tcp），用于区分“服务未起”与“localhost 转发故障”。</summary>
        public static async Task<bool> ProbeInDistroAsync(string distro, int port)
        {
            var cmd = $"(echo > /dev/tcp/127.0.0.1/{port}) >/dev/null 2>&1 && echo DSHWSL_OPEN || echo DSHWSL_CLOSED";
            var r = await RunInDistroAsync(distro, cmd, 30000);
            return r.Ok && r.Output.Trim() == "DSHWSL_OPEN";
        }

        /// <summary>追加 WSLENV 条目（Windows→WSL 传递环境变量）。</summary>
        public static string AppendWslenv(string existing, string entry)
        {
            if (string.IsNullOrEmpty(existing)) return entry;
            if (existing.Split(';').Any(x => x.Trim() == entry)) return existing;
            return existing + ";" + entry;
        }

        /// <summary>解析 Linux 侧 DSH_HOME/工作区：~ 前缀展开，空 = Linux 默认 ~/.dsh。</summary>
        public static string ResolveLinuxPath(string configPath, string distroHomeRoot)
        {
            var h = (configPath ?? "").Trim();
            if (h.Length == 0) return distroHomeRoot.TrimEnd('/') + "/.dsh";
            if (h == "~") return distroHomeRoot;
            if (h.StartsWith("~/", StringComparison.Ordinal)) return distroHomeRoot.TrimEnd('/') + "/" + h.Substring(2);
            return h;
        }

        /// <summary>是否为 Windows 路径（盘符: 开头或 UNC）。</summary>
        public static bool IsWindowsPath(string p) =>
            !string.IsNullOrEmpty(p)
            && (p.Length >= 2 && (p[1] == ':' || p[0] == '\\') || p.StartsWith("\\\\", StringComparison.Ordinal));
    }
}