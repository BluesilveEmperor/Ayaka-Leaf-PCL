using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using PCL.Core.App;
using PCL.Core.Ayakaleaf;
using PCL.Core.Logging;

namespace PCL;

/// <summary>
/// 内嵌 Ayakaleaf 工作台窗口：用本地用户对应的 Ayakaleaf 会话 Cookie 自动登录，
/// 会话失效时自动重登一次，全程无需手动输入账号密码。
/// </summary>
public partial class WorkbenchWindow
{
    /// <summary>编辑器联动脚本（选中公式代码 → 右侧 PDF 自动高亮）。</summary>
    private const string EditorBridgeResource = "Resources/ayakaleaf/editor-synctex.js";

    private readonly AyakaleafAccount _account;
    private string _sid;
    private int _retryCount;

    private static string? _editorBridgeScript;
    private static bool _editorBridgeLoaded;

    private static string UserDataFolder => Path.Combine(AppContext.BaseDirectory, "data", "ayakaleaf", "webview");

    public WorkbenchWindow(AyakaleafAccount account, string sessionId)
    {
        InitializeComponent();
        _account = account;
        _sid = sessionId;
        Loaded += WorkbenchWindow_Loaded;
    }

    private async void WorkbenchWindow_Loaded(object sender, System.Windows.RoutedEventArgs e)
    {
        try
        {
            // 预检 WebView2 运行时（Edge WebView2 Evergreen，Win10/11 一般自带）
            try
            {
                _ = CoreWebView2Environment.GetAvailableBrowserVersionString();
            }
            catch (WebView2RuntimeNotFoundException)
            {
                LabLoading.Text = "未检测到 WebView2 运行时，请安装 Microsoft Edge WebView2 后重试。";
                return;
            }

            var env = await CoreWebView2Environment.CreateAsync(null, UserDataFolder);
            await Web.EnsureCoreWebView2Async(env);
            Web.CoreWebView2.NavigationCompleted += Web_NavigationCompleted;
            // 不显示浏览器报错页细节，直接进入项目列表
            Web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
            // 必须在首次导航之前注入，才能在编辑器页面加载时生效
            await InjectEditorBridgeAsync();
            EnterWorkbench();
        }
        catch (Exception ex)
        {
            LabLoading.Text = "工作台初始化失败：" + ex.Message;
        }
    }

    private void EnterWorkbench()
    {
        InjectSession();
        LabLoading.Visibility = System.Windows.Visibility.Collapsed;
        Web.Source = new Uri(AyakaleafService.BaseUrl + "/project");
    }

    /// <summary>
    /// 注入编辑器联动脚本：左侧选中 LaTeX 公式代码时，右侧 PDF 自动高亮对应位置。
    /// 脚本由 WebView2 在每个新文档创建时执行，内部会自行判断当前是否处于编辑器页面。
    /// </summary>
    private async Task InjectEditorBridgeAsync()
    {
        var script = GetEditorBridgeScript();
        if (string.IsNullOrEmpty(script)) return;

        try
        {
            await Web.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(script);
        }
        catch (Exception ex)
        {
            LogWrapper.Warn(ex, "[Ayakaleaf] 编辑器联动脚本注入失败");
        }
    }

    private static string? GetEditorBridgeScript()
    {
        if (_editorBridgeLoaded) return _editorBridgeScript;
        _editorBridgeLoaded = true;

        try
        {
            using var stream = Basics.GetResourceStream(EditorBridgeResource);
            if (stream is null)
            {
                LogWrapper.Warn("[Ayakaleaf] 未找到编辑器联动脚本资源：" + EditorBridgeResource);
                return null;
            }

            using var reader = new StreamReader(stream, Encoding.UTF8);
            _editorBridgeScript = reader.ReadToEnd();
        }
        catch (Exception ex)
        {
            LogWrapper.Warn(ex, "[Ayakaleaf] 编辑器联动脚本加载失败");
        }

        return _editorBridgeScript;
    }

    private void InjectSession()
    {
        if (string.IsNullOrEmpty(_sid)) return;
        var manager = Web.CoreWebView2.CookieManager;
        var cookie = manager.CreateCookie("overleaf.sid", _sid, "localhost", "/");
        cookie.IsHttpOnly = true;
        cookie.IsSecure = false;
        manager.AddOrUpdateCookie(cookie);
    }

    private async void Web_NavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        try
        {
            var uri = Web.Source?.ToString() ?? "";
            if (!uri.Contains("/login") || _retryCount >= 2) return;

            // 会话失效：重新登录并注入，最多重试 2 次
            _retryCount++;
            _sid = await AyakaleafAccountService.LoginAsync(_account);
            InjectSession();
            Web.Source = new Uri(AyakaleafService.BaseUrl + "/project");
        }
        catch (Exception ex)
        {
            if (_retryCount >= 2)
                LabLoading.Text = "自动登录失败：" + ex.Message;
        }
    }
}
