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
}
