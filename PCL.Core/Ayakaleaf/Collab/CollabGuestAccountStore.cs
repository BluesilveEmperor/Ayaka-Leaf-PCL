using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PCL.Core.Logging;

namespace PCL.Core.Ayakaleaf.Collab;

/// <summary>
/// 协作访客账号契约源头（受邀端视角）：实例标识、显示名清洗、协作邮箱生成、
/// 协作凭据的 DPAPI 加密存取，以及房主端的协作账号识别与显示名解码。
/// <para>
/// 本类是主程序与 CollabLite 的**单一事实来源**：
/// <c>tools/collab-lite/CollabLite.ps1</c> 的显示名清洗、邮箱格式、随机密码字符集、
/// 实例标识文件与凭据文件结构均逐行对齐本类实现，修改本类必须同步轻量端。
/// </para>
/// <para>
/// 契约要点（双端一致）：
/// 协作邮箱 <c>{清洗后显示名}.{4 位十六进制实例标识}@collab.ayaka.local</c>，
/// 与房主本地用户邮箱 <c>{user}.{8 位目录标识}@ayaka.local</c> 域隔离；
/// 凭据按 16 位邀请码数字隔离存储（Email 明文 + DPAPI（CurrentUser）密码密文，
/// 临时文件 + 原子替换），重复加入同一邀请码复用既有凭据不开户。
/// </para>
/// </summary>
public static class CollabGuestAccountStore
{
    /// <summary>协作账号邮箱域名（与房主本地用户的 ayaka.local 命名空间隔离）。</summary>
    public const string CollabEmailDomain = "collab.ayaka.local";

    /// <summary>显示名清洗后的最大长度（截断上限）。</summary>
    public const int MaxDisplayNameLength = 32;

    /// <summary>实例标识文件路径：启动器目录下 <c>data\ayakaleaf\collab-guest-id.txt</c>（首次生成后持久只读）。</summary>
    public static string GuestIdFilePath { get; } =
        Path.Combine(AppContext.BaseDirectory, "data", "ayakaleaf", "collab-guest-id.txt");

    /// <summary>协作凭据存储目录：启动器目录下 <c>data\ayakaleaf\collab-accounts</c>（按邀请码隔离）。</summary>
    public static string CredentialsDirectory { get; } =
        Path.Combine(AppContext.BaseDirectory, "data", "ayakaleaf", "collab-accounts");

    /// <summary>
    /// 读取或首次生成受邀端实例标识（4 位小写十六进制）：
    /// 每个安装实例只生成一次并持久保存，用于区分同显示名的不同受邀端，
    /// 同时保证同一受邀端重复加入时身份稳定。
    /// </summary>
    public static string GetOrCreateGuestId()
    {
        // 已有合法标识：直接复用，绝不重写（重复加入身份稳定的基石）
        var existing = _TryReadGuestId();
        if (existing is not null) return existing;

        // 首次生成：随机 4 位小写十六进制 + 原子写入（临时文件 + 替换）
        var guestId = RandomHex(4);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(GuestIdFilePath)!);
            var tempFile = GuestIdFilePath + ".tmp";
            File.WriteAllText(tempFile, guestId, new UTF8Encoding(false));
            File.Move(tempFile, GuestIdFilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw _StorageFailed(GuestIdFilePath, ex);
        }

