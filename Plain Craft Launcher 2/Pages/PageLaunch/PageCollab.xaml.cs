using System;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using PCL.Core.Ayakaleaf.Collab;
using PCL.Core.UI;

namespace PCL;

/// <summary>
/// 远程协作页：房主开放本机 Ayakaleaf 服务、队友经邀请码接入。
/// 页面仅做状态展示与按钮调度，全部业务在 <see cref="CollabSession"/> 中。
/// </summary>
public partial class PageCollab
{
    private readonly CollabSession _session;
    private bool _subscribed;

    public PageCollab()
    {
        InitializeComponent();
        _session = CollabService.GetOrCreateSession();
        Loaded += PageCollab_Loaded;
        Unloaded += PageCollab_Unloaded;
        Refresh();
    }

    private void PageCollab_Loaded(object sender, RoutedEventArgs e)
    {
        // 切页返回时重新订阅状态变化（Unloaded 时已退订）
        if (!_subscribed)
        {
            _session.Changed += OnSessionChanged;
            _subscribed = true;
        }
        Refresh();
    }

    private void PageCollab_Unloaded(object sender, RoutedEventArgs e)
    {
        if (!_subscribed) return;
        _subscribed = false;
        _session.Changed -= OnSessionChanged;
    }

    private void OnSessionChanged()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(Refresh);
            return;
        }
        Refresh();
    }

    private void Refresh()
    {
        var stage = _session.Stage;
        var role = _session.Role;
        bool idle = stage is CollabStage.Idle or CollabStage.Failed;
        bool active = stage is CollabStage.Working or CollabStage.Ready;

        // 状态文案
        LabStatus.Text = stage switch
        {
            CollabStage.Idle => "未开启协作。",
            CollabStage.Working => _session.StatusText,
            CollabStage.Ready => role == CollabRole.Host
                ? "协作进行中：本机服务已对队友开放，等待队友加入。"
                : "已加入协作：可通过下方地址访问房主的服务。",
            CollabStage.Failed => "协作出错：" + _session.StatusText,
            _ => "未知状态",
        };

        // 公共节点
        if (stage == CollabStage.Ready && _session.ConnectedNodes >= 0)
        {
            LabNodes.Text = $"已连接 {_session.ConnectedNodes} 个公共中转节点";
            LabNodes.Visibility = Visibility.Visible;
        }
        else
        {
            LabNodes.Visibility = Visibility.Collapsed;
        }

        // 成员列表
        PanMembers.Children.Clear();
        if (stage == CollabStage.Ready)
        {
            LabMembersTitle.Visibility = Visibility.Visible;
            if (_session.Members.Count == 0)
            {
                PanMembers.Children.Add(new TextBlock
                {
                    Text = role == CollabRole.Host ? "暂无队友加入，把邀请码发给队友吧。" : "未发现其他成员。",
                    Opacity = 0.6,
                    FontSize = 12.5,
                    TextWrapping = TextWrapping.Wrap,
                });
            }
            else
            {
                foreach (var member in _session.Members)
                {
                    string link = member.IsDirect ? "直连" : "中继";
                    string latency = member.LatencyMs is double ms ? $"{ms:0}ms" : "—";
                    PanMembers.Children.Add(new TextBlock
                    {
                        Text = $"{member.Hostname}（{member.IPv4}）· {link} · {latency}",
                        FontSize = 12.5,
                        Opacity = 0.8,
                        Margin = new Thickness(0, 2, 0, 2),
                        TextWrapping = TextWrapping.Wrap,
                    });
                }
            }
        }
        else
        {
            LabMembersTitle.Visibility = Visibility.Collapsed;
        }

        // 按钮
        BtnStartHost.IsEnabled = idle;
        BtnJoin.IsEnabled = idle && !string.IsNullOrWhiteSpace(TextInvite.Text);
        BtnStop.Visibility = active ? Visibility.Visible : Visibility.Collapsed;

        // 房主：邀请码
        bool hostReady = role == CollabRole.Host && stage == CollabStage.Ready && _session.InviteCode is not null;
        LabInviteCode.Visibility = hostReady ? Visibility.Visible : Visibility.Collapsed;
        LabInviteCode.Text = _session.InviteCode ?? "";
        BtnCopyCode.Visibility = hostReady ? Visibility.Visible : Visibility.Collapsed;

        // 队友：转发地址与工作台
        PanForwards.Children.Clear();
        bool guestReady = role == CollabRole.Guest && stage == CollabStage.Ready;
        if (guestReady)
        {
            foreach (var forward in _session.Forwards)
            {
                var language = _session.GuestLanguages.FirstOrDefault(l => l.LocalPort == forward.LocalPort);
                string text = language is null
                    ? $"http://localhost:{forward.LocalPort}（房主端口 {forward.RemotePort}）"
                    : $"{language.Name}：http://localhost:{forward.LocalPort}";
                PanForwards.Children.Add(new TextBlock
                {
                    Text = text,
                    FontSize = 12.5,
                    Opacity = 0.8,
                    Margin = new Thickness(0, 2, 0, 2),
                    TextWrapping = TextWrapping.Wrap,
                });
            }
        }
        BtnOpenWorkbench.Visibility =
            guestReady && _session.GuestLanguages.Count > 0 && _session.MainLocalPort is not null
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    private void BtnStartHost_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _ = _session.StartHostAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            HintService.Hint(ex.Message, HintType.Error);
        }
        Refresh();
    }

    private void BtnJoin_Click(object sender, RoutedEventArgs e)
    {
        var code = TextInvite.Text;
        if (string.IsNullOrWhiteSpace(code))
        {
            HintService.Hint("请先输入房主分享的邀请码", HintType.Info);
            return;
        }
        try
        {
            _ = _session.JoinAsync(code, CancellationToken.None);
        }
        catch (Exception ex)
        {
            HintService.Hint(ex.Message, HintType.Error);
        }
        Refresh();
    }

    private void BtnStop_Click(object sender, RoutedEventArgs e)
    {
        _ = _session.StopAsync();
        Refresh();
    }

    private void BtnCopyCode_Click(object sender, RoutedEventArgs e)
    {
        var code = _session.InviteCode;
        if (string.IsNullOrEmpty(code))
        {
            BtnCopyCode.Visibility = Visibility.Collapsed;
            return;
        }
        try
        {
            Clipboard.SetDataObject(code, true);
            HintService.Hint("邀请码已复制，发给队友即可邀请加入", HintType.Success);
        }
        catch (Exception ex)
        {
            HintService.Hint("复制失败：" + ex.Message, HintType.Error);
        }
    }

    private void BtnOpenWorkbench_Click(object sender, RoutedEventArgs e)
    {
        if (_session.MainLocalPort is null || _session.GuestLanguages.Count == 0)
        {
            BtnOpenWorkbench.Visibility = Visibility.Collapsed;
            return;
        }
        var window = new WorkbenchWindow(_session.GuestLanguages);
        window.Show();
    }
}
