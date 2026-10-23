using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using PCL.Core.App.Localization;
using PCL.Core.Logging;
using PCL.Core.UI;

namespace PCL;

/// <summary>
/// 启动页右侧面板。AyakaLauncher 本地版：主页固定为本地欢迎卡，不再加载任何网络内容。
/// </summary>
public partial class PageLaunchRight : IRefreshable
{
    public PageLaunchRight()
    {
        InitializeComponent();
        Loaded += (_, _) => Init();
    }

    private void Init()
    {
        PanBack.ScrollToHome();
        PanScroll = PanBack; // 不知道为啥不能在 XAML 设置
        PanLog.Visibility = ModBase.modeDebug ? Visibility.Visible : Visibility.Collapsed;
    }

    #region 主页

    /// <summary>
    ///     立即强制刷新主页（本地版主页为静态欢迎卡，仅需滚动到顶）。
    ///     必须在 UI 线程调用。
    /// </summary>
    public void ForceRefresh()
    {
        ModBase.Log("[Page] 要求刷新主页（本地欢迎页，无需重载）");
        PanBack.ScrollToHome();
    }

    void IRefreshable.Refresh()
    {
        ForceRefresh();
    }

    #endregion

    #region 随机提示（启动页彩蛋文案）

    public static string GetRandomHint(bool enableLengthLimit = false, bool raw = false)
    {
        string[]? lines = null;

        // 外部文件
        var externalPath = Path.Combine(ModBase.exePath, "PCL", "hints.txt");
        if (File.Exists(externalPath))
        {
            try
            {
                lines = File.ReadAllLines(externalPath)
                    .Where(l => !string.IsNullOrWhiteSpace(l))
                    .Select(l => l.Trim())
                    .ToArray();
            }
            catch
            {
                ModBase.Log(
                    Lang.Text("Launch.Homepage.Error.ExternalFile", externalPath),
                    ModBase.LogLevel.Hint,
                    userSummary: Lang.Text("Launch.Homepage.Error.ExternalFile", externalPath));
            }
        }

        // 嵌入式资源
        if (lines is null || lines.Length == 0)
        {
            var langCode = LocalizationService.CurrentLanguage.Code;
            lines = _LoadEmbeddedHints(langCode)
                ?? _LoadEmbeddedHints(LocalizationService.DefaultLanguageCode);
        }

        // 长度限制
        if (enableLengthLimit)
        {
            var shortLines = lines.Where(l => l.Length < 50).ToArray();
            if (shortLines.Length > 0) lines = shortLines;
        }

        // 随机返回
        var hint = lines[Random.Shared.Next(lines.Length)];
        return raw ? hint : hint.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
    }

    private static string[]? _LoadEmbeddedHints(string langCode)
    {
        try
        {
            var uri = new Uri($"pack://application:,,,/Plain Craft Launcher 2;component/Resources/hints/{langCode}.txt", UriKind.Absolute);
            using var stream = Application.GetResourceStream(uri)?.Stream;
            if (stream is null) return null;
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd()
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Select(l => l.Trim())
                .ToArray();
        }
        catch
        {
            return null;
        }
    }

    #endregion
}
