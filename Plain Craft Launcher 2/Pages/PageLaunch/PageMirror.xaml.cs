using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PCL.Core.App;
using PCL.Core.Ayakaleaf;
using PCL.Core.UI;

namespace PCL;

public partial class PageMirror
{
    private int _busy;

    public PageMirror()
    {
        InitializeComponent();
        Loaded += (_, _) => RefreshImages();
    }

    private bool TryLock() => Interlocked.Exchange(ref _busy, 1) == 0;

    private void Unlock() => Interlocked.Exchange(ref _busy, 0);

    private async void RefreshImages()
    {
        if (!Config.Ayakaleaf.Deployed)
        {
            PanImages.Children.Clear();
            PanImages.Children.Add(new TextBlock
            {
                Text = "尚未部署内嵌运行时，请先在「服务」页完成部署。",
                Opacity = 0.6,
                FontSize = 12.5,
            });
            return;
        }
        PanImages.Children.Clear();
        PanImages.Children.Add(new TextBlock { Text = "正在获取镜像列表…", Opacity = 0.6, FontSize = 12.5 });
        var images = await AyakaleafService.ListImagesAsync();
        PanImages.Children.Clear();
        if (images.Count == 0)
        {
            PanImages.Children.Add(new TextBlock
            {
                Text = "暂无镜像，请导入镜像文件或完成部署。",
                Opacity = 0.6,
                FontSize = 12.5,
            });
            return;
        }
        foreach (var image in images)
        {
            var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var name = new TextBlock
            {
                Text = $"{image.Repository}:{image.Tag}",
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(name, 0);
            var size = new TextBlock
            {
                Text = $"{image.Size}    {image.Created}",
                Opacity = 0.6,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 12, 0),
            };
            Grid.SetColumn(size, 1);
            var btnRemove = new MyIconButton { SvgIcon = "lucide/trash-2", LogoScale = 1.0, Opacity = 0.7 };
            var repo = image.Repository;
            var tag = image.Tag;
            btnRemove.Click += (_, _) => RemoveImage(repo, tag);
            Grid.SetColumn(btnRemove, 2);
            row.Children.Add(name);
            row.Children.Add(size);
            row.Children.Add(btnRemove);
            PanImages.Children.Add(row);
        }
    }

    private void RemoveImage(string repository, string tag)
    {
        if (ModMain.MyMsgBox($"确定删除镜像 {repository}:{tag} 吗？", "删除镜像",
                "删除", "取消", isWarn: true) != 1)
            return;
        if (!TryLock()) return;
        ModBase.RunInNewThread(() =>
        {
            try
            {
                AyakaleafService.RemoveImageAsync(repository, tag).GetAwaiter().GetResult();
                ModBase.RunInUi(() =>
                {
                    HintService.Hint("镜像已删除", HintType.Success);
                    RefreshImages();
                });
            }
            catch (Exception ex)
            {
                ModBase.RunInUi(() => HintService.Hint(ex.Message, HintType.Error));
            }
            finally
            {
                Unlock();
            }
        }, "Ayakaleaf ImageRemove");
    }

    private void BtnRefreshImages_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) => RefreshImages();

    private void BtnImport_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!Config.Ayakaleaf.Deployed)
        {
            HintService.Hint("请先在「服务」页完成内嵌运行时部署", HintType.Info);
            return;
        }
        var tar = SystemDialogs.SelectFile("镜像文件|*.tar;*.tar.gz;*.tgz|全部文件|*.*", "选择镜像文件",
            AyakaleafService.RuntimeDirectory);
        if (string.IsNullOrEmpty(tar)) return;
        if (!TryLock()) return;
        BtnImport.IsEnabled = false;
        LabImportProgress.Visibility = Visibility.Visible;
        LabImportProgress.Text = $"正在导入 {Path.GetFileName(tar)}（大文件需要数分钟）…";
        ModBase.RunInNewThread(() =>
        {
            try
            {
                AyakaleafService.ImportImageAsync(tar).GetAwaiter().GetResult();
                ModBase.RunInUi(() =>
                {
                    HintService.Hint("镜像导入完成", HintType.Success);
                    LabImportProgress.Visibility = Visibility.Collapsed;
                    RefreshImages();
                });
            }
            catch (Exception ex)
            {
                ModBase.Log(ex, "[Ayakaleaf] 镜像导入失败");
                ModBase.RunInUi(() =>
                {
                    HintService.Hint(ex.Message, HintType.Error);
                    LabImportProgress.Text = ex.Message;
                });
            }
            finally
            {
                Unlock();
                ModBase.RunInUi(() => BtnImport.IsEnabled = true);
            }
        }, "Ayakaleaf ImageImport");
    }
}
