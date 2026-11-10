using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.Caching;
using System.Threading;
using System.Threading.Tasks;
using Ae.Dns.Client;
using Ae.Dns.Protocol;
using Ae.Dns.Protocol.Enums;
using Ae.Dns.Protocol.Records;
using PCL.Core.IO.Net.Http;
using PCL.Core.Logging;

namespace PCL.Core.IO.Net.Dns;

public class DnsQuery : IDisposable
{
    private const string ModuleName = "DoH query";
    public static DnsQuery Instance { get; } = new();

    private readonly DnsCachingClient _resolver;
    private readonly HttpClient[] _httpClients;

    private DnsQuery()
    {
        var proxyHandler = new HttpClientHandler()
        {
            Proxy = HttpProxyManager.Instance
        };
        // 使用Ae.Dns创建DoH客户端，支持多个DoH服务器
        _httpClients =
        [
            new HttpClient(proxyHandler)
            {
                BaseAddress = new Uri("https://doh.pub/")
            },
            new HttpClient(proxyHandler)
            {
                BaseAddress = new Uri("https://doh.pysio.online/")
            },
            new HttpClient(proxyHandler)
            {
                BaseAddress = new Uri("https://cloudflare-dns.com/")
            }
        ];
        _resolver = new DnsCachingClient(
            new WeightedDnsRacerClient(2, _httpClients.Select(static x => new DnsHttpClient(x)).ToArray<IDnsClient>()),
            new MemoryCache("DoH Query Cache"));
    }

    public async Task<DnsMessage?> QueryAsync(string host, DnsQueryType qType, CancellationToken cts = default)
    {
        try
        {
            return await _resolver.Query(DnsQueryFactory.CreateQuery(host, qType), cts);
        }
        catch (TaskCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogWrapper.Warn(ModuleName, $"Failed to resolve DNS for {host}: {ex.Message}, use system default DNS");
        }

        return null;
    }

    public async Task<IPAddress[]?> QueryForIpAsync(string host, CancellationToken cts = default)
    {
        // hosts 文件条目优先于 DoH（与 OS 解析顺序一致）：
        // Watt Toolkit 等本地加速工具通过 hosts + 本地反代访问 GitHub，
        // 若被 DoH 绕过会直连真实 IP，在网络受限环境下导致 TLS 连接被重置。
        var hostsEntries = _GetHostsEntries();
        if (hostsEntries is not null &&
            hostsEntries.TryGetValue(host.TrimEnd('.'), out var hostsIps) &&
            hostsIps.Count > 0)
        {
            LogWrapper.Debug(ModuleName, $"Host {host} is mapped in hosts file, using hosts address instead of DoH");
            return hostsIps.ToArray();
        }

        var queryResponse = await Task.WhenAll(
            [
                QueryAsync(host, DnsQueryType.A, cts),
                QueryAsync(host, DnsQueryType.AAAA, cts)
            ]
        );

        if (queryResponse.All(static x => x is null))
        {
            LogWrapper.Warn(ModuleName, $"Failed to query IP for host {host} using DoH, use system default DNS");
            return await System.Net.Dns.GetHostAddressesAsync(host, cts);
        }

        return queryResponse.Where(static x => x is not null)
            .SelectMany(static x => x!.Answers)
            .Where(static x => x.Type is DnsQueryType.A or DnsQueryType.AAAA)
            .Select(static x => x.Resource as DnsIpAddressResource)
            .Where(static x => x is not null)
            .Select(static x => x!.IPAddress)
            .ToArray();
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        foreach (var client in _httpClients)
        {
            client.Dispose();
        }
        _resolver.Dispose(); // 好像这个包的 Dispose 并没有做什么 lol
    }

    #region hosts 文件解析（优先于 DoH，与 OS 解析行为一致）

    private static readonly string _hostsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "etc", "hosts");
    private static readonly object _hostsLock = new();
    private static (DateTime Stamp, Dictionary<string, List<IPAddress>> Entries)? _hostsCache;

    /// <summary>
    ///     解析 hosts 文件条目（按 LastWriteTimeUtc 缓存），返回 host → IP 列表。
    ///     与 OS 解析行为一致：忽略通配符条目（如 *.example.com），主机名大小写不敏感。
    /// </summary>
    private static Dictionary<string, List<IPAddress>>? _GetHostsEntries()
    {
        lock (_hostsLock)
        {
            try
            {
                if (!File.Exists(_hostsPath)) return null;
                var stamp = File.GetLastWriteTimeUtc(_hostsPath);
                if (_hostsCache is { } cached && cached.Stamp == stamp)
                    return cached.Entries;

                var entries = new Dictionary<string, List<IPAddress>>(StringComparer.OrdinalIgnoreCase);
                foreach (var rawLine in File.ReadAllLines(_hostsPath))
                {
                    var line = rawLine;
                    var commentIndex = line.IndexOf('#');
                    if (commentIndex >= 0) line = line[..commentIndex];
                    var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 2) continue;
                    if (!IPAddress.TryParse(parts[0], out var ip)) continue;
                    for (var i = 1; i < parts.Length; i++)
                    {
                        var host = parts[i].Trim().TrimEnd('.');
                        // 通配符并非 hosts 标准语法，OS 解析会忽略，这里保持一致
                        if (host.Length == 0 || host.Contains('*')) continue;
                        if (!entries.TryGetValue(host, out var list))
                        {
                            list = new List<IPAddress>();
                            entries[host] = list;
                        }
                        if (!list.Contains(ip)) list.Add(ip);
                    }
                }

                _hostsCache = (stamp, entries);
                LogWrapper.Debug(ModuleName, $"Reloaded hosts file: {entries.Count} entries");
                return entries;
            }
            catch (Exception ex)
            {
                LogWrapper.Warn(ModuleName, $"Failed to read hosts file: {ex.Message}");
                return null;
            }
        }
    }

    #endregion
}