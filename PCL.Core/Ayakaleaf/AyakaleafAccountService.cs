using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using PCL.Core.App;
using PCL.Core.Logging;

namespace PCL.Core.Ayakaleaf;

/// <summary>Ayakaleaf 网页端账号凭据。</summary>
public sealed record AyakaleafAccount(string Email, string Password);

/// <summary>Ayakaleaf 账号流程的失败分类。</summary>
public enum AyakaleafAccountErrorKind
{
    /// <summary>无法连接服务：未启动、端口不通或超时。</summary>
    ServiceUnavailable,

    /// <summary>服务有响应但尚未就绪（取不到 CSRF、5xx 等）。</summary>
    ServiceNotReady,

    /// <summary>服务端已存在同名邮箱，但本机没有对应的登录凭据。</summary>
    AccountConflict,

    /// <summary>注册被服务端拒绝（非「已存在」原因）。</summary>
    RegistrationRejected,

    /// <summary>本机凭据存在但登录失败（密码不符或账号已被删除）。</summary>
    CredentialInvalid,

    /// <summary>启动器尚未登录本地用户。</summary>
    LocalUserMissing,

    /// <summary>凭据文件读写或 DPAPI 解密失败。</summary>
    CredentialStorageFailed,

    /// <summary>重置 Ayakaleaf 账号密码被服务端拒绝（旧密码不符、密码策略不满足或触发限流）。</summary>
    PasswordResetRejected,

    /// <summary>删除 Ayakaleaf 服务端账号被拒绝（缺少管理员凭据 / 管理端点拒绝等）。</summary>
    AccountDeleteRejected,
}

/// <summary>
/// Ayakaleaf 账号流程异常。<see cref="Exception.Message"/> 为可直接展示给用户的简体中文文案，
/// 调用方无需再拼接分类前缀；异常与日志中绝不包含密码明文或密文。
/// </summary>
public sealed class AyakaleafAccountException : Exception
{
    /// <summary>失败分类。</summary>
    public AyakaleafAccountErrorKind Kind { get; }

    /// <summary>本次流程涉及的 Ayakaleaf 账号邮箱（能取得时填充）。</summary>
    public string? Email { get; }

    /// <summary>本次流程涉及的启动器本地用户名（能取得时填充）。</summary>
    public string? LocalUserName { get; }

    /// <summary>创建账号流程异常。</summary>
    /// <param name="kind">失败分类。</param>
    /// <param name="message">可直接展示给用户的简体中文文案。</param>
    /// <param name="email">涉及的 Ayakaleaf 账号邮箱。</param>
    /// <param name="localUserName">涉及的启动器本地用户名。</param>
    /// <param name="innerException">内部异常（不得包含密码）。</param>
    public AyakaleafAccountException(
        AyakaleafAccountErrorKind kind,
        string message,
        string? email = null,
        string? localUserName = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
        Email = email;
        LocalUserName = localUserName;
    }
}

/// <summary>
/// Ayakaleaf 网页端账号自动开户与登录：
/// 为每个本地用户生成并注册一套 Ayakaleaf 账号（email + 随机密码，DPAPI 加密存储），
/// 登录后取会话 Cookie 供内嵌工作台注入。
/// 凭据存放在**启动器所在目录**下（便携、可随目录整体拷贝），不同启动器目录互不串号；
/// 为保证全局单实例的 Overleaf 服务中不同目录不撞同一账号，开户邮箱附带启动器目录标识。
/// </summary>
public static class AyakaleafAccountService
{
    /// <summary>等待互斥量的上限，超时视为同一启动器目录下另有实例正在写入凭据。</summary>
    private static readonly TimeSpan MutexTimeout = TimeSpan.FromSeconds(10);

    private static readonly CookieContainer Cookies = new();
    private static readonly HttpClient Http;

    /// <summary>
    /// 启动器目录标识：exe 目录绝对路径（规范化、转小写以做到大小写不敏感）的
    /// SHA256 前 8 位小写十六进制。同一目录恒定、不同目录不同。
    /// </summary>
    private static readonly string DirectoryId = _ComputeDirectoryId(AppContext.BaseDirectory);

    /// <summary>
    /// 跨进程命名互斥量名称：把启动器目录标识纳入名称，使不同目录的启动器互不阻塞，
    /// 同一目录下 Debug / Release / 多开实例仍严格串行。
    /// 选择命名互斥量而非 <c>*.lock</c> 文件锁：进程异常退出时操作系统会自动释放互斥量
    /// （以 <see cref="AbandonedMutexException"/> 形式接管），不会留下永久死锁的锁文件。
    /// </summary>
    private static readonly string MutexName = @"Local\Ayakaleaf.Accounts." + DirectoryId;

    /// <summary>
    /// 凭据存储根目录：启动器所在目录下的 <c>data\ayakaleaf\accounts</c>。
    /// 每个启动器目录各自一份凭据（便携，可随目录整体拷贝），不写入系统目录；
    /// 因此更换启动器目录即更换账号体系，互不串号。本属性无 IO、无副作用（不创建目录）。
    /// </summary>
    public static string AccountsDirectory { get; } =
        Path.Combine(AppContext.BaseDirectory, "data", "ayakaleaf", "accounts");

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

    // ---------------- 旧位置枚举与迁移（已停用，仅为兼容既有调用保留签名） ----------------

