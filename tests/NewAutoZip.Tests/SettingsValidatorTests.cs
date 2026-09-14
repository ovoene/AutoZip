using NewAutoZip.Core.Configuration;
using NewAutoZip.Core.Notifications;
using Xunit;

namespace NewAutoZip.Tests;

public class SettingsValidatorTests
{
    /// <summary>造一份"本来能通过"的配置，测试再单独破坏其中一项。</summary>
    private static AppSettings Healthy(TempDir root, out string sevenZip)
    {
        sevenZip = root.WriteFile("7za.exe", 16);

        return new AppSettings
        {
            MonitorPath = root.Sub("watch"),
            CloudPath = root.Sub("onedrive"),
            ZipTempPath = root.Sub("ziptemp"),
            Password = "Str0ng-Passw0rd!",
            MinFreeDiskBytes = 0,
            NotifyChannel = NotifierKind.None,
            EnabledEvents = NotifyEvent.All,
        };
    }

    [Fact]
    public void 健康配置可以启动()
    {
        using TempDir root = new();
        AppSettings s = Healthy(root, out string exe);

        ValidationReport report = SettingsValidator.Validate(s, exe);

        Assert.True(report.CanStart, report.ToText());
    }

    [Fact]
    public void OneDrive目录在监控目录下必须是错误()
    {
        // 这是最危险的一项，旧版完全不校验：
        // 生成的 .7z 落在监控目录里 → 被当成新文件再次打包 → 自我喂养式指数膨胀。
        using TempDir root = new();
        AppSettings s = Healthy(root, out string exe);
        s.CloudPath = Path.Combine(s.MonitorPath, "OneDrive");
        Directory.CreateDirectory(s.CloudPath);

        ValidationReport report = SettingsValidator.Validate(s, exe);

        Assert.False(report.CanStart);
        Assert.Contains(report.Errors, i => i.Field == nameof(AppSettings.CloudPath));
    }

    [Fact]
    public void ZipTemp在监控目录下必须是错误()
    {
        using TempDir root = new();
        AppSettings s = Healthy(root, out string exe);
        s.ZipTempPath = Path.Combine(s.MonitorPath, "ZipTemp");
        Directory.CreateDirectory(s.ZipTempPath);

        ValidationReport report = SettingsValidator.Validate(s, exe);

        Assert.False(report.CanStart);
        Assert.Contains(report.Errors, i => i.Field == nameof(AppSettings.ZipTempPath));
    }

    [Fact]
    public void 监控目录不存在是错误()
    {
        using TempDir root = new();
        AppSettings s = Healthy(root, out string exe);
        s.MonitorPath = Path.Combine(root.Path, "这个目录不存在");

        ValidationReport report = SettingsValidator.Validate(s, exe);

        Assert.False(report.CanStart);
        Assert.Contains(report.Errors, i => i.Field == nameof(AppSettings.MonitorPath));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("233")]          // 旧版设计器里硬编码的默认密码
    [InlineData("123456")]
    [InlineData("password")]
    [InlineData("admin")]
    public void 空密码与弱密码被拒绝(string password)
    {
        using TempDir root = new();
        AppSettings s = Healthy(root, out string exe);
        s.Password = password;

        ValidationReport report = SettingsValidator.Validate(s, exe);

        Assert.False(report.CanStart, $"密码 \"{password}\" 竟然通过了校验");
        Assert.Contains(report.Errors, i => i.Field == nameof(AppSettings.Password));
    }

    [Fact]
    public void 含双引号的密码可以使用_只给提醒()
    {
        // 旧版是手工把 -p 拼进命令行字符串的，双引号会把参数引用截断 —— 那时必须拦下。
        // 新版 SevenZipRunner 用 ProcessStartInfo.ArgumentList 传参，由运行时按 Windows
        // 规则自行加引号转义，7-Zip 侧解析回来的仍是原始密码，所以不必拒绝。
        // 但换用其他解压工具时用户容易踩坑，因此保留一条提醒。
        using TempDir root = new();
        AppSettings s = Healthy(root, out string exe);
        s.Password = "abc\"def123";

        ValidationReport report = SettingsValidator.Validate(s, exe);

        Assert.True(report.CanStart, report.ToText());
        Assert.Contains(report.Warnings, i => i.Field == nameof(AppSettings.Password));
    }

