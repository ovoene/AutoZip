using System.Text.Json;
using System.Text.Json.Serialization;

using NewAutoZip.Core.Configuration;

namespace NewAutoZip.Core.Notifications;

/// <summary>
/// 企业微信群机器人。
///
/// 旧版这个渠道有个尤其隐蔽的 bug：<c>WeComWebhookNotifier.IsEnabled</c> 要求地址以
/// <c>https://qyapi.weixin.qq.com/</c> 开头，而选择渠道的判断只看 URL 里有没有 "weixin"。
/// 于是填了个别的含 weixin 的地址时，界面显示"✔ 企业微信群"，通知却被静默丢弃 ——
/// 用户以为配好了，实际一条都收不到。新版渠道由用户显式选择，地址不合规就在启动前报错。
/// </summary>
public sealed class WeComNotifier : NotifierBase
{
    /// <summary>企业微信 markdown 正文上限 4096 字节，按字符留足余量。</summary>
    private const int MaxContentChars = 1800;

    private readonly string _webhookUrl;

    public WeComNotifier(string webhookUrl) => _webhookUrl = webhookUrl;

    public override string DisplayName => "企业微信群机器人";

    protected internal override HttpRequestMessage BuildRequest(NotifyMessage message)
    {
        // markdown 而不是 text：可以给标签加粗，一眼能看出是哪一类事件。
        string content = string.IsNullOrEmpty(message.Tag)
            ? $"**{Escape(message.Title)}**"
            : $"{message.TimeText}{Environment.NewLine}**【{Escape(message.Tag)}】**";

        if (!string.IsNullOrEmpty(message.Body))
        {
            content += Environment.NewLine + Escape(message.Body);
        }

        WeComPayload payload = new()
        {
            MsgType = "markdown",
            Markdown = new WeComMarkdown { Content = Clamp(content, MaxContentChars) },
        };

        return new HttpRequestMessage(HttpMethod.Post, _webhookUrl) { Content = JsonBody(payload) };
    }

    protected internal override string? InspectBody(string body) => WeChatStyleError.Inspect(body, "企业微信");

    /// <summary>
    /// markdown 里的下划线和星号会被当成格式标记。文件名里出现 <c>_</c> 很常见
    /// （<c>2026_09_01_备份.7z</c>），不转义的话消息里会莫名变成斜体。
    /// </summary>
    private static string Escape(string text) =>
        text.Replace("*", "\\*", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal)
            .Replace("`", "\\`", StringComparison.Ordinal);

    private sealed class WeComPayload
    {
        [JsonPropertyName("msgtype")]
        public string MsgType { get; set; } = "markdown";

        [JsonPropertyName("markdown")]
        public WeComMarkdown Markdown { get; set; } = new();
    }

    private sealed class WeComMarkdown
    {
        [JsonPropertyName("content")]
        public string Content { get; set; } = string.Empty;
    }
}

/// <summary>
/// 钉钉群机器人。
///
/// 钉钉是四个渠道里最容易限流的（每机器人每分钟 20 条），而限流的表达方式是
/// <b>HTTP 200 + errcode 非 0</b>。旧版只看状态码，把限流当成发送成功，
/// 用户完全不知道通知丢了。这里必须解析正文。
/// </summary>
public sealed class DingTalkNotifier : NotifierBase
{
    private const int MaxContentChars = 4000;

    private readonly string _webhookUrl;

    public DingTalkNotifier(string webhookUrl) => _webhookUrl = webhookUrl;

    public override string DisplayName => "钉钉群机器人";

    protected internal override HttpRequestMessage BuildRequest(NotifyMessage message)
    {
        DingTalkPayload payload = new()
        {
            MsgType = "text",
            Text = new DingTalkText { Content = Clamp(message.ToPlainText(), MaxContentChars) },
        };

        return new HttpRequestMessage(HttpMethod.Post, _webhookUrl) { Content = JsonBody(payload) };
    }

    protected internal override string? InspectBody(string body) => WeChatStyleError.Inspect(body, "钉钉");

    private sealed class DingTalkPayload
    {
        [JsonPropertyName("msgtype")]
        public string MsgType { get; set; } = "text";

        [JsonPropertyName("text")]
        public DingTalkText Text { get; set; } = new();
    }

    private sealed class DingTalkText
    {
        [JsonPropertyName("content")]
        public string Content { get; set; } = string.Empty;
    }
}

/// <summary>
/// Telegram Bot。
///
/// 注意 Token 只出现在 URL 里，而 <see cref="Diagnostics.SecretRedactor"/> 已把它登记为机密，
/// 所以即便某条日志带上了完整 URL 也会被脱敏 —— 旧版的日志里 Token 是明文。
/// </summary>
public sealed class TelegramNotifier : NotifierBase
{
    /// <summary>Telegram 单条消息上限 4096 字符。</summary>
    private const int MaxContentChars = 4000;

    private readonly string _botToken;
    private readonly string _chatId;

    public TelegramNotifier(string botToken, string chatId)
    {
        _botToken = botToken;
        _chatId = chatId;
    }

    public override string DisplayName => "Telegram 机器人";

