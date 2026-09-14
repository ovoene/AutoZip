using NewAutoZip.Core.Configuration;
using NewAutoZip.Core.Diagnostics;
using Xunit;

namespace NewAutoZip.Tests;

/// <summary>
/// settings.json 里秘密的落盘保护。
///
/// 这一整个文件是补一个真实存在过的覆盖缺口：改动之前，
/// 全部 510 个测试里<b>没有一个</b>覆盖"密码落盘前是否真的被加密"，
/// 而缺陷恰恰就在这条没人看的路径上 —— 明文一旦进了 settings.json
/// 就永远不会被加密，程序表现完全正常，没有任何迹象。
///
/// 注意：这些测试会真实调用 DPAPI（CurrentUser 作用域），
/// 所以只在 Windows 上有意义 —— 本仓库的目标框架就是 net8.0-windows。
/// </summary>
public class SettingsStoreSecretTests
{
    private const string Prefix = "dpapi:";

    /// <summary>
    /// 每个测试用独一无二的秘密值。
    ///
    /// 脱敏器已经按测试隔离了（见 <see cref="NewStore"/>），但秘密值本身仍要唯一：
    /// 这些断言全是"磁盘上有没有出现这个串"，用固定字符串的话，
    /// 并行跑的另一个测试写下的同名串会让断言指向错误的文件内容。
    /// </summary>
    private static string Unique(string tag) => $"NAZ-{tag}-{Guid.NewGuid():N}";

    private static string RawJson(string path) => File.ReadAllText(path);

    /// <summary>
    /// 造一个<b>隔离</b>的 SettingsStore —— 秘密登记到本次调用自己的脱敏器上，
    /// 而不是进程级的 <see cref="SecretRedactor.Shared"/>。
    ///
    /// 本类一律走这里。<c>Load</c>/<c>Save</c> 都会登记秘密，而 <c>SetSecrets</c>
    /// 是整份数组替换；共用单例的话，本类与 BackupEngineTests / NotificationTests
    /// （每次 Start() 都登记自己的密码）会互相顶掉对方的秘密。
    /// </summary>
    private static SettingsStore NewStore(string path) => new(path, new SecretRedactor());

    // ==================================================================
    //  Save：落盘必须是密文
    // ==================================================================

    [Fact]
    public void 保存后磁盘上不该有明文密码()
    {
        using TempDir dir = new("nazsec");
        string path = dir.File("settings.json");
        string secret = Unique("PW");

        NewStore(path).Save(new AppSettings { Password = secret });

        string json = RawJson(path);

        Assert.DoesNotContain(secret, json, StringComparison.Ordinal);
        Assert.Contains(Prefix, json, StringComparison.Ordinal);
    }

    [Fact]
    public void 四个秘密都要加密_不能只保护密码()
    {
        // Webhook URL 和 Telegram Token 同样是秘密：
        // 拿到 Webhook 能往用户的通知渠道里灌任意消息，拿到 Token 等于接管那个 bot。
        using TempDir dir = new("nazsec");
        string path = dir.File("settings.json");

        string pw = Unique("PW");
        string hook = Unique("WH");
        string token = Unique("TG");
        string chat = Unique("CI");

        NewStore(path).Save(new AppSettings
        {
            Password = pw,
            WebhookUrl = hook,
            TelegramBotToken = token,
            TelegramChatId = chat,
        });

        string json = RawJson(path);

        Assert.DoesNotContain(pw, json, StringComparison.Ordinal);
        Assert.DoesNotContain(hook, json, StringComparison.Ordinal);
        Assert.DoesNotContain(token, json, StringComparison.Ordinal);
        Assert.DoesNotContain(chat, json, StringComparison.Ordinal);
    }

    [Fact]
    public void 存进去再读出来要还原成一模一样的明文()
    {
        // 加密了但解不回来，等于把用户的密码弄丢了 —— 比不加密更糟。
        using TempDir dir = new("nazsec");
        string path = dir.File("settings.json");

        string pw = Unique("PW");
        string hook = Unique("WH");

        SettingsStore store = NewStore(path);
        store.Save(new AppSettings { Password = pw, WebhookUrl = hook });

        AppSettings loaded = store.Load(out string? warning);

        Assert.Null(warning);
        Assert.Equal(pw, loaded.Password);
        Assert.Equal(hook, loaded.WebhookUrl);
    }

