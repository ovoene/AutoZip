using System.Text.Json.Serialization;

namespace NewAutoZip.Core.Pipeline;

/// <summary>
/// 已移入 OneDrive、但尚未确认上传完成的归档。
///
/// 旧版完全没有这个概念：等待上传期间进程被杀，重启后归档就成了孤儿，
/// 而源文件还在监控目录里，于是被重新打了一个包。
/// </summary>
public sealed class PendingUpload
{
    /// <summary>归档在 OneDrive 目录里的完整路径。</summary>
    public string ArchivePath { get; set; } = string.Empty;

    public long Bytes { get; set; }

    public int FileCount { get; set; }

    /// <summary>源文件总大小（压缩前）。【结束】通知要报它，所以得跟着状态一起活过重启。</summary>
    public long SourceBytes { get; set; }

    /// <summary>
    /// 这一轮处理的起点 —— 检测到新文件、批处理窗口开启的那一刻。
    /// 【结束】里的"共计耗时"是从这里算到上传确认完成，跨越了打包与上传两段，
    /// 所以必须随状态持久化：等上传期间重启进程，耗时还得接得上。
    /// </summary>
    public DateTimeOffset RoundStartedUtc { get; set; }

    public DateTimeOffset DeliveredUtc { get; set; }

    /// <summary>是否已经向 OneDrive 请求过脱水（置 UNPINNED）。</summary>
    public bool ReleaseRequested { get; set; }

    /// <summary>轮询次数，用于日志与超时判断。</summary>
    public int Polls { get; set; }

    [JsonIgnore]
    public string FileName => Path.GetFileName(ArchivePath);
}

/// <summary>
/// 被隔离的批次：连续失败达到上限后不再重试，等用户处理。
/// 这是"任何批次都不可能无限循环"这一保证的最后一环，旧版彻底没有。
/// </summary>
public sealed class QuarantinedBatch
{
    public string Id { get; set; } = string.Empty;

    public List<string> Files { get; set; } = [];

    public string Reason { get; set; } = string.Empty;

    public int Attempts { get; set; }

    public DateTimeOffset QuarantinedUtc { get; set; }

    public long TotalBytes { get; set; }
}

/// <summary>
/// 需要持久化的运行时状态。与 <see cref="Configuration.AppSettings"/> 分开存放：
/// 配置是人写的，状态是程序写的，混在一起会让"重置状态"变成危险操作。
/// </summary>
public sealed class EngineState
{
    /// <summary>
    /// 处理进度水位线：修改时间不晚于此的文件视为已处理过。
    ///
    /// 旧版 <c>Checkpoint.Save</c> 用 <c>File.WriteAllText</c> 且<b>没有 try/catch</b>，
    /// 磁盘满或无写权限时直接抛异常穿透到主循环，跳过状态复位 —— 于是下一轮重新打包。
    /// 新版走 <see cref="Configuration.AtomicJsonFile"/>，写失败只记日志。
    /// 读取也改成 InvariantCulture + RoundtripKind，不再受系统区域设置影响。
    /// </summary>
    public DateTimeOffset? Checkpoint { get; set; }

    public List<PendingUpload> PendingUploads { get; set; } = [];

    public List<QuarantinedBatch> Quarantined { get; set; } = [];

    /// <summary>首次运行已完成（决定 ProcessExistingFilesOnFirstRun 是否还生效）。</summary>
    public bool FirstRunCompleted { get; set; }

    public long TotalArchivesCreated { get; set; }

    public long TotalBytesArchived { get; set; }

    public long TotalFilesArchived { get; set; }

    /// <summary>
    /// 最近一次<b>打包</b>成功的时刻。
    ///
    /// 打包成功与送达成功分两个字段记，是因为它们代表的处境完全不同：
    /// "包打好了但没送上去"意味着数据还在本机、还占着临时目录；
    /// "压根没打出包"意味着这批文件目前毫无备份。只留一个"最近成功"
    /// 会把这两件事混成一句话，用户还得去翻日志才知道该做什么。
    /// </summary>
    public DateTimeOffset? LastPackSuccessUtc { get; set; }

    public DateTimeOffset? LastPackFailureUtc { get; set; }

    public string? LastPackFailureReason { get; set; }

    /// <summary>最近一次送达成功：OneDrive 模式指云端已确认收到，其他云盘指已剪切进目录。</summary>
    public DateTimeOffset? LastDeliverSuccessUtc { get; set; }

    public DateTimeOffset? LastDeliverFailureUtc { get; set; }

    public string? LastDeliverFailureReason { get; set; }

    public EngineState Clone()
    {
        return new EngineState
        {
            Checkpoint = Checkpoint,
            FirstRunCompleted = FirstRunCompleted,
            TotalArchivesCreated = TotalArchivesCreated,
            TotalBytesArchived = TotalBytesArchived,
            TotalFilesArchived = TotalFilesArchived,
            LastPackSuccessUtc = LastPackSuccessUtc,
            LastPackFailureUtc = LastPackFailureUtc,
            LastPackFailureReason = LastPackFailureReason,
            LastDeliverSuccessUtc = LastDeliverSuccessUtc,
            LastDeliverFailureUtc = LastDeliverFailureUtc,
            LastDeliverFailureReason = LastDeliverFailureReason,
            PendingUploads = PendingUploads.Select(u => new PendingUpload
            {
                ArchivePath = u.ArchivePath,
                Bytes = u.Bytes,
                FileCount = u.FileCount,
                SourceBytes = u.SourceBytes,
                RoundStartedUtc = u.RoundStartedUtc,
                DeliveredUtc = u.DeliveredUtc,
                ReleaseRequested = u.ReleaseRequested,
                Polls = u.Polls,
            }).ToList(),
            Quarantined = Quarantined.Select(q => new QuarantinedBatch
            {
                Id = q.Id,
                Files = [.. q.Files],
                Reason = q.Reason,
                Attempts = q.Attempts,
                QuarantinedUtc = q.QuarantinedUtc,
                TotalBytes = q.TotalBytes,
            }).ToList(),
        };
    }
}