    [Fact]
    public void 含控制字符的密码被拒绝()
    {
        // 控制字符没法可靠地经由命令行传递，且用户无从复现。
        using TempDir root = new();
        AppSettings s = Healthy(root, out string exe);
        s.Password = "abc\tdef123";

        ValidationReport report = SettingsValidator.Validate(s, exe);

        Assert.False(report.CanStart);
        Assert.Contains(report.Errors, i => i.Field == nameof(AppSettings.Password));
    }

    [Fact]
    public void 找不到压缩程序是错误()
    {
        using TempDir root = new();
        AppSettings s = Healthy(root, out _);

        ValidationReport report = SettingsValidator.Validate(s, Path.Combine(root.Path, "没有这个.exe"));

        Assert.False(report.CanStart);
    }

    [Fact]
    public void 日期范围颠倒是错误()
    {
        using TempDir root = new();
        AppSettings s = Healthy(root, out string exe);
        s.EffectiveFrom = new DateOnly(2026, 12, 1);
        s.EffectiveTo = new DateOnly(2026, 1, 1);

        ValidationReport report = SettingsValidator.Validate(s, exe);

        Assert.False(report.CanStart);
    }

    [Fact]
    public void 选了通知渠道却没填地址是错误()
    {
        using TempDir root = new();
        AppSettings s = Healthy(root, out string exe);
        s.NotifyChannel = NotifierKind.WeCom;
        s.WebhookUrl = string.Empty;

        ValidationReport report = SettingsValidator.Validate(s, exe);

        Assert.False(report.CanStart);
    }

    [Fact]
    public void Telegram缺BotToken是错误()
    {
        using TempDir root = new();
        AppSettings s = Healthy(root, out string exe);
        s.NotifyChannel = NotifierKind.Telegram;
        s.TelegramChatId = "12345";
        s.TelegramBotToken = string.Empty;

        ValidationReport report = SettingsValidator.Validate(s, exe);

        Assert.False(report.CanStart);
    }

    [Fact]
    public void 一次列出全部问题_而不是只报第一个()
    {
        // 旧版是"发现一个问题弹一个 MessageBox 然后 return"，用户要点五六次才知道全貌。
        using TempDir root = new();
        AppSettings s = Healthy(root, out _);
        s.MonitorPath = Path.Combine(root.Path, "不存在1");
        s.CloudPath = Path.Combine(root.Path, "不存在2");
        s.Password = "233";

        ValidationReport report = SettingsValidator.Validate(s, Path.Combine(root.Path, "没有.exe"));

        Assert.False(report.CanStart);
        Assert.True(report.Errors.Count() >= 4,
            $"只报了 {report.Errors.Count()} 个错误，应当一次列全：{Environment.NewLine}{report.ToText()}");
    }

    [Fact]
    public void 静默期长于批处理窗口时给出提醒()
    {
        using TempDir root = new();
        AppSettings s = Healthy(root, out string exe);
        s.QuietSeconds = 600;             // 10 分钟
        s.BatchWindowMinutes = 1;         // 1 分钟窗口 —— 永远等不到文件就绪

        ValidationReport report = SettingsValidator.Validate(s, exe);

        Assert.True(report.CanStart);      // 不阻止启动
        Assert.Contains(report.Warnings, i => i.Field == nameof(AppSettings.QuietSeconds));
    }

    [Fact]
    public void 一个事件都没勾选时给出提醒()
    {
        using TempDir root = new();
        AppSettings s = Healthy(root, out string exe);
        s.NotifyChannel = NotifierKind.WeCom;
        s.WebhookUrl = "https://qyapi.weixin.qq.com/cgi-bin/webhook/send?key=abc";
        s.EnabledEvents = NotifyEvent.None;

        ValidationReport report = SettingsValidator.Validate(s, exe);

        Assert.Contains(report.Warnings, i => i.Field == nameof(AppSettings.EnabledEvents));
    }

