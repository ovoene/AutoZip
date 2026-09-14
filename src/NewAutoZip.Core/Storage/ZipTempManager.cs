using NewAutoZip.Core.Diagnostics;
using NewAutoZip.Core.Packing;

namespace NewAutoZip.Core.Storage;

/// <param name="Bytes">归档<b>自身</b>的字节数。</param>
public sealed record ArchiveInfo(string Path, long Bytes, DateTimeOffset CreatedUtc)
{
    public string FileName => System.IO.Path.GetFileName(Path);

    /// <summary>旁挂清单的字节数；没有清单就是 0。</summary>
    public long SidecarBytes { get; init; }

    /// <summary>
    /// 这个归档在磁盘上实际占的总量（含旁挂清单）。配额算账用它。
    ///
    /// 清单只有一两 KB，单看微不足道；但配额的意义是"ZipTemp 到底占了多少"，
    /// 而不是"归档文件加起来多少"。漏算的部分永远不会被任何一条上限看见 ——
    /// 这个项目要防的恰恰就是这种"没人负责的磁盘占用"。
    /// </summary>
    public long TotalBytes => Bytes + SidecarBytes;
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
    /// <summary>
    /// Runner 产生的中间文件，启动时一律清扫。
    ///
    /// <c>*.writing</c> 是旁挂清单的半成品后缀。它<b>不能</b>叫 <c>.tmp</c> ——
    /// 那样会和下面这条 <c>*.tmp</c> 撞上，正在写的清单可能被同一轮清扫删掉。
    ///
    /// 最后一条是<b>包内</b>清单的中转文件。它必须以那个固定名字落在磁盘上
    /// （演练解包后按名字找它），所以没法带临时后缀；正常路径下打包结束就删掉了，
    /// 这里兜住"打包中途进程被杀"留下的那一份。
    /// </summary>
    private static readonly string[] IntermediatePatterns =
        ["*.7z.part", "*.7z.list", "*.tmp", "*.writing", ArchiveManifest.EntryName];

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

                    long sidecarBytes = 0;

                    try
                    {
                        FileInfo sidecar = new(ArchiveManifest.SidecarPathFor(info.FullName));

                        if (sidecar.Exists)
                        {
                            sidecarBytes = sidecar.Length;
                        }
                    }
                    catch
                    {
                        // 读不到清单信息不影响归档本身入账。
                    }

                    archives.Add(new ArchiveInfo(
                        info.FullName,
                        info.Length,
                        new DateTimeOffset(created, TimeSpan.Zero))
                    {
                        SidecarBytes = sidecarBytes,
                    });
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
    /// 清扫 .part / .list / .tmp / .writing 残骸，以及演练留下的临时目录。
    /// 程序启动时以及每次批次结束后都跑一遍。
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

        removed += SweepDrillDirectories();

        if (removed > 0)
        {
            _log.Info($"已清扫 {removed} 个遗留的中间文件（.part / .list / .tmp / 演练目录）。");
        }

