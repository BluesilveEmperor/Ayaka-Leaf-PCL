using System;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PCL.Core.Ayakaleaf;

/// <summary>PDF 反向同步定位结果状态（US7）。</summary>
public enum SyncTexReverseStatus
{
    /// <summary>命中：定位到源文件与行号。</summary>
    Hit,

    /// <summary>未命中：点击位置没有对应的 SyncTeX 记录，或编译产物缺失（HTTP 404）。</summary>
    NoHit,

    /// <summary>定位通道不可用：参数不完整、网络失败或服务返回异常状态。</summary>
    Unavailable,
}

/// <summary>
/// PDF 反向同步定位结果（US7）。<paramref name="File"/> 为项目内相对路径
/// （如 <c>main.tex</c> 或 <c>chapters/intro.tex</c>），<paramref name="Line"/> 为 1 基行号。
/// </summary>
/// <param name="Status">定位结果状态。</param>
/// <param name="File">命中的源文件相对路径（仅 <see cref="SyncTexReverseStatus.Hit"/> 时有值）。</param>
/// <param name="Line">命中行号（1 基；仅 Hit 时有意义）。</param>
/// <param name="Column">命中列号（0 基，服务端原样透传，仅供诊断）。</param>
/// <param name="Error">不可用时的原因描述（仅 <see cref="SyncTexReverseStatus.Unavailable"/> 时有值）。</param>
public sealed record SyncTexReverseResult(
    SyncTexReverseStatus Status,
    string? File,
    int Line,
    int Column,
    string? Error)
{
    /// <summary>命中：定位到 <paramref name="file"/> 的 <paramref name="line"/> 行。</summary>
    public static SyncTexReverseResult Hit(string file, int line, int column) =>
        new(SyncTexReverseStatus.Hit, file, line, column, null);

    /// <summary>未命中：该位置无 SyncTeX 记录。</summary>
    public static SyncTexReverseResult Miss() =>
        new(SyncTexReverseStatus.NoHit, null, 0, 0, null);

    /// <summary>不可用：定位通道异常，<paramref name="error"/> 为原因。</summary>
    public static SyncTexReverseResult Unavailable(string error) =>
        new(SyncTexReverseStatus.Unavailable, null, 0, 0, error);
}

