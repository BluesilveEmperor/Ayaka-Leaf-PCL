using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PCL.Core.App;
using PCL.Core.Users;
using PCL.Core.Users.Api;
using PCL.Core.Utils;

namespace PCL;

/// <summary>
/// 本地用户的界面交互：注册、登录、切换与删除。
/// 用户数据存放在 SQLite，每个用户拥有独立的数据目录。
/// </summary>
public static class LocalUserUi
{
    private static LocalUserApiServer? _apiServer;

    /// <summary>
    /// 启动本地用户服务（HTTP API）。可被本机客户端访问，默认不随启动器启动。
    /// </summary>
    public static bool StartApiServer(int port = 8730)
    {
        try
        {
            if (_apiServer is { IsRunning: true }) return true;
            _apiServer ??= new LocalUserApiServer(new LocalUserService(), port);
            _apiServer.Start();
            ModBase.Log($"[LocalUser] 本地用户服务已启动：{_apiServer.Prefix}");
            return true;
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[LocalUser] 启动本地用户服务失败");
            return false;
        }
    }

    /// <summary>
    /// 停止本地用户服务。
    /// </summary>
    public static void StopApiServer()
    {
        try
        {
            _apiServer?.Dispose();
            _apiServer = null;
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[LocalUser] 停止本地用户服务失败");
        }
    }

    #region 启动初始化

    /// <summary>
    /// 启动时初始化：恢复会话，并将游戏目录校正到当前用户的独立目录。
    /// </summary>
    public static void InitializeOnStartup()
    {
        try
        {
            LocalUserSession.Instance.InitializeAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[LocalUser] 启动初始化本地用户失败");
            return;
        }

        var user = LocalUserSession.Instance.Current;
        if (user is not null)
        {
            ApplyUserGameFolder(user);
        }
        else
        {
            ReleaseUserGameFolder();
        }
    }

    #endregion

    #region 用户数据目录

    /// <summary>
    /// 将当前登录用户的独立目录记录为选中目录。随登录、注册、切换调用。
    /// AyakaLauncher 本地版：MC 文件夹列表已移除，仅更新 States 中的选中目录。
    /// </summary>
    public static void ApplyUserGameFolder(LocalUser user)
    {
        try
        {
            var directory = LocalUserService.GetUserDataDirectory(user);
            Directory.CreateDirectory(Path.Combine(directory, "versions"));
            States.Game.SelectedFolder = directory.Replace(ModBase.exePath, "$");
            ModBase.Log("[LocalUser] 已切换到用户游戏目录：" + directory);
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[LocalUser] 应用用户游戏目录失败");
        }
    }

    /// <summary>
    /// 若当前选中的游戏文件夹位于用户数据根目录内，则回退到普通文件夹。登出时调用。
    /// </summary>
    public static void ReleaseUserGameFolder()
    {
        try
        {
            var selected = States.Game.SelectedFolder?.ToString()?.Replace("$", ModBase.exePath) ?? "";
            if (string.IsNullOrEmpty(selected) ||
                !selected.StartsWith(LocalUserService.UsersRootDirectory, StringComparison.OrdinalIgnoreCase))
                return;

            // AyakaLauncher 本地版：MC 多文件夹列表已移除，登出后直接回退到启动器旁默认目录
            var target = ModBase.exePath + @".minecraft\";
            States.Game.SelectedFolder = target.Replace(ModBase.exePath, "$");
            ModBase.Log("[LocalUser] 已登出用户游戏目录，回退到：" + target);
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[LocalUser] 回退游戏目录失败");
        }
    }


    #endregion

    #region 登录门禁

    /// <summary>
    /// 启动前的本地用户校验。已登录直接放行；未登录则引导登录或注册。
    /// 返回 false 表示用户取消，启动将被中止。
    /// </summary>
    public static bool EnsureLoggedInInteractive()
    {
        try
        {
            LocalUserSession.Instance.InitializeAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            // 用户服务本身异常时不拦截启动，避免锁死 launcher
            ModBase.Log(ex, "[LocalUser] 校验本地用户时初始化失败，放行启动");
            return true;
        }

        var session = LocalUserSession.Instance;
        if (session.IsAuthenticated) return true;

        HintService.Hint("启动前请先登录本地用户。", HintType.Info);
        while (true)
        {
            // MyMsgBoxSelect 的参数含 WPF 控件构造，必须在 UI 线程求值
            var choice = ModBase.RunInUiWait<int?>(() => ModMain.MyMsgBoxSelect(
            [
                new MyListItem { Title = "登录已有本地用户", Type = MyListItem.CheckType.RadioBox, SvgIcon = "lucide/log-in", Checked = true },
                new MyListItem { Title = "注册新的本地用户", Type = MyListItem.CheckType.RadioBox, SvgIcon = "lucide/user-plus" }
            ], "启动前请先登录本地用户", "继续", "取消"));

            if (choice is null) return false;

            var task = choice.Value == 0 ? ShowLoginAsync() : ShowRegisterAsync();
            task.GetAwaiter().GetResult();
            if (session.IsAuthenticated) return true;
            HintService.Hint("尚未登录，无法启动。", HintType.Error);
        }
    }

