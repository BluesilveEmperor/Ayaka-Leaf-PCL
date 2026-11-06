using PCL.Core.App.Configuration;

namespace PCL.Core.App;

/// <summary>
/// 全局配置类。
/// </summary>
// ReSharper disable InconsistentNaming
public static partial class Config
{
    /// <summary>
    /// 系统配置。
    /// </summary>
    [ConfigGroup("System")] partial class SystemConfigGroup
    {
        // /// <summary>
        // /// 系统缓存目录。
        // /// </summary>
        // [ConfigItem<string>("SystemSystemCache", "")] public partial string CacheDirectory { get; set; }

        /// <summary>
        /// 禁用硬件加速。
        /// </summary>
        [ConfigItem<bool>("SystemDisableHardwareAcceleration", false)] public partial bool DisableHardwareAcceleration { get; set; }

        /// <summary>
        /// 遥测。
        /// </summary>
        [ConfigItem<bool>("SystemTelemetry", false)] public partial bool Telemetry { get; set; }

        /// <summary>
        /// 实时日志最大行数。
        /// </summary>
        [ConfigItem<int>("SystemMaxLog", 13)] public partial int MaxGameLog { get; set; }

        /// <summary>
        /// 动画帧率上限。
        /// </summary>
        [ConfigItem<int>("UiAniFPS", 59)] public partial int AnimationFpsLimit { get; set; }

        [ConfigItem<string>("Profile", "{\"lastUsed\":-1,\"profiles\":[]}", ConfigSource.SharedEncrypt)] public partial string Profiles { get; set; }

    }

    /// <summary>
    /// 网络配置。
    /// </summary>
    [ConfigGroup("Network")] partial class NetworkConfigGroup
    {
        [ConfigItem<bool>("SystemNetEnableDoH", true)] public partial bool EnableDoH { get; set; }

        [ConfigGroup("HttpProxy")] partial class HttpProxyConfigGroup
        {
            [ConfigItem<string>("SystemHttpProxy", "", ConfigSource.SharedEncrypt)] public partial string CustomAddress { get; set; }
            [ConfigItem<int>("SystemHttpProxyType", 1)] public partial int Type { get; set; }
            [ConfigItem<string>("SystemHttpProxyCustomUsername", "")] public partial string CustomUsername { get; set; }
            [ConfigItem<string>("SystemHttpProxyCustomPassword", "")] public partial string CustomPassword { get; set; }
        }
    }

    /// <summary>
    /// 调试配置
    /// </summary>
    [ConfigGroup("Debug")] partial class DebugConfigGroup
    {
        [ConfigItem<bool>("SystemDebugMode", false)] public partial bool Enabled { get; set; }
        [ConfigItem<int>("SystemDebugAnim", 9)] public partial int AnimationSpeed { get; set; }
        [ConfigItem<bool>("SystemDebugDelay", false)] public partial bool AddRandomDelay { get; set; }
        [ConfigItem<bool>("SystemDebugSkipCopy", false)] public partial bool DontCopy { get; set; }
        [ConfigItem<bool>("SystemDebugAllowRestrictedFeature", false)] public partial bool AllowRestrictedFeature { get; set; }
    }

    /// <summary>
    /// 下载配置。
    /// </summary>
    [ConfigGroup("Download")] partial class DownloadConfigGroup
    {
        // 保留原配置键，升级后仍沿用用户已设置的数值（值 + 1 为连接上限）。
        [ConfigItem<int>("ToolDownloadThread", 63)] public partial int ThreadLimit { get; set; }
        [ConfigItem<int>("ToolDownloadFileConnection", 7)] public partial int FileConnectionLimit { get; set; }
        [ConfigItem<int>("ToolDownloadSpeed", 42)] public partial int SpeedLimit { get; set; }
        [ConfigItem<DownloadHttpMode>("ToolDownloadHttpMode", DownloadHttpMode.Auto)] public partial DownloadHttpMode HttpMode { get; set; }
    }