    [Fact]
    public void 空秘密保持空字符串_不要写成一坨密文()
    {
        // 空值也加密的话，JSON 里会出现一个看着像有值的 dpapi: 串，
        // 而"有没有填密码"是启动前校验要判断的事。
        using TempDir dir = new("nazsec");
        string path = dir.File("settings.json");

        SettingsStore store = NewStore(path);
        store.Save(new AppSettings { Password = string.Empty });

        Assert.Equal(string.Empty, store.Load(out _).Password);
    }

    // ==================================================================
    //  Load：明文自动升级
    //
    //  钉的就是那个缺陷：Unprotect 对无前缀的值原样放行（有意为之，
    //  手工编辑过的配置得能用），而加密只发生在 Save —— 两条合起来，
    //  明文会无限期躺在磁盘上。
    // ==================================================================

    [Fact]
    public void 读到明文密码时就地升级成密文()
    {
        using TempDir dir = new("nazsec");
        string path = dir.File("settings.json");
        string secret = Unique("PW");

        // 手写一份带明文密码的配置 —— 正是手工编辑 / 部署脚本生成 / 旧版迁移的样子。
        File.WriteAllText(path, $$"""{"Password":"{{secret}}"}""");

        AppSettings loaded = NewStore(path).Load(out _);

        // 明文仍然可用（不能因为升级就把用户的密码弄没了）
        Assert.Equal(secret, loaded.Password);

        // 但磁盘上已经不是明文了
        string json = RawJson(path);
        Assert.DoesNotContain(secret, json, StringComparison.Ordinal);
        Assert.Contains(Prefix, json, StringComparison.Ordinal);
    }

    [Fact]
    public void 升级只动秘密_其余配置一字不改()
    {
        // 升级是顺带做的一次整份重写。要是把别的字段改了 —— 比如被 ClampToLimits
        // 夹成了默认值 —— 用户会发现自己的配置莫名其妙变了。
        using TempDir dir = new("nazsec");
        string path = dir.File("settings.json");
        string secret = Unique("PW");

        File.WriteAllText(path, $$"""
            {
              "Password": "{{secret}}",
              "MonitorPath": "F:\\某个目录",
              "ArchivePrefix": "我的备份",
              "CompressionLevel": 7,
              "QuietSeconds": 42,
              "EncryptFileNames": false
            }
            """);

        SettingsStore store = NewStore(path);
        store.Load(out _);

        // 重新读一次：读到的是升级后写下去的那份
        AppSettings after = store.Load(out _);

        Assert.Equal(secret, after.Password);
        Assert.Equal("F:\\某个目录", after.MonitorPath);
        Assert.Equal("我的备份", after.ArchivePrefix);
        Assert.Equal(7, after.CompressionLevel);
        Assert.Equal(42, after.QuietSeconds);
        Assert.False(after.EncryptFileNames);
    }

    [Fact]
    public void 已经是密文的不重写()
    {
        // 每次启动都重写一遍配置文件是没必要的写盘，
        // 而且会让用户看到 settings.json 的修改时间莫名其妙地变。
        using TempDir dir = new("nazsec");
        string path = dir.File("settings.json");

        SettingsStore store = NewStore(path);
        store.Save(new AppSettings { Password = Unique("PW") });

        string before = RawJson(path);
        store.Load(out _);

        Assert.Equal(before, RawJson(path));
    }

    [Fact]
    public void 四个秘密里任意一个是明文都要触发升级()
    {
        // 判断条件写成"只看密码"的话，单独配了 Webhook 的用户就永远升级不了。
        using TempDir dir = new("nazsec");
        string path = dir.File("settings.json");
        string hook = Unique("WH");

        File.WriteAllText(path, $$"""{"WebhookUrl":"{{hook}}"}""");

        NewStore(path).Load(out _);

        Assert.DoesNotContain(hook, RawJson(path), StringComparison.Ordinal);
    }

