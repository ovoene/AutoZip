using NewAutoZip.Core.Configuration;
using NewAutoZip.Core.Packing;
using NewAutoZip.Core.Scheduling;
using NewAutoZip.Core.Storage;
using Xunit;

namespace NewAutoZip.Tests;

/// <summary>
/// 一批"旧版就是在这里算错了"的小函数。单独测是因为它们的错误会直接显示在界面上。
/// </summary>
public class FormattingTests
{
    [Theory]
    [InlineData(0L, "0 B")]              // 旧版 FormatSize(0) 返回 "1K" —— 空批次显示成 1K
    [InlineData(1L, "1 B")]
    [InlineData(1023L, "1023 B")]
    [InlineData(1024L, "1.00 KB")]
    [InlineData(1536L, "1.50 KB")]
    [InlineData(10240L, "10.0 KB")]      // 精度随量级递减：≥10 用一位小数
    [InlineData(102400L, "100 KB")]      // ≥100 不要小数
    [InlineData(1048576L, "1.00 MB")]
    [InlineData(1073741824L, "1.00 GB")]
    [InlineData(1099511627776L, "1.00 TB")]
    public void Format_边界值都正确(long bytes, string expected) =>
        Assert.Equal(expected, ByteSize.Format(bytes));

    [Fact]
    public void Format_负数不崩溃且不产生垃圾字符串()
    {
        string text = ByteSize.Format(-1);
        Assert.False(string.IsNullOrWhiteSpace(text));
    }

    [Fact]
    public void FormatDuration_超过一天不丢天数()
    {
        // 旧版用 TimeSpan.Hours（上限 23），26 小时会显示成 "2 小时"，直接少算一天。
        string text = ByteSize.FormatDuration(TimeSpan.FromHours(26));
        Assert.DoesNotContain("2 小时", text);
        Assert.Contains("26", text);
    }

    // ==================================================================
    //  通知正文里的紧凑写法 —— 用户给的样例就是 4.72G / 0小时18分46秒
    // ==================================================================

    [Theory]
    [InlineData(0L, "0B")]
    [InlineData(900L, "900B")]
    [InlineData(1024L, "1.00K")]
    [InlineData(5_068_889_026L, "4.72G")]     // 用户样例里的"源文件总大小：4.72G"
    [InlineData(4_585_618_309L, "4.27G")]     // 用户样例里的"压缩包大小：4.27G"
    [InlineData(1099511627776L, "1.00T")]
    public void FormatShort_与用户样例一致(long bytes, string expected) =>
        Assert.Equal(expected, ByteSize.FormatShort(bytes));

    [Fact]
    public void FormatShort_不带空格也不带全称()
    {
        string text = ByteSize.FormatShort(1536);

        Assert.DoesNotContain(" ", text);
        Assert.DoesNotContain("KB", text);
        Assert.EndsWith("K", text);
    }

    [Fact]
    public void FormatShort_不受系统区域设置影响()
    {
        // 德语区小数点是逗号，"4,72G" 会让下游解析和肉眼对比都出错。
        System.Globalization.CultureInfo previous = System.Globalization.CultureInfo.CurrentCulture;

        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            Assert.Equal("4.72G", ByteSize.FormatShort(5_068_889_026L));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void FormatClock_就是用户样例里那个写法()
    {
        // 18:02:07 → 18:20:53 恰好是 0小时18分46秒。
        Assert.Equal("0小时18分46秒", ByteSize.FormatClock(new TimeSpan(0, 18, 46)));
    }

    [Fact]
    public void FormatClock_小时位不省略以便多条通知竖排对齐() =>
        Assert.StartsWith("0小时", ByteSize.FormatClock(TimeSpan.FromSeconds(5)));

    [Fact]
    public void FormatClock_超过一天仍按总小时数计()
    {
        // TimeSpan.Hours 上限 23：26 小时会被印成 "2小时"，凭空少一天。
        Assert.Equal("26小时0分0秒", ByteSize.FormatClock(TimeSpan.FromHours(26)));
    }

    [Fact]
    public void FormatClock_负数归零不产生负号() =>
        Assert.Equal("0小时0分0秒", ByteSize.FormatClock(TimeSpan.FromSeconds(-30)));
}

public class ScheduleWindowTests
{
    private static ScheduleWindow CrossMidnight() => new(
        EffectiveFrom: null,
        EffectiveTo: null,
        AllDay: false,
        DailyStart: new TimeOnly(22, 0),
        DailyEnd: new TimeOnly(6, 0));

