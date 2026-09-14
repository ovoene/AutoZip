using NewAutoZip.Core.Packing;
using Xunit;
using Xunit.Abstractions;

namespace NewAutoZip.Tests;

/// <summary>
/// 对<b>真实 7za.exe</b> 的集成测试。
///
/// 其余测试全部用"指向一个不存在的 7za 路径"来制造失败，覆盖的是失败分支。
/// 那样测不到的东西恰恰是整条打包链路：参数拼装、<c>@listfile</c> 的 UTF-8 编码、
/// <c>-p</c> 密码、<c>-mhe=on</c>、<c>.part → .7z</c> 的原子晋级、<c>7za t</c> 完整性校验，
/// 以及最关键的<b>退出码 1</b>（源文件被别的进程占用）到底会不会被当成失败。
///
/// 退出码 1 这一条就是回归场景 2：旧版 <c>Zip7Service</c> 是 <c>return p.ExitCode == 0;</c>，
/// 把"包其实是好的、只是有个文件没读到"判成彻底失败，而失败分支又不复位状态 ——
/// 这是"每个扫描周期产出一个完整压缩包直到磁盘打满"最常见的触发点。
/// 光测 <c>SevenZipExitCode.IsArchiveUsable(1) == true</c> 不够，那只是分类函数；
/// 这里要真的锁住一个文件、真的跑一遍 7za，确认归档照样生成并晋级。
/// </summary>
public sealed class SevenZipRealBinaryTests(ITestOutputHelper output) : IDisposable
{
    private readonly ITestOutputHelper _output = output;
    private readonly List<IDisposable> _cleanup = [];

    private static readonly TimeSpan PackTimeout = TimeSpan.FromMinutes(3);

    private const string Password = "Str0ng-Passw0rd!";

    public void Dispose()
    {
        foreach (IDisposable d in _cleanup)
        {
            d.Dispose();
        }
    }

    private TempDir NewDir(string label)
    {
        TempDir dir = new(label);
        _cleanup.Add(dir);
        return dir;
    }

    /// <summary>
    /// 找到随程序携带的 7za.exe。
    ///
    /// 测试程序集自己的输出目录里没有它（tools\7za.exe 是 App 项目的 Content），
    /// 所以从测试 bin 往上找仓库根，再取 src\NewAutoZip.App\tools\7za.exe。
    /// 找不到就让测试失败而不是跳过 —— 这个文件是仓库的一部分，缺了本身就是缺陷。
    /// </summary>
    private static string LocateSevenZip()
    {
        return TestBinaries.SevenZip();
    }

    private SevenZipRunner NewRunner(out RecordingLogger log)
    {
        log = new RecordingLogger();
        return new SevenZipRunner(LocateSevenZip(), log);
    }

    /// <summary>写一个不可压缩的文件，避免压缩快到无法观察。</summary>
    private static string WriteRandom(TempDir dir, string name, int bytes)
    {
        string path = dir.File(name);
        byte[] buffer = new byte[bytes];
        Random.Shared.NextBytes(buffer);
        File.WriteAllBytes(path, buffer);
        return path;
    }

    private void Dump(RecordingLogger log, PackResult result, TempDir zipTemp)
    {
        _output.WriteLine($"---- 结果：{result.Outcome} 退出码={result.ExitCode} 错误={result.Error} ----");
        _output.WriteLine($"警告 {result.Warnings.Count} 条：");

        foreach (string w in result.Warnings)
        {
            _output.WriteLine($"  {w}");
        }

        _output.WriteLine("---- 输出目录 ----");

        foreach (string f in zipTemp.Files())
        {
            _output.WriteLine($"  {Path.GetFileName(f)}  {new FileInfo(f).Length} B");
        }

        _output.WriteLine("---- 日志 ----");
        _output.WriteLine(log.Dump());
    }

    // ==================================================================
    //  基本可用性
    // ==================================================================

    [Fact]
    public async Task 携带的7za真的能跑起来并报出版本()
    {
        SevenZipRunner runner = NewRunner(out RecordingLogger log);

        Assert.True(runner.IsAvailable, $"7za 不存在：{runner.ExePath}");

        string? version = await runner.TryGetVersionAsync(CancellationToken.None);

        Assert.NotNull(version);
        Assert.Contains("7-Zip", version, StringComparison.OrdinalIgnoreCase);
        _output.WriteLine(version);
        _ = log;
    }

