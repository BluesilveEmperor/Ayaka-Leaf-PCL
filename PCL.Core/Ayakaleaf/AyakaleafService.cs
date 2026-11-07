using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using PCL.Core.App;
using PCL.Core.App.Configuration;
using PCL.Core.Logging;

namespace PCL.Core.Ayakaleaf;

/// <summary>
/// Ayakaleaf 服务栈状态。
/// </summary>
public enum AyakaleafStatus
{
    /// <summary>内嵌运行时尚未部署。</summary>
    NotDeployed,

    /// <summary>服务栈未运行。</summary>
    Stopped,

    /// <summary>正在启动。</summary>
    Starting,

    /// <summary>运行中（探活通过）。</summary>
    Running,

    /// <summary>正在停止。</summary>
    Stopping,

    /// <summary>异常（启动失败或探活异常）。</summary>
    Error,
}

/// <summary>
/// 容器运行信息。
/// </summary>
public sealed record AyakaleafContainer(string Name, string Image, string Status);

/// <summary>
/// 镜像信息。
/// </summary>
public sealed record AyakaleafImage(string Repository, string Tag, string Size, string Created);

/// <summary>
/// Ayakaleaf（Overleaf 服务栈）管理服务。
/// 运行时形态：WSL 容器（wslc，WSL 3.0+ 内置）直接承载服务镜像，
/// 启动器负责部署（镜像加载 + VHD 数据卷/网络创建）与日常启停管理。
/// 数据卷为独立 .vhdx 文件，备份/恢复即文件级复制。
/// </summary>
public static class AyakaleafService
{
    private static readonly HttpClient ProbeClient = new() { Timeout = TimeSpan.FromSeconds(3) };

    /// <summary>部署等待探活通过的最大秒数。</summary>
    private const int StartProbeTimeoutSeconds = 180;

    /// <summary>每个 VHD 数据卷的容量上限（动态扩展，不预占空间）。</summary>
    private const long VhdVolumeSizeBytes = 20L * 1024 * 1024 * 1024;

    private const string NetworkName = "ol-net";

    public const string MongoContainer = "mongo";
    public const string RedisContainer = "redis";
    public const string OverleafContainer = "overleaf";
    public const string GitBridgeContainer = "git-bridge";

    public const string MongoImage = "mongo:8.0";
    public const string RedisImage = "redis:7";
    public const string OverleafImage = "sharelatex/sharelatex:server-pro";
    public const string GitBridgeImage = "ghcr.io/ayaka-notes/overleaf-pro/git-bridge:latest";

    /// <summary>
    /// 实际使用的 Overleaf 镜像（repo:tag）：配置覆盖优先，留空回落内置默认值。
    /// </summary>
    public static string EffectiveOverleafImage =>
        string.IsNullOrWhiteSpace(Config.Ayakaleaf.OverleafImage) ? OverleafImage : Config.Ayakaleaf.OverleafImage.Trim();

    /// <summary>简体中文界面使用的端口（启用 zh-CN 时为主端口 +1；未启用时回落主端口，该端口不再映射）。</summary>
    public static int ChinesePort => PortForLanguage("zh-CN");

    /// <summary>
    /// 界面语言切换（端口映射 + nginx Host/链接补丁）是否已确认可用。
    /// 这是运行时自检结果而非部署配置：服务处于 Running 并不代表语言切换可用。
    /// </summary>
    public static bool LanguageSwitchAvailable { get; private set; }

    /// <summary>服务栈基础数据卷（独立 VHD 文件）。</summary>
    public static readonly string[] DataVolumes = { "mongo_data", "redis_data", "overleaf_data", "overleaf_logs" };

    /// <summary>Git-Bridge 数据卷（仅启用 Git-Bridge 时创建/使用）。</summary>
    public const string GitBridgeVolume = "git_bridge_data";

    /// <summary>
    /// 当前应存在的数据卷集合：基础 4 卷 + （启用 Git-Bridge 时）git_bridge_data。
    /// </summary>
    public static IReadOnlyList<string> ActiveDataVolumes =>
        Config.Ayakaleaf.EnableGitBridge
            ? new[] { "mongo_data", "redis_data", "overleaf_data", "overleaf_logs", GitBridgeVolume }
            : new[] { "mongo_data", "redis_data", "overleaf_data", "overleaf_logs" };

    /// <summary>内嵌运行时目录（分发包内，含 images\ 镜像包）。</summary>
    public static string RuntimeDirectory => Path.Combine(AppContext.BaseDirectory, "runtime");

    /// <summary>备份导出默认目录。</summary>
    public static string BackupDirectory => Path.Combine(AppContext.BaseDirectory, "backups");

    public static string BaseUrl => $"http://localhost:{Config.Ayakaleaf.Port}";

    /// <summary>
    /// 局域网访问开启时队友可用的站点地址（本机局域网 IP + 主端口），未开启时为 null。
    /// </summary>
    public static string? LanSiteUrl => Config.Ayakaleaf.AllowLanAccess
        ? $"http://{ResolveLanAddress()}:{Config.Ayakaleaf.Port}"
        : null;

