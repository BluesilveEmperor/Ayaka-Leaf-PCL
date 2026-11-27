using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using PCL.Core.App;
using PCL.Core.Logging;

namespace PCL.Core.Ayakaleaf;

/// <summary>环境依赖项状态。</summary>
public enum DependencyStatus
{
    /// <summary>已就绪。</summary>
    Ok,
    /// <summary>缺失或版本不足。</summary>
    Missing,
    /// <summary>修复中。</summary>
    Repairing,
    /// <summary>修复失败（可重试）。</summary>
    RepairFailed,
}

/// <summary>
/// 一项环境依赖（US2 / FR-004 / FR-005）。
/// </summary>
/// <param name="Id">依赖标识：wsl / wslc / dotnet。</param>
/// <param name="Name">展示名。</param>
/// <param name="Status">检测结果。</param>
/// <param name="Detail">给用户看的一句话说明（含版本号或缺失原因）。</param>
/// <param name="RepairHint">修复动作的按钮文案；为 null 表示该项无法自动修复。</param>
/// <param name="RequiresReboot">修复后是否需要重启系统。</param>
public sealed record EnvironmentDependency(
    string Id,
    string Name,
    DependencyStatus Status,
    string Detail,
    string? RepairHint,
    bool RequiresReboot)
{
    public bool IsOk => Status == DependencyStatus.Ok;
}

/// <summary>
/// 环境未就绪（US2 / T007）：部署/启动前的环境门禁检测到缺失项。
/// 界面捕获后应引导用户到环境检测页一键修复，而不是当作普通部署失败提示。
/// </summary>
public sealed class EnvironmentNotReadyException : Exception
{
    /// <summary>检测到的全部依赖项（含就绪项，便于检测页直接复用渲染）。</summary>
    public IReadOnlyList<EnvironmentDependency> Dependencies { get; }

    public EnvironmentNotReadyException(IReadOnlyList<EnvironmentDependency> dependencies) :
        base(BuildMessage(dependencies))
    {
        Dependencies = dependencies;
    }

    private static string BuildMessage(IReadOnlyList<EnvironmentDependency> dependencies)
    {
        var missing = dependencies.Where(d => !d.IsOk).ToList();
        var detail = string.Join("；", missing.Select(d => $"{d.Name}：{d.Detail}"));
        return $"运行环境未就绪（{missing.Count} 项缺失）：{detail}。" +
               "已跳转「环境自检」页，请修复缺失项后重试。";
    }
}

/// <summary>
/// 首启环境检测与一键修复（US2）。
/// <para>
/// 只检测启动器真正依赖的三项：WSL 版本（wslc 需要 WSL 3.0+）、wslc.exe、.NET 运行时。
/// 修复动作全部调用系统官方命令（<c>wsl --install</c> / <c>wsl --update</c>），需要管理员权限时
/// 通过 UAC 提权，避免让用户自己开命令行。
/// </para>
/// </summary>
public static class EnvironmentCheckService
{
    /// <summary>WSL 3.0 起才带 wslc（容器 CLI）。</summary>
    private static readonly Version MinimumWslVersion = new(3, 0);

    /// <summary>逐项检测，返回顺序固定（界面按此顺序渲染）。</summary>
    public static async Task<IReadOnlyList<EnvironmentDependency>> CheckAsync(CancellationToken ct = default)
    {
        var list = new List<EnvironmentDependency>(3)
        {
            await CheckWslAsync(ct).ConfigureAwait(false),
            await CheckWslcAsync(ct).ConfigureAwait(false),
            CheckDotnet(),
        };
        return list;
    }

    /// <summary>最近一次检测结果（进程内缓存；环境检测有进程开销，避免部署/启动重复执行）。</summary>
    private static IReadOnlyList<EnvironmentDependency>? _lastResult;

    /// <summary>
    /// 部署/启动前的环境门禁（US2 / T007 + T008）：
    /// <para>
    /// 上次检测全通过且 wslc 仍在（<c>States.Ayakaleaf.EnvironmentReady</c> 持久化，
    /// 跨启动有效）→ 快速放行，不重复执行检测命令；否则逐项检测：
    /// 全部就绪 → 持久化就绪标记并放行；有缺失项 → 抛
    /// <see cref="EnvironmentNotReadyException"/>（界面据此引导至环境检测页）。
    /// </para>
    /// </summary>
    public static async Task EnsureReadyForDeployAsync(Action<string>? progress = null, CancellationToken ct = default)
    {
        // 快速通道（T008）：已确认过就绪且 wslc.exe 仍在（环境没有明显变化），直接放行
        if (States.Ayakaleaf.EnvironmentReady && WslcInterop.IsAvailable)
            return;

        progress?.Invoke("正在检查运行环境…");
        var items = await CheckAsync(ct).ConfigureAwait(false);
        _lastResult = items;

        var missing = items.Where(i => !i.IsOk).ToList();
        if (missing.Count > 0)
        {
            States.Ayakaleaf.EnvironmentReady = false;
            LogWrapper.Warn($"[Ayakaleaf] 环境检测未通过：{string.Join("；", missing.Select(d => $"{d.Id}={d.Detail}"))}");
            throw new EnvironmentNotReadyException(items);
        }

        States.Ayakaleaf.EnvironmentReady = true;
        LogWrapper.Info($"[Ayakaleaf] 环境检测通过：{string.Join("，", items.Select(d => $"{d.Id} {d.Detail}"))}");
    }