    [Fact]
    public async Task 真实压缩产出可解密的归档且不留中间文件()
    {
        TempDir source = NewDir("naz-7z-src");
        TempDir zipTemp = NewDir("naz-7z-out");

        string a = source.WriteFile("a.dat", 4096);
        string b = source.WriteFile("nested/b.dat", 2048);

        string output = zipTemp.File("Backup_test.7z");
        SevenZipRunner runner = NewRunner(out RecordingLogger log);

        PackResult result = await runner.CreateAsync(
            new PackRequest(output, [a, b], Password, CompressionLevel: 1, EncryptFileNames: true, PackTimeout),
            progress: null,
            CancellationToken.None);

        try
        {
            Assert.True(result.Ok, $"压缩失败：{result.Error}");
            Assert.Equal(PackOutcome.Success, result.Outcome);
            Assert.Equal(SevenZipExitCode.Ok, result.ExitCode);

            Assert.True(File.Exists(output), "归档没有晋级成 .7z");
            Assert.Equal(output, result.ArchivePath);
            Assert.True(result.ArchiveBytes > 0);
            Assert.Equal(2, result.FileCount);

            // 中间产物必须清干净 —— 残留的 .part / .list 就是磁盘被慢慢填满的起点。
            Assert.Empty(zipTemp.Files("*.part"));
            Assert.Empty(zipTemp.Files("*.list"));
            Assert.Single(zipTemp.Files("*.7z"));
        }
        catch
        {
            Dump(log, result, zipTemp);
            throw;
        }
    }

    [Fact]
    public async Task 归档确实被密码保护_错误密码校验不通过()
    {
        TempDir source = NewDir("naz-7z-pwsrc");
        TempDir zipTemp = NewDir("naz-7z-pwout");

        string a = source.WriteFile("secret.dat", 4096);
        string output = zipTemp.File("Backup_pw.7z");

        SevenZipRunner runner = NewRunner(out RecordingLogger log);

        PackResult result = await runner.CreateAsync(
            new PackRequest(output, [a], Password, CompressionLevel: 1, EncryptFileNames: true, PackTimeout),
            progress: null,
            CancellationToken.None);

        try
        {
            Assert.True(result.Ok, $"压缩失败：{result.Error}");

            // 用错密码去列内容：加了 -mhe=on，连文件名表都是加密的，必须失败。
            Assert.NotEqual(0, await RunSevenZipAsync(runner.ExePath, ["l", "-p错误密码", output]));

            // 用对密码则应当通过。
            Assert.Equal(0, await RunSevenZipAsync(runner.ExePath, ["t", "-p" + Password, output]));
        }
        catch
        {
            Dump(log, result, zipTemp);
            throw;
        }
    }

    [Fact]
    public async Task 中文与空格文件名走listfile正常压缩()
    {
        // 旧版把所有路径拼在命令行上，既有 32767 字符上限，中文与空格还要手工转义。
        // 新版一律走 @listfile（UTF-8 无 BOM + -scsUTF-8）。
        TempDir source = NewDir("naz-7z-cn");
        TempDir zipTemp = NewDir("naz-7z-cnout");

        List<string> files =
        [
            source.WriteFile("中文文件名.dat", 512),
            source.WriteFile("带 空格 的 名字.dat", 512),
            source.WriteFile("日本語とEmoji表情.dat", 512),
            source.WriteFile("very-long-" + new string('x', 120) + ".dat", 512),
        ];

        string output = zipTemp.File("Backup_cn.7z");
        SevenZipRunner runner = NewRunner(out RecordingLogger log);

        PackResult result = await runner.CreateAsync(
            new PackRequest(output, files, Password, CompressionLevel: 1, EncryptFileNames: true, PackTimeout),
            progress: null,
            CancellationToken.None);

        try
        {
            Assert.True(result.Ok, $"压缩失败：{result.Error}");
            Assert.Equal(files.Count, result.FileCount);

            // 没有"Cannot find"之类的告警，说明清单里的中文路径确实被正确解析了。
            Assert.DoesNotContain(
                result.Warnings,
                w => w.Contains("Cannot find", StringComparison.OrdinalIgnoreCase));

            Assert.True(File.Exists(output));
            Assert.Empty(zipTemp.Files("*.list"));
        }
        catch
        {
            Dump(log, result, zipTemp);
            throw;
        }
    }

    // ==================================================================
    //  回归场景 2：退出码 1 = 有警告但包是好的，必须视为成功
    // ==================================================================

