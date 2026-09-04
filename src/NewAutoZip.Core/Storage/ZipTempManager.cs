using NewAutoZip.Core.Diagnostics;

namespace NewAutoZip.Core.Storage;

public sealed record ArchiveInfo(string Path, long Bytes, DateTimeOffset CreatedUtc)
{
    public string FileName => System.IO.Path.GetFileName(Path);
}

public sealed record QuotaReport(
    int DeletedCount,
    long DeletedBytes,
    int RemainingCount,
    long RemainingBytes,
    IReadOnlyList<string> DeletedFiles,
    IReadOnlyList<string> Notes)
{
    public static QuotaReport Empty { get; } = new(0, 0, 0, 0, [], []);

    public bool DidSomething => DeletedCount > 0 || Notes.Count > 0;
}

public sealed record PrecheckResult(bool Ok, string? Reason, long FreeBytes, long RequiredBytes)
{
    public static PrecheckResult Pass(long freeBytes) => new(true, null, freeBytes, 0);
}

/// <summary>
/// ZipTemp 目录的守门人 —— 用户报告的"打包失败时 ZipTemp 塞满磁盘"最终由这里兜底。
///
/// 三重硬上限，任何一项超了就按最旧优先淘汰：
///   1. 保留天数（旧版填 0 等于彻底关掉清理，这里下限强制为 1）
///   2. 最大归档数（旧版<b>没有</b>这一条）
///   3. 最大总容量（旧版<b>没有</b>这一条）
///
/// 打包前还要过一遍 <see cref="Precheck"/>：放不下就<b>压根不产生文件</b>，
/// 而不是像旧版那样先写到磁盘满、再让 <c>Checkpoint.Save</c> 因写不进去而抛异常，
/// 抛异常又跳过状态复位，于是下一轮再打一个包 —— 一个自我强化的正反馈环。
///
/// 待上传的归档（<c>protectedPaths</c>）永不删除：它们已经移交给 OneDrive 了，
/// 删掉等于丢备份。这也是配额可能"清不干净"的唯一情形，会在 Notes 里如实说明。
/// </summary>
public sealed class ZipTempManager
{
    /// <summary>Runner 产生的中间文件，启动时一律清扫。</summary>
    private static readonly string[] IntermediatePatterns = ["*.7z.part", "*.7z.list", "*.tmp"];

    private readonly IAppLogger _log;
    private readonly TimeProvider _time;

    private string _directory = string.Empty;

    public ZipTempManager(IAppLogger log, TimeProvider time)
    {
        _log = log;
        _time = time;
    }

    public string Root => _directory;

    public void Configure(string directory) => _directory = directory;

    public bool EnsureDirectory()
    {
        if (string.IsNullOrWhiteSpace(_directory))
        {
            return false;
        }

        try
        {
            Directory.CreateDirectory(_directory);
            return true;
        }
        catch (Exception ex)
        {
            _log.Error($"无法创建临时目录：{_directory}", ex);
            return false;
        }
    }

    public IReadOnlyList<ArchiveInfo> ListArchives()
    {
        if (string.IsNullOrWhiteSpace(_directory) || !Directory.Exists(_directory))
        {
            return [];
        }

        List<ArchiveInfo> archives = [];

        try
        {
            foreach (string path in Directory.EnumerateFiles(_directory, "*.7z"))
            {
                try
                {
                    FileInfo info = new(path);
                    if (!info.Exists)
                    {
                        continue;
                    }

                    // 少数文件系统上创建时间不可靠，取两者较早的一个作为"年龄"依据。
                    DateTime created = info.CreationTimeUtc < info.LastWriteTimeUtc
                        ? info.CreationTimeUtc
                        : info.LastWriteTimeUtc;

                    archives.Add(new ArchiveInfo(
                        info.FullName,
                        info.Length,
                        new DateTimeOffset(created, TimeSpan.Zero)));
                }
                catch
                {
                    // 单个文件读不到就跳过，不能让清理整体失败。
                }
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"枚举临时目录失败：{_directory}", ex);
            return [];
        }

        archives.Sort((a, b) => a.CreatedUtc.CompareTo(b.CreatedUtc));
        return archives;
    }

    /// <summary>
    /// 清扫 .part / .list / .tmp 残骸。程序启动时以及每次批次结束后都跑一遍。
    /// </summary>
    public int SweepIntermediates()
    {
        if (string.IsNullOrWhiteSpace(_directory) || !Directory.Exists(_directory))
        {
            return 0;
        }

        int removed = 0;

        foreach (string pattern in IntermediatePatterns)
        {
            IEnumerable<string> files;

            try
            {
                files = Directory.EnumerateFiles(_directory, pattern);
            }
            catch
            {
                continue;
            }

            foreach (string file in files)
            {
                if (TryDelete(file))
                {
                    removed++;
                }
            }
        }

        if (removed > 0)
        {
            _log.Info($"已清扫 {removed} 个遗留的中间文件（.part / .list / .tmp）。");
        }

        return removed;
    }

