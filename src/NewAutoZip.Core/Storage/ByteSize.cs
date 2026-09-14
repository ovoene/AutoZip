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

    // ==================================================================
    //  单位换算 —— 给"数值 + 单位下拉框"那种输入方式用
    //
    //  配置里一律存字节；界面上让用户按 GB / TB 填。换算只在这里做一次，
    //  各处自己写 1024 * 1024 * 1024 迟早会有人少写一个或多写一个。
    // ==================================================================

    /// <summary>下拉框里能选的单位，索引就是 1024 的幂次（MB=2、GB=3、TB=4）。</summary>
    public static IReadOnlyList<string> UnitNames => Units;

    /// <summary>1024 的 <paramref name="power"/> 次方。超出范围按最接近的合法值处理。</summary>
    public static long UnitFactor(int power)
    {
        power = Math.Clamp(power, 0, Units.Length - 1);

        long factor = 1;

        for (int i = 0; i < power; i++)
        {
            factor *= 1024;
        }

        return factor;
    }

    /// <summary>
    /// 把"数值 + 单位幂次"还原成字节。
    ///
    /// 乘之前先判溢出：用户在 PB 那一档填个很大的数会让 <c>long</c> 绕回负数，
    /// 而负的容量预算会让"放不下"的判断整个反过来 —— 宁可夹在上限。
    /// </summary>
    public static long FromUnit(double value, int power)
    {
        if (value <= 0 || double.IsNaN(value))
        {
            return 0;
        }

        double bytes = value * UnitFactor(power);

        return bytes >= long.MaxValue ? long.MaxValue : (long)bytes;
    }

    /// <summary>
    /// 把字节数拆成"数值 + 单位幂次"，挑<b>能整除的最大单位</b>。
    ///
    /// 1 TB 要显示成 <c>1 TB</c> 而不是 <c>1024 GB</c>：用户填进去什么，
    /// 下次打开设置就该看到什么，否则每存一次读一次单位就往下掉一级。
    /// 整除不了的（比如 1.5 GB 那种手改出来的值）就退到能整除的那一级。
    ///
    /// <paramref name="minPower"/> 是给下拉框用的：下拉框里只有 MB 起步，
    /// 而手改出来的 1536 字节会被拆成"1.5 KB"——单位在下拉框里找不到，
    /// 落到默认项上就成了"1.5 GB"，一百万倍的误差。给个下限，
    /// 宁可显示成 0.0015 MB 这种难看的数字，也不能把用户的值悄悄放大。
    /// </summary>
    public static (double Value, int Power) ToUnit(long bytes, int minPower = 0)
    {
        minPower = Math.Clamp(minPower, 0, Units.Length - 1);

        // 0 没有"最大单位"可言。默认给 GB —— 容量预算按 GB 起步最顺手。
        if (bytes <= 0)
        {
            return (0, Math.Max(3, minPower));
        }

        int power = 0;

        while (power < Units.Length - 1 && bytes % (UnitFactor(power) * 1024) == 0)
        {
            power++;
        }

        power = Math.Max(power, minPower);

        return ((double)bytes / UnitFactor(power), power);
    }
}