    /// <summary>
    /// 旧凭据目录枚举（已停用）。
    /// 凭据现按启动器目录隔离，不再跨目录迁移，因此恒返回空集合；
    /// 保留公开签名只为兼容既有调用方与验证探针，不再读取任何上级 <c>bin</c> 目录。
    /// </summary>
    /// <param name="baseDirectory">已停用参数，忽略。</param>
    /// <returns>恒为空集合。</returns>
    public static IReadOnlyList<string> EnumerateLegacyAccountDirectories(string? baseDirectory = null)
    {
        _ = baseDirectory; // 兼容保留的参数，不参与任何路径计算
        return Array.Empty<string>();
    }

    /// <summary>
    /// 账号凭据目录幂等兜底（已停用跨目录迁移）：确保目标目录存在，不做任何文件复制。
    /// 保留公开签名只为兼容既有调用方与验证探针。
    /// </summary>
    /// <param name="baseDirectory">已停用参数，忽略。</param>
    /// <param name="targetDirectory">目标目录；为 null 时使用 <see cref="AccountsDirectory"/>（仅供探针注入临时目录）。</param>
    /// <returns>本次新迁移的文件数，恒为 0。</returns>
    /// <exception cref="AyakaleafAccountException">目标目录无法创建（<see cref="AyakaleafAccountErrorKind.CredentialStorageFailed"/>）。</exception>
    public static int MigrateLegacyAccounts(string? baseDirectory = null, string? targetDirectory = null)
    {
        _ = baseDirectory;

        var target = targetDirectory ?? AccountsDirectory;
        try
        {
            Directory.CreateDirectory(target);
        }
        catch (Exception ex) when (_IsStorageFailure(ex))
        {
            throw _StorageFailed(target, ex);
        }

        return 0;
    }

    // ---------------- 获取或开户 ----------------

    /// <summary>获取本地用户的 Ayakaleaf 账号；不存在则自动生成并注册。</summary>
    /// <param name="localUserName">启动器当前登录的本地用户名。</param>
    /// <returns>可直接用于登录的账号（密码为内存明文，不会写日志）。</returns>
    /// <exception cref="AyakaleafAccountException">账号流程失败，Message 为可直接展示给用户的简体中文文案。</exception>
    public static Task<AyakaleafAccount> GetOrCreateAccountAsync(string localUserName)
    {
        // 本地用户缺失：不发起任何 HTTP 请求、不触碰凭据文件
        if (string.IsNullOrWhiteSpace(localUserName))
            throw _LocalUserMissing();

        // 命名互斥量具有线程亲和性（获取与释放必须在同一线程），而互斥区内又包含
        // HTTP 的 await 调用。为保证「读取/开户 + 原子写入」整段串行且能正确释放互斥量，
        // 把整段放到独立工作线程执行，并在该线程内同步衔接各异步调用。
        return Task.Run(() => _GetOrCreateAccountCore(localUserName));
    }

    /// <summary>
    /// 在跨进程互斥区内完成「读取或注册 → 原子写入」，返回可用账号。
    /// 该方法为同步实现，运行在 <see cref="GetOrCreateAccountAsync"/> 创建的单一工作线程上。
    /// </summary>
    private static AyakaleafAccount _GetOrCreateAccountCore(string localUserName)
    {
        using var mutex = new Mutex(false, MutexName);
        var mutexAcquired = _AcquireAccountsMutex(mutex);
        try
        {

            try
            {
                Directory.CreateDirectory(AccountsDirectory);
            }
            catch (Exception ex) when (_IsStorageFailure(ex))
            {
                throw _StorageFailed(AccountsDirectory, ex);
            }

            var userName = localUserName.Trim();
            var file = Path.Combine(AccountsDirectory, userName + ".json");

            // 锁内二次确认：文件已存在则直接复用，绝不重写（既有账号与既有邮箱保持原样）
            if (File.Exists(file))
            {
                if (_TryReadAccountFile(file, out var savedAccount) && savedAccount is not null)
                    return savedAccount;

                // 存在但不可用：改名为 .bak 保留（不删除），随后按「无凭据」重新开户
                _QuarantineCorruptFile(file);
            }

            var account = new AyakaleafAccount(_BuildAccountEmail(userName), _GeneratePassword());

            // 注册命中「已存在」会抛 AccountConflict，此处原样向上抛：禁止继续登录、禁止写凭据文件
            RegisterAsync(account).GetAwaiter().GetResult();

            _WriteAccountFileAtomic(file, account);

            // 管理员开通（幂等）：首个开户的本地用户自动认领 Ayakaleaf 管理员（AdminLocalUserName），
            // 此后仅配置指定的本地用户可获管理员。开通失败不影响开户本身：只记日志，
            // 打开管理面板时会经 EnsureServerAdminAsync 重试
            _ = _EnsureServerAdminCore(userName);

            return account;
        }
        finally
        {
            // 所有退出路径（含异常）都必须释放互斥量
            _ReleaseAccountsMutex(mutex, mutexAcquired);
        }
    }

    // ---------------- 注册与登录 ----------------

    /// <summary>向 Ayakaleaf 注册账号（服务端已存在同名邮箱时显式失败，不再视为成功）。</summary>
    /// <param name="account">待注册账号。</param>
    /// <exception cref="AyakaleafAccountException">
    /// 服务不可用 / 未就绪 / 账号冲突（<see cref="AyakaleafAccountErrorKind.AccountConflict"/>）/ 注册被拒。
    /// </exception>
    public static async Task RegisterAsync(AyakaleafAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);

