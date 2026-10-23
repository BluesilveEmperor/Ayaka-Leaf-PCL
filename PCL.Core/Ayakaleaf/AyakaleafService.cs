using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
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
/// 运行时形态：专用 WSL 发行版内嵌 Docker 与全部服务镜像，
/// 启动器负责部署（wsl --import + docker load）与日常启停管理。
/// </summary>
public static class AyakaleafService
{
    private static readonly HttpClient ProbeClient = new() { Timeout = TimeSpan.FromSeconds(3) };

    /// <summary>部署等待探活通过的最大秒数。</summary>
    private const int StartProbeTimeoutSeconds = 180;

    public static string WslDistro =>
        string.IsNullOrWhiteSpace(Config.Ayakaleaf.WslDistro) ? "AyakaleafRuntime" : Config.Ayakaleaf.WslDistro;

    /// <summary>内嵌运行时目录（分发包内，含 runtime.tar / images\ / docker-compose.yml）。</summary>
    public static string RuntimeDirectory => Path.Combine(AppContext.BaseDirectory, "runtime");

    /// <summary>WSL 发行版数据目录（vhdx 存放位置）。</summary>
    public static string WslDataDirectory => Path.Combine(AppContext.BaseDirectory, "data", "wsl");

    /// <summary>备份导出默认目录。</summary>
    public static string BackupDirectory => Path.Combine(AppContext.BaseDirectory, "backups");

    public static string ComposeFilePath
    {
        get
        {
            var configured = Config.Ayakaleaf.ComposePath;
            return string.IsNullOrWhiteSpace(configured)
                ? Path.Combine(RuntimeDirectory, "docker-compose.yml")
                : configured;
        }
    }

    public static string BaseUrl => $"http://localhost:{Config.Ayakaleaf.Port}";

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

    /// <summary>
    /// 检查发行版内 Docker 是否就绪；未就绪时尝试拉起 docker 服务。
    /// </summary>
    public static async Task<bool> EnsureDockerReadyAsync()
    {
        var check = await WslInterop.RunInDistroAsync(WslDistro, "docker info", 15000);
        if (check.IsSuccess) return true;

        // wsl.conf 未启用 systemd 或服务未启动时兜底拉起
        await WslInterop.RunInDistroAsync(WslDistro, "sh -c \"(systemctl start docker 2>/dev/null || service docker start 2>/dev/null)\"", 60000);
        check = await WslInterop.RunInDistroAsync(WslDistro, "docker info", 20000);
        return check.IsSuccess;
    }

    /// <summary>
    /// 部署内嵌运行时：导入 WSL 发行版 → 就绪 Docker → 导入镜像 → 写入部署标记。
    /// </summary>
    public static async Task DeployAsync(Action<string> progress, CancellationToken ct = default)
    {
        var runtimeTar = Path.Combine(RuntimeDirectory, "runtime.tar");
        if (!File.Exists(runtimeTar))
            throw new FileNotFoundException("未找到内嵌运行时包 runtime.tar（应位于 runtime 目录）", runtimeTar);

        if (!await WslInterop.DistroExistsAsync(WslDistro))
        {
            progress("正在导入内嵌运行时（首次需数分钟）…");
            Directory.CreateDirectory(WslDataDirectory);
            var import = await WslInterop.RunAsync(
                $"--import {WslDistro} \"{WslDataDirectory}\" \"{runtimeTar}\" --version 2", 1800000);
            if (!import.IsSuccess)
                throw new Exception("导入 WSL 运行时失败：" + import.ErrorText);
        }
        else
        {
            LogWrapper.Info("[Ayakaleaf] 运行时发行版已存在，跳过导入");
        }

        progress("等待 Docker 就绪…");
        var dockerReady = false;
        for (var i = 0; i < 30; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (await EnsureDockerReadyAsync())
            {
                dockerReady = true;
                break;
            }
            await Task.Delay(2000, ct);
        }
        if (!dockerReady)
            throw new Exception("WSL 内 Docker 未就绪");

        var imagesDir = Path.Combine(RuntimeDirectory, "images");
        if (Directory.Exists(imagesDir))
        {
            foreach (var tar in Directory.GetFiles(imagesDir, "*.tar*").OrderBy(f => f))
            {
                ct.ThrowIfCancellationRequested();
                progress($"导入镜像 {Path.GetFileName(tar)}…");
                await ImportImageAsync(tar, ct);
            }
        }

        Config.Ayakaleaf.Deployed = true;
        progress("部署完成");
    }

    /// <summary>
    /// 启动服务栈（compose up -d + 轮询探活）。
    /// </summary>
    public static async Task StartAsync(Action<string> progress, CancellationToken ct = default)
    {
        if (!await EnsureDockerReadyAsync())
            throw new Exception("WSL 内 Docker 未就绪，请先完成部署");

        progress("正在启动 Ayakaleaf 服务栈…");
        var up = await WslInterop.RunInDistroAsync(
            WslDistro, $"docker compose -f \"{WslInterop.ToWslPath(ComposeFilePath)}\" up -d", 300000);
        if (!up.IsSuccess)
            throw new Exception("启动服务栈失败：" + up.ErrorText);

        progress("等待服务就绪…");
        for (var i = 0; i < StartProbeTimeoutSeconds / 2; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (await ProbeAsync()) return;
            await Task.Delay(2000, ct);
        }
        throw new Exception($"等待服务就绪超时（{StartProbeTimeoutSeconds} 秒），请查看服务日志");
    }

