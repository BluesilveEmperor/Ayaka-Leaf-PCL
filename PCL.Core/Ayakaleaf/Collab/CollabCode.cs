using System;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace PCL.Core.Ayakaleaf.Collab;

/// <summary>
/// 远程协作邀请码：A/XXXX-XXXX-XXXX-XXXX（16 位 base31 + 模 31 校验）。
/// 字母表去除 0/1/I/O/L 防止混淆误读；前 8 位派生网络名，后 8 位为网络密钥。
/// 邀请码即加入协作网络的全部凭据，等同密钥，请勿公开分享。
/// </summary>
public static partial class CollabCode
{
    /// <summary>邀请码前缀。</summary>
    public const string Prefix = "A/";

    private const string Alphabet = "23456789ABCDEFGHJKMNPQRSTUVWXYZ";
    private const int DigitCount = 16;

    private static readonly BigInteger Modulus = BigInteger.Pow(31, DigitCount);

    /// <summary>
    /// 生成新邀请码：128 位随机数取模后调整为 31 的倍数（内嵌校验）。
    /// </summary>
    public static string Create()
    {
        byte[] random = RandomNumberGenerator.GetBytes(16);
        BigInteger value = new BigInteger(random, isUnsigned: true, isBigEndian: true) % Modulus;
        value -= value % 31;

        Span<char> digits = stackalloc char[DigitCount];
        for (int i = DigitCount - 1; i >= 0; i--)
        {
            digits[i] = Alphabet[(int)(value % 31)];
            value /= 31;
        }
        return Prefix + Format(new string(digits));
    }

    /// <summary>
    /// 从任意粘贴文本中提取邀请码（大小写不敏感、允许缺失 A/ 前缀与分隔符）。
    /// </summary>
    public static string? Find(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var match = CodeRegex().Match(text);
        if (!match.Success) return null;

        var digits = string.Concat(match.Groups[1].Value, match.Groups[2].Value, match.Groups[3].Value, match.Groups[4].Value);
        if (!IsChecksumValid(digits)) return null;
        return Prefix + Format(digits);
    }

    /// <summary>严格校验邀请码文本（必须为 Create 生成的规范形式或可提取形式）。</summary>
    public static bool TryParse(string? text, out string code)
    {
        code = "";
        var found = Find(text);
        if (found is null) return false;
        code = found;
        return true;
    }

    /// <summary>网络名（前 8 位派生，固定 ayaka-collab- 前缀）。</summary>
    public static string ToNetworkName(string code) => "ayaka-collab-" + RawDigits(code)[..8];

    /// <summary>网络密钥（后 8 位）。</summary>
    public static string ToNetworkSecret(string code) => RawDigits(code)[8..];

    /// <summary>校验 16 位数字串的模 31 校验位。</summary>
    private static bool IsChecksumValid(string digits)
    {
        if (digits.Length != DigitCount) return false;
        BigInteger value = 0;
        foreach (char c in digits)
        {
            int index = Alphabet.IndexOf(c);
            if (index < 0) return false;
            value = value * 31 + index;
        }
        return value % 31 == 0;
    }

    /// <summary>提取邀请码中的 16 位数字串（大写、无分隔符）。</summary>
    private static string RawDigits(string code)
    {
        var digits = new char[DigitCount];
        int n = 0;
        foreach (char c in code.ToUpperInvariant())
        {
            if (c is >= '0' and <= '9' or >= 'A' and <= 'Z')
            {
                if (n >= DigitCount) throw new FormatException("邀请码格式无效：" + code);
                digits[n++] = c;
            }
        }
        if (n != DigitCount) throw new FormatException("邀请码格式无效：" + code);
        if (!IsChecksumValid(new string(digits))) throw new FormatException("邀请码校验失败：" + code);
        return new string(digits);
    }

    /// <summary>格式化为 XXXX-XXXX-XXXX-XXXX（不含前缀）。</summary>
    private static string Format(string digits) =>
        string.Join("-", [digits[..4], digits[4..8], digits[8..12], digits[12..16]]);

    [GeneratedRegex(@"(?:A/)?([2-9A-HJKMNP-Z]{4})[-\s]*([2-9A-HJKMNP-Z]{4})[-\s]*([2-9A-HJKMNP-Z]{4})[-\s]*([2-9A-HJKMNP-Z]{4})", RegexOptions.IgnoreCase, "zh-CN")]
    private static partial Regex CodeRegex();
}