    [Fact]
    public void 磁盘余量要求高于实际可用空间是错误()
    {
        using TempDir root = new();
        AppSettings s = Healthy(root, out string exe);
        s.MinFreeDiskBytes = SettingsLimits.MinFreeDiskBytesMax;   // 4 TB

        ValidationReport report = SettingsValidator.Validate(s, exe);

        Assert.False(report.CanStart);
        Assert.Contains(report.Errors, i => i.Field == nameof(AppSettings.MinFreeDiskBytes));
    }

    // ==================================================================
    //  数据目录（默认 = exe 所在目录）与监控目录的关系
    // ==================================================================

    [Fact]
    public void 程序数据目录就是监控目录必须是错误()
    {
        using TempDir root = new();
        AppSettings s = Healthy(root, out string exe);

        // 数据目录默认就是 exe 所在目录，所以"把程序放进被监控的目录"很容易发生。
        ValidationReport report = SettingsValidator.Validate(s, exe, dataRoot: s.MonitorPath);

        Assert.False(report.CanStart);
        Assert.Contains(report.Errors, i => i.Field == nameof(AppSettings.MonitorPath));
    }

    [Fact]
    public void 程序数据目录在监控目录之下必须是错误()
    {
        using TempDir root = new();
        AppSettings s = Healthy(root, out string exe);

        string inside = Path.Combine(s.MonitorPath, "tools", "NewAutoZip");

        ValidationReport report = SettingsValidator.Validate(s, exe, dataRoot: inside);

        Assert.False(report.CanStart);
        Assert.Contains(report.Errors, i => i.Field == nameof(AppSettings.MonitorPath));
    }

    [Fact]
    public void 程序数据目录在监控目录之外可以启动()
    {
        using TempDir root = new();
        AppSettings s = Healthy(root, out string exe);

        ValidationReport report = SettingsValidator.Validate(s, exe, dataRoot: root.Sub("program"));

        Assert.True(report.CanStart, report.ToText());
    }

    [Fact]
    public void 监控目录在数据目录之下不算问题()
    {
        // 反过来是允许的：程序装在 F:\NewAutoZip，监控 F:\NewAutoZip\watch。
        // 此时日志与 ZipTemp 是 watch 的兄弟目录，不会被监控到。
        using TempDir root = new();
        AppSettings s = Healthy(root, out string exe);
        s.MonitorPath = Path.Combine(root.Sub("program"), "watch");
        Directory.CreateDirectory(s.MonitorPath);

        ValidationReport report = SettingsValidator.Validate(s, exe, dataRoot: root.Sub("program"));

        Assert.True(report.CanStart, report.ToText());
    }

    // ==================================================================
    //  恢复演练：全部只发提醒，一条都不许拦启动
    // ==================================================================

    [Fact]
    public void 默认配置下演练规则一条都不触发()
    {
        // 默认是"打包后本地演练开着、写清单开着"，这套组合不该招来任何提醒 ——
        // 开箱就冒警告会训练用户忽略警告，那样真正要紧的那条也会被一起忽略。
        using TempDir root = new();
        AppSettings s = Healthy(root, out string exe);

        ValidationReport report = SettingsValidator.Validate(s, exe);

        Assert.True(report.CanStart, report.ToText());

        Assert.DoesNotContain(report.Warnings, i =>
            i.Field == nameof(AppSettings.VerifyAfterPackByExtract)
            || i.Field == nameof(AppSettings.WriteArchiveManifest)
            || i.Field == nameof(AppSettings.CloudDrillIntervalHours));
    }

    [Fact]
    public void 两种演练都关掉只给提醒_不拦启动()
    {
        // 拦下启动意味着连备份本身都不做了 —— 那是拿更大的风险去换更小的风险。
        using TempDir root = new();
        AppSettings s = Healthy(root, out string exe);
        s.VerifyAfterPackByExtract = false;
        s.CloudDrillEnabled = false;

        ValidationReport report = SettingsValidator.Validate(s, exe);

        Assert.True(report.CanStart, $"演练配置拦下了启动，这是不允许的：{report.ToText()}");
        Assert.Contains(report.Warnings, i => i.Field == nameof(AppSettings.VerifyAfterPackByExtract));
        Assert.DoesNotContain(report.Errors, i => i.Field == nameof(AppSettings.VerifyAfterPackByExtract));
    }