    /// <summary>
    /// 首启环境引导（US2 / T009）：启动链路调用一次。
    /// 引导完成过（<c>States.Ayakaleaf.FirstRunGuided</c>）则跳过；
    /// 检测后未就绪时返回 false（由界面跳转环境检测页），就绪则记录标记并返回 true。
    /// </summary>
    /// <returns>环境是否就绪（未就绪时 <paramref name="dependencies"/> 给出缺失明细）。</returns>
    public static async Task<(bool Ready, IReadOnlyList<EnvironmentDependency>? Dependencies)> CheckForFirstRunAsync(
        CancellationToken ct = default)
    {
        // 引导已完成过：不再自动跳转，避免打扰老用户
        if (States.Ayakaleaf.FirstRunGuided) return (true, null);

        var items = await CheckAsync(ct).ConfigureAwait(false);
        _lastResult = items;
        if (items.All(i => i.IsOk))
        {
            States.Ayakaleaf.EnvironmentReady = true;
            States.Ayakaleaf.FirstRunGuided = true;
            LogWrapper.Info("[Ayakaleaf] 首启环境引导：环境就绪，已完成引导");
            return (true, null);
        }

        States.Ayakaleaf.EnvironmentReady = false;
        // 已自动跳转过一次环境页即视为引导完成：后续启动不再强制跳页，
        // 用户可随时从「服务台 → 环境自检」手动进入
        States.Ayakaleaf.FirstRunGuided = true;
        LogWrapper.Warn("[Ayakaleaf] 首启环境引导：检测到缺失项，已引导至环境自检页");
        return (false, items);
    }

    private static async Task<EnvironmentDependency> CheckWslAsync(CancellationToken ct)
    {
        var exe = FindWslExe();
        if (exe is null)
        {
            return new EnvironmentDependency("wsl", "WSL（Windows Subsystem for Linux）",
                DependencyStatus.Missing,
                "未找到 wsl.exe：系统未安装或未启用 WSL。", "一键安装 WSL", true);
        }

        var result = await RunAsync(exe, "--version", ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            // `wsl --version` 在旧版本上不可用；再退一步看 `wsl -l -v`
            var legacy = await RunAsync(exe, "-l -v", ct).ConfigureAwait(false);
            if (legacy.ExitCode != 0)
                return new EnvironmentDependency("wsl", "WSL（Windows Subsystem for Linux）",
                    DependencyStatus.Missing,
                    "WSL 未启用（wsl --version 与 wsl -l -v 均失败）。", "一键安装 WSL", true);

            return new EnvironmentDependency("wsl", "WSL（Windows Subsystem for Linux）",
                DependencyStatus.Missing,
                $"WSL 版本过旧（需 {MinimumWslVersion.Major}.0 以上才能使用容器）。", "更新 WSL", true);
        }

        var version = ParseWslVersion(result.Stdout);
        if (version is null)
            return new EnvironmentDependency("wsl", "WSL（Windows Subsystem for Linux）",
                DependencyStatus.Missing, "无法识别 WSL 版本。", "更新 WSL", true);

        return version >= MinimumWslVersion
            ? new EnvironmentDependency("wsl", "WSL（Windows Subsystem for Linux）",
                DependencyStatus.Ok, $"版本 {version}", null, false)
            : new EnvironmentDependency("wsl", "WSL（Windows Subsystem for Linux）",
                DependencyStatus.Missing,
                $"版本 {version} 过旧，需要 {MinimumWslVersion.Major}.0 以上。", "更新 WSL", true);
    }

