using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PCL.Core.Ayakaleaf;

/// <summary>
/// TeX Live 宏包条目（US4 宏包管理面板的数据行）。
/// </summary>
/// <param name="Name">tlmgr 包名，如 <c>mhchem</c>。</param>
/// <param name="ShortDesc">一句话描述（来自 tlmgr search / info）。</param>
/// <param name="Installed">当前镜像内是否已安装。</param>
/// <param name="Category">tlmgr 分类（Package / Collection / Scheme），未知为空。</param>
public sealed record TexPackageInfo(string Name, string ShortDesc, bool Installed, string Category)
{
    /// <summary>集合类条目的展示名（不可单独卸载，提示用户改由依赖带入）。</summary>
    public bool IsCollection => Category is "Collection" or "Scheme";
}

/// <summary>
/// 宏包操作结果。失败时 <see cref="Output"/> 保留 tlmgr 原始输出，便于面板展示与排错。
/// </summary>
public sealed record PackageOperationResult(bool Success, string Output)
{
    public static PackageOperationResult Ok(string output) => new(true, output);
    public static PackageOperationResult Fail(string output) => new(false, output);
}

/// <summary>
/// TeX Live 宏包管理（US4，FR-007）：搜索、安装、卸载、查询状态。
/// <para>
/// 全部走 <see cref="WslcInterop"/> 在 overleaf 容器内执行 tlmgr —— 与启动器其余
/// 容器操作同一条通道，不额外暴露 docker socket，也不要求用户在容器里敲命令。
/// </para>
/// <para>
/// 解析职责在本类，<see cref="WslcInterop"/> 只负责执行与超时，符合 C1 契约。
/// </para>
/// </summary>
public static class PackageManagerService
{
    private const string Container = AyakaleafService.OverleafContainer;

    /// <summary>tlmgr 直连 CTAN 可能很慢，搜索与安装给足超时。</summary>
    private const int SearchTimeoutMs = 120_000;
    private const int InstallTimeoutMs = 30 * 60_000;

