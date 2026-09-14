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

    /// <summary>用户填的云盘/目标目录容量预算。0 = 没设上限，界面据此隐藏整张卡片。</summary>
    long CloudQuotaBytes,

    /// <summary>目标目录里我们的包实际占了多少（含旁挂清单）。每轮重新扫，手动删包后会自动回落。</summary>
    long CloudUsedBytes,

    /// <summary>还能放多少。普通目录模式下已经和该卷真实可用空间取过较小值。</summary>
    long CloudFreeBytes,

    /// <summary>
    /// 用户填的警戒线。0 = 不告警。
    ///
    /// 界面本可以直接去读当前配置，但那样总览就会出现"一半是引擎正在用的、
    /// 一半是磁盘上刚改还没生效的"——同一张卡片上两个口径。快照里带着，
    /// 显示的就都是这一轮真正在用的那份。
    /// </summary>
    long CloudQuotaWarnBytes,
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
    IReadOnlyList<QuarantineView> Quarantines,

    /// <summary>
    /// 上面那三个容量数字是按什么口径算的（见 <see cref="QuotaBasis"/>）。
    ///
    /// 本地硬盘用 <see cref="QuotaBasis.Volume"/>：三个数全是卷的真实值，
    /// <c>CloudUsedBytes</c> 是<b>整个卷</b>的占用，不只是我们的包 ——
    /// 界面文案必须据此改口，否则用户会以为那么多都是备份占的。
    /// </summary>
    QuotaBasis CloudQuotaBasis = QuotaBasis.Budget,

    /// <summary>我们自己的包占了多少。两种口径下都填，供"本程序备份包占 X"那句用。</summary>
    long CloudOurBytes = 0,

    /// <summary>用户填的预算，0 = 没填。Volume 口径下它和 <c>CloudQuotaBytes</c> 是两回事。</summary>
    long CloudBudgetBytes = 0)
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
        CloudQuotaBytes: 0,
        CloudUsedBytes: 0,
        CloudFreeBytes: 0,
        CloudQuotaWarnBytes: 0,
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

    // ---------- 云盘容量 ----------

    /// <summary>
    /// 有得算吗。本地硬盘下卷的总容量恒大于 0，所以<b>用户没填预算也会显示卡片</b>——
    /// 卷的容量不需要谁来填。云盘/远程目录才要人填，没填就藏起整张卡片。
    /// </summary>
    public bool CloudQuotaConfigured => CloudQuotaBytes > 0;

    public string CloudQuotaText => ByteSize.Format(CloudQuotaBytes);

    public string CloudUsedText => ByteSize.Format(CloudUsedBytes);

    public string CloudFreeText => ByteSize.Format(CloudFreeBytes);

    /// <summary>
    /// 已用百分比，给进度条用。
    ///
    /// 夹在 0–100：已用量是扫出来的实际占用，可能超过用户填的预算
    /// （填小了、或者目录里本来就堆着旧包），而进度条吃到 130 会画到框外面去。
    /// </summary>
    public double CloudUsedPercent =>
        CloudQuotaBytes <= 0 ? 0 : Math.Clamp(CloudUsedBytes * 100.0 / CloudQuotaBytes, 0, 100);

    /// <summary>剩余已经低于警戒线。界面用它把数字变红。没设警戒线时永远为 false。</summary>
    public bool CloudQuotaLow =>
        CloudQuotaConfigured && CloudQuotaWarnBytes > 0 && CloudFreeBytes <= CloudQuotaWarnBytes;

    /// <summary>「警戒线 100.0 GB」。没设警戒线时是空串，界面那一格自然就空着。</summary>
    public string CloudQuotaWarnText =>
        CloudQuotaWarnBytes > 0 ? $"警戒线 {ByteSize.Format(CloudQuotaWarnBytes)}" : string.Empty;

    /// <summary>
    /// 「已用 712.4 GB / 1.00 TB · 剩余 311.6 GB」。
    ///
    /// 三个数之间<b>恒有减法关系</b>（见 <see cref="CloudQuotaStatus.FreeBytes"/>）。
    /// 本地硬盘口径下这是整个卷的账，所以再缀一句我们自己占了多少 ——
    /// 否则用户看着 1.51 TB 会以为全是备份包。
    /// </summary>
    public string CloudQuotaUsageText
    {
        get
        {
            string head = $"已用 {CloudUsedText} / {CloudQuotaText} · 剩余 {CloudFreeText}";

            if (CloudQuotaBasis != QuotaBasis.Volume)
            {
                return head;
            }

            string ours = $" · 其中本程序备份包 {ByteSize.Format(CloudOurBytes)}";

            return CloudBudgetBytes > 0
                ? $"{head}{ours} / 预算 {ByteSize.Format(CloudBudgetBytes)}"
                : $"{head}{ours}";
        }
    }

    /// <summary>
    /// 容量卡片<b>应该</b>按哪份数字显示。
    ///
    /// 引擎在跑 → 用快照自己的值，那是这一轮真正在用的那份预算
    /// （理由见 <see cref="CloudQuotaWarnBytes"/>：同一张卡片上不能出现两个口径）。
    ///
    /// 引擎停着 → 用<b>磁盘上已保存</b>的预算重新算一份。
    /// 不这么做的话卡片会整张消失：<see cref="Stopped"/> 里四个值写死是 0，
    /// 而 <see cref="CloudQuotaConfigured"/> 判的正是 <c>CloudQuotaBytes &gt; 0</c>——
    /// 用户明明填了容量，一停引擎卡片就没了。这和云盘类型当初那个毛病是同一个，
    /// 解法也照搬 <c>MainViewModel.ShownTargetFor</c>。
    ///
    /// 只换容量那四个字段，其余原样带过：这份快照是拼出来的，
    /// 除容量外的字段仍是"已停止"的值，调用方<b>只能</b>拿它读容量。
    ///
    /// 写成静态纯函数是为了单元测试能直接喂输入 —— 扫目录、读配置都在调用方那边。
    /// </summary>
    /// <param name="snapshot">引擎当前快照。</param>
    /// <param name="saved">按磁盘上那份配置算出来的容量账，停止时用它。</param>
    /// <param name="savedWarnBytes">
    /// 磁盘上那份配置里的警戒线。<see cref="CloudQuotaStatus"/> 不带这个字段
    /// （它是 <see cref="CloudQuotaStatus.IsLow"/> 的参数），所以单独传。
    /// </param>
    public static EngineSnapshot QuotaSourceFor(
        EngineSnapshot snapshot,
        CloudQuotaStatus saved,
        long savedWarnBytes)
    {
        if (snapshot.Running)
        {
            return snapshot;
        }

        // 没配预算：卡片本来就该藏着。四个值一起归零，
        // 顺手把上一轮启动残留的旧数字也抹掉。
        if (!saved.Configured)
        {
            return snapshot with
            {
                CloudQuotaBytes = 0,
                CloudUsedBytes = 0,
                CloudFreeBytes = 0,
                CloudQuotaWarnBytes = 0,
                CloudQuotaBasis = QuotaBasis.Budget,
                CloudOurBytes = 0,
                CloudBudgetBytes = 0,
            };
        }

        return snapshot with
        {
            CloudQuotaBytes = saved.QuotaBytes,
            CloudUsedBytes = saved.UsedBytes,
            CloudFreeBytes = saved.FreeBytes,
            CloudQuotaWarnBytes = savedWarnBytes,
            CloudQuotaBasis = saved.Basis,
            CloudOurBytes = saved.OurBytes,
            CloudBudgetBytes = saved.BudgetBytes,
        };
    }
}
