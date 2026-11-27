using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using PCL.Core.Logging;

namespace PCL.Core.Ayakaleaf.Collab;

/// <summary>
/// 面向房主转发地址的协作账号注册/登录 HTTP 客户端（受邀端专用，实例化）。
/// <para>
/// 与 <see cref="AyakaleafAccountService"/> 的既有语义逐条对齐但不复用其静态实例：
/// 本客户端面向**端口转发地址**（<c>http://localhost:&lt;主转发端口&gt;</c>）而非本机服务，
/// 持有独立的 <see cref="HttpClient"/> 与 <see cref="CookieContainer"/>，互不污染全局会话。
/// 对齐点：注册/登录表单字段、CSRF 解析正则、预热重试（5 次 × 2 秒）、
/// 「已存在」冲突判定、只认本次 Set-Cookie 的 sid 判定、错误分类（复用
/// <see cref="AyakaleafAccountException"/> 与其 Kind 枚举）。
/// </para>
/// <para>CollabLite（<c>tools/collab-lite/CollabLite.ps1</c>）以 PowerShell 等价实现本类契约，
/// 文件头注释标注对齐位置；修改本类必须同步轻量端。</para>
/// </summary>
public sealed class CollabAccountClient : IDisposable
{
    /// <summary>CSRF 预热重试次数（容器刚启动时 Overleaf Web 尚在预热，属瞬时状态）。</summary>
    private const int CsrfReadyRetryCount = 5;

    /// <summary>CSRF 预热重试间隔。</summary>
    private static readonly TimeSpan CsrfReadyRetryDelay = TimeSpan.FromSeconds(2);

    private readonly string _baseUrl;
    private readonly CookieContainer _cookies = new();
    private readonly HttpClient _http;

    /// <summary>
    /// 创建协作账号客户端。
    /// </summary>
    /// <param name="baseUrl">房主服务的本地转发地址（如 <c>http://localhost:8080</c>）。</param>
    public CollabAccountClient(string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new ArgumentException("转发地址不能为空", nameof(baseUrl));

        _baseUrl = baseUrl.TrimEnd('/');
        var handler = new HttpClientHandler
        {
            UseProxy = false, // 本地转发，绝不能走系统代理
            UseCookies = true,
            CookieContainer = _cookies,
            AllowAutoRedirect = false,
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
    }

    /// <summary>注册协作账号（服务端已存在同名邮箱时显式失败，语义对齐 AyakaleafAccountService.RegisterAsync）。</summary>
    /// <param name="account">待注册的协作账号（密码仅存在于内存，不落日志）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <exception cref="AyakaleafAccountException">
    /// 服务不可用 / 未就绪 / 账号冲突（<see cref="AyakaleafAccountErrorKind.AccountConflict"/>）/ 注册被拒。
    /// </exception>
    public async Task RegisterAsync(AyakaleafAccount account, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(account);

        var csrf = await _GetCsrfTokenAsync(ct).ConfigureAwait(false);
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["_csrf"] = csrf,
            ["email"] = account.Email,
            ["password"] = account.Password,
        });

        HttpResponseMessage response;
        try
        {
            response = await _http.PostAsync(_baseUrl + "/register", form, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (_IsConnectionFailure(ex))
        {
            throw _ServiceUnavailable(ex);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw _ServiceUnavailable(null);
        }

        using (response)
        {
            var code = (int)response.StatusCode;
            if (code == 200) return; // 成功：{redir:'/login'}

            var body = await _ReadBodyAsync(response, ct).ConfigureAwait(false);

            // 服务端已存在同名邮箱（本机凭据丢失重装的典型场景）：
            // 显式失败并给出可操作建议，禁止用未验证的随机密码冒充成功
            if (code is 400 or 409 or 422 && _ContainsAlreadyRegistered(body))
                throw _AccountConflict(account.Email);

            if (code is 400 or 409 or 422)
                throw _RegistrationRejected(account.Email, code, body);

            throw _ServiceNotReady(code);
        }
    }

    /// <summary>
    /// 登录协作账号并返回本次会话 Cookie（overleaf.sid）。
    /// 成功判定只认本次响应：必须出现 <c>Set-Cookie: overleaf.sid=</c> 且容器中能取到非空值
    /// （禁止复用上一次会话的陈旧 Cookie），语义对齐 AyakaleafAccountService.LoginAsync。
    /// </summary>
    /// <param name="account">已注册的协作账号。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>本次登录响应下发的 overleaf.sid 值。</returns>
    /// <exception cref="AyakaleafAccountException">
    /// 服务不可用 / 未就绪 / 登录失败（<see cref="AyakaleafAccountErrorKind.CredentialInvalid"/>，
    /// 按「已被房主停用」与「密码不符」区分文案）。
    /// </exception>
    public async Task<string> LoginAsync(AyakaleafAccount account, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(account);

        var csrf = await _GetCsrfTokenAsync(ct).ConfigureAwait(false);
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["_csrf"] = csrf,
            ["email"] = account.Email,
            ["password"] = account.Password,
        });

