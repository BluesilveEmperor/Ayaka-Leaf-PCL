using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using PCL.Core.App;
using PCL.Core.Logging;

namespace PCL.Core.Ayakaleaf.Collab;

/// <summary>协作角色。</summary>
public enum CollabRole
{
    /// <summary>未参与协作。</summary>
    None,

    /// <summary>房主：本机服务对队友开放。</summary>
    Host,

    /// <summary>队友：接入房主的协作网络。</summary>
    Guest,
}

/// <summary>协作会话阶段。</summary>
public enum CollabStage
{
    /// <summary>空闲。</summary>
    Idle,

    /// <summary>正在开启/加入（StatusText 描述当前步骤）。</summary>
    Working,

    /// <summary>就绪：协作进行中。</summary>
    Ready,

    /// <summary>失败（StatusText 为原因）。</summary>
    Failed,
}

/// <summary>协作相关的用户可见错误。</summary>
public sealed class CollabException(string message) : Exception(message);

/// <summary>一条本地端口转发：本机 LocalPort → 房主 RemotePort。</summary>
public sealed record CollabPortForward(int LocalPort, int RemotePort);

/// <summary>访客工作台可用的界面语言（按转发端口探测）。</summary>
public sealed record CollabGuestLanguage(string Code, string Name, int LocalPort);

/// <summary>协作网络中的成员。</summary>
public sealed record CollabMemberInfo(string Hostname, string IPv4, bool IsDirect, double? LatencyMs, string Tunnel);

/// <summary>
/// 远程协作会话（单实例）：房主开放本机 Overleaf 服务、队友经虚拟网络接入。
/// 发现协议（零信令服务）：
/// 房主 hostname 编码本机端口结构 <c>ayaka-host-&lt;主端口&gt;-&lt;语言数&gt;</c>，
/// 队友 hostname 为 <c>ayaka-guest-&lt;8 位十六进制&gt;</c>，
/// 队友扫描 peer 列表找到房主并解码端口表，逐端口建立本地转发（本地沿用同端口号，冲突时自动改用空闲端口）。
/// </summary>
public sealed partial class CollabSession : IDisposable
{
    /// <summary>房主 hostname 前缀（后接 主端口-语言数）。</summary>
    public const string HostPrefix = "ayaka-host-";

    /// <summary>队友 hostname 前缀（后接 8 位随机十六进制）。</summary>
    public const string GuestPrefix = "ayaka-guest-";

    private const int HostFindTimeoutSeconds = 30;
    private const int NodeConnectTimeoutSeconds = 10;

    private static readonly HttpClient ProbeClient = new() { Timeout = TimeSpan.FromSeconds(3) };

    private readonly SynchronizationContext? _uiContext = SynchronizationContext.Current;

    private EasyTierInstance? _instance;
    private CancellationTokenSource? _operationCts;
    private CancellationTokenSource? _monitorCts;
    private int _disposed;

    /// <summary>会话状态变化（回调已封送到创建会话的 UI 线程）。</summary>
    public event Action? Changed;

    /// <summary>当前角色。</summary>
    public CollabRole Role { get; private set; } = CollabRole.None;

    /// <summary>当前阶段。</summary>
    public CollabStage Stage { get; private set; } = CollabStage.Idle;

    /// <summary>当前状态描述（步骤进度或失败原因）。</summary>
    public string StatusText { get; private set; } = "";

    /// <summary>邀请码（房主生成 / 队友加入后回填，规范形式 A/XXXX-XXXX-XXXX-XXXX）。</summary>
    public string? InviteCode { get; private set; }

    /// <summary>队友视角：已建立的端口转发列表。</summary>
    public IReadOnlyList<CollabPortForward> Forwards { get; private set; } = [];

    /// <summary>队友视角：访客工作台可用的界面语言。</summary>
    public IReadOnlyList<CollabGuestLanguage> GuestLanguages { get; private set; } = [];

    /// <summary>网络中的其他成员（不含本机）。</summary>
    public IReadOnlyList<CollabMemberInfo> Members { get; private set; } = [];

    /// <summary>已连接的公共节点数（-1 未知）。</summary>
    public int ConnectedNodes { get; private set; } = -1;

    /// <summary>队友视角：房主成员信息（未发现为 null）。</summary>
    public CollabMemberInfo? HostMember => Role == CollabRole.Guest
        ? Members.FirstOrDefault(m => m.Hostname.StartsWith(HostPrefix, StringComparison.Ordinal))
        : null;

    /// <summary>队友视角：主端口（en/English）对应的本地转发端口。</summary>
    public int? MainLocalPort => Forwards.Count > 0 ? Forwards[0].LocalPort : null;