        var csrf = await _GetCsrfTokenAsync().ConfigureAwait(false);
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["_csrf"] = csrf,
            ["email"] = account.Email,
            ["password"] = account.Password,
        });

        HttpResponseMessage response;
        try
        {
            response = await Http.PostAsync(AyakaleafService.BaseUrl + "/register", form).ConfigureAwait(false);
        }
        catch (Exception ex) when (_IsConnectionFailure(ex))
        {
            throw _ServiceUnavailable(ex);
        }

        using (response)
        {
            var code = (int)response.StatusCode;
            if (code == 200) return; // 成功：{redir:'/login'}

            var body = await _ReadBodyAsync(response).ConfigureAwait(false);

            // 服务端已存在同名邮箱：必须显式失败，禁止用未验证的随机密码冒充成功
            if (code is 400 or 409 or 422 && _ContainsAlreadyRegistered(body))
                throw _AccountConflict(account.Email);

            if (code is 400 or 409 or 422)
                throw _RegistrationRejected(account.Email, code, body);

            throw _ServiceNotReady(code);
        }
    }

    /// <summary>登录并返回本次会话 Cookie（overleaf.sid）。</summary>
    /// <param name="account">已注册的账号。</param>
    /// <returns>本次登录响应下发的 overleaf.sid 值。</returns>
    /// <exception cref="AyakaleafAccountException">
    /// 服务不可用 / 未就绪 / 该账号登录失败（<see cref="AyakaleafAccountErrorKind.CredentialInvalid"/>）。
    /// </exception>
    public static async Task<string> LoginAsync(AyakaleafAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);

        var csrf = await _GetCsrfTokenAsync().ConfigureAwait(false);
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["_csrf"] = csrf,
            ["email"] = account.Email,
            ["password"] = account.Password,
        });

        HttpResponseMessage response;
        try
        {
            response = await Http.PostAsync(AyakaleafService.BaseUrl + "/login", form).ConfigureAwait(false);
        }
        catch (Exception ex) when (_IsConnectionFailure(ex))
        {
            throw _ServiceUnavailable(ex);
        }

        using (response)
        {
            var code = (int)response.StatusCode;
            var sid = _ReadCookie("overleaf.sid");

            // 成功判定只认本次响应：必须出现 Set-Cookie: overleaf.sid=，
            // 且 CookieContainer 中能取到非空值（禁止复用上一次会话的陈旧 Cookie）
            if (!_ResponseSetsSessionCookie(response) || string.IsNullOrEmpty(sid))
                throw _CredentialInvalid(account.Email, code);

            return sid;
        }
    }

    // ---------------- 管理员开通与账号生命周期 ----------------

    /// <summary>
    /// 确保指定本地用户具备 Ayakaleaf 服务端管理员身份（幂等）：
    /// <see cref="Config.Ayakaleaf.AdminLocalUserName" /> 为空时由该用户认领管理员；
    /// 已配置为其它用户时本用户不获管理员。随后通过 mongosh 把其服务端账号标记为 isAdmin。
    /// </summary>
    /// <param name="localUserName">启动器本地用户名。</param>
    /// <returns>该用户最终是否具备服务端管理员身份。</returns>
    /// <exception cref="AyakaleafAccountException">本地用户缺失。</exception>
    public static Task<bool> EnsureServerAdminAsync(string localUserName)
    {
        if (string.IsNullOrWhiteSpace(localUserName))
            throw _LocalUserMissing();
        return Task.Run(() => _EnsureServerAdminCore(localUserName));
    }

    /// <summary>认领 + mongosh 开通的同步核心（运行在独立工作线程上）。</summary>
    private static bool _EnsureServerAdminCore(string localUserName)
    {
        var userName = localUserName.Trim();

        var configured = Config.Ayakaleaf.AdminLocalUserName?.Trim();
        if (!string.IsNullOrEmpty(configured) &&
            !string.Equals(userName, configured, StringComparison.OrdinalIgnoreCase))
            return false; // 管理员已由其它本地用户担任

        if (string.IsNullOrEmpty(configured))
        {
            Config.Ayakaleaf.AdminLocalUserName = userName;
            LogWrapper.Info($"[Ayakaleaf] 本地用户 {userName} 已认领 Ayakaleaf 管理员（AdminLocalUserName）");
        }

        // 服务端邮箱：优先读本机凭据文件（含既有账号的原邮箱），缺失时按开户规则推算
        var file = Path.Combine(AccountsDirectory, userName + ".json");
        string email;
        if (_TryReadAccountFile(file, out var saved) && saved is not null)
            email = saved.Email;
        else
            email = _BuildAccountEmail(userName);

        var granted = AyakaleafService.TryGrantServerAdminAsync(email).GetAwaiter().GetResult();
        if (granted)
            LogWrapper.Info($"[Ayakaleaf] 已开通服务端管理员：{email}");

        return granted;
    }

    /// <summary>
    /// 重置本地用户对应的 Ayakaleaf 账号密码：
    /// 用本机 DPAPI 存储的旧凭据登录该账号 → 取登录态 CSRF →
    /// 提交 <c>/user/password/update</c> 改密表单（字段对照 ayakaleaf-pro UserController.changePassword）→
    /// 成功后以新随机密码原子重写本机凭据文件。
    /// </summary>
    /// <param name="localUserName">启动器本地用户名。</param>
    /// <returns>重置后的账号（新密码仅存在于内存，不会写日志）。</returns>
    /// <exception cref="AyakaleafAccountException">
    /// 本地用户缺失 / 无本机凭据 / 旧凭据登录失败 / 服务端拒绝（<see cref="AyakaleafAccountErrorKind.PasswordResetRejected"/>）等。
    /// </exception>
    public static Task<AyakaleafAccount> ResetPasswordAsync(string localUserName)
    {
        if (string.IsNullOrWhiteSpace(localUserName))
            throw _LocalUserMissing();
        return Task.Run(() => _ResetPasswordCore(localUserName));
    }

    /// <summary>重置密码的互斥核心：登录 → CSRF → 改密 → 原子重写凭据文件。</summary>
    private static AyakaleafAccount _ResetPasswordCore(string localUserName)
    {
        using var mutex = new Mutex(false, MutexName);
        var mutexAcquired = _AcquireAccountsMutex(mutex);

        var userName = localUserName.Trim();
        var file = Path.Combine(AccountsDirectory, userName + ".json");
        if (!_TryReadAccountFile(file, out var saved) || saved is null)
            throw _NoStoredCredential(userName);

        try
        {
            // 1) 旧凭据登录（密码不符 / 账号已删除会抛 CredentialInvalid）
            LoginAsync(saved).GetAwaiter().GetResult();

            // 2) 登录态取 CSRF（登录会重建会话，匿名页令牌不可复用）
            var csrf = _GetSessionCsrfTokenAsync().GetAwaiter().GetResult();

            // 3) 生成新随机密码并提交改密表单
            var newPassword = _GeneratePassword();
            _SubmitPasswordChangeAsync(saved, newPassword, csrf).GetAwaiter().GetResult();

            // 4) 以新密码原子重写本机凭据文件
            var updated = new AyakaleafAccount(saved.Email, newPassword);
            _WriteAccountFileAtomic(file, updated);
            LogWrapper.Info($"[Ayakaleaf] 已重置账号密码并更新本机凭据：{saved.Email}");
            return updated;
        }
        finally
        {
            _ReleaseAccountsMutex(mutex, mutexAcquired);
        }
    }

    /// <summary>向 <c>/user/password/update</c> 提交改密表单（currentPassword/newPassword1/newPassword2，对照 ayakaleaf-pro 路由与 zod 校验）。</summary>
    private static async Task _SubmitPasswordChangeAsync(AyakaleafAccount account, string newPassword, string csrf)
    {
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["_csrf"] = csrf,
            ["currentPassword"] = account.Password,
            ["newPassword1"] = newPassword,
            ["newPassword2"] = newPassword,
        });

        HttpResponseMessage response;
        try
        {
            response = await Http.PostAsync(AyakaleafService.BaseUrl + "/user/password/update", form)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (_IsConnectionFailure(ex))
        {
            throw _ServiceUnavailable(ex);
        }

        using (response)
        {
            var code = (int)response.StatusCode;
            if (code == 200) return; // 成功：{message:{type:'success',…}}

            var body = await _ReadBodyAsync(response).ConfigureAwait(false);
            if (code == 400)
                throw _PasswordResetRejected(account.Email, _TruncateDetail(body));
            if (code == 429)
                throw _PasswordResetRejected(account.Email, "尝试过于频繁，请稍后再试");

            throw _ServiceNotReady(code);
        }
    }

    /// <summary>
    /// 以管理员身份删除指定本地用户的 Ayakaleaf 服务端账号，并同步删除其本机凭据文件：
    /// 管理员登录 → 登录态 CSRF → <c>/admin/users/search</c> 按邮箱查 userId →
    /// <c>/admin/user/:userId/delete</c> 删除（端点与参数对照 ayakaleaf-pro admin-tools UserListController）。
    /// 目标账号在服务端不存在时仅清理本机凭据。
    /// </summary>
    /// <param name="targetLocalUserName">要删除服务端账号的本地用户名。</param>
    /// <exception cref="AyakaleafAccountException">
    /// 管理员凭据缺失 / 管理员登录失败 / 管理端点拒绝（<see cref="AyakaleafAccountErrorKind.AccountDeleteRejected"/>）等。
    /// </exception>
    public static Task DeleteServerAccountAsync(string targetLocalUserName)
    {
        if (string.IsNullOrWhiteSpace(targetLocalUserName))
            throw _LocalUserMissing();
        return Task.Run(() => _DeleteServerAccountCore(targetLocalUserName));
    }

    /// <summary>删除服务端账号的互斥核心（删除本机凭据文件前须与开户流程互斥）。</summary>
    private static void _DeleteServerAccountCore(string targetLocalUserName)
    {
        using var mutex = new Mutex(false, MutexName);
        var mutexAcquired = _AcquireAccountsMutex(mutex);

        var userName = targetLocalUserName.Trim();

        // 1) 管理员账号与凭据（AdminLocalUserName 对应本机凭据文件）
        var adminName = Config.Ayakaleaf.AdminLocalUserName?.Trim();
        if (string.IsNullOrEmpty(adminName))
            throw _AccountDeleteRejected(null,
                "尚未开通 Ayakaleaf 管理员账号。请先用管理员本地用户打开一次 Ayakaleaf 工作台或管理面板后再试。");

        var adminFile = Path.Combine(AccountsDirectory, adminName + ".json");
        if (!_TryReadAccountFile(adminFile, out var admin) || admin is null)
            throw _AccountDeleteRejected(null,
                $"本机没有 Ayakaleaf 管理员（{adminName}）的账号凭据。请先用该本地用户打开一次工作台后再试。");

        // 2) 目标邮箱：优先读目标本机凭据文件（保留既有账号原邮箱），缺失时按开户规则推算
        var targetFile = Path.Combine(AccountsDirectory, userName + ".json");
        string email;
        if (_TryReadAccountFile(targetFile, out var target) && target is not null)
            email = target.Email;
        else
            email = _BuildAccountEmail(userName);

        try
        {
            // 3) 管理员登录 → 登录态 CSRF
            LoginAsync(admin).GetAwaiter().GetResult();
            var csrf = _GetSessionCsrfTokenAsync().GetAwaiter().GetResult();

            // 4) 管理端按邮箱查 userId（找不到视为服务端账号已不存在）
            var userId = _AdminFindUserIdAsync(csrf, email).GetAwaiter().GetResult();
            if (userId is not null)
            {
                // 5) 管理端删除服务端账号
                _AdminDeleteUserAsync(csrf, userId).GetAwaiter().GetResult();
            }
            else
            {
                LogWrapper.Info($"[Ayakaleaf] 服务端不存在账号 {email}，跳过管理端删除，仅清理本机凭据");
            }

            // 6) 同步删除本机凭据文件
            _TryDeleteAccountFile(targetFile);
            LogWrapper.Info($"[Ayakaleaf] 已删除 Ayakaleaf 账号（服务端 + 本机凭据）：{email}");
        }
        finally
        {
            _ReleaseAccountsMutex(mutex, mutexAcquired);
        }
    }

    /// <summary>
    /// 取登录态页面的 CSRF 令牌（GET <c>/user/settings</c> 解析 <c>ol-csrfToken</c>）。
    /// 登录会重建会话，匿名页令牌不可复用；非 200（含跳转登录页的 302）按服务未就绪处理。
    /// </summary>
    private static async Task<string> _GetSessionCsrfTokenAsync()
    {
        HttpResponseMessage response;
        try
        {
            response = await Http.GetAsync(AyakaleafService.BaseUrl + "/user/settings").ConfigureAwait(false);
        }
        catch (Exception ex) when (_IsConnectionFailure(ex))
        {
            throw _ServiceUnavailable(ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw _ServiceNotReady((int)response.StatusCode);

            var body = await _ReadBodyAsync(response).ConfigureAwait(false);
            var match = Regex.Match(body, "ol-csrfToken\" content=\"([^\"]+)\"");
            if (!match.Success)
                throw _ServiceNotReady(null);

            return match.Groups[1].Value;
        }
    }

    /// <summary>管理端按邮箱搜索用户 id：<c>POST /admin/users/search</c>（JSON + X-Csrf-Token，响应 users[].id/email/deleted）。</summary>
    /// <returns>匹配的用户 id；服务端不存在该账号时返回 null。</returns>
    private static async Task<string?> _AdminFindUserIdAsync(string csrf, string email)
    {
        var payload = JsonSerializer.Serialize(new AdminSearchRequest { Search = email });
        using var request = new HttpRequestMessage(HttpMethod.Post,
            AyakaleafService.BaseUrl + "/admin/users/search")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-Csrf-Token", csrf);

        HttpResponseMessage response;
        try
        {
            response = await Http.SendAsync(request).ConfigureAwait(false);
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
                throw _AccountDeleteRejected(email, "当前管理员账号没有 Ayakaleaf 站点管理权限。");
            if (!response.IsSuccessStatusCode)
                throw _ServiceNotReady(code);

            try
            {
                using var doc = JsonDocument.Parse(body);
                if (!doc.RootElement.TryGetProperty("users", out var users) ||
                    users.ValueKind != JsonValueKind.Array)
                    return null;

                // 优先返回未删除的匹配项；仅剩已删除（待清理）记录时返回其 id（删除端点会拒绝，但不至于误报“不存在”）
                string? fallback = null;
                foreach (var user in users.EnumerateArray())
                {
                    if (!user.TryGetProperty("email", out var emailElement) ||
                        emailElement.ValueKind != JsonValueKind.String ||
                        !string.Equals(emailElement.GetString(), email, StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (!user.TryGetProperty("id", out var idElement) ||
                        idElement.ValueKind != JsonValueKind.String)
                        continue;

                    var deleted = user.TryGetProperty("deleted", out var deletedElement) &&
                                  deletedElement.ValueKind == JsonValueKind.True;
                    if (!deleted)
                        return idElement.GetString();
                    fallback ??= idElement.GetString();
                }

                return fallback;
            }
            catch (JsonException)
            {
                throw _ServiceNotReady(null);
            }
        }
    }

    /// <summary>管理端删除用户：<c>POST /admin/user/:userId/delete</c>（JSON + X-Csrf-Token，响应 {deletedAt}）。</summary>
    private static async Task _AdminDeleteUserAsync(string csrf, string userId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"{AyakaleafService.BaseUrl}/admin/user/{Uri.EscapeDataString(userId)}/delete")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-Csrf-Token", csrf);

        HttpResponseMessage response;
        try
        {
            response = await Http.SendAsync(request).ConfigureAwait(false);
        }
        catch (Exception ex) when (_IsConnectionFailure(ex))
        {
            throw _ServiceUnavailable(ex);
        }

        using (response)
        {
            var code = (int)response.StatusCode;
            if (code == 200) return; // 成功：{deletedAt:…}

            var body = await _ReadBodyAsync(response).ConfigureAwait(false);
            if (code == 403)
                throw _AccountDeleteRejected(null, "当前管理员账号没有 Ayakaleaf 站点管理权限。");
            if (code == 422)
                throw _AccountDeleteRejected(null, _TruncateDetail(body));

            throw _ServiceNotReady(code);
        }
    }

    /// <summary>删除本机凭据文件（不存在时为空操作；删除失败按存储失败抛出）。</summary>
    private static void _TryDeleteAccountFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex) when (_IsStorageFailure(ex))
        {
            throw _StorageFailed(path, ex);
        }
    }

    /// <summary>获取跨进程账号互斥量（10 秒超时；被弃置的互斥量直接接管）。</summary>
    private static bool _AcquireAccountsMutex(Mutex mutex)
    {
        try
        {
            if (mutex.WaitOne(MutexTimeout)) return true;
        }
        catch (AbandonedMutexException)
        {
            return true; // 上一个持有者异常退出：所有权已转移给当前线程
        }

        throw _StorageFailed(AccountsDirectory, "同一启动器目录下另一个实例正在处理账号凭据");
    }

    /// <summary>释放跨进程账号互斥量（所有退出路径含异常都必须调用）。</summary>
    private static void _ReleaseAccountsMutex(Mutex mutex, bool acquired)
    {
        if (!acquired) return;
        try
        {
            mutex.ReleaseMutex();
        }
        catch (ApplicationException ex)
        {
            LogWrapper.Warn(ex, "[Ayakaleaf] 释放账号凭据互斥量失败");
        }
    }

    // ---------------- HTTP 辅助 ----------------

    /// <summary>取登录页 CSRF 令牌；连接失败映射为服务不可用，非成功状态码 / 未命中映射为服务未就绪。</summary>
    private static async Task<string> _GetCsrfTokenAsync()
    {
        HttpResponseMessage response;
        try
        {
            response = await Http.GetAsync(AyakaleafService.BaseUrl + "/login").ConfigureAwait(false);
        }
        catch (Exception ex) when (_IsConnectionFailure(ex))
        {
            throw _ServiceUnavailable(ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw _ServiceNotReady((int)response.StatusCode);

            // 正则沿用既有实现，保持对 Overleaf 登录页的兼容
            var body = await _ReadBodyAsync(response).ConfigureAwait(false);
            var match = Regex.Match(body, "ol-csrfToken\" content=\"([^\"]+)\"");
            if (!match.Success)
                throw _ServiceNotReady(null);

            return match.Groups[1].Value;
        }
    }

    /// <summary>读取响应体（读取失败按空响应体处理，不覆盖原始状态码语义）。</summary>
    private static async Task<string> _ReadBodyAsync(HttpResponseMessage response)
    {
        try
        {
            return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>判断本次响应是否下发了新的会话 Cookie（<c>Set-Cookie: overleaf.sid=…</c>）。</summary>
    private static bool _ResponseSetsSessionCookie(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var values)) return false;

        foreach (var headerValue in values)
        {
            // 同一个 Set-Cookie 头可能被合并成一行；按逗号拆分后逐段判断
            // （Expires 属性里的逗号所在段不会以 overleaf.sid= 开头）
            foreach (var segment in headerValue.Split(','))
            {
                if (segment.TrimStart().StartsWith("overleaf.sid=", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }

    /// <summary>从会话 Cookie 容器读取指定 Cookie 的值；不存在时返回 null。</summary>
    private static string? _ReadCookie(string name)
    {
        var cookies = Cookies.GetCookies(new Uri(AyakaleafService.BaseUrl));
        foreach (Cookie cookie in cookies)
            if (cookie.Name == name)
                return cookie.Value;
        return null;
    }

    /// <summary>判断异常是否属于连接层失败（连接被拒、超时、DNS / 端口不通）。</summary>
    private static bool _IsConnectionFailure(Exception ex) =>
        ex is HttpRequestException or TaskCanceledException or SocketException
        || ex.InnerException is SocketException;

    /// <summary>判断异常是否属于凭据文件 / 目录的存储失败。</summary>
    private static bool _IsStorageFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or NotSupportedException
            or System.Security.SecurityException or ArgumentException;

    // ---------------- 目录标识与凭据文件 ----------------

    /// <summary>
    /// 计算启动器目录标识：绝对路径规范化（去掉末尾分隔符）并转小写后，
    /// 取 UTF-8 字节的 SHA256 前 8 位小写十六进制。
    /// 同一目录多次运行恒定、不同目录互不相同，用于生成互不冲突的 Ayakaleaf 邮箱。
    /// </summary>
    /// <param name="baseDirectory">启动器目录（通常是 <see cref="AppContext.BaseDirectory"/>）。</param>
    private static string _ComputeDirectoryId(string baseDirectory)
    {
        string normalized;
        try
        {
            normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(baseDirectory));
        }
        catch
        {
            // 极端非法路径：退回原始字符串，仍能产生稳定标识
            normalized = baseDirectory;
        }

        // 大小写不敏感：Windows 路径大小写不影响目录身份
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized.ToLowerInvariant()));
        return Convert.ToHexString(hash)[..8].ToLowerInvariant();
    }

    /// <summary>
    /// 生成 Ayakaleaf 开户邮箱：<c>{小写用户名}.{目录标识}@ayaka.local</c>。
    /// 全局单实例服务下，不同启动器目录因此得到不同账号，不会互相撞「已存在」。
    /// </summary>
    /// <param name="localUserName">启动器本地用户名。</param>
    private static string _BuildAccountEmail(string localUserName) =>
        $"{_SanitizeEmailLocalPart(localUserName)}.{DirectoryId}@ayaka.local";

    /// <summary>
    /// 把用户名转换为合法的邮箱本地部分：转小写后仅保留 <c>[a-z0-9._-]</c>，
    /// 其余字符（含中文、空格、@ 等）替换为下划线；全空时退化为 <c>user</c>。
    /// </summary>
    /// <param name="localUserName">启动器本地用户名。</param>
    private static string _SanitizeEmailLocalPart(string localUserName)
    {
        var lower = localUserName.Trim().ToLowerInvariant();
        var sb = new StringBuilder(lower.Length);
        foreach (var c in lower)
        {
            var allowed = c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '_' or '-';
            sb.Append(allowed ? c : '_');
        }

        return sb.Length == 0 ? "user" : sb.ToString();
    }

    /// <summary>读取并校验凭据文件（JSON 结构、Email / Password 非空、DPAPI 解密成功）。</summary>
    /// <param name="path">凭据文件路径。</param>
    /// <param name="account">解析成功时的账号；失败为 null。</param>
    /// <returns>是否解析成功。</returns>
    private static bool _TryReadAccountFile(string path, out AyakaleafAccount? account)
    {
        account = null;
        try
        {
            var json = File.ReadAllText(path);
            var saved = JsonSerializer.Deserialize<AccountFile>(json);
            if (saved is null) return false;

            var email = saved.Email;
            var encryptedPassword = saved.Password;
            if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(encryptedPassword)) return false;

            var password = _Unprotect(encryptedPassword);
            if (string.IsNullOrEmpty(password)) return false;

            // 既有文件中的邮箱原样返回（不按新规则重算），保证老凭据继续可用
            account = new AyakaleafAccount(email, password);
            return true;
        }
        catch
        {
            // JSON 损坏、IO 失败、Base64 非法或 DPAPI 解密失败：统一按「不可用」处理
            return false;
        }
    }

    /// <summary>把无法解析的凭据文件改名为 .bak 保留（不删除），随后重新开户。</summary>
    private static void _QuarantineCorruptFile(string path)
    {
        var backup = $"{path}.corrupt-{DateTime.Now:yyyyMMddHHmmss}.bak";
        try
        {
            File.Move(path, backup, overwrite: true);
            LogWrapper.Warn($"[Ayakaleaf] 凭据文件无法解析，已改名为 {Path.GetFileName(backup)} 后重新开户");
        }
        catch (Exception ex) when (_IsStorageFailure(ex))
        {
            LogWrapper.Warn($"[Ayakaleaf] 凭据文件无法解析且无法改名（将直接覆盖）：{Path.GetFileName(path)}：{ex.Message}");
        }
    }

    /// <summary>以原子方式写入凭据文件（临时文件 + 替换），避免另一进程读到半写文件。</summary>
    private static void _WriteAccountFileAtomic(string path, AyakaleafAccount account)
    {
        var tempFile = path + ".tmp";
        try
        {
            // 仅保存 DPAPI（CurrentUser）保护后的 Base64 字符串，绝不写明文
            var json = JsonSerializer.Serialize(new AccountFile
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
            throw _StorageFailed(path, ex, account.Email);
        }
    }

    /// <summary>尽力删除临时文件（失败仅告警）。</summary>
    private static void _TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex)
        {
            LogWrapper.Warn($"[Ayakaleaf] 清理临时文件失败：{Path.GetFileName(path)}：{ex.Message}");
        }
    }

    /// <summary>判断注册响应体是否表明「邮箱已存在」。</summary>
    private static bool _ContainsAlreadyRegistered(string body) =>
        body.Contains("already", StringComparison.OrdinalIgnoreCase)
        || body.Contains("已被使用", StringComparison.OrdinalIgnoreCase)
        || body.Contains("已被注册", StringComparison.OrdinalIgnoreCase)
        || body.Contains("已经存在", StringComparison.OrdinalIgnoreCase);

    // ---------------- 账号与文案辅助 ----------------

    /// <summary>
    /// 从 Ayakaleaf 邮箱反推启动器本地用户名：
    /// 新格式 <c>{user}.{8 位目录标识}@ayaka.local</c> 去掉尾部目录标识，
    /// 旧格式 <c>{user}@ayaka.local</c> 整体作为用户名。
    /// </summary>
    private static string? _TryGetLocalUserNameFromEmail(string? email)
    {
        const string suffix = "@ayaka.local";
        if (string.IsNullOrEmpty(email) || !email.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            return null;

        var localPart = email[..^suffix.Length].Trim();

        // 目录标识固定为 8 位小写十六进制，且只出现在末尾
        var stamped = Regex.Match(localPart, @"^(?<name>.+)\.[0-9a-f]{8}$");
        if (stamped.Success) localPart = stamped.Groups["name"].Value;

        return localPart.Length == 0 ? null : localPart;
    }

    /// <summary>取用于用户提示的本地用户名（取不到时显示「未知」）。</summary>
    private static string _DisplayName(string? localUserName) =>
        string.IsNullOrWhiteSpace(localUserName) ? "未知" : localUserName;

    /// <summary>压缩换行并截断到指定长度（用于响应体 / 异常消息，最多 200 字符）。</summary>
    private static string _TruncateDetail(string? detail, int maxLength = 200)
    {
        if (string.IsNullOrWhiteSpace(detail)) return "服务端未返回详细原因";

        var singleLine = Regex.Replace(detail, @"\s+", " ").Trim();
        return singleLine.Length <= maxLength ? singleLine : singleLine[..maxLength];
    }

    /// <summary>生成 24 字符随机密码（沿用既有字符集，排除易混淆字符）。</summary>
    private static string _GeneratePassword()
    {
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghjkmnpqrstuvwxyz23456789";
        var bytes = RandomNumberGenerator.GetBytes(24);
        var sb = new StringBuilder(24);
        foreach (var b in bytes)
            sb.Append(chars[b % chars.Length]);
        return sb.ToString();
    }

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

    // ---------------- 异常工厂（文案模板见计划 2.2.2，不得自行更改） ----------------

    /// <summary>无法连接服务：未启动 / 端口不通 / 超时。</summary>
    private static AyakaleafAccountException _ServiceUnavailable(Exception inner) =>
        new(AyakaleafAccountErrorKind.ServiceUnavailable,
            $"无法连接 Ayakaleaf 服务（{AyakaleafService.BaseUrl}）：服务未启动或端口不通。" +
            "请先在 Ayakaleaf 页面点击“启动”，等待状态变为“运行中”后重试。",
            innerException: inner);

    /// <summary>服务已响应但未就绪（取不到 CSRF 或返回 5xx）；带状态码时在文案末尾附上 HTTP 状态码。</summary>
    private static AyakaleafAccountException _ServiceNotReady(int? statusCode)
    {
        var message =
            $"Ayakaleaf 服务已响应但尚未就绪（{AyakaleafService.BaseUrl}）：未能获取登录页的 CSRF 令牌。" +
            "请稍候几秒后重试；若持续失败请查看服务日志。";
        if (statusCode is not null)
            message += $"（HTTP {statusCode}）";

        return new AyakaleafAccountException(AyakaleafAccountErrorKind.ServiceNotReady, message);
    }

    /// <summary>服务端已存在同名邮箱，但本机没有对应凭据。</summary>
    private static AyakaleafAccountException _AccountConflict(string email)
    {
        var localUserName = _TryGetLocalUserNameFromEmail(email);
        return new AyakaleafAccountException(
            AyakaleafAccountErrorKind.AccountConflict,
            $"Ayakaleaf 账号 {email} 已在服务端存在，但本机没有它的登录凭据，无法自动登录。" +
            "请在该站点登录此账号后重置密码，或删除服务端该账号后重试。" +
            $"当前本地用户：{_DisplayName(localUserName)}。",
            email,
            localUserName);
    }

    /// <summary>注册被服务端拒绝（非「已存在」原因）。</summary>
    private static AyakaleafAccountException _RegistrationRejected(string email, int statusCode, string body) =>
        new(AyakaleafAccountErrorKind.RegistrationRejected,
            $"注册 Ayakaleaf 账号 {email} 失败（HTTP {statusCode}）：{_TruncateDetail(body)}。",
            email,
            _TryGetLocalUserNameFromEmail(email));

    /// <summary>本机凭据存在但登录失败（密码不符 / 账号被删除 / 本次未下发会话 Cookie）。</summary>
    private static AyakaleafAccountException _CredentialInvalid(string email, int statusCode)
    {
        var localUserName = _TryGetLocalUserNameFromEmail(email);
        return new AyakaleafAccountException(
            AyakaleafAccountErrorKind.CredentialInvalid,
            $"Ayakaleaf 账号 {email} 登录失败（HTTP {statusCode}）：本机保存的密码与服务端不一致，或该账号已被删除。" +
            $"请在 Ayakaleaf 站点重置密码，或到“账户中心”核对当前本地用户（{_DisplayName(localUserName)}）。",
            email,
            localUserName);
    }

    /// <summary>启动器尚未登录本地用户。</summary>
    private static AyakaleafAccountException _LocalUserMissing() =>
        new(AyakaleafAccountErrorKind.LocalUserMissing,
            "启动器尚未登录本地用户，无法继承 Ayakaleaf 账号。" +
            "请先在“账户中心”登录或创建一个本地用户，然后重试。");

    /// <summary>凭据读写失败（内部异常形式）。</summary>
    private static AyakaleafAccountException _StorageFailed(
        string path, Exception inner, string? email = null, string? localUserName = null) =>
        _StorageFailed(path, _TruncateDetail(inner.Message), inner, email, localUserName);

    /// <summary>凭据读写失败（原因文本形式）。</summary>
    private static AyakaleafAccountException _StorageFailed(
        string path, string reason, Exception? inner = null, string? email = null, string? localUserName = null) =>
        new(AyakaleafAccountErrorKind.CredentialStorageFailed,
            $"保存 Ayakaleaf 账号凭据失败（{path}）：{_TruncateDetail(reason)}。请检查该目录的读写权限后重试。",
            email,
            localUserName,
            inner);

    /// <summary>本机没有该本地用户的 Ayakaleaf 账号凭据，无法执行对应账号流程。</summary>
    private static AyakaleafAccountException _NoStoredCredential(string localUserName) =>
        new(AyakaleafAccountErrorKind.CredentialStorageFailed,
            $"本机没有本地用户 {localUserName} 的 Ayakaleaf 账号凭据。请先用该用户打开一次 Ayakaleaf 工作台完成自动开户，然后再重试。",
            localUserName: localUserName);

    /// <summary>重置密码被服务端拒绝（旧密码不符 / 密码策略不满足 / 限流）。</summary>
    private static AyakaleafAccountException _PasswordResetRejected(string? email, string reason) =>
        new(AyakaleafAccountErrorKind.PasswordResetRejected,
            $"重置 Ayakaleaf 账号密码失败：{_TruncateDetail(reason)}。",
            email,
            _TryGetLocalUserNameFromEmail(email));

    /// <summary>删除服务端账号被拒绝（缺少管理员凭据 / 无站点管理权限 / 端点拒绝）。</summary>
    private static AyakaleafAccountException _AccountDeleteRejected(string? email, string reason) =>
        new(AyakaleafAccountErrorKind.AccountDeleteRejected,
            $"删除 Ayakaleaf 服务端账号失败：{reason}",
            email,
            _TryGetLocalUserNameFromEmail(email));

    // ---------------- 内部数据类型 ----------------

    /// <summary>凭据文件结构（PascalCase 属性名即 JSON 字段名，兼容既有文件）。</summary>
    private sealed class AccountFile
    {
        /// <summary>Ayakaleaf 账号邮箱。</summary>
        public string? Email { get; set; }

        /// <summary>DPAPI（CurrentUser）保护后的 Base64 密码。</summary>
        public string? Password { get; set; }
    }

    /// <summary>管理端用户搜索请求体（POST /admin/users/search）。</summary>
    private sealed class AdminSearchRequest
    {
        /// <summary>搜索关键词（本服务内为完整邮箱）。</summary>
        public string? Search { get; set; }
    }
}