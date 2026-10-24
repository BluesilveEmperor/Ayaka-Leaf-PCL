using System;
using System.Text.Json.Serialization;

namespace PCL.Core.Users;

/// <summary>
/// 本地用户的持久化模型。
/// </summary>
public sealed class LocalUser
{
    /// <summary>
    /// 用户唯一标识（GUID，32 位十六进制）。
    /// </summary>
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;

    /// <summary>
    /// 登录用户名。唯一，且作为该用户游戏目录的目录名。
    /// </summary>
    [JsonPropertyName("userName")] public string UserName { get; set; } = string.Empty;

    /// <summary>
    /// 显示名称，默认为登录用户名。
    /// </summary>
    [JsonPropertyName("displayName")] public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// 密码哈希（Base64）。绝不存储明文。
    /// </summary>
    [JsonPropertyName("passwordHash")] public string PasswordHash { get; set; } = string.Empty;

    /// <summary>
    /// 密码盐（Base64）。
    /// </summary>
    [JsonPropertyName("passwordSalt")] public string PasswordSalt { get; set; } = string.Empty;

    /// <summary>
    /// PBKDF2 迭代次数。
    /// </summary>
    [JsonPropertyName("passwordIterations")] public int PasswordIterations { get; set; } = PasswordHashParameters.DefaultIterations;

    /// <summary>
    /// 派生密钥长度（字节）。
    /// </summary>
    [JsonPropertyName("passwordKeySize")] public int PasswordKeySize { get; set; } = PasswordHashParameters.DefaultKeySize;

    /// <summary>
    /// 创建时间（UTC）。
    /// </summary>
    [JsonPropertyName("createdAt")] public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// 最后一次成功登录时间（UTC）。从未登录时为 null。
    /// </summary>
    [JsonPropertyName("lastLoginAt")] public DateTimeOffset? LastLoginAt { get; set; }

    /// <summary>
    /// 是否为管理员。首个注册用户自动成为管理员。
    /// </summary>
    [JsonPropertyName("isAdmin")] public bool IsAdmin { get; set; }

    /// <summary>
    /// 是否已停用。停用后不能登录，但数据保留。
    /// </summary>
    [JsonPropertyName("isDisabled")] public bool IsDisabled { get; set; }

    /// <summary>
    /// 该用户的独立游戏目录名。默认与用户名一致，可与用户名解耦。
    /// </summary>
    [JsonPropertyName("dataDirectoryName")] public string DataDirectoryName { get; set; } = string.Empty;

    /// <summary>
    /// 由密码哈希与盐构造的验证对象。
    /// </summary>
    public PasswordHash ToPasswordHash() => new()
    {
        Hash = PasswordHash,
        Salt = PasswordSalt,
        Iterations = PasswordIterations,
        KeySize = PasswordKeySize
    };
}