    private static async Task<EnvironmentDependency> CheckWslcAsync(CancellationToken ct)
    {
        if (!WslcInterop.IsAvailable)
            return new EnvironmentDependency("wslc", "WSL 容器工具（wslc）",
                DependencyStatus.Missing,
                "未找到 wslc.exe：WSL 版本不足 3.0 或未随系统更新。", "更新 WSL", true);

        // 能力探针用 `wslc images` 而不是 `wslc info`：info 只读客户端信息，
        // 在 WSL 无法工作的系统上（如未更新的 Win10 19042）也会成功，造成假绿；
        // images 会真正访问镜像库，把部署时才会炸出的 0x80070307 提前到环境自检阶段。
        var r = await WslcInterop.RunAsync("images", 30_000, ct).ConfigureAwait(false);
        if (r.IsSuccess)
            return new EnvironmentDependency("wslc", "WSL 容器工具（wslc）", DependencyStatus.Ok, "可用", null, false);

        var err = r.ErrorText;
        // 系统过旧（WSL_E_OS_NOT_SUPPORTED / 0x80070307）：WSL 3.x 拒绝在此系统上运行，
        // wsl --update 无济于事，必须先升级 Windows
        if (err.Contains("0x80070307", StringComparison.OrdinalIgnoreCase) ||
            err.Contains("WSL_E_OS_NOT_SUPPORTED", StringComparison.OrdinalIgnoreCase))
            return new EnvironmentDependency("wslc", "WSL 容器工具（wslc）",
                DependencyStatus.Missing,
                "当前 Windows 版本过低，WSL 无法在此系统上运行（错误 0x80070307）：" +
                "请通过「Windows 更新」升级系统（Windows 10 需升至 22H2 并安装全部更新，建议升级 Windows 11），" +
                "升级并重启后再重新检测。",
                "打开 Windows 更新", true);

        return new EnvironmentDependency("wslc", "WSL 容器工具（wslc）", DependencyStatus.Missing,
            "wslc.exe 存在但无法运行：" + FirstLine(err), "更新 WSL", true);
    }

    private static EnvironmentDependency CheckDotnet()
    {
        // 启动器自身在跑，说明 Desktop Runtime 大体可用；回报实际版本并核对要求，
        // 出现低于要求的情况（自托管残缺 / 架构不匹配）时给出安装指引（T010）
        var version = Environment.Version;
        var required = new Version(10, 0);
        return version >= required
            ? new EnvironmentDependency("dotnet", ".NET 运行时",
                DependencyStatus.Ok, $"版本 {version}", null, false)
            : new EnvironmentDependency("dotnet", ".NET 运行时",
                DependencyStatus.Missing,
                $"版本 {version} 低于要求的 {required.Major}.0：请安装 .NET 10 Desktop Runtime" +
                "（https://get.dot.net/10，架构需与主程序一致：x64 程序装 x64 运行时）。",
                "打开下载页", false);
    }

    /// <summary>
    /// 执行一键修复（WSL 缺失/过旧时经 UAC 提权调用官方命令）。
    /// 返回 true 表示命令已成功提交；失败消息附带手动降级命令（Edge Case：
    /// UAC 提权被拒绝时用户仍能照着消息自己修）。
    /// </summary>
    public static async Task<(bool Success, string Message)> RepairAsync(
        EnvironmentDependency dependency, CancellationToken ct = default)
    {
        // 系统过旧导致 WSL 拒绝运行（CheckWslcAsync 检出的 0x80070307）：
        // wsl --update 无济于事，引导用户去 Windows 更新升级系统
        if (dependency.Detail.Contains("0x80070307"))
        {
            try
            {
                Process.Start(new ProcessStartInfo("ms-settings:windowsupdate") { UseShellExecute = true });
            }
            catch
            {
                // 设置页打不开时无妨：提示文案里已给出升级路径
            }
            return (true, "已打开「Windows 更新」：请将系统升级到最新" +
                          "（Windows 10 需升至 22H2 并安装全部更新，建议升级 Windows 11），" +
                          "升级并重启后回到此页点「重新检测」。");
        }

        var exe = FindWslExe();
        if (exe is null)
            return (false, "系统里没有 wsl.exe，无法自动修复。请手动启用「适用于 Linux 的 Windows 子系统」功能后重试。");

        // 已安装但版本旧 → update；完全缺失 → install
        var install = dependency.Detail.Contains("未找到 wsl.exe") || dependency.Detail.Contains("未启用");
        var args = install ? "--install --no-distribution" : "--update";

        var (ok, message) = await RunElevatedAsync(exe, args, ct).ConfigureAwait(false);
        if (!ok)
            return (false,
                message.TrimEnd('。', '.') +
                $"。手动修复：以管理员身份打开 PowerShell，执行 wsl {args}；" +
                "或在「控制面板 → 程序 → 启用或关闭 Windows 功能」中勾选「适用于 Linux 的 Windows 子系统」后重启。");

        return (true, dependency.RequiresReboot
            ? "修复命令已执行。需要重启系统才能生效，请重启后回到此页点「重新检测」复查。"
            : "已完成修复，请点「重新检测」复查。");
    }

