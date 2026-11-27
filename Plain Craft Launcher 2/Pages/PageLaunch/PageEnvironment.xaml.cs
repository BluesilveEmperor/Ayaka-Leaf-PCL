using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PCL.Core.App;
using PCL.Core.Ayakaleaf;

namespace PCL;

/// <summary>
/// 环境自检页（US2 / FR-004 / FR-005）：列出 WSL / wslc / .NET 三项状态，缺失项一键修复。
/// </summary>
public partial class PageEnvironment
{
    private IReadOnlyList<EnvironmentDependency> _items = Array.Empty<EnvironmentDependency>();

    public PageEnvironment()
    {
        InitializeComponent();
        Loaded += async (_, _) => await RecheckAsync();
    }

    private async void BtnRecheck_Click(object sender, MouseButtonEventArgs e) => await RecheckAsync();

    private async Task RecheckAsync()
    {
        BtnRecheck.IsEnabled = false;
        LabSummary.Text = "正在检测…";
        try
        {
            _items = await EnvironmentCheckService.CheckAsync();
            Render();
            var missing = _items.Count(i => !i.IsOk);
            LabSummary.Text = missing == 0
                ? "环境就绪，可以开始部署服务。"
                : $"发现 {missing} 项需要处理：修复后点「重新检测」复查；需要重启系统的项在重启后再复查。";
            if (missing == 0)
            {
                // 与部署门禁共用持久化标记（T008）：此处确认就绪后，部署/启动快速通过
                States.Ayakaleaf.EnvironmentReady = true;
                States.Ayakaleaf.FirstRunGuided = true;
            }
        }
        catch (Exception ex)
        {
            LabSummary.Text = "检测失败：" + ex.Message;
        }
        finally
        {
            BtnRecheck.IsEnabled = true;
        }
    }

    private void Render()
    {
        PanChecks.Children.Clear();
        foreach (var item in _items) PanChecks.Children.Add(BuildRow(item));
    }

    private FrameworkElement BuildRow(EnvironmentDependency item)
    {
        var grid = new Grid { Margin = new Thickness(0, 6, 0, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var title = new StackPanel { Orientation = Orientation.Horizontal };
        title.Children.Add(new TextBlock
        {
            Text = item.Name,
            FontWeight = FontWeights.SemiBold,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
        });
        title.Children.Add(new TextBlock
        {
            Text = item.Status switch
            {
                DependencyStatus.Ok => "就绪",
                DependencyStatus.Missing => "缺失",
                DependencyStatus.Repairing => "修复中",
                DependencyStatus.RepairFailed => "修复失败",
                _ => "",
            },
            FontSize = 10,
            Margin = new Thickness(8, 0, 0, 0),
            Opacity = item.Status == DependencyStatus.Ok ? 0.5 : 0.85,
            VerticalAlignment = VerticalAlignment.Center,
        });
        info.Children.Add(title);
        if (!string.IsNullOrWhiteSpace(item.Detail))
        {
            info.Children.Add(new TextBlock
            {
                Text = item.Detail,
                FontSize = 11,
                Opacity = 0.6,
                Margin = new Thickness(0, 2, 12, 0),
                TextWrapping = TextWrapping.Wrap,
            });
        }

        Grid.SetColumn(info, 0);
        grid.Children.Add(info);

        if (item.RepairHint is not null && !item.IsOk)
        {
            var button = new MyButton
            {
                Height = 30,
                Width = 112,
                VerticalAlignment = VerticalAlignment.Center,
                Text = item.RepairHint,
                ColorType = MyButton.ColorState.Highlight,
            };
            button.Click += async (_, _) => await RepairAsync(item, button);
            Grid.SetColumn(button, 1);
            grid.Children.Add(button);
        }

        return grid;
    }

    private async Task RepairAsync(EnvironmentDependency item, MyButton button)
    {
        button.IsEnabled = false;
        var original = button.Text;
        button.Text = "处理中…";
        LabSummary.Text = $"正在对「{item.Name}」执行修复，请在弹出的系统确认框里允许…";
        try
        {
            // .NET 运行时缺失（T010）：打开官方下载页，由用户下载安装（架构需与主程序一致）
            if (item.Id == "dotnet")
            {
                ModBase.OpenWebsite("https://get.dot.net/10");
                LabSummary.Text = "已打开 .NET 10 Desktop Runtime 下载页：请下载并安装与主程序架构一致的运行时（x64 程序装 x64），完成后点「重新检测」复查。";
                return;
            }

            var (success, message) = await EnvironmentCheckService.RepairAsync(item);
            LabSummary.Text = message;
            if (!success)
            {
                button.Text = "重试";
                return;
            }

            // 需重启才生效的修复不立即复查：重启前复查必然还是缺失，徒增困惑（Edge Case）
            if (item.RequiresReboot) return;

            // 修复命令成功且无需重启：直接复查一次给用户即时反馈
            await RecheckAsync();
        }
        catch (Exception ex)
        {
            LabSummary.Text = "修复失败：" + ex.Message;
            button.Text = "重试";
        }
        finally
        {
            button.IsEnabled = true;
            if (button.Text == "处理中…") button.Text = original;
        }
    }
}