    [Theory]
    [InlineData(23, true)]   // 当晚
    [InlineData(2, true)]    // 次日凌晨
    [InlineData(22, true)]   // 边界：开始时刻
    [InlineData(12, false)]  // 正午 —— 旧版这里恒为 false，跨夜时段永远不工作
    [InlineData(7, false)]
    public void 跨夜时段判断正确(int hour, bool expected)
    {
        // 旧版判断是 now >= start && now <= end，22:00→06:00 时恒假。
        Assert.Equal(expected, CrossMidnight().IsInDailyRange(new TimeOnly(hour, 0)));
    }

    [Fact]
    public void 跨夜标记被正确识别()
    {
        Assert.True(CrossMidnight().WrapsMidnight);
        Assert.False(ScheduleWindow.AlwaysOn.WrapsMidnight);
    }

    [Theory]
    [InlineData(9, true)]
    [InlineData(17, true)]
    [InlineData(8, false)]
    [InlineData(18, false)]
    public void 普通时段判断正确(int hour, bool expected)
    {
        ScheduleWindow w = new(null, null, false, new TimeOnly(9, 0), new TimeOnly(17, 0));
        Assert.Equal(expected, w.IsInDailyRange(new TimeOnly(hour, 0)));
    }

    [Fact]
    public void 全天开关无视时刻()
    {
        Assert.True(ScheduleWindow.AlwaysOn.IsInDailyRange(new TimeOnly(3, 17)));
        Assert.True(ScheduleWindow.AlwaysOn.IsActive(DateTimeOffset.Now));
    }

    [Fact]
    public void 开始与结束相同视为全天_不是永不工作()
    {
        // 旧版这种配置会得到一个"永远不成立"的时段，用户完全看不出为什么不干活。
        ScheduleWindow w = new(null, null, false, new TimeOnly(3, 0), new TimeOnly(3, 0));
        Assert.True(w.IsInDailyRange(new TimeOnly(3, 0)));
        Assert.True(w.IsInDailyRange(new TimeOnly(15, 0)));
    }

    [Fact]
    public void 日期范围之外不激活()
    {
        DateTimeOffset now = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

        ScheduleWindow future = new(
            new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31), true, default, default);
        Assert.False(future.IsActive(now));

        ScheduleWindow past = new(
            new DateOnly(2026, 1, 1), new DateOnly(2026, 2, 1), true, default, default);
        Assert.False(past.IsActive(now));
        Assert.True(past.IsExpired(now));

        ScheduleWindow current = new(
            new DateOnly(2026, 8, 1), new DateOnly(2026, 12, 31), true, default, default);
        Assert.True(current.IsActive(now));
        Assert.False(current.IsExpired(now));
    }

    [Fact]
    public void NextActivation_给出未来时刻而不是过去()
    {
        ScheduleWindow w = new(null, null, false, new TimeOnly(22, 0), new TimeOnly(6, 0));
        DateTimeOffset now = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

        DateTimeOffset? next = w.NextActivation(now, TimeZoneInfo.Utc);

        Assert.NotNull(next);
        Assert.True(next > now, $"NextActivation 返回了过去的时刻：{next}");
    }

    // ==================================================================
    //  NextOpening：【结束】那一行"下一次的工作开始时间"专用
    //
    //  和 NextActivation 只差一点，但那一点是这个方法存在的全部理由：
    //  一轮处理跑完时程序通常还在时段内，NextActivation 那时返回的是"现在"，
    //  印进通知就成了"下一次的工作开始时间：（刚刚过去的那一秒）"。
    // ==================================================================