    [Fact]
    public async Task 场景2_源文件被独占锁住时退出码1但归档照常晋级()
    {
        TempDir source = NewDir("naz-7z-lock");
        TempDir zipTemp = NewDir("naz-7z-lockout");

        string readable = source.WriteFile("readable.dat", 4096);
        string locked = source.WriteFile("locked.dat", 4096);

        string output = zipTemp.File("Backup_locked.7z");
        SevenZipRunner runner = NewRunner(out RecordingLogger log);

        PackResult result;

        // FileShare.None：连 -ssw 也打不开，7za 会报 WARNING 并以退出码 1 结束。
        // 这正是"文件正被业务进程独占写入"的真实情形。
        using (FileStream hold = new(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            result = await runner.CreateAsync(
                new PackRequest(
                    output,
                    [readable, locked],
                    Password,
                    CompressionLevel: 1,
                    EncryptFileNames: true,
                    PackTimeout),
                progress: null,
                CancellationToken.None);

            _ = hold.Length;    // 确保锁在压缩全程都还在
        }

        try
        {
            // 核心断言：这不是失败。
            Assert.True(
                result.Ok,
                $"退出码 {result.ExitCode} 被判成了失败 —— 旧版就是这里开始无限重打包的。错误：{result.Error}");

            Assert.Equal(SevenZipExitCode.Warning, result.ExitCode);
            Assert.Equal(PackOutcome.SuccessWithWarnings, result.Outcome);

            // 包真的生成并晋级了。
            Assert.True(File.Exists(output), "退出码 1 时归档没有晋级");
            Assert.True(result.ArchiveBytes > 0);
            Assert.Empty(zipTemp.Files("*.part"));
            Assert.Empty(zipTemp.Files("*.list"));

            // 警告被如实记录下来，而不是悄悄吞掉。
            Assert.NotEmpty(result.Warnings);
            Assert.True(
                log.Contains("退出码 1"),
                $"没有把退出码 1 记进日志。日志：{log.Dump()}");

            // 上报的是"实际压进去几个"，不是"清单里有几个"。
            // 拿清单条数上报等于虚报备份内容 —— 通知说 2 个文件，包里其实只有 1 个。
            Assert.Equal(1, result.FileCount);
            Assert.True(
                log.Contains("跳过了 1 个"),
                $"没有如实说明跳过了几个文件。日志：{log.Dump()}");

            // 而且包是完整可解的 —— 里面应该有那个读得到的文件。
            Assert.Equal(0, await RunSevenZipAsync(runner.ExePath, ["t", "-p" + Password, output]));
        }
        catch
        {
            Dump(log, result, zipTemp);
            throw;
        }
    }

    // ==================================================================
    //  失败与取消：绝不留下 .7z
    // ==================================================================

    [Fact]
    public async Task 清单里的文件全都不存在时判失败且不产生任何归档()
    {
        TempDir source = NewDir("naz-7z-missing");
        TempDir zipTemp = NewDir("naz-7z-missingout");

        string output = zipTemp.File("Backup_missing.7z");
        SevenZipRunner runner = NewRunner(out RecordingLogger log);

        PackResult result = await runner.CreateAsync(
            new PackRequest(
                output,
                [source.File("ghost1.dat"), source.File("ghost2.dat")],
                Password,
                CompressionLevel: 1,
                EncryptFileNames: true,
                PackTimeout),
            progress: null,
            CancellationToken.None);

        try
        {
            Assert.False(result.Ok, "文件全都不存在却报成功了");

            // 最关键的一条：失败绝不留下 .7z。
            Assert.Empty(zipTemp.Files("*.7z"));
            Assert.Empty(zipTemp.Files("*.part"));
            Assert.Empty(zipTemp.Files("*.list"));
            Assert.Null(result.ArchivePath);
            Assert.NotNull(result.Error);
        }
        catch
        {
            Dump(log, result, zipTemp);
            throw;
        }
    }

    [Fact]
    public async Task 全是0字节的文件也要正常打包_不能被判成空归档()
    {
        // 空归档拦截差点在这里误伤：7za 结尾那行 "Files read from disk: 0"
        // 统计的是"读出过内容的文件数"，压一批 0 字节文件时它就是 0 ——
        // 可归档本身完全正常（171 字节、两个条目、能通过 t 校验）。
        // 所以判据必须用 "Add new data to archive: N files"（条目数），不能用前者。
        //
        // 这不是假想：日志切割、占位文件、`type nul >` 生成的标记文件都是 0 字节，
        // 一整批全是 0 字节完全可能发生。误判的后果是删掉合法归档并报失败。
        TempDir source = NewDir("naz-7z-zero");
        TempDir zipTemp = NewDir("naz-7z-zeroout");

        List<string> files =
        [
            source.WriteFile("empty1.dat", 0),
            source.WriteFile("empty2.dat", 0),
        ];

        string output = zipTemp.File("Backup_zero.7z");
        SevenZipRunner runner = NewRunner(out RecordingLogger log);

        PackResult result = await runner.CreateAsync(
            new PackRequest(output, files, Password, CompressionLevel: 1, EncryptFileNames: true, PackTimeout),
            progress: null,
            CancellationToken.None);

        try
        {
            Assert.True(result.Ok, $"0 字节文件的归档被判成失败了：{result.Error}");
            Assert.Equal(PackOutcome.Success, result.Outcome);

            Assert.True(File.Exists(output), "归档被当成空包删掉了");
            Assert.Equal(2, result.FileCount);
            Assert.Equal(0, await RunSevenZipAsync(runner.ExePath, ["t", "-p" + Password, output]));

            // 也不该出现"跳过了 N 个"这种不实的说明。
            Assert.False(log.Contains("跳过了"), $"误报有文件被跳过。日志：{log.Dump()}");
        }
        catch
        {
            Dump(log, result, zipTemp);
            throw;
        }
    }

    [Fact]
    public async Task 取消时杀掉7za进程并清掉半成品()
    {
        TempDir source = NewDir("naz-7z-cancel");
        TempDir zipTemp = NewDir("naz-7z-cancelout");

        // 96 MB 随机数据 + 最高压缩等级：不可能在取消前跑完。
        List<string> files =
        [
            WriteRandom(source, "big1.bin", 32 * 1024 * 1024),
            WriteRandom(source, "big2.bin", 32 * 1024 * 1024),
            WriteRandom(source, "big3.bin", 32 * 1024 * 1024),
        ];

        string output = zipTemp.File("Backup_cancel.7z");
        SevenZipRunner runner = NewRunner(out RecordingLogger log);

        using CancellationTokenSource cts = new();
        cts.CancelAfter(TimeSpan.FromMilliseconds(400));

        PackResult result = await runner.CreateAsync(
            new PackRequest(output, files, Password, CompressionLevel: 9, EncryptFileNames: true, PackTimeout),
            progress: null,
            cts.Token);

        try
        {
            Assert.Equal(PackOutcome.Cancelled, result.Outcome);

            // 取消同样不能留下任何东西。
            Assert.Empty(zipTemp.Files("*.7z"));
            Assert.Empty(zipTemp.Files("*.part"));
            Assert.Empty(zipTemp.Files("*.list"));

            // 进程真的死了：源文件已经可以被独占打开。
            foreach (string f in files)
            {
                using FileStream _ = new(f, FileMode.Open, FileAccess.Read, FileShare.None);
            }
        }
        catch
        {
            Dump(log, result, zipTemp);
            throw;
        }
    }

    [Fact]
    public async Task 超时时终止7za并清掉半成品()
    {
        TempDir source = NewDir("naz-7z-timeout");
        TempDir zipTemp = NewDir("naz-7z-timeoutout");

        List<string> files =
        [
            WriteRandom(source, "big1.bin", 32 * 1024 * 1024),
            WriteRandom(source, "big2.bin", 32 * 1024 * 1024),
        ];

        string output = zipTemp.File("Backup_timeout.7z");
        SevenZipRunner runner = NewRunner(out RecordingLogger log);

        // 旧版 WaitForExit() 无参：卡住就是永远。
        PackResult result = await runner.CreateAsync(
            new PackRequest(
                output,
                files,
                Password,
                CompressionLevel: 9,
                EncryptFileNames: true,
                Timeout: TimeSpan.FromMilliseconds(400)),
            progress: null,
            CancellationToken.None);

        try
        {
            Assert.Equal(PackOutcome.TimedOut, result.Outcome);
            Assert.False(result.Ok);

            Assert.Empty(zipTemp.Files("*.7z"));
            Assert.Empty(zipTemp.Files("*.part"));
            Assert.Empty(zipTemp.Files("*.list"));

            foreach (string f in files)
            {
                using FileStream _ = new(f, FileMode.Open, FileAccess.Read, FileShare.None);
            }
        }
        catch
        {
            Dump(log, result, zipTemp);
            throw;
        }
    }

    [Fact]
    public async Task 进度回调报出真实百分比()
    {
        // -bsp1 把进度写到 stdout。旧版重定向了 stdout 却从不读取，写满管道就死锁；
        // 这里同时验证"读了"和"解析对了"。
        TempDir source = NewDir("naz-7z-prog");
        TempDir zipTemp = NewDir("naz-7z-progout");

        List<string> files = [];
        for (int i = 0; i < 12; i++)
        {
            files.Add(WriteRandom(source, $"chunk{i}.bin", 2 * 1024 * 1024));
        }

        string output = zipTemp.File("Backup_prog.7z");
        SevenZipRunner runner = NewRunner(out RecordingLogger log);

        List<PackProgress> reports = [];
        Progress<PackProgress> progress = new(p =>
        {
            lock (reports)
            {
                reports.Add(p);
            }
        });

        PackResult result = await runner.CreateAsync(
            new PackRequest(output, files, Password, CompressionLevel: 5, EncryptFileNames: true, PackTimeout),
            progress,
            CancellationToken.None);

        try
        {
            Assert.True(result.Ok, $"压缩失败：{result.Error}");

            PackProgress[] snapshot;
            lock (reports)
            {
                snapshot = [.. reports];
            }

            Assert.NotEmpty(snapshot);
            Assert.All(snapshot, p => Assert.InRange(p.Percent, 0, 100));
        }
        catch
        {
            Dump(log, result, zipTemp);
            throw;
        }
    }

    // ==================================================================
    //  归档清单 + 恢复演练：真的解一遍，证明包还能用
    // ==================================================================

    /// <summary>
    /// 写一个<b>内容独一无二</b>的文件。
    ///
    /// <see cref="TempDir.WriteFile"/> 写全零字节：那样两个不同名的文件只要大小相同，
    /// SHA-256 就一模一样，"解出来的内容和源文件一致"这类断言在张冠李戴时照样通过。
    /// 凡是要逐字节比对、要验校验值的地方，一律用这个。
    /// </summary>
    private static string WriteUnique(TempDir dir, string name, string marker, int padTo = 0)
    {
        string content = marker + "|" + Guid.NewGuid().ToString("N");

        if (padTo > content.Length)
        {
            content += new string('填', padTo - content.Length);
        }

        return dir.WriteText(name, content);
    }

    /// <summary>在解压结果里按文件名递归找那个文件。</summary>
    private static string? FindExtracted(string extractRoot, string fileName) =>
        Directory
            .EnumerateFiles(extractRoot, fileName, SearchOption.AllDirectories)
            .FirstOrDefault();

    [Fact]
    public async Task 打包再解开得到逐字节一致的文件()
    {
        // 只会打包不会解包的备份工具，等于从没验证过自己产出的东西还能不能用。
        // 这里走完整条链路：压进去 → 解出来 → 逐字节比。
        TempDir source = NewDir("naz-rt-src");
        TempDir zipTemp = NewDir("naz-rt-out");
        TempDir extract = NewDir("naz-rt-ext");

        // 内容各不相同 —— 全零内容下面那个 SequenceEqual 会永远成立。
        List<string> files =
        [
            WriteUnique(source, "报表.xlsx", "甲", 3000),
            WriteUnique(source, "readme.txt", "乙"),
            WriteUnique(source, "子目录/明细.csv", "丙", 5000),
        ];

        string output = zipTemp.File("Backup_roundtrip.7z");
        SevenZipRunner runner = NewRunner(out RecordingLogger log);

        PackResult result = await runner.CreateAsync(
            new PackRequest(output, files, Password, CompressionLevel: 1, EncryptFileNames: true, PackTimeout),
            progress: null,
            CancellationToken.None);

        try
        {
            Assert.True(result.Ok, $"压缩失败：{result.Error}");

            ExtractResult extracted = await runner.ExtractAsync(
                new ExtractRequest(output, extract.Path, Password, PackTimeout, Overwrite: true),
                null,
                CancellationToken.None);

            Assert.True(extracted.Ok, $"解压失败：{extracted.Error}");
            Assert.False(extracted.WrongPassword);

            foreach (string original in files)
            {
                string name = Path.GetFileName(original);
                string? restored = FindExtracted(extract.Path, name);

                Assert.True(restored is not null, $"解压结果里没有 {name}");

                Assert.True(
                    File.ReadAllBytes(original).SequenceEqual(File.ReadAllBytes(restored!)),
                    $"{name} 解出来的内容和源文件不一致");
            }
        }
        catch
        {
            Dump(log, result, zipTemp);
            throw;
        }
    }

    [Fact]
    public async Task 用错密码解压必须失败且判定为密码错误()
    {
        // 演练的首要目标就是发现"存着的密码打不开包"。
        // 这个判定错了，那种最要命的故障就会被当成普通解压失败糊弄过去。
        TempDir source = NewDir("naz-rt-wpsrc");
        TempDir zipTemp = NewDir("naz-rt-wpout");
        TempDir extract = NewDir("naz-rt-wpext");

        string a = WriteUnique(source, "机密.dat", "内容", 2048);
        string output = zipTemp.File("Backup_wrongpw.7z");

        SevenZipRunner runner = NewRunner(out RecordingLogger log);

        PackResult result = await runner.CreateAsync(
            new PackRequest(output, [a], Password, CompressionLevel: 1, EncryptFileNames: true, PackTimeout),
            progress: null,
            CancellationToken.None);

        try
        {
            Assert.True(result.Ok, $"压缩失败：{result.Error}");

            ExtractResult bad = await runner.ExtractAsync(
                new ExtractRequest(output, extract.Path, "这不是那个密码", PackTimeout, Overwrite: true),
                null,
                CancellationToken.None);

            Assert.False(bad.Ok, "错密码居然解压成功了");
            Assert.True(bad.WrongPassword, $"没能判定为密码错误。错误信息：{bad.Error}");

            // 解错了就不该在目标目录里留下任何源文件。
            Assert.Null(FindExtracted(extract.Path, "机密.dat"));

            // 同一个包用对密码必须解得开 —— 否则上面的失败可能根本不是密码引起的。
            TempDir good = NewDir("naz-rt-wpgood");

            ExtractResult ok = await runner.ExtractAsync(
                new ExtractRequest(output, good.Path, Password, PackTimeout, Overwrite: true),
                null,
                CancellationToken.None);

            Assert.True(ok.Ok, $"对的密码也解不开：{ok.Error}");
            Assert.False(ok.WrongPassword);
            Assert.NotNull(FindExtracted(good.Path, "机密.dat"));
        }
        catch
        {
            Dump(log, result, zipTemp);
            throw;
        }
    }

    [Fact]
    public async Task 开启文件名加密后仍能完整还原出原文件名()
    {
        // -mhe=on 把文件名表也加密了。要确认这只是"外人看不见"，
        // 而不是"文件名在往返中丢了 / 变成乱码" —— 后者等于备份不可用。
        TempDir source = NewDir("naz-rt-mhesrc");
        TempDir zipTemp = NewDir("naz-rt-mheout");
        TempDir extract = NewDir("naz-rt-mheext");

        List<string> files =
        [
            WriteUnique(source, "中文名字.txt", "甲"),
            WriteUnique(source, "带 空格 的 名字.dat", "乙"),
            WriteUnique(source, "日本語とEmoji.dat", "丙"),
        ];

        string output = zipTemp.File("Backup_mhe.7z");
        SevenZipRunner runner = NewRunner(out RecordingLogger log);

        PackResult result = await runner.CreateAsync(
            new PackRequest(output, files, Password, CompressionLevel: 1, EncryptFileNames: true, PackTimeout),
            progress: null,
            CancellationToken.None);

        try
        {
            Assert.True(result.Ok, $"压缩失败：{result.Error}");

            // 先确认文件名表真的被加密了：不给密码连列都列不出来。
            Assert.NotEqual(0, await RunSevenZipAsync(runner.ExePath, ["l", "-p错误密码", output]));

            ExtractResult extracted = await runner.ExtractAsync(
                new ExtractRequest(output, extract.Path, Password, PackTimeout, Overwrite: true),
                null,
                CancellationToken.None);

            Assert.True(extracted.Ok, $"解压失败：{extracted.Error}");

            foreach (string original in files)
            {
                string name = Path.GetFileName(original);
                string? restored = FindExtracted(extract.Path, name);

                Assert.True(restored is not null, $"文件名没能还原：{name}");

                // 名字对得上还不够，内容也得是原来那份。
                Assert.True(
                    File.ReadAllBytes(original).SequenceEqual(File.ReadAllBytes(restored!)),
                    $"{name} 内容对不上");
            }
        }
        catch
        {
            Dump(log, result, zipTemp);
            throw;
        }
    }

    [Fact]
    public async Task ListAsync列得出条目_错密码时标记密码错误()
    {
        TempDir source = NewDir("naz-rt-lssrc");
        TempDir zipTemp = NewDir("naz-rt-lsout");

        List<string> files =
        [
            WriteUnique(source, "甲.dat", "一", 1024),
            WriteUnique(source, "乙.dat", "二", 2048),
            WriteUnique(source, "子目录/丙.dat", "三", 4096),
        ];

        string output = zipTemp.File("Backup_list.7z");
        SevenZipRunner runner = NewRunner(out RecordingLogger log);

        PackResult result = await runner.CreateAsync(
            new PackRequest(output, files, Password, CompressionLevel: 1, EncryptFileNames: true, PackTimeout),
            progress: null,
            CancellationToken.None);

        try
        {
            Assert.True(result.Ok, $"压缩失败：{result.Error}");

            ArchiveListing listing = await runner.ListAsync(
                output, Password, PackTimeout, CancellationToken.None);

            Assert.True(listing.Ok, $"列出失败：{listing.Error}");
            Assert.False(listing.WrongPassword);
            Assert.False(listing.Truncated);

            // 三个源文件都在（目录条目不算）。
            Assert.Equal(3, listing.FileCount);

            foreach (string original in files)
            {
                string name = Path.GetFileName(original);

                Assert.Contains(
                    listing.Entries,
                    e => !e.IsDirectory
                         && Path.GetFileName(e.Path).Equals(name, StringComparison.OrdinalIgnoreCase));
            }

            // 大小要如实报出来，而不是全 0。
            Assert.Equal(
                files.Sum(f => new FileInfo(f).Length),
                listing.Entries.Where(e => !e.IsDirectory).Sum(e => e.Size));

            Assert.True(listing.TotalBytes > 0);

            // 错密码：-mhe=on 下连文件名表都读不了，必须明确标出是密码问题。
            ArchiveListing bad = await runner.ListAsync(
                output, "这不是那个密码", PackTimeout, CancellationToken.None);

            Assert.False(bad.Ok, "错密码居然列出成功了");
            Assert.True(bad.WrongPassword, $"没能判定为密码错误。错误信息：{bad.Error}");
            Assert.Empty(bad.Entries);
        }
        catch
        {
            Dump(log, result, zipTemp);
            throw;
        }
    }

    [Fact]
    public async Task 恢复演练全过_并且自己把演练目录清干净()
    {
        // 演练要解出一份和源文件等大的副本，是这个程序单次占用磁盘最多的操作。
        // 它的 finally 必须无条件清理 —— 失败分支不清理正是这个项目最惨痛的教训。
        TempDir source = NewDir("naz-drill-src");
        TempDir zipTemp = NewDir("naz-drill-out");

        List<string> files =
        [
            WriteUnique(source, "甲.dat", "一", 4096),
            WriteUnique(source, "乙.dat", "二", 4096),
            WriteUnique(source, "子目录/丙.dat", "三", 4096),
        ];

        string output = zipTemp.File("Backup_drill.7z");
        SevenZipRunner runner = NewRunner(out RecordingLogger log);

        // 包内清单随包一起压进去，演练才有得比对。
        ArchiveManifestDocument doc = await ArchiveManifest.BuildAsync(
            files, source.Path, "Backup_drill.7z",
            new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero),
            new NewAutoZip.Core.Configuration.AppSettings(), log, CancellationToken.None);

        string manifestPath = Path.Combine(zipTemp.Path, ArchiveManifest.EntryName);
        ArchiveManifest.WriteInnerManifest(manifestPath, doc);

        PackResult result = await runner.CreateAsync(
            new PackRequest(output, files, Password, CompressionLevel: 1, EncryptFileNames: true, PackTimeout)
            {
                ExtraFiles = [manifestPath],
            },
            progress: null,
            CancellationToken.None);

        try
        {
            Assert.True(result.Ok, $"压缩失败：{result.Error}");

            RestoreDrillService drill = new(runner, log);

            DrillResult outcome = await drill.RunAsync(
                output, Password, zipTemp.Path, PackTimeout, minFreeBytes: 0, CancellationToken.None);

            Assert.True(outcome.Ok, $"演练没通过：{outcome.Outcome} / {outcome.Message}");
            Assert.Equal(DrillOutcome.Passed, outcome.Outcome);
            Assert.False(outcome.IsProblem);

            // 真的逐条比对过，而不是解开就算数。
            Assert.Equal(3, outcome.Checked);
            Assert.Equal(0, outcome.Mismatched);
            Assert.Equal(0, outcome.Missing);

            // 演练自己的 finally 必须把 __drill_* 收干净 —— 不能等清扫来兜底。
            Assert.Empty(Directory.GetDirectories(
                zipTemp.Path, RestoreDrillService.DrillDirectoryPrefix + "*"));

            // 正经归档一根汗毛都不能动。
            Assert.True(File.Exists(output), "演练把归档本身弄没了");
        }
        catch
        {
            Dump(log, result, zipTemp);
            throw;
        }
    }

