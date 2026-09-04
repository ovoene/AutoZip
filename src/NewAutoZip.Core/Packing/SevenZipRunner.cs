using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using NewAutoZip.Core.Diagnostics;

namespace NewAutoZip.Core.Packing;

/// <summary>
/// 调用 7-Zip 命令行创建 AES-256 加密的 .7z。
///
/// 相对旧版 <c>Zip7Service</c> 的改动（每一条都对应一个实际缺陷）：
///
/// * <b>不再死锁</b>：旧版重定向了 stdout 却从不读取，7z 写满管道缓冲区（Windows 上约 4 KB）
///   就永久阻塞，而主线程在 <c>ReadToEnd()</c> 等 stderr，双方互等。
///   这里用 OutputDataReceived / ErrorDataReceived 异步双流。
/// * <b>有时限、可取消</b>：旧版 <c>WaitForExit()</c> 无参，卡住就是永远。
/// * <b>原子提交</b>：先写 .part，校验通过才改名。失败绝不留下半成品 .7z
///   （旧版失败时残骸留在 ZipTemp，下一轮又打一个，这是磁盘被塞满的直接现象）。
/// * <b>@listfile</b>：旧版把所有路径拼进命令行，超过 32767 字符就直接失败。
/// * <b>退出码分级</b>：见 <see cref="SevenZipExitCode"/>。
/// * <b>密码不进日志</b>：命令行在记录前经 <see cref="SecretRedactor"/> 脱敏。
/// </summary>
public sealed class SevenZipRunner
{
    /// <summary>保留的输出行上限，防止上万个文件时把内存和日志刷爆。</summary>
    private const int MaxRetainedLines = 200;

    /// <summary>
    /// 空 .7z 的字节数。7za 产出的空归档正好是 32 字节的头。
    /// 只在拿不到"Add new data to archive"计数时作为兜底判据。
    /// </summary>
    private const long EmptyArchiveMaxBytes = 32;

