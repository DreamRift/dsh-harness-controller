// ============================================================================
//  ProviderSyncWriter — 供应商预设写入引擎（API 页 llm-pi-ai 迁移轮）
//
//  · 目标文件：实例 DSH_HOME 的 settings.yaml（路径由调用方解析传入；测试重定向临时 HOME）；
//  · 写入目标（dsh 源码证据）：非官方预设 → llm-pi-ai.providers.<key>（dsh 实际读取的段）；
//    官方预设 → 仅写 llm-deepseek.apiKeyEnv（官方模型/思考档/多模态由实例原生适配器提供）；
//    两者都顺手清除根级 providers.<key> 旧块（历史误同步的死配置，迁移清理）；
//  · 合并语义：纯函数在 ProviderSyncMerge（增补式合并，宽松段与已有声明保留）；
//  · 原子性：先备份原文件为 .bak-<ts>，再写临时文件后替换；任一步失败恢复——
//    失败绝不覆盖原配置；合并结果与原文件一致时不落盘（幂等同步零噪音）。
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;

namespace DshController.Core
{
    /// <summary>一次同步的写入计划：由预设经 <see cref="ProviderSyncWrite.For"/> 构建。</summary>
    public sealed class ProviderSyncWrite
    {
        /// <summary>非官方预设：写入 llm-pi-ai.providers 的映射条目（null=官方仅密钥路径）。</summary>
        public InstanceProviderEntry Entry { get; set; }

        /// <summary>官方预设：llm-deepseek.apiKeyEnv 目标名（null=不写）。</summary>
        public string OfficialApiKeyEnv { get; set; }

        /// <summary>同步成功后从根级 providers: 移除的旧块 key（null=不清）。</summary>
        public string CleanupRootKey { get; set; }

        /// <summary>按预设形态出写计划：官方=密钥引用+旧块清理（未填密钥则整单跳过）；
        /// 非官方=llm-pi-ai 块+旧块清理。</summary>
        public static ProviderSyncWrite For(ProviderPreset preset)
        {
            var write = new ProviderSyncWrite();
            if (preset == null) return write;
            if (preset.IsBuiltin)
            {
                if ((preset.ApiKey ?? "").Trim().Length == 0) return write;
                write.OfficialApiKeyEnv = ProviderConfigMapper.ApiKeyEnvName(preset.ProviderId);
                write.CleanupRootKey = preset.ProviderId;
                return write;
            }
            write.Entry = ProviderConfigMapper.ToEntry(preset).Entry;
            write.CleanupRootKey = preset.ProviderId;
            return write;
        }
    }

    public sealed class ProviderSyncWriter
    {
        /// <summary>把写计划并入 settingsPath。成功 true；失败恢复原样并给 error。</summary>
        public bool Apply(string settingsPath, ProviderSyncWrite write, out string error)
        {
            error = "";
            if (string.IsNullOrEmpty(settingsPath)) { error = "settings.yaml 路径为空"; return false; }
            if (IsWriteEmpty(write)) { error = "写计划为空（无可同步内容）"; return false; }
            try
            {
                string original = File.Exists(settingsPath) ? File.ReadAllText(settingsPath) : "";
                string merged = MergeSettings(original, write, out error);
                if (merged == null) return false;

                if (string.Equals(merged, original, StringComparison.Ordinal)) return true;   // 无变化不落盘

                string dir = Path.GetDirectoryName(settingsPath);
                if (string.IsNullOrEmpty(dir)) dir = ".";
                Directory.CreateDirectory(dir);
                string tmp = settingsPath + ".tmp-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                string bak = settingsPath + ".bak-" + DateTime.Now.ToString("yyyyMMddHHmmss");
                File.WriteAllText(tmp, merged);
                try
                {
                    if (File.Exists(settingsPath)) File.Copy(settingsPath, bak, overwrite: true);
                    File.Move(tmp, settingsPath, overwrite: true);
                }
                catch
                {
                    if (File.Exists(tmp)) File.Delete(tmp);
                    throw;
                }
                return true;
            }
            catch (Exception ex)
            {
                error = "写入失败（原配置未改动）：" + ex.Message;
                return false;
            }
        }

