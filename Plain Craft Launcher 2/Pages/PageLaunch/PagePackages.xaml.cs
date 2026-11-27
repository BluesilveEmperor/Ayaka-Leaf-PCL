using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PCL.Core.Ayakaleaf;

namespace PCL;

/// <summary>
/// 宏包管理页（US4 / FR-007）：图形化搜索、安装、卸载 TeX 宏包，操作进度直接回显。
/// <para>
/// 数据来自 <see cref="PackageManagerService"/>（容器内 tlmgr），本页只负责渲染与调度：
/// 单行操作期间禁用该行按钮，避免同一宏包并发安装/卸载。
/// </para>
/// </summary>
public partial class PagePackages
{
    private CancellationTokenSource? _searchCts;

    public PagePackages()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            if (PanResults.Children.Count == 0) await SearchAsync("");
        };
        TextKeyword.KeyDown += async (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            await SearchAsync(TextKeyword.Text);
        };
    }

    private async void BtnSearch_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) => await SearchAsync(TextKeyword.Text);

    private async Task SearchAsync(string keyword)
    {
        // 连点搜索时取消上一次，只保留最新结果
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var ct = _searchCts.Token;

        BtnSearch.IsEnabled = false;
        LabStatus.Text = "正在检索 TeX Live…（首次检索需解析包数据库，可能需要十几秒）";
        PanResults.Children.Clear();
        try
        {
            var items = await PackageManagerService.SearchAsync(keyword, 60, ct);
            if (ct.IsCancellationRequested) return;

            if (items.Count == 0)
            {
                LabStatus.Text = string.IsNullOrWhiteSpace(keyword)
                    ? "容器内未取到已安装列表（服务未启动时无法查询）。"
                    : $"没有匹配「{keyword.Trim()}」的宏包。";
                return;
            }

            foreach (var item in items) PanResults.Children.Add(BuildRow(item));
            LabStatus.Text = $"共 {items.Count} 条结果" + (items.Count >= 60 ? "（已截断，请细化关键字）" : "");
        }
        catch (OperationCanceledException)
        {
            // 被新一次搜索取代，忽略
        }
        catch (Exception ex)
        {
            LabStatus.Text = "检索失败：" + ex.Message;
        }
        finally
        {
            if (!ct.IsCancellationRequested) BtnSearch.IsEnabled = true;
        }
    }

    private FrameworkElement BuildRow(TexPackageInfo item)
    {
        var grid = new Grid { Margin = new Thickness(0, 6, 0, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = item.Name,
            FontWeight = FontWeights.SemiBold,
            FontSize = 13,
        });
        if (!string.IsNullOrWhiteSpace(item.ShortDesc))
        {
            text.Children.Add(new TextBlock
            {
                Text = item.ShortDesc,
                FontSize = 11,
                Opacity = 0.6,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 12, 0),
            });
        }

        Grid.SetColumn(text, 0);
        grid.Children.Add(text);

        var action = new MyButton
        {
            Height = 30,
            Width = 84,
            VerticalAlignment = VerticalAlignment.Center,
            Text = item.Installed ? "卸载" : "安装",
            ColorType = item.Installed ? MyButton.ColorState.Red : MyButton.ColorState.Highlight,
            IsEnabled = !item.IsCollection,
        };
        if (item.IsCollection)
            action.Opacity = 0.5;

        action.Click += async (_, _) =>
        {
            if (item.Installed)
                await UninstallAsync(item, action);
            else
                await InstallAsync(item, action);
        };

        Grid.SetColumn(action, 1);
        grid.Children.Add(action);
        return grid;
    }

    private async Task InstallAsync(TexPackageInfo item, MyButton button)
    {
        button.IsEnabled = false;
        button.Text = "安装中…";
        var progress = new Progress<string>(line => LabStatus.Text = $"{item.Name}：{line}");
        LabStatus.Text = $"正在安装 {item.Name}…";
        try
        {
            var result = await PackageManagerService.InstallAsync(new[] { item.Name }, progress);
            LabStatus.Text = result.Success
                ? $"{item.Name} 安装完成，重新编译即可使用。"
                : $"安装失败：{ExplainFailure(result.Output)}";
            button.Text = result.Success ? "卸载" : "安装";
            button.ColorType = result.Success ? MyButton.ColorState.Red : MyButton.ColorState.Highlight;
            button.IsEnabled = true;
        }
        catch (Exception ex)
        {
            LabStatus.Text = "安装出错：" + ex.Message;
            button.Text = "安装";
            button.IsEnabled = true;
        }
    }

    private async Task UninstallAsync(TexPackageInfo item, MyButton button)
    {
        if (ModMain.MyMsgBox(
                $"确定卸载宏包 {item.Name} 吗？\n\n依赖它的文档将无法编译，可随时重新安装。",
                "卸载宏包", "卸载", "取消", isWarn: true) != 1)
            return;

        button.IsEnabled = false;
        button.Text = "卸载中…";
        var progress = new Progress<string>(line => LabStatus.Text = $"{item.Name}：{line}");
        LabStatus.Text = $"正在卸载 {item.Name}…";
        try
        {
            var result = await PackageManagerService.RemoveAsync(item.Name, progress);
            LabStatus.Text = result.Success
                ? $"{item.Name} 已卸载。"
                : $"卸载失败：{FirstMeaningfulLine(result.Output)}";
            button.Text = result.Success ? "安装" : "卸载";
            button.ColorType = result.Success ? MyButton.ColorState.Highlight : MyButton.ColorState.Red;
            button.IsEnabled = true;
        }
        catch (Exception ex)
        {
            LabStatus.Text = "卸载出错：" + ex.Message;
            button.Text = "卸载";
            button.IsEnabled = true;
        }
    }

    /// <summary>取 tlmgr 输出里第一条有意义的行（排掉空行与进度行），用于失败提示。</summary>
    private static string FirstMeaningfulLine(string output)
    {
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('[')) continue;
            return line.Length > 200 ? line[..200] + "…" : line;
        }

        return "未知错误（详见启动器日志）";
    }

    /// <summary>
    /// 识别 tlmgr 输出中的网络失败特征（Edge Case：网络中断要干净失败）——
    /// 给出明确中文原因而不是落在"未知错误"，且不把部分成功当作整体成功。
    /// </summary>
    private static string ExplainFailure(string output)
    {
        if (Regex.IsMatch(output,
                @"couldn'?t connect|could not connect|Network is unreachable|timed? ?out" +
                @"|Temporary failure in name resolution|Connection refused|SSL connection|failed to connect",
                RegexOptions.IgnoreCase))
            return "网络连接失败：无法访问 CTAN 镜像。请检查网络（或代理）后重试；已下载完成的部分宏包保持可用，重试时 tlmgr 会续装剩余宏包。";
        return FirstMeaningfulLine(output);
    }
}