    /// <summary>容器是否可执行 tlmgr（服务已启动且镜像带 TeX Live）。</summary>
    public static async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        var r = await RunAsync($"exec {Container} tlmgr --version", 30_000, ct).ConfigureAwait(false);
        return r.IsSuccess && r.Stdout.Contains("tlmgr");
    }

    /// <summary>
    /// 按关键字搜索宏包（名称 + 描述都能命中），标注已安装状态。
    /// </summary>
    /// <param name="keyword">关键字，空字符串时返回已安装列表前 <paramref name="limit"/> 条。</param>
    /// <param name="limit">最多返回条数（默认 60，界面一次渲染不宜过多）。</param>
    public static async Task<IReadOnlyList<TexPackageInfo>> SearchAsync(
        string keyword, int limit = 60, CancellationToken ct = default)
    {
        keyword = (keyword ?? "").Trim();
        var installed = await GetInstalledNamesAsync(ct).ConfigureAwait(false);

        if (keyword.Length == 0)
        {
            return installed
                .Take(limit)
                .Select(n => new TexPackageInfo(n, "", true, ""))
                .ToList();
        }

        // --global: 连未安装的一起搜；--keyword: 匹配名称与描述
        var r = await RunAsync(
            $"exec {Container} tlmgr search --global --keyword {Escape(keyword)}",
            SearchTimeoutMs, ct).ConfigureAwait(false);

        if (!r.IsSuccess)
        {
            // 关键字含特殊字符等场景，退化为按名称精确匹配
            var exact = await GetInfoAsync(keyword, installed, ct).ConfigureAwait(false);
            return exact is null ? Array.Empty<TexPackageInfo>() : new[] { exact };
        }

        return ParseSearchOutput(r.Stdout, installed).Take(limit).ToList();
    }

    /// <summary>安装一个或多个宏包（tlmgr 会自动带入依赖），实时回报进度行。</summary>
    public static async Task<PackageOperationResult> InstallAsync(
        IEnumerable<string> names, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var list = names.Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (list.Count == 0) return PackageOperationResult.Ok("没有需要安装的宏包");

        var sb = new StringBuilder();
        var result = await RunStreamingAsync(
            $"exec {Container} tlmgr install {string.Join(" ", list.Select(Escape))}",
            line =>
            {
                sb.AppendLine(line);
                // tlmgr 的进度行形如 "[12/40, 00:03/00:21] install: foo"，原样交给界面
                if (line.StartsWith("["))
                    progress?.Report(line.Trim());
                else if (line.StartsWith("install: ") || line.StartsWith("tlmgr: "))
                    progress?.Report(line.Trim());
            },
            InstallTimeoutMs, ct).ConfigureAwait(false);

        // 退出码 + 状态复查双重确认（C 契约要求）
        var missing = new List<string>();
        foreach (var name in list)
        {
            if (!await IsInstalledAsync(name, ct).ConfigureAwait(false)) missing.Add(name);
        }

        var ok = result.IsSuccess && missing.Count == 0;
        var detail = sb.ToString();
        if (!ok && missing.Count > 0)
            detail += Environment.NewLine + "未确认安装：" + string.Join(", ", missing);
        return new PackageOperationResult(ok, detail);
    }

    /// <summary>
    /// 卸载宏包。集合类条目拒绝卸载（会连带拆掉自动依赖，破坏镜像约定）。
    /// </summary>
    public static async Task<PackageOperationResult> RemoveAsync(
        string name, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0) return PackageOperationResult.Fail("宏包名为空");

        var installed = await GetInstalledNamesAsync(ct).ConfigureAwait(false);
        var info = await GetInfoAsync(name, installed, ct).ConfigureAwait(false);
        if (info is null) return PackageOperationResult.Fail($"镜像内没有名为 {name} 的宏包");
        if (info.IsCollection)
            return PackageOperationResult.Fail($"{name} 是集合（{info.Category}），不支持单独卸载");

        var sb = new StringBuilder();
        var r = await RunStreamingAsync(
            $"exec {Container} tlmgr remove {Escape(name)}",
            line =>
            {
                sb.AppendLine(line);
                if (line.StartsWith("remove: ") || line.StartsWith("tlmgr: "))
                    progress?.Report(line.Trim());
            },
            InstallTimeoutMs, ct).ConfigureAwait(false);

        var stillThere = await IsInstalledAsync(name, ct).ConfigureAwait(false);
        return new PackageOperationResult(r.IsSuccess && !stillThere, sb.ToString());
    }

    /// <summary>单包安装状态（tlmgr info --only-installed 的退出码即结论）。</summary>
    public static async Task<bool> IsInstalledAsync(string name, CancellationToken ct = default)
    {
        var r = await RunAsync(
            $"exec {Container} tlmgr info --only-installed {Escape(name)}",
            30_000, ct).ConfigureAwait(false);
        return r.IsSuccess && r.Stdout.Contains("package:", StringComparison.Ordinal);
    }

    /// <summary>已安装包名清单（失败返回空集合，界面按“不可用”处理）。</summary>
    public static async Task<IReadOnlyList<string>> GetInstalledNamesAsync(CancellationToken ct = default)
    {
        var r = await RunAsync(
            $"exec {Container} tlmgr info --only-installed --data name",
            60_000, ct).ConfigureAwait(false);
        if (!r.IsSuccess) return Array.Empty<string>();

        return r.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => !l.StartsWith("tlmgr:", StringComparison.Ordinal))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(l => l, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // ---------------- 解析 ----------------

    /// <summary>
    /// 解析 <c>tlmgr search --global --keyword</c> 输出：
    /// 包名单独成行，其后以空白开头的行为描述。
    /// </summary>
    internal static IEnumerable<TexPackageInfo> ParseSearchOutput(string output, IReadOnlyCollection<string> installed)
    {
        string? current = null;
        var desc = new StringBuilder();

        IEnumerable<TexPackageInfo> Flush()
        {
            if (current is null) yield break;
            var name = current;
            var text = desc.ToString().Trim().Replace('\t', ' ').Trim();
            var category = name.StartsWith("collection-", StringComparison.Ordinal) ? "Collection"
                : name.StartsWith("scheme-", StringComparison.Ordinal) ? "Scheme"
                : "Package";
            desc.Clear();
            current = null;
            yield return new TexPackageInfo(name, text, installed.Contains(name), category);
        }

        var items = new List<TexPackageInfo>();
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;
            if (line.StartsWith("tlmgr:", StringComparison.Ordinal)) continue;

            var isContinuation = line[0] == ' ' || line[0] == '\t';
            if (!isContinuation)
            {
                items.AddRange(Flush());
                current = line.TrimEnd(':').Trim();
            }
            else if (current != null)
            {
                if (desc.Length > 0) desc.Append(' ');
                desc.Append(line.Trim());
            }
        }

        items.AddRange(Flush());
        return items.Where(i => i.Name.Length > 0);
    }

    private static async Task<TexPackageInfo?> GetInfoAsync(
        string name, IReadOnlyCollection<string> installed, CancellationToken ct)
    {
        var r = await RunAsync($"exec {Container} tlmgr info {Escape(name)}", 60_000, ct).ConfigureAwait(false);
        if (!r.IsSuccess) return null;

        string? shortDesc = null, category = null;
        foreach (var raw in r.Stdout.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("shortdesc:", StringComparison.Ordinal))
                shortDesc = line["shortdesc:".Length..].Trim();
            else if (line.StartsWith("category:", StringComparison.Ordinal))
                category = line["category:".Length..].Trim();
        }

        if (shortDesc is null && category is null) return null;
        return new TexPackageInfo(name, shortDesc ?? "", installed.Contains(name), category ?? "Package");
    }

    /// <summary>参数转义：包名允许字母数字与 <c>. _ + -</c>，其余加单引号保护。</summary>
    private static string Escape(string value)
    {
        if (value.All(c => char.IsLetterOrDigit(c) || c is '.' or '_' or '+' or '-')) return value;
        return "\"" + value.Replace("\"", "") + "\"";
    }

    private static Task<WslResult> RunAsync(string arguments, int timeoutMs, CancellationToken ct) =>
        WslcInterop.RunAsync(arguments, timeoutMs, ct);

    private static Task<WslResult> RunStreamingAsync(
        string arguments, Action<string> onLine, int timeoutMs, CancellationToken ct) =>
        WslcInterop.RunStreamingAsync(arguments, onLine, null, timeoutMs, ct);
}
