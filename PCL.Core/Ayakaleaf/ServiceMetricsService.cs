using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PCL.Core.Ayakaleaf;

/// <summary>服务健康态。失联与"健康但慢"必须区分，界面按此渲染不同配色（US5 Edge Case）。</summary>
public enum ServiceHealth
{
    /// <summary>容器运行且站点探活通过。</summary>
    Healthy,
    /// <summary>容器运行但探活失败/超时（编译卡死、启动预热等）。</summary>
    Degraded,
    /// <summary>容器不存在或未运行：此态下不展示指标（不得显示归零数据）。</summary>
    Unreachable,
}

/// <summary>
/// 服务状态快照（US5 / FR-008）。<see cref="CompileQueueDepth"/> 为近似值：
/// 容器内 latexmk 进程数，代表"正在编译的项目数"，不包含 CLSI 内部排队。
/// </summary>
public sealed record ServiceMetricsSnapshot(
    double CpuPercent,
    long MemoryUsedBytes,
    long MemoryLimitBytes,
    int CompileQueueDepth,
    IReadOnlyList<string> RecentLogLines,
    ServiceHealth Health,
    DateTimeOffset SampledAt)
{
    /// <summary>内存占用百分比（上限未知时返回 0）。</summary>
    public double MemoryPercent => MemoryLimitBytes <= 0 ? 0 : MemoryUsedBytes * 100.0 / MemoryLimitBytes;

    public static ServiceMetricsSnapshot Unreachable(DateTimeOffset at) =>
        new(0, 0, 0, 0, Array.Empty<string>(), ServiceHealth.Unreachable, at);
}

/// <summary>
/// 服务状态采集（US5，FR-008/FR-009）：容器 CPU/内存、编译队列、日志尾部、探活。
/// <para>
/// 全部经 <see cref="WslcInterop"/> 读取（docker stats / docker logs / exec pgrep），
/// 与 C1 契约一致：Interop 只执行，解析在本类。任一子项失败都降级为部分数据，
/// 只有"容器不存在/未运行"才整体判定为 <see cref="ServiceHealth.Unreachable"/>。
/// </para>
/// </summary>
public static class ServiceMetricsService
{
    private static readonly HttpClient ProbeClient = new() { Timeout = TimeSpan.FromSeconds(3) };

    private const string Container = AyakaleafService.OverleafContainer;

    /// <summary>
    /// 采集一次快照。<paramref name="probeUrl"/> 传站点地址（如 http://localhost:8080/login），
    /// 为空时跳过探活并只按容器状态判定。
    /// </summary>
    public static async Task<ServiceMetricsSnapshot> SampleAsync(
        string? probeUrl = null, int logLines = 40, CancellationToken ct = default)
    {
        var now = DateTimeOffset.Now;

        if (!await IsContainerRunningAsync(ct).ConfigureAwait(false))
            return ServiceMetricsSnapshot.Unreachable(now);

        var statsTask = ReadStatsAsync(ct);
        var queueTask = ReadCompileQueueDepthAsync(ct);
        var logsTask = ReadLogTailAsync(logLines, ct);
        var probeTask = ProbeAsync(probeUrl, ct);

        await Task.WhenAll(statsTask, queueTask, logsTask, probeTask).ConfigureAwait(false);

        var (cpu, used, limit) = statsTask.Result;
        var healthy = probeTask.Result;
        return new ServiceMetricsSnapshot(
            cpu, used, limit, queueTask.Result, logsTask.Result,
            healthy ? ServiceHealth.Healthy : ServiceHealth.Degraded,
            now);
    }

    /// <summary>容器是否正在运行（inspect 的 State.Running）。</summary>
    public static async Task<bool> IsContainerRunningAsync(CancellationToken ct = default)
    {
        var r = await WslcInterop.RunAsync($"inspect -t container {Container}", 30_000, ct).ConfigureAwait(false);
        if (!r.IsSuccess) return false;
        return r.Stdout.Contains("\"Running\": true", StringComparison.Ordinal)
            || r.Stdout.Contains("\"Running\":true", StringComparison.Ordinal);
    }

    /// <summary>一键重启服务（FR-009）：只重启容器，数据卷与用户数据不受影响。</summary>
    public static async Task<WslResult> RestartServiceAsync(CancellationToken ct = default) =>
        await WslcInterop.RunAsync($"restart {Container}", 180_000, ct).ConfigureAwait(false);

