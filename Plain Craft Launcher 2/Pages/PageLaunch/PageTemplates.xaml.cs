using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PCL.Core.Ayakaleaf;
using PCL.Core.Users;

namespace PCL;

/// <summary>
/// 中文模板库页（US10 / FR-015）：按场景分类展示内置模板（含预览图），
/// 「一键新建」经 <see cref="TemplateLibraryService"/> 走服务端项目创建入口
/// 生成新项目，并直接打开工作台（<c>/project/&lt;id&gt;</c>）。
/// <para>
/// 模板包随启动器嵌入（<c>Resources/ayakaleaf/templates/*.zip</c>），
/// 本页只读注册表渲染，创建失败保留页面原状、以提示条反馈原因。
/// </para>
/// </summary>
public partial class PageTemplates
{
    private int _busy;

    public PageTemplates()
    {
        InitializeComponent();
        Loaded += (_, _) => Render();
    }

    /// <summary>渲染模板卡片（注册表只读，重复进入页面幂等重建）。</summary>
    private void Render()
    {
        PanTemplates.Children.Clear();
        foreach (var template in TemplateLibraryService.GetTemplates())
            PanTemplates.Children.Add(BuildCard(template));
    }

    /// <summary>构建单张模板卡片：预览图 + 标题/分类 + 描述 + 一键新建。</summary>
    private FrameworkElement BuildCard(TemplateInfo template)
    {
        var card = new Border
        {
            Width = 252,
            Margin = new Thickness(0, 0, 14, 14),
            Background = new SolidColorBrush(Color.FromArgb(0x14, 0x00, 0x00, 0x00)),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10),
        };
        var panel = new StackPanel();

        // 预览图（资源缺失时显示占位文本，不阻塞页面）
        var preview = new Image
        {
            Height = 148,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        var previewBytes = TemplateLibraryService.LoadPreview(template);
        if (previewBytes is { Length: > 0 })
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = new MemoryStream(previewBytes);
            image.EndInit();
            image.Freeze();
            preview.Source = image;
        }
        panel.Children.Add(preview);

        // 标题行：名称 + 场景标签
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        titleRow.Children.Add(new TextBlock
        {
            Text = template.Title,
            FontWeight = FontWeights.Bold,
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center,
        });
        titleRow.Children.Add(new TextBlock
        {
            Text = CategoryLabel(template.Category),
            FontSize = 10.5,
            Opacity = 0.6,
            Margin = new Thickness(8, 0, 0, 0),
            Padding = new Thickness(0, 1, 0, 1),
            VerticalAlignment = VerticalAlignment.Center,
        });
        panel.Children.Add(titleRow);

        // 描述（限高避免长文案撑爆卡片）
        panel.Children.Add(new TextBlock
        {
            Text = template.Description,
            FontSize = 11.5,
            Opacity = 0.65,
            TextWrapping = TextWrapping.Wrap,
            Height = 50,
            Margin = new Thickness(0, 4, 0, 0),
        });

        // 元信息：引擎 / 版本 / 入口
        panel.Children.Add(new TextBlock
        {
            Text = $"引擎 {template.Engine}　v{template.Version}　入口 {template.Entry}",
            FontSize = 10.5,
            Opacity = 0.45,
            Margin = new Thickness(0, 4, 0, 0),
        });

        var button = new MyButton
        {
            Height = 32,
            Margin = new Thickness(0, 10, 0, 0),
            Text = "一键新建",
            ColorType = MyButton.ColorState.Highlight,
        };
        button.Click += (_, _) => CreateFromTemplate(template, button);
        panel.Children.Add(button);

        card.Child = panel;
        return card;
    }

    private static string CategoryLabel(TemplateCategory category) => category switch
    {
        TemplateCategory.Exam => "中学试卷",
        TemplateCategory.Thesis => "本科毕设",
        TemplateCategory.Contest => "竞赛报告",
        TemplateCategory.Journal => "期刊投稿",
        _ => category.ToString(),
    };

    /// <summary>
    /// 一键新建：后台线程经模板库服务创建项目（登录 + 上传导入），
    /// 成功后在 UI 线程打开工作台并定位到新项目；失败保留页面、以状态行 + 提示条反馈。
    /// </summary>
    private void CreateFromTemplate(TemplateInfo template, MyButton button)
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        button.IsEnabled = false;
        LabStatus.Text = $"正在从「{template.Title}」创建项目…（登录、上传模板并导入，通常数秒）";
        ModBase.RunInNewThread(() =>
        {
            try
            {
                // 未登录本地用户时传空串，由账号服务按“本地用户缺失”分类给出中文提示
                var userName = LocalUserSession.Instance.Current?.UserName ?? string.Empty;
                var result = TemplateLibraryService
                    .CreateProjectFromTemplateAsync(template, userName).GetAwaiter().GetResult();
                ModBase.RunInUi(() =>
                {
                    if (result.Status == TemplateCreateStatus.Created)
                    {
                        LabStatus.Text = $"已从「{template.Title}」创建项目，正在打开工作台…";
                        HintService.Hint($"已从「{template.Title}」创建项目", HintType.Success);
                        new WorkbenchWindow(result.Account!, result.SessionId!,
                            "/project/" + result.ProjectId).Show();
                    }
                    else
                    {
                        LabStatus.Text = "创建项目失败：" + result.Error;
                        HintService.Hint("创建项目失败：" + result.Error, HintType.Error);
                    }
                });
            }
            catch (Exception ex)
            {
                ModBase.Log(ex, "[Ayakaleaf] 模板创建项目异常");
                ModBase.RunInUi(() =>
                {
                    LabStatus.Text = "创建项目失败：" + ex.Message;
                    HintService.Hint("创建项目失败：" + ex.Message, HintType.Error);
                });
            }
            finally
            {
                ModBase.RunInUi(() =>
                {
                    button.IsEnabled = true;
                    Interlocked.Exchange(ref _busy, 0);
                });
            }
        }, "Ayakaleaf TemplateCreate");
    }
}
