using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using PCL.Core.Ayakaleaf;
using PCL.Core.Logging;

namespace PCL;

/// <summary>
/// 本地同步绑定管理窗口（US11 / FR-016 / FR-017）。
/// 由工作台右上角「本地同步」入口打开：建立 / 查看 / 暂停 / 立即同步 / 解除
/// 项目与本地文件夹的双向同步绑定。同步通知（冲突 / 错误）由应用级订阅统一
/// 转为中文提示（见 <see cref="FormMain"/>），本窗口只负责刷新列表状态。
/// </summary>
public partial class SyncBindingsWindow
{
    private readonly string? _projectId;
    private readonly string? _projectName;
    private int _busy;

    public SyncBindingsWindow(string? projectId, string? projectName)
    {
        InitializeComponent();
        _projectId = projectId;
        _projectName = projectName;
        if (string.IsNullOrEmpty(_projectId))
            LabBindHint.Text = "当前未打开任何项目：请先在工作台打开一个项目，再回到本窗口绑定。";
        // 通知到达时刷新状态行（提示气泡由应用级订阅弹出，这里不重复提示）
        FolderSyncService.NoticeReceived += FolderSyncService_NoticeReceived;
        Closed += (_, _) => FolderSyncService.NoticeReceived -= FolderSyncService_NoticeReceived;
        Loaded += (_, _) => RefreshList();
    }

    /// <summary>同步通知到达：调度到 UI 线程刷新绑定列表（最近同步时间 / 状态）。</summary>
    private void FolderSyncService_NoticeReceived(SyncNotice notice)
    {
        ModBase.RunInUi(() =>
        {
            try
            {
                if (IsLoaded)
                    RefreshList();
            }
            catch (Exception ex)
            {
                LogWrapper.Warn(ex, "[Ayakaleaf] 刷新本地同步列表失败");
            }
        });
    }

    // ---------------- 绑定 ----------------

    /// <summary>「绑定当前项目到文件夹」：选目录 → 建立绑定并执行首轮同步。</summary>
    private async void BtnBind_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (Interlocked.Exchange(ref _busy, 1) != 0)
        {
            HintService.Hint("正在处理上一步操作，请稍候…", HintType.Info);
            return;
        }

        try
        {
            if (string.IsNullOrEmpty(_projectId))
            {
                HintService.Hint("请先在工作台打开一个项目，再绑定本地文件夹。", HintType.Info);
                return;
            }
            if (FolderSyncService.FindBindingByProject(_projectId) is not null)
            {
                HintService.Hint("该项目已建立本地同步绑定，无需重复绑定。", HintType.Info);
                return;
            }

            var dialog = new OpenFolderDialog
            {
                Title = "选择本地同步文件夹（建议选择空文件夹）",
            };
            if (dialog.ShowDialog(this) != true)
                return;

            var path = dialog.FolderName;
            var nonEmpty = Directory.EnumerateFileSystemEntries(path).Any();
            if (nonEmpty)
            {
                // 非空目录：同名不同内容的文件会生成冲突副本，双方内容都保留，但仍明确告知
                var choice = ModMain.MyMsgBox(
                    "所选文件夹不为空。绑定后将与项目内容比对：同名但内容不同的文件会保留双方版本（本地生成 .conflict- 副本），其余文件不受影响。",
                    "绑定非空文件夹", "继续绑定", "取消");
                if (choice != 1)
                    return;
            }

            LabBindHint.Text = "正在建立绑定并执行首轮同步，请稍候…";
            var binding = await FolderSyncService.BindAsync(path, _projectId, _projectName ?? _projectId);
            LogWrapper.Info($"[Ayakaleaf] 本地同步绑定成功：{binding.LocalPath} ↔ {binding.ProjectName}");
            HintService.Hint($"已绑定「{binding.ProjectName}」，项目文件已同步到：{binding.LocalPath}", HintType.Success);
            LabBindHint.Text = $"项目「{binding.ProjectName}」已绑定到：{binding.LocalPath}";
            RefreshList();
        }
        catch (Exception ex)
        {
            LogWrapper.Warn(ex, "[Ayakaleaf] 建立本地同步绑定失败");
            HintService.Hint("绑定失败：" + ex.Message, HintType.Error);
            LabBindHint.Text = "绑定失败：" + ex.Message;
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    // ---------------- 列表 ----------------

    /// <summary>重建绑定列表（绑定状态可能随时被后台同步更新）。</summary>
    private void RefreshList()
    {
        PanBindings.Children.Clear();
        var bindings = FolderSyncService.GetBindings();
        if (bindings.Count == 0)
        {
            PanBindings.Children.Add(new TextBlock
            {
                Text = "暂无绑定。打开工作台进入一个项目后，即可在上方绑定本地文件夹。",
                FontSize = 12,
                Foreground = System.Windows.Media.Brushes.Gray,
                TextWrapping = TextWrapping.Wrap,
            });
            return;
        }

        foreach (var binding in bindings.ToList())
            PanBindings.Children.Add(BuildBindingRow(binding));
    }

    /// <summary>构造单条绑定的展示行：项目名 / 本地路径 / 状态 / 操作按钮。</summary>
    private StackPanel BuildBindingRow(SyncBinding binding)
    {
        var row = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };

        row.Children.Add(new TextBlock
        {
            Text = string.IsNullOrEmpty(binding.ProjectName) ? binding.ProjectId : binding.ProjectName,
            FontSize = 14,
            FontWeight = FontWeights.Bold,
            Foreground = System.Windows.Media.Brushes.Black,
        });

        row.Children.Add(new TextBlock
        {
            Text = binding.LocalPath,
            FontSize = 12,
            Foreground = System.Windows.Media.Brushes.DimGray,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0),
        });