    #endregion

    #region 用户中心入口

    /// <summary>
    /// 注册新用户。
    /// </summary>
    public static async Task ShowRegisterAsync()
    {
        var userName = ModMain.MyMsgBoxInput("注册本地用户", "请输入用户名（3-16 位字母、数字或下划线）", "",
            validateRules: [new PCL.Core.Utils.Validate.RegexValidator("^[A-Za-z0-9_]{3,16}$", "用户名需为 3-16 位字母、数字或下划线")],
            hintText: "用户名将同时作为该用户的游戏数据目录名与游戏内玩家名", button1: "下一步");
        if (string.IsNullOrWhiteSpace(userName)) return;

        var password = ModMain.MyMsgBoxInput("注册本地用户", "请输入密码（至少 6 位）", "",
            hintText: "密码使用 PBKDF2 加盐哈希存储，不会保存明文", button1: "下一步");
        if (string.IsNullOrEmpty(password)) return;

        if (password.Length < LocalUserService.MinPasswordLength)
        {
            HintService.Hint($"密码至少需要 {LocalUserService.MinPasswordLength} 位。", HintType.Error);
            return;
        }

        var confirm = ModMain.MyMsgBoxInput("注册本地用户", "请再次输入密码以确认", "",
            hintText: "两次输入的密码必须一致", button1: "注册");
        if (string.IsNullOrEmpty(confirm)) return;

        if (!string.Equals(password, confirm, StringComparison.Ordinal))
        {
            HintService.Hint("两次输入的密码不一致。", HintType.Error);
            return;
        }

        LocalUserResult result;
        LocalUser? user;
        try
        {
            (result, user) = await LocalUserSession.Instance
                .InitializeAndRegisterAsync(userName, password)
                .ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[LocalUser] 注册失败");
            HintService.Hint("注册时发生错误，请查看日志。", HintType.Error);
            return;
        }

        switch (result)
        {
            case LocalUserResult.Success when user is not null:
            {
                await LocalUserSession.Instance.LoginAsAsync(user.Id).ConfigureAwait(true);
                HintService.Hint($"注册成功：{user.DisplayName}", HintType.Success);
                ModBase.Log($"[LocalUser] 用户 {user.UserName} 已注册");
                ApplyUserGameFolder(user);
                break;
            }
            case LocalUserResult.UserNameTaken:
                HintService.Hint("该用户名已被注册。", HintType.Error);
                break;
            case LocalUserResult.InvalidUserName:
                HintService.Hint("用户名格式不合法。", HintType.Error);
                break;
            case LocalUserResult.InvalidPassword:
                HintService.Hint("密码不符合要求。", HintType.Error);
                break;
            default:
                HintService.Hint("注册失败，请查看日志。", HintType.Error);
                break;
        }
    }

    /// <summary>
    /// 登录已有本地用户。
    /// </summary>
    public static async Task ShowLoginAsync()
    {
        var userName = ModMain.MyMsgBoxInput("登录本地用户", "请输入用户名", "",
            hintText: "输入注册时使用的用户名", button1: "下一步");
        if (string.IsNullOrWhiteSpace(userName)) return;

        var password = ModMain.MyMsgBoxInput("登录本地用户", "请输入密码", "",
            hintText: "密码将以 PBKDF2 校验", button1: "登录");
        if (string.IsNullOrEmpty(password)) return;

        LocalUserResult result;
        LocalUser? user;
        try
        {
            (result, user) = await LocalUserSession.Instance.LoginAsync(userName, password).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[LocalUser] 登录失败");
            HintService.Hint("登录时发生错误，请查看日志。", HintType.Error);
            return;
        }

        switch (result)
        {
            case LocalUserResult.Success when user is not null:
            {
                HintService.Hint($"登录成功：{user.DisplayName}", HintType.Success);
                ModBase.Log($"[LocalUser] 用户 {user.UserName} 已登录");
                ApplyUserGameFolder(user);
                break;
            }
            case LocalUserResult.InvalidPassword:
                HintService.Hint("用户名或密码错误。", HintType.Error);
                break;
            case LocalUserResult.UserNotFound:
                HintService.Hint("该用户不存在。", HintType.Error);
                break;
            case LocalUserResult.UserDisabled:
                HintService.Hint("该账号已被停用。", HintType.Error);
                break;
            default:
                HintService.Hint("登录失败，请查看日志。", HintType.Error);
                break;
        }
    }

