namespace NewAutoZip.Core.Configuration;

/// <summary>
/// settings.json 的读写。落盘前把密码 / Webhook / Telegram Token 用 DPAPI 加密，
/// 读取后解密回明文供程序使用。
/// </summary>
public sealed class SettingsStore
{
    private readonly string _path;

    public SettingsStore(string path) => _path = path;

    public string Path => _path;

    /// <summary>
    /// 读取配置。任何失败都返回一份默认配置，并通过 <paramref name="warning"/> 说明原因，
    /// 绝不因为配置文件损坏而让程序起不来。
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

        loaded.Password = SecretProtector.Unprotect(loaded.Password);
        loaded.WebhookUrl = SecretProtector.Unprotect(loaded.WebhookUrl);
        loaded.TelegramBotToken = SecretProtector.Unprotect(loaded.TelegramBotToken);
        loaded.TelegramChatId = SecretProtector.Unprotect(loaded.TelegramChatId);

        loaded.ExcludePatterns ??= [];
        loaded.ClampToLimits();

        return loaded;
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
    }
}
