using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PCL.Core.App;
using PCL.Core.Ayakaleaf;
using PCL.Core.Minecraft.Profile.LocalUsers;

namespace PCL;

public partial class PageServiceLeft
{
    private readonly DispatcherTimer _timer;
    private int _busy;
    private int _openBusy;
    private bool _isLoaded;

    public PageServiceLeft()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (!_isLoaded)
            {
                _isLoaded = true;
                _timer.Start();
            }
            RefreshStatusAsync();
        };
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _timer.Tick += (_, _) => RefreshStatusAsync();
    }

    private void SetStatus(AyakaleafStatus status)
    {
        DotStatus.Fill = status switch
        {
            AyakaleafStatus.Running => System.Windows.Media.Brushes.ForestGreen,
            AyakaleafStatus.Starting or AyakaleafStatus.Stopping => System.Windows.Media.Brushes.Goldenrod,
            AyakaleafStatus.Error => System.Windows.Media.Brushes.Firebrick,
            AyakaleafStatus.NotDeployed => System.Windows.Media.Brushes.DarkOrange,
            _ => System.Windows.Media.Brushes.Gray,
        };
        LabStatus.Text = status switch
        {
            AyakaleafStatus.Running => "运行中",
            AyakaleafStatus.Starting => "启动中",
            AyakaleafStatus.Stopping => "停止中",
            AyakaleafStatus.Error => "异常",
            AyakaleafStatus.NotDeployed => "未部署",
            _ => "已停止",
        };
        LabStatusInfo.Text = status switch
        {
            AyakaleafStatus.Running => "Ayakaleaf 服务栈运行中，可从右侧打开站点使用。",
            AyakaleafStatus.Starting => "正在启动服务栈，请稍候…",
            AyakaleafStatus.Stopping => "正在停止服务栈…",
            AyakaleafStatus.Error => "服务异常，请查看服务日志。",
            AyakaleafStatus.NotDeployed => "首次使用请先在右侧完成内嵌运行时部署。",
            _ => "服务栈未运行。",
        };
        LabSiteUrl.Text = status is AyakaleafStatus.Running ? AyakaleafService.BaseUrl : "";
        BtnStart.IsEnabled = status is AyakaleafStatus.Stopped or AyakaleafStatus.Error or AyakaleafStatus.NotDeployed;
        BtnStop.IsEnabled = status is AyakaleafStatus.Running;
        BtnOpen.IsEnabled = status is AyakaleafStatus.Running;
        if (status is not AyakaleafStatus.Starting && status is not AyakaleafStatus.Stopping)
            LabProgress.Visibility = Visibility.Collapsed;
    }

    private async void RefreshStatusAsync()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            if (!Config.Ayakaleaf.Deployed)
            {
                SetStatus(AyakaleafStatus.NotDeployed);
                return;
            }
            var alive = await AyakaleafService.ProbeAsync();
            SetStatus(alive ? AyakaleafStatus.Running : AyakaleafStatus.Stopped);
        }
        catch
        {
            SetStatus(AyakaleafStatus.Error);
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    private void RunStackOperation(bool start)
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        SetStatus(start ? AyakaleafStatus.Starting : AyakaleafStatus.Stopping);
        BtnStart.IsEnabled = false;
        BtnStop.IsEnabled = false;
        BtnOpen.IsEnabled = false;
        LabProgress.Visibility = Visibility.Visible;
        LabProgress.Text = start ? "正在启动…" : "正在停止…";
        ModBase.RunInNewThread(() =>
        {
            try
            {
                if (start)
                    AyakaleafService.StartAsync(msg => ModBase.RunInUi(() => LabProgress.Text = msg)).GetAwaiter().GetResult();
                else
                    AyakaleafService.StopAsync(msg => ModBase.RunInUi(() => LabProgress.Text = msg)).GetAwaiter().GetResult();
                ModBase.RunInUi(() =>
                    HintService.Hint(start ? "Ayakaleaf 已就绪：" + AyakaleafService.BaseUrl : "服务栈已停止",
                        start ? HintType.Success : HintType.Info));
            }
            catch (Exception ex)
            {
                ModBase.Log(ex, "[Ayakaleaf] 服务栈操作失败");
                ModBase.RunInUi(() =>
                {
                    HintService.Hint(ex.Message, HintType.Error);
                    LabProgress.Text = ex.Message;
                });
            }
            finally
            {
                Interlocked.Exchange(ref _busy, 0);
                ModBase.RunInUi(RefreshStatusAsync);
            }
        }, "Ayakaleaf Stack " + (start ? "Start" : "Stop"));
    }

    private void BtnStart_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) => RunStackOperation(true);

    private void BtnStop_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) => RunStackOperation(false);

    private void BtnOpen_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (Interlocked.Exchange(ref _openBusy, 1) == 1) return;
        ModBase.RunInNewThread(() =>
        {
            try
            {
                var user = LocalUserSession.Instance.Current;
                // 自动开户（已存在则复用）+ 登录取会话，然后 UI 线程打开内嵌工作台
                var account = AyakaleafAccountService.GetOrCreateAccountAsync(user.UserName)
                    .GetAwaiter().GetResult();
                var sid = AyakaleafAccountService.LoginAsync(account).GetAwaiter().GetResult();
                ModBase.RunInUi(() => new WorkbenchWindow(account, sid).Show());
            }
            catch (Exception ex)
            {
                HintService.Hint("打开工作台失败：" + ex.Message, HintType.Error);
            }
            finally
            {
                Interlocked.Exchange(ref _openBusy, 0);
            }
        }, "Ayakaleaf OpenWorkbench");
    }

    private async void BtnAccount_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        try
        {
            await LocalUserUi.ShowSwitchUserAsync();
            RefreshStatusAsync();
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[Ayakaleaf] 打开账户管理失败");
        }
    }
}
