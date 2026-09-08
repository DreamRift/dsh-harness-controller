// ============================================================================
//  UsageScanner — 会话日志的扫描与解压（重构 2.0 / P2）
//
//  Windows：直接遍历 <HOME>/sessions/**/session.jsonl.zstd 读文件。
//  WSL：两段式，避免每次把整个 sessions 目录 base64 搬一遍（原型分支的做法）——
//    ① 一次 wsl 往返只列出 (size, mtime, path)，输出极小；
//    ② 只对"缓存里没有或已变化"的文件发第二次往返取内容（分批，命令行不超长）；
//    没有变化的文件则一次都不传。
//  解压统一用 ZstdSharp（纯托管，无原生依赖）。
//  内存缓存按 (路径, size, mtime) 命中，带容量上限与淘汰，避免常驻进程无限增长。
// ============================================================================

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DshController.Core.Usage
{
    /// <summary>一次扫描的结果。</summary>
    public sealed class UsageScanResult
    {
        public List<UsageModelStat> Models { get; set; } = new List<UsageModelStat>();
        public List<UsageSessionScan> Sessions { get; set; } = new List<UsageSessionScan>();
        public int Files { get; set; }
        public bool Complete { get; set; }
        public string Error { get; set; } = "";
    }

    public static class UsageScanner
    {
        /// <summary>会话样本缓存上限（一份样本约几十 KB，512 份足够覆盖常见规模）。</summary>
        public const int CacheCapacity = 512;

        private sealed class CachedSession
        {
            public long Length;
            public long MtimeEpoch;
            public long LastUsedTicks;
            public UsageSessionScan Session;
        }

        private static readonly ConcurrentDictionary<string, CachedSession> Cache =
            new ConcurrentDictionary<string, CachedSession>(StringComparer.OrdinalIgnoreCase);

        public static void ClearCache() => Cache.Clear();

        public static int CacheCount => Cache.Count;

        // ==================== Windows ====================

        public static async Task<UsageScanResult> ScanWindowsAsync(string home, CancellationToken ct)
        {
            var result = new UsageScanResult();
            string root = Path.Combine(home ?? "", "sessions");
            if (!Directory.Exists(root))
            {
                result.Complete = true;
                return result;
            }

            List<string> files = Directory
                .EnumerateFiles(root, "session.jsonl.zstd", SearchOption.AllDirectories)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
            result.Files = files.Count;
            if (files.Count == 0)
            {
                result.Complete = true;
                return result;
            }

            var perSession = new List<UsageSessionScan>();
            await Task.Run(() =>
            {
                foreach (string file in files)
                {
                    ct.ThrowIfCancellationRequested();
                    UsageSessionScan session = LoadWindowsSession(file);
                    if (session != null) perSession.Add(session);
                }
            }, ct).ConfigureAwait(false);

            result.Sessions = perSession;
            result.Models = UsageParser.AggregateModels(perSession.Select(s => s.Samples));
            result.Complete = true;
            return result;
        }

        private static UsageSessionScan LoadWindowsSession(string file)
        {
            try
            {
                var fi = new FileInfo(file);
                if (!fi.Exists) return null;
                long mtime = new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeSeconds();
                string key = "win:" + file;
                UsageSessionScan hit = TryGetCached(key, fi.Length, mtime);
                if (hit != null) return hit;

                string text = Encoding.UTF8.GetString(Decompress(File.ReadAllBytes(file)));
                UsageSessionScan session = UsageParser.ParseSessionScan(text);
                Put(key, fi.Length, mtime, session);
                return session;
            }
            catch
            {
                return null;   // 理由: 单个会话文件损坏/被占用不影响整体统计
            }
        }

        // ==================== WSL ====================

        /// <summary>发行版内一个文件的清单项。</summary>
        public sealed class WslFileEntry
        {
            public long Size;
            public long Mtime;
            public string Path = "";
        }

        /// <summary>第一段：只列出 (size, mtime, path)，输出极小。</summary>
        public static string BuildListScript(string sessionsDir)
        {
            string h = WslTools.Shq(sessionsDir);
            return "if [ -d " + h + " ]; then " +
                   "find " + h + " -type f -name 'session.jsonl.zstd' " +
                   "-exec stat -c '@@DSHL %s %Y %n' {} + 2>/dev/null; fi; printf '@@DSHDONE\n'";
        }

        /// <summary>第二段：只取指定文件的内容（base64 帧）。</summary>
        public static string BuildFetchScript(IEnumerable<string> paths)
        {
            var sb = new StringBuilder();
            foreach (string p in paths ?? Enumerable.Empty<string>())
            {
                string q = WslTools.Shq(p);
                sb.Append("f=").Append(q).Append("; ")
                  .Append("sz=$(stat -c %s \"$f\" 2>/dev/null || echo 0); ")
                  .Append("mt=$(stat -c %Y \"$f\" 2>/dev/null || echo 0); ")
                  .Append("printf '@@DSHU %s %s %s\n' \"$sz\" \"$mt\" \"$f\"; ")
                  .Append("base64 -w0 \"$f\" 2>/dev/null; ")
                  .Append("printf '\n@@DSHEND\n'; ");
            }
            sb.Append("printf '@@DSHDONE\n'");
            return sb.ToString();
        }

        public static List<WslFileEntry> ParseListOutput(string output)
        {
            var list = new List<WslFileEntry>();
            if (string.IsNullOrEmpty(output)) return list;
            foreach (string raw in output.Split('\n'))
            {
                string line = raw.Trim('\r', ' ', '\t');
                if (!line.StartsWith("@@DSHL ", StringComparison.Ordinal)) continue;
                string rest = line.Substring(7);
                int sp1 = rest.IndexOf(' ');
                if (sp1 <= 0) continue;
                int sp2 = rest.IndexOf(' ', sp1 + 1);
                if (sp2 <= 0) continue;
                list.Add(new WslFileEntry
                {
                    Size = ParseLong(rest.Substring(0, sp1)),
                    Mtime = ParseLong(rest.Substring(sp1 + 1, sp2 - sp1 - 1)),
                    Path = rest.Substring(sp2 + 1).Trim()
                });
            }
            return list;
        }

        public static async Task<UsageScanResult> ScanWslAsync(string distro, string linuxHome,
            int batchSize, CancellationToken ct)
        {
            var result = new UsageScanResult();
            string sessionsDir = (linuxHome ?? "").TrimEnd('/') + "/sessions";

            WslResult listed = await WslTools.RunInDistroAsync(distro, BuildListScript(sessionsDir), 60000)
                .ConfigureAwait(false);
            List<WslFileEntry> entries = ParseListOutput(listed.Output);
            result.Files = entries.Count;
            if (entries.Count == 0)
            {
                result.Complete = listed.Ok || (listed.Output ?? "").Contains("@@DSHDONE");
                if (!result.Complete) result.Error = "列出发行版会话日志失败（exit=" + listed.ExitCode + "）";
                return result;
            }

            // 只取缓存未命中的文件；全部命中时第二次往返都省了
            var perSession = new List<UsageSessionScan>();
            var wanted = new List<string>();
            foreach (WslFileEntry e in entries)
            {
                UsageSessionScan hit = TryGetCached(CacheKey(distro, e.Path), e.Size, e.Mtime);
                if (hit != null) perSession.Add(hit);
                else wanted.Add(e.Path);
            }

            for (int i = 0; i < wanted.Count; i += Math.Max(1, batchSize))
            {
                ct.ThrowIfCancellationRequested();
                List<string> batch = wanted.GetRange(i, Math.Min(Math.Max(1, batchSize), wanted.Count - i));
                WslResult fetched = await WslTools
                    .RunInDistroAsync(distro, BuildFetchScript(batch), 300000).ConfigureAwait(false);
                perSession.AddRange(ParseWslScanFrames(fetched.Output, distro));
            }

            result.Sessions = perSession;
            result.Models = UsageParser.AggregateModels(perSession.Select(s => s.Samples));
            result.Complete = true;
            return result;
        }

        /// <summary>解析 base64 帧（离线可测）。</summary>
        public static List<List<UsageSample>> ParseWslFrames(string output, string cacheNamespace)
        {
            return ParseWslScanFrames(output, cacheNamespace).Select(s => s.Samples).ToList();
        }

        /// <summary>解析 WSL 帧为 token 与时序的完整会话扫描结果。</summary>
        public static List<UsageSessionScan> ParseWslScanFrames(string output, string cacheNamespace)
        {
            var result = new List<UsageSessionScan>();
            if (string.IsNullOrEmpty(output)) return result;

            int pos = 0;
            while (pos < output.Length)
            {
                int eol = output.IndexOf('\n', pos);
                if (eol < 0) eol = output.Length;
                string line = output.Substring(pos, eol - pos).TrimEnd('\r');
                pos = eol + 1;
                if (line.Length == 0) continue;
                if (line.StartsWith("@@DSHDONE", StringComparison.Ordinal)) break;
                if (!line.StartsWith("@@DSHU ", StringComparison.Ordinal)) continue;

                string header = line.Substring(7);
                int sp1 = header.IndexOf(' ');
                if (sp1 <= 0) continue;
                int sp2 = header.IndexOf(' ', sp1 + 1);
                if (sp2 <= 0) continue;
                long sz = ParseLong(header.Substring(0, sp1));
                long mt = ParseLong(header.Substring(sp1 + 1, sp2 - sp1 - 1));
                string path = header.Substring(sp2 + 1).Trim();

                var b64 = new StringBuilder();
                while (pos < output.Length)
                {
                    int eol2 = output.IndexOf('\n', pos);
                    if (eol2 < 0) eol2 = output.Length;
                    string dl = output.Substring(pos, eol2 - pos).TrimEnd('\r');
                    pos = eol2 + 1;
                    if (dl == "@@DSHEND") break;
                    if (dl == "@@DSHDONE") { pos = output.Length; break; }
                    if (dl.Length > 0) b64.Append(dl.Trim());
                }
                if (b64.Length == 0) continue;

                string key = CacheKey(cacheNamespace, path);
                UsageSessionScan session = TryGetCached(key, sz, mt);
                if (session == null)
                {
                    try
                    {
                        string text = Encoding.UTF8.GetString(Decompress(Convert.FromBase64String(b64.ToString())));
                        session = UsageParser.ParseSessionScan(text);
                        Put(key, sz, mt, session);
                    }
                    catch
                    {
                        session = null;   // 理由: 单帧损坏（传输截断/文件损坏）跳过，其余会话照常统计
                    }
                }
                if (session != null) result.Add(session);
            }
            return result;
        }

        // ==================== 缓存与压缩 ====================

        private static string CacheKey(string ns, string path) => "wsl:" + ns + ":" + path;

        private static UsageSessionScan TryGetCached(string key, long size, long mtime)
        {
            if (Cache.TryGetValue(key, out CachedSession c) && c.Length == size && c.MtimeEpoch == mtime)
            {
                c.LastUsedTicks = DateTime.UtcNow.Ticks;
                return c.Session;
            }
            return null;
        }

        private static void Put(string key, long size, long mtime, UsageSessionScan session)
        {
            Cache[key] = new CachedSession
            {
                Length = size,
                MtimeEpoch = mtime,
                LastUsedTicks = DateTime.UtcNow.Ticks,
                Session = session
            };
            if (Cache.Count <= CacheCapacity) return;

            // 超限淘汰最久未用的 1/4：常驻进程下缓存不能无限增长
            var oldest = Cache.OrderBy(kv => kv.Value.LastUsedTicks)
                .Take(Math.Max(1, Cache.Count / 4)).Select(kv => kv.Key).ToList();
            foreach (string k in oldest) Cache.TryRemove(k, out _);
        }

        /// <summary>zstd 解压（与 CompressForTest 配对；测试与将来的导入工具都会用到）。</summary>
        public static byte[] Decompress(byte[] compressed)
        {
            using (var input = new MemoryStream(compressed))
            using (var zs = new ZstdSharp.DecompressionStream(input))
            using (var output = new MemoryStream())
            {
                zs.CopyTo(output);
                return output.ToArray();
            }
        }

        /// <summary>压缩辅助（测试 fixture 用）：文本 → zstd 字节。</summary>
        public static byte[] CompressForTest(string text)
        {
            byte[] raw = Encoding.UTF8.GetBytes(text ?? "");
            using (var output = new MemoryStream())
            {
                using (var zs = new ZstdSharp.CompressionStream(output)) zs.Write(raw, 0, raw.Length);
                return output.ToArray();
            }
        }

        private static long ParseLong(string s)
        {
            return long.TryParse((s ?? "").Trim(), System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out long v) ? v : 0;
        }
    }
}
