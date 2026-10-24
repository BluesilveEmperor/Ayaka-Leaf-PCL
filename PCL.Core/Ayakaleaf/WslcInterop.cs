using System;
using System.Diagnostics;
using System.IO;
using System.Text;
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
    public static async Task<WslResult> RunAsync(string arguments, int timeoutMs = 60000)
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

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        using var timeoutCts = new CancellationTokenSource(timeoutMs);
        var exitTask = process.WaitForExitAsync(timeoutCts.Token);
        var finished = await Task.WhenAny(exitTask, Task.Delay(Timeout.Infinite));
        if (finished != exitTask || exitTask.IsCanceled)
        {
            // 超时：WaitForExitAsync 会被取消令牌转为 Canceled 态，须显式终止进程
            try { process.Kill(entireProcessTree: true); } catch { /* 进程可能已退出 */ }
            try { await exitTask; } catch { /* 吞掉取消异常 */ }
            return new WslResult(-1, await stdoutTask, await stderrTask);
        }

        return new WslResult(process.ExitCode, await stdoutTask, await stderrTask);
    }
}