    /// <summary>
    /// 停止服务栈（compose down，数据卷保留）。
    /// </summary>
    public static async Task StopAsync(Action<string> progress, CancellationToken ct = default)
    {
        progress("正在停止 Ayakaleaf 服务栈…");
        var down = await WslInterop.RunInDistroAsync(
            WslDistro, $"docker compose -f \"{WslInterop.ToWslPath(ComposeFilePath)}\" down", 300000);
        if (!down.IsSuccess)
            throw new Exception("停止服务栈失败：" + down.ErrorText);
    }

    /// <summary>
    /// 获取容器列表（含已停止容器）。
    /// </summary>
    public static async Task<List<AyakaleafContainer>> GetContainersAsync()
    {
        var r = await WslInterop.RunInDistroAsync(
            WslDistro, "docker ps -a --format \"{{.Names}}\\t{{.Image}}\\t{{.Status}}\"", 30000);
        var list = new List<AyakaleafContainer>();
        if (!r.IsSuccess) return list;
        foreach (var line in r.Stdout.Split('\n'))
        {
            var parts = line.Trim().Split('\t');
            if (parts.Length < 3) continue;
            list.Add(new AyakaleafContainer(parts[0], parts[1], parts[2]));
        }
        return list;
    }

    /// <summary>
    /// 获取容器日志尾部。
    /// </summary>
    public static async Task<string> GetLogsAsync(int tail = 120, string container = "ol-app")
    {
        var r = await WslInterop.RunInDistroAsync(WslDistro, $"docker logs --tail {tail} {container}", 30000);
        if (!r.IsSuccess) return "（无法获取日志）" + r.ErrorText;
        return (r.Stdout + "\n" + r.Stderr).Trim();
    }

    /// <summary>
    /// 列出镜像。
    /// </summary>
    public static async Task<List<AyakaleafImage>> ListImagesAsync()
    {
        var r = await WslInterop.RunInDistroAsync(
            WslDistro, "docker images --format \"{{.Repository}}\\t{{.Tag}}\\t{{.Size}}\\t{{.CreatedAt}}\"", 30000);
        var list = new List<AyakaleafImage>();
        if (!r.IsSuccess) return list;
        foreach (var line in r.Stdout.Split('\n'))
        {
            var parts = line.Trim().Split('\t');
            if (parts.Length < 4) continue;
            list.Add(new AyakaleafImage(parts[0], parts[1], parts[2], parts[3]));
        }
        return list;
    }

    /// <summary>
    /// 导入镜像 tarball（docker load，重操作，30 分钟超时）。
    /// </summary>
    public static async Task ImportImageAsync(string tarPath, CancellationToken ct = default)
    {
        var r = await WslInterop.RunInDistroAsync(
            WslDistro, $"docker load -i \"{WslInterop.ToWslPath(tarPath)}\"", 1800000);
        if (!r.IsSuccess)
            throw new Exception($"导入镜像 {Path.GetFileName(tarPath)} 失败：{r.ErrorText}");
    }

    /// <summary>
    /// 删除镜像。
    /// </summary>
    public static async Task RemoveImageAsync(string repository, string tag)
    {
        var r = await WslInterop.RunInDistroAsync(WslDistro, $"docker rmi \"{repository}:{tag}\"", 120000);
        if (!r.IsSuccess)
            throw new Exception("删除镜像失败：" + r.ErrorText);
    }

    /// <summary>
    /// 列出数据卷。
    /// </summary>
    public static async Task<List<string>> ListVolumesAsync()
    {
        var r = await WslInterop.RunInDistroAsync(WslDistro, "docker volume ls --format \"{{.Name}}\"", 30000);
        var list = new List<string>();
        if (!r.IsSuccess) return list;
        foreach (var line in r.Stdout.Split('\n'))
        {
            var name = line.Trim();
            if (name.Length > 0) list.Add(name);
        }
        return list;
    }

    /// <summary>
    /// 备份数据卷：直接对发行版内卷数据目录打 tar.gz（无需额外容器/镜像）。
    /// </summary>
    public static async Task<string> BackupVolumeAsync(string volume, string destDir, CancellationToken ct = default)
    {
        Directory.CreateDirectory(destDir);
        var destFile = Path.Combine(destDir, $"{volume}.{DateTime.Now:yyyyMMdd-HHmmss}.tar.gz");
        var cmd = $"tar czf \"{WslInterop.ToWslPath(destFile)}\" -C /var/lib/docker/volumes/{volume}/_data .";
        var r = await WslInterop.RunInDistroAsync(WslDistro, cmd, 1800000);
        if (!r.IsSuccess)
            throw new Exception($"备份数据卷 {volume} 失败：{r.ErrorText}");
        return destFile;
    }

    /// <summary>
    /// 恢复数据卷：确保卷存在后清空并解包备份文件。要求服务栈已停止。
    /// </summary>
    public static async Task RestoreVolumeAsync(string volume, string tarPath, CancellationToken ct = default)
    {
        var create = await WslInterop.RunInDistroAsync(WslDistro, $"docker volume create {volume}", 60000);
        if (!create.IsSuccess)
            throw new Exception($"创建数据卷 {volume} 失败：{create.ErrorText}");
        var wslTar = WslInterop.ToWslPath(tarPath);
        var cmd =
            $"sh -c \"rm -rf /var/lib/docker/volumes/{volume}/_data && mkdir -p /var/lib/docker/volumes/{volume}/_data && tar xzf '{wslTar}' -C /var/lib/docker/volumes/{volume}/_data\"";
        var r = await WslInterop.RunInDistroAsync(WslDistro, cmd, 1800000);
        if (!r.IsSuccess)
            throw new Exception($"恢复数据卷 {volume} 失败：{r.ErrorText}");
    }

    /// <summary>
    /// 打开 Ayakaleaf 站点（系统默认浏览器）。
    /// </summary>
    public static void OpenSite()
    {
        Process.Start(new ProcessStartInfo(BaseUrl) { UseShellExecute = true });
    }
}
