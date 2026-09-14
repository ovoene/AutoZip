using NewAutoZip.Core.Diagnostics;

namespace NewAutoZip.Core.Configuration;

/// <summary>
/// settings.json 的读写。落盘前把密码 / Webhook / Telegram Token 用 DPAPI 加密，
/// 读取后解密回明文供程序使用。
/// </summary>
public sealed class SettingsStore
{
    private readonly string _path;
    private readonly SecretRedactor _redactor;

    /// <param name="redactor">
    /// 秘密登记到哪个脱敏器。默认是进程级的 <see cref="SecretRedactor.Shared"/>，
    /// 生产代码一律走默认值。留这个口子是为了测试：脱敏器是进程级单例，
    /// 而本仓库的测试类默认并行跑，共用一个单例会互相把对方登记的秘密顶掉
    /// —— 表现为"单独跑一直过，全量跑偶尔挂"。
    /// </param>
    public SettingsStore(string path, SecretRedactor? redactor = null)
    {
        _path = path;
        _redactor = redactor ?? SecretRedactor.Shared;
    }

    public string Path => _path;

    /// <summary>
    /// 读取配置。任何失败都返回一份默认配置，并通过 <paramref name="warning"/> 说明原因，
    /// 绝不因为配置文件损坏而让程序起不来。
    ///
    /// 读到明文秘密时<b>就地升级成密文</b>，见 <see cref="UpgradePlaintextSecrets"/>。
    /// </summary>
    public AppSettings Load(out string? warning)
    {
        AppSettings? loaded = AtomicJsonFile.TryRead<AppSettings>(_path, out string? error);

        if (loaded is null)
        {
            warning = error is null ? null : $"配置文件读取失败，已使用默认配置：{error}";
            return new AppSettings();
        }

        warning = null;

        // 这个判断必须在 Unprotect 之前：解密之后就分不出原本是明文还是密文了。
        bool hadPlaintext = HasPlaintextSecret(loaded);

        loaded.Password = SecretProtector.Unprotect(loaded.Password);
        loaded.WebhookUrl = SecretProtector.Unprotect(loaded.WebhookUrl);
        loaded.TelegramBotToken = SecretProtector.Unprotect(loaded.TelegramBotToken);
        loaded.TelegramChatId = SecretProtector.Unprotect(loaded.TelegramChatId);

        loaded.ExcludePatterns ??= [];
        loaded.ClampToLimits();

        // 秘密一读进来就注册给脱敏器，而不是等引擎启动时才注册。
        //
        // 原先唯一的注册点在 BackupEngine.Start()，于是"引擎没跑"的那些路径
        // —— 纯恢复、启动前校验、配置告警本身 —— 逐字脱敏层是空的，
        // 只剩通用正则兜底。而通用正则只认得出 -p 参数和 URL 里的 token=，
        // 认不出密码本身出现在别处（比如异常消息里带了路径或参数回显）。
        // 配置加载是全程序唯一的秘密入口，放这里才真正覆盖全生命周期。
        RegisterSecrets(loaded);

        if (hadPlaintext)
        {
            UpgradePlaintextSecrets(loaded);
        }

        return loaded;
    }

    /// <summary>把四个秘密登记给脱敏器。<see cref="Save"/> 后也要调，否则改了密码日志仍按旧的脱敏。</summary>
    private void RegisterSecrets(AppSettings settings) =>
        _redactor.SetSecrets(
        [
            settings.Password,
            settings.WebhookUrl,
            settings.TelegramBotToken,
            settings.TelegramChatId,
        ]);

    /// <summary>四个秘密里是否有"非空、但没有 dpapi: 前缀"的 —— 也就是明文躺在磁盘上。</summary>
    private static bool HasPlaintextSecret(AppSettings onDisk) =>
        IsPlaintext(onDisk.Password)
        || IsPlaintext(onDisk.WebhookUrl)
        || IsPlaintext(onDisk.TelegramBotToken)
        || IsPlaintext(onDisk.TelegramChatId);

    private static bool IsPlaintext(string? stored) =>
        !string.IsNullOrEmpty(stored) && !SecretProtector.IsProtected(stored);

    /// <summary>
    /// 把磁盘上的明文秘密就地改写成 DPAPI 密文。
    ///
    /// 为什么需要这件事：<see cref="SecretProtector.Unprotect"/> 对无前缀的值原样放行
    /// （这是有意的，手工编辑过的配置得能用），而加密<b>只发生在 <see cref="Save"/></b>。
    /// 两条合起来就成了：手工编辑、部署脚本生成、从旧版迁移过来的明文密码，
    /// 会一直明文躺在 settings.json 里 —— 程序跑得好好的，没有任何迹象，
    /// 直到用户哪天碰巧去设置页按一次保存。实测过：明文配置被完整加载、
    /// 跑完一整轮备份、正常退出，文件里始终是明文。
    ///
    /// <b>失败一律吞掉</b>：这是一次尽力而为的安全加固，不是用户请求的操作。
    /// 配置文件只读、磁盘满、被别的进程占着 —— 任何一种都不该让程序起不来。
    /// 升级不成功就退回原样（明文仍然可用），下次启动再试。
    /// </summary>
    private void UpgradePlaintextSecrets(AppSettings loaded)
    {
        try
        {
            Save(loaded);
        }
        catch (Exception)
        {
            // 故意吞掉 —— 理由见上。
        }
    }

    /// <summary>保存配置。失败会抛异常 —— 用户按了"保存"就该知道有没有成功。</summary>
    public void Save(AppSettings settings)
    {
        AppSettings onDisk = settings.Clone();

        onDisk.Password = SecretProtector.Protect(onDisk.Password);
        onDisk.WebhookUrl = SecretProtector.Protect(onDisk.WebhookUrl);
        onDisk.TelegramBotToken = SecretProtector.Protect(onDisk.TelegramBotToken);
        onDisk.TelegramChatId = SecretProtector.Protect(onDisk.TelegramChatId);

        AtomicJsonFile.Write(_path, onDisk);

        // 用改过的秘密重新登记。写盘成功后才做：写失败时磁盘上还是旧的那份，
        // 脱敏器也该继续认旧的。
        RegisterSecrets(settings);
    }
}
