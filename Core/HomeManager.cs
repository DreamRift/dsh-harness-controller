// ============================================================================
//  HomeManager — 多实例 HOME 创建 / 克隆 / 健康检查 / 删除（v0.3.0）
//
//  设计决策：
//    1. 所有方法只操作调用方传入的路径，不触碰真实默认 ~/.dsh；
//       DefaultHomeRoot 仅计算并返回字符串，不创建目录。
//    2. Clone 采用尽力而为策略：单步失败不中止整体；但排除规则严格，
//       node_modules 在递归复制时绝不进入目标 HOME。
//    3. Standard 只复制与实例配置/技能相关的文件；Full 复制除运行时
//       排除项外的全部内容；Blank 仅建目录，首次启动时由 dsh initProfile
//       生成 profiles。
//    4. 依赖路径重写针对 dstHome\profiles 下所有 package.json。指向
//       srcHome\packages 的 file/link 依赖会复制到新 HOME 并改写前缀，
//       指向 srcHome 其他位置则保留原值视为共享代码。
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace DshController.Core
{
    public enum CloneLevel { Blank, Standard, Full }

    public sealed class HomeManager
    {
        /// <summary>默认实例 HOME 根：%LOCALAPPDATA%\DshController\instances；不存在也仅返回字符串。</summary>
        public static string DefaultHomeRoot()
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(local, "DshController", "instances");
        }

        /// <summary>拼接 homeRoot 与 id 并规范化；id 含非法路径字符时抛 ArgumentException。</summary>
        public string NewHomePath(string homeRoot, string id)
        {
            if (string.IsNullOrEmpty(homeRoot))
                throw new ArgumentException("homeRoot 不能为空。", nameof(homeRoot));
            if (string.IsNullOrEmpty(id))
                throw new ArgumentException("id 不能为空。", nameof(id));
            if (id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || id.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
                throw new ArgumentException("id 包含非法路径字符，不能用作 HOME 目录名。", nameof(id));
            if (id == "." || id == "..")
                throw new ArgumentException("id 不能是当前目录或父目录。", nameof(id));

            return Path.GetFullPath(Path.Combine(homeRoot, id));
        }

        /// <summary>创建空白实例 HOME；Directory.CreateDirectory 幂等。</summary>
        public void CreateBlank(string home)
        {
            if (string.IsNullOrEmpty(home))
                throw new ArgumentException("home 不能为空。", nameof(home));
            Directory.CreateDirectory(home);
        }

        /// <summary>
        /// 克隆实例 HOME 到新 HOME。Blank 只建目录；Standard/Full 按档位复制，
        /// 并执行依赖路径重写。任何单步失败不中止整体（尽力而为）。
        /// </summary>
        public void Clone(string srcHome, string dstHome, CloneLevel level)
        {
            if (string.IsNullOrEmpty(srcHome))
                throw new ArgumentException("srcHome 不能为空。", nameof(srcHome));
            if (string.IsNullOrEmpty(dstHome))
                throw new ArgumentException("dstHome 不能为空。", nameof(dstHome));

            // Blank：只建立目标目录，不复制任何内容。
            Directory.CreateDirectory(dstHome);
            if (level == CloneLevel.Blank)
                return;

            string src = NormalizeDirectoryPath(srcHome);
            string dst = NormalizeDirectoryPath(dstHome);

            if (!Directory.Exists(src))
                return;

            if (string.Equals(src, dst, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("srcHome 与 dstHome 不能相同。", nameof(dstHome));

            CopyDirectoryContents(src, dst, level);
            RewritePackageDependencies(src, dst);
        }

        /// <summary>健康检查：<home>\profiles\web\cordis.yml 存在即为已初始化。</summary>
        public bool HealthCheck(string home, out string detail)
        {
            if (string.IsNullOrEmpty(home))
            {
                detail = "home 路径为空。";
                return false;
            }

            try
            {
                string cordis = Path.Combine(home, "profiles", "web", "cordis.yml");
                if (File.Exists(cordis))
                {
                    detail = "OK：" + cordis;
                    return true;
                }
                detail = "缺少 web profile 初始化产物：" + cordis;
                return false;
            }
            catch (Exception ex)
            {
                detail = "健康检查失败：" + ex.Message;
                return false;
            }
        }

        /// <summary>
        /// 删除实例 HOME。keepBackup=true 时先复制为 zip（<home 所在盘>\<同名>-backup-<yyyyMMdd_HHmmss>.zip）
        /// 再删目录；任何一步失败返回 false。
        /// </summary>
        public bool Delete(string home, bool keepBackup, out string backupPath)
        {
            backupPath = "";
            if (string.IsNullOrEmpty(home) || !Directory.Exists(home))
                return false;

            try
            {
                if (keepBackup)
                {
                    DirectoryInfo di = new DirectoryInfo(home);
                    string time = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    backupPath = Path.Combine(
                        GetDriveRoot(home),
                        di.Name + "-backup-" + time + ".zip");
                    if (File.Exists(backupPath))
                        File.Delete(backupPath);
                    ZipFile.CreateFromDirectory(home, backupPath, CompressionLevel.Optimal, includeBaseDirectory: false);
                }

                Directory.Delete(home, recursive: true);
                return true;
            }
            catch
            {
                backupPath = "";
                return false;
            }
        }

        /// <summary>把目录内容复制到 dst，按档位过滤不需要的文件/目录。</summary>
        private static void CopyDirectoryContents(string src, string dst, CloneLevel level)
        {
            HashSet<string> copyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "settings.yaml",
                ".env",
                "AGENTS.md",
                "cordis.patch.yml",
                ".agent-presets",
                "skills"
            };

            foreach (string srcItem in SafeEnumerateFileSystemEntries(src))
            {
                try
                {
                    string name = Path.GetFileName(srcItem);
                    string dstItem = Path.Combine(dst, name);

                    if (ShouldSkip(name))
                        continue;

                    // Standard 不复制 packages；Full 复制由递归排除规则处理 node_modules。
                    if (level == CloneLevel.Standard && string.Equals(name, "packages", StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (level == CloneLevel.Standard && !copyNames.Contains(name))
                        continue;

                    if (Directory.Exists(srcItem))
                        CopyDirectorySkipNodeModules(srcItem, dstItem);
                    else if (File.Exists(srcItem) && IsAllowedFile(srcItem))
                        CopyFileBestEffort(srcItem, dstItem);
                }
                catch
                {
                    // 单文件/目录失败不中止整体克隆。
                }
            }
        }

        /// <summary>递归复制目录；遇到 node_modules 直接跳过，不进入复制。</summary>
        private static void CopyDirectorySkipNodeModules(string srcDir, string dstDir)
        {
            if (ShouldSkipDir(Path.GetFileName(srcDir)))
                return;

            Directory.CreateDirectory(dstDir);

            foreach (string srcItem in SafeEnumerateFileSystemEntries(srcDir))
            {
                try
                {
                    string name = Path.GetFileName(srcItem);
                    string dstItem = Path.Combine(dstDir, name);

                    if (ShouldSkip(name))
                        continue;

                    if (Directory.Exists(srcItem))
                        CopyDirectorySkipNodeModules(srcItem, dstItem);
                    else if (File.Exists(srcItem) && IsAllowedFile(srcItem))
                        CopyFileBestEffort(srcItem, dstItem);
                }
                catch
                {
                    // 递归子项失败仍继续，不中止复制。
                }
            }
        }

        /// <summary>全部运行时/依赖排除项。目录名 node_modules 走递归入口拦截，文件走此检测。</summary>
        private static bool ShouldSkip(string name)
        {
            if (string.Equals(name, "node_modules", StringComparison.OrdinalIgnoreCase))
                return true;
            if (string.Equals(name, ".dsh-instance.lock", StringComparison.OrdinalIgnoreCase))
                return true;
            if (string.Equals(name, "backend.pid", StringComparison.OrdinalIgnoreCase))
                return true;
            if (name.EndsWith(".log", StringComparison.OrdinalIgnoreCase))
                return true;
            return name.StartsWith("backend-", StringComparison.OrdinalIgnoreCase) &&
                   name.EndsWith(".log", StringComparison.OrdinalIgnoreCase);
        }

        private static bool ShouldSkipDir(string name)
        {
            return string.Equals(name, "node_modules", StringComparison.OrdinalIgnoreCase) ||
                   ShouldSkip(name);
        }

        /// <summary>文件级校验：排除名匹配则跳过；也拦截命名成 backend-*.log 等场景。</summary>
        private static bool IsAllowedFile(string filePath)
        {
            string name = Path.GetFileName(filePath);
            if (ShouldSkip(name))
                return false;

            // 防重复：标准档在顶层过滤时已经只保留白名单项，此处兜底不做额外顶层过滤。
            return true;
        }

        private static void CopyFileBestEffort(string srcFile, string dstFile)
        {
            string dstDir = Path.GetDirectoryName(dstFile);
            if (!string.IsNullOrEmpty(dstDir))
                Directory.CreateDirectory(dstDir);
            File.Copy(srcFile, dstFile, overwrite: true);
        }

        /// <summary>枚举目录项；权限或 IO 异常时吞掉，保持克隆尽力而为。</summary>
        private static IEnumerable<string> SafeEnumerateFileSystemEntries(string dir)
        {
            try
            {
                return Directory.EnumerateFileSystemEntries(dir);
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        /// <summary>扫描 dstHome\profiles 下所有 package.json，改写指向 srcHome\packages 的 file/link 依赖。</summary>
        private static void RewritePackageDependencies(string srcHome, string dstHome)
        {
            string profilesDir = Path.Combine(dstHome, "profiles");
            if (!Directory.Exists(profilesDir))
                return;

            foreach (string packageJson in SafeEnumerateFiles(profilesDir, "package.json"))
            {
                RewriteOnePackageJson(srcHome, dstHome, packageJson);
            }
        }

        private static void RewriteOnePackageJson(string srcHome, string dstHome, string packageJson)
        {
            try
            {
                string json = File.ReadAllText(packageJson, Encoding.UTF8);
                using (JsonDocument doc = JsonDocument.Parse(json))
                {
                    if (doc.RootElement.ValueKind != JsonValueKind.Object)
                        return;

                    if (!doc.RootElement.TryGetProperty("dependencies", out JsonElement deps) ||
                        deps.ValueKind != JsonValueKind.Object)
                        return;

                    string packageDir = Path.GetDirectoryName(packageJson) ?? Environment.CurrentDirectory;
                    Dictionary<string, string> rewritten = new Dictionary<string, string>();
                    foreach (JsonProperty prop in deps.EnumerateObject())
                    {
                        string value = prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() : null;
                        string newValue = RewriteDependencyValue(srcHome, dstHome, packageDir, value,
                            out string packageCopySrc, out string packageCopyDst);
                        if (!string.IsNullOrEmpty(packageCopySrc) && !string.IsNullOrEmpty(packageCopyDst))
                            CopyDependencySource(packageCopySrc, packageCopyDst);
                        rewritten[prop.Name] = newValue;
                    }

                    if (rewritten.Count == 0)
                        return;

                    string updatedJson = RewriteDependenciesText(json, rewritten);
                    if (string.IsNullOrEmpty(updatedJson))
                        return;

                    File.WriteAllText(packageJson, updatedJson, new UTF8Encoding(false));
                }
            }
            catch
            {
                // 单个 package.json 解析/写入失败不中止整个克隆。
            }
        }

        /// <summary>
        /// 处理单个依赖值。返回改写后的依赖值；当需要复制 srcHome\packages 下的包时，
        /// 同时输出源/目标路径，由调用方执行复制。
        /// 相对路径（file:../x / link:..\y）按 package.json 所在目录解析（pnpm 语义）。
        /// </summary>
        private static string RewriteDependencyValue(string srcHome, string dstHome, string packageDir,
            string value, out string packageCopySrc, out string packageCopyDst)
        {
            packageCopySrc = "";
            packageCopyDst = "";

            if (string.IsNullOrEmpty(value))
                return value ?? "";

            string prefix = null;
            string raw = null;
            if (value.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            {
                prefix = "file:";
                raw = value.Substring("file:".Length);
            }
            else if (value.StartsWith("link:", StringComparison.OrdinalIgnoreCase))
            {
                prefix = "link:";
                raw = value.Substring("link:".Length);
            }

            if (prefix == null)
                return value;

            if (string.IsNullOrEmpty(raw))
                return value;

            string refPath = NormalizeDependencyPath(raw, packageDir);
            string srcNorm = NormalizeDirectoryPath(srcHome);
            string dstNorm = NormalizeDirectoryPath(dstHome);

            if (refPath.IndexOf(srcNorm, StringComparison.OrdinalIgnoreCase) < 0)
                return value;

            string packagesPrefix = Path.Combine(srcNorm, "packages");
            if (!StartsWithDirectory(refPath, packagesPrefix))
                return value; // srcHome 内但不在 packages 下：共享代码，保留原值。

            // 计算相对 packages 段的子路径，并映射到新 HOME 的同名位置。
            string rel = refPath.Substring(packagesPrefix.Length).TrimStart('\\', '/');
            string newPackagesPath = Path.Combine(Path.Combine(dstNorm, "packages"), rel);
            newPackagesPath = Path.GetFullPath(newPackagesPath);

            packageCopySrc = refPath;
            packageCopyDst = newPackagesPath;

            // 相对路径由 package.json 所在目录解析时，需反推 profiles 子目录层级。
            // 这里采用绝对路径，保证任何 package.json 深度都能指向新 HOME 的 packages。
            return prefix + newPackagesPath;
        }

        private static string RewriteDependenciesText(string json, Dictionary<string, string> rewritten)
        {
            try
            {
                using (JsonDocument doc = JsonDocument.Parse(json))
                {
                    if (doc.RootElement.ValueKind != JsonValueKind.Object)
                        return "";
                    JsonElement deps = doc.RootElement.GetProperty("dependencies");

                    JsonObjectBuilder builder = new JsonObjectBuilder();
                    foreach (JsonProperty prop in doc.RootElement.EnumerateObject())
                    {
                        if (prop.NameEquals("dependencies"))
                        {
                            builder.AddDependencies(rewritten);
                        }
                        else
                        {
                            builder.AddRaw(prop.Name, prop.Value.GetRawText());
                        }
                    }

                    using (MemoryStream ms = new MemoryStream())
                    {
                        using (Utf8JsonWriter writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
                        {
                            builder.WriteTo(writer);
                        }
                        return Encoding.UTF8.GetString(ms.ToArray());
                    }
                }
            }
            catch
            {
                return "";
            }
        }

        /// <summary>把 src 路径复制到 dst；src 为目录则递归复制并继续排除 node_modules。</summary>
        private static void CopyDependencySource(string src, string dst)
        {
            try
            {
                if (Directory.Exists(src))
                    CopyDirectorySkipNodeModules(src, dst);
                else if (File.Exists(src))
                    CopyFileBestEffort(src, dst);
            }
            catch
            {
                // 包复制失败不中止整体；依赖值已改写，后续健康检查可暴露问题。
            }
        }

        private static IEnumerable<string> SafeEnumerateFiles(string dir, string pattern)
        {
            try
            {
                return Directory.EnumerateFiles(dir, pattern, SearchOption.AllDirectories);
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        private static string NormalizeDirectoryPath(string path)
        {
            return Path.GetFullPath(path).TrimEnd('\\', '/');
        }

        private static string NormalizeDependencyPath(string path, string baseDir)
        {
            try
            {
                // 依赖路径按 pnpm 语义相对 package.json 所在目录解析（file:/link: 前缀已剥离）。
                string combined = path;
                if (!Path.IsPathRooted(combined))
                    combined = Path.Combine(baseDir, combined);
                return Path.GetFullPath(combined);
            }
            catch
            {
                return path;
            }
        }

        private static bool StartsWithDirectory(string full, string dir)
        {
            full = Path.GetFullPath(full);
            dir = Path.GetFullPath(dir).TrimEnd('\\', '/');
            return full.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(full, dir, StringComparison.OrdinalIgnoreCase);
        }

        private static string GetDriveRoot(string home)
        {
            try
            {
                return Path.GetPathRoot(Path.GetFullPath(home)) ?? Path.GetTempPath();
            }
            catch
            {
                return Path.GetTempPath();
            }
        }
    }

    /// <summary>轻量 JSON 对象重建器：用于保留 dependencies 之外字段的原始文本。</summary>
    internal sealed class JsonObjectBuilder
    {
        private readonly List<KeyValuePair<string, string>> _raw = new List<KeyValuePair<string, string>>();
        private Dictionary<string, string> _dependencies;

        public void AddDependencies(Dictionary<string, string> dependencies)
        {
            _dependencies = dependencies;
        }

        public void AddRaw(string name, string rawText)
        {
            _raw.Add(new KeyValuePair<string, string>(name, rawText));
        }

        public void WriteTo(Utf8JsonWriter writer)
        {
            writer.WriteStartObject();
            foreach (KeyValuePair<string, string> pair in _raw)
            {
                writer.WritePropertyName(pair.Key);
                writer.WriteRawValue(pair.Value, skipInputValidation: true);
            }
            if (_dependencies != null)
            {
                writer.WritePropertyName("dependencies");
                writer.WriteStartObject();
                foreach (KeyValuePair<string, string> pair in _dependencies)
                {
                    writer.WritePropertyName(pair.Key);
                    writer.WriteStringValue(pair.Value);
                }
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
        }
    }
}
