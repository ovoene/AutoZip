using NewAutoZip.Core.Configuration;
using NewAutoZip.Core.Diagnostics;

namespace NewAutoZip.Core.Notifications;

/// <summary>
/// 按配置造出通知渠道。
///
/// 旧版是"看 URL 里含不含 weixin / dingtalk 来猜"，而各渠道自己的 <c>IsEnabled</c>
/// 用的又是另一套前缀规则，两者不一致时界面显示已启用、消息却被静默丢弃。
/// 新版渠道由用户显式选择，配置不完整时<b>明确返回 null 并说明原因</b>。
/// </summary>
public static class NotifierFactory
{
    /// <param name="reason">返回 null 时说明为什么造不出来，可直接显示给用户。</param>
    public static INotifier? TryCreate(AppSettings settings, out string reason)
    {
        switch (settings.NotifyChannel)
        {
            case NotifierKind.None:
                reason = "未选择通知渠道。";
                return null;

            case NotifierKind.Telegram:
                if (string.IsNullOrWhiteSpace(settings.TelegramBotToken))
                {
                    reason = "未填 Telegram Bot Token。";
                    return null;
                }

                if (string.IsNullOrWhiteSpace(settings.TelegramChatId))
                {
                    reason = "未填 Telegram Chat ID。";
                    return null;
                }

                reason = string.Empty;
                return new TelegramNotifier(settings.TelegramBotToken.Trim(), settings.TelegramChatId.Trim());

            case NotifierKind.WeCom:
            case NotifierKind.DingTalk:
            case NotifierKind.Webhook:
                if (!ValidUrl(settings.WebhookUrl, out string urlReason))
                {
                    reason = urlReason;
                    return null;
                }

                string url = settings.WebhookUrl.Trim();

                reason = string.Empty;
                return settings.NotifyChannel switch
                {
                    NotifierKind.WeCom => new WeComNotifier(url),
                    NotifierKind.DingTalk => new DingTalkNotifier(url),
                    _ => new WebhookNotifier(url),
                };

            default:
                reason = $"未知的通知渠道：{settings.NotifyChannel}。";
                return null;
        }
    }

    /// <summary>给界面用的渠道名，不需要造实例。</summary>
    public static string NameOf(NotifierKind kind) => kind switch
    {
        NotifierKind.None => "不发送通知",
        NotifierKind.WeCom => "企业微信群机器人",
        NotifierKind.DingTalk => "钉钉群机器人",
        NotifierKind.Telegram => "Telegram 机器人",
        NotifierKind.Webhook => "通用 Webhook",
        _ => kind.ToString(),
    };

    private static bool ValidUrl(string? url, out string reason)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            reason = "未填 Webhook 地址。";
            return false;
        }

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            reason = "Webhook 地址不是合法的 http/https URL。";
            return false;
        }

        reason = string.Empty;
        return true;
    }
}

/// <summary>
/// 真正的通知出口：事件过滤 + 日志记录 + 绝不抛异常。
///
/// 引擎只跟这一个类打交道。它保证三件事：
/// <list type="number">
/// <item><see cref="SendAsync"/> 永不抛异常，也永不让 <c>OperationCanceledException</c> 逃出去 ——
///       旧版正是通知抛异常穿过状态复位代码，才把"钉钉限流"变成了"磁盘被压缩包填满"。</item>
/// <item>未勾选的事件直接跳过，不产生任何网络请求。</item>
/// <item>成功与失败都记日志，且经过脱敏（Webhook 地址与 Token 都是机密）。</item>
/// </list>
/// </summary>
public sealed class NotificationHub : INotificationHub
{
    private readonly INotifier? _notifier;
    private readonly NotifyEvent _enabled;
    private readonly IAppLogger _log;
    private readonly string _disabledReason;

    private NotificationHub(INotifier? notifier, NotifyEvent enabled, IAppLogger log, string disabledReason)
    {
        _notifier = notifier;
        _enabled = enabled;
        _log = log;
        _disabledReason = disabledReason;
    }

