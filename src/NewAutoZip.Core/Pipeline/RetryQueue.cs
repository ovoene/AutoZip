using NewAutoZip.Core.Diagnostics;

namespace NewAutoZip.Core.Pipeline;

public sealed class RetryEntry
{
    public required string Id { get; init; }

    public required List<string> Files { get; init; }

    public int Attempts { get; set; }

    public DateTimeOffset NextAttemptUtc { get; set; }

    public string LastError { get; set; } = string.Empty;

    public DateTimeOffset FirstFailureUtc { get; init; }

    // ---------- 只在"包已经打好、只是没送进目标目录"时才有值 ----------
    //
    // 这种失败重试的是<b>投递</b>，不是打包：包在临时目录里好好躺着，
    // 再压一遍只是白烧 CPU 和磁盘，还会在临时目录里多堆一个同样的包。

    /// <summary>已经打好、等着重新投递的包（在临时目录里）。为空表示重试要从打包开始。</summary>
    public string? ArchivePath { get; set; }

    public long ArchiveBytes { get; set; }

    /// <summary>真正进了包的源文件个数（7za 报的，不是清单条数）。</summary>
    public int ArchivedCount { get; set; }

    /// <summary>源文件总大小。</summary>
    public long SourceBytes { get; set; }

    /// <summary>打包时被 7za 跳过的源文件 —— 投递成功后照样要记进"待重新处理"名单。</summary>
    public List<string> SkippedFiles { get; set; } = [];

    /// <summary>
    /// 这个包的"打包后解压验证"已经做完并通过了。
    /// 没做完（被停止、被界面接管、进程被结束）的包在重新投递之前要先补做一遍 ——
    /// 没验过的包不往目标目录送，这条规矩不能因为中途被打断就绕过去。
    /// </summary>
    public bool Verified { get; set; }

    /// <summary>深拷贝。存盘与发布状态视图都要一份互不相干的副本。</summary>
    public RetryEntry Clone() => new()
    {
        Id = Id,
        Files = [.. Files],
        Attempts = Attempts,
        NextAttemptUtc = NextAttemptUtc,
        LastError = LastError,
        FirstFailureUtc = FirstFailureUtc,
        ArchivePath = ArchivePath,
        ArchiveBytes = ArchiveBytes,
        ArchivedCount = ArchivedCount,
        SourceBytes = SourceBytes,
        SkippedFiles = [.. SkippedFiles],
        Verified = Verified,
    };
}

/// <summary>投递失败时随失败一起登记的那个已经打好的包。见 <see cref="RetryEntry.ArchivePath"/>。</summary>
public sealed record PackedArchive(
    string ArchivePath,
    long ArchiveBytes,
    int ArchivedCount,
    long SourceBytes,
    IReadOnlyList<string> SkippedFiles,
    bool Verified = true);

/// <param name="Requeued">已排入重试队列的批次，null 表示这批直接进了隔离区。</param>
/// <param name="Quarantined">因达到失败上限而被隔离的批次，null 表示尚未到上限。</param>
public sealed record FailureOutcome(RetryEntry? Requeued, QuarantinedBatch? Quarantined)
{
    public bool WasQuarantined => Quarantined is not null;
}

/// <summary>
/// 重试队列与熔断隔离。
///
/// 这里提供旧版彻底缺失的那条保证：<b>任何一个批次都不可能被无限重试</b>。
/// 连续失败达到上限后写入隔离区、发出通知、停止重试，等人来处理。
///
/// 注意重试队列与"当前批次"是两个独立容器。旧版把两者混在同一个 <c>_files</c> 字典里，
/// 结果失败的文件一直赖在里面，既阻塞新批次（<c>allStable</c> 永假），
/// 又在每轮扫描时被重新打包一次。
/// </summary>
public sealed class RetryQueue
{
    private readonly List<RetryEntry> _entries = [];
    private readonly IAppLogger _log;
    private readonly TimeProvider _time;

    private int _sequence;

    public RetryQueue(IAppLogger log, TimeProvider time)
    {
        _log = log;
        _time = time;
    }

    public int Count => _entries.Count;

    public IReadOnlyList<RetryEntry> Entries => _entries;

    public DateTimeOffset? NextAttemptUtc =>
        _entries.Count == 0 ? null : _entries.Min(e => e.NextAttemptUtc);

    /// <summary>队列里涉及的全部文件路径，用来避免同一个文件被两个批次同时处理。</summary>
    public HashSet<string> AllFiles()
    {
        HashSet<string> set = new(StringComparer.OrdinalIgnoreCase);

        foreach (RetryEntry entry in _entries)
        {
            foreach (string file in entry.Files)
            {
                set.Add(file);
            }
        }

        return set;
    }

