using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using PCL.Core.App;

namespace PCL.Core.Users;

/// <summary>
/// 本地用户的 SQLite 存储。存放于共享数据目录下的 users.db。
/// </summary>
public sealed class LocalUserStore : IDisposable
{
    private const string DatabaseFileName = "users.db";

    /// <summary>
    /// 等锁超时（毫秒）。连接打开后立即执行，避免并发访问时直接抛 SQLITE_BUSY。
    /// </summary>
    private const string BusyTimeoutPragma = "PRAGMA busy_timeout=15000;";

    /// <summary>
    /// 启用 WAL，允许读写并发（幂等，重复执行无副作用）。
    /// </summary>
    private const string JournalModePragma = "PRAGMA journal_mode=WAL;";

    private const string DdlCommandText = """
        CREATE TABLE IF NOT EXISTS local_users (
            id                 TEXT PRIMARY KEY NOT NULL,
            user_name          TEXT NOT NULL,
            display_name       TEXT NOT NULL DEFAULT '',
            password_hash      TEXT NOT NULL,
            password_salt      TEXT NOT NULL,
            password_iterations INTEGER NOT NULL DEFAULT 600000,
            password_key_size  INTEGER NOT NULL DEFAULT 32,
            created_at         TEXT NOT NULL,
            last_login_at      TEXT NULL,
            is_admin           INTEGER NOT NULL DEFAULT 0,
            is_disabled        INTEGER NOT NULL DEFAULT 0,
            data_directory_name TEXT NOT NULL DEFAULT ''
        );
        CREATE UNIQUE INDEX IF NOT EXISTS ix_local_users_user_name ON local_users(user_name COLLATE NOCASE);
        """;

    /// <summary>
    /// SQLITE_BUSY / SQLITE_LOCKED 的重试间隔（毫秒），长度即最大重试次数。
    /// </summary>
    private static readonly int[] RetryDelays = [50, 100, 200];

    private readonly string _connectionString;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    /// <summary>
    /// 数据库文件路径。
    /// </summary>
    public string DatabasePath { get; }