    /// <summary>
    /// 切换到其它已注册用户。
    /// </summary>
    public static async Task ShowSwitchUserAsync()
    {
        IReadOnlyList<LocalUser> users;
        try
        {
            users = await LocalUserSession.Instance.ListUsersAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[LocalUser] 读取用户列表失败");
            return;
        }

        var selectable = users.Where(u => !u.IsDisabled).ToList();
        if (selectable.Count == 0)
        {
            HintService.Hint("还没有可登录的本地用户。", HintType.Info);
            return;
        }

        var current = LocalUserSession.Instance.Current;
        var selections = new List<IMyRadio>();
        var userIds = new List<string>();
        foreach (var user in selectable)
        {
            userIds.Add(user.Id);
            selections.Add(new MyListItem
            {
                Title = $"{user.DisplayName}（{user.UserName}）{(user.IsAdmin ? " · 管理员" : "")}",
                Type = MyListItem.CheckType.RadioBox,
                SvgIcon = "lucide/user",
                Checked = current is not null && current.Id == user.Id
            });
        }

        var selected = ModMain.MyMsgBoxSelect(selections, "选择要切换到的用户", "切换", "取消");
        if (selected is null) return;

        var targetId = userIds[selected.Value];
        if (string.IsNullOrEmpty(targetId)) return;

        var result = await LocalUserSession.Instance.LoginAsAsync(targetId).ConfigureAwait(true);
        if (result == LocalUserResult.Success)
        {
            var target = selectable[selected.Value];
            HintService.Hint($"已切换到用户：{target.DisplayName}", HintType.Success);
            ApplyUserGameFolder(target);
        }
        else
        {
            HintService.Hint("切换失败，该用户可能已被停用或删除。", HintType.Error);
        }
    }

    /// <summary>
    /// 登出当前用户：清会话、必要时回退游戏目录。
    /// </summary>
    public static void LogoutCurrentUser()
    {
        LocalUserSession.Instance.Logout();
        ReleaseUserGameFolder();
        HintService.Hint("已登出本地用户。", HintType.Success);
    }

    /// <summary>
    /// 修改当前用户密码。
    /// </summary>
    public static async Task ShowChangePasswordAsync(LocalUser user)
    {
        var oldPassword = ModMain.MyMsgBoxInput("修改密码", $"请输入 {user.UserName} 的当前密码", "",
            hintText: "用于确认身份", button1: "下一步");
        if (string.IsNullOrEmpty(oldPassword)) return;

        var newPassword = ModMain.MyMsgBoxInput("修改密码", "请输入新密码（至少 6 位）", "",
            hintText: "新密码将重新加盐哈希", button1: "下一步");
        if (string.IsNullOrEmpty(newPassword)) return;

        var confirm = ModMain.MyMsgBoxInput("修改密码", "请再次输入新密码", "", button1: "确认修改");
        if (string.IsNullOrEmpty(confirm)) return;

        if (!string.Equals(newPassword, confirm, StringComparison.Ordinal))
        {
            HintService.Hint("两次输入的新密码不一致。", HintType.Error);
            return;
        }

        var result = await LocalUserSession.Instance
            .ChangePasswordAsync(user.Id, oldPassword, newPassword)
            .ConfigureAwait(true);

        if (result == LocalUserResult.Success)
        {
            HintService.Hint("密码已修改。", HintType.Success);
        }
        else if (result == LocalUserResult.InvalidPassword)
        {
            HintService.Hint("当前密码不正确，或新密码不符合要求。", HintType.Error);
        }
        else
        {
            HintService.Hint("修改失败，请查看日志。", HintType.Error);
        }
    }

    #endregion

    #region 管理员用户管理

    /// <summary>
    /// 管理员用户管理：列出全部用户，执行重置密码、停用/启用、删除。
    /// </summary>
    public static async Task ShowUserManagementAsync()
    {
        var session = LocalUserSession.Instance;
        var me = session.Current;
        if (me is null || !me.IsAdmin)
        {
            HintService.Hint("只有管理员可以管理用户。", HintType.Error);
            return;
        }

        while (true)
        {
            IReadOnlyList<LocalUser> users;
            try
            {
                users = await session.ListUsersAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                ModBase.Log(ex, "[LocalUser] 读取用户列表失败");
                return;
            }

            if (users.Count == 0)
            {
                HintService.Hint("还没有任何本地用户。", HintType.Info);
                return;
            }

            var selections = new List<IMyRadio>();
            for (var i = 0; i < users.Count; i++)
            {
                var user = users[i];
                selections.Add(new MyListItem
                {
                    Title = $"{user.DisplayName}（{user.UserName}）" +
                            $"{(user.IsAdmin ? " · 管理员" : "")}{(user.IsDisabled ? " · 已停用" : "")}",
                    Type = MyListItem.CheckType.RadioBox,
                    SvgIcon = "lucide/user",
                    Checked = i == 0
                });
            }

            var picked = ModMain.MyMsgBoxSelect(selections, "用户管理：选择要操作的用户", "操作", "关闭");
            if (picked is null) return;
            var target = users[picked.Value];

            var acted = await ShowUserActionAsync(session, me, target).ConfigureAwait(true);
            if (!acted) return; // 用户点了“关闭”
        }
    }

