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
/// 必须重新纳入处理的单个文件 —— 水位线的<b>例外名单</b>。
///
/// 水位线 <see cref="EngineState.Checkpoint"/> 是一个标量，只能表达
/// "这一刻之前的都处理过了"，无法表达"这一刻之前的都处理过了，<b>除了 F</b>"。
/// 而这种处境是真实存在的：整批文件打包成功，其中一个恰好被别的进程独占，
/// 7za 跳过它并以退出码 1（成功但有警告）收尾 —— 包是好的，那个文件却没进去。
/// 只有水位线的话，它会被永久跨过去，不重试、不隔离、不告警，静默丢失。
///
/// 所以另开一份逐文件的例外名单：命中名单的文件无论水位线怎么走都放行。
/// </summary>
public sealed class ForcedFile
{
    public string Path { get; set; } = string.Empty;

    /// <summary>为什么进名单（写进日志，也给人看）。</summary>
    public string Reason { get; set; } = string.Empty;

    public DateTimeOffset AddedUtc { get; set; }
}

/// <summary>
/// 修改时间落在未来的文件 —— 与 <see cref="ForcedFile"/> 极性相反的例外名单。
///
/// 水位线的推进被截断到"当前时刻"，所以一个 2030 年时间戳的文件不会把水位线毒死；
/// 但代价是它自己也不会被水位线覆盖，下一轮对账又会把它当成新文件重新打包，无限循环。
/// 这份名单按 (路径, 修改时间) 记住"这个时间戳的这个文件已经处理过了"：
/// 时间戳一旦变化（文件被真正重写），配不上就自动重新放行，不需要人工干预。
/// </summary>
public sealed class FutureStampedFile
{
    public string Path { get; set; } = string.Empty;

    /// <summary>当时记下的修改时间。文件被重写后此值配不上，于是重新放行。</summary>
    public DateTimeOffset LastWriteUtc { get; set; }
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

    /// <summary>
    /// 水位线的例外名单：这些文件无论水位线怎么走都要重新纳入处理。
    /// 见 <see cref="ForcedFile"/>。名单是自愈的 —— 文件真被打进包里就立刻移出，
    /// 文件从磁盘上消失也会被清掉，不会无限增长。
    /// </summary>
    public List<ForcedFile> ForcedFiles { get; set; } = [];

    /// <summary>
    /// 修改时间在未来、已经处理过的文件。见 <see cref="FutureStampedFile"/>。
    /// 这一份是为了不让"水位线截断到当前时刻"变成重复打包。
    /// </summary>
    public List<FutureStampedFile> FutureStamped { get; set; } = [];

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

    // ==================== 恢复演练 ====================

    /// <summary>
    /// 最近一次<b>云端</b>演练的时刻。周期判断只看这一个字段。
    ///
    /// 只记云端那一次：本地演练是每次打包都跟着做的，没有"到期了吗"这个问题；
    /// 而云端演练要按 <c>CloudDrillIntervalHours</c> 掐表，还得跨重启记住 ——
    /// 否则每次开机都会立刻拉一个包下来验，把"一周一次"变成"开机一次"。
    /// </summary>
    public DateTimeOffset? LastCloudDrillUtc { get; set; }

    /// <summary>最近一次演练（不分本地云端）的时刻，给界面显示用。</summary>
    public DateTimeOffset? LastDrillUtc { get; set; }

    /// <summary>最近一次演练是否通过。null = 还没演练过。</summary>
    public bool? LastDrillOk { get; set; }

    /// <summary>最近一次演练的结论原文。</summary>
    public string? LastDrillMessage { get; set; }

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
            LastCloudDrillUtc = LastCloudDrillUtc,
            LastDrillUtc = LastDrillUtc,
            LastDrillOk = LastDrillOk,
            LastDrillMessage = LastDrillMessage,
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
            ForcedFiles = ForcedFiles.Select(f => new ForcedFile
            {
                Path = f.Path,
                Reason = f.Reason,
                AddedUtc = f.AddedUtc,
            }).ToList(),
            FutureStamped = FutureStamped.Select(f => new FutureStampedFile
            {
                Path = f.Path,
                LastWriteUtc = f.LastWriteUtc,
            }).ToList(),
        };
    }
}