    // ---------------- 采集 ----------------

    /// <summary>
    /// 解析 <c>wslc stats --no-stream --format json</c>。
    /// 不同引擎可能给数组或逐行 JSON，两种都兼容；解析失败返回全零（由 Health 表达真实状态）。
    /// </summary>
    private static async Task<(double CpuPercent, long UsedBytes, long LimitBytes)> ReadStatsAsync(CancellationToken ct)
    {
        var r = await WslcInterop.RunAsync(
            $"stats --no-stream --format json {Container}", 30_000, ct).ConfigureAwait(false);
        if (!r.IsSuccess) return (0, 0, 0);

        var text = r.Stdout.Trim();
        if (text.Length == 0) return (0, 0, 0);

        try
        {
            using var doc = JsonDocument.Parse(text.StartsWith('[') ? text : "[" + text.Replace("\n", ",") + "]");
            if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
                return (0, 0, 0);

            var e = doc.RootElement[0];
            var cpu = ParsePercent(GetString(e, "CPUPerc"));
            var (used, limit) = ParseMemUsage(GetString(e, "MemUsage"));
            return (cpu, used, limit);
        }
        catch (JsonException)
        {
            return (0, 0, 0);
        }
    }

    /// <summary>容器内 latexmk 进程数（近似编译队列深度）。</summary>
    private static async Task<int> ReadCompileQueueDepthAsync(CancellationToken ct)
    {
        // pgrep -c 无匹配时退出码为 1，用 `; true` 让 sh 始终返回 0
        var r = await WslcInterop.RunAsync(
            $"exec {Container} sh -c \"pgrep -c latexmk; true\"", 30_000, ct).ConfigureAwait(false);
        if (!r.IsSuccess) return 0;
        return int.TryParse(r.Stdout.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
    }

    private static async Task<IReadOnlyList<string>> ReadLogTailAsync(int lines, CancellationToken ct)
    {
        lines = Math.Clamp(lines, 1, 500);
        var r = await WslcInterop.RunAsync($"logs --tail {lines} {Container}", 30_000, ct).ConfigureAwait(false);
        if (!r.IsSuccess) return Array.Empty<string>();

        // stderr 里是容器自身的 stderr 流，日志页两路都展示
        var all = (r.Stdout + r.Stderr)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return all.Length <= lines ? all : all[^lines..];
    }

    private static async Task<bool> ProbeAsync(string? probeUrl, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(probeUrl)) return true;
        try
        {
            using var resp = await ProbeClient.GetAsync(probeUrl, ct).ConfigureAwait(false);
            return (int)resp.StatusCode is >= 200 and < 500;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // ---------------- 解析工具 ----------------

    private static string GetString(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    /// <summary>"1.23%" → 1.23</summary>
    internal static double ParsePercent(string text)
    {
        text = text.Trim().TrimEnd('%').Trim();
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
    }

    /// <summary>"123.4MiB / 6GiB" → (129_394_278, 6_442_450_944)</summary>
    internal static (long Used, long Limit) ParseMemUsage(string text)
    {
        var parts = text.Split('/', StringSplitOptions.TrimEntries);
        return (parts.Length > 0 ? ParseSize(parts[0]) : 0, parts.Length > 1 ? ParseSize(parts[1]) : 0);
    }

    /// <summary>解析 "1.5GiB" / "900MiB" / "12kB" 这类单位。</summary>
    internal static long ParseSize(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return 0;

        var idx = text.TakeWhile(c => char.IsDigit(c) || c is '.' or ',').Count();
        if (idx == 0) return 0;
        var number = text[..idx].Replace(",", "");
        var unit = text[idx..].Trim().ToUpperInvariant();

        if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            return 0;

        var scale = unit switch
        {
            "B" => 1d,
            "KB" => 1e3,
            "KIB" => 1024d,
            "MB" => 1e6,
            "MIB" => 1024d * 1024,
            "GB" => 1e9,
            "GIB" => 1024d * 1024 * 1024,
            "TB" => 1e12,
            "TIB" => 1024d * 1024 * 1024 * 1024,
            _ => 1d,
        };
        return (long)Math.Round(value * scale);
    }
}