    [Fact]
    public async Task 演练用错密码判定为密码错误_目录照样清干净()
    {
        // 最要命的那种故障：settings.json 里的密码已经打不开三天前的包了。
        // 演练存在的首要理由就是在"真要恢复那天"之前把它抓出来。
        TempDir source = NewDir("naz-drill-wpsrc");
        TempDir zipTemp = NewDir("naz-drill-wpout");

        string a = WriteUnique(source, "甲.dat", "一", 4096);
        string output = zipTemp.File("Backup_drillwp.7z");

        SevenZipRunner runner = NewRunner(out RecordingLogger log);

        PackResult result = await runner.CreateAsync(
            new PackRequest(output, [a], Password, CompressionLevel: 1, EncryptFileNames: true, PackTimeout),
            progress: null,
            CancellationToken.None);

        try
        {
            Assert.True(result.Ok, $"压缩失败：{result.Error}");

            RestoreDrillService drill = new(runner, log);

            DrillResult outcome = await drill.RunAsync(
                output, "这不是那个密码", zipTemp.Path, PackTimeout, minFreeBytes: 0, CancellationToken.None);

            Assert.Equal(DrillOutcome.WrongPassword, outcome.Outcome);
            Assert.True(outcome.IsProblem, "密码打不开包却没被算作问题");
            Assert.False(outcome.Ok);

            // 失败路径同样要清干净 —— 这正是"失败分支不清理导致磁盘填满"的翻版。
            Assert.Empty(Directory.GetDirectories(
                zipTemp.Path, RestoreDrillService.DrillDirectoryPrefix + "*"));

            Assert.True(File.Exists(output), "演练失败时把归档删了");
        }
        catch
        {
            Dump(log, result, zipTemp);
            throw;
        }
    }