    /// <summary>
    /// 更新配置。
    /// </summary>
    [ConfigGroup("Update")] partial class UpdateConfigGroup
    {
        /// <summary>
        /// 自动更新行为。
        /// </summary>
        [ConfigItem<LauncherAutoUpdateBehavior>("SystemSystemUpdate", LauncherAutoUpdateBehavior.DownloadAndAnnounce, ConfigSource.Local)] public partial LauncherAutoUpdateBehavior UpdateMode { get; set; }

        /// <summary>
        /// 更新分支。
        /// </summary>
        [ConfigItem<UpdateChannel>("SystemUpdateChannel", UpdateChannel.Release, ConfigSource.Local)] public partial UpdateChannel UpdateChannel { get; set; }
            
        /// <summary>
        /// Mirror 酱 CDK。
        /// </summary>
        [ConfigItem<string>("SystemMirrorChyanKey", "", ConfigSource.SharedEncrypt)] public partial string MirrorChyanKey { get; set; }
    }

    /// <summary>
    /// 个性化配置。
    /// </summary>
    [ConfigGroup("Preference")] partial class PreferenceConfigGroup
    {
        /// <summary>
        /// 启动时显示 Logo。
        /// </summary>
        [ConfigItem<bool>("UiLauncherLogo", true, ConfigSource.Local)] public partial bool ShowStartupLogo { get; set; }

        /// <summary>
        /// 锁定窗口大小。
        /// </summary>
        [ConfigItem<bool>("UiLockWindowSize", false)] public partial bool LockWindowSize { get; set; }

        /// <summary>
        /// 在启动游戏时显示你知道吗。
        /// </summary>
        [ConfigItem<bool>("UiShowLaunchingHint", true, ConfigSource.Local)] public partial bool ShowLaunchingHint { get; set; }

        /// <summary>
        /// 标题内容类型。
        /// </summary>
        [ConfigItem<LauncherTitleType>("UiLogoType", LauncherTitleType.Default, ConfigSource.Local)] public partial LauncherTitleType WindowTitleType { get; set; }

        /// <summary>
        /// 窗口标题文本。
        /// </summary>
        [ConfigItem<string>("UiLogoText", "Ayaka", ConfigSource.Local)] public partial string WindowTitleCustomText { get; set; }

        /// <summary>
        /// 导航栏居左。
        /// </summary>
        [ConfigItem<bool>("UiLogoLeft", false, ConfigSource.Local)] public partial bool TopBarLeftAlign { get; set; }

        /// <summary>
        /// 全局字体。
        /// </summary>
        [ConfigItem<string>("UiFont", "", ConfigSource.Local)] public partial string Font { get; set; }

        /// <summary>
        /// 本地化配置。
        /// </summary>
        [ConfigGroup("Localization", ConfigSource.Local)] partial class LocalizationConfigGroup
        {
            /// <summary>
            /// UI 语言。auto 表示跟随系统语言。
            /// </summary>
            [ConfigItem<string>("UiLanguage", "auto")] public partial string Language { get; set; }

            /// <summary>
            /// UI 展示格式所使用的区域性。auto 表示跟随系统区域格式。
            /// </summary>
            [ConfigItem<string>("UiFormatCulture", "auto")] public partial string FormatCulture { get; set; }

            /// <summary>
            /// 区域覆盖。auto 表示自动判断。
            /// </summary>
            [ConfigItem<string>("UiRegion", "auto")] public partial string Region { get; set; }
        }

        /// <summary>
        /// 界面主题配置。
        /// </summary>
        [ConfigGroup("Theme")] partial class ThemeConfigGroup
        {
            /// <summary>
            /// 配色主题模式。
            /// </summary>
            [ConfigItem<ColorMode>("UiDarkMode", ColorMode.System)] public partial ColorMode ColorMode { get; set; }

            /// <summary>
            /// 暗色配色主题。
            /// </summary>
            [ConfigItem<ColorTheme>("UiDarkColor", ColorTheme.CatBlue)] public partial ColorTheme DarkColor { get; set; }

            /// <summary>
            /// 亮色配色主题。
            /// </summary>
            [ConfigItem<ColorTheme>("UiLightColor", ColorTheme.CatBlue)] public partial ColorTheme LightColor { get; set; }

            /// <summary>
            /// 窗口透明度。
            /// </summary>
            [ConfigItem<int>("UiLauncherTransparent", 600, ConfigSource.Local)] public partial int WindowOpacity { get; set; }

            /// <summary>
            /// 自定义主题：色相 (H)。
            /// </summary>
            [ConfigItem<int>("UiLauncherHue", 180, ConfigSource.Local)] public partial int WindowHue { get; set; }

            /// <summary>
            /// 自定义主题：饱和度 (S)。
            /// </summary>
            [ConfigItem<int>("UiLauncherSat", 80, ConfigSource.Local)] public partial int WindowSat { get; set; }

            /// <summary>
            /// 自定义主题：明度 (L)。
            /// </summary>
            [ConfigItem<int>("UiLauncherLight", 20, ConfigSource.Local)] public partial int WindowLight { get; set; }

            /// <summary>
            /// 自定义主题：色相渐变。
            /// </summary>
            [ConfigItem<int>("UiLauncherDelta", 90, ConfigSource.Local)] public partial int WindowDelta { get; set; }

            /// <summary>
            /// 传说中的主题选择，但是没卵用。
            /// </summary>
            [ConfigItem<int>("UiLauncherTheme", 0, ConfigSource.Local)] public partial int ThemeSelected { get; set; }
        }

        /// <summary>
        /// 背景内容。
        /// </summary>
        [ConfigGroup("Background")] partial class BackgroundConfigGroup
        {
            /// <summary>
            /// 彩色底部填充。
            /// </summary>
            [ConfigItem<bool>("UiBackgroundColorful", true, ConfigSource.Local)] public partial bool BackgroundColorful { get; set; }

            /// <summary>
            /// 透明度。
            /// </summary>
            [ConfigItem<int>("UiBackgroundOpacity", 1000, ConfigSource.Local)] public partial int WallpaperOpacity { get; set; }

            /// <summary>
            /// 旋转。
            /// </summary>
            [ConfigItem<int>("UiBackgroundCarousel", 1000, ConfigSource.Local)] public partial int WallpaperCarousel { get; set; }

            /// <summary>
            /// 模糊遮罩。
            /// </summary>
            [ConfigItem<int>("UiBackgroundBlur", 0, ConfigSource.Local)] public partial int WallpaperBlurRadius { get; set; }

            /// <summary>
            /// 内容裁剪模式。
            /// </summary>
            [ConfigItem<int>("UiBackgroundSuit", 0, ConfigSource.Local)] public partial int WallpaperSuitMode { get; set; }

            /// <summary>
            /// 视频自动暂停。
            /// </summary>
            [ConfigItem<bool>("UiAutoPauseVideo", true, ConfigSource.Local)] public partial bool AutoPauseVideo { get; set; }
        }

        /// <summary>
        /// 高级材质。
        /// </summary>
        [ConfigGroup("Blur")] partial class BlurConfigGroup
        {
            /// <summary>
            /// 是否启用。
            /// </summary>
            [ConfigItem<bool>("UiBlur", false, ConfigSource.Local)] public partial bool IsEnabled { get; set; }

            /// <summary>
            /// 模糊半径。
            /// </summary>
            [ConfigItem<int>("UiBlurValue", 16, ConfigSource.Local)] public partial int Radius { get; set; }

            /// <summary>
            /// 采样率。
            /// </summary>
            [ConfigItem<int>("UiBlurSamplingRate", 70, ConfigSource.Local)] public partial int SamplingRate { get; set; }

            /// <summary>
            /// 模糊方法。
            /// </summary>
            [ConfigItem<int>("UiBlurType", 0, ConfigSource.Local)] public partial int KernelType { get; set; }
        }

        /// <summary>
        /// 自定义主页。
        /// </summary>
        [ConfigGroup("Homepage")] partial class HomepageConfigGroup
        {
            /// <summary>
            /// 主页来源类型。
            /// </summary>
            [ConfigItem<int>("UiCustomType", 0, ConfigSource.Local)] public partial int Type { get; set; }

            /// <summary>
            /// 预设选项。
            /// </summary>
            [ConfigItem<int>("UiCustomPreset", 0, ConfigSource.Local)] public partial int SelectedPreset { get; set; }

            /// <summary>
            /// 自定义 URL。
            /// </summary>
            [ConfigItem<string>("UiCustomNet", "", ConfigSource.Local)] public partial string CustomUrl { get; set; }
        }

        /// <summary>
        /// 背景音乐。
        /// </summary>
        [ConfigGroup("Music")] partial class MusicConfigGroup
        {
            /// <summary>
            /// 音量。
            /// </summary>
            [ConfigItem<int>("UiMusicVolume", 500, ConfigSource.Local)] public partial int Volume { get; set; }

            /// <summary>
            /// 启动游戏后自动暂停。
            /// </summary>
            [ConfigItem<bool>("UiMusicStop", false, ConfigSource.Local)] public partial bool StopInGame { get; set; }

            /// <summary>
            /// 启动游戏后自动开始播放。
            /// </summary>
            [ConfigItem<bool>("UiMusicStart", false, ConfigSource.Local)] public partial bool StartInGame { get; set; }

            /// <summary>
            /// 自动开始播放。
            /// </summary>
            [ConfigItem<bool>("UiMusicAuto", true, ConfigSource.Local)] public partial bool StartOnStartup { get; set; }

            /// <summary>
            /// 随机播放。
            /// </summary>
            [ConfigItem<bool>("UiMusicRandom", true, ConfigSource.Local)] public partial bool ShufflePlayback { get; set; }

            /// <summary>
            /// 启用 SMTC。
            /// </summary>
            [ConfigItem<bool>("UiMusicSMTC", true, ConfigSource.Local)] public partial bool EnableSMTC { get; set; }
        }

        /// <summary>
        /// 功能隐藏。
        /// </summary>
        [ConfigGroup("Hide")]
        partial class HideConfigGroup
        {
            // 主页面
            [ConfigItem<bool>("UiHiddenPageDownload", false, ConfigSource.Local)] public partial bool PageDownload { get; set; }
            [ConfigItem<bool>("UiHiddenPageSetup", false, ConfigSource.Local)] public partial bool PageSetup { get; set; }
            [ConfigItem<bool>("UiHiddenPageTools", false, ConfigSource.Local)] public partial bool PageTools { get; set; }

            // 子页面 设置
            [ConfigItem<bool>("UiHiddenSetupUi", false, ConfigSource.Local)] public partial bool SetupUi { get; set; }
            [ConfigItem<bool>("UiHiddenSetupLauncherLanguage", false, ConfigSource.Local)] public partial bool SetupLauncherLanguage { get; set; }
            [ConfigItem<bool>("UiHiddenSetupLauncherMisc", false, ConfigSource.Local)] public partial bool SetupLauncherMisc { get; set; }
            [ConfigItem<bool>("UiHiddenSetupUpdate", false, ConfigSource.Local)] public partial bool SetupUpdate { get; set; }
            [ConfigItem<bool>("UiHiddenSetupAbout", false, ConfigSource.Local)] public partial bool SetupAbout { get; set; } // 修正名称
            [ConfigItem<bool>("UiHiddenSetupFeedback", false, ConfigSource.Local)] public partial bool SetupFeedback { get; set; } // 修正名称
            [ConfigItem<bool>("UiHiddenSetupLog", false, ConfigSource.Local)] public partial bool SetupLog { get; set; } // 修正名称

            // 特定功能
            [ConfigItem<bool>("UiHiddenFunctionHidden", false, ConfigSource.Local)] public partial bool FunctionHidden { get; set; }
        }
    }

