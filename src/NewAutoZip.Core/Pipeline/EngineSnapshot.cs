using NewAutoZip.Core.Configuration;
using NewAutoZip.Core.Storage;
using NewAutoZip.Core.Watching;

namespace NewAutoZip.Core.Pipeline;

public sealed record TrackedFileView(
    string Path,
    string FileName,
    long Bytes,
    TrackedFileState State,
    DateTimeOffset FirstSeenLocal,
    int QuietProbes)
{
    public string SizeText => ByteSize.Format(Bytes);

    public string StateText => State switch
    {
        TrackedFileState.Ready => "已就绪",
        TrackedFileState.Unreadable => "无法读取",
        _ => "观察中",
    };
}

public sealed record BatchFileView(string Path, string FileName, long Bytes)
{
    public string SizeText => ByteSize.Format(Bytes);
}

public sealed record PendingUploadView(
    string ArchivePath,
    string FileName,
    long Bytes,
    int FileCount,
    DateTimeOffset DeliveredLocal,
    bool ReleaseRequested,
    int Polls)
{
    public string SizeText => ByteSize.Format(Bytes);
}

public sealed record QuarantineView(
    string Id,
    int FileCount,
    long TotalBytes,
    string Reason,
    int Attempts,
    DateTimeOffset QuarantinedLocal,
    IReadOnlyList<string> Files)
{
    public string SizeText => ByteSize.Format(TotalBytes);
}

public sealed record RetryView(
    string Id,
    int FileCount,
    int Attempts,
    DateTimeOffset NextAttemptLocal,
    string LastError);

/// <summary>
/// 引擎状态的不可变快照。
///
/// UI 不订阅细粒度事件，而是以约 4 Hz 轮询这个快照 —— 事件风暴在设计上就不可能发生。
/// 旧版每条日志、每个文件状态变化都单独 <c>BeginInvoke</c> 一次；
/// 扫描间隔可以填 0（NumericUpDown 默认 Minimum=0），一旦填了 0
/// 消息队列会被瞬间打爆、界面完全冻死。
/// </summary>
public sealed record EngineSnapshot(
    EnginePhase Phase,
    string StatusText,
    bool Running,
    bool ScheduleActive,
    DateTimeOffset? NextActivationLocal,
    int TrackedCount,
    int BatchFileCount,
    long BatchBytes,
    DateTimeOffset? WindowClosesAtLocal,
    int PackPercent,
    string? PackCurrentFile,
    int RetryCount,
    DateTimeOffset? NextRetryLocal,
    int QuarantineCount,
    int PendingUploadCount,
    long ArchivesCreated,
    long BytesArchived,
    long FilesArchived,
    int ZipTempArchiveCount,
    long ZipTempBytes,
    long FreeDiskBytes,
    CloudTarget CloudTarget,
    DateTimeOffset? LastPackSuccessLocal,
    DateTimeOffset? LastPackFailureLocal,
    string? LastPackFailureReason,
    DateTimeOffset? LastDeliverSuccessLocal,
    DateTimeOffset? LastDeliverFailureLocal,
    string? LastDeliverFailureReason,
    IReadOnlyList<TrackedFileView> TrackedFiles,
    IReadOnlyList<BatchFileView> BatchFiles,
    IReadOnlyList<PendingUploadView> PendingUploads,
    IReadOnlyList<RetryView> Retries,
    IReadOnlyList<QuarantineView> Quarantines)
{
    public static EngineSnapshot Stopped { get; } = new(
        EnginePhase.Stopped,
        EnginePhaseText.Describe(EnginePhase.Stopped),
        Running: false,
        ScheduleActive: false,
        NextActivationLocal: null,
        TrackedCount: 0,
        BatchFileCount: 0,
        BatchBytes: 0,
        WindowClosesAtLocal: null,
        PackPercent: 0,
        PackCurrentFile: null,
        RetryCount: 0,
        NextRetryLocal: null,
        QuarantineCount: 0,
        PendingUploadCount: 0,
        ArchivesCreated: 0,
        BytesArchived: 0,
        FilesArchived: 0,
        ZipTempArchiveCount: 0,
        ZipTempBytes: 0,
        FreeDiskBytes: 0,
        CloudTarget: CloudTarget.OneDrive,
        LastPackSuccessLocal: null,
        LastPackFailureLocal: null,
        LastPackFailureReason: null,
        LastDeliverSuccessLocal: null,
        LastDeliverFailureLocal: null,
        LastDeliverFailureReason: null,
        TrackedFiles: [],
        BatchFiles: [],
        PendingUploads: [],
        Retries: [],
        Quarantines: []);

    public string BatchBytesText => ByteSize.Format(BatchBytes);

    public string ZipTempBytesText => ByteSize.Format(ZipTempBytes);

    public string FreeDiskText => ByteSize.Format(FreeDiskBytes);

    public string BytesArchivedText => ByteSize.Format(BytesArchived);
}
