using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PCL.Core.Ayakaleaf;

/// <summary>
/// wsl.exe 命令执行结果。
/// </summary>
public readonly record struct WslResult(int ExitCode, string Stdout, string Stderr)
{
    public bool IsSuccess => ExitCode == 0;

    /// <summary>
    /// 取可读的错误信息（docker 常把错误写到 stdout）。
    /// </summary>
    public string ErrorText => string.IsNullOrWhiteSpace(Stderr) ? Stdout : Stderr;
}

/// <summary>
/// wsl.exe 调用封装。
/// 通过环境变量 WSL_UTF8=1 强制 wsl.exe 自身的管理命令（-l/--import 等）输出 UTF-8，
/// 避免 UTF-16 输出导致的乱码问题。
/// </summary>
public static class WslInterop
{
    private const int CreateNoWindowFlag = 0x08000000;

    /// <summary>
    /// Windows 路径转 WSL 内部路径（D:\a\b → /mnt/d/a/b）。
    /// </summary>
    public static string ToWslPath(string windowsPath)
    {
        var full = Path.GetFullPath(windowsPath);
        var root = Path.GetPathRoot(full) ?? throw new ArgumentException("无法解析路径：" + windowsPath);
        var drive = char.ToLowerInvariant(root[0]);
        var rest = full[root.Length..].Replace('\\', '/').TrimEnd('/');
        return $"/mnt/{drive}/{rest}";
    }

    /// <summary>
    /// 执行 wsl.exe 命令。
    /// </summary>
    /// <param name="arguments">wsl.exe 参数（不含可执行文件名）。</param>
    /// <param name="timeoutMs">超时毫秒数，超时后强杀进程树。</param>
    public static async Task<WslResult> RunAsync(string arguments, int timeoutMs = 30000)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "wsl.exe",
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.EnvironmentVariables["WSL_UTF8"] = "1";

        using var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            return new WslResult(-1, "", "无法启动 wsl.exe：" + ex.Message);
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

    /// <summary>
    /// 在指定发行版内执行命令（经过登录 shell）。
    /// </summary>
    public static Task<WslResult> RunInDistroAsync(string distro, string command, int timeoutMs = 60000)
    {
        return RunAsync($"-d \"{distro}\" -- {command}", timeoutMs);
    }

    /// <summary>
    /// 列出全部 WSL 发行版（名称、是否运行中、WSL 版本）。
    /// </summary>
    public static async Task<List<(string Name, bool Running, int Version)>> ListDistrosAsync()
    {
        var result = await RunAsync("-l -v", 20000);
        var list = new List<(string Name, bool Running, int Version)>();
        if (!result.IsSuccess) return list;

        foreach (var rawLine in result.Stdout.Split('\n'))
        {
            var line = rawLine.Replace("\0", "").Trim();
            if (line.Length == 0 || line.StartsWith("NAME")) continue;
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3) continue;
            var name = parts[^3].TrimStart('*');
            var running = parts[^2].Equals("Running", StringComparison.OrdinalIgnoreCase);
            if (!int.TryParse(parts[^1], out var version)) version = 2;
            list.Add((name, running, version));
        }

        return list;
    }

    /// <summary>
    /// 判断指定发行版是否存在。
    /// </summary>
    public static async Task<bool> DistroExistsAsync(string distro)
    {
        var distros = await ListDistrosAsync();
        return distros.Any(d => d.Name.Equals(distro, StringComparison.OrdinalIgnoreCase));
    }
}
