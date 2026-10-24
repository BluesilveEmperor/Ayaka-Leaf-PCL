using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using PCL.Core.App;
using PCL.Core.App.Configuration;
using PCL.Core.Utils;

namespace PCL.Core.Users;

/// <summary>
/// 本地用户的当前会话。登录状态持久化到本地配置文件，重启后保持。
/// </summary>
public sealed class LocalUserSession : IDisposable
{
    private const string SessionFileName = "local-user-session.json";
    private const string ConfigKey = "LocalUserSession";

    private static readonly object _sync = new();
    private static LocalUserSession? _instance;

    private LocalUser? _current;
    private readonly LocalUserService _service;

    private LocalUserSession(LocalUserService service)
    {
        _service = service;
    }

    /// <summary>
    /// 全局会话实例。
    /// </summary>
    public static LocalUserSession Instance
    {
        get
        {
            lock (_sync)
            {
                return _instance ??= new LocalUserSession(new LocalUserService());
            }
        }
    }

    /// <summary>
    /// 当前登录用户。未登录时为 null。
    /// </summary>
    public LocalUser? Current
    {
        get { lock (_sync) return _current; }
    }

    /// <summary>
    /// 当前是否已登录。
    /// </summary>
    public bool IsAuthenticated => Current is not null;

    /// <summary>
    /// 当前登录用户的独立游戏目录。未登录时返回 null。
    /// </summary>
    public string? CurrentDataDirectory
    {
        get
        {
            var user = Current;
            return user is null ? null : LocalUserService.GetUserDataDirectory(user);
        }
    }

    /// <summary>
    /// 初始化：确保数据库可用，并恢复上次的登录状态。
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await _service.InitializeAsync(ct).ConfigureAwait(false);
        await RestoreAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 注册新用户。注册前确保数据库已初始化。
    /// </summary>
    public async Task<(LocalUserResult Result, LocalUser? User)> InitializeAndRegisterAsync(
        string userName, string password, string? displayName = null, CancellationToken ct = default)
    {
        await _service.InitializeAsync(ct).ConfigureAwait(false);
        return await _service.RegisterAsync(userName, password, displayName, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 列出全部本地用户。
    /// </summary>
    public Task<IReadOnlyList<LocalUser>> ListUsersAsync(CancellationToken ct = default)
        => _service.ListAsync(ct);

    /// <summary>
    /// 修改密码。
    /// </summary>
    public Task<LocalUserResult> ChangePasswordAsync(
        string userId, string oldPassword, string newPassword, CancellationToken ct = default)
        => _service.ChangePasswordAsync(userId, oldPassword, newPassword, ct);

    /// <summary>
    /// 管理员重置任意用户密码，无需旧密码。
    /// </summary>
    public Task<LocalUserResult> ResetPasswordAsync(string userId, string newPassword, CancellationToken ct = default)
        => _service.ResetPasswordAsync(userId, newPassword, ct);

    /// <summary>
    /// 启用或停用用户。
    /// </summary>
    public Task<LocalUserResult> SetDisabledAsync(string userId, bool disabled, CancellationToken ct = default)
        => _service.SetDisabledAsync(userId, disabled, ct);

    /// <summary>
    /// 删除用户，可选同时删除其数据目录。不可撤销。
    /// </summary>
    public Task<LocalUserResult> DeleteAsync(string userId, bool deleteData = true, CancellationToken ct = default)
        => _service.DeleteAsync(userId, deleteData, ct);

    /// <summary>
    /// 使用用户名与密码登录。成功后持久化会话。
    /// </summary>
    public async Task<(LocalUserResult Result, LocalUser? User)> LoginAsync(
        string userName, string password, CancellationToken ct = default)
    {
        var (result, user) = await _service.AuthenticateAsync(userName, password, ct).ConfigureAwait(false);
        if (result == LocalUserResult.Success && user is not null)
        {
            lock (_sync) _current = user;
            _Persist();
        }
        return (result, user);
    }

    /// <summary>
    /// 以指定用户身份登录（用于管理员切换）。
    /// </summary>
    public async Task<LocalUserResult> LoginAsAsync(string userId, CancellationToken ct = default)
    {
        var user = await _service.FindByIdAsync(userId, ct).ConfigureAwait(false);
        if (user is null) return LocalUserResult.UserNotFound;
        if (user.IsDisabled) return LocalUserResult.UserDisabled;
        lock (_sync) _current = user;
        _Persist();
        return LocalUserResult.Success;
    }

    /// <summary>
    /// 登出。清除当前会话并删除持久化记录。
    /// </summary>
    public void Logout()
    {
        lock (_sync) _current = null;
        try
        {
            ConfigService.GetProvider(ConfigSource.Local).Delete(ConfigKey);
        }
        catch (Exception)
        {
            // 持久化失败不影响登出本身
        }
    }

    /// <summary>
    /// 从持久化记录恢复登录状态。用户已删除或被停用时视为未登录。
    /// </summary>
    private async Task RestoreAsync(CancellationToken ct)
    {
        string? payload;
        try
        {
            var provider = ConfigService.GetProvider(ConfigSource.Local);
            payload = provider.GetValue<string>(ConfigKey, out var stored) ? stored : null;
        }
        catch (Exception)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(payload)) return;

        try
        {
            var node = JsonCompat.ParseNode(payload);
            if (node is not JsonObject root) return;
            var id = root["userId"]?.ToString();
            if (string.IsNullOrWhiteSpace(id)) return;

            var user = await _service.FindByIdAsync(id, ct).ConfigureAwait(false);
            if (user is null || user.IsDisabled) return;

            lock (_sync) _current = user;
        }
        catch (Exception)
        {
            // 记录损坏时视为未登录
        }
    }

    private void _Persist()
    {
        var user = Current;
        if (user is null) return;
        try
        {
            var payload = JsonSerializer.Serialize(new { userId = user.Id }, JsonCompat.SerializerOptions);
            ConfigService.GetProvider(ConfigSource.Local).SetValue(ConfigKey, payload);
        }
        catch (Exception)
        {
            // 持久化失败不影响本次登录
        }
    }

    public void Dispose() => _service.Dispose();
}