    /// <summary>
    /// 执行三重配额。<paramref name="protectedPaths"/> 里的归档绝不删除。
    /// </summary>
    public QuotaReport Enforce(
        int keepDays,
        int maxCount,
        long maxTotalBytes,
        IReadOnlySet<string>? protectedPaths = null)
    {
        IReadOnlyList<ArchiveInfo> archives = ListArchives();

        if (archives.Count == 0)
        {
            return QuotaReport.Empty;
        }

        keepDays = Math.Max(1, keepDays);
        maxCount = Math.Max(1, maxCount);
        maxTotalBytes = Math.Max(1L * 1024 * 1024, maxTotalBytes);

        HashSet<string> locked = protectedPaths is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(protectedPaths, StringComparer.OrdinalIgnoreCase);

        DateTimeOffset cutoff = _time.GetUtcNow() - TimeSpan.FromDays(keepDays);

        List<ArchiveInfo> survivors = [];
        List<string> deleted = [];
        List<string> notes = [];
        long deletedBytes = 0;

        // 第一轮：按年龄。
        foreach (ArchiveInfo archive in archives)
        {
            if (archive.CreatedUtc >= cutoff)
            {
                survivors.Add(archive);
                continue;
            }

            if (locked.Contains(archive.Path))
            {
                survivors.Add(archive);
                notes.Add($"{archive.FileName} 已超过保留天数，但仍在等待上传，暂不删除。");
                continue;
            }

            if (TryDelete(archive.Path))
            {
                deleted.Add(archive.FileName);
                deletedBytes += archive.Bytes;
            }
            else
            {
                survivors.Add(archive);
            }
        }

        // 第二、三轮：按数量与总容量，都是最旧优先淘汰。survivors 已经是从旧到新排好的。
        // index 只在"这个文件不能删"时前进；能删就 RemoveAt，下一个最旧的自动落到同一位置。
        // 因此 survivors.Count 始终是真实剩余数量，index >= Count 表示剩下的全都不能删。
        long totalBytes = survivors.Sum(a => a.Bytes);
        int index = 0;

        while (index < survivors.Count
               && (survivors.Count > maxCount || totalBytes > maxTotalBytes))
        {
            ArchiveInfo candidate = survivors[index];

            if (locked.Contains(candidate.Path))
            {
                index++;
                continue;
            }

            if (TryDelete(candidate.Path))
            {
                deleted.Add(candidate.FileName);
                deletedBytes += candidate.Bytes;
                totalBytes -= candidate.Bytes;
                survivors.RemoveAt(index);
            }
            else
            {
                index++;
            }
        }

        int remainingCount = survivors.Count;
        long remainingBytes = survivors.Sum(a => a.Bytes);

        if (remainingCount > maxCount || remainingBytes > maxTotalBytes)
        {
            // 只有"剩下的全是待上传归档"才会走到这里。如实报告，不假装配额已生效。
            notes.Add(
                $"配额仍未满足：现存 {remainingCount} 个 / {ByteSize.Format(remainingBytes)}，" +
                $"上限 {maxCount} 个 / {ByteSize.Format(maxTotalBytes)}。" +
                "剩余归档全部处于待上传状态，删除会丢失备份，因此保留。");
        }

        if (deleted.Count > 0)
        {
            _log.Info($"配额清理：删除 {deleted.Count} 个归档，释放 {ByteSize.Format(deletedBytes)}；" +
                      $"现存 {remainingCount} 个 / {ByteSize.Format(remainingBytes)}。");
        }

        foreach (string note in notes)
        {
            _log.Warn(note);
        }

        return new QuotaReport(deleted.Count, deletedBytes, remainingCount, remainingBytes, deleted, notes);
    }

    /// <summary>打包前的磁盘预检：放不下就直接拒绝，不产生任何文件。</summary>
    public PrecheckResult Precheck(long estimatedBytes, long minFreeBytes) =>
        PrecheckPath(_directory, estimatedBytes, minFreeBytes, "临时目录");

    public static PrecheckResult PrecheckPath(string path, long estimatedBytes, long minFreeBytes, string label)
    {
        DiskSpaceInfo space = DiskSpace.Query(path);

        if (!space.Known)
        {
            // 拿不到磁盘信息就不阻拦（否则网络盘上根本没法用），但调用方会记一条警告。
            return new PrecheckResult(true, $"无法获取{label}所在卷的剩余空间，已跳过预检。", 0, 0);
        }

        // 已压缩过的内容（图片 / 视频 / 压缩包）压缩比接近 1，必须按最坏情况留量。
        long required = estimatedBytes <= 0
            ? 0
            : (long)Math.Min(long.MaxValue, estimatedBytes * 1.1);

        if (space.FreeBytes - required < minFreeBytes)
        {
            string reason =
                $"{label}所在卷空间不足：可用 {ByteSize.Format(space.FreeBytes)}，" +
                $"本次预计需要 {ByteSize.Format(required)}，" +
                $"且要求至少保留 {ByteSize.Format(minFreeBytes)}。已拒绝打包，未产生任何文件。";

            return new PrecheckResult(false, reason, space.FreeBytes, required);
        }

        return new PrecheckResult(true, null, space.FreeBytes, required);
    }

    private bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (FileNotFoundException)
        {
            return true;
        }
        catch (DirectoryNotFoundException)
        {
            return true;
        }
        catch (Exception ex)
        {
            _log.Warn($"删除文件失败：{path}", ex);
            return false;
        }
    }
}
