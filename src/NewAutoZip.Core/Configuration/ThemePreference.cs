using System.Globalization;

namespace NewAutoZip.Core.Configuration;

/// <summary>界面主题。</summary>
public enum ThemeMode
{
    /// <summary>跟随 Windows 的浅色/深色设置，并在用户中途改动时实时跟随。</summary>
    System = 0,

    Light = 1,

    Dark = 2,
}

/// <summary>
/// 自定义皮肤的强调色规格。
///
/// 放在 Core 而不是界面层，是为了能不启动 WPF 就把"用户手输的颜色"这件事测干净：
/// 颜色是用户可以直接敲进输入框、也可以手改 settings.json 的字段，
/// 所以必须容错（缩写、大小写、缺 #、带空格）且绝不因为一个坏值就让程序起不来。
/// </summary>
public static class AccentColorSpec
{
    /// <summary>空字符串表示"用 Windows 的系统强调色"。</summary>
    public const string FollowSystem = "";

    /// <summary>
    /// 把用户输入规范成 <c>#RRGGBB</c>（大写）。
    /// 接受 <c>#RGB</c> / <c>RGB</c> / <c>#RRGGBB</c> / <c>RRGGBB</c>，忽略首尾空白与大小写。
    /// 空输入是合法的，表示跟随系统。
    /// </summary>
    public static bool TryNormalize(string? input, out string normalized)
    {
        normalized = FollowSystem;

        if (string.IsNullOrWhiteSpace(input))
        {
            return true;
        }

        string text = input.Trim().TrimStart('#');

        // 只接受 3 位或 6 位十六进制。带 Alpha 的 8 位不收：
        // 强调色是不透明色，收下半透明值只会让界面出现难以解释的发灰效果。
        if (text.Length is not (3 or 6))
        {
            return false;
        }

        foreach (char c in text)
        {
            if (!Uri.IsHexDigit(c))
            {
                return false;
            }
        }

        if (text.Length == 3)
        {
            // #ABC → #AABBCC
            text = string.Concat(text[0], text[0], text[1], text[1], text[2], text[2]);
        }

        normalized = "#" + text.ToUpperInvariant();
        return true;
    }

    /// <summary>规范化，失败时返回"跟随系统"而不是抛异常。用于反序列化后的兜底。</summary>
    public static string Sanitize(string? input) =>
        TryNormalize(input, out string normalized) ? normalized : FollowSystem;

    /// <summary>拆成 R/G/B 三个分量。传进来的必须是已规范化的值。</summary>
    public static bool TryParseRgb(string? normalized, out byte r, out byte g, out byte b)
    {
        r = g = b = 0;

        if (!TryNormalize(normalized, out string hex) || hex.Length != 7)
        {
            return false;
        }

        r = byte.Parse(hex.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        g = byte.Parse(hex.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        b = byte.Parse(hex.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return true;
    }

    /// <summary>
    /// 相对亮度（sRGB → 线性化后加权），用于判断"这个自定义色上面该放白字还是黑字"。
    /// 按 WCAG 2.1 的定义算，不是简单的 (R+G+B)/3 —— 后者会把纯绿判成暗色。
    /// </summary>
    public static double RelativeLuminance(byte r, byte g, byte b) =>
        (0.2126 * Linearize(r)) + (0.7152 * Linearize(g)) + (0.0722 * Linearize(b));

    private static double Linearize(byte channel)
    {
        double c = channel / 255.0;
        return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }

    /// <summary>
    /// 这个强调色上面放白字够不够清楚。
    /// 亮色强调色（明黄、浅青）配白字会完全看不见 —— 主按钮上的文字必须换成黑字。
    /// </summary>
    public static bool PrefersLightForeground(string? normalized) =>
        !TryParseRgb(normalized, out byte r, out byte g, out byte b)
        || RelativeLuminance(r, g, b) < 0.45;

    /// <summary>
    /// 预置皮肤色。UI 上是一排色块，点一下就换，不用自己去想十六进制。
    /// 名字用中文，因为这是给人看的。
    /// </summary>
    public static IReadOnlyList<(string Name, string Hex)> Presets { get; } =
    [
        ("默认蓝", "#0078D4"),
        ("石墨", "#4F5D75"),
        ("常青", "#2E9E6B"),
        ("琥珀", "#D98324"),
        ("绛红", "#C4314B"),
        ("紫罗兰", "#8B5CF6"),
        ("青碧", "#0E9AA7"),
        ("玫瑰", "#E0567A"),
    ];
}
