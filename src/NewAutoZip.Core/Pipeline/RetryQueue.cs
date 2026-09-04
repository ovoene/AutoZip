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
}

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
        int maxAttempts)
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

    public void Remove(string id) => _entries.RemoveAll(e => e.Id == id);

    public void Clear() => _entries.Clear();

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

    private string NextId(DateTimeOffset now) =>
        $"B{now.ToLocalTime():yyyyMMdd-HHmmss}-{++_sequence:00}";

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
