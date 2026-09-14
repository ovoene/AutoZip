using System.Security.Cryptography;
using System.Text;

using NewAutoZip.Core.Configuration;
using NewAutoZip.Core.Packing;

using Xunit;

namespace NewAutoZip.Tests;

/// <summary>
/// 归档清单的两层结构。
///
/// 这里最要紧的一条不是"字段有没有写对"，而是<b>旁挂清单里绝不能出现源文件名</b>——
/// 见 <see cref="旁挂清单里不得出现任何源文件名或路径"/>。那是一条安全属性：
/// <c>-mhe=on</c> 的全部意义就是藏住归档里的文件名表，
/// 而旁挂清单是<b>明文</b>、跟着归档一起进云盘的。两者一撞，加密就等于白开。
/// </summary>
public class ArchiveManifestTests
{
    private static readonly DateTimeOffset Origin = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// 造一批<b>名字很独特</b>的源文件。
    ///
    /// 名字刻意取成不可能碰巧出现在 JSON 里的样子（"绝不能出现在明文里"这类字样）：
    /// 这样"搜不到"就真的等于"没泄漏"，而不是"恰好和某个字段名撞了所以搜不出来"。
    ///
    /// 内容也刻意各不相同 —— 全零内容会让不同文件的 SHA-256 完全一样，
    /// 那样后面关于校验值的断言就成了摆设。
    /// </summary>
    private static (List<string> Files, List<string> SecretNames) BuildSecretFiles(TempDir dir)
    {
        List<string> files =
        [
            dir.WriteText("机密报表_绝不能出现在明文里.xlsx", "财务数据 A"),
            dir.WriteText("客户名单_ULTRA_SECRET_FILENAME.csv", "客户数据 B"),
            dir.WriteText(Path.Combine("子目录", "并购意向书_DO_NOT_LEAK_THIS.docx"), "并购数据 C"),
        ];

        // 断言时要找的"不该出现的字符串"：整个文件名、以及去掉扩展名的主干。
        List<string> secrets =
        [
            "机密报表_绝不能出现在明文里.xlsx",
            "客户名单_ULTRA_SECRET_FILENAME.csv",
            "并购意向书_DO_NOT_LEAK_THIS.docx",
            "ULTRA_SECRET_FILENAME",
            "DO_NOT_LEAK_THIS",
            "机密报表",
            "客户名单",
            "并购意向书",
        ];

        return (files, secrets);
    }

    private static async Task<(ArchiveManifestDocument Doc, RecordingLogger Log)> BuildDocAsync(
        IReadOnlyList<string> files,
        string root,
        string archiveName = "Backup_20260901_120000.7z")
    {
        RecordingLogger log = new();

        ArchiveManifestDocument doc = await ArchiveManifest.BuildAsync(
            files,
            root,
            archiveName,
            Origin,
            new AppSettings(),
            log,
            CancellationToken.None);

        return (doc, log);
    }

    /// <summary>
    /// 断言某段文本里<b>找不到</b>这个片段 —— 原文和 <c>\uXXXX</c> 转义形式都要找不到。
    ///
    /// 只搜原文是不够的：<see cref="AtomicJsonFile.Options"/> 目前配的是
    /// UnsafeRelaxedJsonEscaping（中文不转义），但换一个 Encoder 之后中文就会变成
    /// <c>\u673A\u5BC6...</c>。那时"搜不到原文"照样成立，而文件名其实已经泄漏出去了 ——
    /// 一条会随配置变化而静默失效的安全断言比没有断言更糟。
    /// </summary>
    private static void AssertAbsent(string text, string fragment, string because)
    {
        Assert.False(
            text.Contains(fragment, StringComparison.OrdinalIgnoreCase),
            $"旁挂清单里出现了「{fragment}」。{because}");

        StringBuilder escaped = new();

        foreach (char c in fragment)
        {
            if (c > 127)
            {
                escaped.Append("\\u").Append(((int)c).ToString("x4"));
            }
            else
            {
                escaped.Append(c);
            }
        }

        Assert.False(
            text.Contains(escaped.ToString(), StringComparison.OrdinalIgnoreCase),
            $"旁挂清单里出现了「{fragment}」的 \\uXXXX 转义形式。{because}");
    }

    // ==================================================================
    //  安全属性：旁挂清单是明文、要进云盘，一个源文件名都不能有
    // ==================================================================

