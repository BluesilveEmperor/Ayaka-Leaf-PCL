using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace PCL.Core.Ayakaleaf;

/// <summary>
/// wslc.exe 命令执行结果。
/// </summary>
public readonly record struct WslResult(int ExitCode, string Stdout, string Stderr)
{
    public bool IsSuccess => ExitCode == 0;

    /// <summary>
    /// 取可读的错误信息（引擎错误可能写在 stdout）。
    /// </summary>
    public string ErrorText => string.IsNullOrWhiteSpace(Stderr) ? Stdout : Stderr;
}

/// <summary>
/// wslc.exe（WSL 容器 CLI，WSL 3.0+ 内置）调用封装。
/// 命令面与 docker CLI 高度一致；容器/镜像/卷数据存放在用户目录
/// AppData\Local\wslc\sessions\ 下，其中 VHD 驱动的数据卷是独立 .vhdx 文件。
/// </summary>
public static class WslcInterop
{
    /// <summary>wslc.exe 的候选安装位置。</summary>
    private static readonly string[] CandidatePaths =
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WSL", "wslc.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "WSL", "wslc.exe"),
    };

    private static string? _exePath;
    private static readonly object _exePathLock = new();

    /// <summary>
    /// 定位 wslc.exe（先查已知安装路径，再扫 PATH）。找不到返回 null。
    /// </summary>
    public static string? FindExe()
    {
        lock (_exePathLock)
        {
            if (_exePath != null)
                return File.Exists(_exePath) ? _exePath : _exePath = null;

            foreach (var path in CandidatePaths)
            {
                if (File.Exists(path)) return _exePath = path;
            }

            var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in pathEnv.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                try
                {
                    var candidate = Path.Combine(dir, "wslc.exe");
                    if (File.Exists(candidate)) return _exePath = candidate;
                }
                catch
                {
                    // 目录名非法等情况，跳过
                }
            }

            return null;
        }
    }

    /// <summary>WSL 容器能力是否可用。</summary>
    public static bool IsAvailable => FindExe() != null;

    /// <summary>
    /// 执行 wslc.exe 命令。
    /// </summary>
    /// <param name="arguments">wslc 参数（不含可执行文件名）。</param>
    /// <param name="timeoutMs">超时毫秒数，超时后强杀进程树。</param>
    /// <param name="ct">外部取消令牌（界面关闭/切换页面时中止长命令）。</param>
    public static Task<WslResult> RunAsync(string arguments, int timeoutMs = 60000, CancellationToken ct = default) =>
        RunStreamingAsync(arguments, null, null, timeoutMs, ct);

    /// <summary>
    /// 执行 wslc.exe 命令并按行回调输出（用于 tlmgr 安装进度这类长任务）。
    /// </summary>
    /// <param name="arguments">wslc 参数（不含可执行文件名）。</param>
    /// <param name="onLine">stdout 每行的回调；为 null 时只收集完整输出。</param>
    /// <param name="onErrorLine">stderr 每行的回调，可为 null。</param>
    /// <param name="timeoutMs">超时毫秒数，超时后强杀进程树。</param>
    /// <param name="ct">外部取消令牌。</param>
    public static async Task<WslResult> RunStreamingAsync(
        string arguments,
        Action<string>? onLine,
        Action<string>? onErrorLine,
        int timeoutMs = 60000,
        CancellationToken ct = default)
    {
        var exe = FindExe();
        if (exe == null)
            return new WslResult(-1, "", "未找到 wslc.exe（WSL 容器工具需要 WSL 3.0+）");

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        using var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            return new WslResult(-1, "", "无法启动 wslc.exe：" + ex.Message);
        }

        // 超时与外部取消合并：任一触发都终止进程树
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linkedCts.CancelAfter(timeoutMs);

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        async Task PumpAsync(StreamReader reader, StringBuilder sink, Action<string>? callback)
        {
            while (await reader.ReadLineAsync(linkedCts.Token).ConfigureAwait(false) is { } line)
            {
                sink.AppendLine(line);
                try { callback?.Invoke(line); } catch { /* 回调异常不影响进程读取 */ }
            }
        }

        try
        {
            await Task.WhenAll(
                PumpAsync(process.StandardOutput, stdout, onLine),
                PumpAsync(process.StandardError, stderr, onErrorLine),
                process.WaitForExitAsync(linkedCts.Token)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 超时或用户取消：进程可能仍在跑，显式终止
            try { process.Kill(entireProcessTree: true); } catch { /* 进程可能已退出 */ }
            return new WslResult(-1, stdout.ToString(), stderr.ToString());
        }

        return new WslResult(process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    // ---------------- 文件搬运（C1 契约：宿主 ↔ 容器） ----------------
    // wslc 命令面与 docker CLI 一致，容器文件搬运优先走 `wslc cp`（与 docker cp 语义相同）。
    // 个别部署若缺少 cp 子命令，可退回 exec + base64 的文本通道（见
    // WriteTextToContainerAsync / ReadTextFromContainerAsync，符号面板补丁同款路径）。

    /// <summary>
    /// 把单个 wslc 参数加引号（含空格/中文的 Windows 路径必需；引号在 C# 侧
    /// 直接传给 wslc.exe，无 shell 中间层，不需要额外转义）。
    /// </summary>
    public static string Quote(string argument) => $"\"{argument.Replace("\"", "")}\"";

    /// <summary>
    /// 把宿主机文件复制进容器（wslc cp，语义同 docker cp）。
    /// 目标为目录时以“/”结尾（与 docker cp 一致，目录不存在会失败）。
    /// </summary>
    public static Task<WslResult> CopyToContainerAsync(string container, string localPath, string containerPath,
        int timeoutMs = 120_000, CancellationToken ct = default) =>
        RunAsync($"cp {Quote(localPath)} {Quote($"{container}:{containerPath}")}", timeoutMs, ct);

    /// <summary>
    /// 把容器内文件复制到宿主机（wslc cp，语义同 docker cp）。
    /// </summary>
    public static Task<WslResult> CopyFromContainerAsync(string container, string containerPath, string localPath,
        int timeoutMs = 120_000, CancellationToken ct = default) =>
        RunAsync($"cp {Quote($"{container}:{containerPath}")} {Quote(localPath)}", timeoutMs, ct);

    /// <summary>
    /// 在容器内执行命令并取回 base64 编码的 stdout（用于小文件读取：
    /// <c>exec 容器 sh -c "cat 路径 | base64"</c>，规避二进制输出经管道编码损坏）。
    /// 路径含特殊字符时建议先经 <see cref="VerifyContainerPathRoundtripAsync"/> 校验。
    /// </summary>
    public static async Task<byte[]?> ReadFileFromContainerAsync(string container, string containerPath,
        int timeoutMs = 60_000, CancellationToken ct = default)
    {
        var r = await RunAsync(
            $"exec {container} sh -c \"cat {EscapeSingle(containerPath)} | base64\"", timeoutMs, ct).ConfigureAwait(false);
        if (!r.IsSuccess) return null;
        try
        {
            var b64 = Regex.Replace(r.Stdout, @"\s", "");
            return Convert.FromBase64String(b64);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 把小段文本写入容器内文件（UTF-8，整体覆盖）。经 base64 + exec 传递，
    /// 避开多层引号转义问题（与 nginx 语言补丁同款通道）；大文件请改用
    /// <see cref="CopyToContainerAsync"/>。
    /// </summary>
    public static async Task<bool> WriteTextToContainerAsync(string container, string containerPath, string content,
        int timeoutMs = 60_000, CancellationToken ct = default)
    {
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(content ?? ""));
        var r = await RunAsync(
            $"exec {container} sh -c \"echo {b64} | base64 -d > {EscapeSingle(containerPath)}\"", timeoutMs, ct).ConfigureAwait(false);
        return r.IsSuccess;
    }

    /// <summary>
    /// 容器路径往返一致性校验（FR-017 边界：路径含空格/中文/特殊字符时，
    /// 多层引号与 shell 转义可能丢失字符）：把路径写进临时文件再读回比对。
    /// </summary>
    /// <returns>一致返回 true；容器不可达或读回内容不一致返回 false。</returns>
    public static async Task<bool> VerifyContainerPathRoundtripAsync(string container, string containerPath,
        CancellationToken ct = default)
    {
        var probe = $"ayaka-path-probe-{Guid.NewGuid():N}";
        var ok = await WriteTextToContainerAsync(container, containerPath, probe, 30_000, ct).ConfigureAwait(false);
        if (!ok) return false;
        var bytes = await ReadFileFromContainerAsync(container, containerPath, 30_000, ct).ConfigureAwait(false);
        return bytes is not null && Encoding.UTF8.GetString(bytes).Trim() == probe;
    }

    /// <summary>为容器内 sh -c 命令转义单引号（用双引号包裹路径，路径内的双引号先剔除）。</summary>
    private static string EscapeSingle(string path) =>
        "'" + (path ?? "").Replace("\"", "").Replace("'", "'\\''") + "'";
}
