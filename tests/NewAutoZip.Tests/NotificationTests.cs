using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using NewAutoZip.Core.Configuration;
using NewAutoZip.Core.Notifications;
using Xunit;

namespace NewAutoZip.Tests;

/// <summary>
/// 极简 HTTP 服务器，用来在不联网的前提下测真实的请求-响应往返。
///
/// 用裸 TcpListener 而不是 HttpListener：后者在 Windows 上非管理员身份注册 URL 前缀
/// 会失败（HTTP.SYS 的 URL ACL），测试不该要求管理员权限。
/// </summary>
internal sealed class FakeHttpServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<string> _bodies = [];
    private readonly object _gate = new();
    private readonly Func<int, (int Status, string Body)> _respond;

    private int _requests;

    /// <param name="respond">收到第 N 次请求（从 1 开始）时返回什么。</param>
    public FakeHttpServer(Func<int, (int Status, string Body)> respond)
    {
        _respond = respond;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();

        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;

        _ = Task.Run(AcceptLoopAsync);
    }

    public int Port { get; }

    public string Url => $"http://127.0.0.1:{Port}/webhook";

    public int RequestCount => Volatile.Read(ref _requests);

    public IReadOnlyList<string> Bodies
    {
        get { lock (_gate) { return [.. _bodies]; } }
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;

            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token);
            }
            catch (Exception)
            {
                return;     // 已停止
            }

            _ = Task.Run(() => HandleAsync(client));
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                using NetworkStream stream = client.GetStream();

                // 读到头部结束，再按 Content-Length 精确读正文。
                byte[] buffer = new byte[16 * 1024];
                StringBuilder raw = new();
                int contentLength = -1;
                int headerEnd = -1;
                int total = 0;

                while (total < buffer.Length)
                {
                    int read = await stream.ReadAsync(buffer.AsMemory(total), _cts.Token);

                    if (read == 0)
                    {
                        break;
                    }

                    total += read;
                    raw.Clear();
                    raw.Append(Encoding.UTF8.GetString(buffer, 0, total));

                    string text = raw.ToString();
                    headerEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);

                    if (headerEnd < 0)
                    {
                        continue;
                    }

                    if (contentLength < 0)
                    {
                        contentLength = ParseContentLength(text[..headerEnd]);
                    }

                    if (total - (headerEnd + 4) >= contentLength)
                    {
                        break;
                    }
                }

                string body = headerEnd >= 0 && contentLength > 0
                    ? Encoding.UTF8.GetString(buffer, headerEnd + 4, Math.Min(contentLength, total - headerEnd - 4))
                    : string.Empty;

                int n = Interlocked.Increment(ref _requests);

                lock (_gate)
                {
                    _bodies.Add(body);
                }

                (int status, string responseBody) = _respond(n);

                byte[] payload = Encoding.UTF8.GetBytes(responseBody);

                string head =
                    $"HTTP/1.1 {status} {Reason(status)}\r\n" +
                    "Content-Type: application/json; charset=utf-8\r\n" +
                    $"Content-Length: {payload.Length}\r\n" +
                    "Connection: close\r\n\r\n";

                await stream.WriteAsync(Encoding.UTF8.GetBytes(head), _cts.Token);
                await stream.WriteAsync(payload, _cts.Token);
                await stream.FlushAsync(_cts.Token);
            }
            catch (Exception)
            {
                // 测试服务器出错不该让测试挂在这里
            }
        }
    }

    private static int ParseContentLength(string headers)
    {
        foreach (string line in headers.Split("\r\n"))
        {
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(line["Content-Length:".Length..].Trim(), out int value))
            {
                return value;
            }
        }

        return 0;
    }

    private static string Reason(int status) => status switch
    {
        200 => "OK",
        401 => "Unauthorized",
        404 => "Not Found",
        429 => "Too Many Requests",
        500 => "Internal Server Error",
        503 => "Service Unavailable",
        _ => "Unknown",
    };

    public void Dispose()
    {
        try
        {
            _cts.Cancel();
            _listener.Stop();
            _cts.Dispose();
        }
        catch
        {
            // 忽略
        }
    }
}

/// <summary>
/// 载荷生成。旧版是手拼 JSON 字符串手写转义 ——
/// 文件名里一个引号、一个反斜杠、一个换行就能把整条消息弄坏，而且那一层完全没法测。
/// </summary>
public class NotifyPayloadTests
{
    private static readonly NotifyMessage Sample = new(
        NotifyEvent.PackCompleted,
        "打包完成",
        "文件：2026_09_01 报表.xlsx");

    private static async Task<JsonDocument> PayloadOfAsync(NotifierBase notifier, NotifyMessage message)
    {
        using HttpRequestMessage request = notifier.BuildRequest(message);
        Assert.NotNull(request.Content);

        string json = await request.Content!.ReadAsStringAsync();
        return JsonDocument.Parse(json);
    }

    [Fact]
    public async Task 企业微信载荷是合法JSON且转义了markdown标记()
    {
        using JsonDocument doc = await PayloadOfAsync(new WeComNotifier("https://example.invalid/h"), Sample);

        Assert.Equal("markdown", doc.RootElement.GetProperty("msgtype").GetString());

        string content = doc.RootElement.GetProperty("markdown").GetProperty("content").GetString()!;

        Assert.Contains("**打包完成**", content);

        // 文件名里的下划线必须被转义，否则消息里 "2026_09_01 报表" 会变成斜体乱码。
        Assert.Contains("2026\\_09\\_01", content);
    }

