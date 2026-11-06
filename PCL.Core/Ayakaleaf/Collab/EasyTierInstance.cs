using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PCL.Core.Logging;

namespace PCL.Core.Ayakaleaf.Collab;

/// <summary>
/// 虚拟网络中的成员（由 easytier-cli peer 输出解析）。
/// </summary>
public sealed record EasyTierPeer
{
    /// <summary>成员主机名（发现协议载体）。</summary>
    public required string Hostname { get; init; }

    /// <summary>虚拟 IPv4 地址；公共节点为空（非网络成员）。</summary>
    public string IPv4 { get; init; } = "";

    /// <summary>是否为本机。</summary>
    public bool IsLocal { get; init; }

    /// <summary>是否与本成员 P2P 直连（否则经节点中继）。</summary>
    public bool IsDirect { get; init; }

    /// <summary>往返延迟（毫秒），未测量为 null。</summary>
    public double? LatencyMs { get; init; }

    /// <summary>使用的传输协议（udp / tcp / quic 等）。</summary>
    public string Tunnel { get; init; } = "";

    /// <summary>是否为网络成员（公共节点不算）。</summary>
    public bool IsMember => IPv4.Length > 0;

    public static EasyTierPeer FromJson(JsonElement item)
    {
        string cost = GetString(item, "cost");
        return new EasyTierPeer
        {
            Hostname = GetString(item, "hostname"),
            IPv4 = GetString(item, "ipv4"),
            IsLocal = cost == "Local",
            IsDirect = cost == "p2p",
            LatencyMs = GetDouble(item, "lat_ms"),
            Tunnel = GetString(item, "tunnel_proto"),
        };
    }

    private static string GetString(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var value)) return "";
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.Number => value.GetRawText(),
            _ => "",
        };
    }

    private static double? GetDouble(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetDouble(),
            JsonValueKind.String when double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null,
        };
    }
}

/// <summary>
/// 运行中的虚拟网络实例（easytier-core 子进程），经本地 RPC 端口管理。
/// 通过 CLI 子命令查询成员（peer）、节点连接（connector）与添加端口转发（port-forward）。
/// </summary>
public sealed class EasyTierInstance : IDisposable
{
    private const int OutputLimit = 300;
    private static readonly TimeSpan CliTimeout = TimeSpan.FromSeconds(10);

    private readonly Process _process;
    private readonly string _configPath;
    private readonly int _rpcPort;
    private readonly Queue<string> _output = new();
    private int _disposed;

    private EasyTierInstance(Process process, string configPath, int rpcPort)
    {
        _process = process;
        _configPath = configPath;
        _rpcPort = rpcPort;
    }

    /// <summary>进程意外退出时触发（工作线程回调）。</summary>
    public event EventHandler? Exited;

    public int RpcPort => _rpcPort;