    private static readonly Regex ProgressPattern = new(
        @"^\s*(?<pct>\d{1,3})%(?:\s+(?<count>\d+))?(?:\s+[-+U=T.]\s+(?<file>.+?))?\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// 7za 结尾会打印 <c>Add new data to archive: N files, M bytes</c>。
    /// 这是<b>扫描阶段</b>解析出的条目数：清单里的文件全都不存在时它就是 0。
    ///
    /// 注意<b>不能</b>用同样出现在结尾的 <c>Files read from disk: N</c>：那一行统计的是
    /// "读出过内容的文件数"，压一批 0 字节文件时它也是 0，而归档是完全正常的。
    /// 拿它判空会把合法归档误删。
    ///
    /// 我们随程序携带的是 7za.exe 独立控制台版，它自身的提示一律英文（只有 Windows 给出的
    /// 错误原因会本地化），所以这里可以安全地按英文匹配 —— 不同于旧版去猜中文 Shell 列名。
    /// 万一用的是本地化过的完整版 7z.exe 而匹配不到，退回按归档体积判空。
    /// </summary>
    private static readonly Regex EntriesAddedPattern = new(
        @"^\s*Add new data to archive:\s*(?<n>\d+)\s+files?\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>
    /// <c>WARNING: Cannot open 3 files</c> —— 扫描时找到了、但<b>读的时候</b>打不开的文件数
    /// （被别的进程独占）。这些文件不会进包，但也不会让上面那行的数字变小，
    /// 所以"真正进包的条目数"= 扫描数 − 这个数。
    /// </summary>
    private static readonly Regex CannotOpenPattern = new(
        @"Cannot open\s+(?<n>\d+)\s+files?\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private readonly string _exePath;
    private readonly IAppLogger _log;

    public SevenZipRunner(string exePath, IAppLogger log)
    {
        _exePath = exePath;
        _log = log;
    }

    public string ExePath => _exePath;

    public bool IsAvailable => File.Exists(_exePath);

    /// <summary>读取 7za 版本号，同时验证它真的能跑起来。失败返回 null。</summary>
    public async Task<string?> TryGetVersionAsync(CancellationToken ct)
    {
        if (!IsAvailable)
        {
            return null;
        }

        try
        {
            ProcessRunResult run = await RunAsync(["i"], TimeSpan.FromSeconds(20), null, ct).ConfigureAwait(false);

            foreach (string line in run.Lines)
            {
                if (line.StartsWith("7-Zip", StringComparison.OrdinalIgnoreCase))
                {
                    return line.Trim();
                }
            }

            return run.ExitCode == SevenZipExitCode.Ok ? "7-Zip（版本未知）" : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Warn($"无法运行 7za（{_exePath}）。", ex);
            return null;
        }
    }

    public async Task<PackResult> CreateAsync(
        PackRequest request,
        IProgress<PackProgress>? progress,
        CancellationToken ct)
    {
        long startedAt = Stopwatch.GetTimestamp();

        if (!IsAvailable)
        {
            return PackResult.Fail(
                PackOutcome.ExecutableMissing,
                $"找不到压缩程序：{_exePath}。请确认 tools\\7za.exe 与主程序一起发布。");
        }

        if (request.Files.Count == 0)
        {
            return PackResult.Fail(PackOutcome.Failed, "文件清单为空，没有可压缩的内容。");
        }

        string partPath = request.OutputPath + ".part";
        string listPath = request.OutputPath + ".list";

        try
        {
            string? directory = Path.GetDirectoryName(request.OutputPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // 清掉上一次可能残留的中间产物，避免 7z 往旧包里追加内容。
            TryDelete(partPath);
            TryDelete(request.OutputPath);

            // 清单用 UTF-8 无 BOM + -scsUTF-8：7-Zip 明确按该开关解释清单编码，
            // 加 BOM 会让第一行路径多出三个字节而找不到文件。
            await File.WriteAllLinesAsync(listPath, request.Files, new UTF8Encoding(false), ct)
                .ConfigureAwait(false);

            List<string> args = BuildCreateArgs(request, partPath, listPath);

            _log.Info($"开始压缩 {request.Files.Count} 个文件 → {Path.GetFileName(request.OutputPath)}");
            _log.Debug($"命令行：{SecretRedactor.Shared.Redact(_exePath + " " + string.Join(' ', args))}");

            ProcessRunResult run = await RunAsync(args, request.Timeout, progress, ct).ConfigureAwait(false);
            TimeSpan elapsed = Stopwatch.GetElapsedTime(startedAt);

            List<string> warnings = ExtractWarnings(run.Lines);

            if (run.TimedOut)
            {
                TryDelete(partPath);
                return PackResult.Fail(
                    PackOutcome.TimedOut,
                    $"压缩超时（超过 {request.Timeout.TotalMinutes:0.#} 分钟），已终止 7za 并删除半成品。",
                    run.ExitCode,
                    elapsed);
            }

            if (!SevenZipExitCode.IsArchiveUsable(run.ExitCode))
            {
                TryDelete(partPath);

                string detail = BuildFailureDetail(run.Lines);
                return PackResult.Fail(
                    PackOutcome.Failed,
                    $"压缩失败：{SevenZipExitCode.Describe(run.ExitCode)}。{detail}",
                    run.ExitCode,
                    elapsed);
            }

            if (!File.Exists(partPath))
            {
                // 退出码说成功但文件不在 —— 只能当失败处理，绝不能上报成功。
                return PackResult.Fail(
                    PackOutcome.Failed,
                    $"7za 返回 {run.ExitCode}（{SevenZipExitCode.Describe(run.ExitCode)}），但没有生成归档文件。",
                    run.ExitCode,
                    elapsed);
            }

            if (run.ExitCode == SevenZipExitCode.Warning)
            {
                _log.Warn($"7za 返回退出码 1：{SevenZipExitCode.Describe(SevenZipExitCode.Warning)}。" +
                          $"共 {warnings.Count} 条警告。");

                foreach (string warning in warnings.Take(20))
                {
                    _log.Warn($"  {warning}");
                }
            }

            // 真正进包的条目数：扫描到的条目 − 读的时候打不开的条目。
            int? scanned = ParseEntriesAdded(run.Lines);
            int unreadable = ParseCannotOpenCount(run.Lines);
            int? actualCount = scanned is int n ? Math.Max(0, n - unreadable) : null;

            // 空归档拦截。
            //
            // 清单里的文件在压缩开始前全部消失（被删或被移走）时，7za 并不报致命错误：
            // 它打印 WARNING、以退出码 1 结束，并留下一个 32 字节的空 .7z。
            // 按退出码分级这是"包可用"，于是会一路晋级、投递到 OneDrive、发一条
            // "备份完成"的通知 —— 一次彻底的假成功，而且比直接失败更危险：
            // 用户以为备份好了，实际什么都没有。
            //
            // 两道判据，任一命中即判空：
            //   1. 算出来真正进包的条目数为 0（全部消失，或全部被独占打不开）；
            //   2. 归档体积没超过空归档头的 32 字节 —— 不管上面的数字怎么算，
            //      32 字节的 .7z 里不可能有内容。这一条同时兜住了"用的是本地化版
            //      7z.exe、上面那行匹配不到"的情况。
            //      （反例参照：一批 0 字节文件的合法归档是 171 字节，不会被误伤。）
            bool archiveIsEmpty = actualCount == 0 || SafeLength(partPath) <= EmptyArchiveMaxBytes;

            if (archiveIsEmpty)
            {
                TryDelete(partPath);

                string detail = warnings.Count > 0
                    ? string.Join(" | ", warnings.Take(3))
                    : BuildFailureDetail(run.Lines);

                return PackResult.Fail(
                    PackOutcome.Failed,
                    "归档是空的：一个文件都没能加进去（清单里的文件可能已全部被删除、移走或被独占占用）。" +
                    $"已删除空归档，不会上报成功。{detail}".TrimEnd(),
                    run.ExitCode,
                    Stopwatch.GetElapsedTime(startedAt));
            }

            if (actualCount is int actual && actual < request.Files.Count)
            {
                // 部分文件没进包：包是好的（场景 2），但必须如实说明少了几个，
                // 而不是拿清单条数当归档内容数上报。
                _log.Warn($"清单 {request.Files.Count} 个文件，实际只压进 {actual} 个，" +
                          $"跳过了 {request.Files.Count - actual} 个（详见上面的警告）。");
            }

            if (request.VerifyAfterPack)
            {
                (bool ok, string? error) = await VerifyAsync(partPath, request.Password, request.Timeout, ct)
                    .ConfigureAwait(false);

                if (!ok)
                {
                    TryDelete(partPath);
                    return PackResult.Fail(
                        PackOutcome.VerificationFailed,
                        $"归档完整性校验失败，已删除：{error}",
                        run.ExitCode,
                        Stopwatch.GetElapsedTime(startedAt));
                }
            }

            // 原子晋级：到这一步才出现名为 .7z 的文件。
            try
            {
                File.Move(partPath, request.OutputPath, overwrite: true);
            }
            catch (Exception ex)
            {
                TryDelete(partPath);
                return PackResult.Fail(
                    PackOutcome.Failed,
                    $"归档已生成但改名失败：{ex.Message}",
                    run.ExitCode,
                    Stopwatch.GetElapsedTime(startedAt));
            }

            long size = SafeLength(request.OutputPath);
            elapsed = Stopwatch.GetElapsedTime(startedAt);

            return new PackResult(
                run.ExitCode == SevenZipExitCode.Ok ? PackOutcome.Success : PackOutcome.SuccessWithWarnings,
                run.ExitCode,
                request.OutputPath,
                size,
                actualCount ?? request.Files.Count,
                elapsed,
                warnings,
                null);
        }
        catch (OperationCanceledException)
        {
            TryDelete(partPath);
            return PackResult.Fail(
                PackOutcome.Cancelled,
                "压缩已被取消，半成品已清理。",
                SevenZipExitCode.UserStopped,
                Stopwatch.GetElapsedTime(startedAt));
        }
        catch (Exception ex)
        {
            TryDelete(partPath);
            _log.Error("压缩过程出现未预期的异常。", ex);
            return PackResult.Fail(
                PackOutcome.Failed,
                $"压缩过程异常：{ex.Message}",
                elapsed: Stopwatch.GetElapsedTime(startedAt));
        }
        finally
        {
            TryDelete(listPath);
        }
    }

    /// <summary>用 <c>7za t</c> 做一次真实解码校验。加密文件名时必须带密码，否则连头都读不了。</summary>
    private async Task<(bool Ok, string? Error)> VerifyAsync(
        string archivePath,
        string password,
        TimeSpan timeout,
        CancellationToken ct)
    {
        List<string> args = ["t", "-y", "-bso0", "-bsp0", "-sccUTF-8"];

        if (!string.IsNullOrEmpty(password))
        {
            args.Add("-p" + password);
        }
        else
        {
            args.Add("-p");
        }

        args.Add(archivePath);

        try
        {
            ProcessRunResult run = await RunAsync(args, timeout, null, ct).ConfigureAwait(false);

            if (run.TimedOut)
            {
                return (false, "校验超时。");
            }

            if (SevenZipExitCode.IsArchiveUsable(run.ExitCode))
            {
                return (true, null);
            }

            return (false, $"{SevenZipExitCode.Describe(run.ExitCode)} {BuildFailureDetail(run.Lines)}".Trim());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static List<string> BuildCreateArgs(PackRequest request, string partPath, string listPath)
    {
        List<string> args =
        [
            "a",
            "-t7z",
            "-mx=" + request.CompressionLevel.ToString(CultureInfo.InvariantCulture),
            "-mmt=on",
            "-ssw",              // 允许压缩正被其他进程写入的文件，大幅降低"退出码 1"的概率
            "-y",                // 不交互，任何提问都自动回答 yes
            "-bsp1",             // 进度写到 stdout，用于解析真实百分比
            "-scsUTF-8",         // @listfile 按 UTF-8 解释
            "-sccUTF-8",         // 控制台输出用 UTF-8，中文文件名不乱码
        ];

        if (request.EncryptFileNames)
        {
            args.Add("-mhe=on");
        }

        // 用 ArgumentList 传参，由运行时按 Windows 规则加引号；不需要手工拼接与转义。
        // 残留风险：-p 后面的密码仍会短暂出现在进程命令行上，7-Zip CLI 无法从 stdin 读密码。
        args.Add("-p" + request.Password);

        // 注意：不能加 "--"。7-Zip 的 -- 开关会同时停止解析 @listfile。
        args.Add(partPath);
        args.Add("@" + listPath);

        return args;
    }

    private sealed record ProcessRunResult(int ExitCode, List<string> Lines, bool TimedOut);

    private async Task<ProcessRunResult> RunAsync(
        IReadOnlyList<string> args,
        TimeSpan timeout,
        IProgress<PackProgress>? progress,
        CancellationToken ct)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = _exePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(_exePath) is { Length: > 0 } dir
                ? dir
                : Environment.CurrentDirectory,
        };

        foreach (string arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        List<string> lines = new(MaxRetainedLines);
        object linesGate = new();
        int lastPercent = -1;

        void Capture(string? line)
        {
            if (line is null)
            {
                return;
            }

            // -bsp1 的进度行以 \r 回车刷新，一行里可能挤着多个百分比片段。
            foreach (string piece in line.Split('\r', StringSplitOptions.RemoveEmptyEntries))
            {
                string text = piece.TrimEnd();
                if (text.Length == 0)
                {
                    continue;
                }

                Match match = ProgressPattern.Match(text);
                if (match.Success)
                {
                    if (progress is null)
                    {
                        continue;
                    }

                    int percent = int.Parse(match.Groups["pct"].Value, CultureInfo.InvariantCulture);
                    percent = Math.Clamp(percent, 0, 100);

                    string? current = match.Groups["file"].Success
                        ? match.Groups["file"].Value.Trim()
                        : null;

                    if (percent != lastPercent || current is not null)
                    {
                        lastPercent = percent;
                        progress.Report(new PackProgress(percent, current));
                    }

                    continue;
                }

                lock (linesGate)
                {
                    bool interesting = text.Contains("WARNING", StringComparison.OrdinalIgnoreCase)
                        || text.Contains("ERROR", StringComparison.OrdinalIgnoreCase)
                        || text.Contains("Cannot", StringComparison.OrdinalIgnoreCase)
                        // 空归档判据所依赖的那一行 —— 上万条警告也不能把它挤掉。
                        || text.Contains("Add new data to archive", StringComparison.OrdinalIgnoreCase);

                    if (lines.Count < MaxRetainedLines || interesting)
                    {
                        lines.Add(text);
                    }
                }
            }
        }

        using Process process = new() { StartInfo = startInfo };
        process.OutputDataReceived += (_, e) => Capture(e.Data);
        process.ErrorDataReceived += (_, e) => Capture(e.Data);

        if (!process.Start())
        {
            throw new InvalidOperationException($"无法启动进程：{_exePath}");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // 关掉 stdin：若 7z 仍想提问（-y 之外的极端情况），它会立刻收到 EOF 而不是永久等待。
        try
        {
            process.StandardInput.Close();
        }
        catch
        {
            // 无所谓
        }

        using CancellationTokenSource timeoutCts = new();
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        if (timeout > TimeSpan.Zero && timeout < TimeSpan.MaxValue)
        {
            timeoutCts.CancelAfter(timeout);
        }

        bool timedOut = false;

        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            timedOut = timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested;

            KillTree(process);

            if (!timedOut)
            {
                // 是调用方要求停止 —— 往上抛，由 CreateAsync 归类成 Cancelled。
                throw;
            }
        }

        // 等异步流把剩余内容排空（WaitForExit 的无参重载才会做这件事）。
        try
        {
            process.WaitForExit();
        }
        catch
        {
            // 已退出
        }

        int exitCode;
        try
        {
            exitCode = process.ExitCode;
        }
        catch
        {
            exitCode = timedOut ? SevenZipExitCode.UserStopped : SevenZipExitCode.FatalError;
        }

        List<string> snapshot;
        lock (linesGate)
        {
            snapshot = [.. lines];
        }

        return new ProcessRunResult(exitCode, snapshot, timedOut);
    }

    private void KillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch (Exception ex)
        {
            _log.Warn("终止 7za 进程时出错。", ex);
        }
    }

    /// <summary>
    /// 从 7za 输出里取出真正加进归档的条目数。取不到返回 null（调用方须有兜底判据）。
    /// 从后往前找：这一行出现在输出末尾，而"Cannot open"之类的告警在它之前。
    /// </summary>
    private static int? ParseEntriesAdded(IReadOnlyList<string> lines)
    {
        for (int i = lines.Count - 1; i >= 0; i--)
        {
            Match match = EntriesAddedPattern.Match(lines[i]);

            if (match.Success
                && int.TryParse(
                    match.Groups["n"].Value,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out int count))
            {
                return count;
            }
        }

        return null;
    }

    /// <summary>
    /// 取出"有几个文件打不开"。没有这行警告就是 0。
    /// 从后往前找并取第一个命中：这是一条汇总行，出现在末尾。
    /// </summary>
    private static int ParseCannotOpenCount(IReadOnlyList<string> lines)
    {
        for (int i = lines.Count - 1; i >= 0; i--)
        {
            Match match = CannotOpenPattern.Match(lines[i]);

            if (match.Success
                && int.TryParse(
                    match.Groups["n"].Value,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out int count))
            {
                return count;
            }
        }

        return 0;
    }

    private static List<string> ExtractWarnings(IEnumerable<string> lines) =>
        lines.Where(l => l.Contains("WARNING", StringComparison.OrdinalIgnoreCase)
                      || l.Contains("Cannot open", StringComparison.OrdinalIgnoreCase)
                      || l.Contains("being used by another process", StringComparison.OrdinalIgnoreCase))
             .Distinct(StringComparer.Ordinal)
             .Take(50)
             .ToList();

    private static string BuildFailureDetail(IReadOnlyList<string> lines)
    {
        List<string> interesting = lines
            .Where(l => l.Contains("ERROR", StringComparison.OrdinalIgnoreCase)
                     || l.Contains("Cannot", StringComparison.OrdinalIgnoreCase)
                     || l.Contains("not supported", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.Ordinal)
            .Take(5)
            .ToList();

        if (interesting.Count == 0)
        {
            interesting = lines.TakeLast(3).ToList();
        }

        return interesting.Count == 0 ? string.Empty : string.Join(" | ", interesting);
    }

    private static long SafeLength(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch
        {
            return 0;
        }
    }

    private void TryDelete(string path)
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
            _log.Warn($"删除临时文件失败：{path}", ex);
        }
    }
}
