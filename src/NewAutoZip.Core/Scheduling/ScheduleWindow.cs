using NewAutoZip.Core.Configuration;

namespace NewAutoZip.Core.Scheduling;

/// <summary>
/// 工作时间窗口。
///
/// 旧版把"绝对日期范围"和"每日时段"挤进两个 DateTimePicker，语义混在一起；
/// MainForm_Load 里又有一句 <c>dtStart.Value = DateTime.Now</c> 悄悄把每日时段改成
/// "当前时刻 → 23:59"，用户在设计器里配的值被无声覆盖。
/// 而且跨夜时段（22:00 → 06:00）在旧逻辑下恒为假，永远不工作。
///
/// 新版把两个概念彻底拆开，并显式支持跨夜。
/// </summary>
public sealed record ScheduleWindow(
    DateOnly? EffectiveFrom,
    DateOnly? EffectiveTo,
    bool AllDay,
    TimeOnly DailyStart,
    TimeOnly DailyEnd)
{
    private const int MaxProbeDays = 400;

    public static ScheduleWindow AlwaysOn { get; } =
        new(null, null, true, TimeOnly.MinValue, TimeOnly.MaxValue);

    public static ScheduleWindow FromSettings(AppSettings settings) => new(
        settings.EffectiveFrom,
        settings.EffectiveTo,
        settings.AllDay,
        settings.DailyStart,
        settings.DailyEnd);

    /// <summary>每日时段是否跨过午夜。</summary>
    public bool WrapsMidnight => !AllDay && DailyEnd < DailyStart;

    /// <summary>
    /// 这个时段<b>不会关门</b>：全天，或者起止时间相同（后者按全天处理，见 <see cref="IsInDailyRange"/>）。
    ///
    /// 单独拎出来是因为"没有下一次开门"和"再也不会开门了"是两件完全不同的事，
    /// 而两者都表现为 <see cref="NextOpening"/> 返回 null。
    /// </summary>
    public bool NeverCloses => AllDay || ToMinutes(DailyStart) == ToMinutes(DailyEnd);

    public bool IsActive(DateTimeOffset localNow)
    {
        DateOnly date = DateOnly.FromDateTime(localNow.DateTime);

        if (EffectiveFrom is { } from && date < from)
        {
            return false;
        }

        if (EffectiveTo is { } to && date > to)
        {
            return false;
        }

        return AllDay || IsInDailyRange(TimeOnly.FromDateTime(localNow.DateTime));
    }

    /// <summary>生效日期范围已经整体过期（不可能再有下一次）。</summary>
    public bool IsExpired(DateTimeOffset localNow) =>
        EffectiveTo is { } to && DateOnly.FromDateTime(localNow.DateTime) > to;

    /// <summary>
    /// 生效起始日期那天的本地零点（换算成 UTC）。没设起始日期时返回 null。
    ///
    /// 这个值不只决定"哪天开始工作"，同时是<b>文件的准入下限</b>：
    /// 用户在界面上写"从 8 月 1 日起生效"，意思是"8 月 1 日以后的文件才算"，
    /// 而不仅仅是"8 月 1 日以后程序才醒着"。
    /// 从这一天的零点整开始算（用 &gt;= 比较），所以那天凌晨落下的文件也在范围内。
    /// </summary>
    public DateTimeOffset? EffectiveFromUtc(TimeZoneInfo? zone = null)
    {
        if (EffectiveFrom is not { } from)
        {
            return null;
        }

        zone ??= TimeZoneInfo.Local;

        DateTime naive = from.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);

        return new DateTimeOffset(naive, zone.GetUtcOffset(naive)).ToUniversalTime();
    }

    public bool IsInDailyRange(TimeOnly time)
    {
        // 按分钟比较：DailyEnd 通常是 23:59，若按 tick 比较则 23:59:30 会被判定为超出。
        int t = ToMinutes(time);
        int start = ToMinutes(DailyStart);
        int end = ToMinutes(DailyEnd);

        // 起止相同：视为全天。旧版这种配置会变成"永远不工作"的静默陷阱。
        if (start == end)
        {
            return true;
        }

        return start < end
            ? t >= start && t <= end
            : t >= start || t <= end;   // 跨夜
    }

    /// <summary>
    /// 下一次进入工作时段的本地时刻。已在时段内则返回 <paramref name="localNow"/>；
    /// 永远不会再进入（生效日期已过）则返回 null。
    /// </summary>
    public DateTimeOffset? NextActivation(DateTimeOffset localNow, TimeZoneInfo? zone = null)
    {
        if (IsActive(localNow))
        {
            return localNow;
        }

        if (IsExpired(localNow))
        {
            return null;
        }

        zone ??= TimeZoneInfo.Local;

        DateOnly today = DateOnly.FromDateTime(localNow.DateTime);
        DateOnly probeFrom = EffectiveFrom is { } from && from > today ? from : today;

        TimeOnly openAt = NeverCloses ? TimeOnly.MinValue : DailyStart;

        for (int offset = 0; offset < MaxProbeDays; offset++)
        {
            DateOnly day = probeFrom.AddDays(offset);

            if (EffectiveTo is { } to && day > to)
            {
                return null;
            }

            DateTime naive = day.ToDateTime(openAt, DateTimeKind.Unspecified);
            DateTimeOffset candidate = new(naive, zone.GetUtcOffset(naive));

            if (candidate > localNow)
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// 下一次工作时段<b>开门</b>的本地时刻，严格晚于 <paramref name="localNow"/>。
    ///
    /// 与 <see cref="NextActivation"/> 只差一处，但这一处很要紧：本方法<b>不会</b>因为
    /// "现在正在时段内"就把当前时刻当答案。一轮处理跑完的时候程序通常还在时段里，
    /// 【结束】通知要回答的是"下一次什么时候开工"——
    /// 那一刻 <see cref="NextActivation"/> 返回的是"现在"，印出去就成了
    /// "下一次的工作开始时间：（刚刚过去的那一秒）"。
    ///
    /// 返回 null 有两种含义，调用方必须分开说，别都糊成"没有了"：
    /// <list type="bullet">
    /// <item><see cref="NeverCloses"/> 为真 —— 门根本不关，"下一次开门"这回事不存在；</item>
    /// <item>生效日期已经整体过期 —— 真的不会再有下一次了，需要用户去改设置。</item>
    /// </list>
    /// </summary>
    public DateTimeOffset? NextOpening(DateTimeOffset localNow, TimeZoneInfo? zone = null)
    {
        if (IsExpired(localNow))
        {
            return null;
        }

        zone ??= TimeZoneInfo.Local;

        DateOnly today = DateOnly.FromDateTime(localNow.DateTime);
        DateOnly probeFrom = EffectiveFrom is { } from && from > today ? from : today;

        // 不关门的时段只有一个"开门"时刻：生效起始日的零点。
        TimeOnly openAt = NeverCloses ? TimeOnly.MinValue : DailyStart;

        for (int offset = 0; offset < MaxProbeDays; offset++)
        {
            DateOnly day = probeFrom.AddDays(offset);

            if (EffectiveTo is { } to && day > to)
            {
                return null;
            }

            DateTime naive = day.ToDateTime(openAt, DateTimeKind.Unspecified);
            DateTimeOffset candidate = new(naive, zone.GetUtcOffset(naive));

            if (candidate > localNow)
            {
                return candidate;
            }

            // 不关门的时段：那唯一的开门时刻已经过去了，就不必再往后翻 ——
            // 明天零点不叫"开门"，因为门从来没关过。
            if (NeverCloses)
            {
                return null;
            }
        }

        return null;
    }

    private static int ToMinutes(TimeOnly time) => (time.Hour * 60) + time.Minute;
}
