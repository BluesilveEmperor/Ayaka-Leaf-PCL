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
            var btnBackup = new MyIconButton { SvgIcon = "lucide/download", LogoScale = 1.0, Opacity = 0.7 };
            btnBackup.Click += (_, _) => BackupVolume(volume);
            Grid.SetColumn(btnBackup, 1);
            var btnRestore = new MyIconButton { SvgIcon = "lucide/rotate-ccw", LogoScale = 1.0, Opacity = 0.7 };
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
        var tar = SystemDialogs.SelectFile("Ayakaleaf 备份|*.vhdx|全部文件|*.*", "选择备份文件",
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

    /// <summary>
    /// 全量备份：数据卷 + 账号凭据 + 本地用户库 + 启动器配置 → 单个带时间戳的 zip。
    /// 服务运行中也可备份（提示建议停止以保证一致性），与单卷备份语义一致。
    /// </summary>
    private void BtnFullBackup_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!Config.Ayakaleaf.Deployed)
        {
            HintService.Hint("请先在「服务」页完成内嵌运行时部署", HintType.Info);
            return;
        }
        if (!TryLock()) return;
        LabFullProgress.Visibility = Visibility.Visible;
        LabFullProgress.Text = "正在准备全量备份…";
        ModBase.RunInNewThread(() =>
        {
            try
            {
                var file = AyakaleafService.CreateFullBackupAsync(
                    msg => ModBase.RunInUi(() => LabFullProgress.Text = msg)).GetAwaiter().GetResult();
                ModBase.RunInUi(() =>
                {
                    HintService.Hint("全量备份完成：" + file, HintType.Success);
                    LabFullProgress.Text = "最近全量备份：" + file;
                });
            }
            catch (Exception ex)
            {
                ModBase.Log(ex, "[Ayakaleaf] 全量备份失败");
                ModBase.RunInUi(() =>
                {
                    HintService.Hint("全量备份失败：" + ex.Message, HintType.Error);
                    LabFullProgress.Text = ex.Message;
                });
            }
            finally
            {
                Unlock();
            }
        }, "Ayakaleaf FullBackup");
    }

    /// <summary>
    /// 全量恢复：选择全量备份 zip，将数据卷、账号凭据、本地用户库与配置还原到对应位置。
    /// 恢复要求服务栈停止；恢复完成后建议立即重启启动器使配置与用户数据生效。
    /// </summary>
    private void BtnFullRestore_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!Config.Ayakaleaf.Deployed)
        {
            HintService.Hint("请先在「服务」页完成内嵌运行时部署", HintType.Info);
            return;
        }
        if (ModMain.MyMsgBox(
                "全量恢复将覆盖全部数据卷、账号凭据、本地用户库与启动器配置的现有数据，且要求服务栈处于停止状态。\n恢复完成后建议立即重启启动器。\n\n是否继续？",
                "全量恢复", "选择备份文件", "取消", isWarn: true) != 1)
            return;
        var zip = SystemDialogs.SelectFile("Ayakaleaf 全量备份|*.zip|全部文件|*.*", "选择全量备份文件",
            AyakaleafService.BackupDirectory);
        if (string.IsNullOrEmpty(zip)) return;
        if (!TryLock()) return;
        LabFullProgress.Visibility = Visibility.Visible;
        LabFullProgress.Text = "正在准备全量恢复…";
        ModBase.RunInNewThread(() =>
        {
            try
            {
                AyakaleafService.RestoreFullBackupAsync(zip,
                    msg => ModBase.RunInUi(() => LabFullProgress.Text = msg)).GetAwaiter().GetResult();
                ModBase.RunInUi(() =>
                {
                    HintService.Hint("全量恢复完成，建议立即重启启动器使配置与用户数据生效", HintType.Success);
                    LabFullProgress.Text = "已从 " + Path.GetFileName(zip) + " 全量恢复，建议立即重启启动器";
                });
            }
            catch (Exception ex)
            {
                ModBase.Log(ex, "[Ayakaleaf] 全量恢复失败");
                ModBase.RunInUi(() =>
                {
                    HintService.Hint("全量恢复失败：" + ex.Message, HintType.Error);
                    LabFullProgress.Text = ex.Message;
                });
            }
            finally
            {
                Unlock();
            }
        }, "Ayakaleaf FullRestore");
    }

    private void BtnRestore_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!Config.Ayakaleaf.Deployed)
        {
            HintService.Hint("请先在「服务」页完成内嵌运行时部署", HintType.Info);
            return;
        }
        var tar = SystemDialogs.SelectFile("Ayakaleaf 备份|*.vhdx|全部文件|*.*", "选择备份文件",
            AyakaleafService.BackupDirectory);
        if (string.IsNullOrEmpty(tar)) return;
        // 文件名格式：卷名.yyyyMMdd-HHmmss.vhdx
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