    [Fact]
    public void 开了演练却关了清单只给提醒()
    {
        // 没有清单，演练仍能验出"密码打不开"和"包坏了"，只是验不出"内容变了"。
        using TempDir root = new();
        AppSettings s = Healthy(root, out string exe);
        s.VerifyAfterPackByExtract = true;
        s.WriteArchiveManifest = false;

        ValidationReport report = SettingsValidator.Validate(s, exe);

        Assert.True(report.CanStart, report.ToText());
        Assert.Contains(report.Warnings, i => i.Field == nameof(AppSettings.WriteArchiveManifest));
    }

    [Fact]
    public void 两种演练都关着时不再抱怨没写清单()
    {
        // 清单是给演练用的账本。演练都关了还催人开清单，就是在制造噪音。
        using TempDir root = new();
        AppSettings s = Healthy(root, out string exe);
        s.VerifyAfterPackByExtract = false;
        s.CloudDrillEnabled = false;
        s.WriteArchiveManifest = false;

        ValidationReport report = SettingsValidator.Validate(s, exe);

        Assert.True(report.CanStart, report.ToText());
        Assert.DoesNotContain(report.Warnings, i => i.Field == nameof(AppSettings.WriteArchiveManifest));
    }

    [Fact]
    public void 云端演练间隔过密只给提醒()
    {
        // 云端的包多半已脱水，每次演练都要重新下载回本地 —— 按流量计费的线路上是持续开销。
        using TempDir root = new();
        AppSettings s = Healthy(root, out string exe);
        s.CloudDrillEnabled = true;
        s.CloudDrillIntervalHours = 1;

        ValidationReport report = SettingsValidator.Validate(s, exe);

        Assert.True(report.CanStart, report.ToText());
        Assert.Contains(report.Warnings, i => i.Field == nameof(AppSettings.CloudDrillIntervalHours));
        Assert.DoesNotContain(report.Errors, i => i.Field == nameof(AppSettings.CloudDrillIntervalHours));
    }

    [Fact]
    public void 云端演练间隔够宽松时不提醒()
    {
        using TempDir root = new();
        AppSettings s = Healthy(root, out string exe);
        s.CloudDrillEnabled = true;
        s.CloudDrillIntervalHours = 24;

        ValidationReport report = SettingsValidator.Validate(s, exe);

        Assert.True(report.CanStart, report.ToText());
        Assert.DoesNotContain(report.Warnings, i => i.Field == nameof(AppSettings.CloudDrillIntervalHours));
    }

    [Fact]
    public void 云端演练关着时不管间隔多小都不提醒()
    {
        // 功能都没开，间隔是多少根本不产生任何流量。
        using TempDir root = new();
        AppSettings s = Healthy(root, out string exe);
        s.CloudDrillEnabled = false;
        s.CloudDrillIntervalHours = 1;

        ValidationReport report = SettingsValidator.Validate(s, exe);

        Assert.DoesNotContain(report.Warnings, i => i.Field == nameof(AppSettings.CloudDrillIntervalHours));
    }

    [Fact]
    public void 最糟的演练配置组合也依然能启动()
    {
        // 把演练相关的每一项都调成最差，CanStart 仍必须为 true。
        // 这一条是对"演练规则一律 Warning"这个设计的兜底 ——
        // 将来谁把某条改成 Error，会先在这里被挡下。
        using TempDir root = new();
        AppSettings s = Healthy(root, out string exe);
        s.VerifyAfterPackByExtract = false;
        s.CloudDrillEnabled = true;
        s.CloudDrillIntervalHours = 1;
        s.WriteArchiveManifest = false;
        s.DrillTimeoutMinutes = 1;

        ValidationReport report = SettingsValidator.Validate(s, exe);

        Assert.True(report.CanStart, $"演练配置拦下了启动：{report.ToText()}");

        Assert.DoesNotContain(report.Errors, i =>
            i.Field == nameof(AppSettings.VerifyAfterPackByExtract)
            || i.Field == nameof(AppSettings.CloudDrillEnabled)
            || i.Field == nameof(AppSettings.CloudDrillIntervalHours)
            || i.Field == nameof(AppSettings.WriteArchiveManifest)
            || i.Field == nameof(AppSettings.DrillTimeoutMinutes));
    }