    [Fact]
    public void NextOpening_时段内也给出下一次开门而不是现在()
    {
        ScheduleWindow w = new(null, null, false, new TimeOnly(9, 0), new TimeOnly(17, 0));
        DateTimeOffset now = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

        // 对照：此刻在时段内，NextActivation 返回的就是"现在"。
        Assert.Equal(now, w.NextActivation(now, TimeZoneInfo.Utc));

        DateTimeOffset? next = w.NextOpening(now, TimeZoneInfo.Utc);

        Assert.Equal(new DateTimeOffset(2026, 9, 2, 9, 0, 0, TimeSpan.Zero), next);
    }

    [Fact]
    public void NextOpening_时段外给出当天稍后的开门时刻()
    {
        ScheduleWindow w = new(null, null, false, new TimeOnly(9, 0), new TimeOnly(17, 0));
        DateTimeOffset earlyMorning = new(2026, 9, 1, 6, 30, 0, TimeSpan.Zero);

        Assert.Equal(
            new DateTimeOffset(2026, 9, 1, 9, 0, 0, TimeSpan.Zero),
            w.NextOpening(earlyMorning, TimeZoneInfo.Utc));
    }

    [Fact]
    public void NextOpening_不关门的时段没有下一次开门()
    {
        // 全天模式下"下一次开门"这回事不存在 —— 门从来没关过，明天零点也不叫开门。
        // 调用方（【结束】那一行）必须把这种 null 说成"全天监控"，
        // 而不是跟"生效日期已过期"混成同一句"没有了"。
        DateTimeOffset now = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

        Assert.True(ScheduleWindow.AlwaysOn.NeverCloses);
        Assert.Null(ScheduleWindow.AlwaysOn.NextOpening(now, TimeZoneInfo.Utc));

        // 起止相同同样按全天处理，不该退化成"每天 03:00 开一次门"。
        ScheduleWindow sameEnds = new(null, null, false, new TimeOnly(3, 0), new TimeOnly(3, 0));
        Assert.True(sameEnds.NeverCloses);
        Assert.Null(sameEnds.NextOpening(now, TimeZoneInfo.Utc));
    }

    [Fact]
    public void NextOpening_生效日期过期返回空_起始日期在未来则取那天()
    {
        DateTimeOffset now = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

        ScheduleWindow expired = new(
            new DateOnly(2026, 1, 1), new DateOnly(2026, 2, 1), true, default, default);
        Assert.Null(expired.NextOpening(now, TimeZoneInfo.Utc));

        // 起始日期还没到：第一次开门是那天的 DailyStart，不是今天的。
        ScheduleWindow future = new(
            new DateOnly(2026, 10, 1), null, false, new TimeOnly(9, 0), new TimeOnly(17, 0));
        Assert.Equal(
            new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero),
            future.NextOpening(now, TimeZoneInfo.Utc));

