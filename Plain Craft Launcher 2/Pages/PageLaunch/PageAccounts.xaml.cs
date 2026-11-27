using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PCL.Core.Ayakaleaf;

namespace PCL;

/// <summary>
/// 账号管理页（US6 / FR-010）：列出站点账号，停用/启用、重置密码、切换管理员。
/// <para>
/// 所有操作走管理端 API（<see cref="AyakaleafAccountService"/> 的管理员会话），
/// 成功后按返回值就地刷新该行状态，不整页重载，避免丢失滚动位置。
/// </para>
/// </summary>
public partial class PageAccounts
{
    private List<ManagedAccount> _accounts = new();

    public PageAccounts()
    {
        InitializeComponent();
        Loaded += async (_, _) => await ReloadAsync();
        TextKeyword.KeyDown += async (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            await ReloadAsync();
        };
    }

    private async void BtnRefresh_Click(object sender, MouseButtonEventArgs e) => await ReloadAsync();

    private async Task ReloadAsync()
    {
        BtnRefresh.IsEnabled = false;
        LabStatus.Text = "正在读取账号列表…";
        try
        {
            var list = await AyakaleafAccountService.ListServerAccountsAsync(TextKeyword.Text);
            _accounts = list.Where(a => !a.Deleted).ToList();
            Render();
            LabStatus.Text = _accounts.Count == 0
                ? "没有匹配的账号。"
                : $"共 {_accounts.Count} 个账号（停用 {_accounts.Count(a => a.Suspended)}，未登录过 {_accounts.Count(a => a.Inactive)}）。";
        }
        catch (AyakaleafAccountException ex)
        {
            LabStatus.Text = ex.Message;
        }
        catch (Exception ex)
        {
            LabStatus.Text = "读取失败：" + ex.Message;
        }
        finally
        {
            BtnRefresh.IsEnabled = true;
        }
    }

    private void Render()
    {
        PanAccounts.Children.Clear();
        foreach (var account in _accounts) PanAccounts.Children.Add(BuildRow(account));
    }