    [Fact]
    public async Task 演练遇到损坏的包判失败_目录照样清干净()
    {
        TempDir zipTemp = NewDir("naz-drill-badout");

        // 一个彻头彻尾不是 7z 的文件。
        string output = zipTemp.WriteText("Backup_corrupt.7z", "这根本不是一个压缩包，只是一段文字。");

        SevenZipRunner runner = NewRunner(out RecordingLogger log);
        RestoreDrillService drill = new(runner, log);

        DrillResult outcome = await drill.RunAsync(
            output, Password, zipTemp.Path, PackTimeout, minFreeBytes: 0, CancellationToken.None);

        Assert.Equal(DrillOutcome.Failed, outcome.Outcome);
        Assert.True(outcome.IsProblem);

        Assert.Empty(Directory.GetDirectories(
            zipTemp.Path, RestoreDrillService.DrillDirectoryPrefix + "*"));
    }

    [Fact]
    public async Task 演练云端目录里的包_复制进来验完再清干净()
    {
        // 云盘里的包不能就地解压（那是人家的同步目录），要先复制到 ZipTemp。
        // 复制品必须落在 __drill_* 里面：放 ZipTemp 根部会被 ListArchives 当成正经归档，
        // 配额可能在演练跑到一半时把它删掉。
        TempDir source = NewDir("naz-drill-cloudsrc");
        TempDir zipTemp = NewDir("naz-drill-cloudwork");
        TempDir cloud = NewDir("naz-drill-cloud");

        List<string> files =
        [
            WriteUnique(source, "甲.dat", "一", 4096),
            WriteUnique(source, "乙.dat", "二", 4096),
        ];

        string output = cloud.File("Backup_cloud.7z");
        SevenZipRunner runner = NewRunner(out RecordingLogger log);

        PackResult result = await runner.CreateAsync(
            new PackRequest(output, files, Password, CompressionLevel: 1, EncryptFileNames: true, PackTimeout),
            progress: null,
            CancellationToken.None);

        try
        {
            Assert.True(result.Ok, $"压缩失败：{result.Error}");

            RestoreDrillService drill = new(runner, log);

            DrillResult outcome = await drill.RunAsync(
                output, Password, zipTemp.Path, PackTimeout, minFreeBytes: 0, CancellationToken.None);

            Assert.True(outcome.Ok, $"演练没通过：{outcome.Outcome} / {outcome.Message}");

            // 工作目录里不能留下任何东西 —— 尤其不能留下那份复制进来的包。
            Assert.Empty(Directory.GetDirectories(
                zipTemp.Path, RestoreDrillService.DrillDirectoryPrefix + "*"));
            Assert.Empty(zipTemp.Files("*.7z"));

            // 云盘里那个原包一动不动。
            Assert.True(File.Exists(output), "演练动了云盘里的包");
        }
        catch
        {
            Dump(log, result, zipTemp);
            throw;
        }
    }

    /// <summary>直接跑一次 7za 拿退出码，用来独立验证归档（不经过被测代码）。</summary>
    private static async Task<int> RunSevenZipAsync(string exe, IReadOnlyList<string> args)
    {
        System.Diagnostics.ProcessStartInfo info = new()
        {
            FileName = exe,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (string a in args)
        {
            info.ArgumentList.Add(a);
        }

        using System.Diagnostics.Process process = new() { StartInfo = info };
        process.Start();

        // 必须读掉输出，否则大量输出会写满管道把子进程卡死 —— 旧版正是栽在这里。
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync();
        await Task.WhenAll(stdout, stderr);

        return process.ExitCode;
    }
}
