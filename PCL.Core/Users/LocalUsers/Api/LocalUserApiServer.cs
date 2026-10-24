using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PCL.Core.Utils;

namespace PCL.Core.Users.Api;

/// <summary>
/// 本地用户 HTTP 服务。基于 <see cref="HttpListener"/>，随启动器进程运行，
/// 供同机或局域网内的其它客户端注册、登录与管理用户。
/// </summary>
public sealed class LocalUserApiServer : IDisposable
{
    private readonly LocalUserService _service;
    private readonly HttpListener _listener = new();
    private readonly ConcurrentDictionary<string, string> _tokens = new();
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;
    private bool _disposed;

    /// <summary>
    /// 监听地址，例如 http://127.0.0.1:8730/。
    /// </summary>
    public string Prefix { get; }

    /// <summary>
    /// 监听端口。
    /// </summary>
    public int Port { get; }

    /// <summary>
    /// 服务是否正在运行。
    /// </summary>
    public bool IsRunning { get; private set; }

    public LocalUserApiServer(LocalUserService service, int port = 8730, string? host = "127.0.0.1")
    {
        _service = service;
        Port = port;
        Prefix = $"http://{host}:{port}/";
    }

    /// <summary>
    /// 启动服务。
    /// </summary>
    public void Start()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(LocalUserApiServer));
        if (IsRunning) return;

        _listener.Prefixes.Add(Prefix);
        _listener.Start();
        IsRunning = true;
        _loop = Task.Run(() => _AcceptLoopAsync(_cts.Token));
    }

    /// <summary>
    /// 停止服务。
    /// </summary>
    public void Stop()
    {
        if (!IsRunning) return;
        try { _cts.Cancel(); } catch { }
        try { _listener.Stop(); } catch { }
        IsRunning = false;
    }

    private async Task _AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (HttpListenerException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            _ = Task.Run(() => _HandleAsync(context, ct), ct);
        }
    }

    private async Task _HandleAsync(HttpListenerContext context, CancellationToken ct)
    {
        try
        {
            var path = context.Request.Url?.AbsolutePath.TrimEnd('/') ?? string.Empty;
            var method = context.Request.HttpMethod;

            // 仅允许本机访问
            if (!IsLocalRequest(context.Request))
            {
                await WriteJsonAsync(context, 403, LocalUserApi.Response.Fail("forbidden", "Only local requests are allowed."))
                    .ConfigureAwait(false);
                return;
            }

            LocalUserApi.Response response;
            var status = 200;

            switch (method.ToUpperInvariant())
            {
                case "GET" when path == "/api/health":
                    response = LocalUserApi.Response.Success("healthy", "ok");
                    break;

                case "GET" when path == "/api/users":
                    response = await _HandleListAsync(context, ct).ConfigureAwait(false);
                    break;

                case "POST" when path == "/api/register":
                    response = await _HandleRegisterAsync(context, ct).ConfigureAwait(false);
                    status = response.Ok ? 200 : 400;
                    break;

                case "POST" when path == "/api/login":
                    response = await _HandleLoginAsync(context, ct).ConfigureAwait(false);
                    status = response.Ok ? 200 : 401;
                    break;

                case "POST" when path == "/api/logout":
                    response = _HandleLogout(context);
                    break;

                case "POST" when path == "/api/change-password":
                    response = await _HandleChangePasswordAsync(context, ct).ConfigureAwait(false);
                    status = response.Ok ? 200 : 400;
                    break;

                case "POST" when path == "/api/delete-user":
                    response = await _HandleDeleteAsync(context, ct).ConfigureAwait(false);
                    status = response.Ok ? 200 : 400;
                    break;

                default:
                    response = LocalUserApi.Response.Fail("not_found", $"No route for {method} {path}");
                    status = 404;
                    break;
            }

            await WriteJsonAsync(context, status, response).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            try
            {
                await WriteJsonAsync(context, 500, LocalUserApi.Response.Fail("internal_error", ex.Message))
                    .ConfigureAwait(false);
            }
            catch
            {
                // 忽略写回失败
            }
        }
    }

    private async Task<LocalUserApi.Response> _HandleListAsync(HttpListenerContext context, CancellationToken ct)
    {
        var users = await _service.ListAsync(ct).ConfigureAwait(false);
        return new LocalUserApi.Response
        {
            Ok = true,
            Code = "ok",
            Users = users.Select(ToUserInfo).ToList()
        };
    }

    private async Task<LocalUserApi.Response> _HandleRegisterAsync(HttpListenerContext context, CancellationToken ct)
    {
        var request = await ReadJsonAsync<LocalUserApi.RegisterRequest>(context, ct).ConfigureAwait(false);
        if (request is null) return LocalUserApi.Response.Fail("invalid_body", "Request body is not valid JSON.");

        var (result, user) = await _service.RegisterAsync(request.UserName, request.Password, request.DisplayName, ct)
            .ConfigureAwait(false);

        if (result != LocalUserResult.Success || user is null)
            return LocalUserApi.Response.Fail(result.ToString(), DescribeResult(result));

        var token = _IssueToken(user.Id);
        return new LocalUserApi.Response
        {
            Ok = true,
            Code = "ok",
            User = ToUserInfo(user),
            Token = token,
            Message = "Registration succeeded."
        };
    }

    private async Task<LocalUserApi.Response> _HandleLoginAsync(HttpListenerContext context, CancellationToken ct)
    {
        var request = await ReadJsonAsync<LocalUserApi.LoginRequest>(context, ct).ConfigureAwait(false);
        if (request is null) return LocalUserApi.Response.Fail("invalid_body", "Request body is not valid JSON.");

        var (result, user) = await _service.AuthenticateAsync(request.UserName, request.Password, ct)
            .ConfigureAwait(false);

        if (result != LocalUserResult.Success || user is null)
        {
            var message = result == LocalUserResult.InvalidPassword
                ? "Incorrect user name or password."
                : DescribeResult(result);
            return LocalUserApi.Response.Fail(result.ToString(), message);
        }

        var token = _IssueToken(user.Id);
        return new LocalUserApi.Response
        {
            Ok = true,
            Code = "ok",
            User = ToUserInfo(user),
            Token = token,
            Message = "Login succeeded."
        };
    }

    private LocalUserApi.Response _HandleLogout(HttpListenerContext context)
    {
        var token = _ReadToken(context.Request);
        if (!string.IsNullOrEmpty(token)) _tokens.TryRemove(token, out _);
        return LocalUserApi.Response.Success("ok", "Logged out.");
    }

    private async Task<LocalUserApi.Response> _HandleChangePasswordAsync(HttpListenerContext context, CancellationToken ct)
    {
        var request = await ReadJsonAsync<LocalUserApi.ChangePasswordRequest>(context, ct).ConfigureAwait(false);
        if (request is null) return LocalUserApi.Response.Fail("invalid_body", "Request body is not valid JSON.");

        var result = await _service.ChangePasswordAsync(request.UserId, request.OldPassword, request.NewPassword, ct)
            .ConfigureAwait(false);
        return result == LocalUserResult.Success
            ? LocalUserApi.Response.Success("ok", "Password changed.")
            : LocalUserApi.Response.Fail(result.ToString(), DescribeResult(result));
    }

    private async Task<LocalUserApi.Response> _HandleDeleteAsync(HttpListenerContext context, CancellationToken ct)
    {
        var body = await ReadJsonAsync<Dictionary<string, string>>(context, ct).ConfigureAwait(false);
        if (body is null || !body.TryGetValue("userId", out var userId) || string.IsNullOrWhiteSpace(userId))
            return LocalUserApi.Response.Fail("invalid_body", "userId is required.");

        var result = await _service.DeleteAsync(userId, true, ct).ConfigureAwait(false);
        return result == LocalUserResult.Success
            ? LocalUserApi.Response.Success("ok", "User deleted.")
            : LocalUserApi.Response.Fail(result.ToString(), DescribeResult(result));
    }

    private string _IssueToken(string userId)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
        _tokens[token] = userId;
        return token;
    }

    private static string? _ReadToken(HttpListenerRequest request)
    {
        var header = request.Headers["Authorization"];
        if (!string.IsNullOrEmpty(header) && header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return header["Bearer ".Length..].Trim();
        return request.QueryString["token"];
    }

    private static bool IsLocalRequest(HttpListenerRequest request)
    {
        var remote = request.RemoteEndPoint?.Address;
        if (remote is null) return true;
        return IPAddress.IsLoopback(remote) || remote.Equals(IPAddress.IPv6Loopback);
    }

    private static LocalUserApi.UserInfo ToUserInfo(LocalUser user) => new()
    {
        Id = user.Id,
        UserName = user.UserName,
        DisplayName = user.DisplayName,
        CreatedAt = user.CreatedAt.ToString("O"),
        LastLoginAt = user.LastLoginAt?.ToString("O"),
        IsAdmin = user.IsAdmin,
        IsDisabled = user.IsDisabled,
        DataDirectory = LocalUserService.GetUserDataDirectory(user)
    };

    private static string DescribeResult(LocalUserResult result) => result switch
    {
        LocalUserResult.InvalidUserName => "User name must be 3-16 letters, digits or underscores.",
        LocalUserResult.InvalidPassword => $"Password must be {LocalUserService.MinPasswordLength}-{LocalUserService.MaxPasswordLength} characters.",
        LocalUserResult.UserNameTaken => "This user name is already registered.",
        LocalUserResult.UserNotFound => "User not found.",
        LocalUserResult.UserDisabled => "This account is disabled.",
        _ => result.ToString()
    };

    private static async Task<T?> ReadJsonAsync<T>(HttpListenerContext context, CancellationToken ct)
        where T : class
    {
        try
        {
            using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
            var body = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(body)) return null;
            return JsonSerializer.Deserialize<T>(body, JsonCompat.SerializerOptions);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static async Task WriteJsonAsync(HttpListenerContext context, int status, LocalUserApi.Response response)
    {
        var payload = JsonSerializer.Serialize(response, JsonCompat.SerializerOptions);
        var bytes = Encoding.UTF8.GetBytes(payload);
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        context.Response.Close();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        try { _cts.Cancel(); } catch { }
        try { _listener.Close(); } catch { }
        _cts.Dispose();
    }
}
