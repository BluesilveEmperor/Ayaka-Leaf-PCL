using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using PCL.Core.App;
using PCL.Core.Logging;

namespace PCL.Core.Users;

/// <summary>
/// 本地用户操作的结果码。
/// </summary>
public enum LocalUserResult
{
    Success,
    InvalidUserName,
    InvalidPassword,
    UserNameTaken,
    UserNotFound,
    UserDisabled,
    NotAuthenticated,
    NotAuthorized,
    AlreadyExists
}

/// <summary>
/// 本地用户业务服务：注册、登录、改密、删除与数据目录管理。
/// </summary>
public sealed class LocalUserService : IDisposable
{
    /// <summary>
    /// 用户名规则：3-16 位字母、数字或下划线。
    /// </summary>
    public static readonly Regex UserNamePattern = new("^[A-Za-z0-9_]{3,16}$", RegexOptions.Compiled);

    /// <summary>
    /// 密码最短长度。
    /// </summary>
    public const int MinPasswordLength = 6;

    /// <summary>
    /// 密码最长长度。
    /// </summary>
    public const int MaxPasswordLength = 128;

    private readonly LocalUserStore _store;

    public LocalUserService(LocalUserStore? store = null)
    {
        _store = store ?? new LocalUserStore();
    }

    /// <summary>
    /// 用户数据根目录。所有用户的独立游戏目录均位于其下。
    /// 随启动器目录存放（<c>PCL\Users</c>），便于便携管理，不同启动器目录之间互不串数据。
    /// </summary>
    public static string UsersRootDirectory => Path.Combine(Paths.Data, "Users");

    /// <summary>
    /// 校验用户名的合法性。
    /// </summary>
    public static bool IsValidUserName(string? userName)
        => !string.IsNullOrWhiteSpace(userName) && UserNamePattern.IsMatch(userName);

    /// <summary>
    /// 校验密码的合法性。
    /// </summary>
    public static bool IsValidPassword(string? password)
        => !string.IsNullOrEmpty(password) && password.Length >= MinPasswordLength && password.Length <= MaxPasswordLength;

    /// <summary>
    /// 注册新用户。首个注册用户自动成为管理员。
    /// </summary>
    public async Task<(LocalUserResult Result, LocalUser? User)> RegisterAsync(
        string userName, string password, string? displayName = null, CancellationToken ct = default)
    {
        if (!IsValidUserName(userName)) return (LocalUserResult.InvalidUserName, null);
        if (!IsValidPassword(password)) return (LocalUserResult.InvalidPassword, null);

        if (await _store.UserNameExistsAsync(userName, ct).ConfigureAwait(false))
            return (LocalUserResult.UserNameTaken, null);

        var hash = PasswordHasher.Hash(password);
        var isFirst = await _store.CountAsync(ct).ConfigureAwait(false) == 0;
        var user = new LocalUser
        {
            Id = Guid.NewGuid().ToString("N"),
            UserName = userName,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? userName : displayName.Trim(),
            PasswordHash = hash.Hash,
            PasswordSalt = hash.Salt,
            PasswordIterations = hash.Iterations,
            PasswordKeySize = hash.KeySize,
            CreatedAt = DateTimeOffset.UtcNow,
            IsAdmin = isFirst,
            DataDirectoryName = SanitizeDirectoryName(userName)
        };

        try
        {
            await _store.AddAsync(user, ct).ConfigureAwait(false);
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            // 唯一索引冲突：并发注册同名用户
            return (LocalUserResult.UserNameTaken, null);
        }

        return (LocalUserResult.Success, user);
    }

    /// <summary>
    /// 校验凭据。成功时返回用户并更新最后登录时间。
    /// </summary>
    public async Task<(LocalUserResult Result, LocalUser? User)> AuthenticateAsync(
        string userName, string password, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrEmpty(password))
            return (LocalUserResult.InvalidUserName, null);

        var user = await _store.FindByUserNameAsync(userName, ct).ConfigureAwait(false);
        if (user is null) return (LocalUserResult.UserNotFound, null);
        if (user.IsDisabled) return (LocalUserResult.UserDisabled, null);

        if (!PasswordHasher.Verify(password, user.ToPasswordHash()))
            return (LocalUserResult.InvalidPassword, null);

        // 密码校验通过即视为登录成功；审计字段属于非关键写入，失败只降级为警告。
        user.LastLoginAt = DateTimeOffset.UtcNow;
        try
        {
            await _store.UpdateAsync(user, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 取消不属于登录失败，按取消语义向上传播
            throw;
        }
        catch (Exception ex)
        {
            LogWrapper.Warn(ex, $"[LocalUser] 更新最后登录时间失败：{user.UserName}");
        }

        return (LocalUserResult.Success, user);
    }

