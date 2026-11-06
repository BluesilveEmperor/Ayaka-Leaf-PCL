namespace PCL.Core.App;

/// <summary>
/// 文件下载使用的 HTTP 协议模式。
/// </summary>
public enum DownloadHttpMode
{
    Auto = 0,
    Http11 = 1,
    Http2 = 2
}

/// <summary>
/// 主题模式（亮/暗/系统）
/// </summary>
public enum ColorMode
{
    Light = 0,
    Dark = 1,
    System = 2
}

/// <summary>
/// 配色主题
/// </summary>
public enum ColorTheme
{
    SkyBlue = 0,
    CatBlue = 1,
    DeathBlue = 2,
    HmclBlue = 3
}

/// <summary>
/// 更新通道
/// </summary>
public enum UpdateChannel
{
    Release = 0,
    Beta = 1,
    Dev = 2
}

/// <summary>
/// 启动器更新行为
/// </summary>
public enum LauncherAutoUpdateBehavior
{
    DownloadAndInstall = 0,
    DownloadAndAnnounce = 1,
    AnnounceOnly = 2,
    Disable = 3
}

public enum LauncherTitleType
{
    None = 0,
    Default = 1,
    Text = 2,
    Image = 3
}

/// <summary>
/// Ayakaleaf 服务端公开注册策略。
/// </summary>
public enum AyakaleafRegistrationMode
{
    /// <summary>开放注册：任何人可通过邮箱自助注册（OVERLEAF_ALLOW_PUBLIC_REGISTRATION=true）。</summary>
    Open = 0,

    /// <summary>域名限制：仅允许指定邮箱域名的用户注册（激活邮件方式，需要 SMTP；当前启动器未配置邮件发送）。</summary>
    DomainList = 1,

    /// <summary>关闭公开注册：仅既有账号与管理员可登录（不注入注册变量）。</summary>
    Closed = 2
}
