using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PCL.Core.App;
using PCL.Core.Ayakaleaf;

namespace PCL;

public partial class PageServiceRight
{
    private int _busy;

    public PageServiceRight()
    {
        InitializeComponent();
        Loaded += (_, _) => RefreshAll();
    }

    private void RefreshAll()
    {
        CardDeploy.Visibility = Config.Ayakaleaf.Deployed ? Visibility.Collapsed : Visibility.Visible;
        CheckLanAccess.SetChecked(Config.Ayakaleaf.AllowLanAccess, false);
        RefreshLanAddress();
        RefreshContainers();
        RefreshLog();
    }

    /// <summary>
    /// 更新局域网访问地址提示（跟随开关状态与本机当前 IP）。
    /// </summary>
    private void RefreshLanAddress()
    {
        var url = AyakaleafService.LanSiteUrl;
        if (url == null)
        {
            LabLanAddress.Visibility = Visibility.Collapsed;
            return;
        }
        LabLanAddress.Visibility = Visibility.Visible;
        var lanIp = AyakaleafService.ResolveLanAddress();
        LabLanAddress.Text = $"队友访问地址：{url}（简体中文：http://{lanIp}:{AyakaleafService.ChinesePort}）";
    }

    private void CheckLanAccess_Change(object sender, bool user)
    {
        if (!user) return; // 初始化赋值不处理
        Config.Ayakaleaf.AllowLanAccess = CheckLanAccess.Checked == true;
        RefreshLanAddress();
        HintService.Hint("已保存。重启服务后生效（若服务正在运行，请停止后重新启动）", HintType.Success);
    }

    private bool TryLock()
    {
        return Interlocked.Exchange(ref _busy, 1) == 0;
    }

    private void Unlock()
    {
        Interlocked.Exchange(ref _busy, 0);
    }

    private async void RefreshContainers()
    {
        if (!Config.Ayakaleaf.Deployed) return;
        PanContainers.Children.Clear();
        PanContainers.Children.Add(new TextBlock { Text = "正在获取容器列表…", Opacity = 0.6, FontSize = 12.5 });
        var containers = await AyakaleafService.GetContainersAsync();
        PanContainers.Children.Clear();
        if (containers.Count == 0)
        {
            PanContainers.Children.Add(new TextBlock
            {
                Text = "暂无容器（服务栈未启动或未部署）",
                Opacity = 0.6,
                FontSize = 12.5,
            });
            return;
        }
        foreach (var c in containers)
        {
            var running = c.Status.StartsWith("Up", StringComparison.OrdinalIgnoreCase);
            var item = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            item.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            item.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            item.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var dot = new System.Windows.Shapes.Ellipse
            {
                Width = 9,
                Height = 9,
                Fill = running ? Brushes.ForestGreen : Brushes.Gray,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 9, 0),
            };
            Grid.SetColumn(dot, 0);
            var name = new TextBlock
            {
                Text = c.Name,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(name, 1);
            var status = new TextBlock
            {
                Text = c.Status,
                Opacity = 0.65,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0),
            };
            Grid.SetColumn(status, 2);
            item.Children.Add(dot);
            item.Children.Add(name);
            item.Children.Add(status);
            PanContainers.Children.Add(item);
        }
    }

    private async void RefreshLog()
    {
        if (!Config.Ayakaleaf.Deployed) return;
        LabLog.Text = "正在获取日志…";
        var log = await AyakaleafService.GetLogsAsync();
        LabLog.Text = string.IsNullOrWhiteSpace(log) ? "（暂无日志）" : log;
    }

    private void BtnRefreshContainers_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) => RefreshContainers();

    private void BtnRefreshLog_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) => RefreshLog();

    private void BtnDeploy_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!TryLock()) return;
        BtnDeploy.IsEnabled = false;
        LabDeployProgress.Visibility = Visibility.Visible;
        LabDeployProgress.Text = "准备部署…";
        ModBase.RunInNewThread(() =>
        {
            try
            {
                AyakaleafService.DeployAsync(
                    msg => ModBase.RunInUi(() => LabDeployProgress.Text = msg)).GetAwaiter().GetResult();
                ModBase.RunInUi(() =>
                {
                    HintService.Hint("内嵌运行时部署完成", HintType.Success);
                    RefreshAll();
                });
            }
            catch (Exception ex)
            {
                ModBase.Log(ex, "[Ayakaleaf] 部署失败");
                ModBase.RunInUi(() =>
                {
                    HintService.Hint(ex.Message, HintType.Error);
                    LabDeployProgress.Text = ex.Message;
                });
            }
            finally
            {
                Unlock();
                ModBase.RunInUi(() => BtnDeploy.IsEnabled = true);
            }
        }, "Ayakaleaf Deploy");
    }
}