    /// <summary>
    /// 按配置构造。配置不完整时返回一个"啥也不发"的 hub 并记一条警告 ——
    /// 而不是抛异常挡住引擎启动。备份本身比通知重要。
    /// </summary>
    public static NotificationHub Create(AppSettings settings, IAppLogger log)
    {
        INotifier? notifier = NotifierFactory.TryCreate(settings, out string reason);

        if (notifier is null)
        {
            if (settings.NotifyChannel != NotifierKind.None)
            {
                // 用户选了渠道却没配好 —— 这是他大概率想收通知，必须说出来。
                log.Warn($"通知渠道「{NotifierFactory.NameOf(settings.NotifyChannel)}」未生效：{reason}" +
                         "备份会照常运行，但你收不到任何提醒。");
            }

            return new NotificationHub(null, NotifyEvent.None, log, reason);
        }

        if (settings.EnabledEvents == NotifyEvent.None)
        {
            log.Warn($"已配置「{notifier.DisplayName}」但没有勾选任何事件，实际不会发出任何通知。");
        }
        else
        {
            log.Info($"通知渠道：{notifier.DisplayName}");
        }

        return new NotificationHub(notifier, settings.EnabledEvents, log, string.Empty);
    }

    public string ChannelName => _notifier?.DisplayName ?? "未配置";

    public bool IsEnabled => _notifier is not null && _enabled != NotifyEvent.None;

    /// <summary>未生效时的原因，界面可以直接显示。</summary>
    public string DisabledReason => _disabledReason;

    public async Task<NotifyResult> SendAsync(NotifyMessage message, CancellationToken ct)
    {
        if (_notifier is null || (_enabled & message.Event) == 0)
        {
            return NotifyResult.Skipped;
        }

        NotifyResult result;

        try
        {
            result = await _notifier.SendAsync(message, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // NotifierBase 已经接住一切，走到这里说明某个实现违反了契约。
            // 即便如此也绝不让异常继续往上跑 —— 这正是旧版磁盘被填满的根因链条起点。
            _log.Error($"通知渠道「{_notifier.DisplayName}」抛出了异常（这是实现缺陷）。", ex);
            return NotifyResult.Failure(ex.Message, 0);
        }

        if (result.Ok)
        {
            _log.Debug($"通知已发送（{message.Event}，第 {result.Attempts} 次尝试成功）：{message.Title}");
        }
        else
        {
            // 只记日志，绝不影响主流程。
            _log.Warn($"通知发送失败（{message.Event}，尝试 {result.Attempts} 次）：{result.Error}" +
                      $"｜原本要发的内容：{message.Title}");
        }

        return result;
    }

    /// <summary>
    /// 界面上「测试发送」按钮用的：忽略事件勾选，直接发一条。
    /// 旧版没有这个功能，用户只能等到真出事时才发现通知根本没通。
    /// </summary>
    public static async Task<NotifyResult> TestAsync(
        AppSettings settings, IAppLogger log, CancellationToken ct)
    {
        INotifier? notifier = NotifierFactory.TryCreate(settings, out string reason);

        if (notifier is null)
        {
            return NotifyResult.Failure(reason, 0);
        }

        // 用与真实通知完全一样的三段式渲染，这样"测试通了"就真的等于"以后收到的长这样"。
        NotifyMessage message = NotifyMessage.Create(
            NotifyEvent.EngineStarted,
            NotifyTag.Test,
            DateTimeOffset.Now,
            $"如果你看到这条消息，说明「{notifier.DisplayName}」配置正确。",
            $"来自计算机：{MachineName()}",
            "正式通知的格式与这条一致：第一行时间，第二行【事件标签】，随后是正文。");

        try
        {
            NotifyResult result = await notifier.SendAsync(message, ct).ConfigureAwait(false);

            log.Info(result.Ok
                ? $"测试通知发送成功（{notifier.DisplayName}，第 {result.Attempts} 次尝试）。"
                : $"测试通知发送失败（{notifier.DisplayName}）：{result.Error}");

            return result;
        }
        catch (Exception ex)
        {
            log.Error("测试通知过程中出现异常。", ex);
            return NotifyResult.Failure($"{ex.GetType().Name}：{ex.Message}", 0);
        }
    }

    private static string MachineName()
    {
        try
        {
            return Environment.MachineName;
        }
        catch (Exception)
        {
            return "未知";
        }
    }
}