    /// <summary>队友视角：访客工作台入口地址。</summary>
    public string? GuestBaseUrl => MainLocalPort is int port ? $"http://localhost:{port}" : null;

    /// <summary>
    /// 房主：开启协作（校验服务运行 → 下载组件 → 生成邀请码 → 启动虚拟网络）。
    /// </summary>
    public async Task StartHostAsync(CancellationToken cancellation)
    {
        EnsureStartable();
        var operationCts = new CancellationTokenSource();
        _operationCts = operationCts;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, operationCts.Token);
        var ct = linked.Token;

        Role = CollabRole.Host;
        Stage = CollabStage.Working;
        SetStatus("正在检查服务状态…");
        RaiseChanged();
        try
        {
            if (!await AyakaleafService.IsContainerRunningAsync(AyakaleafService.OverleafContainer).ConfigureAwait(false))
                throw new CollabException("Ayakaleaf 服务未运行，请先在「服务」页启动服务");

            var languages = AyakaleafService.GetEnabledLanguages();
            int mainPort = Config.Ayakaleaf.Port;
            int portCount = languages.Count;

            SetStatus("正在准备协作组件…");
            RaiseChanged();
            await EasyTierPackage.InstallAsync(null, ct).ConfigureAwait(false);

            var code = CollabCode.Create();
            InviteCode = code;

            SetStatus("正在启动虚拟网络…");
            RaiseChanged();
            var instance = EasyTierInstance.Start(new EasyTierConfig
            {
                Hostname = $"{HostPrefix}{mainPort}-{portCount}",
                NetworkName = CollabCode.ToNetworkName(code),
                NetworkSecret = CollabCode.ToNetworkSecret(code),
                TcpWhitelist = Enumerable.Range(0, portCount)
                    .Select(i => (mainPort + i).ToString(CultureInfo.InvariantCulture)).ToList(),
                UdpWhitelist = [],
                Peers = CollabNodes.Effective(Config.Ayakaleaf.CollabCustomNodes),
                HideIp = Config.Ayakaleaf.CollabHideIp,
                DirectOnly = Config.Ayakaleaf.CollabDirectOnly,
            });
            instance.Exited += OnInstanceExited;
            _instance = instance;

            SetStatus("正在连接公共节点…");
            RaiseChanged();
            bool connected = false;
            for (int i = 0; i < NodeConnectTimeoutSeconds; i++)
            {
                await Task.Delay(1000, ct).ConfigureAwait(false);
                ThrowIfInstanceDead();
                int nodes = await instance.CountConnectedNodesAsync(ct).ConfigureAwait(false);
                if (nodes > 0)
                {
                    ConnectedNodes = nodes;
                    connected = true;
                    break;
                }
            }
            if (!connected)
                throw new CollabException("无法连接任何公共节点，请检查网络或在高级设置中配置自定义节点");

            Stage = CollabStage.Ready;
            SetStatus("协作已开启，等待队友加入…");
            StartMonitorLoop();
            RaiseChanged();
        }
        catch (OperationCanceledException)
        {
            HandleCancelled();
        }
        catch (Exception ex)
        {
            HandleFailure(ex);
        }
        finally
        {
            _operationCts = null;
            operationCts.Dispose();
        }
    }

    /// <summary>
    /// 队友：加入协作（解析邀请码 → 下载组件 → 启动虚拟网络 → 寻找房主 → 逐端口转发）。
    /// </summary>
    /// <param name="inviteCode">邀请码（任意包含邀请码的文本均可）。</param>
    public async Task JoinAsync(string inviteCode, CancellationToken cancellation)
    {
        EnsureStartable();
        var operationCts = new CancellationTokenSource();
        _operationCts = operationCts;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, operationCts.Token);
        var ct = linked.Token;

        Role = CollabRole.Guest;
        Stage = CollabStage.Working;
        SetStatus("正在解析邀请码…");
        RaiseChanged();
        try
        {
            if (!CollabCode.TryParse(inviteCode, out var code))
                throw new CollabException("邀请码无效，请检查后重新输入");
            InviteCode = code;

            SetStatus("正在准备协作组件…");
            RaiseChanged();
            await EasyTierPackage.InstallAsync(null, ct).ConfigureAwait(false);

            SetStatus("正在启动虚拟网络…");
            RaiseChanged();
            var instance = EasyTierInstance.Start(new EasyTierConfig
            {
                Hostname = $"{GuestPrefix}{RandomHex(8)}",
                NetworkName = CollabCode.ToNetworkName(code),
                NetworkSecret = CollabCode.ToNetworkSecret(code),
                TcpWhitelist = ["0"],
                UdpWhitelist = [],
                Peers = CollabNodes.Effective(Config.Ayakaleaf.CollabCustomNodes),
                HideIp = Config.Ayakaleaf.CollabHideIp,
                DirectOnly = Config.Ayakaleaf.CollabDirectOnly,
            });
            instance.Exited += OnInstanceExited;
            _instance = instance;

            SetStatus("正在寻找房主…");
            RaiseChanged();
            EasyTierPeer? host = null;
            for (int i = 0; i < HostFindTimeoutSeconds; i++)
            {
                await Task.Delay(1000, ct).ConfigureAwait(false);
                ThrowIfInstanceDead();
                var peers = await instance.GetPeersAsync(ct).ConfigureAwait(false);
                host = peers.FirstOrDefault(p =>
                    p.IsMember && p.Hostname.StartsWith(HostPrefix, StringComparison.Ordinal));
                if (host is not null) break;
            }
            if (host is null || string.IsNullOrEmpty(host.IPv4))
                throw new CollabException("未能找到房主，请确认邀请码正确且房主已开启协作");

            // 解码房主端口结构：ayaka-host-<主端口>-<语言数>
            var parts = host.Hostname[HostPrefix.Length..].Split('-');
            if (parts.Length != 2
                || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int mainPort)
                || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int portCount)
                || mainPort is <= 0 or > 65535
                || portCount is <= 0 or > 18)
            {
                throw new CollabException("房主端口信息解析失败：" + host.Hostname);
            }

            SetStatus("正在建立端口转发…");
            RaiseChanged();
            var hostAddress = IPAddress.Parse(host.IPv4);
            var forwards = new List<CollabPortForward>();
            for (int i = 0; i < portCount; i++)
            {
                int remotePort = mainPort + i;
                // 本地沿用同端口号（浏览器地址与房主一致）；被占用时改用空闲端口
                int localPort = IsLocalPortFree(remotePort) ? remotePort : EasyTierInstance.FindFreePort();
                var bind = new IPEndPoint(IPAddress.Loopback, localPort);
                var target = new IPEndPoint(hostAddress, remotePort);
                if (!await instance.AddPortForwardAsync("tcp", bind, target, ct).ConfigureAwait(false))
                    throw new CollabException($"端口转发建立失败（远程端口 {remotePort}），请重试");
                forwards.Add(new CollabPortForward(localPort, remotePort));
            }
            Forwards = forwards;

            SetStatus("正在探测服务语言…");
            RaiseChanged();
            await ProbeGuestLanguagesAsync(ct).ConfigureAwait(false);

            Stage = CollabStage.Ready;
            SetStatus("已加入协作");
            StartMonitorLoop();
            RaiseChanged();
        }
        catch (OperationCanceledException)
        {
            HandleCancelled();
        }
        catch (Exception ex)
        {
            HandleFailure(ex);
        }
        finally
        {
            _operationCts = null;
            operationCts.Dispose();
        }
    }

    /// <summary>停止协作会话（可随时调用）。</summary>
    public Task StopAsync()
    {
        _operationCts?.Cancel();
        Stage = CollabStage.Idle;
        Role = CollabRole.None;
        StopInternal();
        InviteCode = null;
        SetStatus("");
        RaiseChanged();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _operationCts?.Cancel();
        StopInternal();
        _operationCts?.Dispose();
        _operationCts = null;
    }

    // ---------------- 内部实现 ----------------

    private void EnsureStartable()
    {
        if (Stage == CollabStage.Working) throw new InvalidOperationException("已有协作操作正在进行中");
        if (Stage == CollabStage.Ready) throw new InvalidOperationException("协作已在进行中，请先停止当前会话");
    }

    private void ThrowIfInstanceDead()
    {
        if (_instance is null || !_instance.IsAlive)
            throw new CollabException("网络组件已退出，请重试");
    }

    private void HandleCancelled()
    {
        StopInternal();
        Role = CollabRole.None;
        Stage = CollabStage.Idle;
        InviteCode = null;
        SetStatus("");
        RaiseChanged();
    }

    private void HandleFailure(Exception ex)
    {
        StopInternal();
        Stage = CollabStage.Failed;
        SetStatus(ex is CollabException ? ex.Message : "发生错误：" + ex.Message);
        LogWrapper.Error(ex, "Collab", "协作会话失败");
        RaiseChanged();
    }

    private void StopInternal()
    {
        _monitorCts?.Cancel();
        _monitorCts?.Dispose();
        _monitorCts = null;
        if (_instance is not null)
        {
            _instance.Exited -= OnInstanceExited;
            _instance.Dispose();
            _instance = null;
        }
        Members = [];
        ConnectedNodes = -1;
        Forwards = [];
        GuestLanguages = [];
    }

    private void OnInstanceExited(object? sender, EventArgs e)
    {
        if (Stage != CollabStage.Ready) return;
        StopInternal();
        Stage = CollabStage.Failed;
        SetStatus("网络组件意外退出，协作已中断");
        RaiseChanged();
    }

    private void StartMonitorLoop()
    {
        var cts = new CancellationTokenSource();
        _monitorCts = cts;
        _ = Task.Run(() => MonitorLoopAsync(cts.Token));
    }

    private async Task MonitorLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                var instance = _instance;
                if (instance is null) return;
                if (!instance.IsAlive)
                {
                    if (Stage == CollabStage.Ready)
                    {
                        StopInternal();
                        Stage = CollabStage.Failed;
                        SetStatus("网络组件意外退出，协作已中断");
                        RaiseChanged();
                    }
                    return;
                }

                int nodes = await instance.CountConnectedNodesAsync(ct).ConfigureAwait(false);
                var peers = await instance.GetPeersAsync(ct).ConfigureAwait(false);
                ConnectedNodes = nodes;
                Members = peers
                    .Where(p => p.IsMember && !p.IsLocal)
                    .Select(p => new CollabMemberInfo(p.Hostname, p.IPv4, p.IsDirect, p.LatencyMs, p.Tunnel))
                    .ToList();
                RaiseChanged();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            LogWrapper.Warn(ex, "Collab", "协作状态监控异常");
        }
    }

    /// <summary>按转发端口探测房主的界面语言（读取 /login 页面的 lang 属性；主端口固定 en）。</summary>
    private async Task ProbeGuestLanguagesAsync(CancellationToken cancellation)
    {
        var forwards = Forwards;
        if (forwards.Count == 0) return;

        var probes = new List<Task<CollabGuestLanguage?>>(forwards.Count);
        for (int i = 0; i < forwards.Count; i++)
        {
            probes.Add(ProbeLanguageAsync(forwards[i], i, cancellation));
        }
        var results = await Task.WhenAll(probes).ConfigureAwait(false);
        GuestLanguages = results.OfType<CollabGuestLanguage>().ToList();
    }

    private static async Task<CollabGuestLanguage?> ProbeLanguageAsync(CollabPortForward forward, int index, CancellationToken cancellation)
    {
        // 主端口固定绑定 en/English
        if (index == 0)
            return new CollabGuestLanguage("en", "English", forward.LocalPort);

        try
        {
            using var response = await ProbeClient.GetAsync($"http://localhost:{forward.LocalPort}/login", cancellation).ConfigureAwait(false);
            string html = await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false);
            var match = HtmlLangRegex().Match(html);
            if (!match.Success) return null;

            string code = match.Groups[1].Value;
            var supported = AyakaleafService.SupportedLanguages.FirstOrDefault(l =>
                string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase));
            return new CollabGuestLanguage(supported?.Code ?? code, supported?.NativeName ?? code, forward.LocalPort);
        }
        catch
        {
            // 探测失败不阻塞加入流程，该端口不出现在语言下拉中
            return null;
        }
    }

    private static bool IsLocalPortFree(int port)
    {
        try
        {
            var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static string RandomHex(int digits)
    {
        byte[] bytes = RandomNumberGenerator.GetBytes((digits + 1) / 2);
        return Convert.ToHexString(bytes).ToLowerInvariant()[..digits];
    }

    private void SetStatus(string text) => StatusText = text;

    private void RaiseChanged()
    {
        var handler = Changed;
        if (handler is null) return;
        if (_uiContext is not null) _uiContext.Post(_ => handler(), null);
        else Task.Run(handler);
    }

    [GeneratedRegex(@"<html[^>]*\blang\s*=\s*""([a-zA-Z\-]+)""")]
    private static partial Regex HtmlLangRegex();
}

/// <summary>
/// 全局协作会话服务：进程内单例，切页保持会话存活，退出时统一释放。
/// </summary>
public static class CollabService
{
    /// <summary>当前协作会话（未创建为 null）。</summary>
    public static CollabSession? Session { get; private set; }

    /// <summary>获取或创建全局协作会话。</summary>
    public static CollabSession GetOrCreateSession()
    {
        Session ??= new CollabSession();
        return Session;
    }

    /// <summary>停止并释放协作会话（应用退出时调用；JobObject 保证子进程一并结束）。</summary>
    public static void Shutdown()
    {
        Session?.Dispose();
        Session = null;
    }
}