    [Fact]
    public async Task 钉钉载荷是纯文本且标题正文都在()
    {
        using JsonDocument doc = await PayloadOfAsync(new DingTalkNotifier("https://example.invalid/h"), Sample);

        Assert.Equal("text", doc.RootElement.GetProperty("msgtype").GetString());

        string content = doc.RootElement.GetProperty("text").GetProperty("content").GetString()!;

        Assert.Contains("打包完成", content);
        Assert.Contains("报表.xlsx", content);
    }

    [Fact]
    public async Task Telegram载荷带正确的会话Id且不使用parse_mode()
    {
        using JsonDocument doc = await PayloadOfAsync(new TelegramNotifier("BOT:TOKEN", "-100123456"), Sample);

        Assert.Equal("-100123456", doc.RootElement.GetProperty("chat_id").GetString());
        Assert.Contains("打包完成", doc.RootElement.GetProperty("text").GetString());

        // 一旦启用 parse_mode，文件名里的下划线或方括号会让 Telegram 直接 400，整条发不出去。
        Assert.False(doc.RootElement.TryGetProperty("parse_mode", out _));
    }

    [Fact]
    public void Telegram的Token被URL编码后放进地址()
    {
        // Token 里出现 ':' 是正常的（`123456:AAH...`），但整个 Token 是路径的一部分，必须编码。
        TelegramNotifier notifier = new("123:AA/BB", "42");

        using HttpRequestMessage request = notifier.BuildRequest(Sample);

        Assert.NotNull(request.RequestUri);
        Assert.DoesNotContain("AA/BB", request.RequestUri!.ToString());
        Assert.EndsWith("/sendMessage", request.RequestUri.AbsolutePath);
    }

    [Fact]
    public async Task 通用Webhook载荷是结构化的且事件名不本地化()
    {
        using JsonDocument doc = await PayloadOfAsync(
            new WebhookNotifier("https://example.invalid/h"),
            new NotifyMessage(NotifyEvent.Quarantined, "批次已隔离", "连续失败 6 次"));

        Assert.Equal("AutoZip", doc.RootElement.GetProperty("source").GetString());

        // 下游要能按事件类型分流，不该逼它去正则匹配中文。
        Assert.Equal("Quarantined", doc.RootElement.GetProperty("event").GetString());
        Assert.Equal("error", doc.RootElement.GetProperty("severity").GetString());
        Assert.Equal("批次已隔离", doc.RootElement.GetProperty("title").GetString());

        // 时间戳必须能被任何语言按 ISO-8601 解析回来。
        Assert.True(DateTimeOffset.TryParse(
            doc.RootElement.GetProperty("timestamp").GetString(), out _));
    }

    [Theory]
    [InlineData(NotifyEvent.Failure, "error")]
    [InlineData(NotifyEvent.Quarantined, "error")]
    [InlineData(NotifyEvent.DiskWarning, "warning")]
    [InlineData(NotifyEvent.PackCompleted, "info")]
    [InlineData(NotifyEvent.EngineStarted, "info")]
    public async Task 严重程度按事件正确映射(NotifyEvent e, string expected)
    {
        using JsonDocument doc = await PayloadOfAsync(
            new WebhookNotifier("https://example.invalid/h"), new NotifyMessage(e, "t", "b"));

        Assert.Equal(expected, doc.RootElement.GetProperty("severity").GetString());
    }

    [Fact]
    public async Task 特殊字符不会破坏载荷()
    {
        // 引号、反斜杠、换行、emoji、中文全角引号 —— 手拼 JSON 时每一个都是雷。
        NotifyMessage nasty = new(
            NotifyEvent.Failure,
            "失败：C:\\路径\\带\"引号\".7z",
            "原因：\n\t换行与制表符 🚨 “全角” \\ 结尾反斜杠\\");

        using JsonDocument doc = await PayloadOfAsync(new DingTalkNotifier("https://example.invalid/h"), nasty);

        string content = doc.RootElement.GetProperty("text").GetProperty("content").GetString()!;

        Assert.Contains("带\"引号\".7z", content);
        Assert.Contains("🚨", content);
        Assert.Contains("结尾反斜杠\\", content);
    }

    [Fact]
    public async Task 超长内容被截断而不是整条丢失()
    {
        // 超过渠道上限时服务端拒收整条消息。截断后带提示，总比什么都收不到好。
        NotifyMessage huge = new(NotifyEvent.Failure, "失败", new string('x', 50_000));

        using JsonDocument doc = await PayloadOfAsync(new WeComNotifier("https://example.invalid/h"), huge);

        string content = doc.RootElement.GetProperty("markdown").GetProperty("content").GetString()!;

        Assert.True(content.Length <= 1800, $"截断后仍有 {content.Length} 字符");
        Assert.Contains("已截断", content);
        Assert.Contains("失败", content);
    }
}