    private FrameworkElement BuildRow(ManagedAccount account)
    {
        var grid = new Grid { Margin = new Thickness(0, 6, 0, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var title = new StackPanel { Orientation = Orientation.Horizontal };
        title.Children.Add(new TextBlock
        {
            Text = account.DisplayName,
            FontWeight = FontWeights.SemiBold,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
        });
        if (account.IsAdmin) title.Children.Add(BuildTag("管理员", 0.85));
        if (account.Suspended) title.Children.Add(BuildTag("已停用", 0.55));
        else if (account.Inactive) title.Children.Add(BuildTag("未登录过", 0.55));
        info.Children.Add(title);

        var sub = account.DisplayName == account.Email ? "" : account.Email;
        if (account.SignUpAt is { } at)
            sub = sub.Length == 0 ? $"注册于 {at:yyyy-MM-dd}" : $"{sub}　注册于 {at:yyyy-MM-dd}";
        if (sub.Length > 0)
        {
            info.Children.Add(new TextBlock
            {
                Text = sub,
                FontSize = 11,
                Opacity = 0.6,
                Margin = new Thickness(0, 2, 12, 0),
                TextWrapping = TextWrapping.Wrap,
            });
        }

        Grid.SetColumn(info, 0);
        grid.Children.Add(info);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

        var btnSuspend = new MyButton
        {
            Height = 30, Width = 76, Margin = new Thickness(0, 0, 8, 0),
            Text = account.Suspended ? "启用" : "停用",
            ColorType = account.Suspended ? MyButton.ColorState.Normal : MyButton.ColorState.Red,
        };
        btnSuspend.Click += async (_, _) => await ToggleSuspendAsync(account, btnSuspend);
        actions.Children.Add(btnSuspend);

        var btnReset = new MyButton { Height = 30, Width = 88, Margin = new Thickness(0, 0, 8, 0), Text = "重置密码" };
        btnReset.Click += async (_, _) => await ResetPasswordAsync(account, btnReset);
        actions.Children.Add(btnReset);

        var btnAdmin = new MyButton
        {
            Height = 30, Width = 96,
            Text = account.IsAdmin ? "取消管理员" : "设为管理员",
            ColorType = account.IsAdmin ? MyButton.ColorState.Normal : MyButton.ColorState.Highlight,
        };
        btnAdmin.Click += async (_, _) => await ToggleAdminAsync(account, btnAdmin);
        actions.Children.Add(btnAdmin);

        Grid.SetColumn(actions, 1);
        grid.Children.Add(actions);
        return grid;
    }

    private static TextBlock BuildTag(string text, double opacity) => new()
    {
        Text = text,
        FontSize = 10,
        Opacity = opacity,
        Margin = new Thickness(8, 0, 0, 0),
        VerticalAlignment = VerticalAlignment.Center,
    };

    private async Task ToggleSuspendAsync(ManagedAccount account, MyButton button)
    {
        var target = !account.Suspended;
        if (target && ModMain.MyMsgBox(
                $"停用后 {account.Email} 将立即无法登录，可随时启用。\n\n是否停用？",
                "停用账号", "停用", "取消", isWarn: true) != 1)
            return;

        button.IsEnabled = false;
        LabStatus.Text = target ? $"正在停用 {account.Email}…" : $"正在启用 {account.Email}…";
        try
        {
            await AyakaleafAccountService.SetServerAccountSuspendedAsync(account.Id, target);
            Replace(account with { Suspended = target });
            LabStatus.Text = target ? $"{account.Email} 已停用。" : $"{account.Email} 已启用。";
        }
        catch (AyakaleafAccountException ex)
        {
            LabStatus.Text = ex.Message;
        }
        catch (Exception ex)
        {
            LabStatus.Text = "操作失败：" + ex.Message;
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private async Task ResetPasswordAsync(ManagedAccount account, MyButton button)
    {
        if (ModMain.MyMsgBox(
                $"将为 {account.Email} 生成一个新的随机密码，原密码立即失效。\n\n是否继续？",
                "重置密码", "重置", "取消", isWarn: true) != 1)
            return;

        button.IsEnabled = false;
        LabStatus.Text = $"正在重置 {account.Email} 的密码…";
        try
        {
            var password = AyakaleafAccountService.GenerateTemporaryPassword();
            await AyakaleafAccountService.ResetServerAccountPasswordAsync(account.Id, password);
            LabStatus.Text = $"已重置 {account.Email} 的密码：{password}（仅显示这一次，请转告用户后让其自行修改）";
        }
        catch (AyakaleafAccountException ex)
        {
            LabStatus.Text = ex.Message;
        }
        catch (Exception ex)
        {
            LabStatus.Text = "重置失败：" + ex.Message;
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private async Task ToggleAdminAsync(ManagedAccount account, MyButton button)
    {
        var target = !account.IsAdmin;
        if (target && ModMain.MyMsgBox(
                $"将 {account.Email} 设为站点管理员，可访问管理后台并管理其他账号。\n\n是否继续？",
                "设为管理员", "设为管理员", "取消", isWarn: true) != 1)
            return;

        button.IsEnabled = false;
        LabStatus.Text = "正在更新权限…";
        try
        {
            await AyakaleafAccountService.SetServerAccountAdminAsync(account.Id, target);
            Replace(account with { IsAdmin = target });
            LabStatus.Text = target ? $"{account.Email} 已是管理员。" : $"{account.Email} 已取消管理员。";
        }
        catch (AyakaleafAccountException ex)
        {
            LabStatus.Text = ex.Message;
        }
        catch (Exception ex)
        {
            LabStatus.Text = "操作失败：" + ex.Message;
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    /// <summary>就地替换单行（保持顺序与滚动位置），避免整页重绘。</summary>
    private void Replace(ManagedAccount updated)
    {
        var index = _accounts.FindIndex(a => a.Id == updated.Id);
        if (index < 0) return;
        _accounts[index] = updated;
        if (index < PanAccounts.Children.Count)
            PanAccounts.Children[index] = BuildRow(updated);
    }
}