/// <summary>
/// PDF 反向同步定位服务（US7 / FR-009）：把 PDF 页面坐标转换为源文件与行号，
/// 供工作台「单击 PDF 跳回代码」使用。
/// <para>
/// 端点契约（镜像内 <c>web/app/src/Features/Compile/CompileController.mjs</c>，2026-11 实证）：
/// <c>GET /project/:Project_id/sync/pdf?page&amp;h&amp;v&amp;clsiserverid&amp;editorId&amp;buildId</c>，
/// 会话经 <c>overleaf.sid</c> Cookie 鉴权；<c>editorId</c>/<c>buildId</c> 被服务端强制校验
/// （<c>/^[a-f0-9-]+$/</c>，缺失即 500），<c>clsiserverid</c> 可选；
/// 响应为 <c>{code: [{file, line, column}], downloadedFromCache}</c>，
/// 编译产物缺失时返回 404 空体。行号 1 基，与官方 <c>goToCodeLine</c> 直接兼容。
/// </para>
/// <para>
/// 坐标语义：page 为 1 基页码，h 为距页面左缘的横向偏移（bp），
/// v 为距页面顶部的纵向偏移（bp）——与官方前端 <c>_syncToCode</c> 在无
/// pageSize 分支（<c>clickPosition</c> 产物）下的取值一致。
/// </para>
/// <para>
/// 本服务持有独立 HttpClient（不走系统代理、不共享账号服务的 Cookie 容器），
/// 会话 Cookie 按请求手动附加，避免与登录流程的会话状态互串。
/// </para>
/// </summary>
public static class SyncTexReverseService
{
    /// <summary>反向定位请求超时：本地服务的常规查询应远低于此值（FR-009 要求常规文档 ≤1s）。</summary>
    private static readonly TimeSpan LocateTimeout = TimeSpan.FromSeconds(5);

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            UseProxy = false, // 本地服务，绝不能走系统代理
            UseCookies = false, // 会话 Cookie 按请求手动附加，避免多会话互串
            AllowAutoRedirect = false, // 会话失效时的 /login 跳转按异常处理，不跟随
        };
        return new HttpClient(handler) { Timeout = LocateTimeout };
    }

    /// <summary>
    /// 反向定位：PDF 页面坐标 → 源文件/行号。
    /// 任何失败都不抛异常，以 <see cref="SyncTexReverseResult"/> 状态返回，供调用方决定回退策略。
    /// </summary>
    /// <param name="origin">服务地址（含协议与端口，须与工作台页面同源，如 <c>http://localhost:8085</c>）。</param>
    /// <param name="sessionId"><c>overleaf.sid</c> 会话标识。</param>
    /// <param name="projectId">Overleaf 项目标识（ObjectId）。</param>
    /// <param name="page">页码（1 基）。</param>
    /// <param name="h">距页面左缘的横向偏移（bp）。</param>
    /// <param name="v">距页面顶部的纵向偏移（bp）。</param>
    /// <param name="editorId">编译上下文的编辑器标识（服务端强制要求）。</param>
    /// <param name="buildId">编译产物标识（服务端强制要求）。</param>
    /// <param name="clsiServerId">CLSI 服务标识（可选，多 CLSI 部署时用于路由）。</param>
    /// <param name="ct">取消令牌。</param>
    public static async Task<SyncTexReverseResult> LocateAsync(
        string origin,
        string sessionId,
        string projectId,
        int page,
        double h,
        double v,
        string? editorId,
        string? buildId,
        string? clsiServerId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(origin) ||
            string.IsNullOrWhiteSpace(sessionId) ||
            string.IsNullOrWhiteSpace(projectId))
            return SyncTexReverseResult.Unavailable("反向定位参数不完整（地址/会话/项目标识缺失）。");
        if (page < 1)
            return SyncTexReverseResult.Unavailable($"反向定位页码非法：{page}。");
        if (!double.IsFinite(h) || !double.IsFinite(v))
            return SyncTexReverseResult.Unavailable("反向定位坐标非法。");
        // web 端 proxySyncPdf 强制要求 editorId/buildId；尚未观察到编译上下文时
        // 官方双击反向同步同样无法工作，按未命中处理（页面侧静默，不回退弹错）
        if (string.IsNullOrWhiteSpace(editorId) || string.IsNullOrWhiteSpace(buildId))
            return SyncTexReverseResult.Miss();

        var query =
            "?page=" + page.ToString(CultureInfo.InvariantCulture) +
            "&h=" + h.ToString("0.##", CultureInfo.InvariantCulture) +
            "&v=" + v.ToString("0.##", CultureInfo.InvariantCulture);
        if (!string.IsNullOrEmpty(clsiServerId))
            query += "&clsiserverid=" + Uri.EscapeDataString(clsiServerId);
        query += "&editorId=" + Uri.EscapeDataString(editorId);
        query += "&buildId=" + Uri.EscapeDataString(buildId);

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            origin + "/project/" + Uri.EscapeDataString(projectId) + "/sync/pdf" + query);
        request.Headers.TryAddWithoutValidation("Cookie", "overleaf.sid=" + sessionId);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");

        HttpResponseMessage response;
        try
        {
            response = await Http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 含超时（TaskCanceledException）与调用方取消：均按通道不可用返回，
            // 由调用方决定回退策略；本服务不向调用方抛异常
            return SyncTexReverseResult.Unavailable("反向定位请求失败：" + ex.Message);
        }

        using (response)
        {
            var code = (int)response.StatusCode;
            // 编译产物缺失：与官方一致（NotFoundError → 404 空体），按未命中处理
            if (code == 404)
                return SyncTexReverseResult.Miss();
            if (!response.IsSuccessStatusCode)
                return SyncTexReverseResult.Unavailable($"反向定位被拒绝（HTTP {code}）。");

            try
            {
                await using var stream = await response.Content
                    .ReadAsStreamAsync(ct).ConfigureAwait(false);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct)
                    .ConfigureAwait(false);

                var root = doc.RootElement;
                if (!root.TryGetProperty("code", out var codeArray) ||
                    codeArray.ValueKind != JsonValueKind.Array ||
                    codeArray.GetArrayLength() == 0)
                    return SyncTexReverseResult.Miss();

                // 官方 goToCodeLine 取 code[0]（file/line），column 仅诊断用途
                var first = codeArray[0];
                var file = first.TryGetProperty("file", out var fileElement) &&
                           fileElement.ValueKind == JsonValueKind.String
                    ? fileElement.GetString()
                    : null;
                var line = first.TryGetProperty("line", out var lineElement) &&
                           lineElement.ValueKind == JsonValueKind.Number &&
                           lineElement.TryGetInt32(out var lineNumber)
                    ? lineNumber
                    : 0;
                var column = first.TryGetProperty("column", out var columnElement) &&
                             columnElement.ValueKind == JsonValueKind.Number &&
                             columnElement.TryGetInt32(out var columnNumber)
                    ? columnNumber
                    : 0;

                if (string.IsNullOrWhiteSpace(file) || line < 1)
                    return SyncTexReverseResult.Miss();

                return SyncTexReverseResult.Hit(file!, line, column);
            }
            catch (JsonException ex)
            {
                return SyncTexReverseResult.Unavailable("反向定位响应解析失败：" + ex.Message);
            }
        }
    }
}