    /// <summary>
    /// 解析本机局域网 IPv4 地址（跳过回环、隧道、常见虚拟网卡，
    /// 以及代理 TUN 虚拟网卡如 Mihomo/Clash 与 198.18.0.0/15 保留网段）。
    /// 找不到可用地址时返回 "localhost"。
    /// </summary>
    public static string ResolveLanAddress()
    {
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                var name = nic.Name.ToLowerInvariant();
                if (name.Contains("vethernet") || name.Contains("vmware") ||
                    name.Contains("virtualbox") || name.Contains("loopback") ||
                    name.Contains("tap-") || name.Contains("tun") ||
                    // 代理 TUN 虚拟网卡（Clash Verge/Mihomo 走 wintun 驱动，
                    // 接口类型显示为 Ethernet，接口名默认 "Mihomo"，上方类型与 tun 过滤拦不住）
                    name.Contains("mihomo") || name.Contains("clash") ||
                    name.Contains("singbox") || name.Contains("sing-box") ||
                    name.Contains("wintun") || name.Contains("wireguard"))
                    continue;
                foreach (var addr in nic.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var ip = addr.Address.ToString();
                    if (ip.StartsWith("169.254.", StringComparison.Ordinal)) continue;
                    // 198.18.0.0/15：RFC 2544 基准保留段，Mihomo/Clash/sing-box 等
                    // 代理 TUN 默认网段；真实局域网不会使用，排除以免误选虚拟网卡 IP
                    if (ip.StartsWith("198.18.", StringComparison.Ordinal) ||
                        ip.StartsWith("198.19.", StringComparison.Ordinal)) continue;
                    return ip;
                }
            }
        }
        catch
        {
            // 网络枚举失败按 localhost 处理
        }
        return "localhost";
    }

    /// <summary>
    /// 取项目邀请令牌密钥；配置为空时生成 32 字节随机值并持久化，
    /// 之后重建容器复用同一密钥，已发出的邀请链接不失效。
    /// </summary>
    private static string EnsureInviteTokenSecret()
    {
        var secret = Config.Ayakaleaf.InviteTokenSecret;
        if (!string.IsNullOrEmpty(secret)) return secret;
        secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        Config.Ayakaleaf.InviteTokenSecret = secret;
        return secret;
    }

    /// <summary>
    /// 按界面语言偏好返回工作台入口 URL（语言代码经 <see cref="NormalizeLanguageCode"/> 归一化，
    /// 未启用/未知语言回落主端口 English）。
    /// </summary>
    public static string BaseUrlForLanguage(string lang) => $"http://localhost:{PortForLanguage(lang)}";

    // ---------------- 界面语言（多语言端口分配） ----------------

    /// <summary>镜像内置的界面语言：Code 为服务端 lngCode，NativeName 为语言自称（注入脚本下拉显示），Name 为设置页中文名。</summary>
    public sealed record AyakaleafLanguage(string Code, string NativeName, string Name);

    /// <summary>
    /// 镜像内置的全部界面语言（对照 ayakaleaf-pro <c>services/web/locales</c> 的 18 份语言资源）。
    /// 顺序即设置页展示顺序；en 固定首位绑定主端口。
    /// </summary>
    public static readonly AyakaleafLanguage[] SupportedLanguages =
    [
        new("en", "English", "英语"),
        new("zh-CN", "简体中文", "简体中文"),
        new("cs", "Čeština", "捷克语"),
        new("da", "Dansk", "丹麦语"),
        new("de", "Deutsch", "德语"),
        new("es", "Español", "西班牙语"),
        new("fi", "Suomi", "芬兰语"),
        new("fr", "Français", "法语"),
        new("it", "Italiano", "意大利语"),
        new("ja", "日本語", "日语"),
        new("ko", "한국어", "韩语"),
        new("nl", "Nederlands", "荷兰语"),
        new("no", "Norsk", "挪威语"),
        new("pl", "Polski", "波兰语"),
        new("pt", "Português", "葡萄牙语"),
        new("ru", "Русский", "俄语"),
        new("sv", "Svenska", "瑞典语"),
        new("tr", "Türkçe", "土耳其语"),
    ];

    /// <summary>
    /// 解析 <c>EnabledLanguages</c> 配置为有序语言代码列表：
    /// 逗号分隔、忽略未知代码与重复项、en 固定首位（绑定主端口）。
    /// 默认配置 "en,zh-CN" → [en, zh-CN]，与历史双语言行为完全一致。
    /// </summary>
    public static List<string> GetEnabledLanguages()
    {
        var result = new List<string>();
        foreach (var raw in (Config.Ayakaleaf.EnabledLanguages ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var code = NormalizeLanguageCode(raw.Trim());
            if (code == "en" || result.Contains(code)) continue;
            result.Add(code);
        }
        result.Insert(0, "en");
        return result;
    }

    /// <summary>
    /// 归一化语言代码：旧值 "zh" 兼容映射为 "zh-CN"；匹配镜像内置语言（忽略大小写）；
    /// 未知代码回落 "en"。
    /// </summary>
    public static string NormalizeLanguageCode(string? lang)
    {
        if (string.Equals(lang, "zh", StringComparison.OrdinalIgnoreCase)) return "zh-CN";
        foreach (var supported in SupportedLanguages)
        {
            if (string.Equals(supported.Code, lang, StringComparison.OrdinalIgnoreCase)) return supported.Code;
        }
        return "en";
    }

    /// <summary>语言在启用列表中的序号（en=0），未启用返回 0。</summary>
    public static int LanguageIndex(string lang)
    {
        var code = NormalizeLanguageCode(lang);
        return GetEnabledLanguages().IndexOf(code);
    }

    /// <summary>指定界面语言绑定的本机端口：en（或未启用/未知语言）= 主端口，其后每语言 +1。</summary>
    public static int PortForLanguage(string lang) => Config.Ayakaleaf.Port + LanguageIndex(lang);

    /// <summary>指定语言是否已在 <c>EnabledLanguages</c> 中启用（lang 先归一化，"zh" 视同 "zh-CN"）。</summary>
    public static bool IsLanguageEnabled(string lang) => LanguageIndex(lang) > 0;

    /// <summary>
    /// 当前部署/运行状态（轻量判断，供 UI 初始显示）。
    /// </summary>
    public static AyakaleafStatus InitialStatus => Config.Ayakaleaf.Deployed ? AyakaleafStatus.Stopped : AyakaleafStatus.NotDeployed;

    /// <summary>
    /// HTTP 探活：能建立连接并收到任意 HTTP 响应即视为服务在运行。
    /// </summary>
    public static async Task<bool> ProbeAsync()
    {
        try
        {
            using var resp = await ProbeClient.GetAsync(BaseUrl + Config.Ayakaleaf.ProbePath);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ---------------- 查询 ----------------

    /// <summary>
    /// 判断容器是否存在（任意状态）。
    /// </summary>
    public static async Task<bool> ContainerExistsAsync(string name)
    {
        return (await WslcInterop.RunAsync($"inspect \"{name}\"", 30000)).IsSuccess;
    }

    /// <summary>
    /// 判断容器是否运行中（inspect State.Running）。
    /// </summary>
    public static async Task<bool> IsContainerRunningAsync(string name)
    {
        var r = await WslcInterop.RunAsync($"inspect \"{name}\"", 30000);
        if (!r.IsSuccess) return false;
        return TryGetInspectRunning(r.Stdout);
    }

    private static bool TryGetInspectRunning(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.TryGetProperty("State", out var state) &&
                    state.TryGetProperty("Running", out var running))
                    return running.GetBoolean();
            }
        }
        catch
        {
            // JSON 解析失败按未运行处理
        }
        return false;
    }

    /// <summary>
    /// 获取容器列表（仅 Ayakaleaf 服务栈的三只容器，含已停止的）。
    /// </summary>
    public static async Task<List<AyakaleafContainer>> GetContainersAsync()
    {
        var r = await WslcInterop.RunAsync("list -a --format json", 30000);
        var list = new List<AyakaleafContainer>();
        if (!r.IsSuccess) return list;
        foreach (var line in r.Stdout.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || !trimmed.StartsWith('{')) continue;
            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                var root = doc.RootElement;
                var name = root.TryGetProperty("Names", out var n) ? n.GetString() : null;
                var image = root.TryGetProperty("Image", out var i) ? i.GetString() : null;
                var status = root.TryGetProperty("Status", out var s) ? s.GetString() : null;
                if (name == null) continue;
                if (!name.Equals(MongoContainer, StringComparison.Ordinal) &&
                    !name.Equals(RedisContainer, StringComparison.Ordinal) &&
                    !name.Equals(OverleafContainer, StringComparison.Ordinal) &&
                    !name.Equals(GitBridgeContainer, StringComparison.Ordinal))
                    continue;
                list.Add(new AyakaleafContainer(name, image ?? "", status ?? ""));
            }
            catch
            {
                // 单行解析失败跳过
            }
        }
        return list;
    }

    /// <summary>
    /// 获取容器日志尾部。
    /// </summary>
    public static async Task<string> GetLogsAsync(int tail = 120, string container = OverleafContainer)
    {
        var r = await WslcInterop.RunAsync($"logs --tail {tail} \"{container}\"", 30000);
        if (!r.IsSuccess) return "（无法获取日志）" + r.ErrorText;
        return (r.Stdout + "\n" + r.Stderr).Trim();
    }

    /// <summary>
    /// 列出服务栈数据卷（实际存在于引擎中的）。
    /// </summary>
    public static async Task<List<string>> ListVolumesAsync()
    {
        var list = new List<string>();
        foreach (var volume in ActiveDataVolumes)
        {
            if ((await WslcInterop.RunAsync($"volume inspect \"{volume}\"", 30000)).IsSuccess)
                list.Add(volume);
        }
        return list;
    }

    /// <summary>
    /// 列出镜像。
    /// </summary>
    public static async Task<List<AyakaleafImage>> ListImagesAsync()
    {
        var r = await WslcInterop.RunAsync("images --format json", 30000);
        var list = new List<AyakaleafImage>();
        if (!r.IsSuccess) return list;
        foreach (var line in r.Stdout.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || !trimmed.StartsWith('{')) continue;
            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                var root = doc.RootElement;
                list.Add(new AyakaleafImage(
                    root.TryGetProperty("Repository", out var repo) ? repo.GetString() ?? "" : "",
                    root.TryGetProperty("Tag", out var tag) ? tag.GetString() ?? "" : "",
                    root.TryGetProperty("Size", out var size) ? size.GetString() ?? "" : "",
                    root.TryGetProperty("CreatedSince", out var created) ? created.GetString() ?? "" : ""));
            }
            catch
            {
                // 单行解析失败跳过
            }
        }
        return list;
    }

    // ---------------- 部署 ----------------

    /// <summary>
    /// 部署内嵌运行时：加载镜像（幂等）→ 创建 VHD 数据卷 → 创建服务网络。
    /// </summary>
    public static async Task DeployAsync(Action<string> progress, CancellationToken ct = default)
    {
        if (!WslcInterop.IsAvailable)
            throw new Exception("未找到 wslc.exe（WSL 容器工具），请将 WSL 升级到 3.0 或更高版本后重试");

        var imagesDir = Path.Combine(RuntimeDirectory, "images");
        if (!Directory.Exists(imagesDir))
            throw new DirectoryNotFoundException($"未找到镜像目录 {imagesDir}");
        var tars = Directory.GetFiles(imagesDir, "*.tar*").OrderBy(f => f).ToArray();
        if (tars.Length == 0)
            throw new FileNotFoundException($"镜像目录 {imagesDir} 中没有镜像包（*.tar / *.tar.gz）");

        foreach (var tar in tars)
        {
            ct.ThrowIfCancellationRequested();
            progress($"加载镜像 {Path.GetFileName(tar)}…");
            var r = await WslcInterop.RunAsync($"load -i \"{tar}\"", 1800000);
            if (!r.IsSuccess)
                throw new Exception($"加载镜像 {Path.GetFileName(tar)} 失败：{r.ErrorText}");
        }

        await EnsureResourcesAsync(progress, ct);

        Config.Ayakaleaf.Deployed = true;
        progress("部署完成");
    }

    /// <summary>
    /// 确保数据卷与服务网络存在（已存在则跳过）。
    /// </summary>
    private static async Task EnsureResourcesAsync(Action<string> progress, CancellationToken ct)
    {
        progress("检查数据卷…");
        foreach (var volume in ActiveDataVolumes)
        {
            ct.ThrowIfCancellationRequested();
            await CreateResourceIfMissingAsync(
                $"volume create \"{volume}\" -d vhd -o SizeBytes={VhdVolumeSizeBytes}", $"数据卷 {volume}");
        }

        progress("检查服务网络…");
        await CreateResourceIfMissingAsync($"network create {NetworkName}", "服务网络");
    }

    /// <summary>
    /// 资源不存在时创建（重复创建视为成功）。
    /// </summary>
    private static async Task CreateResourceIfMissingAsync(string arguments, string what)
    {
        var r = await WslcInterop.RunAsync(arguments, 120000);
        if (r.IsSuccess) return;
        var text = r.Stdout + " " + r.Stderr;
        if (text.Contains("ALREADY_EXISTS", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("already in use", StringComparison.OrdinalIgnoreCase))
        {
            LogWrapper.Info($"[Ayakaleaf] {what} 已存在，跳过创建");
            return;
        }
        throw new Exception($"创建{what}失败：" + r.ErrorText);
    }

    /// <summary>
    /// 构造 Overleaf 语言域名映射 JSON（每个启用的界面语言绑定到同容器的一个端口）。
    /// 局域网开放时额外登记 hide 的 localhost 条目：语言选择器展示可分享的
    /// 局域网地址，而本机（启动器工作台等）经 localhost 访问仍能正确识别语言。
    /// </summary>
    private static string BuildLangMapping(string host)
    {
        var langs = GetEnabledLanguages();
        var entries = new List<string>();
        var localEntries = new List<string>();
        for (var i = 0; i < langs.Count; i++)
        {
            var code = langs[i];
            var port = Config.Ayakaleaf.Port + i;
            var key = SubdomainKeyFor(code);
            entries.Add($"\"{key}\":{{\"lngCode\":\"{code}\",\"url\":\"http://{host}:{port}\"}}");
            if (host != "localhost")
                localEntries.Add($"\"{key}-local\":{{\"lngCode\":\"{code}\",\"url\":\"http://localhost:{port}\",\"hide\":true}}");
        }
        return "{" + string.Join(",", entries.Concat(localEntries)) + "}";
    }

    /// <summary>
    /// 语言映射 JSON 键：en=www、zh-CN=zh（两者与历史配置保持一致，
    /// 默认配置升级后 ContainerProfile 不变、容器不会无谓重建），其余语言用自身代码。
    /// </summary>
    private static string SubdomainKeyFor(string code) => code switch
    {
        "en" => "www",
        "zh-CN" => "zh",
        _ => code,
    };

    /// <summary>
    /// 写出 Overleaf 语言域名映射环境文件，返回文件路径。
    /// 页脚语言选择器据此列出可切换的语言；地址使用指定主机
    /// （局域网开放时为本机 IP，队友点击切换语言同样可达）。
    /// </summary>
    private static string WriteOverleafLanguageEnvFile(string host)
    {
        var path = Path.Combine(Path.GetTempPath(), "ayakaleaf-overleaf-lang.env");
        File.WriteAllText(path, "OVERLEAF_LANG_DOMAIN_MAPPING=" + BuildLangMapping(host) + "\n", new System.Text.UTF8Encoding(false));
        return path;
    }

    // ---------------- 功能环境变量构建 ----------------

    /// <summary>
    /// 构建 Overleaf 容器的功能环境变量集合（不含基础连接变量与站点地址，
    /// 也不含经 --env-file 传递的语言映射）。
    /// 每个功能遵循「开关开启且必要凭据非空才注入」；凭据缺失时不注入半套变量。
    /// </summary>
    private static List<KeyValuePair<string, string>> BuildFeatureEnvironment()
    {
        var env = new List<KeyValuePair<string, string>>();
        var cfg = Config.Ayakaleaf;

        // Git-Bridge / MCP Server
        if (cfg.EnableGitBridge)
        {
            env.Add(new("GIT_BRIDGE_ENABLED", "true"));
            env.Add(new("GIT_BRIDGE_HOST", GitBridgeContainer));
            env.Add(new("GIT_BRIDGE_PORT", "8000"));
        }

        // AI Chat / Error Assistant
        if (cfg.EnableAi && !string.IsNullOrWhiteSpace(cfg.AiBaseUrl) &&
            !string.IsNullOrWhiteSpace(cfg.AiApiKey) && !string.IsNullOrWhiteSpace(cfg.AiModel))
        {
            env.Add(new("AI_ENABLED", "true"));
            env.Add(new("AI_BASE_URL", cfg.AiBaseUrl.Trim()));
            env.Add(new("AI_API_KEY", cfg.AiApiKey.Trim()));
            env.Add(new("AI_MODEL", cfg.AiModel.Trim()));
            if (!string.IsNullOrWhiteSpace(cfg.AiImageModel)) env.Add(new("AI_IMAGE_MODEL", cfg.AiImageModel.Trim()));
            if (!string.IsNullOrWhiteSpace(cfg.AiPdfModel)) env.Add(new("AI_PDF_MODEL", cfg.AiPdfModel.Trim()));
            if (cfg.AiTokenQuota > 0)
            {
                env.Add(new("AI_TOKEN_QUOTA", cfg.AiTokenQuota.ToString()));
                env.Add(new("AI_TOKEN_QUOTA_PERIOD", "month"));
            }
        }

        // Template Gallery
        if (cfg.EnableTemplateGallery)
        {
            env.Add(new("OVERLEAF_TEMPLATE_GALLERY", "true"));
            if (!string.IsNullOrWhiteSpace(cfg.TemplatesUserId))
                env.Add(new("OVERLEAF_TEMPLATES_USER_ID", cfg.TemplatesUserId.Trim()));
            if (!string.IsNullOrWhiteSpace(cfg.TemplateCategories))
                env.Add(new("OVERLEAF_TEMPLATE_CATEGORIES", cfg.TemplateCategories.Trim()));
        }

        // GitHub Sync
        if (cfg.EnableGithubSync && cfg.EnableGitBridge &&
            !string.IsNullOrWhiteSpace(cfg.GithubClientId) && !string.IsNullOrWhiteSpace(cfg.GithubClientSecret))
        {
            env.Add(new("GITHUB_SYNC_ENABLED", "true"));
            env.Add(new("GITHUB_SYNC_URL", cfg.GithubSyncUrl.Trim()));
            env.Add(new("GITHUB_SYNC_CLIENT_ID", cfg.GithubClientId.Trim()));
            env.Add(new("GITHUB_SYNC_CLIENT_SECRET", cfg.GithubClientSecret.Trim()));
            env.Add(new("GITHUB_TOKEN_CIPHER_PASSWORD", EnsureTokenCipherPassword(
                cfg.GithubTokenCipherPassword, v => cfg.GithubTokenCipherPassword = v)));
        }

        // GitLab Sync
        if (cfg.EnableGitlabSync && cfg.EnableGitBridge &&
            !string.IsNullOrWhiteSpace(cfg.GitlabClientId) && !string.IsNullOrWhiteSpace(cfg.GitlabClientSecret))
        {
            env.Add(new("GITLAB_SYNC_ENABLED", "true"));
            env.Add(new("GITLAB_SYNC_URL", cfg.GitlabSyncUrl.Trim()));
            env.Add(new("GITLAB_SYNC_CLIENT_ID", cfg.GitlabClientId.Trim()));
            env.Add(new("GITLAB_SYNC_CLIENT_SECRET", cfg.GitlabClientSecret.Trim()));
            env.Add(new("GITLAB_TOKEN_CIPHER_PASSWORD", EnsureTokenCipherPassword(
                cfg.GitlabTokenCipherPassword, v => cfg.GitlabTokenCipherPassword = v)));
        }

        // Gitea Sync
        if (cfg.EnableGiteaSync && cfg.EnableGitBridge &&
            !string.IsNullOrWhiteSpace(cfg.GiteaClientId) && !string.IsNullOrWhiteSpace(cfg.GiteaClientSecret))
        {
            env.Add(new("GITEA_SYNC_ENABLED", "true"));
            env.Add(new("GITEA_SYNC_URL", cfg.GiteaSyncUrl.Trim()));
            env.Add(new("GITEA_SYNC_CLIENT_ID", cfg.GiteaClientId.Trim()));
            env.Add(new("GITEA_SYNC_CLIENT_SECRET", cfg.GiteaClientSecret.Trim()));
            env.Add(new("GITEA_TOKEN_CIPHER_PASSWORD", EnsureTokenCipherPassword(
                cfg.GiteaTokenCipherPassword, v => cfg.GiteaTokenCipherPassword = v)));
        }

        // Zotero / Mendeley（链接文件类型按需追加）
        var linkedTypes = new List<string>();
        if (cfg.EnableZotero && !string.IsNullOrWhiteSpace(cfg.ZoteroClientId) && !string.IsNullOrWhiteSpace(cfg.ZoteroClientSecret))
        {
            linkedTypes.Add("zotero");
            env.Add(new("ZOTERO_CLIENT_ID", cfg.ZoteroClientId.Trim()));
            env.Add(new("ZOTERO_CLIENT_SECRET", cfg.ZoteroClientSecret.Trim()));
        }
        if (cfg.EnableMendeley && !string.IsNullOrWhiteSpace(cfg.MendeleyClientId) && !string.IsNullOrWhiteSpace(cfg.MendeleyClientSecret))
        {
            linkedTypes.Add("mendeley");
            env.Add(new("MENDELEY_CLIENT_ID", cfg.MendeleyClientId.Trim()));
            env.Add(new("MENDELEY_CLIENT_SECRET", cfg.MendeleyClientSecret.Trim()));
        }
        if (linkedTypes.Count > 0)
            env.Add(new("ENABLED_LINKED_FILE_TYPES", "project_file,project_output_file," + string.Join(",", linkedTypes)));

        // reCAPTCHA
        if (cfg.EnableRecaptcha && !string.IsNullOrWhiteSpace(cfg.RecaptchaSiteKey) && !string.IsNullOrWhiteSpace(cfg.RecaptchaSecretKey))
        {
            env.Add(new("RECAPTCHA_SITE_KEY", cfg.RecaptchaSiteKey.Trim()));
            env.Add(new("RECAPTCHA_SECRET_KEY", cfg.RecaptchaSecretKey.Trim()));
        }

        // 泄露密码检查
        if (cfg.EnableHibp)
            env.Add(new("HAVE_I_BEEN_PWNED_ENABLED", "true"));

        // 品牌化
        if (!string.IsNullOrWhiteSpace(cfg.AppName)) env.Add(new("OVERLEAF_APP_NAME", cfg.AppName.Trim()));
        if (!string.IsNullOrWhiteSpace(cfg.NavTitle)) env.Add(new("OVERLEAF_NAV_TITLE", cfg.NavTitle.Trim()));
        if (!string.IsNullOrWhiteSpace(cfg.HeaderImageUrl)) env.Add(new("OVERLEAF_HEADER_IMAGE_URL", cfg.HeaderImageUrl.Trim()));
        if (!string.IsNullOrWhiteSpace(cfg.HeaderImageUrlLight)) env.Add(new("OVERLEAF_HEADER_IMAGE_URL_LIGHT", cfg.HeaderImageUrlLight.Trim()));
        if (!string.IsNullOrWhiteSpace(cfg.HeaderImageUrlDark)) env.Add(new("OVERLEAF_HEADER_IMAGE_URL_DARK", cfg.HeaderImageUrlDark.Trim()));

        // 高级环境变量（每行 key=value，原样注入；用于 SSO 等未提供专用 UI 的配置）
        foreach (var raw in (cfg.AdvancedEnv ?? "").Split('\n'))
        {
            var line = raw.Trim();
            var eq = line.IndexOf('=');
            if (line.Length == 0 || line.StartsWith('#') || eq <= 0) continue;
            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();
            if (key.Length == 0) continue;
            env.Add(new(key, value));
        }

        return env;
    }

    /// <summary>
    /// 按注册策略生成注册相关环境变量：
    /// Open → 允许公开注册；DomainList → 域列表（域名非空时）；
    /// Closed → 不注入（服务端默认不开放注册）。
    /// </summary>
    private static List<KeyValuePair<string, string>> BuildRegistrationEnvironment()
    {
        var cfg = Config.Ayakaleaf;
        switch (cfg.RegistrationMode)
        {
            case AyakaleafRegistrationMode.Open:
                return [new("OVERLEAF_ALLOW_PUBLIC_REGISTRATION", "true")];
            case AyakaleafRegistrationMode.DomainList when !string.IsNullOrWhiteSpace(cfg.RegistrationDomains):
                return [new("OVERLEAF_ALLOW_PUBLIC_REGISTRATION", cfg.RegistrationDomains.Replace("\n", "").Replace(" ", ""))];
            case AyakaleafRegistrationMode.Closed:
            default:
                return [];
        }
    }

    /// <summary>
    /// 计算功能环境变量的稳定指纹：按 key 排序后拼接 key=value，
    /// 取 UTF-8 SHA256 前 16 位十六进制。用于 ContainerProfile 漂移检测。
    /// </summary>
    internal static string ComputeFeatureFingerprint(IEnumerable<KeyValuePair<string, string>> env)
    {
        var joined = string.Join("\n", env
            .OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => p.Key + "=" + p.Value));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(joined));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    /// <summary>
    /// 取用户 token 加密密钥；配置为空时生成 32 字节随机值并持久化（每个 provider 独立）。
    /// </summary>
    private static string EnsureTokenCipherPassword(string current, Action<string> persist)
    {
        if (!string.IsNullOrEmpty(current)) return current;
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        persist(secret);
        return secret;
    }

    /// <summary>
    /// 确保容器内 nginx 两项补丁（幂等，每次 StartAsync 都会执行）：
    /// 1. 转发完整的 Host 头（$host → $http_host）：默认的 $host 不带端口，
    ///    web 服务按 Host 匹配界面语言时无法区分主端口（en）与 +1 端口（zh-CN）。
    /// 2. 语言链接主机保持补丁：sub_filter 向每个 HTML 页面注入脚本，把指向
    ///    语言端口的链接重写为当前访问的主机名。语言映射中的链接使用局域网 IP，
    ///    localhost 访问者点击页脚语言选择器会跨主机跳转而丢失会话 Cookie，
    ///    重写后“在哪个主机访问就在哪个主机上切端口”，localhost 与 IP 均无缝。
    /// 任一补丁失败不阻断启动；nginx 已运行则重载配置。
    /// </summary>
    private static async Task EnsureNginxPatchesAsync(Action<string> progress)
    {
        // 用 "." 通配 "$"（写法上避开反斜杠与句中 $，兼容 wslc/sed 参数传递链）
        const string sedScript = "s/Host .host;/Host $http_host;/g";
        for (var attempt = 0; ; attempt++)
        {
            var r = await WslcInterop.RunAsync(
                $"exec {OverleafContainer} sed -i \"{sedScript}\" /etc/nginx/sites-enabled/overleaf.conf", 30000);
            if (r.IsSuccess) break;
            if (attempt >= 2)
            {
                // 补丁失败不阻断启动：语言切换不可用，但服务本身不受影响
                LogWrapper.Warn($"[Ayakaleaf] nginx Host 头补丁执行失败：{r.ErrorText?.Trim()}");
                progress("警告：界面语言切换可能不可用（nginx 配置补丁失败）");
                LanguageSwitchAvailable = false;
                return;
            }
            await Task.Delay(2000);
        }

        // 语言链接主机保持补丁 + 品牌替换：写入 conf.d（nginx.conf 已 include），内容经
        // base64 传递以避开多层引号转义问题。品牌替换把上游写死、不走 i18n 的
        // "Overleaf Community Edition"（页面标题）与 "DIGITAL SCIENCE"/"Digital Science"
        //（页脚署名）统一替换为品牌名（品牌化未配置时用 Ayakaleaf）。
        var languagePorts = string.Join("\",\"", GetEnabledLanguages().Select((_, i) => Config.Ayakaleaf.Port + i));
        var js =
            "(function(){var ayakaPorts=[\"" + languagePorts + "\"];" +
            "function fix(a){try{var u=new URL(a.href);" +
            "if(u.hostname===location.hostname||ayakaPorts.indexOf(u.port)<0)return;" +
            "u.hostname=location.hostname;a.href=u.href}catch(e){}}" +
            "function run(){for(var l=document.links,i=0;i<l.length;i++)fix(l[i])}" +
            "var q=false;function s(){if(q)return;q=true;" +
            "requestAnimationFrame(function(){q=false;run()})}" +
            "new MutationObserver(s).observe(document.documentElement,{childList:true,subtree:true});s()})()";
        var brand = string.IsNullOrWhiteSpace(Config.Ayakaleaf.AppName) ? "Ayakaleaf" : Config.Ayakaleaf.AppName.Trim().Replace("'", "");
        var conf =
            "sub_filter '</head>' '<script>" + js + "</script></head>';\n" +
            "sub_filter 'Overleaf Community Edition' '" + brand + "';\n" +
            "sub_filter 'DIGITAL SCIENCE' '" + brand + "';\n" +
            "sub_filter 'Digital Science' '" + brand + "';\n" +
            "sub_filter_once off;\n";
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(conf));
        var write = await WslcInterop.RunAsync(
            $"exec {OverleafContainer} sh -c \"echo {b64} | base64 -d > /etc/nginx/conf.d/ayaka-lang.conf\"", 30000);
        if (!write.IsSuccess)
        {
            LogWrapper.Warn($"[Ayakaleaf] 语言链接补丁写入失败：{write.ErrorText?.Trim()}");
            progress("警告：页脚语言切换可能跨主机丢失登录状态（补丁写入失败）");
            LanguageSwitchAvailable = false;
        }

        // 校验并重载。绝不回滚删除补丁文件：补丁内容为启动器生成的已知良好配置，
        // 容器启动期间 nginx -t 可能瞬时失败（服务未就绪），若据此回滚会把刚写好的
        // 补丁误删（历史缺陷）。reload 失败同样安全忽略——nginx 稍后启动时会读取
        // 已补丁的配置。
        var check = await WslcInterop.RunAsync(
            $"exec {OverleafContainer} sh -c \"nginx -t && nginx -s reload || true\"", 30000);
        if (!check.IsSuccess)
        {
            LogWrapper.Warn("[Ayakaleaf] nginx 配置重载失败（容器启动中属正常，稍后启动进程会读取新配置）");
        }
    }


    /// <summary>
    /// 重新自检界面语言切换能力（供打开工作台前调用）。
    /// 该状态是静态运行时结果，启动器重启后会被重置，而服务可能仍在运行，
    /// 因此显示“不可用”提示前需要重新确认一次（超时较短，避免拖慢界面）。
    /// </summary>
    public static Task RefreshLanguageSwitchAvailabilityAsync() => VerifyLanguageSwitchAsync(5000);

    /// <summary>
    /// 运行时自检界面语言切换能力（探活通过后执行一次）：
    /// 1. <c>/etc/nginx/sites-enabled/overleaf.conf</c> 的 <c>proxy_set_header Host</c> 行带
    ///    <c>$http_host</c>（默认的 <c>$host</c> 丢掉端口，web 服务无法按 Host 区分界面语言）；
    /// 2. <c>/etc/nginx/conf.d/ayaka-lang.conf</c> 存在（页脚语言链接主机保持补丁）。
    /// 任一不满足即视为不可用并告警；自检本身执行失败只告警，不影响 Running 状态。
    /// </summary>
    private static async Task VerifyLanguageSwitchAsync(int timeoutMs = 30000)
    {
        try
        {
            // 用 "." 通配 "$"，规避 wslc 参数传递链对 $ 的处理（与 sed 补丁写法一致）
            var hostPatch = await WslcInterop.RunAsync(
                $"exec {OverleafContainer} sh -c \"grep -q 'proxy_set_header Host .http_host' /etc/nginx/sites-enabled/overleaf.conf\"",
                timeoutMs);
            if (!hostPatch.IsSuccess)
            {
                LanguageSwitchAvailable = false;
                LogWrapper.Warn("[Ayakaleaf] 界面语言切换不可用：nginx 未转发完整 Host 头（缺少 $http_host 补丁）");
                return;
            }

            var langConf = await WslcInterop.RunAsync(
                $"exec {OverleafContainer} sh -c \"test -f /etc/nginx/conf.d/ayaka-lang.conf\"", timeoutMs);
            if (!langConf.IsSuccess)
            {
                LanguageSwitchAvailable = false;
                LogWrapper.Warn("[Ayakaleaf] 界面语言切换不可用：缺少语言链接主机保持补丁 ayaka-lang.conf");
                return;
            }

            LanguageSwitchAvailable = true;
            LogWrapper.Info("[Ayakaleaf] 界面语言切换自检通过：Host 头与语言补丁均已生效");
        }
        catch (Exception ex)
        {
            // 容器不可用等异常：保持不可用状态，但服务本身仍可处于 Running
            LanguageSwitchAvailable = false;
            LogWrapper.Warn(ex, "[Ayakaleaf] 界面语言切换自检失败");
        }
    }

    // ---------------- 符号面板拓展补丁 ----------------

    /// <summary>符号面板补丁脚本资源名（PCL.Core 嵌入资源，由 tools/ayakaleaf/generate-symbol-patch.py 生成）。</summary>
    private const string SymbolPatchResourceName = "PCL.Core.Ayakaleaf.Assets.symbol-patch.js";

    /// <summary>补丁内容发生变化、编辑器 HTTP 磁盘缓存需要清理（bundle 文件名不变，防内容陈旧）。</summary>
    private static volatile bool _symbolPatchChanged;

    /// <summary>
    /// 取走“符号面板补丁已更新，需清理编辑器缓存”标记（一次性；由工作台窗口在
    /// 创建 WebView2 后消费，清理磁盘缓存保证下次加载到补丁后的 bundle 内容）。
    /// </summary>
    public static bool ConsumeSymbolPatchCacheRefresh()
    {
        var changed = _symbolPatchChanged;
        _symbolPatchChanged = false;
        return changed;
    }

    /// <summary>
    /// 确保容器内符号面板拓展补丁生效（幂等，每次启动执行）：
    /// 对 Overleaf 前端 ide bundle 打数据补丁，符号面板拓展为 LaTeX-Workshop 的
    /// 10 个中文分类（合并原生 5 分类符号，共 763 个符号）。补丁脚本自带备份、
    /// 语法校验与从基线重放，失败仅告警不阻断启动（面板保持原生 5 分类）。
    /// </summary>
    private static async Task EnsureSymbolPalettePatchAsync(Action<string> progress)
    {
        var script = ReadSymbolPatchScript();
        if (string.IsNullOrEmpty(script))
        {
            LogWrapper.Warn("[Ayakaleaf] 未找到符号面板补丁脚本资源，符号面板保持原生状态");
            return;
        }

        // 脚本约 110KB，gzip + base64 后约 40KB。Windows CreateProcess 单条命令行
        // 上限 32K 字符（Linux 侧 128K 反而不受限），因此分块经多次 exec 追加写入，
        // 每块 2 万字符，最后统一解码执行。
        var raw = Encoding.UTF8.GetBytes(script);
        using var compressed = new MemoryStream();
        using (var gzip = new GZipStream(compressed, CompressionLevel.Fastest))
            gzip.Write(raw, 0, raw.Length);
        var payload = Convert.ToBase64String(compressed.ToArray());
        const int chunkSize = 20000;

        progress("应用符号面板拓展补丁…");
        for (var attempt = 0; ; attempt++)
        {
            var uploadFailed = false;
            for (var i = 0; i < payload.Length; i += chunkSize)
            {
                var chunk = payload.Substring(i, Math.Min(chunkSize, payload.Length - i));
                var op = i == 0 ? ">" : ">>";
                var up = await WslcInterop.RunAsync(
                    $"exec {OverleafContainer} sh -c \"echo {chunk} {op} /tmp/ayaka-symbol-patch.b64\"", 60000);
                if (!up.IsSuccess)
                {
                    uploadFailed = true;
                    break;
                }
            }
            if (uploadFailed)
            {
                if (attempt >= 2)
                {
                    LogWrapper.Warn("[Ayakaleaf] 符号面板拓展补丁上传失败（符号面板保持原生状态）");
                    progress("警告：符号面板拓展未生效（补丁上传失败，服务不受影响）");
                    return;
                }
                await Task.Delay(2000);
                continue;
            }

            var r = await WslcInterop.RunAsync(
                $"exec {OverleafContainer} sh -c \"base64 -d /tmp/ayaka-symbol-patch.b64 | gunzip > /tmp/ayaka-symbol-patch.js" +
                " && node /tmp/ayaka-symbol-patch.js\"", 120000);
            if (r.IsSuccess)
            {
                LogWrapper.Info("[Ayakaleaf] 符号面板拓展补丁已应用：" + r.Stdout.Trim());
                TryMarkSymbolPatchChanged(r.Stdout);
                return;
            }
            if (attempt >= 2)
            {
                // 补丁失败不阻断启动：符号面板回退为原生 5 分类
                LogWrapper.Warn($"[Ayakaleaf] 符号面板拓展补丁执行失败（符号面板保持原生状态）：{r.ErrorText?.Trim()}");
                progress("警告：符号面板拓展未生效（补丁执行失败，服务不受影响）");
                return;
            }
            await Task.Delay(2000);
        }
    }

    private static string? ReadSymbolPatchScript()
    {
        try
        {
            using var stream = typeof(AyakaleafService).Assembly.GetManifestResourceStream(SymbolPatchResourceName);
            if (stream is null) return null;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        }
        catch (Exception ex)
        {
            LogWrapper.Warn(ex, "[Ayakaleaf] 读取符号面板补丁脚本失败");
            return null;
        }
    }

    /// <summary>解析补丁脚本输出，内容变化时置位缓存清理标记（解析失败只影响缓存清理）。</summary>
    private static void TryMarkSymbolPatchChanged(string stdout)
    {
        try
        {
            using var doc = JsonDocument.Parse(stdout.Trim());
            if (doc.RootElement.TryGetProperty("changed", out var changed) &&
                changed.ValueKind == JsonValueKind.True)
                _symbolPatchChanged = true;
        }
        catch
        {
            // 输出非预期格式：仅跳过缓存清理标记
        }
    }

    // ---------------- 启停 ----------------

    /// <summary>
    /// 启动服务栈：MongoDB（含副本集初始化）→ Redis → Overleaf → 轮询探活。
    /// 容器不存在则创建，存在但停止则重新启动（数据卷内容保留）。
    /// </summary>
    public static async Task StartAsync(Action<string> progress, CancellationToken ct = default)
    {
        if (!WslcInterop.IsAvailable)
            throw new Exception("未找到 wslc.exe（WSL 容器工具），请将 WSL 升级到 3.0 或更高版本后重试");

        // 启动过程中语言端口尚不可达：先置不可用，探活通过后由自检刷新
        LanguageSwitchAvailable = false;

        await EnsureResourcesAsync(progress, ct);

        progress("启动 MongoDB…");
        await StartContainerAsync(MongoContainer,
            $"run -d --name {MongoContainer} --network {NetworkName} " +
            $"-v mongo_data:/data/db -e MONGO_INITDB_DATABASE=sharelatex {MongoImage} --replSet overleaf",
            progress);
        await InitMongoReplicaSetAsync(progress);

        progress("启动 Redis…");
        await StartContainerAsync(RedisContainer,
            $"run -d --name {RedisContainer} --network {NetworkName} -v redis_data:/data {RedisImage}",
            progress);

        progress("启动 Ayakaleaf…");
        // 期望网络配置：局域网开放时绑定 0.0.0.0 并以本机局域网 IP 作为站点地址，
        // 邀请链接、语言切换地址对队友直接可用；关闭时仅绑定本机回环。
        var secret = EnsureInviteTokenSecret();
        var lanIp = Config.Ayakaleaf.AllowLanAccess ? ResolveLanAddress() : "localhost";
        var bindHost = Config.Ayakaleaf.AllowLanAccess ? "0.0.0.0" : "127.0.0.1";
        var siteUrl = $"http://{lanIp}:{Config.Ayakaleaf.Port}";

        // 功能环境变量（注册策略 + 各 Pro 功能开关）与功能指纹：
        // 任一功能配置变化 → 指纹变化 → 容器签名漂移 → 自动重建容器（数据卷不受影响）
        var featureEnv = new List<KeyValuePair<string, string>>(BuildRegistrationEnvironment());
        featureEnv.AddRange(BuildFeatureEnvironment());
        var fingerprint = ComputeFeatureFingerprint(featureEnv);

        // Git-Bridge（启用时）：随站点地址重建的无状态容器，数据在 git_bridge_data 卷中
        if (Config.Ayakaleaf.EnableGitBridge)
        {
            progress("启动 Git-Bridge…");
            await WslcInterop.RunAsync($"stop \"{GitBridgeContainer}\"", 60000);
            await WslcInterop.RunAsync($"rm \"{GitBridgeContainer}\"", 60000);
            var gb = await WslcInterop.RunAsync(
                $"run -d --name {GitBridgeContainer} --network {NetworkName} " +
                $"-v {GitBridgeVolume}:/data/git-bridge " +
                "-e GIT_BRIDGE_PORT=8000 " +
                $"-e GIT_BRIDGE_API_BASE_URL=http://{OverleafContainer}:3000/api/v0 " +
                $"-e GIT_BRIDGE_OAUTH2_SERVER=http://{OverleafContainer}:3000 " +
                $"-e GIT_BRIDGE_POSTBACK_BASE_URL=http://{GitBridgeContainer}:8000 " +
                "-e GIT_BRIDGE_ROOT_DIR=/data/git-bridge " +
                $"-e GIT_BRIDGE_ALLOWED_CORS_ORIGINS={siteUrl} " +
                GitBridgeImage, 300000);
            if (!gb.IsSuccess)
                throw new Exception($"创建容器 {GitBridgeContainer} 失败（请确认已在镜像页导入 git-bridge 镜像包）：" + gb.ErrorText);
        }
        else if (await ContainerExistsAsync(GitBridgeContainer))
        {
            // 已关闭 Git-Bridge：清理遗留容器（数据卷保留，重新启用不受影响）
            await WslcInterop.RunAsync($"stop \"{GitBridgeContainer}\"", 60000);
            await WslcInterop.RunAsync($"rm \"{GitBridgeContainer}\"", 60000);
        }

        var langMapping = BuildLangMapping(lanIp);
        // 多语言端口分配：en=主端口，其后每语言 +1（默认 en,zh-CN → 主端口,主端口+1，与历史一致）
        var languagePorts = GetEnabledLanguages().Select((_, i) => Config.Ayakaleaf.Port + i).ToList();
        var profile = $"{bindHost}:{string.Join(",", languagePorts)}|{siteUrl}|{secret}|{langMapping}|{fingerprint}";

        // 配置漂移（端口、局域网开关、本机 IP、邀请密钥、功能配置变化）→ 删除重建容器；数据卷不受影响
        if (Config.Ayakaleaf.ContainerProfile != profile && await ContainerExistsAsync(OverleafContainer))
        {
            progress("配置已变化，正在重建 Ayakaleaf 容器…");
            // 容器可能已在运行：先停后删；已停止时结果忽略
            await WslcInterop.RunAsync($"stop \"{OverleafContainer}\"", 60000);
            var rm = await WslcInterop.RunAsync($"rm \"{OverleafContainer}\"", 60000);
            if (!rm.IsSuccess)
                throw new Exception("配置已变化，但旧容器无法删除（重建失败）：" + rm.ErrorText);
        }

        // 功能环境变量逐项追加（值统一去引号并整体加引号，兼容含空格的取值）
        var envArgs = new StringBuilder();
        foreach (var kv in featureEnv)
        {
            var value = kv.Value.Replace("\"", "");
            envArgs.Append($"-e \"{kv.Key}={value}\" ");
        }

        var created = await StartContainerAsync(OverleafContainer,
            $"run -d --name {OverleafContainer} --network {NetworkName} " +
            string.Join(" ", languagePorts.Select(p => $"-p {bindHost}:{p}:80")) + " " +
            "-v overleaf_data:/var/lib/overleaf/data -v overleaf_logs:/var/log/overleaf " +
            $"-e OVERLEAF_INVITE_TOKEN_SECRET={secret} " +
            "-e OVERLEAF_MONGO_URL=mongodb://mongo/sharelatex " +
            "-e OVERLEAF_REDIS_HOST=redis -e REDIS_HOST=redis -e OVERLEAF_REDIS_PORT=6379 " +
            // 本地实例未配置 SMTP，确认邮件无法发送：
            // 禁用邮箱确认，隐藏项目页“请确认您的主要电子邮件地址”横幅。
            "-e EMAIL_CONFIRMATION_DISABLED=true " +
            $"{envArgs}" +
            $"-e OVERLEAF_SITE_URL={siteUrl} " +
            // 界面语言注册（镜像内置 18 语资源，按 EnabledLanguages 启用）：
            // en=主端口，其余语言依次 +1，页脚语言选择器可互相切换。
            // JSON 含引号，经 --env-file 传递以避开命令行转义问题。
            $"--env-file \"{WriteOverleafLanguageEnvFile(lanIp)}\" " +
            EffectiveOverleafImage,
            progress);
        if (created) Config.Ayakaleaf.ContainerProfile = profile;
        await EnsureNginxPatchesAsync(progress);
        await EnsureSymbolPalettePatchAsync(progress);

        progress("等待服务就绪…");
        for (var i = 0; i < StartProbeTimeoutSeconds / 2; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (await ProbeAsync())
            {
                // 探活只证明主端口可访问，语言切换另需确认 nginx 补丁真的落地
                await VerifyLanguageSwitchAsync();
                return;
            }
            await Task.Delay(2000, ct);
        }
        LanguageSwitchAvailable = false;
        throw new Exception($"等待服务就绪超时（{StartProbeTimeoutSeconds} 秒），请查看服务日志");
    }

    /// <summary>
    /// 启动单只容器：运行中跳过；已存在则启动；否则按参数创建。
    /// 返回是否新建了容器。
    /// </summary>
    private static async Task<bool> StartContainerAsync(string name, string runArguments, Action<string> progress)
    {
        if (await IsContainerRunningAsync(name)) return false;
        if (await ContainerExistsAsync(name))
        {
            progress($"启动既有容器 {name}…");
            var start = await WslcInterop.RunAsync($"start \"{name}\"", 120000);
            if (!start.IsSuccess)
                throw new Exception($"启动容器 {name} 失败：{start.ErrorText}");
            return false;
        }
        var run = await WslcInterop.RunAsync(runArguments, 300000);
        if (!run.IsSuccess)
            throw new Exception($"创建容器 {name} 失败：{run.ErrorText}");
        return true;
    }

    /// <summary>
    /// 确认 MongoDB 副本集就绪（事务支持所需）。
    /// 幂等：副本集已初始化时直接通过。
    /// </summary>
    private static async Task InitMongoReplicaSetAsync(Action<string> progress)
    {
        progress("初始化 MongoDB 副本集…");
        // 引号在 C# 侧直接传给 wslc.exe，无 shell 中间层；JS 内层用单引号
        const string helloJs = "db.hello().isWritablePrimary";
        const string initiateJs =
            "try { rs.status().ok } catch (e) { rs.initiate({_id:'overleaf',members:[{_id:0,host:'mongo:27017'}]}) }";

        for (var i = 0; i < 45; i++)
        {
            var hello = await WslcInterop.RunAsync(
                $"exec {MongoContainer} mongosh --quiet --eval \"{helloJs}\"", 30000);
            if (hello.IsSuccess && hello.Stdout.Contains("true"))
                return; // 已是 PRIMARY（既有副本集随容器恢复）

            if (i == 0)
            {
                // mongo 首次可连接后发起副本集初始化（已初始化则 rs.status 正常返回）
                await WslcInterop.RunAsync(
                    $"exec {MongoContainer} mongosh --quiet --eval \"{initiateJs}\"", 60000);
            }
            await Task.Delay(2000);
        }

        throw new Exception("MongoDB 副本集未能在时限内就绪，请查看服务日志");
    }

    /// <summary>
    /// 将指定邮箱的 Ayakaleaf 服务端账号开通为管理员（幂等）：
    /// 在 mongo 容器内执行 mongosh，对 sharelatex.users 执行
    /// <c>updateOne({email},{$set:{isAdmin:true}})</c>（字段名对照 ayakaleaf-pro User 模型）。
    /// </summary>
    /// <param name="email">目标账号邮箱。</param>
    /// <returns>是否开通成功（mongo 容器不可达、mongosh 失败或未匹配到账号时返回 false，不抛异常）。</returns>
    public static async Task<bool> TryGrantServerAdminAsync(string email)
    {
        if (string.IsNullOrWhiteSpace(email)) return false;

        // 引号在 C# 侧直接传给 wslc.exe，无 shell 中间层；JS 内层用单引号。
        // 邮箱由账号服务生成（[a-z0-9._-]），仍转义单引号/反斜杠防御异常数据
        var safeEmail = email.Replace("\\", "\\\\").Replace("'", "\\'");
        var js = $"db.getSiblingDB('sharelatex').users" +
                 $".updateOne({{email:'{safeEmail}'}},{{$set:{{isAdmin:true}}}})";

        var r = await WslcInterop.RunAsync(
            $"exec {MongoContainer} mongosh --quiet --eval \"{js}\"", 30000);
        if (!r.IsSuccess)
        {
            LogWrapper.Warn($"[Ayakaleaf] 开通服务端管理员失败（{email}）：{r.ErrorText.Trim()}");
            return false;
        }

        // mongosh 会输出 updateOne 结果（matchedCount 等）：未匹配到账号视为开通失败
        if (!Regex.IsMatch(r.Stdout, @"matchedCount\s*:\s*1"))
        {
            LogWrapper.Warn($"[Ayakaleaf] 开通服务端管理员失败（{email}）：服务端不存在该账号（{r.Stdout.Trim()}）");
            return false;
        }

        return true;
    }

    /// <summary>
    /// 停止服务栈（容器保留，数据卷不受影响；重新启动时复用容器）。
    /// </summary>
    public static async Task StopAsync(Action<string> progress, CancellationToken ct = default)
    {
        progress("正在停止 Ayakaleaf 服务栈…");
        try
        {
            foreach (var name in new[] { OverleafContainer, GitBridgeContainer, RedisContainer, MongoContainer })
            {
                ct.ThrowIfCancellationRequested();
                if (!await ContainerExistsAsync(name)) continue;
                var stop = await WslcInterop.RunAsync($"stop \"{name}\"", 120000);
                if (!stop.IsSuccess)
                    LogWrapper.Info($"[Ayakaleaf] 停止容器 {name} 返回 exit={stop.ExitCode}（可能已停止）：{stop.ErrorText?.Trim()}");
            }
        }
        finally
        {
            // 服务已停止（或停止过程被取消）：语言端口不再可达，切换能力随之失效
            LanguageSwitchAvailable = false;
        }
    }

    // ---------------- 镜像 ----------------

    /// <summary>
    /// 加载镜像 tarball（docker save 存档格式；重操作，30 分钟超时）。
    /// </summary>
    public static async Task ImportImageAsync(string tarPath, CancellationToken ct = default)
    {
        var r = await WslcInterop.RunAsync($"load -i \"{tarPath}\"", 1800000);
        if (!r.IsSuccess)
            throw new Exception($"导入镜像 {Path.GetFileName(tarPath)} 失败：{r.ErrorText}");
    }

    /// <summary>
    /// 删除镜像。
    /// </summary>
    public static async Task RemoveImageAsync(string repository, string tag)
    {
        var r = await WslcInterop.RunAsync($"rmi \"{repository}:{tag}\"", 120000);
        if (!r.IsSuccess)
            throw new Exception("删除镜像失败：" + r.ErrorText);
    }

    // ---------------- 备份/恢复 ----------------

    /// <summary>
    /// 取数据卷的 VHD 文件路径（volume inspect Status.HostPath）。
    /// </summary>
    public static async Task<string> GetVolumeHostPathAsync(string volume)
    {
        var r = await WslcInterop.RunAsync($"volume inspect \"{volume}\"", 30000);
        if (!r.IsSuccess)
            throw new Exception($"数据卷 {volume} 不存在或无法访问：{r.ErrorText}");
        string? hostPath = null;
        try
        {
            using var doc = JsonDocument.Parse(r.Stdout);
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.TryGetProperty("Status", out var status) &&
                    status.TryGetProperty("HostPath", out var path))
                    hostPath = path.GetString();
            }
        }
        catch (Exception ex)
        {
            throw new Exception($"解析数据卷 {volume} 信息失败：" + ex.Message);
        }
        return string.IsNullOrEmpty(hostPath)
            ? throw new Exception($"解析数据卷 {volume} 信息失败：未返回存储路径")
            : hostPath;
    }

    /// <summary>
    /// 备份数据卷：复制卷的 VHD 文件到目标目录（服务运行中也可备份，
    /// 但建议先停止服务栈以保证数据一致性）。
    /// </summary>
    public static async Task<string> BackupVolumeAsync(string volume, string destDir, CancellationToken ct = default)
    {
        Directory.CreateDirectory(destDir);
        var hostPath = await GetVolumeHostPathAsync(volume);
        var destFile = Path.Combine(destDir, $"{volume}.{DateTime.Now:yyyyMMdd-HHmmss}.vhdx");
        ct.ThrowIfCancellationRequested();
        await Task.Run(() => File.Copy(hostPath, destFile, overwrite: false), ct);
        return destFile;
    }

    /// <summary>
    /// 恢复数据卷：用备份的 VHD 文件覆盖卷存储。要求服务栈处于停止状态。
    /// </summary>
    public static async Task RestoreVolumeAsync(string volume, string vhdxPath, CancellationToken ct = default)
    {
        if (!DataVolumes.Contains(volume) && volume != GitBridgeVolume)
            throw new Exception($"未知的数据卷 {volume}");
        foreach (var name in new[] { MongoContainer, RedisContainer, OverleafContainer, GitBridgeContainer })
        {
            if (await IsContainerRunningAsync(name))
                throw new Exception($"恢复前请先停止服务栈（容器 {name} 仍在运行）");
        }
        var hostPath = await GetVolumeHostPathAsync(volume);
        ct.ThrowIfCancellationRequested();
        await Task.Run(() => File.Copy(vhdxPath, hostPath, overwrite: true), ct);
    }

    // ---------------- 其他 ----------------

    /// <summary>
    /// 打开 Ayakaleaf 站点（系统默认浏览器）。
    /// </summary>
    public static void OpenSite()
    {
        Process.Start(new ProcessStartInfo(BaseUrl) { UseShellExecute = true });
    }

    // ---------------- 功能速览 ----------------

    /// <summary>
    /// 列出当前配置下实际生效的功能名（与 <see cref="BuildFeatureEnvironment"/> 的
    /// 门控条件一致：开关开启且必要凭据非空），供服务台状态速览展示。
    /// </summary>
    public static List<string> GetEnabledFeatureNames()
    {
        var names = new List<string>();
        var cfg = Config.Ayakaleaf;

        if (cfg.EnableGitBridge) names.Add("Git-Bridge / MCP");
        if (cfg.EnableAi && !string.IsNullOrWhiteSpace(cfg.AiBaseUrl) &&
            !string.IsNullOrWhiteSpace(cfg.AiApiKey) && !string.IsNullOrWhiteSpace(cfg.AiModel))
            names.Add("AI 助手");
        if (cfg.EnableTemplateGallery) names.Add("模板画廊");
        if (cfg.EnableGithubSync && cfg.EnableGitBridge &&
            !string.IsNullOrWhiteSpace(cfg.GithubClientId) && !string.IsNullOrWhiteSpace(cfg.GithubClientSecret))
            names.Add("GitHub 同步");
        if (cfg.EnableGitlabSync && cfg.EnableGitBridge &&
            !string.IsNullOrWhiteSpace(cfg.GitlabClientId) && !string.IsNullOrWhiteSpace(cfg.GitlabClientSecret))
            names.Add("GitLab 同步");
        if (cfg.EnableGiteaSync && cfg.EnableGitBridge &&
            !string.IsNullOrWhiteSpace(cfg.GiteaClientId) && !string.IsNullOrWhiteSpace(cfg.GiteaClientSecret))
            names.Add("Gitea 同步");
        if (cfg.EnableZotero && !string.IsNullOrWhiteSpace(cfg.ZoteroClientId) &&
            !string.IsNullOrWhiteSpace(cfg.ZoteroClientSecret))
            names.Add("Zotero");
        if (cfg.EnableMendeley && !string.IsNullOrWhiteSpace(cfg.MendeleyClientId) &&
            !string.IsNullOrWhiteSpace(cfg.MendeleyClientSecret))
            names.Add("Mendeley");
        if (cfg.EnableRecaptcha && !string.IsNullOrWhiteSpace(cfg.RecaptchaSiteKey) &&
            !string.IsNullOrWhiteSpace(cfg.RecaptchaSecretKey))
            names.Add("reCAPTCHA");
        if (cfg.EnableHibp) names.Add("泄露密码检查");
        if (!string.IsNullOrWhiteSpace(cfg.AppName) || !string.IsNullOrWhiteSpace(cfg.NavTitle) ||
            !string.IsNullOrWhiteSpace(cfg.HeaderImageUrl) || !string.IsNullOrWhiteSpace(cfg.HeaderImageUrlLight) ||
            !string.IsNullOrWhiteSpace(cfg.HeaderImageUrlDark))
            names.Add("品牌化");
        var langs = GetEnabledLanguages();
        if (langs.Count > 2) names.Add($"界面语言 ×{langs.Count}");
        return names;
    }

    // ---------------- 全量备份/恢复 ----------------

    /// <summary>
    /// 全量备份：4(+1) 数据卷 VHD + Ayakaleaf 账号凭据 + 本地用户库（users.db，含 WAL 边车）
    /// + 启动器配置（共享加密配置 + 本地配置），打包为单个带时间戳的 zip。
    /// 服务运行中也可备份，但建议先停止服务栈以保证数据一致性。
    /// </summary>
    /// <returns>生成的 zip 文件路径。</returns>
    public static async Task<string> CreateFullBackupAsync(Action<string> progress, CancellationToken ct = default)
    {
        var destFile = Path.Combine(BackupDirectory, $"ayakaleaf-full-backup-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        Directory.CreateDirectory(BackupDirectory);
        var staging = Path.Combine(Path.GetTempPath(), $"ayakaleaf-full-backup-{DateTime.Now:yyyyMMdd-HHmmss-fff}");
        try
        {
            Directory.CreateDirectory(staging);

            // 1. 数据卷 VHD（运行中备份可用，一致性以停止服务栈为宜）
            var volumesDir = Path.Combine(staging, "volumes");
            Directory.CreateDirectory(volumesDir);
            foreach (var volume in ActiveDataVolumes)
            {
                ct.ThrowIfCancellationRequested();
                progress($"正在备份数据卷 {volume}…");
                var hostPath = await GetVolumeHostPathAsync(volume);
                await Task.Run(() => File.Copy(hostPath, Path.Combine(volumesDir, $"{volume}.vhdx"), overwrite: false), ct);
            }

            // 2. Ayakaleaf 账号凭据
            if (Directory.Exists(AyakaleafAccountService.AccountsDirectory))
            {
                ct.ThrowIfCancellationRequested();
                progress("正在备份账号凭据…");
                var accountsDir = Path.Combine(staging, "accounts");
                await Task.Run(() => CopyDirectoryRecursive(AyakaleafAccountService.AccountsDirectory, accountsDir), ct);
            }

            // 3. 本地用户库（users.db 为 WAL 模式，-wal/-shm 边车一并打包）
            var usersDb = Path.Combine(Paths.Data, "users.db");
            if (File.Exists(usersDb))
            {
                ct.ThrowIfCancellationRequested();
                progress("正在备份本地用户库…");
                await Task.Run(() =>
                {
                    File.Copy(usersDb, Path.Combine(staging, "users.db"), overwrite: true);
                    foreach (var suffix in new[] { "-wal", "-shm" })
                    {
                        var sidecar = usersDb + suffix;
                        if (File.Exists(sidecar))
                            File.Copy(sidecar, Path.Combine(staging, "users.db" + suffix), overwrite: true);
                    }
                }, ct);
            }

            // 4. 启动器配置（共享加密配置含全部 Ayakaleaf 功能开关与密钥）
            progress("正在备份启动器配置…");
            var configDir = Path.Combine(staging, "config");
            Directory.CreateDirectory(configDir);
            if (File.Exists(ConfigService.SharedConfigPath))
                File.Copy(ConfigService.SharedConfigPath, Path.Combine(configDir, "config.v1.json"), overwrite: true);
            if (File.Exists(ConfigService.LocalConfigPath))
                File.Copy(ConfigService.LocalConfigPath, Path.Combine(configDir, "config.v1.yml"), overwrite: true);

            // 5. 打包为单 zip（VHD 内多为不可压缩数据，Fastest 避免长时间 CPU 压缩）
            progress("正在打包全量备份…");
            await Task.Run(() => ZipFile.CreateFromDirectory(staging, destFile, CompressionLevel.Fastest, includeBaseDirectory: false), ct);
        }
        finally
        {
            try { Directory.Delete(staging, recursive: true); } catch { /* 临时目录清理失败可忽略 */ }
        }
        return destFile;
    }

    /// <summary>
    /// 全量恢复：解包 <see cref="CreateFullBackupAsync"/> 生成的 zip，将数据卷、账号凭据、
    /// 本地用户库与启动器配置还原到对应位置。要求服务栈处于停止状态；
    /// 恢复配置与用户数据后建议立即重启启动器使内存状态与新文件一致。
    /// </summary>
    public static async Task RestoreFullBackupAsync(string zipPath, Action<string> progress, CancellationToken ct = default)
    {
        if (!File.Exists(zipPath)) throw new FileNotFoundException("备份文件不存在：" + zipPath);
        foreach (var name in new[] { MongoContainer, RedisContainer, OverleafContainer, GitBridgeContainer })
        {
            if (await IsContainerRunningAsync(name))
                throw new Exception($"恢复前请先停止服务栈（容器 {name} 仍在运行）");
        }

        var staging = Path.Combine(Path.GetTempPath(), $"ayakaleaf-full-restore-{DateTime.Now:yyyyMMdd-HHmmss-fff}");
        try
        {
            progress("正在解包备份…");
            await Task.Run(() => ZipFile.ExtractToDirectory(zipPath, staging), ct);

            // 1. 数据卷 VHD（卷名白名单校验，防止恢复未知卷覆盖其他数据）
            var volumesDir = Path.Combine(staging, "volumes");
            if (Directory.Exists(volumesDir))
            {
                foreach (var vhdx in Directory.GetFiles(volumesDir, "*.vhdx"))
                {
                    ct.ThrowIfCancellationRequested();
                    var volume = Path.GetFileNameWithoutExtension(vhdx);
                    if (!DataVolumes.Contains(volume) && volume != GitBridgeVolume)
                        throw new Exception($"备份中包含未知的数据卷 {volume}，已中止恢复");
                    progress($"正在恢复数据卷 {volume}…");
                    var hostPath = await GetVolumeHostPathAsync(volume);
                    await Task.Run(() => File.Copy(vhdx, hostPath, overwrite: true), ct);
                }
            }

            // 2. 账号凭据（整体替换：先清空现有凭据目录再复制）
            var accountsDir = Path.Combine(staging, "accounts");
            if (Directory.Exists(accountsDir))
            {
                ct.ThrowIfCancellationRequested();
                progress("正在恢复账号凭据…");
                await Task.Run(() =>
                {
                    var target = AyakaleafAccountService.AccountsDirectory;
                    if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
                    CopyDirectoryRecursive(accountsDir, target);
                }, ct);
            }

            // 3. 本地用户库（连接池可能持有文件句柄，先清池；WAL 边车一并还原）
            var usersDb = Path.Combine(staging, "users.db");
            if (File.Exists(usersDb))
            {
                ct.ThrowIfCancellationRequested();
                progress("正在恢复本地用户库…");
                await Task.Run(() =>
                {
                    SqliteConnection.ClearAllPools();
                    var target = Path.Combine(Paths.Data, "users.db");
                    File.Copy(usersDb, target, overwrite: true);
                    foreach (var suffix in new[] { "-wal", "-shm" })
                    {
                        var sourceSidecar = usersDb + suffix;
                        var targetSidecar = target + suffix;
                        if (File.Exists(sourceSidecar)) File.Copy(sourceSidecar, targetSidecar, overwrite: true);
                        else if (File.Exists(targetSidecar)) File.Delete(targetSidecar);
                    }
                }, ct);
            }

            // 4. 启动器配置
            progress("正在恢复启动器配置…");
            var configDir = Path.Combine(staging, "config");
            var sharedConfig = Path.Combine(configDir, "config.v1.json");
            var localConfig = Path.Combine(configDir, "config.v1.yml");
            if (File.Exists(sharedConfig))
                File.Copy(sharedConfig, ConfigService.SharedConfigPath, overwrite: true);
            if (File.Exists(localConfig))
                File.Copy(localConfig, ConfigService.LocalConfigPath, overwrite: true);
        }
        finally
        {
            try { Directory.Delete(staging, recursive: true); } catch { /* 临时目录清理失败可忽略 */ }
        }
    }

    /// <summary>递归复制目录（含所有子目录与文件，覆盖同名目标）。</summary>
    private static void CopyDirectoryRecursive(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.GetFiles(sourceDir))
            File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)), overwrite: true);
        foreach (var dir in Directory.GetDirectories(sourceDir))
            CopyDirectoryRecursive(dir, Path.Combine(destDir, Path.GetFileName(dir)));
    }
}
