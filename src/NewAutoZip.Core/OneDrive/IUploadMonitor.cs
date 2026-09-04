using NewAutoZip.Core.Pipeline;

namespace NewAutoZip.Core.OneDrive;

public enum UploadStatus
{
    /// <summary>还在上传，继续等。</summary>
    Pending,

    /// <summary>已确认上传完成。</summary>
    Completed,

    /// <summary>无法确认（卷不支持云占位符、或同步引擎没在跑）。不谎报成功。</summary>
    Unknown,

    /// <summary>超过等待时限仍未确认。</summary>
    TimedOut,

    /// <summary>归档在 OneDrive 目录里消失了 —— 被人手动删了或移走了。</summary>
    Vanished,
}

/// <param name="Status">当前判定。</param>
/// <param name="Detail">给人看的说明，会原样进日志和通知，必须如实。</param>
/// <param name="LocalBytes">该文件当前在本地实际占用的字节数；脱水成功后接近 0。</param>
/// <param name="SpaceReleased">是否已确认本地空间被释放。</param>
public sealed record UploadCheckResult(
    UploadStatus Status,
    string? Detail,
    long LocalBytes,
    bool SpaceReleased);

/// <summary>
/// 上传完成与本地空间释放的判定。抽成接口有两个原因：
/// 一是可以用 fake 做确定性单元测试，二是没装 OneDrive 的机器也能跑完整流程。
/// </summary>
public interface IUploadMonitor
{
    string DisplayName { get; }

    /// <summary>
    /// 检查一次。<b>必须立即返回</b>，绝不在内部长时间等待 ——
    /// 旧版把整个等待过程做成 <c>Thread.Sleep(60000)</c> 循环，
    /// 最长两小时不响应停止请求，UI 的"停止"按钮形同虚设。
    /// </summary>
    Task<UploadCheckResult> CheckAsync(PendingUpload upload, bool requestRelease, CancellationToken ct);
}

/// <summary>
/// 占位实现：只确认"归档已在 OneDrive 目录里"，不判断上传是否完成。
/// 用于尚未接入真实检测、或用户明确关闭了空间释放的场景。说明文字如实反映这一点。
/// </summary>
public sealed class DeliveryOnlyUploadMonitor : IUploadMonitor
{
    public static DeliveryOnlyUploadMonitor Instance { get; } = new();

    public string DisplayName => "仅确认已交付（未启用上传检测）";

    public Task<UploadCheckResult> CheckAsync(PendingUpload upload, bool requestRelease, CancellationToken ct)
    {
        if (!File.Exists(upload.ArchivePath))
        {
            return Task.FromResult(new UploadCheckResult(
                UploadStatus.Vanished,
                "归档已不在 OneDrive 目录中。",
                0,
                false));
        }

        return Task.FromResult(new UploadCheckResult(
            UploadStatus.Completed,
            "已移入 OneDrive 目录。未启用上传检测，因此无法确认云端是否已收到。",
            upload.Bytes,
            false));
    }
}
