using System.Diagnostics;

using NewAutoZip.Core.Diagnostics;
using NewAutoZip.Core.Storage;

namespace NewAutoZip.Core.Packing;

/// <summary>演练是怎么收场的。</summary>
public enum DrillOutcome
{
    /// <summary>全过：包能解开，逐文件校验值都对得上。</summary>
    Passed,

    /// <summary>解开了，但有对不上的地方（少文件、哈希不符）。包能用，内容存疑。</summary>
    PassedWithWarnings,

    /// <summary><b>密码打不开这个包。</b>单列一档 —— 这是演练存在的首要理由。</summary>
    WrongPassword,

    /// <summary>包坏了、解压出错。</summary>
    Failed,

    /// <summary>空间不够 / 找不到 7za / 包不在了，压根没验成。<b>不等于包有问题。</b></summary>
    Skipped,

    Cancelled,
}

/// <param name="Outcome">结局。</param>
/// <param name="ArchiveFileName">验的是哪个包。</param>
/// <param name="Checked">比对了几个文件的校验值。</param>
/// <param name="Mismatched">校验值对不上的文件数。</param>
/// <param name="Missing">清单里有、解出来却没有的文件数。</param>
/// <param name="Message">给人看的一句话结论。</param>
public sealed record DrillResult(
    DrillOutcome Outcome,
    string ArchiveFileName,
    int Checked,
    int Mismatched,
    int Missing,
    TimeSpan Elapsed,
    string Message)
{
    /// <summary>
    /// 演练是否<b>证明了这个包可用</b>。
    /// <see cref="DrillOutcome.Skipped"/> 不算通过 —— 它什么都没证明，
    /// 但也不该被当成失败去拦投递、发告警。调用方要分开处理这两件事。
    /// </summary>
    public bool Ok => Outcome is DrillOutcome.Passed or DrillOutcome.PassedWithWarnings;

    /// <summary>真的验出问题了（而不是没能验）。发通知、拦投递看这个。</summary>
    public bool IsProblem => Outcome is DrillOutcome.WrongPassword or DrillOutcome.Failed;

    public static DrillResult Skip(string archiveFileName, string message) =>
        new(DrillOutcome.Skipped, archiveFileName, 0, 0, 0, TimeSpan.Zero, message);
}

/// <summary>
/// 恢复演练 —— <b>真的把包解开一遍</b>，证明它还能用。
///
/// 为什么光有打包后的 <c>7za t</c> 不够：那一步用的是内存里刚拿来打包的那个密码，
/// 验的是刚写完的那个包，只能证明"刚才那个密码能解开刚才那个包"，近乎同义反复。
/// 它<b>证明不了</b>：
///
/// <list type="bullet">
/// <item>settings.json 里存的密码还解不解得开三天前那个包（DPAPI 密文被破坏、用户改了密码）；</item>
/// <item>包传上云之后有没有在传输或存储中损坏；</item>
/// <item>解出来的内容是不是还等于当初压进去的内容。</item>
/// </list>
///
/// 备份行业那句老话：<b>没试过恢复的备份不算备份。</b>
///
/// <b>清理是 finally 里的无条件动作。</b>这个项目最惨痛的教训（README 第三节）
/// 就是失败分支不清理，导致磁盘被一路填满。演练要解出一份和源文件等大的副本，
/// 是这个程序里单次占用磁盘最多的操作，绝不能有一条路径漏掉清理。
/// </summary>
public sealed class RestoreDrillService
{
    /// <summary>演练用的临时目录前缀。清扫时按它匹配。</summary>
    public const string DrillDirectoryPrefix = "__drill_";

