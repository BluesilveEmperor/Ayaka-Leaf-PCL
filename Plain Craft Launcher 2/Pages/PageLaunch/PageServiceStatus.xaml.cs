using System;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using PCL.Core.App;
using PCL.Core.Ayakaleaf;

namespace PCL;

/// <summary>
/// 服务状态页（US5 / FR-008 / FR-009）：容器 CPU/内存、编译队列、日志尾部，一键重启。
/// <para>
/// 采集只在页面可见时进行（<see cref="DispatcherTimer"/> 周期 3 秒），页面切走即停，
/// 避免后台空转消耗 wslc 调用。失联态不渲染指标数字，防止误读成"0 占用"。
/// </para>
/// </summary>
public partial class PageServiceStatus
{
    private readonly DispatcherTimer _timer;
    private int _refreshing;

    public PageServiceStatus()
    {
        InitializeComponent();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _timer.Tick += async (_, _) => await RefreshAsync();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                _timer.Start();
                _ = RefreshAsync();
            }
            else
            {
                _timer.Stop();
            }
        };
    }

    private string ProbeUrl =>
        $"http://localhost:{Config.Ayakaleaf.Port}{Config.Ayakaleaf.ProbePath}";

    private async System.Threading.Tasks.Task RefreshAsync()
    {
        // 上一次采集未结束时跳过，避免重启期间堆积请求
        if (System.Threading.Interlocked.Exchange(ref _refreshing, 1) == 1) return;
        try
        {
            var snapshot = await ServiceMetricsService.SampleAsync(ProbeUrl).ConfigureAwait(true);
            Apply(snapshot);
        }
        catch (Exception ex)
        {
            LabHealth.Text = "状态采集失败";
            LabHint.Text = ex.Message;
            PanMetrics.Visibility = Visibility.Collapsed;
        }
        finally
        {
            System.Threading.Interlocked.Exchange(ref _refreshing, 0);
        }
    }

    private void Apply(ServiceMetricsSnapshot s)
    {
        switch (s.Health)
        {
            case ServiceHealth.Unreachable:
                LabHealth.Text = "服务未运行";
                LabHint.Text = "容器不存在或已停止。请回到服务台启动服务后再查看状态。";
                PanMetrics.Visibility = Visibility.Collapsed;
                LabSampledAt.Text = $"检测时间 {s.SampledAt:HH:mm:ss}";
                TextLog.Text = "";
                return;
            case ServiceHealth.Degraded:
                LabHealth.Text = "服务运行中，但页面暂时无法访问";
                LabHint.Text = "典型原因是服务刚启动仍在预热，或编译任务堆积导致响应变慢。可稍候刷新，持续如此建议一键重启。";
                break;
            default:
                LabHealth.Text = "服务正常";
                LabHint.Text = "";
                break;
        }

        PanMetrics.Visibility = Visibility.Visible;
        LabCpu.Text = $"{s.CpuPercent:0.0}%";
        LabMemory.Text = s.MemoryLimitBytes > 0
            ? $"{FormatSize(s.MemoryUsedBytes)} / {FormatSize(s.MemoryLimitBytes)}"
            : FormatSize(s.MemoryUsedBytes);
        LabQueue.Text = s.CompileQueueDepth > 0 ? $"{s.CompileQueueDepth} 个任务" : "空闲";
        LabSampledAt.Text = $"采集时间 {s.SampledAt:HH:mm:ss}，每 3 秒自动刷新";

        // 日志每 3 秒整块替换会让滚动位置丢失；只有内容变化才更新
        var text = string.Join(Environment.NewLine, s.RecentLogLines);
        if (TextLog.Text != text)
        {
            var atEnd = TextLog.VerticalOffset >= TextLog.ExtentHeight - TextLog.ViewportHeight - 4;
            TextLog.Text = text;
            if (atEnd) TextLog.ScrollToEnd();
        }
    }

    private async void BtnRefresh_Click(object sender, MouseButtonEventArgs e)
    {
        BtnRefresh.IsEnabled = false;
        try
        {
            await RefreshAsync();
        }
        finally
        {
            BtnRefresh.IsEnabled = true;
        }
    }

    private async void BtnRestart_Click(object sender, MouseButtonEventArgs e)
    {
        if (ModMain.MyMsgBox(
                "重启会中断正在进行的编译，已保存的项目与账号数据不受影响。\n\n是否继续？",
                "重启服务", "重启", "取消", isWarn: true) != 1)
            return;

        BtnRestart.IsEnabled = false;
        LabHint.Text = "正在重启服务…";
        try
        {
            var result = await ServiceMetricsService.RestartServiceAsync().ConfigureAwait(true);
            if (!result.IsSuccess)
            {
                LabHint.Text = "重启失败：" + result.ErrorText.Trim();
                return;
            }

            LabHint.Text = "已重启，等待服务就绪…";
            // 探活最多等 60 秒：Overleaf 冷启动通常 20~40 秒
            for (var i = 0; i < 20; i++)
            {
                await System.Threading.Tasks.Task.Delay(3000).ConfigureAwait(true);
                var snapshot = await ServiceMetricsService.SampleAsync(ProbeUrl).ConfigureAwait(true);
                if (snapshot.Health == ServiceHealth.Healthy)
                {
                    Apply(snapshot);
                    return;
                }
            }

            LabHint.Text = "已重启，但 60 秒内未探活成功。可稍后手动刷新查看。";
        }
        catch (Exception ex)
        {
            LabHint.Text = "重启失败：" + ex.Message;
        }
        finally
        {
            BtnRestart.IsEnabled = true;
        }
    }

    private static string FormatSize(long bytes)
    {
        if (bytes <= 0) return "—";
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.#} {units[unit]}";
    }
}