    [Fact]
    public async Task 旁挂清单里不得出现任何源文件名或路径()
    {
        // -mhe=on 把归档里的文件名表也加密了，拿到包的人连"里面有什么"都看不出来。
        // 旁挂清单是明文、文件名是 <归档名>.manifest.json、跟着归档一起进云盘 ——
        // 往里写文件名等于把那道加密保护的东西原样交出去：攻击者不必解包，读个 JSON 就够了。
        using TempDir source = new("naz-mf-secret");
        using TempDir zipTemp = new("naz-mf-out");

        (List<string> files, List<string> secrets) = BuildSecretFiles(source);

        (ArchiveManifestDocument doc, _) = await BuildDocAsync(files, source.Path);

        // 先确认这批名字真的进了<b>包内</b>清单 —— 否则下面"搜不到"可能只是因为
        // 压根没人往清单里写过东西，那条断言就成了永远为真的空壳。
        string innerJson = System.Text.Json.JsonSerializer.Serialize(doc, AtomicJsonFile.Options);

        Assert.Contains("机密报表_绝不能出现在明文里.xlsx", innerJson, StringComparison.Ordinal);
        Assert.Contains("DO_NOT_LEAK_THIS", innerJson, StringComparison.Ordinal);

        // 造一个假归档让 WriteSidecarAsync 能算出体积与哈希。
        string archivePath = zipTemp.WriteText("Backup_20260901_120000.7z", "假装这是个压缩包");

        RecordingLogger log = new();
        ArchiveManifestSidecar? sidecar = await ArchiveManifest.WriteSidecarAsync(
            archivePath, doc, log, CancellationToken.None);

        Assert.NotNull(sidecar);

        // 读<b>磁盘上那份</b>，而不是重新序列化一遍内存对象：进云盘的就是这个文件。
        string sidecarPath = ArchiveManifest.SidecarPathFor(archivePath);
        Assert.True(File.Exists(sidecarPath), "旁挂清单没有落盘");

        string sidecarJson = await File.ReadAllTextAsync(sidecarPath);

        foreach (string secret in secrets)
        {
            AssertAbsent(sidecarJson, secret,
                "旁挂清单是明文且随包进云盘，出现源文件名等于让 -mhe=on 白加密");
        }

        // 目录名同样不能出现 —— 路径本身也是情报（项目代号、客户名常在目录名里）。
        AssertAbsent(sidecarJson, "子目录", "子目录名也是源路径的一部分");
        AssertAbsent(sidecarJson, source.Path, "监控目录的绝对路径不该进旁挂清单");

        // 该有的校验元数据得在，否则这层清单就没用了。
        Assert.Contains("Backup_20260901_120000.7z", sidecarJson, StringComparison.Ordinal);
        Assert.Equal(3, sidecar.FileCount);
        Assert.Equal(64, sidecar.ArchiveSha256.Length);
    }

    [Fact]
    public async Task 包内清单反过来必须留下文件名否则没法逐条核对()
    {
        // 与上一条成对：包内那层是随 AES-256 加密的，想读它得先有密码 ——
        // 而有密码的人本来就能列出整个包。所以这一层<b>应该</b>有文件名，
        // 没有反而说明演练那边永远比不出"少了哪个文件"。
        using TempDir source = new("naz-mf-inner");

        (List<string> files, _) = BuildSecretFiles(source);
        (ArchiveManifestDocument doc, _) = await BuildDocAsync(files, source.Path);

        Assert.Equal(3, doc.Entries.Count);

        // 路径是相对监控目录算的，不是绝对路径。
        Assert.Contains(doc.Entries, e => e.Path == "机密报表_绝不能出现在明文里.xlsx");
        Assert.Contains(doc.Entries, e =>
            e.Path == Path.Combine("子目录", "并购意向书_DO_NOT_LEAK_THIS.docx"));
        Assert.DoesNotContain(doc.Entries, e => Path.IsPathRooted(e.Path));

        // 每条都得有能用的校验值，且内容不同的文件哈希必须不同。
        Assert.All(doc.Entries, e => Assert.True(e.Verifiable, $"{e.Path} 没有校验值"));
        Assert.Equal(3, doc.Entries.Select(e => e.Sha256!).Distinct(StringComparer.Ordinal).Count());
    }

    // ==================================================================
    //  SHA-256
    // ==================================================================

