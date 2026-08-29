using System.Text.Json;
using System.Text.Json.Serialization;

namespace DshWslCtrl;

/// <summary>
/// 全局配置（config.json）。字段结构与 DshController 的 instances.json 实例字段同构，
/// 便于验证通过后移植回正式版。
/// </summary>
public sealed class DshWslConfig
{
    /// <summary>WSL 发行版名称（如 Ubuntu-24.04）</summary>
    public string Distro { get; set; } = "Ubuntu-24.04";

    /// <summary>harness 监听端口（Windows 浏览器访问 http://127.0.0.1:端口/）</summary>
    public int Port { get; set; } = 3080;

    /// <summary>WSL 内工作区：Linux 绝对路径（~/ 开头或 /）或 Windows 路径（C:\...，按需共享时用）。默认 Linux 原生实现隔离。</summary>
    public string Workspace { get; set; } = "~/dsh-workspaces/main";

    /// <summary>Linux 侧 DSH_HOME（支持 ~ 前缀；空 = 用 Linux 默认 ~/.dsh）</summary>
    public string Home { get; set; } = "~/dsh-instances/main";

    /// <summary>就绪后自动打开浏览器（默认关闭，界面由用户 dshwsl open 控制）</summary>
    public bool AutoOpenBrowser { get; set; } = false;

    /// <summary>额外 --trusted-host 参数（可多个）</summary>
    public List<string> TrustedHosts { get; set; } = new();

    /// <summary>provision 时安装的 Node.js 版本（与 Windows 侧一致）</summary>
    public string NodeVersion { get; set; } = "24.19.0";

    /// <summary>npm 安装源</summary>
    public string NpmRegistry { get; set; } = "https://registry.npmmirror.com";

    /// <summary>发行版首次初始化时的默认 Linux 用户名（start 自举用）</summary>
    public string DefaultUserName { get; set; } = "dsh";

    /// <summary>停止 harness 后 WSL 关闭策略：smart（默认）| always | distroOnly</summary>
    public string VmShutdownPolicy { get; set; } = "smart";

    /// <summary>WSL 内 dsh 命令覆盖（空 = bash -lc 'command -v dsh' 自动解析）</summary>
    public string DshCommand { get; set; } = "";

    /// <summary>多实例定义（参考 Windows 侧 instances.json 的实例字段结构；缺省字段回落到顶层默认）</summary>
    public List<InstanceDef> Instances { get; set; } = new();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>配置目录：DSHWSL_CONFIG_DIR 环境变量 &gt; exe 旁(存在 config.json 时) &gt; 当前目录(存在时) &gt; exe 旁</summary>
    public static string ConfigDir { get; } = ResolveConfigDir();

    public static string ConfigPath => Path.Combine(ConfigDir, "config.json");

    /// <summary>运行时状态目录（state-端口.json、上传临时脚本等）</summary>
    public static string RunDir => Path.Combine(ConfigDir, "run");

    private static string ResolveConfigDir()
    {
        var env = Environment.GetEnvironmentVariable("DSHWSL_CONFIG_DIR");
        if (!string.IsNullOrEmpty(env)) return env;
        string exeDir = AppContext.BaseDirectory;
        if (File.Exists(Path.Combine(exeDir, "config.json"))) return exeDir;
        if (File.Exists(Path.Combine(Environment.CurrentDirectory, "config.json"))) return Environment.CurrentDirectory;
        return exeDir;
    }

    public static DshWslConfig Load() => Load(ConfigPath);

    public static DshWslConfig Load(string path)
    {
        if (File.Exists(path))
        {
            try
            {
                var cfg = JsonSerializer.Deserialize<DshWslConfig>(File.ReadAllText(path), JsonOpts);
                if (cfg != null) return cfg;
            }
            catch
            {
                try { File.Copy(path, path + ".bad", true); } catch { }
            }
        }
        var fresh = new DshWslConfig();
        if (path == ConfigPath) fresh.Save();
        return fresh;
    }

    public void Save() => Save(ConfigPath);

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOpts));
    }

    /// <summary>规范化校验：返回错误列表（空 = 有效）</summary>
    public List<string> Validate()
    {
        var errors = new List<string>();
        if (Port is < 1 or > 65535) errors.Add($"端口非法: {Port}");
        if (string.IsNullOrWhiteSpace(Distro)) errors.Add("distro 未配置");
        if (!VmShutdownPolicy.Equals("smart", StringComparison.OrdinalIgnoreCase)
            && !VmShutdownPolicy.Equals("always", StringComparison.OrdinalIgnoreCase)
            && !VmShutdownPolicy.Equals("distroOnly", StringComparison.OrdinalIgnoreCase))
            errors.Add($"vmShutdownPolicy 非法: {VmShutdownPolicy}（可选 smart/always/distroOnly）");
        return errors;
    }
}

/// <summary>单个 WSL harness 实例的运行时状态（run/state-端口.json），供 status/stop 在工具重启后使用。</summary>
public sealed class RunState
{
    public int Port { get; set; }
    public string Distro { get; set; } = "";
    /// <summary>foreground（前台流式）| detach（后台，日志在 WSL /tmp）</summary>
    public string Mode { get; set; } = "foreground";
    /// <summary>解析后的 Linux 绝对 DSH_HOME</summary>
    public string DistroHome { get; set; } = "";
    /// <summary>Windows 工作区路径</summary>
    public string Workspace { get; set; } = "";
    /// <summary>WSL 内工作区路径（/mnt/c/...）</summary>
    public string WslWorkspace { get; set; } = "";
    /// <summary>解析出的 dsh 绝对路径</summary>
    public string DshCommand { get; set; } = "";
    /// <summary>宿主 wsl.exe 进程 PID（detach 模式下作为存活监视）</summary>
    public int WslExePid { get; set; }
    public DateTime StartedAt { get; set; }
    public string Url { get; set; } = "";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string PathFor(int port) => Path.Combine(DshWslConfig.RunDir, $"state-{port}.json");

    public static RunState? Load(int port)
    {
        try
        {
            var p = PathFor(port);
            return File.Exists(p) ? JsonSerializer.Deserialize<RunState>(File.ReadAllText(p), JsonOpts) : null;
        }
        catch { return null; }
    }

    public static List<RunState> LoadAll()
    {
        var list = new List<RunState>();
        try
        {
            if (Directory.Exists(DshWslConfig.RunDir))
                foreach (var f in Directory.GetFiles(DshWslConfig.RunDir, "state-*.json"))
                {
                    var s = JsonSerializer.Deserialize<RunState>(File.ReadAllText(f), JsonOpts);
                    if (s != null) list.Add(s);
                }
        }
        catch { }
        return list;
    }

    public void Save()
    {
        Directory.CreateDirectory(DshWslConfig.RunDir);
        File.WriteAllText(PathFor(Port), JsonSerializer.Serialize(this, JsonOpts));
    }

    public void Delete()
    {
        try { File.Delete(PathFor(Port)); } catch { }
    }
}

/// <summary>多实例定义（字段结构对齐 Windows 侧 DshController 的 instances.json 实例字段，便于将来移植）。</summary>
public sealed class InstanceDef
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Distro { get; set; }
    public int Port { get; set; } = 3080;
    public string? Workspace { get; set; }
    public string? Home { get; set; }
    public bool AutoOpenBrowser { get; set; } = false;
    public List<string> TrustedHosts { get; set; } = new();
}