    // ==================================================================
    //  云盘容量预算
    //
    //  三条规则全都以"用户填了非 0 值"为前提。默认 0 = 没启用，
    //  一条都不该触发 —— 下面第一个测试钉的就是这件事。
    // ==================================================================

    [Fact]
    public void 没填容量预算时一条提醒都不该出现()
    {
        // 新字段遇上老 settings.json 就是这个样子：反序列化拿到 0。
        // 这里要是变黄变红，等于所有老用户升级后都平白多出一条提醒。
        using TempDir root = new();
        AppSettings s = Healthy(root, out string exe);
        s.CloudQuotaBytes = 0;
        s.CloudQuotaWarnBytes = 0;

        ValidationReport report = SettingsValidator.Validate(s, exe);

        Assert.True(report.CanStart, report.ToText());

        Assert.DoesNotContain(report.Issues, i =>
            i.Field == nameof(AppSettings.CloudQuotaBytes)
            || i.Field == nameof(AppSettings.CloudQuotaWarnBytes));
    }

    [Fact]
    public void 只填警戒线不填总容量要提醒()
    {
        using TempDir root = new();
        AppSettings s = Healthy(root, out string exe);
        s.CloudQuotaBytes = 0;
        s.CloudQuotaWarnBytes = 10L * 1024 * 1024 * 1024;

        ValidationReport report = SettingsValidator.Validate(s, exe);

        // 只是提醒，不该拦下启动：没填总容量顶多是这条警戒线不起作用，
        // 而拦下启动意味着连备份本身都不做了。
        Assert.True(report.CanStart, report.ToText());
        Assert.Contains(report.Warnings, i => i.Field == nameof(AppSettings.CloudQuotaWarnBytes));
    }

    [Fact]
    public void 警戒线不低于总容量是错误()
    {
        // 这种配置下剩余量从第一轮起就在警戒线之下，每一轮都会告警 ——
        // 等于把告警变成了噪音，必须在启动前拦下来。
        using TempDir root = new();
        AppSettings s = Healthy(root, out string exe);
        s.CloudQuotaBytes = 100L * 1024 * 1024 * 1024;
        s.CloudQuotaWarnBytes = s.CloudQuotaBytes;

        ValidationReport report = SettingsValidator.Validate(s, exe);

        Assert.False(report.CanStart);
        Assert.Contains(report.Errors, i => i.Field == nameof(AppSettings.CloudQuotaWarnBytes));
    }

    [Fact]
    public void 普通目录的预算超过磁盘总容量要提醒()
    {
        // 1 PB，任何真实磁盘都不可能有这么大。
        using TempDir root = new();
        AppSettings s = Healthy(root, out string exe);
        s.CloudTarget = CloudTarget.Folder;
        s.CloudQuotaBytes = 1024L * 1024 * 1024 * 1024 * 1024;

        ValidationReport report = SettingsValidator.Validate(s, exe);

        Assert.True(report.CanStart, report.ToText());
        Assert.Contains(report.Warnings, i => i.Field == nameof(AppSettings.CloudQuotaBytes));
    }

    [Fact]
    public void OneDrive模式不拿本地磁盘去质疑云盘容量()
    {
        // 同样填 1 PB，但这一次是 OneDrive 模式。
        // 云端的包会脱水，本地卷的大小和云端配额没有任何关系 ——
        // 这里要是也提醒，等于在说"你的 OneDrive 不可能有 1 PB，因为你的 C 盘没这么大"。
        using TempDir root = new();
        AppSettings s = Healthy(root, out string exe);
        s.CloudTarget = CloudTarget.OneDrive;
        s.CloudQuotaBytes = 1024L * 1024 * 1024 * 1024 * 1024;

        ValidationReport report = SettingsValidator.Validate(s, exe);

        Assert.DoesNotContain(report.Issues, i => i.Field == nameof(AppSettings.CloudQuotaBytes));
    }
}