        HttpResponseMessage response;
        try
        {
            response = await _http.PostAsync(_baseUrl + "/login", form, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (_IsConnectionFailure(ex))
        {
            throw _ServiceUnavailable(ex);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw _ServiceUnavailable(null);
        }

        using (response)
        {
            var code = (int)response.StatusCode;
            var sid = _ReadCookie("overleaf.sid");

            // 成功判定只认本次响应：必须下发新的 overleaf.sid 且容器中能取到非空值
            if (!_ResponseSetsSessionCookie(response) || string.IsNullOrEmpty(sid))
                throw _CredentialInvalid(account.Email, code, await _ReadBodyAsync(response, ct).ConfigureAwait(false));

            return sid;
        }
    }

    public void Dispose() => _http.Dispose();

    // ---------------- HTTP 辅助（语义对齐 AyakaleafAccountService） ----------------

    /// <summary>取房主登录页 CSRF 令牌；服务预热期按 5 次 × 2 秒退避重试，超限才抛服务未就绪。</summary>
    private async Task<string> _GetCsrfTokenAsync(CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            HttpResponseMessage response;
            try
            {
                response = await _http.GetAsync(_baseUrl + "/login", ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (_IsConnectionFailure(ex))
            {
                throw _ServiceUnavailable(ex);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw _ServiceUnavailable(null);
            }

            int? statusCode = null;
            using (response)
            {
                if (response.IsSuccessStatusCode)
                {
                    // 正则沿用既有实现，保持对 Overleaf 登录页的兼容
                    var body = await _ReadBodyAsync(response, ct).ConfigureAwait(false);
                    var match = Regex.Match(body, "ol-csrfToken\" content=\"([^\"]+)\"");
                    if (match.Success)
                        return match.Groups[1].Value;
                }
                else
                {
                    statusCode = (int)response.StatusCode;
                }
            }

            if (attempt >= CsrfReadyRetryCount)
                throw _ServiceNotReady(statusCode);

            LogWrapper.Warn(
                "[Collab] 房主登录页尚未就绪（第 " + attempt + "/" + CsrfReadyRetryCount + " 次探测失败" +
                (statusCode is null ? "" : "，HTTP " + statusCode) +
                "），" + CsrfReadyRetryDelay.TotalSeconds.ToString("0") + "s 后重试");
            await Task.Delay(CsrfReadyRetryDelay, ct).ConfigureAwait(false);
        }
    }

    /// <summary>读取响应体（读取失败按空响应体处理，不覆盖原始状态码语义）。</summary>
    private static async Task<string> _ReadBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>判断本次响应是否下发了新的会话 Cookie（<c>Set-Cookie: overleaf.sid=…</c>）。</summary>
    private static bool _ResponseSetsSessionCookie(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var values)) return false;

        foreach (var headerValue in values)
        {
            // 同一个 Set-Cookie 头可能被合并成一行；按逗号拆分后逐段判断
            foreach (var segment in headerValue.Split(','))
            {
                if (segment.TrimStart().StartsWith("overleaf.sid=", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }

    /// <summary>从会话 Cookie 容器读取指定 Cookie 的值；不存在时返回 null。</summary>
    private string? _ReadCookie(string name)
    {
        var cookies = _cookies.GetCookies(new Uri(_baseUrl));
        foreach (Cookie cookie in cookies)
            if (cookie.Name == name)
                return cookie.Value;
        return null;
    }

    /// <summary>判断异常是否属于连接层失败（连接被拒、超时、DNS / 端口不通）。</summary>
    private static bool _IsConnectionFailure(Exception ex) =>
        ex is HttpRequestException or TaskCanceledException or SocketException
        || ex.InnerException is SocketException;

    /// <summary>判断注册响应体是否表明「邮箱已存在」（对齐 AyakaleafAccountService._ContainsAlreadyRegistered）。</summary>
    private static bool _ContainsAlreadyRegistered(string body) =>
        body.Contains("already", StringComparison.OrdinalIgnoreCase)
        || body.Contains("已被使用", StringComparison.OrdinalIgnoreCase)
        || body.Contains("已被注册", StringComparison.OrdinalIgnoreCase)
        || body.Contains("已经存在", StringComparison.OrdinalIgnoreCase);

    /// <summary>判断登录失败响应体是否表明「账号已被停用」。</summary>
    private static bool _ContainsSuspended(string body) =>
        body.Contains("suspend", StringComparison.OrdinalIgnoreCase)
        || body.Contains("停用", StringComparison.OrdinalIgnoreCase)
        || body.Contains("禁用", StringComparison.OrdinalIgnoreCase);

    /// <summary>压缩换行并截断（用于响应体 / 异常消息，最多 200 字符，不含密码）。</summary>
    private static string _TruncateDetail(string? detail, int maxLength = 200)
    {
        if (string.IsNullOrWhiteSpace(detail)) return "服务端未返回详细原因";

        var singleLine = Regex.Replace(detail, @"\s+", " ").Trim();
        return singleLine.Length <= maxLength ? singleLine : singleLine[..maxLength];
    }

    // ---------------- 异常工厂（中文文案，绝不含密码明文或密文） ----------------

    private AyakaleafAccountException _ServiceUnavailable(Exception? inner) =>
        new(AyakaleafAccountErrorKind.ServiceUnavailable,
            $"无法连接房主服务（{_baseUrl}）：协作转发可能已中断。请确认房主仍在线、网络正常后重试。",
            innerException: inner);

    private AyakaleafAccountException _ServiceNotReady(int? statusCode)
    {
        var message =
            $"房主服务已响应但尚未就绪（{_baseUrl}）：未能获取登录页的 CSRF 令牌。" +
            "服务可能正在预热，请稍候几秒后重试。";
        if (statusCode is not null)
            message += $"（HTTP {statusCode}）";

        return new AyakaleafAccountException(AyakaleafAccountErrorKind.ServiceNotReady, message);
    }

    /// <summary>服务端已存在同名邮箱（本机凭据丢失/重装的典型场景）：给出可操作建议，不静默失败。</summary>
    private static AyakaleafAccountException _AccountConflict(string email) =>
        new(AyakaleafAccountErrorKind.AccountConflict,
            $"协作账号 {email} 已在房主服务端存在，但本机没有它的登录凭据，无法自动登录。" +
            "可任选其一：①联系房主在「账号中心」重置该账号密码；②修改显示名重新开户。" +
            "若房主不再需要该账号，也可请其在「账号中心」删除后重试。",
            email);

    private static AyakaleafAccountException _RegistrationRejected(string email, int statusCode, string body) =>
        new(AyakaleafAccountErrorKind.RegistrationRejected,
            $"注册协作账号 {email} 失败（HTTP {statusCode}）：{_TruncateDetail(body)}。",
            email);

    /// <summary>登录失败：按「已被房主停用」与「密码不符」区分中文文案。</summary>
    private static AyakaleafAccountException _CredentialInvalid(string email, int statusCode, string body)
    {
        if (_ContainsSuspended(body))
        {
            return new AyakaleafAccountException(
                AyakaleafAccountErrorKind.CredentialInvalid,
                $"协作账号 {email} 登录失败（HTTP {statusCode}）：该账号已被房主停用。" +
                "请联系房主在「账号中心」启用该账号后重试。",
                email);
        }

        return new AyakaleafAccountException(
            AyakaleafAccountErrorKind.CredentialInvalid,
            $"协作账号 {email} 登录失败（HTTP {statusCode}）：本机保存的密码与服务端不一致" +
            "（可能已被房主重置），或该账号已被删除。请联系房主在「账号中心」重置密码后重试。",
            email);
    }
}
