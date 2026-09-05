// ============================================================================
//  JsonStore — 带原子写与损坏兜底的 JSON 落盘（重构 2.0 / P1）
//
//  历史问题：instances.json / plugin-records / plugin-cache / 用量备份各写了一遍
//  "临时文件 + Move overwrite + 损坏回退"，四份实现四种细节差异。
//  这里收敛成一个泛型工具：
//    - 写：先写 .tmp 再 Move(overwrite)，保证读者永远看到完整文件；
//    - 读：文件缺失或解析失败都返回兜底值，绝不抛给调用方；
//    - 目录自动创建；写失败返回 false 由调用方决定降级（不抛）。
// ============================================================================

using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DshController.Core.Storage
{
    public static class JsonStore
    {
        public static JsonSerializerOptions ReadOptions { get; } = new JsonSerializerOptions
        {
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            PropertyNameCaseInsensitive = true
        };

        public static JsonSerializerOptions WriteOptions { get; } = new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        /// <summary>读取；文件不存在/损坏/为空对象时返回 fallback()。</summary>
        public static T Read<T>(string path, Func<T> fallback)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return fallback();
                string text = File.ReadAllText(path, Encoding.UTF8);
                if (string.IsNullOrWhiteSpace(text)) return fallback();
                T parsed = JsonSerializer.Deserialize<T>(text, ReadOptions);
                return parsed == null ? fallback() : parsed;
            }
            catch
            {
                return fallback();   // 理由: 状态文件损坏不得阻断启动，调用方按"没有数据"继续
            }
        }

        /// <summary>原子写（tmp + Move overwrite）；失败返回 false，不抛。</summary>
        public static bool Write<T>(string path, T value)
        {
            try
            {
                if (string.IsNullOrEmpty(path)) return false;
                AppPaths.EnsureDir(Path.GetDirectoryName(path));
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(value, WriteOptions), new UTF8Encoding(false));
                File.Move(tmp, path, overwrite: true);
                return true;
            }
            catch
            {
                return false;   // 理由: 写失败由调用方决定降级（内存态仍可用），不影响主流程
            }
        }

        /// <summary>删除文件（不存在视为成功）。</summary>
        public static bool Delete(string path)
        {
            try
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path)) File.Delete(path);
                return true;
            }
            catch
            {
                return false;   // 理由: 删除失败只影响残留文件，不影响功能
            }
        }
    }
}