        // 全天 + 未来起始日期：唯一的"开门"就是那天零点。
        ScheduleWindow futureAllDay = new(new DateOnly(2026, 10, 1), null, true, default, default);
        Assert.Equal(
            new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero),
            futureAllDay.NextOpening(now, TimeZoneInfo.Utc));
    }

    [Fact]
    public void NextOpening_跨夜时段的开门时刻是DailyStart()
    {
        ScheduleWindow w = CrossMidnight();   // 22:00 → 06:00

        // 凌晨 2 点正在时段里，但"下一次开门"是今晚 22:00，不是"现在"。
        Assert.Equal(
            new DateTimeOffset(2026, 9, 1, 22, 0, 0, TimeSpan.Zero),
            w.NextOpening(new DateTimeOffset(2026, 9, 1, 2, 0, 0, TimeSpan.Zero), TimeZoneInfo.Utc));

        // 晚上 23 点也在时段里，今天的 22:00 已经过去了 —— 下一次是明晚。
        Assert.Equal(
            new DateTimeOffset(2026, 9, 2, 22, 0, 0, TimeSpan.Zero),
            w.NextOpening(new DateTimeOffset(2026, 9, 1, 23, 0, 0, TimeSpan.Zero), TimeZoneInfo.Utc));
    }

    // ==================================================================
    //  起始日期同时是"文件准入下限"
    //  用户报告：设了起始日期，那天之后已经存在的文件却检测不到。
    // ==================================================================

    [Fact]
    public void EffectiveFromUtc_是那天的本地零点()
    {
        ScheduleWindow w = new(new DateOnly(2026, 8, 1), null, true, default, default);

        // 用固定偏移的时区，断言才与运行这台机器的设置无关。
        TimeZoneInfo plus8 = TimeZoneInfo.CreateCustomTimeZone("t+8", TimeSpan.FromHours(8), "t+8", "t+8");

        DateTimeOffset? from = w.EffectiveFromUtc(plus8);

        Assert.NotNull(from);

        // 东八区的 8-1 零点 = UTC 7-31 16:00。取的是那天零点整，不是"设置时的那一刻"。
        Assert.Equal(new DateTimeOffset(2026, 7, 31, 16, 0, 0, TimeSpan.Zero), from!.Value);
    }

    [Fact]
    public void EffectiveFromUtc_没设起始日期就返回空表示不过滤()
    {
        Assert.Null(ScheduleWindow.AlwaysOn.EffectiveFromUtc(TimeZoneInfo.Utc));

        ScheduleWindow onlyTo = new(null, new DateOnly(2026, 12, 31), true, default, default);
        Assert.Null(onlyTo.EffectiveFromUtc(TimeZoneInfo.Utc));
    }
}

public class SevenZipExitCodeTests
{
    [Theory]
    [InlineData(SevenZipExitCode.Ok, true)]
    [InlineData(SevenZipExitCode.Warning, true)]        // 关键：1 = 有警告但压缩包是好的
    [InlineData(SevenZipExitCode.FatalError, false)]
    [InlineData(SevenZipExitCode.CommandLineError, false)]
    [InlineData(SevenZipExitCode.NotEnoughMemory, false)]
    [InlineData(SevenZipExitCode.UserStopped, false)]
    public void 退出码分级正确(int code, bool usable)
    {
        // 旧版 Zip7Service 是 `return p.ExitCode == 0;`，把退出码 1 当彻底失败。
        // 而 1 的最常见原因就是"某个文件被别的进程锁着，已跳过" —— 包其实完全可用。
        // 这是"每 30 秒产出一个完整压缩包直到磁盘满"的头号触发点。
        Assert.Equal(usable, SevenZipExitCode.IsArchiveUsable(code));
    }