    [Fact]
    public async Task ComputeSha256Async与独立算出来的值一致()
    {
        using TempDir dir = new("naz-mf-sha");

        // "hello" 的 SHA-256 是一个众所皆知的常量 —— 写死它可以同时验出
        // "算错了"和"算的是别的东西"（例如把路径也混进了哈希）。
        const string known = "2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824";

        string file = dir.WriteText("hello.txt", "hello");

        Assert.Equal(known, await ArchiveManifest.ComputeSha256Async(file, CancellationToken.None));

        // 再拿一份随机内容与 .NET 自己的实现对照，确认不是碰巧对上了那一个常量。
        byte[] payload = new byte[100_000];
        Random.Shared.NextBytes(payload);

        string big = dir.File("big.bin");
        await File.WriteAllBytesAsync(big, payload);

        string expected = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();

        Assert.Equal(expected, await ArchiveManifest.ComputeSha256Async(big, CancellationToken.None));
    }

    [Fact]
    public async Task ComputeSha256Async对正被写入的文件也能读()
    {
        // 源文件很可能正被业务进程持有。用 FileShare.None 打开就会把这一步搞崩 ——
        // 旧版对被监控文件就是这么做的。
        using TempDir dir = new("naz-mf-share");

        string path = dir.WriteText("正在被写.log", "第一行");

        await using (FileStream holder = new(
            path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
        {
            string sha = await ArchiveManifest.ComputeSha256Async(path, CancellationToken.None);

            Assert.Equal(64, sha.Length);
            _ = holder.Length;
        }
    }

    // ==================================================================
    //  往返序列化
    // ==================================================================

    [Fact]
    public void 旁挂清单能往返序列化且字段不丢()
    {
        using TempDir dir = new("naz-mf-roundtrip");

        string archivePath = dir.File("Backup_20260901_120000.7z");

        ArchiveManifestSidecar original = new()
        {
            SchemaVersion = ArchiveManifest.CurrentSchemaVersion,
            ArchiveFileName = "Backup_20260901_120000.7z",
            ArchiveBytes = 123_456_789,
            ArchiveSha256 = new string('a', 64),
            CreatedLocal = Origin,
            FileCount = 42,
            SourceBytes = 987_654_321,
            CompressionLevel = 5,
            EncryptFileNames = true,
            HasInnerManifest = true,
            Machine = "测试机器",
            AppVersion = "1.2.3",
        };

        ArchiveManifest.WriteSidecarFile(ArchiveManifest.SidecarPathFor(archivePath), original);

        // 写清单用的临时后缀必须是 .writing 而不是 .tmp ——
        // ZipTempManager.SweepIntermediates 会删 ZipTemp 下所有 *.tmp，
        // 而清单恰恰写在 ZipTemp 里，撞上就是清单被自己人删掉。
        Assert.Empty(dir.Files("*.writing"));
        Assert.Empty(dir.Files("*.tmp"));

        ArchiveManifestSidecar? read = ArchiveManifest.TryReadSidecar(archivePath, out string? error);

        Assert.Null(error);
        Assert.NotNull(read);

        Assert.Equal(original.SchemaVersion, read.SchemaVersion);
        Assert.Equal(original.ArchiveFileName, read.ArchiveFileName);
        Assert.Equal(original.ArchiveBytes, read.ArchiveBytes);
        Assert.Equal(original.ArchiveSha256, read.ArchiveSha256);
        Assert.Equal(original.CreatedLocal, read.CreatedLocal);
        Assert.Equal(original.FileCount, read.FileCount);
        Assert.Equal(original.SourceBytes, read.SourceBytes);
        Assert.Equal(original.CompressionLevel, read.CompressionLevel);
        Assert.Equal(original.EncryptFileNames, read.EncryptFileNames);
        Assert.Equal(original.HasInnerManifest, read.HasInnerManifest);
        Assert.Equal(original.Machine, read.Machine);
        Assert.Equal(original.AppVersion, read.AppVersion);
    }

    [Fact]
    public async Task 包内清单能往返序列化且逐条字段不丢()
    {
        using TempDir source = new("naz-mf-innerrt");
        using TempDir target = new("naz-mf-innerrt-out");

        (List<string> files, _) = BuildSecretFiles(source);
        (ArchiveManifestDocument doc, _) = await BuildDocAsync(files, source.Path);

        string path = Path.Combine(target.Path, ArchiveManifest.EntryName);
        ArchiveManifest.WriteInnerManifest(path, doc);

        // 文件名必须<b>正好</b>是那个约定名 —— 演练解开后是按名字找它的。
        Assert.True(File.Exists(path));
        Assert.Equal(ArchiveManifest.EntryName, Path.GetFileName(path));

        ArchiveManifestDocument? read = ArchiveManifest.TryFindInnerManifest(target.Path, out string? error);

        Assert.Null(error);
        Assert.NotNull(read);

        Assert.Equal(doc.SchemaVersion, read.SchemaVersion);
        Assert.Equal(doc.ArchiveFileName, read.ArchiveFileName);
        Assert.Equal(doc.CreatedLocal, read.CreatedLocal);
        Assert.Equal(doc.MonitorRoot, read.MonitorRoot);
        Assert.Equal(doc.FileCount, read.FileCount);
        Assert.Equal(doc.TotalBytes, read.TotalBytes);
        Assert.Equal(doc.CompressionLevel, read.CompressionLevel);
        Assert.Equal(doc.EncryptFileNames, read.EncryptFileNames);
        Assert.Equal(doc.Entries.Count, read.Entries.Count);

        for (int i = 0; i < doc.Entries.Count; i++)
        {
            Assert.Equal(doc.Entries[i].Path, read.Entries[i].Path);
            Assert.Equal(doc.Entries[i].Bytes, read.Entries[i].Bytes);
            Assert.Equal(doc.Entries[i].Sha256, read.Entries[i].Sha256);
            Assert.Equal(doc.Entries[i].ModifiedUtc, read.Entries[i].ModifiedUtc);
            Assert.Equal(doc.Entries[i].Note, read.Entries[i].Note);
        }
    }

    // ==================================================================
    //  BuildAsync 的容错：一个文件读不出哈希不该让整轮备份失败
    // ==================================================================

    [Fact]
    public async Task BuildAsync遇到被独占的文件不抛异常而是记下Note()
    {
        // 那个文件本来也会被 7za 以退出码 1 跳过 —— 包照样是好的。
        // 为它抛异常等于让一个被记事本打开的文件搞掉整轮备份。
        using TempDir source = new("naz-mf-locked");

        string ok = source.WriteText("读得到.txt", "正常内容");
        string locked = source.WriteText("被独占.txt", "打不开的内容");

        RecordingLogger log = new();
        ArchiveManifestDocument doc;

        // FileShare.None：连 ComputeSha256Async 的 ReadWrite|Delete 也打不开。
        using (FileStream hold = new(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            doc = await ArchiveManifest.BuildAsync(
                [ok, locked],
                source.Path,
                "Backup_locked.7z",
                Origin,
                new AppSettings(),
                log,
                CancellationToken.None);

            _ = hold.Length;
        }

        Assert.Equal(2, doc.Entries.Count);

        ArchiveManifestEntry okEntry = doc.Entries.Single(e => e.Path == "读得到.txt");
        Assert.NotNull(okEntry.Sha256);
        Assert.True(okEntry.Verifiable);
        Assert.Null(okEntry.Note);

        ArchiveManifestEntry lockedEntry = doc.Entries.Single(e => e.Path == "被独占.txt");
        Assert.Null(lockedEntry.Sha256);
        Assert.False(lockedEntry.Verifiable);
        Assert.False(string.IsNullOrWhiteSpace(lockedEntry.Note));

        // 日志得说清楚有几个没读到，而不是悄悄跳过。
        Assert.True(log.Contains("1 个未能读取"), $"没有如实记录读不到的文件数。日志：{log.Dump()}");
    }

    [Fact]
    public async Task BuildAsync遇到已消失的文件记Note且不计入总字节()
    {
        using TempDir source = new("naz-mf-ghost");

        string alive = source.WriteText("还在.txt", "12345");
        string ghost = source.File("打包前就没了.txt");

        RecordingLogger log = new();

        ArchiveManifestDocument doc = await ArchiveManifest.BuildAsync(
            [alive, ghost],
            source.Path,
            "Backup_ghost.7z",
            Origin,
            new AppSettings(),
            log,
            CancellationToken.None);

        ArchiveManifestEntry ghostEntry = doc.Entries.Single(e => e.Path == "打包前就没了.txt");

        Assert.Null(ghostEntry.Sha256);
        Assert.Equal(0, ghostEntry.Bytes);
        Assert.False(string.IsNullOrWhiteSpace(ghostEntry.Note));
        Assert.Null(ghostEntry.ModifiedUtc);

        // 总字节只算真的读到的那些，否则清单会虚报源文件体积。
        Assert.Equal(new FileInfo(alive).Length, doc.TotalBytes);
        Assert.Equal(2, doc.FileCount);
    }

    // ==================================================================
    //  TryFindInnerManifest：必须靠"搜文件名"而不是拼路径
    // ==================================================================

    [Fact]
    public void TryFindInnerManifest能在多层子目录里找到清单()
    {
        // 7-Zip 存条目时会剥掉盘符与公共前缀，清单的落点取决于这一批文件的公共前缀 ——
        // 同一个包换一批源文件，层级就不一样。所以只能搜文件名，写死路径必然有对不上的那天。
        using TempDir root = new("naz-mf-find");

        string deep = Path.Combine(root.Path, "ZipTemp", "a", "b", "c");
        Directory.CreateDirectory(deep);

        ArchiveManifestDocument doc = new()
        {
            ArchiveFileName = "Backup_deep.7z",
            FileCount = 7,
            TotalBytes = 4096,
            CreatedLocal = Origin,
        };

        ArchiveManifest.WriteInnerManifest(Path.Combine(deep, ArchiveManifest.EntryName), doc);

        // 放几个同目录的干扰文件，确认不是"随便捡到第一个 json"。
        File.WriteAllText(Path.Combine(deep, "别的东西.json"), "{}");
        File.WriteAllText(Path.Combine(root.Path, "ZipTemp", "也不是清单.json"), "{}");

        ArchiveManifestDocument? found = ArchiveManifest.TryFindInnerManifest(root.Path, out string? error);

        Assert.Null(error);
        Assert.NotNull(found);
        Assert.Equal("Backup_deep.7z", found.ArchiveFileName);
        Assert.Equal(7, found.FileCount);
    }

    [Fact]
    public void TryFindInnerManifest找不到时给出原因而不是抛异常()
    {
        using TempDir root = new("naz-mf-nofind");

        root.WriteText("只有别的文件.txt", "内容");

        ArchiveManifestDocument? found = ArchiveManifest.TryFindInnerManifest(root.Path, out string? error);

        Assert.Null(found);
        Assert.False(string.IsNullOrWhiteSpace(error));

        // 目录压根不存在时同样不抛 —— 演练那边靠这个返回值决定"能不能逐条核对"。
        ArchiveManifestDocument? missing = ArchiveManifest.TryFindInnerManifest(
            Path.Combine(root.Path, "这个目录不存在"), out string? missingError);

        Assert.Null(missing);
        Assert.False(string.IsNullOrWhiteSpace(missingError));
    }

    // ==================================================================
    //  路径推导：各处不许自己拼字符串
    // ==================================================================

    [Fact]
    public void 旁挂清单路径接在完整归档名之后()
    {
        // 是 Backup_x.7z.manifest.json，不是 Backup_x.manifest.json ——
        // 后者会让两个只差扩展名的归档共用一份清单。
        string path = ArchiveManifest.SidecarPathFor(@"D:\ZipTemp\Backup_1.7z");

        Assert.Equal(@"D:\ZipTemp\Backup_1.7z.manifest.json", path);
        Assert.True(ArchiveManifest.IsSidecar(path));
        Assert.False(ArchiveManifest.IsSidecar(@"D:\ZipTemp\Backup_1.7z"));

        // 大小写不敏感：Windows 上文件名大小写不区分，漏掉这一点会让孤儿清单清不掉。
        Assert.True(ArchiveManifest.IsSidecar(@"D:\ZipTemp\Backup_1.7z.MANIFEST.JSON"));
    }

    [Fact]
    public void TryReadSidecar读不到时返回空而不抛()
    {
        using TempDir dir = new("naz-mf-read");

        // 压根没有清单。
        Assert.Null(ArchiveManifest.TryReadSidecar(dir.File("不存在.7z"), out string? missingError));
        Assert.Null(missingError);      // 文件不存在不算错误，只是没有

        // 清单存在但内容是坏的。
        string archivePath = dir.File("坏清单.7z");
        File.WriteAllText(ArchiveManifest.SidecarPathFor(archivePath), "{ 这不是合法 JSON ");

        Assert.Null(ArchiveManifest.TryReadSidecar(archivePath, out string? brokenError));
        Assert.False(string.IsNullOrWhiteSpace(brokenError));
    }
}
