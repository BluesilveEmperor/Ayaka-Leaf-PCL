using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using PCL.Core.App;
using PCL.Core.Logging;

namespace PCL.Core.Ayakaleaf.Collab;

/// <summary>组件下载进度。</summary>
public sealed record CollabDownloadProgress(long Received, long Total);

/// <summary>
/// EasyTier 组件（Apache-2.0，独立组网工具）下载与校验。
/// 首次使用时从官方发布渠道下载到 {数据目录}\collab\easytier，
/// 每个文件逐一 SHA-256 校验，任一不符立即丢弃换源，防止篡改与损坏。
/// </summary>
public static class EasyTierPackage
{
    /// <summary>组件版本（固定）。</summary>
    public const string Version = "2.6.4";

    private const string CoreFile = "easytier-core.exe";
    private const string CliFile = "easytier-cli.exe";
    private const string PacketFile = "Packet.dll";

    private static readonly string[] Files = [CoreFile, CliFile, PacketFile];

    // 官方 v2.6.4 发布包内各文件的 SHA-256（与 Files 顺序对应）
    private static readonly Dictionary<string, string[]> Hashes = new()
    {
        ["x86_64"] =
        [
            "da7eb2d24b5416f3d3407636949e964a0750e3f9dc53a828cb6799a57ead445d",
            "d8783e851e944b44a9b71b39fd02f227ec0a2a82b3165c55ead5dd32dcde53a1",
            "c7c03a87eac7243ccbe331554624b18803010b740e311fc8cfddb573096eacac",
        ],
        ["arm64"] =
        [
            "da912e2befd4c59c81228c0dac7e19f0190aa4221475117108ad2f0045b3065d",
            "295852b6d68ab5c675437f19f37e975e90d41814ec76eabc135d3eb367bfccd0",
            "fe56bf638c1f05751a4f36377d71f48fb7f43446e92475eda32dc0f4f2ebd60c",
        ],
    };

    private static readonly object Sync = new();
    private static bool _verified;

    private static readonly HttpClient Http = CreateClient();

    /// <summary>当前处理器架构对应的组件标识："x86_64" 或 "arm64"。</summary>
    public static string Architecture { get; } = RuntimeInformation.ProcessArchitecture switch
    {
        System.Runtime.InteropServices.Architecture.X64 => "x86_64",
        System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
        _ => throw new PlatformNotSupportedException(
            $"远程协作组件不支持当前处理器架构：{RuntimeInformation.ProcessArchitecture}"),
    };

    /// <summary>组件安装目录（便携，随数据目录存放）。</summary>
    public static string InstallDirectory { get; } = Path.Combine(
        string.IsNullOrEmpty(Paths.Data) ? AppContext.BaseDirectory : Paths.Data,
        "collab", "easytier");

    public static string CorePath => Path.Combine(InstallDirectory, CoreFile);

    public static string CliPath => Path.Combine(InstallDirectory, CliFile);

    /// <summary>组件是否完整（三个文件齐全且哈希校验通过；每次进程运行内校验一次）。</summary>
    public static bool IsInstalled()
    {
        lock (Sync)
        {
            if (_verified) return true;
            _verified = Files.All(IsFileValid);
            return _verified;
        }
    }

    /// <summary>
    /// 下载缺失或损坏的组件，按官方 Gitee → GitHub → 镜像源顺序尝试，全部失败抛出异常。
    /// </summary>
    public static async Task InstallAsync(IProgress<CollabDownloadProgress>? progress, CancellationToken cancellation)
    {
        if (await Task.Run(IsInstalled, cancellation).ConfigureAwait(false)) return;

        Directory.CreateDirectory(InstallDirectory);
        Exception? last = null;
        foreach (var url in GetSourceUrls())
        {
            try
            {
                LogWrapper.Info("Collab", $"开始从 {url} 下载协作组件…");
                await DownloadAsync(url, progress, cancellation).ConfigureAwait(false);
                lock (Sync)
                {
                    _verified = Files.All(IsFileValid);
                }
                if (_verified)
                {
                    LogWrapper.Info("Collab", "协作组件下载并校验完成");
                    return;
                }
                throw new InvalidDataException("下载的组件文件校验失败");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                last = ex;
                LogWrapper.Warn(ex, "Collab", $"下载源 {url} 失败");
            }
        }
        throw new InvalidOperationException("协作组件下载失败：所有下载源均不可用，请检查网络连接", last);
    }

    private static async Task DownloadAsync(string url, IProgress<CollabDownloadProgress>? progress, CancellationToken cancellation)
    {
        string archive = Path.Combine(InstallDirectory, "download.zip.tmp");
        try
        {
            using (var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                long total = response.Content.Headers.ContentLength ?? 0;
                long received = 0;
                await using (var network = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false))
                await using (var output = File.Create(archive))
                {
                    var buffer = new byte[81920];
                    int read;
                    while ((read = await network.ReadAsync(buffer, cancellation).ConfigureAwait(false)) > 0)
                    {
                        await output.WriteAsync(buffer.AsMemory(0, read), cancellation).ConfigureAwait(false);
                        received += read;
                        progress?.Report(new CollabDownloadProgress(received, total));
                    }
                }
            }

            using var zip = ZipFile.OpenRead(archive);
            foreach (string file in Files.Where(f => !IsFileValid(f)))
            {
                var entry = zip.Entries.FirstOrDefault(e => MatchesEntry(e.FullName, file))
                    ?? throw new InvalidDataException($"发布包内未找到 {file}");
                string temp = Path.Combine(InstallDirectory, file + ".tmp");
                entry.ExtractToFile(temp, overwrite: true);
                CommitFile(file, temp);
            }
        }
        finally
        {
            TryDelete(archive);
        }
    }

    /// <summary>校验临时文件哈希后移动到位；校验失败丢弃并抛出异常。</summary>
    private static void CommitFile(string file, string temp)
    {
        string target = Path.Combine(InstallDirectory, file);
        if (!string.Equals(ComputeHash(temp), ExpectedHash(file), StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(temp);
            throw new InvalidDataException($"{file} 哈希校验失败");
        }
        TryDelete(target);
        File.Move(temp, target);
    }

    private static IEnumerable<string> GetSourceUrls()
    {
        string asset = $"easytier-windows-{Architecture}-v{Version}.zip";
        string github = $"https://github.com/EasyTier/EasyTier/releases/download/v{Version}/{asset}";
        yield return $"https://gitee.com/easytier/EasyTier/releases/download/v{Version}/{asset}";
        yield return github;
        yield return "https://ghfast.top/" + github;
        yield return "https://gh-proxy.com/" + github;
    }

    private static bool IsFileValid(string file)
    {
        string path = Path.Combine(InstallDirectory, file);
        try
        {
            return File.Exists(path)
                && string.Equals(ComputeHash(path), ExpectedHash(file), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string ExpectedHash(string file) => Hashes[Architecture][Array.IndexOf(Files, file)];

    private static string ComputeHash(string path)
    {
        using var sha = SHA256.Create();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(sha.ComputeHash(stream));
    }

    private static bool MatchesEntry(string entryName, string file)
    {
        string name = entryName.Replace('\\', '/');
        return name.EndsWith("/" + file, StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, file, StringComparison.OrdinalIgnoreCase);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true })
        {
            Timeout = TimeSpan.FromMinutes(10),
        };
        string version = typeof(EasyTierPackage).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
        client.DefaultRequestHeaders.UserAgent.ParseAdd("AyakaLauncher/" + version);
        return client;
    }
}
