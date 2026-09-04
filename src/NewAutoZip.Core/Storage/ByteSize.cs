using System.Globalization;

namespace NewAutoZip.Core.Storage;

/// <summary>
/// 字节数的人类可读格式化。
///
/// 旧版 <c>MainForm.FormatSize</c> 有个能骗人的 bug：
/// <c>while (len &gt;= 1024) { order++; len /= 1024; }</c> 之后用 <c>"{0:0.##} {1}"</c>，
/// 但它先做 <c>len = len / 1024.0</c> 再判断，导致 <c>FormatSize(0)</c> 返回 <c>"1K"</c> ——
/// 空批次会显示成 1 KB。
/// </summary>
public static class ByteSize
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];

    private static readonly string[] ShortUnits = ["B", "K", "M", "G", "T", "P"];

    public static string Format(long bytes)
    {
        if (bytes < 0)
        {
            return "-" + Format(-bytes);
        }

        if (bytes < 1024)
        {
            return bytes.ToString(CultureInfo.CurrentCulture) + " B";
        }

        double value = bytes;
        int order = 0;

        while (value >= 1024 && order < Units.Length - 1)
        {
            value /= 1024;
            order++;
        }

        string format = value >= 100 ? "0" : value >= 10 ? "0.0" : "0.00";
        return value.ToString(format, CultureInfo.CurrentCulture) + " " + Units[order];
    }

    /// <summary>
    /// 时长的人类可读格式化。旧版用 <c>TimeSpan.Hours</c>，上限 23，
    /// 一个跑了 26 小时的批次会显示成 2 小时。这里用 TotalHours。
    /// </summary>
    public static string FormatDuration(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
        {
            span = TimeSpan.Zero;
        }

        if (span.TotalSeconds < 1)
        {
            return $"{span.TotalMilliseconds:0} 毫秒";
        }

        if (span.TotalMinutes < 1)
        {
            return $"{span.TotalSeconds:0.0} 秒";
        }

        if (span.TotalHours < 1)
        {
            return $"{span.Minutes} 分 {span.Seconds} 秒";
        }

        return $"{(long)span.TotalHours} 小时 {span.Minutes} 分 {span.Seconds} 秒";
    }

    /// <summary>
    /// 紧凑写法：<c>4.72G</c> / <c>123M</c> / <c>512K</c> / <c>900B</c>。
    /// 通知正文用这一种 —— 用户给的样例就是这个写法，而且一行里要塞好几个数字，越短越好读。
    /// 界面上仍用 <see cref="Format"/> 的带空格全称，那里空间够。
    /// </summary>
    public static string FormatShort(long bytes)
    {
        if (bytes < 0)
        {
            return "-" + FormatShort(-bytes);
        }

        if (bytes < 1024)
        {
            return bytes.ToString(CultureInfo.InvariantCulture) + "B";
        }

        double value = bytes;
        int order = 0;

        while (value >= 1024 && order < ShortUnits.Length - 1)
        {
            value /= 1024;
            order++;
        }

        string format = value >= 100 ? "0" : "0.00";
        return value.ToString(format, CultureInfo.InvariantCulture) + ShortUnits[order];
    }

    /// <summary>
    /// 通知里的耗时写法：<c>0小时18分46秒</c>。小时位不省略，这样多条通知竖着排能对齐。
    /// 用 TotalHours 而不是 <c>TimeSpan.Hours</c> —— 后者上限 23，跑了 26 小时会显示成 2 小时。
    /// </summary>
    public static string FormatClock(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
        {
            span = TimeSpan.Zero;
        }

        return $"{(long)span.TotalHours}小时{span.Minutes}分{span.Seconds}秒";
    }
}