    protected internal override HttpRequestMessage BuildRequest(NotifyMessage message)
    {
        // 不用 parse_mode：一旦文件名里带下划线或方括号，Markdown/HTML 解析会直接 400，
        // 消息整条发不出去。纯文本永远发得出去，而这正是通知该有的可靠性。
        TelegramPayload payload = new()
        {
            ChatId = _chatId,
            Text = Clamp(message.ToPlainText(), MaxContentChars),
            DisableNotification = false,
        };

        string url = $"https://api.telegram.org/bot{Uri.EscapeDataString(_botToken)}/sendMessage";

        return new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonBody(payload) };
    }

    protected internal override string? InspectBody(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(body);

            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (doc.RootElement.TryGetProperty("ok", out JsonElement ok)
                && ok.ValueKind == JsonValueKind.False)
            {
                string description = doc.RootElement.TryGetProperty("description", out JsonElement d)
                    ? d.GetString() ?? "无说明"
                    : "无说明";

                int code = doc.RootElement.TryGetProperty("error_code", out JsonElement c)
                           && c.TryGetInt32(out int parsed)
                    ? parsed
                    : 0;

                return $"Telegram 返回失败（error_code={code}）：{description}";
            }
        }
        catch (JsonException)
        {
            // 不是 JSON 就不追究，状态码已经是 2xx。
        }

        return null;
    }

    private sealed class TelegramPayload
    {
        [JsonPropertyName("chat_id")]
        public string ChatId { get; set; } = string.Empty;

        [JsonPropertyName("text")]
        public string Text { get; set; } = string.Empty;

        [JsonPropertyName("disable_notification")]
        public bool DisableNotification { get; set; }
    }
}

/// <summary>
/// 通用 Webhook（任意 HTTP 接收端：自动化平台 / Home Assistant / 自建服务）。
///
/// 发结构化 JSON 而不是拼好的文本 —— 这样下游可以按事件类型分流，
/// 而不是去正则匹配一段中文。事件名用不变的英文枚举名，本地化文本另外给。
/// <c>text</c> 字段是渲染好的完整消息（就是企业微信/钉钉里看到的那一段），
/// 下游想直接转发就取它，想自己排版就取 <c>tag</c> / <c>time</c> / <c>body</c>。
/// </summary>
public sealed class WebhookNotifier : NotifierBase
{
    private readonly string _webhookUrl;

    public WebhookNotifier(string webhookUrl) => _webhookUrl = webhookUrl;

    public override string DisplayName => "通用 Webhook";

    protected internal override HttpRequestMessage BuildRequest(NotifyMessage message)
    {
        WebhookPayload payload = new()
        {
            Source = AppPaths.ProductName,
            Event = message.Event.ToString(),
            Severity = Severity(message.Event),
            Tag = message.Tag,
            Time = message.TimeText,
            Title = message.Title,
            Body = message.Body,
            Text = message.ToPlainText(),
            Machine = SafeMachineName(),

            // 明确带上时区偏移的 ISO-8601。旧版 checkpoint 就是因为用了本地格式化字符串，
            // 换个系统区域设置就解析不回来。
            TimestampUtc = DateTimeOffset.UtcNow.ToString("O"),
        };

        return new HttpRequestMessage(HttpMethod.Post, _webhookUrl) { Content = JsonBody(payload) };
    }

    /// <summary>给下游一个不必理解中文就能分流的严重程度。</summary>
    private static string Severity(NotifyEvent e) => e switch
    {
        NotifyEvent.Failure or NotifyEvent.Quarantined => "error",
        NotifyEvent.DiskWarning => "warning",
        _ => "info",
    };

    private static string SafeMachineName()
    {
        try
        {
            return Environment.MachineName;
        }
        catch (Exception)
        {
            return "unknown";
        }
    }

    private sealed class WebhookPayload
    {
        [JsonPropertyName("source")]
        public string Source { get; set; } = string.Empty;

        [JsonPropertyName("event")]
        public string Event { get; set; } = string.Empty;

        [JsonPropertyName("severity")]
        public string Severity { get; set; } = string.Empty;

        [JsonPropertyName("tag")]
        public string Tag { get; set; } = string.Empty;

        [JsonPropertyName("time")]
        public string Time { get; set; } = string.Empty;

        [JsonPropertyName("title")]
        public string Title { get; set; } = string.Empty;

        [JsonPropertyName("body")]
        public string Body { get; set; } = string.Empty;

        /// <summary>渲染好的完整消息，与企业微信/钉钉/Telegram 收到的文本一致。</summary>
        [JsonPropertyName("text")]
        public string Text { get; set; } = string.Empty;

        [JsonPropertyName("machine")]
        public string Machine { get; set; } = string.Empty;

        [JsonPropertyName("timestamp")]
        public string TimestampUtc { get; set; } = string.Empty;
    }
}

/// <summary>
/// 企业微信与钉钉共用同一套 <c>{"errcode":n,"errmsg":"..."}</c> 约定，
/// 而且都用 HTTP 200 承载业务失败。解析逻辑抽在这里，两边不必各写一份。
/// </summary>
internal static class WeChatStyleError
{
    /// <returns>null 表示成功。</returns>
    internal static string? Inspect(string body, string channel)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            // 正文空但状态码 2xx：无从判断，按成功处理，不制造假告警。
            return null;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(body);

            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("errcode", out JsonElement code)
                || !code.TryGetInt32(out int errcode)
                || errcode == 0)
            {
                return null;
            }

            string errmsg = doc.RootElement.TryGetProperty("errmsg", out JsonElement m)
                ? m.GetString() ?? string.Empty
                : string.Empty;

            return $"{channel}返回失败（errcode={errcode}）：{Explain(errcode, errmsg)}";
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>把最常见的几个错误码翻成能直接照着改的说明。</summary>
    private static string Explain(int errcode, string errmsg) => errcode switch
    {
        93000 => $"机器人 Webhook 地址无效或已被移除（{errmsg}）。请到群设置里重新获取。",
        45009 => $"接口调用超过频率限制，已被限流（{errmsg}）。请降低通知频率或减少勾选的事件。",
        130101 => $"发送过于频繁，已被限流（{errmsg}）。",
        310000 => $"关键词校验未通过或 IP 不在白名单（{errmsg}）。",
        _ => string.IsNullOrWhiteSpace(errmsg) ? "无说明。" : errmsg,
    };
}