        /// <summary>WSL 实例写入（N7）：settings.yaml 在发行版 Linux 侧 HOME。
        /// 合并/备份语义与 Apply 一致（备份 .bak-<ts>、无变化不落盘）；读写走 WslTools
        /// 的 /mnt/c cp 中转通道（规避 UNC 时序），发行版未运行时由命令自然唤醒。
        /// 统一落 LF 行尾（Linux 侧惯例）。</summary>
        public async Task<bool> ApplyWslAsync(string distro, string wslHomeConfig, ProviderSyncWrite write, out string error)
        {
            error = "";
            if (string.IsNullOrWhiteSpace(distro)) { error = "WSL 实例缺少发行版信息"; return false; }
            if (IsWriteEmpty(write)) { error = "写计划为空（无可同步内容）"; return false; }
            try
            {
                // 未运行时 RunInDistroAsync 会自然唤醒 VM；无响应（未安装/唤醒失败）即中止
                string distroHome = await WslTools.GetDistroHomeAsync(distro);
                if (string.IsNullOrWhiteSpace(distroHome))
                { error = "WSL 发行版无响应（未运行且无法自动唤醒），本次未写入"; return false; }

                string linuxHome = WslTools.ResolveLinuxPath(wslHomeConfig, distroHome).TrimEnd('/');
                string settingsPath = linuxHome + "/settings.yaml";
                string original = await WslTools.ReadDistroFileAsync(distro, settingsPath) ?? "";
                string merged = MergeSettings(original, write, out string mergeError);
                if (merged == null) { error = mergeError; return false; }
                if (string.Equals(merged, original, StringComparison.Ordinal)) return true;   // 无变化不落盘

                string ts = DateTime.Now.ToString("yyyyMMddHHmmss");
                string bak = settingsPath + ".bak-" + ts;
                string shHome = Sh(linuxHome), shPath = Sh(settingsPath), shBak = Sh(bak);
                WslTools.WslResult prep = await WslTools.RunInDistroAsync(distro,
                    $"mkdir -p {shHome} && (cp {shPath} {shBak} 2>/dev/null || true)", 60000);
                if (prep == null || !prep.Ok)
                { error = "写入失败（原配置未改动）：发行版内准备备份失败"; return false; }

                string localDir = Path.Combine(Path.GetTempPath(), "dsh-sync-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                string lf = merged.Replace("\r\n", "\n");
                if (!await WslTools.WriteDistroFileAsync(distro, settingsPath, lf, localDir))
                { error = "写入失败（原配置未改动）：/mnt/c 中转通道不可用"; return false; }
                return true;
            }
            catch (Exception ex)
            {
                error = "写入失败（原配置未改动）：" + ex.Message;
                return false;
            }
        }

        private static bool IsWriteEmpty(ProviderSyncWrite write) =>
            write == null || (write.Entry == null && write.OfficialApiKeyEnv == null && write.CleanupRootKey == null);

        /// <summary>合并链（共享）：非官方块 → 官方密钥 → 根级旧块清理；任一步失败返回 null。</summary>
        private static string MergeSettings(string original, ProviderSyncWrite write, out string error)
        {
            string merged = original ?? "";
            if (write.Entry != null)
            {
                if (string.IsNullOrEmpty(write.Entry.Key)) { error = "条目无效（缺 key）"; return null; }
                string block = ProviderSyncPlan.RenderYamlBlock(write.Entry);
                merged = ProviderSyncMerge.MergePiAiProviders(merged, block, write.Entry.Key, out error);
                if (merged == null) { error = "合并失败（原配置未改动）：" + error; return null; }
            }
            if (!string.IsNullOrEmpty(write.OfficialApiKeyEnv))
            {
                merged = ProviderSyncMerge.SetLlmDeepseekApiKeyEnv(merged, write.OfficialApiKeyEnv, out error);
                if (merged == null) { error = "官方密钥写入失败（原配置未改动）：" + error; return null; }
            }
            if (!string.IsNullOrEmpty(write.CleanupRootKey))
            {
                merged = ProviderSyncMerge.RemoveRootProvidersKey(merged, write.CleanupRootKey, out error);
                if (merged == null) { error = "旧根级块清理失败（原配置未改动）：" + error; return null; }
            }
            error = "";
            return merged;
        }

        /// <summary>bash 单引号转义（发行版内命令拼装用）。</summary>
        private static string Sh(string s) => "'" + (s ?? "").Replace("'", "'\\''") + "'";
    }
}