    [Fact]
    public void 配置文件只读时升级失败也不能让程序起不来()
    {
        // 升级是尽力而为的安全加固，不是用户请求的操作。
        // 配置文件只读 / 磁盘满 / 被别的进程占着，任何一种都不该让程序崩在启动路径上。
        using TempDir dir = new("nazsec");
        string path = dir.File("settings.json");
        string secret = Unique("PW");

        File.WriteAllText(path, $$"""{"Password":"{{secret}}"}""");
        File.SetAttributes(path, FileAttributes.ReadOnly);

        try
        {
            AppSettings loaded = NewStore(path).Load(out _);

            // 升级没做成，但明文仍然可用 —— 行为退回改动之前的样子
            Assert.Equal(secret, loaded.Password);
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
    }

    [Fact]
    public void 文件不存在时不写出一份配置来()
    {
        // 头一次运行时 settings.json 还不存在，此时 Load 返回默认配置。
        // 要是这条路径顺手"升级"一下，就等于凭空生成了一份配置文件。
        using TempDir dir = new("nazsec");
        string path = dir.File("settings.json");

        NewStore(path).Load(out _);

        Assert.False(File.Exists(path), "文件本来不存在，Load 却造出了一份");
    }

    // ==================================================================
    //  秘密注册：日志脱敏的逐字层
    //
    //  这两个测试用<b>自己的</b> SecretRedactor 实例，不碰 SecretRedactor.Shared。
    //  单例是进程级的、SetSecrets 又是整份数组替换，而 BackupEngineTests /
    //  NotificationTests 每次 Start() 都会往里塞它们自己的密码 —— 共用的话
    //  两个方向都会翻车：我登记的秘密被顶掉（本测试假失败），
    //  或者我把它们的顶掉（它们的脱敏断言假失败）。
    //  表现都是"单独跑一直过，全量跑偶尔挂"。
    // ==================================================================

    [Fact]
    public void 配置一读进来就把秘密注册给脱敏器()
    {
        // 原先唯一的注册点在 BackupEngine.Start()，于是"引擎没跑"的路径
        // —— 纯恢复、启动前校验 —— 逐字脱敏层是空的。
        using TempDir dir = new("nazsec");
        string path = dir.File("settings.json");
        string secret = Unique("PW");

        // 先用一个 store 写出一份<b>已加密</b>的配置。
        //
        // 这里必须是已加密的：喂明文的话会触发就地升级，而升级走的是 Save，
        // Save 自己也会登记秘密 —— 于是就算 Load 里一行注册都没有，断言照样通过。
        // （写这个测试时就是这么错的：把 Load 里的注册整行注释掉，12 个测试全绿。）
        NewStore(path).Save(new AppSettings { Password = secret });

        // 换一个全新的脱敏器，这样"秘密在不在里面"只可能是这次 Load 放进去的。
        SecretRedactor redactor = new();
        new SettingsStore(path, redactor).Load(out _);

        // 引擎从未启动过，但这条日志已经该被脱敏了
        string redacted = redactor.Redact($"归档失败，用的密码是 {secret}");

        Assert.DoesNotContain(secret, redacted, StringComparison.Ordinal);
        Assert.Contains(SecretRedactor.Mask, redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void 改了密码之后按新密码脱敏()
    {
        // 保存后不重新注册的话，脱敏器还认着旧密码，新密码会明文写进日志。
        using TempDir dir = new("nazsec");
        string path = dir.File("settings.json");
        string original = Unique("PW");
        string updated = Unique("PW");
        SecretRedactor redactor = new();

        SettingsStore store = new(path, redactor);
        store.Save(new AppSettings { Password = original });
        store.Save(new AppSettings { Password = updated });

        string redacted = redactor.Redact($"归档失败，用的密码是 {updated}");

        Assert.DoesNotContain(updated, redacted, StringComparison.Ordinal);
        Assert.Contains(SecretRedactor.Mask, redacted, StringComparison.Ordinal);
    }
}