    /// <summary>
    /// 修改密码。需要提供旧密码，除非操作者是管理员且显式要求重置。
    /// </summary>
    public async Task<LocalUserResult> ChangePasswordAsync(
        string userId, string oldPassword, string newPassword, CancellationToken ct = default)
    {
        if (!IsValidPassword(newPassword)) return LocalUserResult.InvalidPassword;

        var user = await _store.FindByIdAsync(userId, ct).ConfigureAwait(false);
        if (user is null) return LocalUserResult.UserNotFound;
        if (!PasswordHasher.Verify(oldPassword, user.ToPasswordHash())) return LocalUserResult.InvalidPassword;

        var hash = PasswordHasher.Hash(newPassword);
        user.PasswordHash = hash.Hash;
        user.PasswordSalt = hash.Salt;
        user.PasswordIterations = hash.Iterations;
        user.PasswordKeySize = hash.KeySize;
        await _store.UpdateAsync(user, ct).ConfigureAwait(false);
        return LocalUserResult.Success;
    }

    /// <summary>
    /// 管理员重置任意用户密码，无需旧密码。
    /// </summary>
    public async Task<LocalUserResult> ResetPasswordAsync(
        string userId, string newPassword, CancellationToken ct = default)
    {
        if (!IsValidPassword(newPassword)) return LocalUserResult.InvalidPassword;

        var user = await _store.FindByIdAsync(userId, ct).ConfigureAwait(false);
        if (user is null) return LocalUserResult.UserNotFound;

        var hash = PasswordHasher.Hash(newPassword);
        user.PasswordHash = hash.Hash;
        user.PasswordSalt = hash.Salt;
        user.PasswordIterations = hash.Iterations;
        user.PasswordKeySize = hash.KeySize;
        await _store.UpdateAsync(user, ct).ConfigureAwait(false);
        return LocalUserResult.Success;
    }

    /// <summary>
    /// 列出全部用户。
    /// </summary>
    public Task<IReadOnlyList<LocalUser>> ListAsync(CancellationToken ct = default)
        => _store.ListAsync(ct);

    /// <summary>
    /// 按标识读取用户。
    /// </summary>
    public Task<LocalUser?> FindByIdAsync(string userId, CancellationToken ct = default)
        => _store.FindByIdAsync(userId, ct);

    /// <summary>
    /// 按用户名读取用户。
    /// </summary>
    public Task<LocalUser?> FindByUserNameAsync(string userName, CancellationToken ct = default)
        => _store.FindByUserNameAsync(userName, ct);

    /// <summary>
    /// 删除用户，并同时删除其数据目录。不可撤销。
    /// </summary>
    public async Task<LocalUserResult> DeleteAsync(string userId, bool deleteData = true, CancellationToken ct = default)
    {
        var user = await _store.FindByIdAsync(userId, ct).ConfigureAwait(false);
        if (user is null) return LocalUserResult.UserNotFound;

        if (!await _store.DeleteAsync(userId, ct).ConfigureAwait(false)) return LocalUserResult.UserNotFound;

        if (deleteData)
        {
            try
            {
                var directory = GetUserDataDirectory(user);
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
            catch (IOException)
            {
                // 数据目录被占用时保留，交由用户手动清理
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return LocalUserResult.Success;
    }

    /// <summary>
    /// 启用或停用用户。
    /// </summary>
    public async Task<LocalUserResult> SetDisabledAsync(string userId, bool disabled, CancellationToken ct = default)
    {
        var user = await _store.FindByIdAsync(userId, ct).ConfigureAwait(false);
        if (user is null) return LocalUserResult.UserNotFound;
        user.IsDisabled = disabled;
        await _store.UpdateAsync(user, ct).ConfigureAwait(false);
        return LocalUserResult.Success;
    }

    /// <summary>
    /// 解析用户的独立游戏目录（不存在则创建）。
    /// </summary>
    public static string GetUserDataDirectory(LocalUser user)
    {
        var name = string.IsNullOrWhiteSpace(user.DataDirectoryName)
            ? SanitizeDirectoryName(user.UserName)
            : user.DataDirectoryName;
        var directory = Path.Combine(UsersRootDirectory, name);
        Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>
    /// 解析用户的独立游戏目录（按用户标识）。
    /// </summary>
    public static string GetUserDataDirectoryById(string userId, string fallbackName)
    {
        var name = string.IsNullOrWhiteSpace(fallbackName) ? userId : SanitizeDirectoryName(fallbackName);
        var directory = Path.Combine(UsersRootDirectory, name);
        Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>
    /// 将用户名转换为安全的目录名。用户名本身已限制为字母数字下划线，此处做二次防护。
    /// </summary>
    public static string SanitizeDirectoryName(string userName)
    {
        if (string.IsNullOrWhiteSpace(userName)) return "default";
        var invalid = Path.GetInvalidFileNameChars();
        var chars = userName.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray();
        var result = new string(chars).Trim();
        return result.Length == 0 ? "default" : result;
    }

    /// <summary>
    /// 初始化数据库。
    /// </summary>
    public Task InitializeAsync(CancellationToken ct = default) => _store.InitializeAsync(ct);

    public void Dispose() => _store.Dispose();
}