/// <summary>响应正文里的业务错误码解析。HTTP 200 不等于发送成功。</summary>
public class NotifyErrorParsingTests
{
    [Fact]
    public void 钉钉限流被识别为失败()
    {
        // 旧版只看状态码，把这个当成"发送成功" —— 用户根本不知道通知丢了。
        string? error = new DingTalkNotifier("https://example.invalid/h")
            .InspectBody("""{"errcode":130101,"errmsg":"send too fast"}""");

        Assert.NotNull(error);
        Assert.Contains("限流", error);
        Assert.Contains("130101", error);
    }

    [Fact]
    public void 企业微信机器人被移除时给出可操作的说明()
    {
        string? error = new WeComNotifier("https://example.invalid/h")
            .InspectBody("""{"errcode":93000,"errmsg":"invalid webhook url"}""");

        Assert.NotNull(error);
        Assert.Contains("重新获取", error);
    }

    [Fact]
    public void 错误码为零视为成功()
    {
        Assert.Null(new WeComNotifier("https://example.invalid/h")
            .InspectBody("""{"errcode":0,"errmsg":"ok"}"""));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("OK")]
    [InlineData("<html>not json</html>")]
    [InlineData("[1,2,3]")]
    [InlineData("null")]
    public void 无法解析的正文不制造假告警(string body)
    {
        // 自建 Webhook 什么都可能返回。状态码已经 2xx，别因为正文看不懂就报错。
        Assert.Null(new DingTalkNotifier("https://example.invalid/h").InspectBody(body));
        Assert.Null(new TelegramNotifier("t", "c").InspectBody(body));
    }

    [Fact]
    public void Telegram的ok为false时识别为失败()
    {
        string? error = new TelegramNotifier("t", "c")
            .InspectBody("""{"ok":false,"error_code":400,"description":"chat not found"}""");

        Assert.NotNull(error);
        Assert.Contains("400", error);
        Assert.Contains("chat not found", error);
    }

    [Fact]
    public void Telegram的ok为true时视为成功()
    {
        Assert.Null(new TelegramNotifier("t", "c")
            .InspectBody("""{"ok":true,"result":{"message_id":7}}"""));
    }
}

/// <summary>真实 HTTP 往返：成功、重试、放弃、取消。</summary>
public class NotifierTransportTests
{
    private static readonly NotifyMessage Msg = new(NotifyEvent.Failure, "测试", "正文");

    [Fact]
    public async Task 成功时只发一次()
    {
        using FakeHttpServer server = new(_ => (200, """{"errcode":0}"""));

        NotifyResult result = await new WebhookNotifier(server.Url).SendAsync(Msg, CancellationToken.None);

        Assert.True(result.Ok, result.Error);
        Assert.Equal(1, result.Attempts);
        Assert.Equal(1, server.RequestCount);
    }

