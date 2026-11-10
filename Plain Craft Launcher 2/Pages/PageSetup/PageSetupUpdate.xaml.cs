using System.Windows;
using System.Windows.Controls;
using PCL.Core.App;

namespace PCL;

public partial class PageSetupUpdate
{
    public PageSetupUpdate()
    {
        InitializeComponent();
        Loaded += (_, _) => Init();
    }

    private void Init()
    {
        TextCurrentVersion.Text = "AyakaLauncher " + VersionNameFormat(ModBase.versionBaseName);
        CheckUpdateEnabled.SetChecked(Config.Update.ChannelEnabled, false);
        ModAnimation.AniControlEnabled += 1;
        ComboUpdateMode.SelectedIndex = (int)Config.Update.UpdateMode;
        ModAnimation.AniControlEnabled -= 1;
        RefreshUpdateUi();
    }

    /// <summary>
    ///     按总开关刷新更新通道控件的可用状态与说明文案。
    /// </summary>
    private void RefreshUpdateUi()
    {
        var enabled = Config.Update.ChannelEnabled;
        ComboUpdateMode.IsEnabled = enabled;
        BtnCheckUpdate.IsEnabled = enabled;
        TextCurrentDesc.Text = enabled
            ? "正在使用 GitHub Releases 更新通道"
            : "更新通道已关闭，启动器不会检查或下载更新";
        TextUpdateChannelDesc.Text = enabled
            ? $"当前更新方式：{GetModeName(Config.Update.UpdateMode)}。启动器会在启动时按此方式检查 GitHub Releases。"
            : "更新通道已关闭，启动器不会检查或下载更新。勾选上方选项即可开启在线更新。";
    }

    private static string GetModeName(LauncherAutoUpdateBehavior mode)
    {
        return mode switch
        {
            LauncherAutoUpdateBehavior.DownloadAndInstall => "自动下载并安装",
            LauncherAutoUpdateBehavior.DownloadAndAnnounce => "自动下载并提示更新",
            LauncherAutoUpdateBehavior.AnnounceOnly => "仅提示有新版本",
            _ => "不自动检查"
        };
    }

    private void CheckUpdateEnabled_OnChange(object sender, bool user)
    {
        if (ModAnimation.AniControlEnabled != 0)
            return;
        Config.Update.ChannelEnabled = CheckUpdateEnabled.Checked ?? true;
        RefreshUpdateUi();
    }

    private void ComboUpdateMode_OnChange(object sender, SelectionChangedEventArgs e)
    {
        if (ModAnimation.AniControlEnabled != 0 || ComboUpdateMode.SelectedIndex < 0)
            return;
        Config.Update.UpdateMode = (LauncherAutoUpdateBehavior)ComboUpdateMode.SelectedIndex;
        RefreshUpdateUi();
    }

    private void BtnCheckUpdate_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        ModBase.RunInNewThread(() => UpdateManager.UpdateStart(UpdateEnums.UpdateType.PromptOnly), "CheckUpdate");
    }

    public string VersionNameFormat(string str)
    {
        str = str.Replace("v", "");
        if (!str.Contains("-"))
            return str;
        var add = str.AfterLast("-");
        str = str.BeforeLast("-");
        return $"{str} {add.Replace(".", " ").Replace("beta", "Beta").Replace("rc", "RC")}";
    }
}