        LogWrapper.Info($"[Collab] 已生成协作实例标识（collab-guest-id），本实例的协作账号后缀固定为 .{guestId}");
        return guestId;
    }

    /// <summary>
    /// 把队友显示名转换为合法的邮箱本地部分（语义对齐 <c>AyakaleafAccountService._SanitizeEmailLocalPart</c>）：
    /// 去首尾空白 → 转小写 → 仅保留 <c>[a-z0-9._-]</c>，其余字符（含中文、空格、@ 等）替换为下划线 →
    /// 全空回退 <c>guest</c> → 截断到 <see cref="MaxDisplayNameLength"/> 字符。
    /// </summary>
    public static string SanitizeDisplayName(string displayName)
    {
        var lower = (displayName ?? "").Trim().ToLowerInvariant();
        var sb = new StringBuilder(lower.Length);
        foreach (var c in lower)
        {
            var allowed = c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '_' or '-';
            sb.Append(allowed ? c : '_');
        }

        if (sb.Length == 0) return "guest";
        return sb.Length <= MaxDisplayNameLength ? sb.ToString() : sb.ToString(0, MaxDisplayNameLength);
    }

    /// <summary>
    /// 生成协作账号邮箱：<c>{清洗后显示名}.{4 位实例标识}@collab.ayaka.local</c>。
    /// 实例标识保证同显示名的不同受邀端互不冲突，同一受邀端重复加入邮箱不变。
    /// </summary>
    public static string BuildCollabEmail(string displayName) =>
        $"{SanitizeDisplayName(displayName)}.{GetOrCreateGuestId()}@{CollabEmailDomain}";

    /// <summary>
    /// 生成协作账号随机密码：24 字符，沿用主程序既有字符集（排除易混字符）。
    /// 与 <c>AyakaleafAccountService</c> 的本地用户随机密码同一规则，CollabLite 逐行对齐。
    /// </summary>
    public static string GenerateCollabPassword()
    {
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghjkmnpqrstuvwxyz23456789";
        var bytes = RandomNumberGenerator.GetBytes(24);
        var sb = new StringBuilder(24);
        foreach (var b in bytes)
            sb.Append(chars[b % chars.Length]);
        return sb.ToString();
    }

    /// <summary>
    /// 按邀请码读取既有协作凭据（重复加入复用，不开户）。
    /// 凭据不存在、文件损坏、JSON 非法或 DPAPI 解密失败时统一返回 null（按「无凭据」走重新开户路径）。
    /// </summary>
    /// <param name="inviteCode">邀请码（规范形式 A/XXXX-XXXX-XXXX-XXXX 或 16 位数字串均可）。</param>
    public static AyakaleafAccount? TryLoadCredential(string inviteCode)
    {
        var key = NormalizeInviteKey(inviteCode);
        if (key is null) return null;

        var path = Path.Combine(CredentialsDirectory, key + ".json");
        try
        {
            if (!File.Exists(path)) return null;

            var json = File.ReadAllText(path);
            var saved = JsonSerializer.Deserialize<CredentialFile>(json);
            if (saved is null) return null;

            var email = saved.Email;
            var encryptedPassword = saved.Password;
            if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(encryptedPassword)) return null;

            var password = _Unprotect(encryptedPassword);
            if (string.IsNullOrEmpty(password)) return null;

            // 既有文件中的邮箱原样返回（不按新规则重算），保证既有协作身份不变
            return new AyakaleafAccount(email, password);
        }
        catch
        {
            // JSON 损坏、IO 失败、Base64 非法或 DPAPI 解密失败：统一按「不可用」处理，走重新开户
            return null;
        }
    }

    /// <summary>
    /// 原子写入协作凭据（临时文件 + 替换，避免另一进程读到半写文件）。
    /// 仅保存 Email 明文与 DPAPI（CurrentUser）保护后的 Base64 密文，绝不写明文密码。
    /// </summary>
    /// <param name="inviteCode">邀请码（按 16 位数字串隔离存储）。</param>
    /// <param name="account">协作账号（密码仅存在于内存，不落日志）。</param>
    /// <exception cref="AyakaleafAccountException">凭据写入或 DPAPI 保护失败。</exception>
    public static void SaveCredential(string inviteCode, AyakaleafAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);

        var key = NormalizeInviteKey(inviteCode)
            ?? throw new AyakaleafAccountException(AyakaleafAccountErrorKind.CredentialStorageFailed,
                "邀请码格式无效，无法保存协作凭据。");

        try
        {
            Directory.CreateDirectory(CredentialsDirectory);
        }
        catch (Exception ex) when (_IsStorageFailure(ex))
        {
            throw _StorageFailed(CredentialsDirectory, ex);
        }

        var path = Path.Combine(CredentialsDirectory, key + ".json");
        var tempFile = path + ".tmp";
        try
        {
            var json = JsonSerializer.Serialize(new CredentialFile
            {
                Email = account.Email,
                Password = _Protect(account.Password),
            });
            File.WriteAllText(tempFile, json, new UTF8Encoding(false));
            File.Move(tempFile, path, overwrite: true);
        }
        catch (Exception ex) when (_IsStorageFailure(ex) || ex is CryptographicException)
        {
            _TryDeleteFile(tempFile);
            throw _StorageFailed(path, ex);
        }
    }

    /// <summary>判断邮箱是否属于协作账号（按 <c>@collab.ayaka.local</c> 域名识别，房主端 PageAccounts 复用）。</summary>
    public static bool IsCollabAccount(string email) =>
        !string.IsNullOrEmpty(email) &&
        email.EndsWith("@" + CollabEmailDomain, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 从协作账号邮箱解码队友显示名：去掉 <c>@collab.ayaka.local</c> 域名与尾部
    /// <c>.{4 位十六进制实例标识}</c> 后缀。非协作邮箱或解码结果为空时返回 null。
    /// </summary>
    public static string? TryDecodeDisplayName(string email)
    {
        if (!IsCollabAccount(email)) return null;

        var localPart = email[..^("@" + CollabEmailDomain).Length].Trim();
        // 实例标识固定为 4 位小写十六进制，且只出现在末尾
        if (localPart.Length > 5 && localPart[^5] == '.')
        {
            var suffix = localPart[^4..];
            var isHex = true;
            foreach (var c in suffix)
            {
                if (c is not ((>= '0' and <= '9') or (>= 'a' and <= 'f')))
                {
                    isHex = false;
                    break;
                }
            }
            if (isHex) localPart = localPart[..^5];
        }

        return localPart.Length == 0 ? null : localPart;
    }

    /// <summary>
    /// 把邀请码规范化为凭据存储键（16 位数字串）：剥离 A/ 前缀与分隔符、转大写。
    /// 与 <c>CollabCode.RawDigits</c> 语义一致；无法得到 16 位数字串时返回 null。
    /// </summary>
    public static string? NormalizeInviteKey(string inviteCode)
    {
        if (string.IsNullOrEmpty(inviteCode)) return null;

        var sb = new StringBuilder(20);
        foreach (var c in inviteCode.Trim().ToUpperInvariant())
        {
            if (char.IsLetterOrDigit(c)) sb.Append(c);
        }

        var digits = sb.ToString();
        if (digits.Length == 17 && digits[0] == 'A')
            digits = digits[1..]; // 剥离 A/ 前缀字符

        return digits.Length == 16 ? digits : null;
    }

    // ---------------- 内部实现 ----------------

    /// <summary>读取并校验实例标识文件（4 位小写十六进制）；不存在或非法返回 null。</summary>
    private static string? _TryReadGuestId()
    {
        try
        {
            if (!File.Exists(GuestIdFilePath)) return null;
            var value = File.ReadAllText(GuestIdFilePath).Trim();
            if (value.Length != 4) return null;
            foreach (var c in value)
            {
                if (c is not ((>= '0' and <= '9') or (>= 'a' and <= 'f')))
                    return null;
            }
            return value;
        }
        catch
        {
            return null;
        }
    }

    private static string RandomHex(int digits)
    {
        var bytes = RandomNumberGenerator.GetBytes((digits + 1) / 2);
        return Convert.ToHexString(bytes).ToLowerInvariant()[..digits];
    }

    private static bool _IsStorageFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or NotSupportedException
            or System.Security.SecurityException or ArgumentException;

    /// <summary>用 DPAPI（CurrentUser）保护密码，返回 Base64 字符串。</summary>
    private static string _Protect(string plain)
    {
        var data = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(data);
    }

    /// <summary>用 DPAPI（CurrentUser）解保护 Base64 密码，返回明文。</summary>
    private static string _Unprotect(string encoded)
    {
        var data = ProtectedData.Unprotect(Convert.FromBase64String(encoded), null, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(data);
    }

    private static void _TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex)
        {
            LogWrapper.Warn($"[Collab] 清理协作凭据临时文件失败：{Path.GetFileName(path)}：{ex.Message}");
        }
    }

    private static AyakaleafAccountException _StorageFailed(string path, Exception inner) =>
        new(AyakaleafAccountErrorKind.CredentialStorageFailed,
            $"保存协作账号凭据失败（{path}）：{inner.Message}。请检查该目录的读写权限后重试。",
            innerException: inner);

    /// <summary>协作凭据文件结构（与 CollabLite.ps1 逐行对齐：Email 明文 + DPAPI 密文）。</summary>
    private sealed class CredentialFile
    {
        /// <summary>协作账号邮箱。</summary>
        public string? Email { get; set; }

        /// <summary>DPAPI（CurrentUser）保护后的 Base64 密码，绝不含明文。</summary>
        public string? Password { get; set; }
    }
}
