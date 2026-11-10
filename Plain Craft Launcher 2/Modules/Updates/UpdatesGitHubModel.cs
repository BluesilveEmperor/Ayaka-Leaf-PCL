using System.IO;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.Utils;
using PCL.Network;
using PCL.Network.Loaders;

namespace PCL;

/// <summary>
///     GitHub Releases 更新源（AyakaLauncher 独立版本线）。
///     stable → /releases/latest；beta → /releases 中第一个 prerelease。
///     附件命名约定：AyakaLauncher_{配置}_{平台}.exe（x64/arm64），
///     伴生 &lt;附件名&gt;.sha256 文本文件提供 SHA256 校验值。
/// </summary>
public class UpdatesGitHubModel : IUpdateSource
{
    private const string Owner = "BluesilveEmperor";
    private const string Repo = "Ayaka-Leaf-PCL";
    private const string ApiBase = "https://api.github.com/repos/" + Owner + "/" + Repo;
    private const int CacheHours = 1;

    // GitHub API 响应缓存（1 小时，未认证限额 60 req/h/IP，足够）
    private JsonObject? _stableCache;
    private JsonObject? _betaCache;

    public string SourceName { get; set; } = "GitHub";

    public bool IsAvailable()
    {
        return true;
    }

    public bool RefreshCache()
    {
        // 缓存按需惰性加载（GetLatestValue 内部处理），无需预热
        return true;
    }

    public VersionDataModel GetLatestVersion(UpdateChannel channel, UpdateArch arch)
    {
        var release = GetRelease(channel);
        var asset = PickAsset(release.Assets, arch);
        if (asset is null)
            throw new Exception($"GitHub Release 中找不到 {arch} 平台的 exe 附件");

        var tagName = (release.TagName ?? "").Trim();
        var versionName = tagName.TrimStart('v', 'V');

        // sha256 伴生文件缺失不阻断版本检查（如旧版 Release 未按新约定上传），
        // 仅在真正下载并校验更新包时强制要求，避免"检查更新"因缺少校验文件而整体失败。
        string? sha256 = null;
        try
        {
            sha256 = GetAssetSha256(release, asset);
        }
        catch (Exception ex)
        {
            ModBase.Log($"[Update] 更新源未提供 SHA256 校验文件，暂不获取：{ex.Message}");
        }

        return new VersionDataModel
        {
            VersionName = versionName,
            VersionCode = 0, // 更新判断只依赖 SemVer，与现有源一致
            Sha256 = sha256 ?? "",
            Source = SourceName,
            Changelog = release.Body ?? ""
        };
    }

    public bool IsLatest(UpdateChannel channel, UpdateArch arch, SemVer currentVersion, int currentVersionCode)
    {
        var latestVersion = GetLatestVersion(channel, arch);
        return currentVersion >= SemVer.Parse(latestVersion.VersionName);
    }

    public VersionAnnouncementDataModel GetAnnouncementList()
    {
        // GitHub Releases 无公告体系
        throw new NotSupportedException("GitHub 源不提供公告");
    }

    public List<ModLoader.LoaderBase> GetDownloadLoader(UpdateChannel channel, UpdateArch arch, string output)
    {
        var loaders = new List<ModLoader.LoaderBase>();
        var tempPath = $@"{ModBase.pathTemp}Cache\Update\Download\GitHubLatest.bin";
        loaders.Add(new ModLoader.LoaderTask<int, List<DownloadFile>>(Lang.Text("Update.Task.GetVersionInfo"), load =>
        {
            var release = GetRelease(channel);
            var asset = PickAsset(release.Assets, arch);
            if (asset is null)
                throw new Exception($"GitHub Release 中找不到 {arch} 平台的 exe 附件");
            load.output = new List<DownloadFile> { new(BuildDownloadUrls(asset.BrowserDownloadUrl), tempPath) };
        }));
        loaders.Add(new LoaderDownload(Lang.Text("Update.Task.DownloadFile"), new List<DownloadFile>()));
        loaders.Add(new ModLoader.LoaderTask<string, int>(Lang.Text("Update.Task.ApplyFile"), _ =>
        {
            File.Copy(tempPath, output, true);
        }));
        return loaders;
    }

    #region GitHub API

    /// <summary>
    ///     获取指定通道的 Release（带 1 小时本地缓存，缓存文件以 LastWriteTime 判新鲜度）。
    /// </summary>
    private GitHubReleaseModel GetRelease(UpdateChannel channel)
    {
        var cacheField = channel == UpdateChannel.beta ? _betaCache : _stableCache;
        if (cacheField is not null)
            return cacheField.ToObject<GitHubReleaseModel>()!;

        var cacheFile = Path.Combine(ModBase.pathTemp, "Cache", "Update",
            $"github-{(channel == UpdateChannel.beta ? "beta" : "stable")}.json");
        string content;
        if (IsCacheFresh(cacheFile))
        {
            content = ModBase.ReadFile(cacheFile);
        }
        else
        {
            var url = channel == UpdateChannel.beta
                ? $"{ApiBase}/releases"
                : $"{ApiBase}/releases/latest";
            content = Requester.FetchString(url, new RequestParam
            {
                Accept = "application/vnd.github+json",
                UseBrowserUserAgent = true,
                Timeout = 30000,
                Retries = 3
            });
            ModBase.WriteFile(cacheFile, content);
        }

        var release = ParseRelease(content, channel);
        var parsed = (JsonObject)ModBase.GetJson(SerializeRelease(release));
        if (channel == UpdateChannel.beta)
            _betaCache = parsed;
        else
            _stableCache = parsed;
        return release;
    }