    /// <summary>
    /// 对单个用户执行管理动作。返回 false 表示退出管理界面，true 表示返回列表。
    /// </summary>
    private static async Task<bool> ShowUserActionAsync(LocalUserSession session, LocalUser me, LocalUser target)
    {
        var isSelf = string.Equals(target.Id, me.Id, StringComparison.OrdinalIgnoreCase);

        var actions = new List<IMyRadio>
        {
            new MyListItem { Title = "重置密码", Type = MyListItem.CheckType.RadioBox, SvgIcon = "lucide/key-round", Checked = true }
        };
        if (!isSelf)
        {
            actions.Add(new MyListItem
            {
                Title = target.IsDisabled ? "启用该用户" : "停用该用户",
                Type = MyListItem.CheckType.RadioBox, SvgIcon = "lucide/user-x"
            });
            actions.Add(new MyListItem
                { Title = "删除该用户（含数据目录）", Type = MyListItem.CheckType.RadioBox, SvgIcon = "lucide/trash-2" });
        }

        var act = ModMain.MyMsgBoxSelect(actions, $"用户管理：{target.UserName}{(isSelf ? "（当前登录，仅可改密）" : "")}", "执行", "返回");
        if (act is null) return true;

        switch (act.Value)
        {
            case 0: // 重置密码
            {
                var newPwd = ModMain.MyMsgBoxInput("重置密码", $"为 {target.UserName} 设置新密码（至少 6 位）", "",
                    hintText: "无需提供旧密码（管理员权限）", button1: "重置");
                if (string.IsNullOrEmpty(newPwd)) return true;
                if (newPwd.Length < LocalUserService.MinPasswordLength)
                {
                    HintService.Hint($"密码至少需要 {LocalUserService.MinPasswordLength} 位。", HintType.Error);
                    return true;
                }
                var result = await session.ResetPasswordAsync(target.Id, newPwd).ConfigureAwait(true);
                HintService.Hint(result == LocalUserResult.Success ? "密码已重置。" : "重置失败，请查看日志。",
                    result == LocalUserResult.Success ? HintType.Success : HintType.Error);
                return true;
            }
            case 1 when !isSelf: // 停用/启用
            {
                if (!target.IsDisabled && target.IsAdmin && await _CountEnabledAdminsAsync(session) <= 1)
                {
                    HintService.Hint("不能停用最后一个可用的管理员。", HintType.Error);
                    return true;
                }
                var result = await session.SetDisabledAsync(target.Id, !target.IsDisabled).ConfigureAwait(true);
                if (result == LocalUserResult.Success)
                    HintService.Hint(target.IsDisabled ? "已启用。" : "已停用。", HintType.Success);
                else
                    HintService.Hint("操作失败，请查看日志。", HintType.Error);
                return true;
            }
            case 2 when !isSelf: // 删除
            {
                var confirm = ModMain.MyMsgBox(
                    $"将删除用户 {target.UserName} 及其全部游戏数据（存档、版本、配置），且不可恢复！确定继续？",
                    "删除本地用户", "删除", "取消", isWarn: true, forceWait: true);
                if (confirm != 1) return true;
                if (target.IsAdmin && await _CountEnabledAdminsAsync(session) <= 1)
                {
                    HintService.Hint("不能删除最后一个可用的管理员。", HintType.Error);
                    return true;
                }
                var result = await session.DeleteAsync(target.Id, true).ConfigureAwait(true);
                HintService.Hint(result == LocalUserResult.Success ? "用户已删除。" : "删除失败，请查看日志。",
                    result == LocalUserResult.Success ? HintType.Success : HintType.Error);
                return true;
            }
            default:
                return true;
        }
    }

    private static async Task<int> _CountEnabledAdminsAsync(LocalUserSession session)
    {
        try
        {
            var users = await session.ListUsersAsync().ConfigureAwait(true);
            return users.Count(u => u.IsAdmin && !u.IsDisabled);
        }
        catch (Exception)
        {
            return int.MaxValue; // 查询失败时按“不止一个”处理，避免误锁
        }
    }

    #endregion
}
