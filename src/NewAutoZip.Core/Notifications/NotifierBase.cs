using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

using NewAutoZip.Core.Configuration;

namespace NewAutoZip.Core.Notifications;

/// <summary>
/// 所有 HTTP 通知渠道的共同骨架。
///
/// 这个基类存在的唯一理由，是把旧版散在四个类里、四种写法、四种 bug 的逻辑收成一份：
///
/// <list type="bullet">
/// <item>旧版每个渠道各自 <c>new HttpClient()</c>。频繁新建会耗尽 socket
///       （TIME_WAIT 堆积），而且长期存活的实例又拿不到 DNS 变更。这里用<b>共享静态实例</b>。</item>
/// <item>旧版三个渠道都调 <c>EnsureSuccessStatusCode()</c>，异常直接往上抛。
///       这里<b>接住一切</b>，用 <see cref="NotifyResult"/> 表达失败。</item>
/// <item>旧版手拼 JSON 字符串、手写转义。文件名里一个引号或反斜杠就能把载荷弄坏。
///       这里统一走 <see cref="JsonSerializer"/>。</item>
/// <item>旧版没有超时、没有重试。这里 15 秒超时 + 3 次带抖动的重试，
///       且只对<b>值得重试</b>的错误重试（网络抖动、限流、5xx），
///       配置错误（401/404）立刻放弃 —— 重试它只是浪费时间并刷日志。</item>
/// </list>
/// </summary>
public abstract class NotifierBase : INotifier
{
    /// <summary>
    /// 全程序共用一个 HttpClient。
    ///
    /// <c>PooledConnectionLifetime</c> 让连接定期重建，这样既不会耗尽 socket，
    /// 也不会像"永久单例 HttpClient"那样长期缓存住过期的 DNS 解析结果。
    /// </summary>
    private static readonly HttpClient Http = CreateClient();

    /// <summary>单次请求的超时。通知不重要到值得让主流程多等。</summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    protected const int MaxAttempts = 3;

    /// <summary>紧凑输出，且不转义非 ASCII —— 中文文件名要在 JSON 里保持可读。</summary>
    protected static readonly JsonSerializerOptions Json = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    public abstract string DisplayName { get; }

    /// <summary>
    /// 渠道自己造请求。抛异常也没关系，<see cref="SendAsync"/> 会接住。
    ///
    /// 声明成 <c>protected internal</c> 是为了让测试能直接检查生成的 JSON 载荷 ——
    /// 旧版手拼 JSON 手写转义，文件名里一个引号就能把消息弄坏，而这一层当时根本无法测。
    /// </summary>
    protected internal abstract HttpRequestMessage BuildRequest(NotifyMessage message);

    /// <summary>
    /// 渠道自己判断响应体是否真的成功。
    ///
    /// 企业微信和钉钉都会在 HTTP 200 里用 <c>errcode != 0</c> 表示失败 ——
    /// 只看状态码会把"钉钉限流"当成"发送成功"。
    /// </summary>
    /// <returns>null 表示成功；否则是给人看的失败原因。</returns>
    protected internal virtual string? InspectBody(string body) => null;

