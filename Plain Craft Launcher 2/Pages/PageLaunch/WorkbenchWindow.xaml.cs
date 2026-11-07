using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using PCL.Core.App;
using PCL.Core.Ayakaleaf;
using PCL.Core.Ayakaleaf.Collab;
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

    /// <summary>界面语言切换脚本（文件 → 设置 → 拼写和语言 面板中添加“界面语言”选项）。</summary>
    private const string InterfaceLanguageResource = "Resources/ayakaleaf/interface-language.js";

    /// <summary>界面语言切换不可用时的中文提示（服务可正常使用，只是切换不会生效）。</summary>
    private const string LanguageUnavailableHint =
        "提示：界面语言切换当前不可用（服务语言补丁未生效），可在日志中查看原因。";

    private readonly AyakaleafAccount? _account;
    private string _sid;
    private int _retryCount;

    /// <summary>访客模式：经协作转发端口访问房主服务（无本地会话注入）。</summary>
    private readonly bool _isGuestMode;

    /// <summary>访客模式：可用的界面语言（按转发端口探测）。</summary>
    private readonly IReadOnlyList<CollabGuestLanguage>? _guestLanguages;

    /// <summary>访客模式：工作台入口地址（房主主端口的本地转发）。</summary>
    private readonly string _guestBaseUrl = "";

    /// <summary>初始访问路径（默认 /project；管理面板入口传 /admin）。</summary>
    private readonly string _initialPath;

    /// <summary>是否需要在窗口顶部保留“界面语言切换不可用”提示。</summary>
    private bool _languageUnavailableNotice;

    /// <summary>是否正在等待一次语言端口切换导航完成（用于区分加载失败是否由语言切换引起）。</summary>
    private bool _pendingLanguageSwitch;

    /// <summary>本次语言切换的目标地址（导航失败时用于日志与提示）。</summary>
    private string? _pendingLanguageTargetUrl;

    /// <summary>当前是否正在显示“界面语言切换失败”提示。</summary>
    private bool _languageSwitchFailed;

    private static string? _editorBridgeScript;
    private static bool _editorBridgeLoaded;
    private static string? _interfaceLanguageScript;
    private static bool _interfaceLanguageLoaded;

    private static string UserDataFolder => Path.Combine(AppContext.BaseDirectory, "data", "ayakaleaf", "webview");

    /// <summary>按 <see cref="_initialPath"/> 与用户语言偏好生成的初始访问地址。</summary>
    private string InitialUrl => _isGuestMode
        ? _guestBaseUrl + _initialPath
        : AyakaleafService.BaseUrlForLanguage(Config.Ayakaleaf.InterfaceLanguage) + _initialPath;

    public WorkbenchWindow(AyakaleafAccount account, string sessionId, string initialPath = "/project")
    {
        InitializeComponent();
        _account = account;
        _sid = sessionId;
        // 路径规范化：空白回退默认 /project；确保以 / 开头
        var path = string.IsNullOrWhiteSpace(initialPath) ? "/project" : initialPath.Trim();
        _initialPath = path.StartsWith('/') ? path : "/" + path;
        Loaded += WorkbenchWindow_Loaded;
    }

    /// <summary>
    /// 访客模式构造：经协作端口转发访问房主的 Ayakaleaf 服务。
    /// 不注入本地会话（账号在房主的服务上注册/登录），语言列表来自加入时对转发端口的探测。
    /// </summary>
    public WorkbenchWindow(IReadOnlyList<CollabGuestLanguage> languages, string initialPath = "/project")
    {
        InitializeComponent();
        _isGuestMode = true;
        _guestLanguages = languages;
        _guestBaseUrl = languages.Count > 0 ? $"http://localhost:{languages[0].LocalPort}" : "http://localhost";
        var path = string.IsNullOrWhiteSpace(initialPath) ? "/project" : initialPath.Trim();
        _initialPath = path.StartsWith('/') ? path : "/" + path;
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

            // 服务处于“运行中”但语言补丁未落地时，工作台仍可正常编辑，
            // 只是切换界面语言不会生效 —— 用中文提示明确告知，不阻断进入。
            // 静态可用性状态在启动器重启后会被重置，而服务可能仍在运行：先重新自检一次。
            // 访客模式跳过本地补丁检测：语言列表来自加入协作时对房主端口的探测。
            if (!_isGuestMode)
            {
                if (!AyakaleafService.LanguageSwitchAvailable)
                    await AyakaleafService.RefreshLanguageSwitchAvailabilityAsync();

                if (!AyakaleafService.LanguageSwitchAvailable)
                {
                    _languageUnavailableNotice = true;
                    ShowLanguageUnavailableHint();
                }
            }

            var env = await CoreWebView2Environment.CreateAsync(null, UserDataFolder);
            await Web.EnsureCoreWebView2Async(env);
            Web.CoreWebView2.NavigationCompleted += Web_NavigationCompleted;
            Web.CoreWebView2.WebMessageReceived += Web_WebMessageReceived;
            // 不显示浏览器报错页细节，直接进入项目列表
            Web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
            // 符号面板补丁更新过则清理 HTTP 磁盘缓存：bundle 文件名带哈希但补丁只改内容，
            // 不清缓存会因启发式缓存策略加载到旧版编辑器脚本
            if (AyakaleafService.ConsumeSymbolPatchCacheRefresh())
                await Web.CoreWebView2.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.DiskCache);
            // 必须在首次导航之前注入，才能在编辑器页面加载时生效
            await InjectEditorBridgeAsync();
            await InjectInterfaceLanguageAsync();
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
        // 语言切换不可用时保留顶部提示；否则按正常流程隐藏加载提示
        if (!_languageUnavailableNotice)
            LabLoading.Visibility = System.Windows.Visibility.Collapsed;
        Web.Source = new Uri(InitialUrl);
    }

    /// <summary>
    /// 在 <see cref="LabLoading"/> 上显示“界面语言切换不可用”提示。
    /// 进入工作台后该提示停靠在窗口顶部且不拦截鼠标，避免影响编辑器交互。
    /// </summary>
    private void ShowLanguageUnavailableHint()
    {
        ShowTopHint(LanguageUnavailableHint);
        LogWrapper.Warn("[Ayakaleaf] 界面语言切换当前不可用（服务语言补丁未生效），工作台仍以当前语言进入");
    }

    /// <summary>
    /// 把 <see cref="LabLoading"/> 配置为窗口顶部的提示条（不拦截编辑器点击）。
    /// </summary>
    private void ShowTopHint(string text)
    {
        LabLoading.Text = text;
        LabLoading.FontSize = 12;
        LabLoading.HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
        LabLoading.VerticalAlignment = System.Windows.VerticalAlignment.Top;
        LabLoading.Margin = new System.Windows.Thickness(0, 8, 0, 0);
        LabLoading.IsHitTestVisible = false;
        LabLoading.Visibility = System.Windows.Visibility.Visible;
    }

    /// <summary>
    /// 语言端口导航失败时的中文提示：说明失败原因与目标端口，
    /// 并保持可见直到下一次语言切换成功，避免失败被静默掩盖。
    /// </summary>
    private void ShowLanguageSwitchFailedHint(string port, CoreWebView2WebErrorStatus status)
    {
        _languageSwitchFailed = true;
        ShowTopHint($"界面语言切换失败：服务语言端口不可达（{port}），请检查服务状态后重试。（{status}）");
    }

    /// <summary>
    /// 语言切换成功后撤掉失败提示；若服务本身存在“切换不可用”提示则恢复该提示。
    /// </summary>
    private void ClearLanguageSwitchFailedHint()
    {
        if (!_languageSwitchFailed) return;
        _languageSwitchFailed = false;
        if (_languageUnavailableNotice)
            ShowTopHint(LanguageUnavailableHint);
        else
            LabLoading.Visibility = System.Windows.Visibility.Collapsed;
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

    /// <summary>
    /// 注入界面语言切换脚本：在编辑器 文件 → 设置 → 拼写和语言 面板中
    /// 添加“界面语言”选项，切换后跳转到对应语言端口的同一路径，
    /// 并通过 postMessage 通知宿主持久化语言偏好。
    /// 语言列表按 <c>EnabledLanguages</c> 配置生成（仅含已启用语言）。
    /// </summary>
    private async Task InjectInterfaceLanguageAsync()
    {
        var raw = GetInterfaceLanguageScript();
        if (string.IsNullOrEmpty(raw)) return;

        var script = raw.Replace("__AYAKA_LANGUAGES_JSON__", BuildLanguagesJson());

        try
        {
            await Web.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(script);
        }
        catch (Exception ex)
        {
            LogWrapper.Warn(ex, "[Ayakaleaf] 界面语言脚本注入失败");
        }
    }

    /// <summary>
    /// 构造注入脚本使用的语言列表 JSON：
    /// <c>[{"code":"en","name":"English","port":8080},…]</c>。
    /// 本地模式：name 取各语言自称，port 按启用顺序分配（en=主端口，其后 +1）；
    /// 访客模式：直接使用加入协作时探测到的语言与本地转发端口。
    /// </summary>
    private string BuildLanguagesJson()
    {
        var sb = new StringBuilder("[");

        if (_isGuestMode)
        {
            var langs = _guestLanguages ?? [];
            for (var i = 0; i < langs.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("{\"code\":\"").Append(langs[i].Code)
                    .Append("\",\"name\":\"").Append(langs[i].Name)
                    .Append("\",\"port\":").Append(langs[i].LocalPort)
                    .Append('}');
            }
            return sb.Append(']').ToString();
        }

        var enabled = AyakaleafService.GetEnabledLanguages();
        for (var i = 0; i < enabled.Count; i++)
        {
            if (i > 0) sb.Append(',');
            var code = enabled[i];
            var native = AyakaleafService.SupportedLanguages
                .Where(l => l.Code == code)
                .Select(l => l.NativeName)
                .FirstOrDefault() ?? code;
            // name/port 均为启动器生成的受控值（语言代码/自称/端口），无引号注入风险
            sb.Append("{\"code\":\"").Append(code)
                .Append("\",\"name\":\"").Append(native)
                .Append("\",\"port\":").Append(Config.Ayakaleaf.Port + i)
                .Append('}');
        }
        return sb.Append(']').ToString();
    }

    private static string? GetInterfaceLanguageScript()
    {
        if (_interfaceLanguageLoaded) return _interfaceLanguageScript;
        _interfaceLanguageLoaded = true;

        try
        {
            using var stream = Basics.GetResourceStream(InterfaceLanguageResource);
            if (stream is null)
            {
                LogWrapper.Warn("[Ayakaleaf] 未找到界面语言脚本资源：" + InterfaceLanguageResource);
                return null;
            }

            using var reader = new StreamReader(stream, Encoding.UTF8);
            _interfaceLanguageScript = reader.ReadToEnd();
        }
        catch (Exception ex)
        {
            LogWrapper.Warn(ex, "[Ayakaleaf] 界面语言脚本加载失败");
        }

        return _interfaceLanguageScript;
    }

    /// <summary>
    /// 接收页面脚本消息：
    /// 1. <c>ayaka:setInterfaceLanguage</c> —— 持久化工作台界面语言偏好，并回发保存结果；
    /// 2. <c>ayaka:langAnchorMissing</c> —— 注入脚本找不到“拼写和语言”设置锚点，记录警告。
    /// </summary>
    private void Web_WebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String) return;

            switch (type.GetString())
            {
                case "ayaka:setInterfaceLanguage":
                    SaveInterfaceLanguage(root);
                    break;
                case "ayaka:langAnchorMissing":
                    LogWrapper.Warn("[Ayakaleaf] 未找到工作台拼写和语言设置锚点，界面语言条目未注入");
                    break;
            }
        }
        catch (Exception ex)
        {
            LogWrapper.Warn(ex, "[Ayakaleaf] 处理工作台消息失败");
        }
    }

    /// <summary>
    /// 持久化界面语言偏好并回发 <c>ayaka:interfaceLanguageSaved</c>。
    /// 回执仅用于日志与可观测性，不是页面跳转的前置条件（跳转由注入脚本自行调度），
    /// 避免网络/线程抖动导致“点了没反应”。
    /// </summary>
    private void SaveInterfaceLanguage(JsonElement root)
    {
        var lang = root.TryGetProperty("lang", out var langElement) && langElement.ValueKind == JsonValueKind.String
            ? langElement.GetString()
            : null;
        // 归一化为镜像内置语言代码：旧脚本值 "zh" 兼容映射 "zh-CN"，未知回落 "en"
        var normalized = AyakaleafService.NormalizeLanguageCode(lang);

        var ok = true;
        try
        {
            // 访客模式不写本地配置：该偏好描述的是房主服务的语言，与本地服务无关
            if (!_isGuestMode)
                Config.Ayakaleaf.InterfaceLanguage = normalized;
        }
        catch (Exception ex)
        {
            ok = false;
            LogWrapper.Warn(ex, "[Ayakaleaf] 保存工作台界面语言失败");
        }

        if (ok)
            LogWrapper.Info($"[Ayakaleaf] 工作台界面语言已保存：{normalized}（下次打开工作台沿用该语言）");

        // 无论持久化是否成功，脚本都会跳转语言端口：失败回执不能阻止跳转，
        // 因此仍需记录“待确认的语言切换”，在导航失败时给出明确提示。
        TrackPendingLanguageSwitch(normalized);
        ReplyInterfaceLanguageSaved(normalized, ok);
    }

    /// <summary>
    /// 标记一次待确认的语言端口切换，用于在导航失败时判断失败是否由语言切换引起。
    /// 目标端口与当前地址端口一致时（注入脚本不会跳转）不标记，避免误报。
    /// </summary>
    private void TrackPendingLanguageSwitch(string lang)
    {
        var targetPort = PortForLanguageOrGuest(lang);
        // CoreWebView2.Source 会跟随页面内导航，比控件的 Source 属性更可靠
        string? currentUrl = null;
        try
        {
            currentUrl = Web.CoreWebView2?.Source;
        }
        catch
        {
            // WebView 可能已被释放：按地址未知处理，仅在导航失败时才会用到该标记
        }

        if (string.IsNullOrEmpty(currentUrl)) currentUrl = Web.Source?.ToString();

        if (!Uri.TryCreate(currentUrl, UriKind.Absolute, out var currentUri))
        {
            // 地址未知时保守标记，仅在导航失败时才会给出提示
            _pendingLanguageSwitch = true;
            _pendingLanguageTargetUrl = $"http://localhost:{targetPort}";
            return;
        }

        // 已在目标端口：注入脚本不会跳转，无需等待导航结果
        if (currentUri.Port == targetPort) return;

        // 注入脚本按“同路径换端口”跳转，据此推导出本次切换的目标地址
        _pendingLanguageSwitch = true;
        _pendingLanguageTargetUrl = new UriBuilder(currentUri) { Port = targetPort }.Uri.ToString();
    }

    /// <summary>
    /// 向页面回发语言偏好保存结果。
    /// </summary>
    private void ReplyInterfaceLanguageSaved(string lang, bool ok)
    {
        var core = Web.CoreWebView2;
        if (core is null) return;

        try
        {
            core.PostWebMessageAsJson(JsonSerializer.Serialize(new
            {
                type = "ayaka:interfaceLanguageSaved",
                lang,
                ok,
            }));
        }
        catch (Exception ex)
        {
            LogWrapper.Warn(ex, "[Ayakaleaf] 回发界面语言保存结果失败");
        }
    }

    /// <summary>
    /// 查询界面语言对应的访问端口：本地模式按服务语言端口，
    /// 访客模式按协作转发端口（未知语言回落主端口）。
    /// </summary>
    private int PortForLanguageOrGuest(string lang)
    {
        if (_isGuestMode)
        {
            var match = _guestLanguages?.FirstOrDefault(l =>
                string.Equals(l.Code, lang, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match.LocalPort;
            return _guestLanguages is { Count: > 0 } ? _guestLanguages[0].LocalPort : Config.Ayakaleaf.Port;
        }
        return AyakaleafService.PortForLanguage(lang);
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
        // CoreWebView2.Source 会跟随页面内导航；失败时可能为空，回退到控件上设置的地址
        var uri = Web.CoreWebView2?.Source ?? Web.Source?.ToString() ?? "";

        // 1. 加载失败：绝不静默；语言端口切换失败时额外给出用户可见的中文提示
        if (!e.IsSuccess)
        {
            // 语言切换导航失败时以目标地址为准（失败后 CoreWebView2.Source 可能仍是旧页面）
            var failedUrl = _pendingLanguageSwitch && !string.IsNullOrEmpty(_pendingLanguageTargetUrl)
                ? _pendingLanguageTargetUrl
                : uri;
            LogWrapper.Warn($"[Ayakaleaf] 工作台页面加载失败（{e.WebErrorStatus}）：{failedUrl}");

            if (_pendingLanguageSwitch)
            {
                var targetUrl = _pendingLanguageTargetUrl ?? "未知地址";
                var port = Uri.TryCreate(targetUrl, UriKind.Absolute, out var targetUri)
                    ? targetUri.Port.ToString()
                    : "未知端口";
                _pendingLanguageSwitch = false;
                _pendingLanguageTargetUrl = null;
                LogWrapper.Warn($"[Ayakaleaf] 界面语言切换失败：目标语言端口不可达（{targetUrl}），错误状态 {e.WebErrorStatus}");
                ShowLanguageSwitchFailedHint(port, e.WebErrorStatus);
            }

            return;
        }

        // 2. 加载成功：记录结果；若本次导航正是语言切换的目标则确认完成并撤掉失败提示
        LogWrapper.Info($"[Ayakaleaf] 工作台页面加载完成：{uri}");
        if (_pendingLanguageSwitch)
        {
            _pendingLanguageSwitch = false;
            _pendingLanguageTargetUrl = null;
            ClearLanguageSwitchFailedHint();
        }

        // 3. 会话失效重试（既有逻辑）；访客模式无本地账号，不自动重登
        try
        {
            if (_isGuestMode || !uri.Contains("/login") || _retryCount >= 2) return;

            // 会话失效：重新登录并注入，最多重试 2 次
            _retryCount++;
            _sid = await AyakaleafAccountService.LoginAsync(_account);
            InjectSession();
            Web.Source = new Uri(InitialUrl);
        }
        catch (Exception ex)
        {
            // 续期失败必须可见：无论是否达到重试上限都展示原因（Message 为账号服务的分类中文文案），
            // 否则工作台会静默停在登录页，用户无从得知是服务未启动、账号密码失效还是本地用户缺失。
            LogWrapper.Warn(ex, "[Ayakaleaf] 工作台会话续期失败");
            LabLoading.Text = "自动登录失败：" + ex.Message;
            LabLoading.Visibility = System.Windows.Visibility.Visible;
        }
    }
}
