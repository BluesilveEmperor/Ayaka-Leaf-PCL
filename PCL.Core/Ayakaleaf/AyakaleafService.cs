using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PCL.Core.App;
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

    public const string MongoImage = "mongo:8.0";
    public const string RedisImage = "redis:7";
    public const string OverleafImage = "sharelatex/sharelatex:server-pro";

    /// <summary>简体中文界面使用的端口（主端口 +1，同一容器映射双端口实现语言切换）。</summary>
    public static int ChinesePort => Config.Ayakaleaf.Port + 1;

    /// <summary>
    /// 界面语言切换（端口映射 + nginx Host/链接补丁）是否已确认可用。
    /// 这是运行时自检结果而非部署配置：服务处于 Running 并不代表语言切换可用。
    /// </summary>
    public static bool LanguageSwitchAvailable { get; private set; }

    /// <summary>服务栈数据卷（独立 VHD 文件）。</summary>
    public static readonly string[] DataVolumes = { "mongo_data", "redis_data", "overleaf_data", "overleaf_logs" };

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
    /// 按界面语言偏好返回工作台入口 URL（"zh" = 简体中文端口，其余 = 主端口 English）。
    /// </summary>
    public static string BaseUrlForLanguage(string lang) =>
        lang == "zh" ? $"http://localhost:{ChinesePort}" : BaseUrl;

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
                    !name.Equals(OverleafContainer, StringComparison.Ordinal))
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
        foreach (var volume in DataVolumes)
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
        foreach (var volume in DataVolumes)
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
    /// 构造 Overleaf 语言域名映射 JSON（两种界面语言绑定到同容器的两个端口）。
    /// 局域网开放时额外登记 hide 的 localhost 条目：语言选择器展示可分享的
    /// 局域网地址，而本机（启动器工作台等）经 localhost 访问仍能正确识别语言。
    /// </summary>
    private static string BuildLangMapping(string host)
    {
        var main =
            "{\"www\":{\"lngCode\":\"en\",\"url\":\"http://" + host + ":" + Config.Ayakaleaf.Port + "\"}," +
            "\"zh\":{\"lngCode\":\"zh-CN\",\"url\":\"http://" + host + ":" + ChinesePort + "\"}";
        if (host == "localhost") return main + "}";
        return main + "," +
            "\"www-local\":{\"lngCode\":\"en\",\"url\":\"http://localhost:" + Config.Ayakaleaf.Port + "\",\"hide\":true}," +
            "\"zh-local\":{\"lngCode\":\"zh-CN\",\"url\":\"http://localhost:" + ChinesePort + "\",\"hide\":true}}";
    }

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

        // 语言链接主机保持补丁：写入 conf.d（nginx.conf 已 include），内容经
        // base64 传递以避开多层引号转义问题
        var js =
            "(function(){var ayakaPorts=[\"" + Config.Ayakaleaf.Port + "\",\"" + ChinesePort + "\"];" +
            "function fix(a){try{var u=new URL(a.href);" +
            "if(u.hostname===location.hostname||ayakaPorts.indexOf(u.port)<0)return;" +
            "u.hostname=location.hostname;a.href=u.href}catch(e){}}" +
            "function run(){for(var l=document.links,i=0;i<l.length;i++)fix(l[i])}" +
            "var q=false;function s(){if(q)return;q=true;" +
            "requestAnimationFrame(function(){q=false;run()})}" +
            "new MutationObserver(s).observe(document.documentElement,{childList:true,subtree:true});s()})()";
        var conf = "sub_filter '</head>' '<script>" + js + "</script></head>';\n";
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(conf));
        var write = await WslcInterop.RunAsync(
            $"exec {OverleafContainer} sh -c \"echo {b64} | base64 -d > /etc/nginx/conf.d/ayaka-lang.conf\"", 30000);
        if (!write.IsSuccess)
        {
            LogWrapper.Warn($"[Ayakaleaf] 语言链接补丁写入失败：{write.ErrorText?.Trim()}");
            progress("警告：页脚语言切换可能跨主机丢失登录状态（补丁写入失败）");
            LanguageSwitchAvailable = false;
        }

        // 校验后重载：校验失败则回滚补丁文件再重载，保证站点不被坏配置卡死。
        // nginx 可能尚未随容器启动完成，重载失败可忽略（启动时会读取已补丁的配置）
        var check = await WslcInterop.RunAsync(
            $"exec {OverleafContainer} sh -c " +
            "\"nginx -t && nginx -s reload || (rm -f /etc/nginx/conf.d/ayaka-lang.conf; nginx -s reload)\"",
            30000);
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
        var langMapping = BuildLangMapping(lanIp);
        var profile = $"{bindHost}:{Config.Ayakaleaf.Port},{ChinesePort}|{siteUrl}|{secret}|{langMapping}";

        // 配置漂移（端口、局域网开关、本机 IP、邀请密钥变化）→ 删除重建容器；数据卷不受影响
        if (Config.Ayakaleaf.ContainerProfile != profile && await ContainerExistsAsync(OverleafContainer))
        {
            progress("网络配置已变化，正在重建 Ayakaleaf 容器…");
            // 容器可能已在运行：先停后删；已停止时结果忽略
            await WslcInterop.RunAsync($"stop \"{OverleafContainer}\"", 60000);
            var rm = await WslcInterop.RunAsync($"rm \"{OverleafContainer}\"", 60000);
            if (!rm.IsSuccess)
                throw new Exception("网络配置已变化，但旧容器无法删除（重建失败）：" + rm.ErrorText);
        }

        var created = await StartContainerAsync(OverleafContainer,
            $"run -d --name {OverleafContainer} --network {NetworkName} " +
            $"-p {bindHost}:{Config.Ayakaleaf.Port}:80 -p {bindHost}:{ChinesePort}:80 " +
            "-v overleaf_data:/var/lib/overleaf/data -v overleaf_logs:/var/log/overleaf " +
            $"-e OVERLEAF_INVITE_TOKEN_SECRET={secret} " +
            "-e OVERLEAF_MONGO_URL=mongodb://mongo/sharelatex " +
            "-e OVERLEAF_REDIS_HOST=redis -e REDIS_HOST=redis -e OVERLEAF_REDIS_PORT=6379 " +
            "-e OVERLEAF_ALLOW_PUBLIC_REGISTRATION=true " +
            // 本地实例未配置 SMTP，确认邮件无法发送：
            // 禁用邮箱确认，隐藏项目页“请确认您的主要电子邮件地址”横幅。
            "-e EMAIL_CONFIRMATION_DISABLED=true " +
            $"-e OVERLEAF_SITE_URL={siteUrl} " +
            // 界面语言注册（镜像内置 en / zh-CN 双语资源）：
            // 英文=主端口，简体中文=主端口+1，页脚语言选择器可互相切换。
            // JSON 含引号，经 --env-file 传递以避开命令行转义问题。
            $"--env-file \"{WriteOverleafLanguageEnvFile(lanIp)}\" " +
            OverleafImage,
            progress);
        if (created) Config.Ayakaleaf.ContainerProfile = profile;
        await EnsureNginxPatchesAsync(progress);

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
    /// 停止服务栈（容器保留，数据卷不受影响；重新启动时复用容器）。
    /// </summary>
    public static async Task StopAsync(Action<string> progress, CancellationToken ct = default)
    {
        progress("正在停止 Ayakaleaf 服务栈…");
        try
        {
            foreach (var name in new[] { OverleafContainer, RedisContainer, MongoContainer })
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
        if (!DataVolumes.Contains(volume))
            throw new Exception($"未知的数据卷 {volume}");
        foreach (var name in new[] { MongoContainer, RedisContainer, OverleafContainer })
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
}
