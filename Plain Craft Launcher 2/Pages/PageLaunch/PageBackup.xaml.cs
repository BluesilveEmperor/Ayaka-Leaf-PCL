using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PCL.Core.App;
using PCL.Core.Ayakaleaf;
using PCL.Core.UI;

namespace PCL;

public partial class PageBackup
{
    private int _busy;

    public PageBackup()
    {
        InitializeComponent();
        Loaded += (_, _) => RefreshVolumes();
    }

    private bool TryLock() => Interlocked.Exchange(ref _busy, 1) == 0;

    private void Unlock() => Interlocked.Exchange(ref _busy, 0);

    private async void RefreshVolumes()
    {
        if (!Config.Ayakaleaf.Deployed)
        {
            PanVolumes.Children.Clear();
            PanVolumes.Children.Add(new TextBlock
            {
                Text = "尚未部署内嵌运行时，请先在「服务」页完成部署。",
                Opacity = 0.6,
                FontSize = 12.5,
            });
            return;
        }
        PanVolumes.Children.Clear();
        PanVolumes.Children.Add(new TextBlock { Text = "正在获取数据卷列表…", Opacity = 0.6, FontSize = 12.5 });
        var volumes = await AyakaleafService.ListVolumesAsync();
        PanVolumes.Children.Clear();
        if (volumes.Count == 0)
        {
            PanVolumes.Children.Add(new TextBlock
            {
                Text = "暂无数据卷（服务栈启动后生成）。",
                Opacity = 0.6,
                FontSize = 12.5,
            });
            return;
        }
        foreach (var volume in volumes)
        {
            var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var name = new TextBlock
            {
                Text = volume,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(name, 0);
            var btnBackup = new MyIconButton { SvgIcon = "lucide/hard-drive-download", LogoScale = 1.0, Opacity = 0.7 };
            btnBackup.Click += (_, _) => BackupVolume(volume);
            Grid.SetColumn(btnBackup, 1);
            var btnRestore = new MyIconButton { SvgIcon = "lucide/history", LogoScale = 1.0, Opacity = 0.7 };
            btnRestore.Click += (_, _) => RestoreVolume(volume);
            Grid.SetColumn(btnRestore, 2);
            row.Children.Add(name);
            row.Children.Add(btnBackup);
            row.Children.Add(btnRestore);
            PanVolumes.Children.Add(row);
        }
    }

    private void BackupVolume(string volume)
    {
        if (!TryLock()) return;
        LabRestoreProgress.Visibility = Visibility.Visible;
        LabRestoreProgress.Text = $"正在备份 {volume}…";
        ModBase.RunInNewThread(() =>
        {
            try
            {
                var file = AyakaleafService.BackupVolumeAsync(volume, AyakaleafService.BackupDirectory).GetAwaiter().GetResult();
                ModBase.RunInUi(() =>
                {
                    HintService.Hint("备份完成：" + file, HintType.Success);
                    LabRestoreProgress.Text = "最近备份：" + file;
                });
            }
            catch (Exception ex)
            {
                ModBase.Log(ex, "[Ayakaleaf] 备份失败");
                ModBase.RunInUi(() =>
                {
                    HintService.Hint(ex.Message, HintType.Error);
                    LabRestoreProgress.Text = ex.Message;
                });
            }
            finally
            {
                Unlock();
            }
        }, "Ayakaleaf Backup");
    }

    private void RestoreVolume(string volume)
    {
        if (ModMain.MyMsgBox(
                $"恢复将覆盖数据卷 {volume} 的全部现有数据，且要求服务栈处于停止状态。\n\n是否继续？",
                "恢复数据卷", "选择备份文件", "取消", isWarn: true) != 1)
            return;
        var tar = SystemDialogs.SelectFile("Ayakaleaf 备份|*.tar.gz|全部文件|*.*", "选择备份文件",
            AyakaleafService.BackupDirectory);
        if (string.IsNullOrEmpty(tar)) return;
        if (!TryLock()) return;
        LabRestoreProgress.Visibility = Visibility.Visible;
        LabRestoreProgress.Text = $"正在恢复 {volume}…";
        ModBase.RunInNewThread(() =>
        {
            try
            {
                AyakaleafService.RestoreVolumeAsync(volume, tar).GetAwaiter().GetResult();
                ModBase.RunInUi(() =>
                {
                    HintService.Hint($"数据卷 {volume} 已恢复", HintType.Success);
                    LabRestoreProgress.Text = $"已从 {Path.GetFileName(tar)} 恢复 {volume}";
                });
            }
            catch (Exception ex)
            {
                ModBase.Log(ex, "[Ayakaleaf] 恢复失败");
                ModBase.RunInUi(() =>
                {
                    HintService.Hint(ex.Message, HintType.Error);
                    LabRestoreProgress.Text = ex.Message;
                });
            }
            finally
            {
                Unlock();
            }
        }, "Ayakaleaf Restore");
    }

    private void BtnRefreshVolumes_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) => RefreshVolumes();

    private void BtnRestore_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!Config.Ayakaleaf.Deployed)
        {
            HintService.Hint("请先在「服务」页完成内嵌运行时部署", HintType.Info);
            return;
        }
        var tar = SystemDialogs.SelectFile("Ayakaleaf 备份|*.tar.gz|全部文件|*.*", "选择备份文件",
            AyakaleafService.BackupDirectory);
        if (string.IsNullOrEmpty(tar)) return;
        // 文件名格式：卷名.yyyyMMdd-HHmmss.tar.gz
        var fileName = Path.GetFileNameWithoutExtension(tar); // 卷名.yyyyMMdd-HHmmss
        var dotIndex = fileName.LastIndexOf('.');
        if (dotIndex <= 0)
        {
            HintService.Hint("无法从文件名识别数据卷，请在数据卷列表中点击对应卷的恢复按钮", HintType.Error);
            return;
        }
        RestoreVolume(fileName[..dotIndex]);
    }
}