    /// <summary>
    /// Ayakaleaf 服务栈配置。
    /// </summary>
    [ConfigGroup("Ayakaleaf")] partial class AyakaleafConfigGroup
    {
        /// <summary>
        /// 对外服务端口（nginx 映射到宿主的端口）。
        /// </summary>
        [ConfigItem<int>("AyakaleafPort", 8080, ConfigSource.Local)] public partial int Port { get; set; }

        /// <summary>
        /// 探活路径。
        /// </summary>
        [ConfigItem<string>("AyakaleafProbePath", "/login", ConfigSource.Local)] public partial string ProbePath { get; set; }

        /// <summary>
        /// 内嵌运行时是否已完成部署。
        /// </summary>
        [ConfigItem<bool>("AyakaleafDeployed", false, ConfigSource.Local)] public partial bool Deployed { get; set; }

        /// <summary>
        /// 工作台界面语言（"en" = English，"zh" = 简体中文），在编辑器设置面板中切换。
        /// </summary>
        [ConfigItem<string>("AyakaleafInterfaceLanguage", "en", ConfigSource.Local)] public partial string InterfaceLanguage { get; set; }

        /// <summary>
        /// 是否允许局域网设备访问（团队协作）。开启后端口绑定 0.0.0.0，
        /// 同一局域网内的队友可通过本机 IP 访问并注册账号；关闭时仅本机可访问。
        /// 修改后重启服务生效（容器会按新配置自动重建）。
        /// </summary>
        [ConfigItem<bool>("AyakaleafAllowLanAccess", true, ConfigSource.Local)] public partial bool AllowLanAccess { get; set; }

        /// <summary>
        /// 项目邀请令牌密钥。首次使用时随机生成并持久化，之后重建容器复用同一密钥，
        /// 已发出的邀请链接不失效。
        /// </summary>
        [ConfigItem<string>("AyakaleafInviteTokenSecret", "", ConfigSource.Local)] public partial string InviteTokenSecret { get; set; }

        /// <summary>
        /// 最近一次创建 overleaf 容器使用的配置签名（端口绑定、站点地址、语言映射、邀请密钥、功能指纹）。
        /// 启动时与期望签名比对，不一致则自动重建容器（数据卷不受影响）。
        /// </summary>
        [ConfigItem<string>("AyakaleafContainerProfile", "", ConfigSource.Local)] public partial string ContainerProfile { get; set; }

        // ---------------- 镜像 ----------------

        /// <summary>
        /// Overleaf 服务端镜像（repo:tag）。留空使用内置默认值。
        /// 修改后重启服务生效（容器按新镜像重建，数据卷保留）。
        /// </summary>
        [ConfigItem<string>("AyakaleafOverleafImage", "", ConfigSource.Local)] public partial string OverleafImage { get; set; }

        // ---------------- Git-Bridge / MCP ----------------

        /// <summary>
        /// 是否启用 Git-Bridge（git clone/pull/push 项目）与 MCP Server。
        /// 需要部署 git-bridge 容器（runtime 镜像包）；修改后重启服务生效。
        /// </summary>
        [ConfigItem<bool>("AyakaleafEnableGitBridge", false, ConfigSource.Local)] public partial bool EnableGitBridge { get; set; }

        // ---------------- 远程协作 ----------------

        /// <summary>
        /// 远程协作是否隐藏真实 IP：不监听、不直连，全部流量经公共节点中继。
        /// </summary>
        [ConfigItem<bool>("AyakaleafCollabHideIp", false, ConfigSource.Local)] public partial bool CollabHideIp { get; set; }

        /// <summary>
        /// 远程协作是否仅允许 P2P 直连（不经过公共节点中继兜底）。
        /// </summary>
        [ConfigItem<bool>("AyakaleafCollabDirectOnly", false, ConfigSource.Local)] public partial bool CollabDirectOnly { get; set; }

        /// <summary>
        /// 远程协作自定义节点：逗号分隔的节点 URL（tcp://…、udp://…、wss://…、https://…），
        /// 非空时追加到内置公共节点之后。
        /// </summary>
        [ConfigItem<string>("AyakaleafCollabCustomNodes", "", ConfigSource.Local)] public partial string CollabCustomNodes { get; set; }

        // ---------------- AI ----------------

        /// <summary>
        /// 是否启用 AI Chat 与 Error Assistant。需要配置 AI 网关地址、密钥与模型。
        /// </summary>
        [ConfigItem<bool>("AyakaleafEnableAi", false, ConfigSource.Local)] public partial bool EnableAi { get; set; }

        /// <summary>AI 网关地址（OpenAI 兼容）。</summary>
        [ConfigItem<string>("AyakaleafAiBaseUrl", "", ConfigSource.Local)] public partial string AiBaseUrl { get; set; }

        /// <summary>AI 网关 API 密钥。</summary>
        [ConfigItem<string>("AyakaleafAiApiKey", "", ConfigSource.SharedEncrypt)] public partial string AiApiKey { get; set; }

        /// <summary>默认对话模型。</summary>
        [ConfigItem<string>("AyakaleafAiModel", "", ConfigSource.Local)] public partial string AiModel { get; set; }

        /// <summary>图片对话模型（留空回落默认模型）。</summary>
        [ConfigItem<string>("AyakaleafAiImageModel", "", ConfigSource.Local)] public partial string AiImageModel { get; set; }

        /// <summary>PDF 附件对话模型（留空回落默认模型）。</summary>
        [ConfigItem<string>("AyakaleafAiPdfModel", "", ConfigSource.Local)] public partial string AiPdfModel { get; set; }

        /// <summary>每用户 AI Token 配额（0 = 不限制）。</summary>
        [ConfigItem<int>("AyakaleafAiTokenQuota", 0, ConfigSource.Local)] public partial int AiTokenQuota { get; set; }

        // ---------------- 模板画廊 ----------------

        /// <summary>是否启用模板画廊（/templates 导航入口）。</summary>
        [ConfigItem<bool>("AyakaleafEnableTemplateGallery", false, ConfigSource.Local)] public partial bool EnableTemplateGallery { get; set; }

        /// <summary>模板归属用户 ID（ayakaleaf 服务端用户 ObjectId；留空使用镜像默认值）。</summary>
        [ConfigItem<string>("AyakaleafTemplatesUserId", "", ConfigSource.Local)] public partial string TemplatesUserId { get; set; }

        /// <summary>模板画廊分类（空格分隔 key，如 "academic-journal book presentation"；默认 all）。</summary>
        [ConfigItem<string>("AyakaleafTemplateCategories", "", ConfigSource.Local)] public partial string TemplateCategories { get; set; }

        // ---------------- GitHub / GitLab / Gitea 同步 ----------------

        /// <summary>是否启用 GitHub 双向同步（需先启用 Git-Bridge 并填写 OAuth 应用凭据）。</summary>
        [ConfigItem<bool>("AyakaleafEnableGithubSync", false, ConfigSource.Local)] public partial bool EnableGithubSync { get; set; }

        /// <summary>GitHub 站点地址（GitHub.com 或 GitHub Enterprise）。</summary>
        [ConfigItem<string>("AyakaleafGithubSyncUrl", "https://github.com", ConfigSource.Local)] public partial string GithubSyncUrl { get; set; }

        /// <summary>GitHub OAuth 应用 Client ID。</summary>
        [ConfigItem<string>("AyakaleafGithubClientId", "", ConfigSource.Local)] public partial string GithubClientId { get; set; }

        /// <summary>GitHub OAuth 应用 Client Secret。</summary>
        [ConfigItem<string>("AyakaleafGithubClientSecret", "", ConfigSource.SharedEncrypt)] public partial string GithubClientSecret { get; set; }

        /// <summary>GitHub 用户 token 加密密钥（留空自动生成并持久化）。</summary>
        [ConfigItem<string>("AyakaleafGithubTokenCipherPassword", "", ConfigSource.SharedEncrypt)] public partial string GithubTokenCipherPassword { get; set; }

        /// <summary>是否启用 GitLab 双向同步。</summary>
        [ConfigItem<bool>("AyakaleafEnableGitlabSync", false, ConfigSource.Local)] public partial bool EnableGitlabSync { get; set; }

        /// <summary>GitLab 站点地址（GitLab.com 或自建）。</summary>
        [ConfigItem<string>("AyakaleafGitlabSyncUrl", "https://gitlab.com", ConfigSource.Local)] public partial string GitlabSyncUrl { get; set; }

        /// <summary>GitLab OAuth 应用 Client ID。</summary>
        [ConfigItem<string>("AyakaleafGitlabClientId", "", ConfigSource.Local)] public partial string GitlabClientId { get; set; }

        /// <summary>GitLab OAuth 应用 Client Secret。</summary>
        [ConfigItem<string>("AyakaleafGitlabClientSecret", "", ConfigSource.SharedEncrypt)] public partial string GitlabClientSecret { get; set; }

        /// <summary>GitLab 用户 token 加密密钥（留空自动生成并持久化）。</summary>
        [ConfigItem<string>("AyakaleafGitlabTokenCipherPassword", "", ConfigSource.SharedEncrypt)] public partial string GitlabTokenCipherPassword { get; set; }

        /// <summary>是否启用 Gitea 双向同步。</summary>
        [ConfigItem<bool>("AyakaleafEnableGiteaSync", false, ConfigSource.Local)] public partial bool EnableGiteaSync { get; set; }

        /// <summary>Gitea 站点地址（Gitea.com 或自建 ≥1.23）。</summary>
        [ConfigItem<string>("AyakaleafGiteaSyncUrl", "https://gitea.com", ConfigSource.Local)] public partial string GiteaSyncUrl { get; set; }

        /// <summary>Gitea OAuth 应用 Client ID。</summary>
        [ConfigItem<string>("AyakaleafGiteaClientId", "", ConfigSource.Local)] public partial string GiteaClientId { get; set; }

        /// <summary>Gitea OAuth 应用 Client Secret。</summary>
        [ConfigItem<string>("AyakaleafGiteaClientSecret", "", ConfigSource.SharedEncrypt)] public partial string GiteaClientSecret { get; set; }

        /// <summary>Gitea 用户 token 加密密钥（留空自动生成并持久化）。</summary>
        [ConfigItem<string>("AyakaleafGiteaTokenCipherPassword", "", ConfigSource.SharedEncrypt)] public partial string GiteaTokenCipherPassword { get; set; }

        // ---------------- Zotero / Mendeley ----------------

        /// <summary>是否启用 Zotero 文献关联。</summary>
        [ConfigItem<bool>("AyakaleafEnableZotero", false, ConfigSource.Local)] public partial bool EnableZotero { get; set; }

        /// <summary>Zotero OAuth Client ID。</summary>
        [ConfigItem<string>("AyakaleafZoteroClientId", "", ConfigSource.Local)] public partial string ZoteroClientId { get; set; }

        /// <summary>Zotero OAuth Client Secret。</summary>
        [ConfigItem<string>("AyakaleafZoteroClientSecret", "", ConfigSource.SharedEncrypt)] public partial string ZoteroClientSecret { get; set; }

        /// <summary>是否启用 Mendeley 文献关联。</summary>
        [ConfigItem<bool>("AyakaleafEnableMendeley", false, ConfigSource.Local)] public partial bool EnableMendeley { get; set; }

        /// <summary>Mendeley OAuth Client ID。</summary>
        [ConfigItem<string>("AyakaleafMendeleyClientId", "", ConfigSource.Local)] public partial string MendeleyClientId { get; set; }

        /// <summary>Mendeley OAuth Client Secret。</summary>
        [ConfigItem<string>("AyakaleafMendeleyClientSecret", "", ConfigSource.SharedEncrypt)] public partial string MendeleyClientSecret { get; set; }

        // ---------------- 注册 / 验证码 / 安全 ----------------

        /// <summary>公开注册策略（开放 / 域名限制 / 关闭）。</summary>
        [ConfigItem<AyakaleafRegistrationMode>("AyakaleafRegistrationMode", AyakaleafRegistrationMode.Open, ConfigSource.Local)] public partial AyakaleafRegistrationMode RegistrationMode { get; set; }

        /// <summary>域名限制模式允许的邮箱域列表（逗号分隔，如 "@example.com,@*.example.org"）。</summary>
        [ConfigItem<string>("AyakaleafRegistrationDomains", "", ConfigSource.Local)] public partial string RegistrationDomains { get; set; }

        /// <summary>是否启用 reCAPTCHA（登录/注册/找回密码）。</summary>
        [ConfigItem<bool>("AyakaleafEnableRecaptcha", false, ConfigSource.Local)] public partial bool EnableRecaptcha { get; set; }

        /// <summary>reCAPTCHA Site Key。</summary>
        [ConfigItem<string>("AyakaleafRecaptchaSiteKey", "", ConfigSource.Local)] public partial string RecaptchaSiteKey { get; set; }

        /// <summary>reCAPTCHA Secret Key。</summary>
        [ConfigItem<string>("AyakaleafRecaptchaSecretKey", "", ConfigSource.SharedEncrypt)] public partial string RecaptchaSecretKey { get; set; }

        /// <summary>是否启用泄露密码检查（注册时拒绝已泄露密码）。</summary>
        [ConfigItem<bool>("AyakaleafEnableHibp", false, ConfigSource.Local)] public partial bool EnableHibp { get; set; }

        // ---------------- 品牌化 ----------------

        /// <summary>实例名称（OVERLEAF_APP_NAME，留空不覆盖）。</summary>
        [ConfigItem<string>("AyakaleafAppName", "", ConfigSource.Local)] public partial string AppName { get; set; }

        /// <summary>导航栏标题（OVERLEAF_NAV_TITLE，留空不覆盖）。</summary>
        [ConfigItem<string>("AyakaleafNavTitle", "", ConfigSource.Local)] public partial string NavTitle { get; set; }

        /// <summary>导航栏 Logo 图片 URL（留空不覆盖）。</summary>
        [ConfigItem<string>("AyakaleafHeaderImageUrl", "", ConfigSource.Local)] public partial string HeaderImageUrl { get; set; }

        /// <summary>导航栏 Logo（浅色主题）URL。</summary>
        [ConfigItem<string>("AyakaleafHeaderImageUrlLight", "", ConfigSource.Local)] public partial string HeaderImageUrlLight { get; set; }

        /// <summary>导航栏 Logo（深色主题）URL。</summary>
        [ConfigItem<string>("AyakaleafHeaderImageUrlDark", "", ConfigSource.Local)] public partial string HeaderImageUrlDark { get; set; }

        // ---------------- 高级 / 管理 ----------------

        /// <summary>
        /// 高级环境变量（每行一条 key=value，启动 overleaf 容器时原样注入；
        /// 适用于 SSO（EXTERNAL_AUTH / OVERLEAF_LDAP_* / OVERLEAF_SAML_* / OVERLEAF_OIDC_*）等未提供专用 UI 的配置）。
        /// </summary>
        [ConfigItem<string>("AyakaleafAdvancedEnv", "", ConfigSource.Local)] public partial string AdvancedEnv { get; set; }

        /// <summary>管理员对应的本地用户名（用于开通 ayakaleaf 服务端管理员；留空取首个本地用户）。</summary>
        [ConfigItem<string>("AyakaleafAdminLocalUserName", "", ConfigSource.Local)] public partial string AdminLocalUserName { get; set; }

        /// <summary>
        /// 工作台界面语言列表（逗号分隔 lngCode，如 "en,zh-CN,ja-JP"）。
        /// 第一个语言绑定主端口，其余依次 +1；修改后重启服务生效。
        /// </summary>
        [ConfigItem<string>("AyakaleafEnabledLanguages", "en,zh-CN", ConfigSource.Local)] public partial string EnabledLanguages { get; set; }
    }
}