    private static GitHubReleaseModel ParseRelease(string content, UpdateChannel channel)
    {
        var json = ModBase.GetJson(content);
        if (channel == UpdateChannel.beta)
        {
            // /releases 返回数组（创建时间倒序），取第一个 prerelease
            if (json is not JsonArray array)
                throw new Exception("GitHub /releases 响应格式异常");
            foreach (var item in array)
            {
                if (item is null) continue;
                var release = item.ToObject<GitHubReleaseModel>();
                if (release?.Prerelease == true)
                    return release;
            }
            throw new Exception("GitHub 上没有 prerelease 版本");
        }
        return json.ToObject<GitHubReleaseModel>() ?? throw new Exception("GitHub Release 响应解析失败");
    }

    /// <summary>
    ///     将解析后的 Release 重新序列化为统一结构缓存到内存（去除对数组/字段顺序的依赖）。
    /// </summary>
    private static string SerializeRelease(GitHubReleaseModel release)
    {
        var node = new JsonObject();
        node["tag_name"] = release.TagName;
        node["name"] = release.Name;
        node["body"] = release.Body;
        node["prerelease"] = release.Prerelease;
        var assets = new JsonArray();
        foreach (var asset in release.Assets ?? [])
        {
            var assetNode = new JsonObject();
            assetNode["name"] = asset.Name;
            assetNode["browser_download_url"] = asset.BrowserDownloadUrl;
            assetNode["size"] = asset.Size;
            assets.Add(assetNode);
        }
        node["assets"] = assets;
        return node.ToJsonString();
    }

    private static bool IsCacheFresh(string path)
    {
        var info = new FileInfo(path);
        return info.Exists && (DateTime.Now - info.LastWriteTime).TotalHours < CacheHours;
    }

    /// <summary>
    ///     按平台匹配 exe 附件（排除 .sha256 伴生文件）；arm64 判定优先于 x64。
    /// </summary>
    private static GitHubAssetModel? PickAsset(List<GitHubAssetModel> assets, UpdateArch arch)
    {
        return assets?.FirstOrDefault(a =>
        {
            var name = a.Name ?? "";
            if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                return false;
            var lower = name.ToLowerInvariant();
            return arch == UpdateArch.arm64
                ? lower.Contains("arm64")
                : lower.Contains("x64") && !lower.Contains("arm64");
        });
    }

    /// <summary>
    ///     读取伴生 &lt;附件名&gt;.sha256 文本附件内容（同 Release 一并缓存到本地，tag 维度唯一）。
    /// </summary>
    private static string GetAssetSha256(GitHubReleaseModel release, GitHubAssetModel asset)
    {
        var shaAsset = release.Assets?.FirstOrDefault(a =>
            (a.Name ?? "").Equals(asset.Name + ".sha256", StringComparison.OrdinalIgnoreCase));
        if (shaAsset is null)
            throw new Exception($"GitHub Release 缺少 {asset.Name}.sha256 校验文件");

        var cacheFile = Path.Combine(ModBase.pathTemp, "Cache", "Update",
            $"github-{(release.TagName ?? "unknown").TrimStart('v', 'V')}.sha256");
        if (File.Exists(cacheFile))
            return ModBase.ReadFile(cacheFile).Trim();

        // sha256 文件极小，加速前缀优先，失败回退直连
        var content = "";
        foreach (var url in BuildDownloadUrls(shaAsset.BrowserDownloadUrl))
        {
            try
            {
                content = Requester.FetchString(url, new RequestParam { Timeout = 15000, Retries = 1 });
                if (!string.IsNullOrWhiteSpace(content)) break;
            }
            catch (Exception ex)
            {
                ModBase.Log(ex, $"[Update] 下载 sha256 校验文件失败：{url}");
            }
        }
        if (string.IsNullOrWhiteSpace(content))
            throw new Exception($"无法下载 {shaAsset.Name} 校验文件");
        ModBase.WriteFile(cacheFile, content);
        return content.Trim();
    }

    /// <summary>
    ///     构造下载候选 URL：ghfast.top / gh-proxy.com 加速优先，直连兜底（国内可达性）。
    /// </summary>
    private static IEnumerable<string> BuildDownloadUrls(string browserDownloadUrl)
    {
        return new[]
        {
            $"https://ghfast.top/{browserDownloadUrl}",
            $"https://gh-proxy.com/{browserDownloadUrl}",
            browserDownloadUrl
        };
    }

    #endregion

    #region GitHub API 响应模型

    private class GitHubReleaseModel
    {
        [JsonPropertyName("tag_name")] public string? TagName { get; set; }

        [JsonPropertyName("name")] public string? Name { get; set; }

        [JsonPropertyName("body")] public string? Body { get; set; }

        [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }

        [JsonPropertyName("assets")] public List<GitHubAssetModel>? Assets { get; set; }
    }

    private class GitHubAssetModel
    {
        [JsonPropertyName("name")] public string? Name { get; set; }

        [JsonPropertyName("browser_download_url")] public string? BrowserDownloadUrl { get; set; }

        [JsonPropertyName("size")] public long Size { get; set; }
    }

    #endregion
}