    /// <summary>
    /// 登记一次批次失败。返回值说明这批是被排入重试队列还是被隔离。
    /// </summary>
    /// <param name="existingId">
    /// 若这批本来就来自重试队列，传它的 Id，累加失败次数；传 null 表示首次失败。
    /// </param>
    public FailureOutcome RecordFailure(
        string? existingId,
        IReadOnlyList<string> files,
        long totalBytes,
        string reason,
        int maxAttempts,
        PackedArchive? packed = null)
    {
        DateTimeOffset now = _time.GetUtcNow();

        RetryEntry? entry = existingId is null
            ? null
            : _entries.FirstOrDefault(e => e.Id == existingId);

        if (entry is null)
        {
            entry = new RetryEntry
            {
                Id = existingId ?? NextId(now),
                Files = [.. files],
                FirstFailureUtc = now,
            };

            _entries.Add(entry);
        }
        else
        {
            // 重试时文件集合可能已变化（有的被删了），以本次实际尝试的为准。
            entry.Files.Clear();
            entry.Files.AddRange(files);
        }

        entry.Attempts++;
        entry.LastError = reason;

        // 打包失败时把旧的包信息清掉 —— 那个包要么已经不在了，要么不再可信。
        entry.ArchivePath = packed?.ArchivePath;
        entry.ArchiveBytes = packed?.ArchiveBytes ?? 0;
        entry.ArchivedCount = packed?.ArchivedCount ?? 0;
        entry.SourceBytes = packed?.SourceBytes ?? 0;
        entry.SkippedFiles = packed is null ? [] : [.. packed.SkippedFiles];
        entry.Verified = packed?.Verified ?? false;

        if (entry.Attempts >= maxAttempts)
        {
            _entries.Remove(entry);

            QuarantinedBatch quarantined = new()
            {
                Id = entry.Id,
                Files = [.. entry.Files],
                Reason = reason,
                Attempts = entry.Attempts,
                QuarantinedUtc = now,
                TotalBytes = totalBytes,
            };

            _log.Error(
                $"批次 {entry.Id} 连续失败 {entry.Attempts} 次，已移入隔离区并停止重试。" +
                $"涉及 {entry.Files.Count} 个文件。最后一次的原因：{reason}");

            return new FailureOutcome(null, quarantined);
        }

        entry.NextAttemptUtc = RetryPolicy.NextAttemptUtc(entry.Attempts, now);

        TimeSpan delay = entry.NextAttemptUtc - now;
        _log.Warn(
            $"批次 {entry.Id} 第 {entry.Attempts} 次失败，" +
            $"将在 {FormatDelay(delay)} 后重试（第 {maxAttempts} 次失败后进入隔离区）。原因：{reason}");

        return new FailureOutcome(entry, null);
    }

    /// <summary>取出一个到点可以重试的批次。没有到点的返回 null。</summary>
    public RetryEntry? TakeDue()
    {
        DateTimeOffset now = _time.GetUtcNow();

        RetryEntry? due = _entries
            .Where(e => e.NextAttemptUtc <= now)
            .OrderBy(e => e.NextAttemptUtc)
            .FirstOrDefault();

        return due;
    }

    public RetryEntry? Find(string id) => _entries.FirstOrDefault(e => e.Id == id);

    public void Remove(string id) => _entries.RemoveAll(e => e.Id == id);

    public void Clear() => _entries.Clear();

    /// <summary>
    /// 用户在隔离区点了「重试」：作为一个<b>立刻到期</b>的独立重试批次放回来，次数从头算。
    ///
    /// 不并进当前批次：那样要等一个完整的批处理窗口才会打包（界面上说的是"立刻"），
    /// 还会把窗口里本来在等的其他文件一起拖延；而且这一批之前一直失败，
    /// 和无辜文件混在一个包里，再失败就会把它们一起拖进隔离区。
    /// </summary>
    public RetryEntry RequeueNow(string id, IReadOnlyList<string> files)
    {
        _entries.RemoveAll(e => e.Id == id);

        DateTimeOffset now = _time.GetUtcNow();

        RetryEntry entry = new()
        {
            Id = id,
            Files = [.. files],
            Attempts = 0,
            NextAttemptUtc = now,
            LastError = "从隔离区手动放回",
            FirstFailureUtc = now,
        };

        _entries.Add(entry);
        return entry;
    }