    // ---------------- 进程工具 ----------------

    private static string? FindSystemExe(string name)
    {
        var systemRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var candidate = System.IO.Path.Combine(systemRoot, "System32", name);
        if (System.IO.File.Exists(candidate)) return candidate;

        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var full = System.IO.Path.Combine(dir, name);
                if (System.IO.File.Exists(full)) return full;
            }
            catch
            {
                // 非法路径跳过
            }
        }

        return null;
    }

    /// <summary>
    /// 定位 wsl.exe：WSL 3.x（独立安装版）的 wsl.exe 在「Program Files\WSL」下，
    /// 而 System32 里的可能是 Win10 旧内置版（不认识 --version，导致 3.x 已装却误报未启用/过旧），故优先前者。
    /// </summary>
    private static string? FindWslExe()
    {
        var pf = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WSL", "wsl.exe");
        if (System.IO.File.Exists(pf)) return pf;

        return FindSystemExe("wsl.exe");
    }

    private static async Task<(int ExitCode, string Stdout)> RunAsync(string exe, string arguments, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // 不预设输出编码：wsl.exe 重定向输出为 UTF-16LE，wslc 为 UTF-8，
            // 读原始字节后由 DecodeOutput 自行识别（预设 UTF-8 会把 UTF-16 输出解成乱码）。
        };

        using var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(60_000);
        var stdoutTask = ReadAllBytesAsync(process.StandardOutput.BaseStream, cts.Token);
        var stderrTask = ReadAllBytesAsync(process.StandardError.BaseStream, cts.Token);
        try
        {
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* 已退出 */ }
            return (-1, "命令执行超时");
        }

        var text = DecodeOutput(await stdoutTask.ConfigureAwait(false)) +
                   DecodeOutput(await stderrTask.ConfigureAwait(false));
        return (process.ExitCode, text);
    }

    private static async Task<byte[]> ReadAllBytesAsync(System.IO.Stream stream, CancellationToken ct)
    {
        using var ms = new System.IO.MemoryStream();
        await stream.CopyToAsync(ms, 81920, ct).ConfigureAwait(false);
        return ms.ToArray();
    }

    /// <summary>
    /// 识别进程输出的编码：带 UTF-16LE BOM 或奇数位字节大面积为 0（ASCII 的 UTF-16 特征）→ 按 UTF-16 解码，
    /// 否则按 UTF-8（wslc / 新版 WSL 工具输出 UTF-8）。
    /// </summary>
    internal static string DecodeOutput(byte[] bytes)
    {
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return global::System.Text.Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);

        var checks = 0;
        var zeros = 0;
        for (var i = 1; i < bytes.Length; i += 2)
        {
            checks++;
            if (bytes[i] == 0) zeros++;
        }
        if (checks >= 4 && zeros * 2 > checks)
            return global::System.Text.Encoding.Unicode.GetString(bytes);

        return global::System.Text.Encoding.UTF8.GetString(bytes);
    }

    /// <summary>以管理员身份运行（UAC 提权），供 wsl --install/--update 使用。</summary>
    private static async Task<(bool Success, string Message)> RunElevatedAsync(
        string exe, string arguments, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = arguments,
            UseShellExecute = true,
            Verb = "runas",
        };

        try
        {
            using var process = Process.Start(psi);
            if (process is null) return (false, "无法启动提权进程。");
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            return process.ExitCode == 0
                ? (true, "")
                : (false, $"命令返回 {process.ExitCode}。可在系统「启用或关闭 Windows 功能」中手动确认。");
        }
        catch (OperationCanceledException)
        {
            return (false, "已取消。");
        }
        catch (Exception ex)
        {
            // 用户在 UAC 弹窗点了「否」时抛 Win32Exception
            return (false, "提权被拒绝或失败：" + ex.Message);
        }
    }

    /// <summary>解析 <c>wsl --version</c> 输出中的版本号（中文/英文输出都覆盖）。</summary>
    internal static Version? ParseWslVersion(string output)
    {
        var match = Regex.Match(output, @"(?:WSL\s*(?:版本|version)\s*[:：]?\s*)(\d+)\.(\d+)(?:\.(\d+))?",
            RegexOptions.IgnoreCase);
        if (!match.Success) return null;
        var major = int.Parse(match.Groups[1].Value);
        var minor = int.Parse(match.Groups[2].Value);
        var build = match.Groups[3].Success ? int.Parse(match.Groups[3].Value) : 0;
        return new Version(major, minor, build);
    }

    private static string FirstLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";
}