        var lastSync = binding.LastSyncAtUtc == DateTime.MinValue
            ? "尚未同步"
            : "上次同步：" + binding.LastSyncAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        var status = binding.Paused ? "已暂停（不同步）· " + lastSync : "监控中 · " + lastSync;
        row.Children.Add(new TextBlock
        {
            Text = status,
            FontSize = 12,
            Foreground = System.Windows.Media.Brushes.Gray,
            Margin = new Thickness(0, 2, 0, 0),
        });

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 8, 0, 0),
        };

        var btnSyncNow = new MyButton
        {
            Text = "立即同步",
            Height = 28,
            Width = 90,
            Margin = new Thickness(0, 0, 10, 0),
        };
        btnSyncNow.Click += (_, _) => SyncNowAsync(binding.BindingId);
        buttons.Children.Add(btnSyncNow);

        var btnPause = new MyButton
        {
            Text = binding.Paused ? "恢复" : "暂停",
            Height = 28,
            Width = 70,
            Margin = new Thickness(0, 0, 10, 0),
        };
        btnPause.Click += (_, _) =>
        {
            try
            {
                FolderSyncService.SetPaused(binding.BindingId, !binding.Paused);
                HintService.Hint(binding.Paused
                    ? $"已恢复「{binding.ProjectName}」的本地同步。"
                    : $"已暂停「{binding.ProjectName}」的本地同步（恢复前不再自动同步）。", HintType.Info);
                RefreshList();
            }
            catch (Exception ex)
            {
                HintService.Hint("操作失败：" + ex.Message, HintType.Error);
            }
        };
        buttons.Children.Add(btnPause);

        var btnOpen = new MyButton
        {
            Text = "打开文件夹",
            Height = 28,
            Width = 100,
            Margin = new Thickness(0, 0, 10, 0),
        };
        btnOpen.Click += (_, _) =>
        {
            try
            {
                if (!Directory.Exists(binding.LocalPath))
                {
                    HintService.Hint("本地文件夹已不存在，请解除绑定后重新绑定。", HintType.Warning);
                    return;
                }
                Process.Start(new ProcessStartInfo { FileName = binding.LocalPath, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                LogWrapper.Warn(ex, "[Ayakaleaf] 打开本地同步文件夹失败");
                HintService.Hint("打开文件夹失败：" + ex.Message, HintType.Error);
            }
        };
        buttons.Children.Add(btnOpen);

        var btnUnbind = new MyButton
        {
            Text = "解除绑定",
            Height = 28,
            Width = 90,
            ColorType = MyButton.ColorState.Red,
        };
        btnUnbind.Click += (_, _) => Unbind(binding);
        buttons.Children.Add(btnUnbind);

        row.Children.Add(buttons);
        return row;
    }

    /// <summary>立即执行一轮完整同步并提示结果统计。</summary>
    private async void SyncNowAsync(string bindingId)
    {
        if (Interlocked.Exchange(ref _busy, 1) != 0)
        {
            HintService.Hint("已有同步操作正在进行，请稍候…", HintType.Info);
            return;
        }

        try
        {
            HintService.Hint("正在同步…", HintType.Info);
            var stats = await FolderSyncService.SyncNowAsync(bindingId);
            var summary = $"同步完成：拉取 {stats.Pulled} 项，推送 {stats.Pushed} 项";
            if (stats.Conflicts > 0)
                summary += $"，冲突 {stats.Conflicts} 项（双方版本均已保留，详见本地 .conflict- 副本）";
            if (stats.Errors > 0)
                summary += $"，失败 {stats.Errors} 项";
            HintService.Hint(summary, stats.Errors > 0 ? HintType.Warning : HintType.Success);
            RefreshList();
        }
        catch (Exception ex)
        {
            LogWrapper.Warn(ex, "[Ayakaleaf] 手动同步失败");
            HintService.Hint("同步失败：" + ex.Message, HintType.Error);
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    /// <summary>解除绑定（二次确认；本地文件与同步清单均保留，仅停止自动同步）。</summary>
    private void Unbind(SyncBinding binding)
    {
        var choice = ModMain.MyMsgBox(
            $"解除「{binding.ProjectName}」的本地同步绑定？\r\n本地文件夹与已同步的文件都会保留，只是不再自动同步。",
            "解除绑定", "确认解除", "取消", isWarn: true);
        if (choice != 1)
            return;

        try
        {
            FolderSyncService.Unbind(binding.BindingId);
            HintService.Hint($"已解除「{binding.ProjectName}」的本地同步绑定。", HintType.Success);
            RefreshList();
        }
        catch (Exception ex)
        {
            HintService.Hint("解除绑定失败：" + ex.Message, HintType.Error);
        }
    }
}