    /// <summary>
    /// 换成磁盘上存的那一份（引擎启动时调用）。
    ///
    /// 重试队列原先只活在内存里：进程一重启（开机、崩溃、界面接管后台实例）就清零，
    /// "连续失败 N 次进隔离区"这道闸跟着失效 —— 一个永远失败的批次每次重启都从第 1 次重新数。
    /// </summary>
    public void Load(IEnumerable<RetryEntry>? saved)
    {
        _entries.Clear();

        if (saved is null)
        {
            return;
        }

        foreach (RetryEntry entry in saved)
        {
            if (entry is null || string.IsNullOrWhiteSpace(entry.Id) || entry.Files is null)
            {
                continue;
            }

            RetryEntry copy = entry.Clone();
            copy.SkippedFiles ??= [];

            if (_entries.All(e => e.Id != copy.Id))
            {
                _entries.Add(copy);
            }
        }
    }

    /// <summary>
    /// 接回一个"包已经打好、还没来得及送进目标目录"进程就被打断的批次：作为立刻到期的重试放进队列，
    /// 下一轮只补做验证（如果没做完）并投递，不重新打包。
    ///
    /// 它本来就是重试批次的话沿用原来的编号和失败次数；新批次则分配一个编号、次数从 0 起 ——
    /// 被打断不是失败，不该往隔离那边记一笔。
    /// </summary>
    public RetryEntry AdoptInterrupted(RetryEntry interrupted)
    {
        DateTimeOffset now = _time.GetUtcNow();

        RetryEntry? existing = string.IsNullOrEmpty(interrupted.Id)
            ? null
            : _entries.FirstOrDefault(e => e.Id == interrupted.Id);

        RetryEntry entry = existing ?? new RetryEntry
        {
            Id = string.IsNullOrEmpty(interrupted.Id) ? NextId(now) : interrupted.Id,
            Files = [],
            FirstFailureUtc = now,
            LastError = "上次进程在投递之前被打断",
        };

        entry.Files.Clear();
        entry.Files.AddRange(interrupted.Files);
        entry.ArchivePath = interrupted.ArchivePath;
        entry.ArchiveBytes = interrupted.ArchiveBytes;
        entry.ArchivedCount = interrupted.ArchivedCount;
        entry.SourceBytes = interrupted.SourceBytes;
        entry.SkippedFiles = [.. interrupted.SkippedFiles ?? []];
        entry.Verified = interrupted.Verified;
        entry.NextAttemptUtc = now;

        if (existing is null)
        {
            _entries.Add(entry);
        }

        return entry;
    }

    /// <summary>当前队列的深拷贝，存进 state.json 用。</summary>
    public List<RetryEntry> Export() => [.. _entries.Select(e => e.Clone())];

    /// <summary>还在等重新投递的包。临时目录的配额清理不能删它们。</summary>
    public IEnumerable<string> PendingArchives() =>
        _entries.Where(e => !string.IsNullOrEmpty(e.ArchivePath)).Select(e => e.ArchivePath!);

    /// <summary>
    /// 把已经不存在的源文件从各批次里剔除；剔空的批次直接丢弃。
    ///
    /// 旧版没有这一步：源文件被删除后，那个批次永远凑不齐，
    /// 于是永久卡在"等待"状态并每轮重打一次包。
    /// </summary>
    public int PruneMissingFiles(Func<string, bool> exists)
    {
        int dropped = 0;

        for (int i = _entries.Count - 1; i >= 0; i--)
        {
            RetryEntry entry = _entries[i];

            // 包已经打好、只等投递的批次不能因为源文件没了就丢掉：
            // 源文件被轮转删除是常态，而那个包正是它们仅存的备份。
            if (!string.IsNullOrEmpty(entry.ArchivePath) && SafeExists(exists, entry.ArchivePath))
            {
                continue;
            }

            int before = entry.Files.Count;

            entry.Files.RemoveAll(f => !exists(f));

            if (entry.Files.Count != before)
            {
                _log.Info($"批次 {entry.Id} 中有 {before - entry.Files.Count} 个源文件已不存在，已从该批次移除。");
            }

            if (entry.Files.Count == 0)
            {
                _entries.RemoveAt(i);
                dropped++;
                _log.Info($"批次 {entry.Id} 的源文件已全部消失，放弃该批次（不再重试）。");
            }
        }

        return dropped;
    }

    private string NextId(DateTimeOffset now)
    {
        // 从磁盘读回来的批次编号也在队列里，序号撞上了就往后挪。
        string id;

        do
        {
            id = $"B{now.ToLocalTime():yyyyMMdd-HHmmss}-{++_sequence:00}";
        }
        while (_entries.Any(e => e.Id == id));

        return id;
    }

    private static bool SafeExists(Func<string, bool> exists, string path)
    {
        try
        {
            return exists(path);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string FormatDelay(TimeSpan delay)
    {
        if (delay < TimeSpan.Zero)
        {
            delay = TimeSpan.Zero;
        }

        return delay.TotalMinutes < 1
            ? $"{delay.TotalSeconds:0} 秒"
            : $"{delay.TotalMinutes:0.#} 分钟";
    }
}
