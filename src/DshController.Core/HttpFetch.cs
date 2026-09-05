// ============================================================================
//  HttpFetch — 轻量 HTTP GET 封装（v0.6.0 插件市场）
//
//  项目此前维持"黑盒"边界（进程 + 端口 + 文件系统，零 HttpClient）；插件市场
//  需要拉取插件目录与 npm 包元数据，是第一个联网点。设计约束：
//    - 单例 HttpClient（连接池复用），默认走系统代理（企业/自建源环境友好）；
//    - 每次调用独立超时（CancellationTokenSource），任何失败返回 null 不抛异常，
//      调用方按"数据不可用"降级（目录回退缓存 / 兼容信息显示未声明）；
//    - User-Agent 标识 DshController/<版本>，便于数据源侧统计。
// ============================================================================

using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace DshController.Core
{
    public static class HttpFetch
    {
        private static readonly HttpClient Client = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        })
        {
            // 单次调用都有自己的 CTS 超时，这里只兜底防止连接池挂死
            Timeout = TimeSpan.FromSeconds(120)
        };

        static HttpFetch()
        {
            try { Client.DefaultRequestHeaders.UserAgent.ParseAdd("DshController/" + ErrorReporter.AppVersion); }
            catch
            {
                // 理由: AppVersion 若含不符合 User-Agent 语法的字符，ParseAdd 会抛异常；仅使自定义 UA 头不生效，请求仍可用，故尽力而为。
            }
        }

        /// <summary>GET 文本；失败（网络/超时/非 2xx）返回 null，不抛异常。</summary>
        public static async Task<string> GetStringAsync(string url, int timeoutMs = 20000)
        {
            if (string.IsNullOrWhiteSpace(url)) return null;
            try
            {
                using (var cts = new CancellationTokenSource(timeoutMs))
                using (var resp = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                    .ConfigureAwait(false))
                {
                    if (!resp.IsSuccessStatusCode) return null;
                    return await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                }
            }
            catch
            {
                return null;
            }
        }
    }
}
