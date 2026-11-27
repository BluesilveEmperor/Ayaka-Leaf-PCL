using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PCL.Core.App;

namespace PCL.Core.Ayakaleaf;

/// <summary>
/// 服务端账号条目（US6，FR-010）。字段取自 <c>POST /admin/users/search</c> 的响应。
/// </summary>
/// <param name="Id">服务端用户 ObjectId。</param>
/// <param name="Email">登录邮箱。</param>
/// <param name="DisplayName">姓名（缺省时回落邮箱）。</param>
/// <param name="IsAdmin">是否站点管理员。</param>
/// <param name="Suspended">是否已停用（停用后无法登录，可恢复）。</param>
/// <param name="Deleted">是否已删除（软删除，可恢复）。</param>
/// <param name="Inactive">是否从未登录过（新注册用户的近似判断）。</param>
/// <param name="SignUpAt">注册时间（服务端未提供时为 null）。</param>
public sealed record ManagedAccount(
    string Id,
    string Email,
    string DisplayName,
    bool IsAdmin,
    bool Suspended,
    bool Deleted,
    bool Inactive,
    DateTimeOffset? SignUpAt)
{
    /// <summary>是否需要房主关注（停用/删除/从未登录）。界面据此高亮。</summary>
    public bool NeedsAttention => Suspended || Deleted || Inactive;
}

/// <summary>
/// 账号管理（US6 / FR-010）：列表、停用/启用、重置密码、权限变更。
/// <para>
/// 复用 <see cref="AyakaleafAccountService"/> 的管理员会话（本机管理员凭据 → 登录 → 登录态 CSRF），
/// 端点对照镜像内 <c>modules/admin-tools/app/src/AdminToolsRouter.mjs</c>：
/// <c>POST /admin/users/search</c> 与 <c>POST /admin/user/:id/update</c>。
/// </para>
/// <para>
/// 说明：本镜像体系 <c>EMAIL_CONFIRMATION_DISABLED=true</c>，注册即生效、没有独立的"待审批"状态。
/// 因此审批动作落成"停用/启用"：房主可先把可疑账号停用，确认后启用；停用即会话失效、无法登录。
/// </para>
/// </summary>
public static partial class AyakaleafAccountService
{
    /// <summary>管理员操作串行化：共用 HttpClient 的 Cookie 容器，并发登录会互相踢掉会话。</summary>
    private static readonly SemaphoreSlim AdminGate = new(1, 1);

    /// <summary>
    /// 列出服务端账号。<paramref name="query"/> 为空时返回全部（服务端分页上限内）。
    /// </summary>
    public static Task<IReadOnlyList<ManagedAccount>> ListServerAccountsAsync(
        string query = "", CancellationToken ct = default)
    {
        var keyword = query.Trim();
        return RunWithAdminAsync(async (csrf, token) =>
        {
            var payload = JsonSerializer.Serialize(new AdminSearchRequest { Search = keyword });
            using var doc = await _AdminPostJsonAsync(csrf, "/admin/users/search", payload, token).ConfigureAwait(false);
            return _ParseAccounts(doc);
        }, ct);
    }

    /// <summary>停用 / 启用账号（<c>suspended</c>）。停用后该账号立即无法登录，已登录会话在下次鉴权时失效。</summary>
    public static Task SetServerAccountSuspendedAsync(string userId, bool suspended, CancellationToken ct = default)
    {
        RequireId(userId);
        return RunWithAdminAsync(async (csrf, token) =>
        {
            var payload = JsonSerializer.Serialize(new { suspended });
            using var _ = await _AdminPostJsonAsync(
                csrf, $"/admin/user/{Uri.EscapeDataString(userId)}/update", payload, token).ConfigureAwait(false);
            return true;
        }, ct);
    }

    /// <summary>
    /// 重置账号密码（服务端要求 ≥8 位，且会校验弱口令库）。返回的密码由调用方一次性展示给房主。
    /// </summary>
    public static Task ResetServerAccountPasswordAsync(string userId, string newPassword, CancellationToken ct = default)
    {
        RequireId(userId);
        if (string.IsNullOrWhiteSpace(newPassword) || newPassword.Length < 8)
            throw _AdminRejected("新密码至少 8 位。");
        return RunWithAdminAsync(async (csrf, token) =>
        {
            var payload = JsonSerializer.Serialize(new { password = newPassword });
            using var _ = await _AdminPostJsonAsync(
                csrf, $"/admin/user/{Uri.EscapeDataString(userId)}/update", payload, token).ConfigureAwait(false);
            return true;
        }, ct);
    }

    /// <summary>设置 / 取消站点管理员权限（<c>isAdmin</c>）。</summary>
    public static Task SetServerAccountAdminAsync(string userId, bool isAdmin, CancellationToken ct = default)
    {
        RequireId(userId);
        return RunWithAdminAsync(async (csrf, token) =>
        {
            var payload = JsonSerializer.Serialize(new { isAdmin });
            using var _ = await _AdminPostJsonAsync(
                csrf, $"/admin/user/{Uri.EscapeDataString(userId)}/update", payload, token).ConfigureAwait(false);
            return true;
        }, ct);
    }