    /// <summary>
    /// 正在使用中的演练目录。
    ///
    /// <see cref="Storage.ZipTempManager.SweepIntermediates"/> 会把 ZipTemp 下所有
    /// <c>__drill_*</c> 目录当成残骸删掉 —— 那对<b>上次异常退出</b>留下的目录是对的，
    /// 但它不能删掉一个<b>正在解压</b>的目录。
    ///
    /// 目录名里的进程号挡不住这件事：界面上手动触发的演练和引擎自己跑的演练
    /// <b>在同一个进程里</b>，只是不在同一个线程上。引擎 tick 结束时照例清扫一遍，
    /// 正好可以撞上界面那一侧跑了一半的演练。所以在这里登记，清扫时避开。
    /// </summary>
    private static readonly HashSet<string> ActiveDrills = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>这个演练目录是不是正在用。清扫前问一句。</summary>
    public static bool IsActive(string directory)
    {
        string key = NormalizeKey(directory);

        lock (ActiveDrills)
        {
            return ActiveDrills.Contains(key);
        }
    }

    private static void Register(string directory)
    {
        string key = NormalizeKey(directory);

        lock (ActiveDrills)
        {
            ActiveDrills.Add(key);
        }
    }

    private static void Unregister(string directory)
    {
        string key = NormalizeKey(directory);

        lock (ActiveDrills)
        {
            ActiveDrills.Remove(key);
        }
    }