    public LocalUserStore(string? databasePath = null)
    {
        DatabasePath = databasePath ?? Path.Combine(Paths.SharedData, DatabaseFileName);
        var directory = Path.GetDirectoryName(DatabasePath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        _connectionString = $"Data Source={DatabasePath};Pooling=True;Default Timeout=15";
    }

    private async Task<SqliteConnection> _CreateConnectionAsync()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(LocalUserStore));
        var conn = new SqliteConnection(_connectionString);
        try
        {
            await conn.OpenAsync().ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = BusyTimeoutPragma;
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            return conn;
        }
        catch
        {
            await conn.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// 所有数据库访问的唯一入口：在 <see cref="_gate"/> 内串行执行，
    /// 并在 SQLITE_BUSY / SQLITE_LOCKED 上按 50/100/200ms 退避重试。
    /// </summary>
    private async Task<T> _AccessAsync<T>(Func<SqliteConnection, Task<T>> work, CancellationToken ct)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(LocalUserStore));
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_disposed) throw new ObjectDisposedException(nameof(LocalUserStore));
            for (var attempt = 0; ; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    await using var conn = await _CreateConnectionAsync().ConfigureAwait(false);
                    return await work(conn).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // 取消不重试，按取消语义向上传播
                    throw;
                }
                catch (SqliteException ex) when (attempt < RetryDelays.Length && _IsTransientLockError(ex))
                {
                    // 数据库被其他连接短暂占用：退避后重试，仍失败则原样抛出
                    await Task.Delay(RetryDelays[attempt], ct).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task _AccessAsync(Func<SqliteConnection, Task> work, CancellationToken ct)
        => await _AccessAsync<bool>(async conn =>
        {
            await work(conn).ConfigureAwait(false);
            return true;
        }, ct).ConfigureAwait(false);

    /// <summary>
    /// 判断是否为可重试的瞬时锁冲突：5 = SQLITE_BUSY，6 = SQLITE_LOCKED。
    /// </summary>
    private static bool _IsTransientLockError(SqliteException ex) => ex.SqliteErrorCode is 5 or 6;

    /// <summary>
    /// 初始化数据库表结构。
    /// </summary>
    public Task InitializeAsync(CancellationToken ct = default)
        => _AccessAsync(async conn =>
        {
            // 先切到 WAL，再做建表，最后再确保一次等锁超时
            await using (var journalMode = conn.CreateCommand())
            {
                journalMode.CommandText = JournalModePragma;
                await journalMode.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = DdlCommandText;
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            cmd.CommandText = BusyTimeoutPragma;
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }, ct);

    /// <summary>
    /// 判断用户名是否已被占用（忽略大小写）。
    /// </summary>
    public Task<bool> UserNameExistsAsync(string userName, CancellationToken ct = default)
        => _AccessAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM local_users WHERE user_name = $name COLLATE NOCASE LIMIT 1";
            cmd.Parameters.AddWithValue("$name", userName);
            return await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is not null;
        }, ct);

    /// <summary>
    /// 插入用户。
    /// </summary>
    public Task AddAsync(LocalUser user, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        return _AccessAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO local_users (
                    id, user_name, display_name, password_hash, password_salt,
                    password_iterations, password_key_size, created_at, last_login_at,
                    is_admin, is_disabled, data_directory_name)
                VALUES (
                    $id, $userName, $displayName, $passwordHash, $passwordSalt,
                    $passwordIterations, $passwordKeySize, $createdAt, $lastLoginAt,
                    $isAdmin, $isDisabled, $dataDirectoryName);
                """;
            cmd.Parameters.AddWithValue("$id", user.Id);
            cmd.Parameters.AddWithValue("$userName", user.UserName);
            cmd.Parameters.AddWithValue("$displayName", user.DisplayName);
            cmd.Parameters.AddWithValue("$passwordHash", user.PasswordHash);
            cmd.Parameters.AddWithValue("$passwordSalt", user.PasswordSalt);
            cmd.Parameters.AddWithValue("$passwordIterations", user.PasswordIterations);
            cmd.Parameters.AddWithValue("$passwordKeySize", user.PasswordKeySize);
            cmd.Parameters.AddWithValue("$createdAt", user.CreatedAt.ToString("O"));
            cmd.Parameters.AddWithValue("$lastLoginAt", (object?)user.LastLoginAt?.ToString("O") ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$isAdmin", user.IsAdmin ? 1 : 0);
            cmd.Parameters.AddWithValue("$isDisabled", user.IsDisabled ? 1 : 0);
            cmd.Parameters.AddWithValue("$dataDirectoryName", user.DataDirectoryName);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }, ct);
    }

    /// <summary>
    /// 按用户名读取用户。
    /// </summary>
    public Task<LocalUser?> FindByUserNameAsync(string userName, CancellationToken ct = default)
        => _AccessAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT {Columns} FROM local_users WHERE user_name = $name COLLATE NOCASE LIMIT 1";
            cmd.Parameters.AddWithValue("$name", userName);
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            return await reader.ReadAsync(ct).ConfigureAwait(false) ? _Read(reader) : null;
        }, ct);

    /// <summary>
    /// 按标识读取用户。
    /// </summary>
    public Task<LocalUser?> FindByIdAsync(string id, CancellationToken ct = default)
        => _AccessAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT {Columns} FROM local_users WHERE id = $id LIMIT 1";
            cmd.Parameters.AddWithValue("$id", id);
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            return await reader.ReadAsync(ct).ConfigureAwait(false) ? _Read(reader) : null;
        }, ct);

    /// <summary>
    /// 列出全部用户，按创建时间升序。
    /// </summary>
    public Task<IReadOnlyList<LocalUser>> ListAsync(CancellationToken ct = default)
        => _AccessAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT {Columns} FROM local_users ORDER BY created_at ASC";
            var result = new List<LocalUser>();
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                result.Add(_Read(reader));
            return (IReadOnlyList<LocalUser>)result;
        }, ct);

    /// <summary>
    /// 更新用户全部可变字段。
    /// </summary>
    public Task UpdateAsync(LocalUser user, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        return _AccessAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE local_users SET
                    user_name = $userName,
                    display_name = $displayName,
                    password_hash = $passwordHash,
                    password_salt = $passwordSalt,
                    password_iterations = $passwordIterations,
                    password_key_size = $passwordKeySize,
                    last_login_at = $lastLoginAt,
                    is_admin = $isAdmin,
                    is_disabled = $isDisabled,
                    data_directory_name = $dataDirectoryName
                WHERE id = $id;
                """;
            cmd.Parameters.AddWithValue("$id", user.Id);
            cmd.Parameters.AddWithValue("$userName", user.UserName);
            cmd.Parameters.AddWithValue("$displayName", user.DisplayName);
            cmd.Parameters.AddWithValue("$passwordHash", user.PasswordHash);
            cmd.Parameters.AddWithValue("$passwordSalt", user.PasswordSalt);
            cmd.Parameters.AddWithValue("$passwordIterations", user.PasswordIterations);
            cmd.Parameters.AddWithValue("$passwordKeySize", user.PasswordKeySize);
            cmd.Parameters.AddWithValue("$lastLoginAt", (object?)user.LastLoginAt?.ToString("O") ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$isAdmin", user.IsAdmin ? 1 : 0);
            cmd.Parameters.AddWithValue("$isDisabled", user.IsDisabled ? 1 : 0);
            cmd.Parameters.AddWithValue("$dataDirectoryName", user.DataDirectoryName);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }, ct);
    }

    /// <summary>
    /// 删除用户。返回是否确实删除了记录。
    /// </summary>
    public Task<bool> DeleteAsync(string id, CancellationToken ct = default)
        => _AccessAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM local_users WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0;
        }, ct);

    /// <summary>
    /// 统计用户总数。
    /// </summary>
    public Task<int> CountAsync(CancellationToken ct = default)
        => _AccessAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM local_users";
            return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false));
        }, ct);

    private const string Columns =
        "id, user_name, display_name, password_hash, password_salt, password_iterations, " +
        "password_key_size, created_at, last_login_at, is_admin, is_disabled, data_directory_name";

    private static LocalUser _Read(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(0),
        UserName = reader.GetString(1),
        DisplayName = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
        PasswordHash = reader.GetString(3),
        PasswordSalt = reader.GetString(4),
        PasswordIterations = reader.GetInt32(5),
        PasswordKeySize = reader.GetInt32(6),
        CreatedAt = DateTimeOffset.TryParse(reader.GetString(7), out var created) ? created : DateTimeOffset.UtcNow,
        LastLoginAt = reader.IsDBNull(8)
            ? null
            : DateTimeOffset.TryParse(reader.GetString(8), out var last) ? last : null,
        IsAdmin = reader.GetInt32(9) != 0,
        IsDisabled = reader.GetInt32(10) != 0,
        DataDirectoryName = reader.IsDBNull(11) ? string.Empty : reader.GetString(11)
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _gate.Dispose();
        SqliteConnection.ClearAllPools();
    }
}