    /// <summary>生成一个符合服务端策略的随机密码（大小写 + 数字，20 位，避开易混字符）。</summary>
    public static string GenerateTemporaryPassword()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";
        var buffer = new byte[20];
        System.Security.Cryptography.RandomNumberGenerator.Fill(buffer);
        var sb = new StringBuilder(buffer.Length);
        foreach (var b in buffer) sb.Append(alphabet[b % alphabet.Length]);
        // 保证至少含一个数字与一个大写字母，避免服务端弱口令策略误判
        sb.Append('7').Append('K');
        return sb.ToString();
    }

    // ---------------- 管理员会话 ----------------

    /// <summary>取管理员凭据并登录，把登录态 CSRF 交给 <paramref name="action"/> 执行管理请求。</summary>
    private static async Task<T> RunWithAdminAsync<T>(
        Func<string, CancellationToken, Task<T>> action, CancellationToken ct)
    {
        var adminName = Config.Ayakaleaf.AdminLocalUserName?.Trim();
        if (string.IsNullOrEmpty(adminName))
            throw _AdminRejected("尚未开通 Ayakaleaf 管理员账号。请先用管理员本地用户打开一次 Ayakaleaf 工作台后再试。");

        var adminFile = Path.Combine(AccountsDirectory, adminName + ".json");
        if (!_TryReadAccountFile(adminFile, out var admin) || admin is null)
            throw _AdminRejected(
                $"本机没有 Ayakaleaf 管理员（{adminName}）的账号凭据。请先用该本地用户打开一次工作台后再试。");

        await AdminGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await LoginAsync(admin).ConfigureAwait(false);
            var csrf = await _GetSessionCsrfTokenAsync().ConfigureAwait(false);
            return await action(csrf, ct).ConfigureAwait(false);
        }
        finally
        {
            AdminGate.Release();
        }
    }

    /// <summary>带 CSRF 的 JSON POST；403 转成"无管理权限"，其余非 2xx 带上服务端文案。</summary>
    private static async Task<JsonDocument> _AdminPostJsonAsync(
        string csrf, string path, string payload, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, AyakaleafService.BaseUrl + path)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-Csrf-Token", csrf);

        HttpResponseMessage response;
        try
        {
            response = await Http.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (_IsConnectionFailure(ex))
        {
            throw _ServiceUnavailable(ex);
        }

        using (response)
        {
            var code = (int)response.StatusCode;
            var body = await _ReadBodyAsync(response).ConfigureAwait(false);

            if (code == 403)
                throw _AdminRejected("当前管理员账号没有站点管理权限（请确认本机管理员账号在站点内仍是管理员）。");
            if (!response.IsSuccessStatusCode)
                throw _AdminRejected($"管理请求被拒绝（HTTP {code}）：{_TruncateDetail(body)}");

            if (string.IsNullOrWhiteSpace(body)) return JsonDocument.Parse("{}");
            try
            {
                return JsonDocument.Parse(body);
            }
            catch (JsonException)
            {
                // 端点在成功时可能返回空体或非 JSON；管理操作只要 2xx 即视为成功
                return JsonDocument.Parse("{}");
            }
        }
    }

    /// <summary>解析 users[]：字段名兼容 camelCase 与 snake_case。</summary>
    private static IReadOnlyList<ManagedAccount> _ParseAccounts(JsonDocument doc)
    {
        var list = new List<ManagedAccount>();
        if (!doc.RootElement.TryGetProperty("users", out var users) || users.ValueKind != JsonValueKind.Array)
            return list;

        foreach (var user in users.EnumerateArray())
        {
            var id = _Str(user, "id");
            var email = _Str(user, "email");
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(email)) continue;

            var first = _Str(user, "first_name", "firstName");
            var last = _Str(user, "last_name", "lastName");
            var name = string.Join(" ", new[] { first, last }.Where(s => !string.IsNullOrWhiteSpace(s))).Trim();

            list.Add(new ManagedAccount(
                id,
                email,
                string.IsNullOrEmpty(name) ? email : name,
                _Bool(user, "isAdmin"),
                _Bool(user, "suspended"),
                _Bool(user, "deleted"),
                _Bool(user, "inactive"),
                _Date(user, "signUpDate", "signUpDate_")));
        }

        return list;
    }

    private static string _Str(JsonElement e, params string[] names)
    {
        foreach (var n in names)
        {
            if (e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String)
                return v.GetString() ?? "";
        }

        return "";
    }

    private static bool _Bool(JsonElement e, params string[] names)
    {
        foreach (var n in names)
        {
            if (e.TryGetProperty(n, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False)
                return v.GetBoolean();
        }

        return false;
    }

    /// <summary>服务端可能给 ISO 字符串或毫秒时间戳。</summary>
    private static DateTimeOffset? _Date(JsonElement e, params string[] names)
    {
        foreach (var n in names)
        {
            if (!e.TryGetProperty(n, out var v)) continue;
            if (v.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(v.GetString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var parsed))
                return parsed;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var ms) && ms > 0)
                return DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime();
        }

        return null;
    }

    private static void RequireId(string userId)
    {
        if (string.IsNullOrWhiteSpace(userId))
            throw _AdminRejected("缺少目标账号标识。");
    }

    private static AyakaleafAccountException _AdminRejected(string message, int? statusCode = null) =>
        new(AyakaleafAccountErrorKind.AdminOperationRejected,
            statusCode is null ? message : $"{message}（HTTP {statusCode}）");
}