    /// <summary>登记与查询必须用同一种写法，否则避让会失效（而且是静默失效）。</summary>
    private static string NormalizeKey(string directory)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        }
        catch
        {
            return directory;
        }
    }

    /// <summary>
    /// 逐文件比对的条数上限。
    ///
    /// 校验值比对要把解出来的文件<b>再读一遍</b>：一个 5 GB 的包，解压读一遍、
    /// 比对再读一遍，磁盘 IO 翻倍。包内清单可能有几十万条，全比一遍能跑到天亮，
    /// 而这个操作是在备份主循环里同步做的 —— 它会挡住下一批文件的处理。
    ///
    /// 所以：<b>全部文件都验"解得出来、大小对"，抽头 N 个验校验值。</b>
    /// 位腐和截断是整片发生的，抽样足以发现；而"密码打不开"这个首要目标
    /// 在解压那一步就已经判定完了，根本不依赖逐文件比对。
    /// 抽了多少条如实记在结果里，不假装全验过。
    /// </summary>
    private const int MaxHashChecks = 64;

    /// <summary>
    /// 演练目录的序号发生器。
    ///
    /// 目录名里光有进程号是不够的：界面上手动触发的演练和引擎自己跑的演练
    /// <b>在同一个进程里</b>，而它们完全可能挑中<b>同一个包</b>
    /// （用户看着某个包可疑就手动验一把，引擎恰好也轮到它）。
    /// 那样两次演练会解进同一个目录，谁先跑完谁就把另一边的解压结果连目录一起删掉 ——
    /// 于是另一边报"演练失败"。这种假警报比不报还坏：它会让人开始怀疑所有演练结果。
    /// </summary>
    private static int _drillSequence;

    private readonly SevenZipRunner _runner;
    private readonly IAppLogger _log;

    public RestoreDrillService(SevenZipRunner runner, IAppLogger log)
    {
        _runner = runner;
        _log = log;
    }

    /// <summary>
    /// 对一个归档做完整演练。
    /// </summary>
    /// <param name="archivePath">
    /// 要验的包。可以在云盘目录里 —— 那种情况下会先<b>复制</b>到
    /// <paramref name="workRoot"/> 再验（读云端脱水文件会触发重新下载，
    /// 而且不能在人家同步目录里解压）。复制进来的那份在 finally 里删掉。
    /// </param>
    /// <param name="password">要验证的密码。<b>调用方应当传从 settings.json 现读出来的那个</b>，
    /// 而不是内存里用于打包的副本 —— 验的就是"存下来的那个还好不好用"。</param>
    /// <param name="workRoot">干活的目录（ZipTemp）。</param>
    /// <param name="minFreeBytes">磁盘预检的下限。</param>
    public async Task<DrillResult> RunAsync(
        string archivePath,
        string password,
        string workRoot,
        TimeSpan timeout,
        long minFreeBytes,
        CancellationToken ct)
    {
        long startedAt = Stopwatch.GetTimestamp();
        string archiveName = Path.GetFileName(archivePath);

        if (!_runner.IsAvailable)
        {
            return DrillResult.Skip(archiveName, "找不到压缩程序，本次演练跳过。");
        }

        if (!File.Exists(archivePath))
        {
            return DrillResult.Skip(archiveName, "归档已不存在，本次演练跳过。");
        }

        long archiveBytes;
        try
        {
            archiveBytes = new FileInfo(archivePath).Length;
        }
        catch (Exception ex)
        {
            return DrillResult.Skip(archiveName, $"读不到归档信息，本次演练跳过：{ex.Message}");
        }

        // 需要的空间：解出来的内容（按最坏情况估成压缩包的 3 倍）+ 可能要复制进来的那一份。
        // 估不准没关系 —— 宁可跳过一次演练，也不能把磁盘写满。
        bool needsCopy = !IsUnder(archivePath, workRoot);
        long estimated = (archiveBytes * 3) + (needsCopy ? archiveBytes : 0);

        PrecheckResult precheck = ZipTempManager.PrecheckPath(workRoot, estimated, minFreeBytes, "演练目录");

        if (!precheck.Ok)
        {
            return DrillResult.Skip(
                archiveName,
                $"磁盘空间不足，本次演练跳过（不影响备份本身）。{precheck.Reason}");
        }

        // 目录名带上归档名、进程号与一个自增序号：多个实例、以及同一进程里
        // 并行的两次演练（界面手动 + 引擎自动）都不会互相踩，
        // 也让人一眼看出这个目录是谁留下的。
        string drillDir = Path.Combine(
            workRoot,
            DrillDirectoryPrefix + Path.GetFileNameWithoutExtension(archiveName)
            + "_" + Environment.ProcessId
            + "_" + Interlocked.Increment(ref _drillSequence));

        // 解压落点单独一层。云端包复制进来时放在 drillDir 根部，
        // 与解压结果分开 —— 否则找包内清单时会连那份复制品一起扫。
        string extractDir = Path.Combine(drillDir, "extracted");

        string? copiedArchive = null;

        // 登记在先：从这一刻起，清扫要绕开这个目录。
        Register(drillDir);

        try
        {
            string target = archivePath;

            if (needsCopy)
            {
                // 云端的包：复制到本地再验。这一步在 OneDrive 脱水场景下会触发重新下载，
                // 是有流量成本的 —— 所以云端演练默认关着、周期由用户自己定。
                //
                // 复制品必须放在 drillDir <b>里面</b>，不能放 ZipTemp 根部：
                // 根部任何一个 .7z 都会被 ZipTempManager.ListArchives 当成正经归档，
                // 于是配额可能在演练进行到一半时把它删掉，或者把它算进"现存几个包"。
                Directory.CreateDirectory(drillDir);
                copiedArchive = Path.Combine(drillDir, archiveName);
                _log.Info($"恢复演练：把 {archiveName} 复制到本地临时目录（云端包可能需要重新下载）。");

                await CopyAsync(archivePath, copiedArchive, ct).ConfigureAwait(false);
                target = copiedArchive;
            }

            // 第一步：t 校验。密码不对在这里就会暴露 —— 这是整个演练的首要目标，
            // 而且它比解压便宜得多（不落盘）。
            (bool verifyOk, string? verifyError, bool wrongPassword) =
                await _runner.VerifyArchiveAsync(target, password, timeout, ct).ConfigureAwait(false);

            if (!verifyOk)
            {
                return new DrillResult(
                    wrongPassword ? DrillOutcome.WrongPassword : DrillOutcome.Failed,
                    archiveName,
                    0, 0, 0,
                    Stopwatch.GetElapsedTime(startedAt),
                    wrongPassword
                        ? $"密码打不开这个包：{archiveName}。存在设置里的密码与它对不上 —— " +
                          "现在还能补救（找回旧密码、或重做一份用当前密码加密的备份）；" +
                          "等真要恢复那天才发现就来不及了。"
                        : $"完整性校验未通过：{verifyError}");
            }

            // 第二步：真解一遍。t 只解码不落盘，解压才能证明"文件真能被还原出来"。
            ExtractResult extract = await _runner.ExtractAsync(
                new ExtractRequest(target, extractDir, password, timeout, Overwrite: true),
                null,
                ct).ConfigureAwait(false);

            if (!extract.Ok)
            {
                return new DrillResult(
                    extract.WrongPassword ? DrillOutcome.WrongPassword
                        : extract.Outcome == PackOutcome.Cancelled ? DrillOutcome.Cancelled
                        : DrillOutcome.Failed,
                    archiveName,
                    0, 0, 0,
                    Stopwatch.GetElapsedTime(startedAt),
                    extract.Error ?? "解压失败。");
            }

            // 第三步：拿包内清单逐条比对。
            return await CompareAsync(extractDir, archiveName, startedAt, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new DrillResult(
                DrillOutcome.Cancelled,
                archiveName,
                0, 0, 0,
                Stopwatch.GetElapsedTime(startedAt),
                "演练已取消。");
        }
        catch (Exception ex)
        {
            _log.Error($"恢复演练出现未预期的异常：{archiveName}", ex);

            return new DrillResult(
                DrillOutcome.Failed,
                archiveName,
                0, 0, 0,
                Stopwatch.GetElapsedTime(startedAt),
                $"演练过程异常：{ex.Message}");
        }
        finally
        {
            // 无条件清理。上面每一条 return 路径都会经过这里 —— 这是刻意的。
            TryDeleteDirectory(drillDir);

            // 复制进来的那份包在 drillDir 里面，上一句正常情况下已经连它一起删了。
            // 这里再补一刀是因为：递归删目录只要有<b>一个</b>解出来的文件被占用就会中途失败，
            // 而那份复制品往往是整个目录里最大的一个文件。单独再删一次，
            // 至少能把最占地方的那块空间收回来。
            if (copiedArchive is not null)
            {
                TryDeleteFile(copiedArchive);
            }

            // 注销放在删除<b>之后</b>：中间这段时间里目录始终是"有主"的，
            // 清扫不会和我们自己的删除撞在一起。删不掉也要注销 ——
            // 那样下一轮清扫才会接手重试。
            Unregister(drillDir);
        }
    }

    /// <summary>拿包内清单逐条核对解出来的东西。</summary>
    private async Task<DrillResult> CompareAsync(
        string extractedRoot,
        string archiveName,
        long startedAt,
        CancellationToken ct)
    {
        ArchiveManifestDocument? manifest = ArchiveManifest.TryFindInnerManifest(extractedRoot, out string? manifestError);

        if (manifest is null)
        {
            // 老版本产出的包里没有清单。包解开了就是解开了，如实说明"没法逐条核对"。
            return new DrillResult(
                DrillOutcome.PassedWithWarnings,
                archiveName,
                0, 0, 0,
                Stopwatch.GetElapsedTime(startedAt),
                $"包能正常解开，但没能逐文件核对（{manifestError}）。" +
                "这通常说明它是启用清单之前产出的旧包。");
        }

        // 解出来的文件按"文件名 + 大小"建索引。
        //
        // 不按相对路径匹配：清单里的路径是相对监控目录算的，而 7-Zip 存条目时
        // 会剥掉盘符和公共前缀，两边的层级对不上。文件名 + 大小已经足够 ——
        // 这一步要抓的是"少了文件 / 内容变了"，不是"目录结构变了"。
        Dictionary<string, List<string>> extracted = new(StringComparer.OrdinalIgnoreCase);

        foreach (string file in Directory.EnumerateFiles(extractedRoot, "*", SearchOption.AllDirectories))
        {
            string name = Path.GetFileName(file);

            if (name.Equals(ArchiveManifest.EntryName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!extracted.TryGetValue(name, out List<string>? bucket))
            {
                bucket = [];
                extracted[name] = bucket;
            }

            bucket.Add(file);
        }

        int missing = 0;
        int mismatched = 0;
        int hashChecked = 0;
        List<string> details = [];

        foreach (ArchiveManifestEntry entry in manifest.Entries)
        {
            ct.ThrowIfCancellationRequested();

            // 打包时就没读到的文件不该记在解压这一边的账上 —— 它本来就没进包。
            if (entry.Note is { Length: > 0 } && !entry.Verifiable)
            {
                continue;
            }

            string name = Path.GetFileName(entry.Path);

            if (!extracted.TryGetValue(name, out List<string>? candidates) || candidates.Count == 0)
            {
                missing++;

                if (details.Count < 5)
                {
                    details.Add($"缺失：{name}");
                }

                continue;
            }

            // 同名文件可能有多个（不同子目录）。先按大小对上的那个。
            string? match = candidates.FirstOrDefault(p => SafeLength(p) == entry.Bytes);

            if (match is null)
            {
                mismatched++;

                if (details.Count < 5)
                {
                    details.Add($"大小不符：{name}");
                }

                continue;
            }

            // 校验值比对是抽样的，理由见 MaxHashChecks。
            if (hashChecked >= MaxHashChecks || !entry.Verifiable)
            {
                continue;
            }

            hashChecked++;

            try
            {
                string actual = await ArchiveManifest.ComputeSha256Async(match, ct).ConfigureAwait(false);

                if (!string.Equals(actual, entry.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    mismatched++;

                    if (details.Count < 5)
                    {
                        details.Add($"校验值不符：{name}");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                mismatched++;

                if (details.Count < 5)
                {
                    details.Add($"无法读取解出的文件 {name}：{ex.Message}");
                }
            }
        }

        TimeSpan elapsed = Stopwatch.GetElapsedTime(startedAt);
        bool clean = missing == 0 && mismatched == 0;

        string sampleNote = manifest.Entries.Count > hashChecked
            ? $"（全部 {manifest.Entries.Count} 个文件都已解出并核对大小，其中 {hashChecked} 个抽验了校验值）"
            : $"（{hashChecked} 个文件全部核对了校验值）";

        string message = clean
            ? $"恢复演练通过：{archiveName} 能用当前密码完整解开，内容与打包时一致{sampleNote}，" +
              $"耗时 {elapsed.TotalSeconds:0.#} 秒。"
            : $"恢复演练发现问题：{archiveName} 能解开，但 {missing} 个文件缺失、{mismatched} 个对不上。" +
              (details.Count > 0 ? " " + string.Join("；", details) : string.Empty);

        return new DrillResult(
            clean ? DrillOutcome.Passed : DrillOutcome.PassedWithWarnings,
            archiveName,
            hashChecked,
            mismatched,
            missing,
            elapsed,
            message);
    }

    private static async Task CopyAsync(string source, string destination, CancellationToken ct)
    {
        await using FileStream input = new(
            source, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, useAsync: true);

        await using FileStream output = new(
            destination, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, useAsync: true);

        await input.CopyToAsync(output, ct).ConfigureAwait(false);
    }

    /// <summary>路径是不是在某个目录下面。用于判断"这个包已经在本地工作目录里了吗"。</summary>
    private static bool IsUnder(string path, string root)
    {
        try
        {
            string full = Path.GetFullPath(path);
            string fullRoot = Path.GetFullPath(root);

            return full.StartsWith(
                fullRoot.EndsWith(Path.DirectorySeparatorChar) ? fullRoot : fullRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static long SafeLength(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch
        {
            return -1;
        }
    }

    private void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex)
        {
            // 演练目录留在磁盘上是要出事的（它和源文件一样大）。删不掉必须让人知道，
            // 而且 ZipTempManager 的清扫会在下一轮再试一次。
            _log.Warn($"删除演练临时目录失败，下次清扫会再试：{path}", ex);
        }
    }

    private void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"删除演练临时文件失败，下次清扫会再试：{path}", ex);
        }
    }
}
