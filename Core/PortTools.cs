// ============================================================================
//  PortTools — 端口探测 / 监听进程定位 / 进程树终止（v0.2.0，全异步）
//
//  相比 legacy 版：
//    - Probe 由同步 WaitOne(1200) 改为 ConnectAsync + 取消令牌，完全不占线程；
//    - FindListenerPid 结果缓存 3 秒（v0.1.0 在外部后端运行时每秒起一个
//      netstat 子进程且同步阻塞 UI 线程 3 秒）；
//    - KillTree 优先用 .NET 的 Kill(entireProcessTree)，taskkill /T /F 兜底；
//    - 所有长等待均为 await，调用方 UI 线程零冻结。
// ============================================================================

using System;
using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DshController.Core
{
    public static class PortTools
    {
        public static readonly TimeSpan ProbeTimeout = TimeSpan.FromMilliseconds(1200);

        private static readonly Regex ListenRx = new Regex(
            @":(\d+)\s+\S+\s+LISTENING\s+(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // netstat 结果缓存（外部后端运行时状态刷新每秒询问，避免进程风暴）
        private static readonly object NetstatLock = new object();
        private static Tuple<int, DateTime> _netstatCache;

        public static string Url(string host, int port) { return "http://" + host + ":" + port + "/"; }

        /// <summary>TCP 握手探测（不依赖 HTTP 语义与系统代理），全异步。</summary>
        public static async Task<bool> ProbeAsync(string host, int port, CancellationToken ct = default)
        {
            try
            {
                using (var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    timeoutCts.CancelAfter(ProbeTimeout);
                    using (var client = new TcpClient())
                    {
                        await client.ConnectAsync(host, port, timeoutCts.Token);
                        return true;
                    }
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return false; // 仅探测超时
            }
            catch
            {
                return false;
            }
        }

        /// <summary>等待端口到达指定状态。</summary>
        public static async Task<bool> WaitForPortAsync(string host, int port, bool up, int timeoutSeconds,
            CancellationToken ct = default)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                if (await ProbeAsync(host, port, ct) == up) return true;
                try { await Task.Delay(500, ct); } catch (OperationCanceledException) { break; }
            }
            return await ProbeAsync(host, port, ct) == up;
        }

        /// <summary>通过 netstat 找监听端口的 PID（异步读输出防死锁，结果缓存 3 秒）。</summary>
        public static async Task<int> FindListenerPidAsync(int port)
        {
            lock (NetstatLock)
            {
                if (_netstatCache != null && _netstatCache.Item1 == port &&
                    DateTime.UtcNow - _netstatCache.Item2 < TimeSpan.FromSeconds(3))
                    return _lastPid;
            }
            try
            {
                var psi = new ProcessStartInfo("netstat.exe", "-ano -p TCP")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    StandardOutputEncoding = Encoding.UTF8
                };
                using (Process p = Process.Start(psi))
                {
                    // 先异步读完输出（流关闭即进程基本结束），再限时等待退出，避免死锁
                    string outp = await p.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
                    if (!await Task.Run(() => p.WaitForExit(3000)).ConfigureAwait(false))
                    {
                        try { p.Kill(); } catch { }
                        return 0;
                    }
                    foreach (string line in outp.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        Match m = ListenRx.Match(line);
                        if (m.Success)
                        {
                            int lp;
                            if (int.TryParse(m.Groups[1].Value, out int want) && want == port &&
                                int.TryParse(m.Groups[2].Value, out lp))
                            {
                                lock (NetstatLock)
                                {
                                    _netstatCache = Tuple.Create(port, DateTime.UtcNow);
                                    _lastPid = lp;
                                }
                                return lp;
                            }
                        }
                    }
                }
            }
            catch { }
            return 0;
        }

        private static int _lastPid; // 与 _netstatCache 配套：命中缓存时直接取上次 PID

        /// <summary>进程是否存活（吞掉"无关联进程"等异常，修 legacy D6 一类问题）。</summary>
        public static bool IsAlive(int pid)
        {
            if (pid <= 0) return false;
            try
            {
                using (Process p = Process.GetProcessById(pid)) return !p.HasExited;
            }
            catch { return false; }
        }

        /// <summary>结束进程树：优先 .NET Kill(entireProcessTree)，taskkill /T /F 兜底。</summary>
        public static async Task<bool> KillTreeAsync(int pid)
        {
            bool killed = false;
            try
            {
                using (Process p = Process.GetProcessById(pid))
                {
                    p.Kill(entireProcessTree: true);   // .NET 6：一次杀整棵树
                    killed = await Task.Run(() => p.WaitForExit(5000)).ConfigureAwait(false);
                }
            }
            catch { /* 已退出或权限问题，走兜底 */ }

            if (!killed || IsAlive(pid))
            {
                try
                {
                    var psi = new ProcessStartInfo("taskkill.exe", "/PID " + pid + " /T /F")
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    using (Process t = Process.Start(psi))
                        killed = await Task.Run(() => t.WaitForExit(10000)).ConfigureAwait(false);
                }
                catch { }
            }

            // 复核（短重试窗口，容忍内核收尾）
            for (int i = 0; i < 5 && IsAlive(pid); i++)
                await Task.Delay(200).ConfigureAwait(false);
            return !IsAlive(pid);
        }

        /// <summary>确保端口释放：先杀已知进程树；仍监听则定位监听者一并结束。</summary>
        public static async Task<Tuple<bool, string>> EnsurePortFreeAsync(string host, int port, int knownPid)
        {
            var sb = new StringBuilder();
            bool closed = !await ProbeAsync(host, port).ConfigureAwait(false);
            if (knownPid > 0 && !closed)
            {
                bool ok = await KillTreeAsync(knownPid).ConfigureAwait(false);
                sb.AppendLine("kill tree(" + knownPid + "): " + (ok ? "ok" : "fail"));
                closed = !await ProbeAsync(host, port).ConfigureAwait(false);
            }
            if (!closed)
            {
                int listener = await FindListenerPidAsync(port).ConfigureAwait(false);
                if (listener > 0 && listener != knownPid)
                {
                    bool ok = await KillTreeAsync(listener).ConfigureAwait(false);
                    sb.AppendLine("kill listener(" + listener + "): " + (ok ? "ok" : "fail"));
                }
                bool settled = await WaitForPortAsync(host, port, false, 15).ConfigureAwait(false);
                sb.AppendLine("wait port free: " + (settled ? "ok" : "timeout"));
                closed = !await ProbeAsync(host, port).ConfigureAwait(false);
            }
            sb.AppendLine("port closed: " + (closed ? "YES" : "NO"));
            return Tuple.Create(closed, sb.ToString().TrimEnd());
        }

        /// <summary>系统空闲内存等信息（错误报告用，尽力而为）。</summary>
        public static string OsDescription()
        {
            try
            {
                return RuntimeInformation.OSDescription + " (" +
                       (Environment.Is64BitOperatingSystem ? "x64" : "x86") + ")";
            }
            catch { return Environment.OSVersion.ToString(); }
        }
    }
}
