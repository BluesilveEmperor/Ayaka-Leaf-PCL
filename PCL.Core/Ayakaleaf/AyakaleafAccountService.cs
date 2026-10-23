using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace PCL.Core.Ayakaleaf;

/// <summary>Ayakaleaf 网页端账号凭据。</summary>
public sealed record AyakaleafAccount(string Email, string Password);

/// <summary>
/// Ayakaleaf 网页端账号自动开户与登录：
/// 为每个本地用户生成并注册一套 Ayakaleaf 账号（email + 随机密码，DPAPI 加密存储），
/// 登录后取会话 Cookie 供内嵌工作台注入。
/// </summary>
public static class AyakaleafAccountService
{
    private static readonly CookieContainer Cookies = new();
    private static readonly HttpClient Http;

    private static string AccountsDirectory => Path.Combine(AppContext.BaseDirectory, "data", "ayakaleaf", "accounts");

    static AyakaleafAccountService()
    {
        var handler = new HttpClientHandler
        {
            UseProxy = false, // 本地服务，绝不能走系统代理
            UseCookies = true,
            CookieContainer = Cookies,
            AllowAutoRedirect = false,
        };
        Http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
    }

    /// <summary>
    /// 获取本地用户的 Ayakaleaf 账号；不存在则自动生成并注册。
    /// 注册失败时若为「已存在」则直接复用。
    /// </summary>
    public static async Task<AyakaleafAccount> GetOrCreateAccountAsync(string localUserName)
    {
        if (string.IsNullOrWhiteSpace(localUserName))
            throw new InvalidOperationException("本地用户未登录，无法打开工作台");

        Directory.CreateDirectory(AccountsDirectory);
        var file = Path.Combine(AccountsDirectory, localUserName + ".json");
        if (File.Exists(file))
        {
            try
            {
                var saved = JsonSerializer.Deserialize<AccountFile>(File.ReadAllText(file));
                if (saved is not null)
                    return new AyakaleafAccount(saved.Email, Unprotect(saved.Password));
            }
            catch
            {
                // 凭据文件损坏则重建
            }
        }

        var account = new AyakaleafAccount(
            localUserName.ToLowerInvariant() + "@ayaka.local",
            GeneratePassword());
        await RegisterAsync(account);
        File.WriteAllText(file, JsonSerializer.Serialize(new AccountFile
        {
            Email = account.Email,
            Password = Protect(account.Password),
        }));
        return account;
    }

    /// <summary>登录并返回会话 Cookie（overleaf.sid）。</summary>
    public static async Task<string> LoginAsync(AyakaleafAccount account)
    {
        var csrf = await GetCsrfTokenAsync();
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["_csrf"] = csrf,
            ["email"] = account.Email,
            ["password"] = account.Password,
        });
        using var resp = await Http.PostAsync(AyakaleafService.BaseUrl + "/login", form);
        var sid = ReadCookie("overleaf.sid");
        if (string.IsNullOrEmpty(sid))
            throw new Exception($"登录 Ayakaleaf 失败：HTTP {(int)resp.StatusCode}，未取得会话");
        return sid;
    }

    /// <summary>向 Ayakaleaf 注册账号（已存在视为成功）。</summary>
    public static async Task RegisterAsync(AyakaleafAccount account)
    {
        var csrf = await GetCsrfTokenAsync();
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["_csrf"] = csrf,
            ["email"] = account.Email,
            ["password"] = account.Password,
        });
        using var resp = await Http.PostAsync(AyakaleafService.BaseUrl + "/register", form);
        var code = (int)resp.StatusCode;
        if (code == 200) return; // 成功：{redir:'/login'}
        if (code == 400)
        {
            var body = await resp.Content.ReadAsStringAsync();
            if (body.Contains("already", StringComparison.OrdinalIgnoreCase) ||
                body.Contains("已被使用", StringComparison.Ordinal))
                return; // 账号已存在，直接复用
            throw new Exception("注册 Ayakaleaf 账号失败：" + body);
        }
        throw new Exception($"注册 Ayakaleaf 账号失败：HTTP {code}");
    }

    private static async Task<string> GetCsrfTokenAsync()
    {
        var body = await Http.GetStringAsync(AyakaleafService.BaseUrl + "/login");
        var m = Regex.Match(body, "ol-csrfToken\" content=\"([^\"]+)\"");
        if (!m.Success)
            throw new Exception("未能在 Ayakaleaf 登录页找到 CSRF 令牌（服务可能未就绪）");
        return m.Groups[1].Value;
    }

    private static string ReadCookie(string name)
    {
        var cookies = Cookies.GetCookies(new Uri(AyakaleafService.BaseUrl));
        foreach (Cookie c in cookies)
            if (c.Name == name)
                return c.Value;
        return null;
    }

    private static string GeneratePassword()
    {
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghjkmnpqrstuvwxyz23456789";
        var bytes = RandomNumberGenerator.GetBytes(24);
        var sb = new StringBuilder(24);
        foreach (var b in bytes)
            sb.Append(chars[b % chars.Length]);
        return sb.ToString();
    }

    private static string Protect(string plain)
    {
        var data = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(data);
    }

    private static string Unprotect(string encoded)
    {
        var data = ProtectedData.Unprotect(Convert.FromBase64String(encoded), null, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(data);
    }

    private sealed class AccountFile
    {
        public string Email { get; set; }
        public string Password { get; set; }
    }
}
