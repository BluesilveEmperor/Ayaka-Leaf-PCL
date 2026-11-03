using System.Text.RegularExpressions;

namespace PCL.Core.Utils;

/// <summary>
/// 基于代码生成优化的正则表达式实例。
/// </summary>
public static partial class RegexPatterns
{
    /// <summary>
    /// 换行符，包括 <c>\r\n</c> <c>\n</c> <c>\r</c> 三种。
    /// </summary>
    public static readonly Regex NewLine = _NewLine();
    [GeneratedRegex(@"\r\n|\n|\r")]
    private static partial Regex _NewLine();

    /// <summary>
    /// ISO 8601 当日终点时间写法 24:00(:00)(.0…)，语义等于次日零点。
    /// </summary>
    public static readonly Regex Iso8601EndOfDay = _Iso8601EndOfDay();
    [GeneratedRegex(@"[Tt]24:00(?::00(?:\.0+)?)?(?=[Zz+\-]|$)")]
    private static partial Regex _Iso8601EndOfDay();

    /// <summary>
    /// Semantic Versioning (SemVer) 规范的版本号，包含可选的 v 前缀。
    /// </summary>
    public static readonly Regex SemVer = _SemVer();
    private const string PatternSemVer =
        @"^v?(?<major>0|[1-9]\d*)\.(?<minor>0|[1-9]\d*)\.(?<patch>0|[1-9]\d*)" +
        @"(?:-(?<prerelease>(?:0|[1-9]\d*|\d*[a-zA-Z-][0-9a-zA-Z-]*)(?:\.(?:0|[1-9]\d*|\d*[a-zA-Z-][0-9a-zA-Z-]*))*))?" +
        @"(?:\+(?<build>[0-9a-zA-Z-]+(?:\.[0-9a-zA-Z-]+)*))?$";
    [GeneratedRegex(PatternSemVer, RegexOptions.ExplicitCapture)]
    private static partial Regex _SemVer();

    /// <summary>
    /// 简单匹配 HTTP(S) URI，若需严格检查请使用 <see cref="FullHttpUri"/>。
    /// </summary>
    public static readonly Regex HttpUri = _HttpUri();
    private const string PatternHttpUri = @"^https?://(?:\[[^\]\s]+\]|[^/\s?#:]+)(?::\d{1,5})?(?:/[^\s?#]*)?(?:\?[^\s#]*)?(?:#\S*)?$";
    [GeneratedRegex(PatternHttpUri, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex _HttpUri();

    /// <summary>
    /// 包含完整规则的 HTTP(S) URI，含有 <c>scheme</c> <c>host</c> <c>ipv6</c>
    /// <c>port</c> <c>path</c> <c>query</c> <c>fragment</c> 分组。
    /// </summary>
    public static readonly Regex FullHttpUri = _FullHttpUri();
    private const string PatternFullHttpUri =
        @"^(?<scheme>https?)://(?<host>localhost|(?:(?:25[0-5]|2[0-4]\d|1?\d?\d)(?:\.(?:25[0-5]|2[0-4]\d|1?\d?\d)){3})" +
        @"|\[(?<ipv6>[0-9A-Fa-f:.]+)\]|(?:(?:[A-Za-z0-9](?:[A-Za-z0-9\-]{0,61}[A-Za-z0-9])?\.)+(?:[A-Za-z]{2,63}|xn--[" +
        @"A-Za-z0-9\-]{2,59})))(?::(?<port>6553[0-5]|655[0-2]\d|65[0-4]\d{2}|6[0-4]\d{3}|[1-5]\d{4}|[1-9]\d{0,3}))?" +
        @"(?<path>/[^\s?#]*)?(?:\?(?<query>[^\s#]*))?(?:#(?<fragment>[^\s]*))?$";
    [GeneratedRegex(PatternFullHttpUri, RegexOptions.ExplicitCapture | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex _FullHttpUri();

    /// <summary>
    /// LastPending(_Xxx).log 路径。
    /// </summary>
    public static readonly Regex LastPendingLogPath = _LastPendingLogPath();
    [GeneratedRegex(@"\\LastPending[_]?[^\\]*\.log$", RegexOptions.IgnoreCase)]
    private static partial Regex _LastPendingLogPath();

    /// <summary>
    /// 使用 IsMatch 检查是否存在中文字符
    /// </summary>
    public static readonly Regex HasChineseChar = _HasChineseChar();
    [GeneratedRegex("[\u4e00-\u9fbb]")]
    private static partial Regex _HasChineseChar();

    /// <summary>
    /// 用 Replace 替换英文中的分隔特征
    /// </summary>
    public static readonly Regex EnglishSpacedKeywords = _EnglishSpacedKeywords();
    [GeneratedRegex("([A-Z]+|[a-z]+?)(?=[A-Z]+[a-z]+[a-z ]*)")]
    private static partial Regex _EnglishSpacedKeywords();

    /// <summary>
    /// NTFS 8.3 文件名格式
    /// </summary>
    public static readonly Regex Ntfs83FileName = _Ntfs83FileName();
    [GeneratedRegex(@".{2,}~\d")]
    private static partial Regex _Ntfs83FileName();

    public static readonly Regex UncPath = _UncPath();
    [GeneratedRegex("""^\\\\[^\\/:*?"<>|]+\\[^\\/:*?"<>|]+(\\[^\\/:*?"<>|]+)*\\?$""")]
    private static partial Regex _UncPath();
}