        return removed;
    }

    /// <summary>
    /// 删掉演练留下的临时目录。
    ///
    /// 这些目录里装的是<b>解压出来的完整副本</b>，和源文件一样大 ——
    /// 是这个程序在磁盘上单次占用最多的东西。演练自己的 finally 会删，
    /// 但进程被杀、解出来的文件正被杀毒软件占用时都删不掉，必须有人兜底。
    ///
    /// 跳过正在使用中的目录：手动演练和引擎演练在同一个进程里并行，
    /// 引擎 tick 结束时的这一轮清扫正好能撞上界面那边跑了一半的演练。
    /// </summary>
    private int SweepDrillDirectories()
    {
        IEnumerable<string> directories;

        try
        {
            directories = Directory.EnumerateDirectories(
                _directory, RestoreDrillService.DrillDirectoryPrefix + "*");
        }
        catch
        {
            return 0;
        }

        int removed = 0;

        foreach (string dir in directories)
        {
            if (RestoreDrillService.IsActive(dir))
            {
                continue;
            }

            try
            {
                Directory.Delete(dir, recursive: true);
                removed++;
            }
            catch (DirectoryNotFoundException)
            {
                // 已经没了，正是我们要的结果。
            }
            catch (Exception ex)
            {
                _log.Warn($"删除演练临时目录失败，下次清扫会再试：{dir}", ex);
            }
        }

        return removed;
    }

    /// <summary>
    /// 执行三重配额。<paramref name="protectedPaths"/> 里的归档绝不删除。
    ///
    /// 删归档时<b>连带删掉它的旁挂清单</b>。漏了这一步，清单就成了孤儿：
    /// 归档被淘汰一个它留下一个，谁也不会再删，一直攒到手动清理为止 ——
    /// 这正是这个类存在的理由那类 bug。
    /// </summary>
    public QuotaReport Enforce(
        int keepDays,
        int maxCount,
        long maxTotalBytes,
        IReadOnlySet<string>? protectedPaths = null)
    {
        IReadOnlyList<ArchiveInfo> archives = ListArchives();

        // 先收走那些"归档已经不在了，清单还留着"的孤儿。它们可能来自上一个版本、
        // 也可能来自某次删除只删掉了一半的中断 —— 不管怎么来的，留着没有任何用处。
        int orphans = SweepOrphanSidecars(archives);

        if (orphans > 0)
        {
            _log.Info($"清理了 {orphans} 份没有对应归档的旁挂清单。");
        }

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

            if (DeleteArchive(archive))
            {
                deleted.Add(archive.FileName);
                deletedBytes += archive.TotalBytes;
            }
            else
            {
                survivors.Add(archive);
            }
        }

        // 第二、三轮：按数量与总容量，都是最旧优先淘汰。survivors 已经是从旧到新排好的。
        // index 只在"这个文件不能删"时前进；能删就 RemoveAt，下一个最旧的自动落到同一位置。
        // 因此 survivors.Count 始终是真实剩余数量，index >= Count 表示剩下的全都不能删。
        long totalBytes = survivors.Sum(a => a.TotalBytes);
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

            if (DeleteArchive(candidate))
            {
                deleted.Add(candidate.FileName);
                deletedBytes += candidate.TotalBytes;
                totalBytes -= candidate.TotalBytes;
                survivors.RemoveAt(index);
            }
            else
            {
                index++;
            }
        }

        int remainingCount = survivors.Count;
        long remainingBytes = survivors.Sum(a => a.TotalBytes);

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

    /// <summary>删一个归档，连同它的旁挂清单。归档本身删掉了才算成功。</summary>
    private bool DeleteArchive(ArchiveInfo archive)
    {
        if (!TryDelete(archive.Path))
        {
            // 归档没删掉就别动清单 —— 那会把一个还在的包变成没有账本的包。
            return false;
        }

        TryDelete(ArchiveManifest.SidecarPathFor(archive.Path));
        return true;
    }

    /// <summary>删掉那些对应归档已经不存在的旁挂清单。</summary>
    private int SweepOrphanSidecars(IReadOnlyList<ArchiveInfo> archives)
    {
        IEnumerable<string> sidecars;

        try
        {
            sidecars = Directory.EnumerateFiles(_directory, "*" + ArchiveManifest.SidecarSuffix);
        }
        catch
        {
            return 0;
        }

        HashSet<string> known = new(
            archives.Select(a => ArchiveManifest.SidecarPathFor(a.Path)),
            StringComparer.OrdinalIgnoreCase);

        int removed = 0;

        foreach (string sidecar in sidecars)
        {
            if (known.Contains(sidecar))
            {
                continue;
            }

            // 归档不在了才删。这里不能反过来按"清单名去掉后缀"推归档路径就直接判断 ——
            // ListArchives 可能因为读不到某个文件而跳过它，那时归档其实还在。
            string archivePath = sidecar[..^ArchiveManifest.SidecarSuffix.Length];

            if (File.Exists(archivePath))
            {
                continue;
            }

            if (TryDelete(sidecar))
            {
                removed++;
            }
        }

        return removed;
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
