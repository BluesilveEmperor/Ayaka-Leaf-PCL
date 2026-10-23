using System;

namespace PCL.Core.Minecraft.Profile.LocalUsers;

/// <summary>
/// 本地用户的密码派生参数。PBKDF2-HMAC-SHA256，每用户独立随机盐。
/// </summary>
public sealed class PasswordHashParameters
{
    /// <summary>
    /// 默认迭代次数。OWASP 对 PBKDF2-HMAC-SHA256 的建议下限为 600,000。
    /// </summary>
    public const int DefaultIterations = 600_000;

    /// <summary>
    /// 派生密钥长度（字节）。
    /// </summary>
    public const int DefaultKeySize = 32;

    /// <summary>
    /// 盐长度（字节）。
    /// </summary>
    public const int SaltSize = 16;

    /// <summary>
    /// 迭代次数。
    /// </summary>
    public int Iterations { get; init; } = DefaultIterations;

    /// <summary>
    /// 派生密钥长度（字节）。
    /// </summary>
    public int KeySize { get; init; } = DefaultKeySize;
}

/// <summary>
/// 密码哈希结果，包含派生参数，便于日后在不影响旧用户的前提下提升迭代次数。
/// </summary>
public sealed class PasswordHash
{
    /// <summary>
    /// 哈希值（Base64）。
    /// </summary>
    public string Hash { get; init; } = string.Empty;

    /// <summary>
    /// 盐（Base64）。
    /// </summary>
    public string Salt { get; init; } = string.Empty;

    /// <summary>
    /// PBKDF2 迭代次数。
    /// </summary>
    public int Iterations { get; init; } = PasswordHashParameters.DefaultIterations;

    /// <summary>
    /// 派生密钥长度（字节）。
    /// </summary>
    public int KeySize { get; init; } = PasswordHashParameters.DefaultKeySize;
}

/// <summary>
/// 使用 PBKDF2-HMAC-SHA256 派生并校验密码。比较使用固定时间，避免计时侧信道。
/// </summary>
public static class PasswordHasher
{
    private const string AlgorithmName = "PBKDF2-SHA256";

    /// <summary>
    /// 为明文密码生成哈希与随机盐。
    /// </summary>
    public static PasswordHash Hash(string password, PasswordHashParameters? parameters = null)
    {
        ArgumentNullException.ThrowIfNull(password);
        parameters ??= new PasswordHashParameters();

        var salt = System.Security.Cryptography.RandomNumberGenerator.GetBytes(PasswordHashParameters.SaltSize);
        var key = DeriveKey(password, salt, parameters.Iterations, parameters.KeySize);

        return new PasswordHash
        {
            Hash = Convert.ToBase64String(key),
            Salt = Convert.ToBase64String(salt),
            Iterations = parameters.Iterations,
            KeySize = parameters.KeySize
        };
    }

    /// <summary>
    /// 使用存储的盐与迭代次数校验明文密码。
    /// </summary>
    public static bool Verify(string password, PasswordHash hash)
    {
        ArgumentNullException.ThrowIfNull(password);
        ArgumentNullException.ThrowIfNull(hash);

        if (string.IsNullOrEmpty(hash.Hash) || string.IsNullOrEmpty(hash.Salt)) return false;

        byte[] salt;
        byte[] expected;
        try
        {
            salt = Convert.FromBase64String(hash.Salt);
            expected = Convert.FromBase64String(hash.Hash);
        }
        catch (FormatException)
        {
            return false;
        }

        var iterations = hash.Iterations > 0 ? hash.Iterations : PasswordHashParameters.DefaultIterations;
        var keySize = hash.KeySize > 0 ? hash.KeySize : expected.Length;

        var actual = DeriveKey(password, salt, iterations, keySize);
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static byte[] DeriveKey(string password, byte[] salt, int iterations, int keySize)
        => System.Security.Cryptography.Rfc2898DeriveBytes.Pbkdf2(
            System.Text.Encoding.UTF8.GetBytes(password),
            salt,
            iterations,
            System.Security.Cryptography.HashAlgorithmName.SHA256,
            keySize);
}
