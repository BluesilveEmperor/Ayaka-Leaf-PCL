using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FluentValidation;
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
            var choice = _UiSelect(() => new List<IMyRadio>
            {
                new MyListItem
                {
                    Title = "登录已有本地用户", Type = MyListItem.CheckType.RadioBox, SvgIcon = "lucide/log-in",
                    Checked = true
                },
                new MyListItem
                {
                    Title = "注册新的本地用户", Type = MyListItem.CheckType.RadioBox, SvgIcon = "lucide/user-plus"
                }
            }, "启动前请先登录本地用户", "继续", "取消");

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
    /// 账户中心：切换用户 / 注册新用户 / 管理员用户管理 / 登出。
    /// 所有异常在本方法内捕获，不向调用方抛出。
    /// </summary>
    public static async Task ShowAccountCenterAsync()
    {
        try
        {
            var current = LocalUserSession.Instance.Current;

            // 条目与动作按同一顺序追加，避免按索引分派时因条件条目而错位
            var actions = new List<Func<Task>>();
            var selections = _UiSelect(() =>
            {
                var items = new List<IMyRadio>
                {
                    new MyListItem
                    {
                        Title = "切换/登录用户", Type = MyListItem.CheckType.RadioBox, SvgIcon = "lucide/log-in",
                        Checked = true
                    },
                    new MyListItem
                    {
                        Title = "注册新用户", Type = MyListItem.CheckType.RadioBox, SvgIcon = "lucide/user-plus"
                    }
                };
                actions.Add(ShowSwitchUserAsync);
                actions.Add(ShowRegisterAsync);
                if (current is { IsAdmin: true })
                {
                    items.Add(new MyListItem
                    {
                        Title = "用户管理", Type = MyListItem.CheckType.RadioBox, SvgIcon = "lucide/users-round"
                    });
                    actions.Add(ShowUserManagementAsync);
                }

                if (current is not null)
                {
                    items.Add(new MyListItem
                        { Title = "登出当前用户", Type = MyListItem.CheckType.RadioBox, SvgIcon = "lucide/log-out" });
                    actions.Add(() =>
                    {
                        LogoutCurrentUser();
                        return Task.CompletedTask;
                    });
                }

                return items;
            }, "账户中心", "继续", "取消");

            if (selections is null) return;
            if (selections.Value < 0 || selections.Value >= actions.Count) return;
            await actions[selections.Value]().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[LocalUser] 账户中心操作失败");
            HintService.Hint("操作失败，请查看日志。", HintType.Error);
        }
    }

    /// <summary>
    /// 注册新用户。
    /// </summary>
    public static async Task ShowRegisterAsync()
    {
        var userName = _UiInput("注册本地用户", "请输入用户名（3-16 位字母、数字或下划线）",
            "用户名将同时作为该用户的游戏数据目录名与游戏内玩家名",
            validateRules:
            [
                new PCL.Core.Utils.Validate.RegexValidator("^[A-Za-z0-9_]{3,16}$", "用户名需为 3-16 位字母、数字或下划线")
            ]);
        if (string.IsNullOrWhiteSpace(userName)) return;

        var password = _UiInput("注册本地用户", "请输入密码（至少 6 位）", "请输入至少 6 位密码", isPassword: true);
        if (string.IsNullOrEmpty(password)) return;

        if (password.Length < LocalUserService.MinPasswordLength)
        {
            HintService.Hint($"密码至少需要 {LocalUserService.MinPasswordLength} 位。", HintType.Error);
            return;
        }

        var confirm = _UiInput("注册本地用户", "请再次输入密码以确认", "请再次输入相同的密码", isPassword: true);
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
        var userName = _UiInput("登录本地用户", "请输入用户名", "输入注册时使用的用户名");
        if (string.IsNullOrWhiteSpace(userName)) return;

        var password = _UiInput("登录本地用户", "请输入密码", "请输入该用户的登录密码", isPassword: true);
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
        var selected = _UiSelect(() =>
        {
            var items = new List<IMyRadio>();
            foreach (var user in selectable)
                items.Add(new MyListItem
                {
                    Title = $"{user.DisplayName}（{user.UserName}）{(user.IsAdmin ? " · 管理员" : "")}",
                    Type = MyListItem.CheckType.RadioBox,
                    SvgIcon = "lucide/user",
                    Checked = current is not null && current.Id == user.Id
                });
            return items;
        }, "选择要切换到的用户", "切换", "取消");
        if (selected is null) return;
        if (selected.Value < 0 || selected.Value >= selectable.Count) return;

        var target = selectable[selected.Value];
        if (string.IsNullOrEmpty(target.Id)) return;

        var result = await LocalUserSession.Instance.LoginAsAsync(target.Id).ConfigureAwait(true);
        if (result == LocalUserResult.Success)
        {
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
        var oldPassword = _UiInput("修改密码", $"请输入 {user.UserName} 的当前密码", "用于确认身份", isPassword: true);
        if (string.IsNullOrEmpty(oldPassword)) return;

        var newPassword = _UiInput("修改密码", "请输入新密码（至少 6 位）", "请输入至少 6 位新密码", isPassword: true);
        if (string.IsNullOrEmpty(newPassword)) return;

        var confirm = _UiInput("修改密码", "请再次输入新密码", "请再次输入相同的新密码", isPassword: true);
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

            var picked = _UiSelect(() =>
            {
                var items = new List<IMyRadio>();
                for (var i = 0; i < users.Count; i++)
                {
                    var user = users[i];
                    items.Add(new MyListItem
                    {
                        Title = $"{user.DisplayName}（{user.UserName}）" +
                                $"{(user.IsAdmin ? " · 管理员" : "")}{(user.IsDisabled ? " · 已停用" : "")}",
                        Type = MyListItem.CheckType.RadioBox,
                        SvgIcon = "lucide/user",
                        Checked = i == 0
                    });
                }

                return items;
            }, "用户管理：选择要操作的用户", "操作", "关闭");
            if (picked is null) return;
            if (picked.Value < 0 || picked.Value >= users.Count) return;
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

        var act = _UiSelect(() =>
        {
            var actions = new List<IMyRadio>
            {
                new MyListItem
                {
                    Title = "重置密码", Type = MyListItem.CheckType.RadioBox, SvgIcon = "lucide/key-round",
                    Checked = true
                }
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

            return actions;
        }, $"用户管理：{target.UserName}{(isSelf ? "（当前登录，仅可改密）" : "")}", "执行", "返回");
        if (act is null) return true;

        switch (act.Value)
        {
            case 0: // 重置密码
            {
                var newPwd = _UiInput("重置密码", $"为 {target.UserName} 设置新密码（至少 6 位）",
                    "无需提供旧密码（管理员权限）", isPassword: true);
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
                var confirm = _UiConfirm(
                    $"将删除用户 {target.UserName} 及其全部游戏数据（存档、版本、配置），且不可恢复！确定继续？",
                    "删除本地用户", "删除", "取消", true);
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

    #region 弹窗 UI 线程调度

    // MyMsgBox* 内部使用 Dispatcher.PushFrame 嵌套消息泵，只能在 UI 线程调用；
    // 且列表项等 WPF 控件也必须在同一线程构造。
    // 因此所有用户交互统一经由以下辅助方法，禁止在调用线程直接创建弹窗内容。

    /// <summary>
    /// 在 UI 线程显示输入弹窗并返回输入内容。取消、无法显示或发生异常时返回 <c>null</c>。
    /// </summary>
    /// <param name="title">弹窗标题。</param>
    /// <param name="text">弹窗说明文本。</param>
    /// <param name="hintText">输入框下方的提示文本。</param>
    /// <param name="isPassword">是否为密码输入模式（遮蔽显示，依赖 C-PASSWORD-INPUT@1）。</param>
    /// <param name="validateRules">输入校验规则。</param>
    private static string? _UiInput(string title, string text, string hintText, bool isPassword = false,
        Collection<IValidator<string>>? validateRules = null)
    {
        if (Application.Current is null) return null; // 没有 UI 环境时按“取消”处理
        try
        {
            return ModBase.RunInUiWait<string>(() => ModMain.MyMsgBoxInput(title, text, "", validateRules, hintText,
                isPassword: isPassword));
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[LocalUser] 显示输入弹窗失败");
            return null;
        }
    }

    /// <summary>
    /// 在 UI 线程显示选择弹窗，返回选中项索引。取消、无法显示或发生异常时返回 <c>null</c>。
    /// </summary>
    private static int? _UiSelect(List<IMyRadio> selections, string title, string button1, string button2)
    {
        return _UiSelect(() => selections, title, button1, button2);
    }

    /// <summary>
    /// 在 UI 线程显示选择弹窗，返回选中项索引。取消、无法显示或发生异常时返回 <c>null</c>。
    /// 列表项由 <paramref name="selectionsFactory" /> 在 UI 线程内构造，
    /// 避免在后台线程提前 new 出 WPF 控件。
    /// </summary>
    private static int? _UiSelect(Func<List<IMyRadio>> selectionsFactory, string title, string button1,
        string button2)
    {
        if (Application.Current is null) return null; // 没有 UI 环境时按“取消”处理
        try
        {
            return ModBase.RunInUiWait<int?>(() =>
                ModMain.MyMsgBoxSelect(selectionsFactory(), title, button1, button2));
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[LocalUser] 显示选择弹窗失败");
            return null;
        }
    }

    /// <summary>
    /// 在 UI 线程显示确认弹窗，返回点击的按钮编号（从 1 开始）。
    /// 取消、无法显示或发生异常时返回 <c>0</c>，调用方应按“取消”处理。
    /// </summary>
    private static int _UiConfirm(string message, string title, string button1, string button2, bool isWarn)
    {
        if (Application.Current is null) return 0; // 没有 UI 环境时按“取消”处理
        try
        {
            return ModBase.RunInUiWait<int>(() =>
                ModMain.MyMsgBox(message, title, button1, button2, isWarn: isWarn, forceWait: true));
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[LocalUser] 显示确认弹窗失败");
            return 0;
        }
    }

    #endregion
}