    [Fact]
    public async Task 服务端五百错误会重试三次后放弃()
    {
        using FakeHttpServer server = new(_ => (500, "boom"));

        NotifyResult result = await new WebhookNotifier(server.Url).SendAsync(Msg, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(3, result.Attempts);
        Assert.Equal(3, server.RequestCount);
        Assert.Contains("500", result.Error);
    }

    [Fact]
    public async Task 第二次就成功时不再继续重试()
    {
        using FakeHttpServer server = new(n => n == 1 ? (503, "") : (200, ""));

        NotifyResult result = await new WebhookNotifier(server.Url).SendAsync(Msg, CancellationToken.None);

        Assert.True(result.Ok, result.Error);
        Assert.Equal(2, result.Attempts);
        Assert.Equal(2, server.RequestCount);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(404)]
    public async Task 配置类错误不重试(int status)
    {
        // 401/404 是地址或凭据写错了。重试三次只是把同一条错误刷三遍，还浪费时间。
        using FakeHttpServer server = new(_ => (status, "nope"));

        NotifyResult result = await new WebhookNotifier(server.Url).SendAsync(Msg, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(1, server.RequestCount);

        // 尝试次数必须如实 —— 报成 3 次会把用户引向"网络不稳定"，
        // 而真正的原因是地址或凭据写错了。
        Assert.Equal(1, result.Attempts);
        Assert.Contains(status.ToString(), result.Error);
    }

    [Fact]
    public async Task 限流会重试()
    {
        using FakeHttpServer server = new(n => n < 3 ? (429, "") : (200, ""));

        NotifyResult result = await new WebhookNotifier(server.Url).SendAsync(Msg, CancellationToken.None);

        Assert.True(result.Ok, result.Error);
        Assert.Equal(3, server.RequestCount);
    }

    [Fact]
    public async Task 正文里的错误码也算失败并如实上报()
    {
        using FakeHttpServer server = new(_ => (200, """{"errcode":93000,"errmsg":"gone"}"""));

        NotifyResult result = await new WeComNotifier(server.Url).SendAsync(Msg, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("93000", result.Error);
        Assert.Equal(1, server.RequestCount);      // 93000 不值得重试
    }

    [Fact]
    public async Task 正文里的限流错误会重试()
    {
        using FakeHttpServer server = new(n =>
            n < 2 ? (200, """{"errcode":130101,"errmsg":"too fast"}""") : (200, """{"errcode":0}"""));

        NotifyResult result = await new DingTalkNotifier(server.Url).SendAsync(Msg, CancellationToken.None);

        Assert.True(result.Ok, result.Error);
        Assert.Equal(2, server.RequestCount);
    }

    [Fact]
    public async Task 连不上时返回失败而不是抛异常()
    {
        // 这是全套设计里最关键的一条契约：通知失败绝不能变成异常穿到主流程去。
        // 旧版就是因为异常穿过了状态复位代码，才把"Webhook 挂了"变成"磁盘被压缩包填满"。
        NotifyResult result = await new WebhookNotifier("http://127.0.0.1:1/nothing-here")
            .SendAsync(Msg, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));

        // 错误信息必须是人能看懂并照着排查的，不是 "An error occurred while sending the request"。
        Assert.DoesNotContain("An error occurred", result.Error);
    }

    [Fact]
    public async Task 域名解析失败时给出可操作的说明()
    {
        NotifyResult result = await new WebhookNotifier("https://这个域名一定不存在.invalid/h")
            .SendAsync(Msg, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    [Fact]
    public async Task 已取消的令牌立即返回不发请求()
    {
        using FakeHttpServer server = new(_ => (200, ""));
        using CancellationTokenSource cts = new();
        await cts.CancelAsync();

        NotifyResult result = await new WebhookNotifier(server.Url).SendAsync(Msg, cts.Token);

        Assert.False(result.Ok);
        Assert.Equal(0, server.RequestCount);
    }

    [Fact]
    public async Task 重试等待期间被取消会立刻退出()
    {
        using FakeHttpServer server = new(_ => (500, ""));
        using CancellationTokenSource cts = new();

        Task<NotifyResult> sending = new WebhookNotifier(server.Url).SendAsync(Msg, cts.Token);

        // 第一次请求发出后、退避等待中取消 —— "停止"按钮必须立刻生效。
        await Task.Delay(150);
        await cts.CancelAsync();

        NotifyResult result = await sending;

        Assert.False(result.Ok);
        Assert.True(server.RequestCount < 3, $"取消后还继续发了 {server.RequestCount} 次");
    }

    [Fact]
    public async Task 请求体真的送到了服务端()
    {
        using FakeHttpServer server = new(_ => (200, ""));

        await new WebhookNotifier(server.Url).SendAsync(
            new NotifyMessage(NotifyEvent.DiskWarning, "磁盘不足", "只剩 1 GB"), CancellationToken.None);

        string body = Assert.Single(server.Bodies);

        using JsonDocument doc = JsonDocument.Parse(body);
        Assert.Equal("DiskWarning", doc.RootElement.GetProperty("event").GetString());
        Assert.Equal("磁盘不足", doc.RootElement.GetProperty("title").GetString());
    }
}

/// <summary>渠道工厂。旧版靠 URL 里含不含 "weixin" 猜渠道，两套规则还不一致。</summary>
public class NotifierFactoryTests
{
    private static AppSettings With(NotifierKind kind, string url = "https://example.com/hook") =>
        new() { NotifyChannel = kind, WebhookUrl = url };

    [Fact]
    public void 未选渠道时明确返回空并说明()
    {
        Assert.Null(NotifierFactory.TryCreate(With(NotifierKind.None), out string reason));
        Assert.Contains("未选择", reason);
    }

    [Theory]
    [InlineData(NotifierKind.WeCom, typeof(WeComNotifier))]
    [InlineData(NotifierKind.DingTalk, typeof(DingTalkNotifier))]
    [InlineData(NotifierKind.Webhook, typeof(WebhookNotifier))]
    public void 按显式选择造出对应渠道(NotifierKind kind, Type expected)
    {
        // 关键：地址里含什么字样都不影响 —— 只看用户选了哪个。
        INotifier? notifier = NotifierFactory.TryCreate(
            With(kind, "https://qyapi.weixin.qq.com/cgi-bin/webhook/send?key=x"), out string reason);

        Assert.NotNull(notifier);
        Assert.Equal(string.Empty, reason);
        Assert.IsType(expected, notifier);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("不是网址")]
    [InlineData("ftp://example.com/h")]
    [InlineData("file:///C:/x")]
    public void 非法地址被拒绝并说明原因(string url)
    {
        Assert.Null(NotifierFactory.TryCreate(With(NotifierKind.WeCom, url), out string reason));
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    [Fact]
    public void 地址两端的空白被自动去掉()
    {
        // 从浏览器复制 Webhook 地址时很容易带上首尾空白，这不该算配置错误。
        INotifier? notifier = NotifierFactory.TryCreate(
            With(NotifierKind.Webhook, "  https://example.com/hook  "), out _);

        Assert.NotNull(notifier);
    }

    [Fact]
    public void Telegram缺少任一项都被拒绝()
    {
        AppSettings onlyToken = new()
        {
            NotifyChannel = NotifierKind.Telegram,
            TelegramBotToken = "123:AA",
        };

        Assert.Null(NotifierFactory.TryCreate(onlyToken, out string r1));
        Assert.Contains("Chat ID", r1);

        AppSettings onlyChat = new()
        {
            NotifyChannel = NotifierKind.Telegram,
            TelegramChatId = "42",
        };

        Assert.Null(NotifierFactory.TryCreate(onlyChat, out string r2));
        Assert.Contains("Token", r2);
    }

    [Fact]
    public void Telegram配齐后可以造出来()
    {
        AppSettings s = new()
        {
            NotifyChannel = NotifierKind.Telegram,
            TelegramBotToken = " 123:AA ",
            TelegramChatId = " 42 ",
        };

        Assert.IsType<TelegramNotifier>(NotifierFactory.TryCreate(s, out _));
    }

    [Fact]
    public void 每个渠道都有可显示的名字()
    {
        foreach (NotifierKind kind in Enum.GetValues<NotifierKind>())
        {
            Assert.False(string.IsNullOrWhiteSpace(NotifierFactory.NameOf(kind)));
        }
    }
}

/// <summary>通知出口：事件过滤、日志、以及"永不抛异常"这条硬契约。</summary>
public class NotificationHubTests
{
    /// <summary>故意违反契约的渠道 —— 用来验证 hub 兜得住。</summary>
    private sealed class ThrowingNotifier : INotifier
    {
        public string DisplayName => "会抛异常的渠道";

        public Task<NotifyResult> SendAsync(NotifyMessage message, CancellationToken ct) =>
            throw new InvalidOperationException("我就是要抛");
    }

    private sealed class CountingNotifier : INotifier
    {
        public int Sent { get; private set; }

        public List<NotifyEvent> Events { get; } = [];

        public string DisplayName => "计数渠道";

        public Task<NotifyResult> SendAsync(NotifyMessage message, CancellationToken ct)
        {
            Sent++;
            Events.Add(message.Event);
            return Task.FromResult(NotifyResult.Success(1));
        }
    }

    private static NotificationHub HubWith(INotifier notifier, NotifyEvent enabled, RecordingLogger log)
    {
        // 构造函数是私有的（只能经 Create 走配置），测试用反射注入自定义渠道，
        // 这样可以在完全不联网的前提下覆盖事件过滤与异常兜底。
        object hub = Activator.CreateInstance(
            typeof(NotificationHub),
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            binder: null,
            args: [notifier, enabled, log, string.Empty],
            culture: null)!;

        return (NotificationHub)hub;
    }

    [Fact]
    public async Task 未勾选的事件被跳过且不产生请求()
    {
        CountingNotifier notifier = new();
        RecordingLogger log = new();
        NotificationHub hub = HubWith(notifier, NotifyEvent.Failure | NotifyEvent.Quarantined, log);

        await hub.SendAsync(new NotifyMessage(NotifyEvent.PackCompleted, "打包完成", ""), CancellationToken.None);
        await hub.SendAsync(new NotifyMessage(NotifyEvent.EngineStarted, "已启动", ""), CancellationToken.None);

        Assert.Equal(0, notifier.Sent);

        await hub.SendAsync(new NotifyMessage(NotifyEvent.Failure, "失败", ""), CancellationToken.None);

        Assert.Equal(1, notifier.Sent);
        Assert.Equal(NotifyEvent.Failure, notifier.Events[0]);
    }

    [Fact]
    public async Task 渠道违反契约抛异常时被兜住并记为缺陷()
    {
        // 这条是整个防爆链的最后一道闸。
        RecordingLogger log = new();
        NotificationHub hub = HubWith(new ThrowingNotifier(), NotifyEvent.All, log);

        NotifyResult result = await hub.SendAsync(
            new NotifyMessage(NotifyEvent.Failure, "失败", ""), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("我就是要抛", result.Error);
        Assert.True(log.Contains("这是实现缺陷"), log.Dump());
    }

    [Fact]
    public async Task 未配置时静默跳过()
    {
        RecordingLogger log = new();
        NotificationHub hub = NotificationHub.Create(new AppSettings(), log);

        Assert.False(hub.IsEnabled);
        Assert.Equal("未配置", hub.ChannelName);

        NotifyResult result = await hub.SendAsync(
            new NotifyMessage(NotifyEvent.Failure, "失败", ""), CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal(0, result.Attempts);
    }

    [Fact]
    public void 选了渠道却没配好时明确警告()
    {
        // 用户显然是想收通知的，静默失效比报错糟糕得多。
        RecordingLogger log = new();

        NotificationHub hub = NotificationHub.Create(
            new AppSettings { NotifyChannel = NotifierKind.DingTalk, WebhookUrl = "" }, log);

        Assert.False(hub.IsEnabled);
        Assert.True(log.Contains("未生效"), log.Dump());
        Assert.True(log.Contains("收不到任何提醒"), log.Dump());
        Assert.False(string.IsNullOrWhiteSpace(hub.DisabledReason));
    }

    [Fact]
    public void 配好渠道但一个事件都没勾时也要警告()
    {
        RecordingLogger log = new();

        NotificationHub hub = NotificationHub.Create(
            new AppSettings
            {
                NotifyChannel = NotifierKind.Webhook,
                WebhookUrl = "https://example.com/h",
                EnabledEvents = NotifyEvent.None,
            },
            log);

        Assert.False(hub.IsEnabled);
        Assert.True(log.Contains("没有勾选任何事件"), log.Dump());
    }

    [Fact]
    public void 配置正确时记录渠道名()
    {
        RecordingLogger log = new();

        NotificationHub hub = NotificationHub.Create(
            new AppSettings
            {
                NotifyChannel = NotifierKind.Webhook,
                WebhookUrl = "https://example.com/h",
                EnabledEvents = NotifyEvent.All,
            },
            log);

        Assert.True(hub.IsEnabled);
        Assert.Equal("通用 Webhook", hub.ChannelName);
        Assert.True(log.Contains("通知渠道：通用 Webhook"), log.Dump());
    }

    [Fact]
    public async Task 测试发送在未配置时立刻返回失败且不联网()
    {
        RecordingLogger log = new();

        NotifyResult result = await NotificationHub.TestAsync(
            new AppSettings { NotifyChannel = NotifierKind.Telegram }, log, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(0, result.Attempts);
        Assert.Contains("Token", result.Error);
    }

    [Fact]
    public async Task 测试发送真的会发出请求()
    {
        using FakeHttpServer server = new(_ => (200, ""));
        RecordingLogger log = new();

        NotifyResult result = await NotificationHub.TestAsync(
            new AppSettings
            {
                NotifyChannel = NotifierKind.Webhook,
                WebhookUrl = server.Url,

                // 测试发送要无视事件勾选 —— 否则用户勾了"仅失败"时按钮就永远没反应。
                EnabledEvents = NotifyEvent.None,
            },
            log,
            CancellationToken.None);

        Assert.True(result.Ok, result.Error);
        Assert.Equal(1, server.RequestCount);
        Assert.True(log.Contains("测试通知发送成功"), log.Dump());
    }

    [Fact]
    public async Task 通知失败只记日志不抛异常()
    {
        using FakeHttpServer server = new(_ => (500, "down"));
        RecordingLogger log = new();

        NotificationHub hub = NotificationHub.Create(
            new AppSettings
            {
                NotifyChannel = NotifierKind.Webhook,
                WebhookUrl = server.Url,
                EnabledEvents = NotifyEvent.All,
            },
            log);

        NotifyResult result = await hub.SendAsync(
            new NotifyMessage(NotifyEvent.Failure, "打包失败", "详情"), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.True(log.Contains("通知发送失败"), log.Dump());

        // 日志里必须留下原本要发的内容，否则用户永远不知道漏掉了什么。
        Assert.True(log.Contains("打包失败"), log.Dump());
    }
}

/// <summary>
/// 通知正文的三段式格式：第一行 <c>yyyy-MM-dd HH:mm:ss</c>、第二行 <c>【标签】</c>、随后是正文行。
/// 用户是按样例逐字提的要求，所以这里逐字断言 —— 格式一旦被后来的改动带跑，测试立刻红。
/// </summary>
public class NotifyFormatTests
{
    private static readonly DateTimeOffset At1810 =
        new(2026, 8, 24, 18, 10, 47, TimeSpan.FromHours(8));

    private static NotifyMessage Packed() => NotifyMessage.Create(
        NotifyEvent.PackCompleted,
        NotifyTag.Packed,
        At1810,
        "本次共 1 个文件打包成功。",
        "源文件总大小：4.72G",
        "文件列表：",
        "1. MT20210608_20260824_180100.bak（4.72G）",
        "压缩包：Backup_20260824_180907.7z",
        "压缩包大小：4.27G");

    [Fact]
    public void 正文第一行是时间第二行是方括号标签()
    {
        string[] lines = Packed().ToPlainText().Split(Environment.NewLine);

        Assert.Equal("2026-08-24 18:10:47", lines[0]);
        Assert.Equal("【打包】", lines[1]);
        Assert.Equal("本次共 1 个文件打包成功。", lines[2]);
        Assert.Equal("源文件总大小：4.72G", lines[3]);
    }

    [Fact]
    public void 时间不受系统区域设置影响()
    {
        // 阿拉伯语区的默认数字形状会把 2026-08-24 18:10:47 渲染成 ٢٠٢٦-٠٨-٢٤ ١٨:١٠:٤٧。
        System.Globalization.CultureInfo previous = System.Globalization.CultureInfo.CurrentCulture;

        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("ar-SA");
            Assert.Equal("2026-08-24 18:10:47", Packed().TimeText);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void 空行与null行被丢弃不留空白()
    {
        NotifyMessage m = NotifyMessage.Create(
            NotifyEvent.RoundFinished, NotifyTag.RoundFinished, At1810,
            "本次自动化处理已结束。",
            null,
            "   ",
            "共计耗时：0小时18分46秒");

        string[] lines = m.ToPlainText().Split(Environment.NewLine);

        Assert.Equal(4, lines.Length);        // 时间 + 【结束】 + 两行正文
        Assert.DoesNotContain(lines, string.IsNullOrWhiteSpace);
    }

    [Fact]
    public void 十二种事件各有自己的标签()
    {
        // 顺序 = 一次备份的时间线：启动 → 开工 → 第 N 次发现新文件 → 就绪 → 打包 → 上传 → 结束 → 收工，
        // 之后才是三个异常事件与【停止】。
        (NotifyEvent Event, string Tag)[] map =
        [
            (NotifyEvent.EngineStarted, "启动"),
            (NotifyEvent.ScheduleOpened, "开工"),
            (NotifyEvent.RoundStarted, "第 1 次发现新文件"),
            (NotifyEvent.FilesStable, "就绪"),
            (NotifyEvent.PackCompleted, "打包"),
            (NotifyEvent.UploadCompleted, "上传"),
            (NotifyEvent.RoundFinished, "结束"),
            (NotifyEvent.ScheduleClosed, "收工"),
            (NotifyEvent.Failure, "失败"),
            (NotifyEvent.Quarantined, "隔离"),
            (NotifyEvent.DiskWarning, "磁盘"),
            (NotifyEvent.EngineStopped, "停止"),
        ];

        foreach ((NotifyEvent evt, string tag) in map)
        {
            NotifyMessage m = NotifyMessage.Create(evt, tag, At1810, "正文");

            Assert.Contains($"【{tag}】", m.ToPlainText());
            Assert.Equal(tag, m.Tag);
        }

        // 每个标签都不一样，否则通知里根本分不清是哪类事件。
        Assert.Equal(map.Length, map.Select(x => x.Tag).Distinct().Count());

        // 表格要盖住枚举里的每一位。漏一个就意味着有个事件没人验证过它的标签。
        Assert.Equal(map.Length, SingleBitEvents().Count);
    }

    [Fact]
    public void 每一个事件位都在All里面()
    {
        // All 是"全都要"的意思。漏掉一位的后果很隐蔽：配置里写着 "All" 的老用户
        // 升级后<b>静默</b>收不到新事件，而界面上那个勾还是勾着的。
        foreach (NotifyEvent evt in SingleBitEvents())
        {
            Assert.True(NotifyEvent.All.HasFlag(evt), $"{evt} 不在 All 里");
        }
    }

    [Fact]
    public void 流程类事件不混进ProblemsOnly()
    {
        // ProblemsOnly = 只在出问题时打扰我，所以只该有这三位。
        Assert.Equal(
            NotifyEvent.Failure | NotifyEvent.Quarantined | NotifyEvent.DiskWarning,
            NotifyEvent.ProblemsOnly);

        foreach (NotifyEvent evt in new[]
        {
            NotifyEvent.EngineStarted, NotifyEvent.ScheduleOpened, NotifyEvent.RoundStarted,
            NotifyEvent.FilesStable, NotifyEvent.PackCompleted, NotifyEvent.UploadCompleted,
            NotifyEvent.RoundFinished, NotifyEvent.ScheduleClosed, NotifyEvent.EngineStopped,
        })
        {
            Assert.False(NotifyEvent.ProblemsOnly.HasFlag(evt), $"{evt} 不该在 ProblemsOnly 里");
        }
    }

    [Fact]
    public void 事件名字不能改()
    {
        // 设置里 EnabledEvents 是按<i>名字</i>序列化的（"PackCompleted, Failure"）。
        // 改名 = 用户已有配置反序列化失败、订阅静默清零，所以名字锁在这里。
        Assert.Equal(
            new[]
            {
                "DiskWarning", "EngineStarted", "EngineStopped", "Failure", "FilesStable",
                "PackCompleted", "Quarantined", "RoundFinished", "RoundStarted",
                "ScheduleClosed", "ScheduleOpened", "UploadCompleted",
            },
            SingleBitEvents().Select(e => e.ToString()).OrderBy(n => n, StringComparer.Ordinal));
    }

    /// <summary>枚举里所有"单独一位"的事件 —— 排除 None 和 All / ProblemsOnly 这类组合值。</summary>
    private static List<NotifyEvent> SingleBitEvents() =>
    [
        .. Enum.GetValues<NotifyEvent>()
            .Where(e => e != NotifyEvent.None && System.Numerics.BitOperations.PopCount((uint)e) == 1),
    ];

    [Fact]
    public void 标签用词锁定()
    {
        // 这些词会出现在用户手机上，也出现在设置页的勾选项里。
        // 改动它们要连设置页文案一起改，所以在这里钉住。
        Assert.Equal("启动", NotifyTag.EngineStarted);
        Assert.Equal("开工", NotifyTag.ScheduleOpened);

        // 唯一带编号的标签：N 是本轮第几次通报（一条通知 +1，与那一条带了几个文件无关），
        // 和正文里"发现新文件：1. 2. 3."那份每条各自从 1 数的清单不是一回事。
        // 原来这里固定是"开始"，一轮里发几次就是几条一模一样的【开始】，用户分不清是新文件还是重复播报。
        Assert.Equal("第 1 次发现新文件", NotifyTag.Discovered(1));
        Assert.Equal("第 2 次发现新文件", NotifyTag.Discovered(2));
        Assert.Equal("第 12 次发现新文件", NotifyTag.Discovered(12));

        Assert.Equal("就绪", NotifyTag.FilesStable);
        Assert.Equal("打包", NotifyTag.Packed);
        Assert.Equal("上传", NotifyTag.Uploaded);
        Assert.Equal("结束", NotifyTag.RoundFinished);

        // 【取消】和【结束】共用 NotifyEvent.RoundFinished 那一位，所以用词必须不同：
        // 正文里一个压缩包都没有，标签得让人一眼看出这一轮是空手收场。
        Assert.Equal("取消", NotifyTag.RoundCancelled);
        Assert.NotEqual(NotifyTag.RoundFinished, NotifyTag.RoundCancelled);

        Assert.Equal("收工", NotifyTag.ScheduleClosed);
        Assert.Equal("失败", NotifyTag.Failure);
        Assert.Equal("隔离", NotifyTag.Quarantined);
        Assert.Equal("磁盘", NotifyTag.Disk);
        Assert.Equal("停止", NotifyTag.EngineStopped);
        Assert.Equal("测试", NotifyTag.Test);
    }

    [Fact]
    public void 每个渠道都能收到时间和标签()
    {
        NotifyMessage m = Packed();

        Assert.Contains("2026-08-24 18:10:47", m.ToPlainText());
        Assert.Contains("【打包】", m.ToPlainText());
    }

    [Fact]
    public async Task 企业微信正文把标签加粗且保留时间()
    {
        using HttpRequestMessage request = new WeComNotifier("https://example.invalid/h").BuildRequest(Packed());
        using JsonDocument doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());

        string content = doc.RootElement.GetProperty("markdown").GetProperty("content").GetString()!;

        Assert.StartsWith("2026-08-24 18:10:47", content);
        Assert.Contains("**【打包】**", content);

        // 文件名里的下划线仍然要转义，否则 markdown 会把它当斜体标记。
        Assert.Contains(@"MT20210608\_20260824\_180100.bak", content);
    }

    [Fact]
    public async Task 钉钉与Telegram发的是同一段纯文本()
    {
        NotifyMessage m = Packed();
        string expected = m.ToPlainText();

        using HttpRequestMessage ding = new DingTalkNotifier("https://example.invalid/h").BuildRequest(m);
        using JsonDocument dingDoc = JsonDocument.Parse(await ding.Content!.ReadAsStringAsync());

        using HttpRequestMessage tg = new TelegramNotifier("T", "42").BuildRequest(m);
        using JsonDocument tgDoc = JsonDocument.Parse(await tg.Content!.ReadAsStringAsync());

        Assert.Equal(expected, dingDoc.RootElement.GetProperty("text").GetProperty("content").GetString());
        Assert.Equal(expected, tgDoc.RootElement.GetProperty("text").GetString());
    }

    [Fact]
    public async Task 通用Webhook以JSON推送且字段可被下游分流()
    {
        using HttpRequestMessage request = new WebhookNotifier("https://example.invalid/h").BuildRequest(Packed());

        Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);

        using JsonDocument doc = JsonDocument.Parse(await request.Content.ReadAsStringAsync());
        JsonElement root = doc.RootElement;

        // 事件名用不变的英文枚举名：下游不该去正则匹配中文。
        Assert.Equal("PackCompleted", root.GetProperty("event").GetString());
        Assert.Equal("打包", root.GetProperty("tag").GetString());
        Assert.Equal("2026-08-24 18:10:47", root.GetProperty("time").GetString());
        Assert.Equal("info", root.GetProperty("severity").GetString());

        // text 就是企业微信/钉钉/Telegram 看到的那一整段，想直接转发的下游取它即可。
        string text = root.GetProperty("text").GetString()!;
        Assert.StartsWith("2026-08-24 18:10:47", text);
        Assert.Contains("【打包】", text);
        Assert.Contains("压缩包大小：4.27G", text);

        // 带时区偏移的 ISO-8601，能被 DateTimeOffset.Parse 原样读回。
        Assert.True(DateTimeOffset.TryParse(root.GetProperty("timestamp").GetString(), out _));
    }

    [Theory]
    [InlineData(NotifyEvent.RoundStarted, "info")]
    [InlineData(NotifyEvent.RoundFinished, "info")]
    [InlineData(NotifyEvent.ScheduleOpened, "info")]
    [InlineData(NotifyEvent.FilesStable, "info")]
    [InlineData(NotifyEvent.ScheduleClosed, "info")]
    [InlineData(NotifyEvent.Failure, "error")]
    [InlineData(NotifyEvent.Quarantined, "error")]
    [InlineData(NotifyEvent.DiskWarning, "warning")]
    public async Task Webhook按事件给出严重程度(NotifyEvent evt, string severity)
    {
        using HttpRequestMessage request = new WebhookNotifier("https://example.invalid/h")
            .BuildRequest(NotifyMessage.Create(evt, "x", At1810, "正文"));

        using JsonDocument doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());

        Assert.Equal(severity, doc.RootElement.GetProperty("severity").GetString());
    }

    [Fact]
    public async Task 测试发送用的也是同一套格式()
    {
        using FakeHttpServer server = new(_ => (200, "{}"));
        RecordingLogger log = new();

        NotifyResult result = await NotificationHub.TestAsync(
            new AppSettings { NotifyChannel = NotifierKind.Webhook, WebhookUrl = server.Url },
            log,
            CancellationToken.None);

        Assert.True(result.Ok, result.Error);

        using JsonDocument doc = JsonDocument.Parse(server.Bodies[0]);

        Assert.Equal("测试", doc.RootElement.GetProperty("tag").GetString());
        Assert.Contains("【测试】", doc.RootElement.GetProperty("text").GetString());
    }
}
