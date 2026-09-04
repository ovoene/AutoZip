namespace NewAutoZip.Core.Notifications;

/// <summary>
/// 通知出口。引擎只认这一个接口，不关心底下是企业微信还是 Telegram。
///
/// 契约里最重要的一条：<b>SendAsync 永不抛异常</b>。
/// 旧版 <see cref="INotifier"/> 的三个实现都调了 <c>EnsureSuccessStatusCode()</c>，
/// 而【打包】那次通知调用<b>没有包 try/catch</b>；钉钉限流、自建 Webhook 挂掉、Telegram 连不上，
/// 任一情况都会让异常穿过状态复位代码 —— 于是下一轮重新打包，无限循环。
/// 通知失败绝不能影响备份主流程。
/// </summary>
public interface INotificationHub
{
    /// <summary>当前渠道的显示名，未配置时返回"未配置"。</summary>
    string ChannelName { get; }

    bool IsEnabled { get; }

    /// <summary>发送一条通知。事件未被勾选时静默跳过。永不抛异常。</summary>
    Task<NotifyResult> SendAsync(NotifyMessage message, CancellationToken ct);
}

public sealed class NullNotificationHub : INotificationHub
{
    public static NullNotificationHub Instance { get; } = new();

    public string ChannelName => "未配置";

    public bool IsEnabled => false;

    public Task<NotifyResult> SendAsync(NotifyMessage message, CancellationToken ct) =>
        Task.FromResult(NotifyResult.Skipped);
}
