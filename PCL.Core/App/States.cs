using System.Collections.Generic;
using PCL.Core.App.Configuration;

namespace PCL.Core.App;

/// <summary>
/// 全局状态类。
/// </summary>
// ReSharper disable InconsistentNaming
public static partial class States
{
    /// <summary>
    /// 自定义变量。
    /// </summary>
    [AnyConfigItem<Dictionary<string, string>>("CustomVariables", ConfigSource.Local)] public static partial Dictionary<string, string> CustomVariables { get; set; }

    /// <summary>
    /// 提示状态。
    /// </summary>
    [ConfigGroup("Hint")] partial class HintStatesGroup
    {
        /// <summary>
        /// 执行自定义主页内含命令提示。
        /// </summary>
        [ConfigItem<bool>("HintCustomCommand", false)] public partial bool HomepageCommand { get; set; }
    }

    /// <summary>
    /// 游戏与启动相关状态。
    /// </summary>
    [ConfigGroup("Game")] partial class GameStatesGroup
    {
        /// <summary>
        /// 当前文件夹。
        /// </summary>
        [ConfigItem<string>("LaunchFolderSelect", "", ConfigSource.Local)] public partial string SelectedFolder { get; set; }

        /// <summary>
        /// 所有文件夹。
        /// </summary>
        [ConfigItem<string>("LaunchFolders", "")] public partial string Folders { get; set; }
    }

    /// <summary>
    /// 用户界面状态。
    /// </summary>
    [ConfigGroup("UI")] partial class UiStatesGroup
    {
        /// <summary>
        /// 窗口高度。
        /// </summary>
        [ConfigItem<double>("WindowHeight", 550, ConfigSource.Local)] public partial double WindowHeight { get; set; }

        /// <summary>
        /// 窗口宽度。
        /// </summary>
        [ConfigItem<double>("WindowWidth", 900, ConfigSource.Local)] public partial double WindowWidth { get; set; }

    }

    /// <summary>
    /// 系统状态。
    /// </summary>
    [ConfigGroup("System")] partial class SystemStatesGroup
    {
        /// <summary>
        /// 识别码。
        /// </summary>
        [ConfigItem<string>("LaunchUuid", "")] public partial string LaunchUuid { get; set; }

        /// <summary>
        /// 上次导出配置路径。
        /// </summary>
        [ConfigItem<string>("CacheExportConfig", "")] public partial string ExportConfigPath { get; set; }

        /// <summary>
        /// 最终用户许可协议。
        /// </summary>
        [ConfigItem<bool>("SystemEula", false)] public partial bool LauncherEula { get; set; }

        /// <summary>
        /// 启动器打开次数。
        /// </summary>
        [ConfigItem<int>("SystemCount", 0, ConfigSource.SharedEncrypt)] public partial int StartupCount { get; set; }

        /// <summary>
        /// 上个版本。
        /// </summary>
        [ConfigItem<int>("SystemLastVersionReg", 0, ConfigSource.SharedEncrypt)] public partial int LastVersion { get; set; }

        /// <summary>
        /// 上个最高 Beta 版本。
        /// </summary>
        [ConfigItem<int>("SystemHighestBetaVersionReg", 0, ConfigSource.SharedEncrypt)] public partial int LastBetaVersion { get; set; }

        /// <summary>
        /// 上个最高 Alpha 版本。
        /// </summary>
        [ConfigItem<int>("SystemHighestAlphaVersionReg", 0, ConfigSource.SharedEncrypt)] public partial int LastAlphaVersion { get; set; }

        /// <summary>
        /// 启动器公告。
        /// </summary>
        [ConfigItem<int>("SystemSystemActivity", 0, ConfigSource.Local)] public partial int AnnounceSolution { get; set; }

    }
}