    public bool IsAlive
    {
        get
        {
            try
            {
                return !_process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// 启动新的虚拟网络实例。
    /// </summary>
    public static EasyTierInstance Start(EasyTierConfig config)
    {
        string runtimeDirectory = Path.Combine(EasyTierPackage.InstallDirectory, "runtime");
        Directory.CreateDirectory(runtimeDirectory);
        CleanupStaleConfigs(runtimeDirectory);

        string configPath = Path.Combine(runtimeDirectory, Guid.NewGuid().ToString("N") + ".toml");
        File.WriteAllText(configPath, config.ToToml(), new UTF8Encoding(false));

        int rpcPort = FindFreePort();
        var process = new Process
        {
            StartInfo = new ProcessStartInfo(EasyTierPackage.CorePath)
            {
                Arguments = $"-c \"{configPath}\" -r 127.0.0.1:{rpcPort} --multi-thread",
                WorkingDirectory = EasyTierPackage.InstallDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            },
            EnableRaisingEvents = true,
        };

        var instance = new EasyTierInstance(process, configPath, rpcPort);
        process.OutputDataReceived += (_, e) => instance.Capture(e.Data);
        process.ErrorDataReceived += (_, e) => instance.Capture(e.Data);
        process.Exited += (_, _) => instance.OnExited();

        LogWrapper.Info("Collab", $"启动网络实例（rpc {rpcPort}，hostname {config.Hostname}，隐藏 IP {config.HideIp}）");
        process.Start();
        ChildProcessJob.Assign(process);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return instance;
    }

    /// <summary>
    /// 网络成员列表（含本机）；进程未就绪时为空列表。
    /// </summary>
    public async Task<IReadOnlyList<EasyTierPeer>> GetPeersAsync(CancellationToken cancellation)
    {
        string? json = await RunCliAsync(cancellation, "-o", "json", "peer").ConfigureAwait(false);
        if (json is null) return [];

        var result = new List<EasyTierPeer>();
        try
        {
            using var doc = JsonDocument.Parse(json.Trim());
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return result;
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                result.Add(EasyTierPeer.FromJson(item));
            }
        }
        catch (JsonException ex)
        {
            LogWrapper.Warn(ex, "Collab", "解析 peer 输出失败");
        }
        return result;
    }

    /// <summary>
    /// 已连接的节点数；未知返回 -1。
    /// </summary>
    public async Task<int> CountConnectedNodesAsync(CancellationToken cancellation)
    {
        string? json = await RunCliAsync(cancellation, "-o", "json", "connector").ConfigureAwait(false);
        if (json is null) return -1;

        try
        {
            using var doc = JsonDocument.Parse(json.Trim());
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return -1;
            int count = 0;
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (!item.TryGetProperty("status", out var status)) continue;
                // status 0 = 已连接
                int? value = status.ValueKind switch
                {
                    JsonValueKind.Number => status.GetInt32(),
                    JsonValueKind.String when int.TryParse(status.GetString(), out var parsed) => parsed,
                    _ => null,
                };
                if (value == 0) count++;
            }
            return count;
        }
        catch (JsonException)
        {
            return -1;
        }
    }

    /// <summary>
    /// 添加端口转发：本机 <paramref name="bind"/> → 网络内 <paramref name="target"/>（重试 3 次）。
    /// </summary>
    /// <param name="protocol">"tcp" 或 "udp"。</param>
    public async Task<bool> AddPortForwardAsync(string protocol, IPEndPoint bind, IPEndPoint target, CancellationToken cancellation)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            string? output = await RunCliAsync(cancellation, "port-forward", "add", protocol, FormatEndPoint(bind), FormatEndPoint(target)).ConfigureAwait(false);
            if (output is not null) return true;
            await Task.Delay(500 + attempt * 1000, cancellation).ConfigureAwait(false);
        }
        LogWrapper.Warn("Collab", $"端口转发添加失败：{protocol} {FormatEndPoint(bind)} -> {FormatEndPoint(target)}");
        return false;
    }

    /// <summary>进程最近输出的日志行（环形缓冲，供诊断）。</summary>
    public string GetRecentOutput()
    {
        lock (_output) return string.Join(Environment.NewLine, _output);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        try
        {
            if (!_process.HasExited)
            {
                _process.Kill();
                _process.WaitForExit(3000);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
        }
        _process.Dispose();

        try
        {
            File.Delete(_configPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        LogWrapper.Info("Collab", "网络实例已停止");
    }

    /// <summary>当前无人监听的本地回环端口。</summary>
    public static int FindFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private async Task<string?> RunCliAsync(CancellationToken cancellation, params string[] arguments)
    {
        if (!IsAlive) return null;

        var start = new ProcessStartInfo(EasyTierPackage.CliPath)
        {
            Arguments = $"-p 127.0.0.1:{_rpcPort} " + string.Join(" ", arguments.Select(QuoteArgument)),
            WorkingDirectory = EasyTierPackage.InstallDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        return await Task.Run(() =>
        {
            using var cli = new Process { StartInfo = start };
            try
            {
                cli.Start();
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            {
                LogWrapper.Warn("Collab", "CLI 启动失败：" + ex.Message);
                return null;
            }

            var stdout = cli.StandardOutput.ReadToEndAsync();
            var stderr = cli.StandardError.ReadToEndAsync();
            if (!cli.WaitForExit((int)CliTimeout.TotalMilliseconds))
            {
                try { cli.Kill(); }
                catch (InvalidOperationException) { }
                return null;
            }
            cli.WaitForExit();

            if (cli.ExitCode != 0)
            {
                LogWrapper.Info("Collab", $"CLI 命令 [{string.Join(" ", arguments)}] 退出码 {cli.ExitCode}：{stderr.Result.Trim()}");
                return null;
            }
            return stdout.Result;
        }, cancellation).ConfigureAwait(false);
    }

    private void Capture(string? line)
    {
        if (line is null) return;
        lock (_output)
        {
            _output.Enqueue(line);
            while (_output.Count > OutputLimit) _output.Dequeue();
        }
    }

    private void OnExited()
    {
        if (Volatile.Read(ref _disposed) != 0) return;

        int code;
        try
        {
            code = _process.ExitCode;
        }
        catch (InvalidOperationException)
        {
            code = -1;
        }
        LogWrapper.Error("Collab", $"网络实例意外退出（退出码 {code}）。最近输出：{Environment.NewLine}{GetRecentOutput()}");
        Exited?.Invoke(this, EventArgs.Empty);
    }

    private static string FormatEndPoint(IPEndPoint endPoint)
    {
        return endPoint.AddressFamily == AddressFamily.InterNetworkV6
            ? $"[{endPoint.Address}]:{endPoint.Port}"
            : $"{endPoint.Address}:{endPoint.Port}";
    }

    private static string QuoteArgument(string argument)
    {
        return argument.IndexOfAny([' ', '"']) < 0 ? argument : "\"" + argument.Replace("\"", "\\\"") + "\"";
    }

    /// <summary>清理上次异常退出遗留的 TOML 配置文件（当前进程内仅本实例使用该目录）。</summary>
    private static void CleanupStaleConfigs(string runtimeDirectory)
    {
        try
        {
            foreach (string file in Directory.GetFiles(runtimeDirectory, "*.toml"))
            {
                File.Delete(file);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
