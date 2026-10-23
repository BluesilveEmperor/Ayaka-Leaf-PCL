using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PCL.Core.Utils;

namespace PCL.Core.Minecraft.Profile.LocalUsers.Api;

/// <summary>
/// 本地用户服务的 HTTP 客户端。启动器既可直接使用本地存储，也可通过该客户端
/// 访问同一台机器上运行的用户服务，从而与其它客户端共享同一份用户数据。
/// </summary>
public sealed class LocalUserApiClient : IDisposable
{
    private readonly HttpClient _http;
    private bool _disposed;

    /// <summary>
    /// 服务基址，例如 http://127.0.0.1:8730
    /// </summary>
    public string BaseAddress { get; }

    /// <summary>
    /// 当前持有的会话令牌。
    /// </summary>
    public string? Token { get; private set; }

    public LocalUserApiClient(string baseAddress = "http://127.0.0.1:8730", HttpMessageHandler? handler = null)
    {
        BaseAddress = baseAddress.TrimEnd('/');
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.BaseAddress = new Uri(BaseAddress + "/");
        _http.Timeout = TimeSpan.FromSeconds(15);
    }

    /// <summary>
    /// 检查服务是否可达。
    /// </summary>
    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await _http.GetAsync("api/health", ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 注册用户。
    /// </summary>
    public async Task<LocalUserApi.Response> RegisterAsync(
        string userName, string password, string? displayName = null, CancellationToken ct = default)
    {
        var request = new LocalUserApi.RegisterRequest
        {
            UserName = userName,
            Password = password,
            DisplayName = displayName
        };
        return await _PostAsync<LocalUserApi.RegisterRequest>("api/register", request, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 登录。成功后自动保存返回的令牌。
    /// </summary>
    public async Task<LocalUserApi.Response> LoginAsync(
        string userName, string password, CancellationToken ct = default)
    {
        var request = new LocalUserApi.LoginRequest { UserName = userName, Password = password };
        var response = await _PostAsync<LocalUserApi.LoginRequest>("api/login", request, ct)
            .ConfigureAwait(false);
        if (response.Ok && !string.IsNullOrEmpty(response.Token)) Token = response.Token;
        return response;
    }

    /// <summary>
    /// 登出并清除本地令牌。
    /// </summary>
    public async Task LogoutAsync(CancellationToken ct = default)
    {
        try
        {
            await _http.PostAsync("api/logout", new StringContent(string.Empty), ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 忽略网络错误，登出以清除本地令牌为准
        }
        Token = null;
    }

    /// <summary>
    /// 列出全部用户。
    /// </summary>
    public async Task<LocalUserApi.Response> ListUsersAsync(CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "api/users");
            _ApplyToken(request);
            var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<LocalUserApi.Response>(body, JsonCompat.SerializerOptions)
                   ?? LocalUserApi.Response.Fail("invalid_response", "Unparsable response.");
        }
        catch (Exception ex)
        {
            return LocalUserApi.Response.Fail("network_error", ex.Message);
        }
    }

    /// <summary>
    /// 修改密码。
    /// </summary>
    public async Task<LocalUserApi.Response> ChangePasswordAsync(
        string userId, string oldPassword, string newPassword, CancellationToken ct = default)
    {
        var request = new LocalUserApi.ChangePasswordRequest
        {
            UserId = userId,
            OldPassword = oldPassword,
            NewPassword = newPassword
        };
        return await _PostAsync<LocalUserApi.ChangePasswordRequest>(
            "api/change-password", request, ct).ConfigureAwait(false);
    }

    private async Task<LocalUserApi.Response> _PostAsync<TRequest>(
        string path, TRequest payload, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(payload, JsonCompat.SerializerOptions),
                    Encoding.UTF8, "application/json")
            };
            _ApplyToken(request);
            var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var parsed = JsonSerializer.Deserialize<LocalUserApi.Response>(body, JsonCompat.SerializerOptions);
            if (parsed is not null) return parsed;
            return LocalUserApi.Response.Fail("invalid_response", "Unparsable response.");
        }
        catch (Exception ex)
        {
            return LocalUserApi.Response.Fail("network_error", ex.Message);
        }
    }

    private void _ApplyToken(HttpRequestMessage request)
    {
        if (!string.IsNullOrEmpty(Token))
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + Token);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _http.Dispose();
    }
}