    [Fact]
    public void 未知退出码按失败处理()
    {
        Assert.False(SevenZipExitCode.IsArchiveUsable(3));
        Assert.False(SevenZipExitCode.IsArchiveUsable(-1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(255)]
    [InlineData(42)]
    public void 每个退出码都有人话解释(int code) =>
        Assert.False(string.IsNullOrWhiteSpace(SevenZipExitCode.Describe(code)));
}

public class SettingsLimitsTests
{
    [Fact]
    public void ClampToLimits_把危险的零值抬到安全下限()
    {
        // 旧版四个 NumericUpDown 全是 WinForms 默认的 Minimum=0：
        //   * 扫描间隔 0 → 主循环变忙等，UI 消息队列瞬间被打爆
        //   * 保留天数 0 → CleanupZipTemp 等于彻底关闭
        AppSettings s = new()
        {
            RefreshIntervalSeconds = 0,
            QuietSeconds = 0,
            BatchWindowMinutes = 0,
            ZipTempKeepDays = 0,
            ZipTempMaxCount = 0,
            StableConfirmRounds = 0,
            UploadPollSeconds = 0,
            PackTimeoutMinutes = 0,
            MaxAttemptsBeforeQuarantine = 0,

            // 0 会让文件第一次读不到就被放弃，等于关掉了"等它解锁"这件事。
            UnreadableGiveUpMinutes = 0,
        };

        s.ClampToLimits();

        Assert.True(s.RefreshIntervalSeconds >= SettingsLimits.RefreshIntervalSecondsMin);
        Assert.True(s.QuietSeconds >= SettingsLimits.QuietSecondsMin);
        Assert.True(s.BatchWindowMinutes >= SettingsLimits.BatchWindowMinutesMin);
        Assert.True(s.ZipTempKeepDays >= SettingsLimits.ZipTempKeepDaysMin);
        Assert.True(s.ZipTempMaxCount >= SettingsLimits.ZipTempMaxCountMin);
        Assert.True(s.StableConfirmRounds >= SettingsLimits.StableConfirmRoundsMin);
        Assert.True(s.UploadPollSeconds >= SettingsLimits.UploadPollSecondsMin);
        Assert.True(s.PackTimeoutMinutes >= SettingsLimits.PackTimeoutMinutesMin);
        Assert.True(s.MaxAttemptsBeforeQuarantine >= SettingsLimits.MaxAttemptsBeforeQuarantineMin);
        Assert.True(s.UnreadableGiveUpMinutes >= SettingsLimits.UnreadableGiveUpMinutesMin);
    }

    [Fact]
    public void ClampToLimits_压住超大值()
    {
        AppSettings s = new()
        {
            RefreshIntervalSeconds = int.MaxValue,
            CompressionLevel = 99,
            ZipTempKeepDays = int.MaxValue,
            UnreadableGiveUpMinutes = int.MaxValue,
        };

        s.ClampToLimits();

        Assert.Equal(SettingsLimits.RefreshIntervalSecondsMax, s.RefreshIntervalSeconds);
        Assert.Equal(SettingsLimits.CompressionLevelMax, s.CompressionLevel);
        Assert.Equal(SettingsLimits.ZipTempKeepDaysMax, s.ZipTempKeepDays);
        Assert.Equal(SettingsLimits.UnreadableGiveUpMinutesMax, s.UnreadableGiveUpMinutes);
    }

    [Fact]
    public void Clone_是深拷贝()
    {
        AppSettings a = new() { ExcludePatterns = ["*.tmp"] };
        AppSettings b = a.Clone();

        b.ExcludePatterns.Add("*.bak");

        Assert.Single(a.ExcludePatterns);
        Assert.Equal(2, b.ExcludePatterns.Count);
    }
}

public class ArchiveNamingTests
{
    private static readonly DateTimeOffset When =
        new(2026, 8, 13, 18, 9, 0, TimeSpan.FromHours(8));

    [Fact]
    public void 文件名就是前缀加秒级时间戳没有别的后缀()
    {
        Assert.Equal("Backup_20260813_180900.7z", ArchiveNaming.Build("Backup", When));
    }

    [Fact]
    public void 文件数量不会出现在文件名里()
    {
        // 旧命名是 Backup_20260813_180900_7files.7z，用户明确要求去掉这个后缀。
        string name = ArchiveNaming.Build("Backup", When);

        Assert.DoesNotContain("files", name, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, name.Count(c => c == '_'));
    }

    [Fact]
    public void 时间戳不受系统区域设置影响()
    {
        // 用波斯历这类非公历文化跑一遍：区域设置一变，年份就会变成 1405 之类。
        System.Globalization.CultureInfo previous = System.Globalization.CultureInfo.CurrentCulture;

        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("fa-IR");
            Assert.Equal("Backup_20260813_180900.7z", ArchiveNaming.Build("Backup", When));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("...")]
    public void 空前缀退回Backup(string prefix)
    {
        Assert.Equal("Backup_20260813_180900.7z", ArchiveNaming.Build(prefix, When));
    }

    [Fact]
    public void 前缀里的非法字符被换成下划线()
    {
        string name = ArchiveNaming.Build("My:Backup*?", When);

        Assert.Equal("My_Backup___20260813_180900.7z", name);
        Assert.DoesNotContain(Path.GetInvalidFileNameChars(), name.Contains);
    }
}
