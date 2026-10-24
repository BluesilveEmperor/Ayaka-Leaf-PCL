using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PCL.Core.Users;
using PCL.Core.Utils;

namespace PCL;

/// <summary>
/// 本地用户页面：注册、登录、切换用户，并显示当前登录用户及其独立数据目录。
/// 管理员可进入用户管理（重置密码、停用、删除）。整个界面以代码构建，避免与本地化资源耦合。
/// </summary>
public class PageLocalUser : Grid
{
    private readonly TextBlock _labelCurrent = new();
    private readonly TextBlock _labelDirectory = new();
    private readonly MyButton _btnRegister = new();
    private readonly MyButton _btnLogin = new();
    private readonly MyButton _btnSwitch = new();
    private readonly MyButton _btnChangePassword = new();
    private readonly MyButton _btnManage = new();
    private readonly MyButton _btnLogout = new();
    private readonly StackPanel _panelActions = new();
    private readonly StackPanel _panelLogged = new();

    public PageLocalUser()
    {
        BuildUi();
        Refresh();
    }

    private void BuildUi()
    {
        Margin = new Thickness(10, 5, 10, 5);
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(8) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(8) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(12) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var title = new TextBlock
        {
            Text = "本地用户",
            FontSize = 17,
            FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        SetRow(title, 0);
        Children.Add(title);

        _labelCurrent.HorizontalAlignment = HorizontalAlignment.Center;
        _labelCurrent.Margin = new Thickness(0, 6, 0, 0);
        SetRow(_labelCurrent, 2);
        Children.Add(_labelCurrent);

        _labelDirectory.HorizontalAlignment = HorizontalAlignment.Center;
        _labelDirectory.FontSize = 11;
        _labelDirectory.TextWrapping = TextWrapping.Wrap;
        _labelDirectory.Opacity = 0.6;
        _labelDirectory.Margin = new Thickness(0, 4, 0, 0);
        SetRow(_labelDirectory, 4);
        Children.Add(_labelDirectory);

        _panelActions.Orientation = Orientation.Horizontal;
        _panelActions.HorizontalAlignment = HorizontalAlignment.Center;
        SetRow(_panelActions, 6);
        Children.Add(_panelActions);

        _panelLogged.Orientation = Orientation.Horizontal;
        _panelLogged.HorizontalAlignment = HorizontalAlignment.Center;
        _panelLogged.Visibility = Visibility.Collapsed;
        SetRow(_panelLogged, 6);
        Children.Add(_panelLogged);

        Configure(_btnRegister, "注册", BtnRegister_Click);
        Configure(_btnLogin, "登录", BtnLogin_Click);
        Configure(_btnSwitch, "切换用户", BtnSwitch_Click);
        Configure(_btnChangePassword, "修改密码", BtnChangePassword_Click);
        Configure(_btnManage, "用户管理", BtnManage_Click);
        Configure(_btnLogout, "登出", BtnLogout_Click);
    }

    private static void Configure(MyButton button, string text, MyButton.ClickEventHandler handler)
    {
        button.Text = text;
        button.Height = 32;
        button.Width = 88;
        button.Margin = new Thickness(4, 0, 4, 0);
        button.ColorType = MyButton.ColorState.Normal;
        button.Click += handler;
    }

    /// <summary>
    /// 刷新页面显示。
    /// </summary>
    public void Refresh()
    {
        var session = LocalUserSession.Instance;
        var user = session.Current;

        if (user is null)
        {
            _labelCurrent.Text = "未登录本地用户";
            _labelDirectory.Text = "登录或注册后，每个用户将拥有独立的游戏数据目录。";
            _panelActions.Visibility = Visibility.Visible;
            _panelLogged.Visibility = Visibility.Collapsed;
            _panelActions.Children.Clear();
            _panelActions.Children.Add(_btnRegister);
            _panelActions.Children.Add(_btnLogin);
        }
        else
        {
            _labelCurrent.Text = $"已登录：{user.DisplayName}（{user.UserName}）";
            string directory;
            try
            {
                directory = LocalUserService.GetUserDataDirectory(user);
            }
            catch (Exception ex)
            {
                ModBase.Log(ex, "[LocalUser] 解析用户数据目录失败");
                directory = "(无法解析)";
            }
            _labelDirectory.Text = $"数据目录：{directory}";
            _panelActions.Visibility = Visibility.Collapsed;
            _panelLogged.Visibility = Visibility.Visible;
            _panelLogged.Children.Clear();
            _panelLogged.Children.Add(_btnSwitch);
            _panelLogged.Children.Add(_btnChangePassword);
            if (user.IsAdmin) _panelLogged.Children.Add(_btnManage);
            _panelLogged.Children.Add(_btnLogout);
        }
    }

    private void BtnRegister_Click(object sender, MouseButtonEventArgs e)
    {
        ModBase.RunInNewThread(() => LocalUserUi.ShowRegisterAsync(), "LocalUser Register");
    }

    private void BtnLogin_Click(object sender, MouseButtonEventArgs e)
    {
        ModBase.RunInNewThread(() => LocalUserUi.ShowLoginAsync(), "LocalUser Login");
    }

    private void BtnSwitch_Click(object sender, MouseButtonEventArgs e)
    {
        ModBase.RunInNewThread(() => LocalUserUi.ShowSwitchUserAsync(), "LocalUser Switch");
    }

    private void BtnChangePassword_Click(object sender, MouseButtonEventArgs e)
    {
        var user = LocalUserSession.Instance.Current;
        if (user is null) return;
        ModBase.RunInNewThread(() => LocalUserUi.ShowChangePasswordAsync(user), "LocalUser ChangePwd");
    }

    private void BtnManage_Click(object sender, MouseButtonEventArgs e)
    {
        ModBase.RunInNewThread(() => LocalUserUi.ShowUserManagementAsync(), "LocalUser Manage");
    }

    private void BtnLogout_Click(object sender, MouseButtonEventArgs e)
    {
        LocalUserUi.LogoutCurrentUser();
        Refresh();
    }
}