    public async Task<NotifyResult> SendAsync(NotifyMessage message, CancellationToken ct)
    {
        string? lastError = null;

        // 如实记录真正发出去了几次。不能直接拿 MaxAttempts 交差 ——
        // 401/404 这类配置错误只试一次就该放弃，若报成"尝试 3 次"，
        // 用户会以为是网络不稳定而反复重试，真正的原因（地址写错了）反而被掩盖。
        int made = 0;

        for (int attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            if (ct.IsCancellationRequested)
            {
                // 停止请求优先于通知。已经试过几次就如实说几次。
                return NotifyResult.Failure("已取消。", made);
            }

            made = attempt;

            (bool ok, string? error, bool retryable) = await AttemptAsync(message, ct).ConfigureAwait(false);

            if (ok)
            {
                return NotifyResult.Success(attempt);
            }

            lastError = error;

            if (!retryable || attempt == MaxAttempts)
            {
                break;
            }

            try
            {
                await Task.Delay(BackoffFor(attempt), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return NotifyResult.Failure(lastError ?? "已取消。", made);
            }
        }

        return NotifyResult.Failure(lastError ?? "未知错误。", made);
    }

    /// <summary>发一次。<b>绝不抛异常</b>，一切都翻译成返回值。</summary>
    private async Task<(bool Ok, string? Error, bool Retryable)> AttemptAsync(
        NotifyMessage message, CancellationToken ct)
    {
        try
        {
            using HttpRequestMessage request = BuildRequest(message);

            // 把外部取消和自身超时合起来：Stop 要能立刻打断，卡住的服务器也不能拖着我们。
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(RequestTimeout);

            using HttpResponseMessage response =
                await Http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token)
                    .ConfigureAwait(false);

            string body = await ReadBodyAsync(response, timeout.Token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return (false,
                    $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}{Excerpt(body)}",
                    IsRetryableStatus(response.StatusCode));
            }

            // HTTP 200 不代表业务成功 —— 企业微信/钉钉会在正文里给 errcode。
            string? bodyError = InspectBody(body);

            return bodyError is null
                ? (true, null, false)
                : (false, bodyError, IsRetryableBodyError(bodyError));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return (false, "已取消。", false);
        }
        catch (OperationCanceledException)
        {
            // 不是外部取消，那就是我们自己的 15 秒超时到了。
            return (false, $"请求超过 {RequestTimeout.TotalSeconds:0} 秒未完成。", true);
        }
        catch (HttpRequestException ex)
        {
            return (false, Describe(ex), true);
        }
        catch (Exception ex)
        {
            // 例如 URL 拼不出来、序列化失败。重试无益。
            return (false, $"{ex.GetType().Name}：{ex.Message}", false);
        }
    }

    private static async Task<string> ReadBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return string.Empty;   // 正文读不到不影响状态码判断
        }
    }

    /// <summary>
    /// 值得重试的状态码：限流、请求超时、以及服务端自己的 5xx。
    /// 401/403/404 是配置写错了，重试三次只是把同一条错误刷三遍。
    /// </summary>
    private static bool IsRetryableStatus(HttpStatusCode status) =>
        status is HttpStatusCode.RequestTimeout
            or HttpStatusCode.TooManyRequests
            or HttpStatusCode.InternalServerError
            or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout;

    /// <summary>正文里报的错是否值得重试。默认只认限流。</summary>
    protected virtual bool IsRetryableBodyError(string error) =>
        error.Contains("限流", StringComparison.Ordinal)
        || error.Contains("frequency", StringComparison.OrdinalIgnoreCase)
        || error.Contains("rate limit", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 递增退避 + 抖动。抖动来自 <see cref="Random.Shared"/>：
    /// 多条通知同时失败时若一起重试，只会把限流撞得更死。
    /// </summary>
    private static TimeSpan BackoffFor(int attempt) =>
        TimeSpan.FromMilliseconds((500 * attempt) + Random.Shared.Next(0, 400));

    /// <summary>
    /// 把 HttpRequestException 翻译成人能看懂的话。
    /// 默认的 "An error occurred while sending the request" 对用户毫无帮助。
    /// </summary>
    private static string Describe(HttpRequestException ex)
    {
        string inner = ex.InnerException?.Message ?? ex.Message;

        return ex.HttpRequestError switch
        {
            HttpRequestError.NameResolutionError => $"域名解析失败（{inner}）。检查网址拼写与网络连接。",
            HttpRequestError.ConnectionError => $"无法建立连接（{inner}）。检查网络、代理或防火墙。",
            HttpRequestError.SecureConnectionError => $"TLS 握手失败（{inner}）。检查系统时间与证书。",
            HttpRequestError.ProxyTunnelError => $"代理隧道建立失败（{inner}）。",
            _ => inner,
        };
    }

    /// <summary>失败时带上一小段正文帮助定位，但不能把整个页面倒进日志。</summary>
    private static string Excerpt(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return string.Empty;
        }

        string flat = body.ReplaceLineEndings(" ").Trim();

        return flat.Length <= 200
            ? $"：{flat}"
            : $"：{flat[..200]}…";
    }

    private static HttpClient CreateClient()
    {
        SocketsHttpHandler handler = new()
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(10),
            AutomaticDecompression = DecompressionMethods.All,

            // 通知类接口不该跳转；跟着 302 走可能把 Webhook 密钥送到别的地方去。
            AllowAutoRedirect = false,
        };

        HttpClient client = new(handler, disposeHandler: false)
        {
            // 真正的超时由每次请求的 CTS 控制，这里放宽，避免两套超时互相干扰。
            Timeout = Timeout.InfiniteTimeSpan,
        };

        client.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue(AppPaths.ProductName, AppPaths.ProductVersion));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        return client;
    }

    /// <summary>JSON 请求体。渠道实现里统一用它，避免各写一套。</summary>
    protected static HttpContent JsonBody<T>(T payload) =>
        new StringContent(JsonSerializer.Serialize(payload, Json), Encoding.UTF8, "application/json");

    /// <summary>
    /// 截断过长文本。企业微信 markdown 上限 4096 字节、钉钉 20000、Telegram 4096 字符。
    /// 超限时服务端直接拒收整条消息 —— 与其丢掉全部，不如截断后带上省略提示。
    /// </summary>
    protected static string Clamp(string text, int maxChars)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxChars)
        {
            return text;
        }

        const string tail = "\n…（内容过长已截断，完整信息见程序日志）";

        int keep = Math.Max(0, maxChars - tail.Length);
        return text[..keep] + tail;
    }
}
