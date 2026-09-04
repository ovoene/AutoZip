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
