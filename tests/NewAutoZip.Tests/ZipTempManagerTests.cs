using Microsoft.Extensions.Time.Testing;
using NewAutoZip.Core.Packing;
using NewAutoZip.Core.Storage;
using Xunit;

namespace NewAutoZip.Tests;

public class ZipTempManagerTests
{
    private static readonly DateTimeOffset Origin = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    private const long NoByteCap = long.MaxValue;
    private const int NoCountCap = int.MaxValue;
    private const int NoAgeCap = 365;

    private static (ZipTempManager Mgr, FakeTimeProvider Time, RecordingLogger Log) Build(TempDir dir)
    {
        FakeTimeProvider time = new(Origin);
        RecordingLogger log = new();
        ZipTempManager mgr = new(log, time);
        mgr.Configure(dir.Path);
        mgr.EnsureDirectory();
        return (mgr, time, log);
    }

    /// <summary>造一个归档，并把它的时间戳设成"若干天前"。</summary>
    private static string MakeArchive(TempDir dir, string name, int bytes, int ageDays = 0)
    {
        string path = dir.WriteFile(name, bytes);
        DateTime stamp = Origin.UtcDateTime.AddDays(-ageDays);
        File.SetCreationTimeUtc(path, stamp);
        File.SetLastWriteTimeUtc(path, stamp);
        return path;
    }

    [Fact]
    public void 数量配额生效_保留最新的N个()
    {
        // 回归场景 4：把最大归档数调成 3，连打 5 次 → 只剩最新 3 个。
        using TempDir dir = new();
        (ZipTempManager mgr, _, _) = Build(dir);

        for (int i = 0; i < 5; i++)
        {
            MakeArchive(dir, $"Backup_{i}.7z", 1024, ageDays: 5 - i);   // i 越大越新
        }

        QuotaReport report = mgr.Enforce(NoAgeCap, maxCount: 3, NoByteCap);

        Assert.Equal(2, report.DeletedCount);
        Assert.Equal(3, report.RemainingCount);

        string[] left = dir.Files("*.7z").Select(Path.GetFileName).OfType<string>().Order().ToArray();
        Assert.Equal(["Backup_2.7z", "Backup_3.7z", "Backup_4.7z"], left);
    }

    [Fact]
    public void 容量配额生效_按最旧优先淘汰()
    {
        using TempDir dir = new();
        (ZipTempManager mgr, _, _) = Build(dir);

        for (int i = 0; i < 5; i++)
        {
            MakeArchive(dir, $"Backup_{i}.7z", 40 * 1024 * 1024, ageDays: 5 - i);   // 每个 40 MB
        }

        // 上限 100 MB（下限保护是 1 MB，所以这个值有效）→ 只能留下 2 个。
        QuotaReport report = mgr.Enforce(NoAgeCap, NoCountCap, maxTotalBytes: 100L * 1024 * 1024);

        Assert.True(report.RemainingBytes <= 100L * 1024 * 1024,
            $"配额执行后仍有 {report.RemainingBytes} 字节，超过 100 MB 上限");
        Assert.True(report.DeletedCount >= 3);
        Assert.DoesNotContain("Backup_0.7z", dir.Files("*.7z").Select(Path.GetFileName).OfType<string>());
    }

    [Fact]
    public void 天数配额生效()
    {
        using TempDir dir = new();
        (ZipTempManager mgr, _, _) = Build(dir);

        MakeArchive(dir, "old.7z", 1024, ageDays: 10);
        MakeArchive(dir, "fresh.7z", 1024, ageDays: 1);

        QuotaReport report = mgr.Enforce(keepDays: 3, NoCountCap, NoByteCap);

        Assert.Equal(1, report.DeletedCount);
        Assert.Single(dir.Files("*.7z"));
        Assert.EndsWith("fresh.7z", dir.Files("*.7z")[0]);
    }

    [Fact]
    public void 保留天数为0时不等于关闭清理()
    {
        // 旧版 ZipTempKeepDays=0 → CleanupZipTemp 直接 return，等于彻底关闭清理，
        // 而 UI 上的 NumericUpDown 默认最小值就是 0。这里必须被抬到至少 1 天。
        using TempDir dir = new();
        (ZipTempManager mgr, _, _) = Build(dir);

        MakeArchive(dir, "veryold.7z", 1024, ageDays: 30);

        QuotaReport report = mgr.Enforce(keepDays: 0, NoCountCap, NoByteCap);

        Assert.Equal(1, report.DeletedCount);
        Assert.Empty(dir.Files("*.7z"));
    }

    [Fact]
    public void 待上传的归档绝不被配额删除()
    {
        using TempDir dir = new();
        (ZipTempManager mgr, _, _) = Build(dir);

        string locked = MakeArchive(dir, "pending.7z", 1024, ageDays: 99);   // 最旧的
        string b = MakeArchive(dir, "b.7z", 1024, ageDays: 2);
        string c = MakeArchive(dir, "c.7z", 1024, ageDays: 1);

        HashSet<string> keep = new([locked], StringComparer.OrdinalIgnoreCase);
        QuotaReport report = mgr.Enforce(keepDays: 1, maxCount: 1, NoByteCap, keep);

        Assert.True(File.Exists(locked), "待上传的归档被删掉了 —— 这会让上传等待永远无法完成");
        Assert.NotEmpty(report.Notes);       // 必须如实说明"有文件删不掉"

        // 保护是有条件的：没在 keep 集合里的同样超期文件照删，
        // 否则"保护"就退化成了"配额失效"。
        Assert.False(File.Exists(b), "未受保护的超期归档没被清理");
        Assert.False(File.Exists(c), "未受保护的超期归档没被清理");
        Assert.Equal(2, report.DeletedCount);
    }

    [Fact]
    public void 全部归档都受保护时不报错也不空转失败()
    {
        using TempDir dir = new();
        (ZipTempManager mgr, _, _) = Build(dir);

        string a = MakeArchive(dir, "a.7z", 1024, ageDays: 99);
        string b = MakeArchive(dir, "b.7z", 1024, ageDays: 98);

        HashSet<string> keep = new([a, b], StringComparer.OrdinalIgnoreCase);
        QuotaReport report = mgr.Enforce(keepDays: 1, maxCount: 1, 1024, keep);

        Assert.Equal(0, report.DeletedCount);
        Assert.Equal(2, report.RemainingCount);
        Assert.True(File.Exists(a) && File.Exists(b));
    }

    [Fact]
    public void 中间文件被清扫_但正式归档不动()
    {
        // 打包中途被杀（断电、任务管理器结束进程）留下的 .part / .list 必须清掉，
        // 否则它们会一直占着磁盘且永远不会被 *.7z 配额看到。
        using TempDir dir = new();
        (ZipTempManager mgr, _, _) = Build(dir);

        dir.WriteFile("Backup_1.7z.part", 4096);
        dir.WriteFile("Backup_1.7z.list", 128);
        dir.WriteFile("scratch.tmp", 64);
        dir.WriteFile("Backup_0.7z", 1024);

        int swept = mgr.SweepIntermediates();

        Assert.Equal(3, swept);
        Assert.Single(dir.Files("*.7z"));
        Assert.Empty(dir.Files("*.part"));
        Assert.Empty(dir.Files("*.list"));
        Assert.Empty(dir.Files("*.tmp"));
    }

    [Fact]
    public void 磁盘预检_空间不够时拒绝()
    {
        // 回归场景 5：把最小剩余空间调到大于当前可用空间 → 拒绝打包，且不产生任何文件。
        using TempDir dir = new();

        PrecheckResult result = ZipTempManager.PrecheckPath(
            dir.Path,
            estimatedBytes: 1024,
            minFreeBytes: long.MaxValue / 4,
            label: "测试卷");

        Assert.False(result.Ok);
        Assert.False(string.IsNullOrWhiteSpace(result.Reason));
        Assert.Contains("测试卷", result.Reason!);
    }

    [Fact]
    public void 磁盘预检_空间够时通过()
    {
        using TempDir dir = new();

        PrecheckResult result = ZipTempManager.PrecheckPath(dir.Path, 1024, 0, "测试卷");

        Assert.True(result.Ok, result.Reason);
    }

    [Fact]
    public void 磁盘预检_路径不存在时不谎报有空间()
    {
        // 取不到磁盘信息时返回 Unknown 而不是 0 —— "查不出来"绝不能被当成"没空间"，
        // 否则网络盘会被永久拒绝；但也不能当成"空间无限"。
        PrecheckResult result = ZipTempManager.PrecheckPath(
            "\\\\不存在的主机\\不存在的共享\\x",
            estimatedBytes: 1024,
            minFreeBytes: 0,
            label: "网络卷");

        Assert.True(result.Ok);     // 不阻塞
    }

    [Fact]
    public void 目录不存在时ListArchives返回空而不是抛异常()
    {
        FakeTimeProvider time = new(Origin);
        ZipTempManager mgr = new(new RecordingLogger(), time);
        mgr.Configure("Z:\\这个盘不存在\\ZipTemp");

        Assert.Empty(mgr.ListArchives());
        Assert.Equal(QuotaReport.Empty, mgr.Enforce(3, 20, 1024 * 1024));
        Assert.Equal(0, mgr.SweepIntermediates());
    }

    [Fact]
    public void 三种配额可以同时生效()
    {
        using TempDir dir = new();
        (ZipTempManager mgr, _, _) = Build(dir);

        MakeArchive(dir, "ancient.7z", 1024, ageDays: 100);         // 被天数淘汰
        MakeArchive(dir, "big1.7z", 30 * 1024 * 1024, ageDays: 2);
        MakeArchive(dir, "big2.7z", 30 * 1024 * 1024, ageDays: 1);
        MakeArchive(dir, "big3.7z", 30 * 1024 * 1024, ageDays: 0);

        QuotaReport report = mgr.Enforce(keepDays: 3, maxCount: 2, maxTotalBytes: 40L * 1024 * 1024);

        Assert.True(report.RemainingCount <= 2, $"数量上限失效，还剩 {report.RemainingCount} 个");
        Assert.True(report.RemainingBytes <= 40L * 1024 * 1024,
            $"容量上限失效，还剩 {report.RemainingBytes} 字节");
        Assert.False(File.Exists(dir.File("ancient.7z")));
    }

    // ==================================================================
    //  旁挂清单：跟着归档生，也必须跟着归档死
    // ==================================================================

    /// <summary>给一个归档配一份真的旁挂清单，返回清单路径。</summary>
    private static string MakeSidecar(string archivePath, int fileCount = 3)
    {
        string sidecarPath = ArchiveManifest.SidecarPathFor(archivePath);

        ArchiveManifest.WriteSidecarFile(sidecarPath, new ArchiveManifestSidecar
        {
            ArchiveFileName = Path.GetFileName(archivePath),
            ArchiveBytes = 1024,
            ArchiveSha256 = new string('a', 64),
            CreatedLocal = Origin,
            FileCount = fileCount,
        });

        return sidecarPath;
    }

    private static string[] Sidecars(TempDir dir) => dir.Files("*" + ArchiveManifest.SidecarSuffix);

    [Fact]
    public void 归档不含清单文件_清单不会被当成归档算进配额()
    {
        // Windows 的通配符匹配对三字符扩展名有"前缀匹配"的历史包袱（*.htm 能匹配 .html）。
        // 万一 *.7z 也匹配上了 Backup_0.7z.manifest.json，清单就会被当成正经归档：
        // 数量配额瞬间翻倍失真，而且会去删一个"归档"却删掉了别人的账本。
        using TempDir dir = new();
        (ZipTempManager mgr, _, _) = Build(dir);

        string archive = MakeArchive(dir, "Backup_0.7z", 1024);
        MakeSidecar(archive);

        IReadOnlyList<ArchiveInfo> archives = mgr.ListArchives();

        Assert.Single(archives);
        Assert.Equal("Backup_0.7z", archives[0].FileName);
    }

    [Fact]
    public void 清单体积计入配额账面()
    {
        // 配额的意义是"ZipTemp 到底占了多少"，不是"归档文件加起来多少"。
        // 漏算的部分永远不会被任何一条上限看见 —— 这正是这个类要防的那种"没人负责的占用"。
        using TempDir dir = new();
        (ZipTempManager mgr, _, _) = Build(dir);

        string archive = MakeArchive(dir, "Backup_0.7z", 1024);
        string sidecar = MakeSidecar(archive);

        ArchiveInfo info = Assert.Single(mgr.ListArchives());

        Assert.Equal(1024, info.Bytes);
        Assert.Equal(new FileInfo(sidecar).Length, info.SidecarBytes);
        Assert.Equal(info.Bytes + info.SidecarBytes, info.TotalBytes);
        Assert.True(info.TotalBytes > info.Bytes, "清单体积没有计入");
    }

    [Fact]
    public void 配额淘汰归档时连它的旁挂清单一起删掉()
    {
        // 漏了这一步，清单就成了孤儿：归档被淘汰一个它留下一个，
        // 谁也不会再删，一直攒到手动清理为止。
        using TempDir dir = new();
        (ZipTempManager mgr, _, _) = Build(dir);

        List<string> archives = [];

        for (int i = 0; i < 5; i++)
        {
            string path = MakeArchive(dir, $"Backup_{i}.7z", 1024, ageDays: 5 - i);  // i 越大越新
            MakeSidecar(path);
            archives.Add(path);
        }

        Assert.Equal(5, Sidecars(dir).Length);

        QuotaReport report = mgr.Enforce(NoAgeCap, maxCount: 3, NoByteCap);

        Assert.Equal(2, report.DeletedCount);

        // 被淘汰的那两个：归档和清单都不在了。
        for (int i = 0; i < 2; i++)
        {
            Assert.False(File.Exists(archives[i]), $"Backup_{i}.7z 没被删");
            Assert.False(
                File.Exists(ArchiveManifest.SidecarPathFor(archives[i])),
                $"Backup_{i}.7z 的旁挂清单成了孤儿");
        }

        // 活下来的那三个：清单也得还在，不能把账本删了把包留下。
        for (int i = 2; i < 5; i++)
        {
            Assert.True(File.Exists(archives[i]));
            Assert.True(
                File.Exists(ArchiveManifest.SidecarPathFor(archives[i])),
                $"Backup_{i}.7z 还在，清单却被删了");
        }

        Assert.Equal(3, Sidecars(dir).Length);
    }

    [Fact]
    public void 按天数淘汰时同样连清单一起删()
    {
        using TempDir dir = new();
        (ZipTempManager mgr, _, _) = Build(dir);

        string old = MakeArchive(dir, "old.7z", 1024, ageDays: 10);
        string fresh = MakeArchive(dir, "fresh.7z", 1024, ageDays: 1);
        MakeSidecar(old);
        MakeSidecar(fresh);

        mgr.Enforce(keepDays: 3, NoCountCap, NoByteCap);

        Assert.False(File.Exists(old));
        Assert.False(File.Exists(ArchiveManifest.SidecarPathFor(old)), "超期归档的清单成了孤儿");

        Assert.True(File.Exists(fresh));
        Assert.True(File.Exists(ArchiveManifest.SidecarPathFor(fresh)));
    }

    [Fact]
    public void 待上传归档没被删时它的清单也不能删()
    {
        // 归档没删掉就别动清单 —— 那会把一个还在的包变成没有账本的包，
        // 将来想验"它还是不是当初那个包"就永远验不了了。
        using TempDir dir = new();
        (ZipTempManager mgr, _, _) = Build(dir);

        string locked = MakeArchive(dir, "pending.7z", 1024, ageDays: 99);
        MakeSidecar(locked);

        HashSet<string> keep = new([locked], StringComparer.OrdinalIgnoreCase);
        mgr.Enforce(keepDays: 1, maxCount: 1, NoByteCap, keep);

        Assert.True(File.Exists(locked));
        Assert.True(
            File.Exists(ArchiveManifest.SidecarPathFor(locked)),
            "归档因待上传而保留，清单却被删了");
    }

    [Fact]
    public void 归档已经没了只剩清单时_孤儿清单被扫掉()
    {
        // 可能来自上一个版本，也可能来自某次只删掉一半的中断。
        // 不管怎么来的，留着没有任何用处，而且永远不会再被任何一条配额看到。
        using TempDir dir = new();
        (ZipTempManager mgr, _, RecordingLogger log) = Build(dir);

        // 三份清单，一个归档都没有。
        MakeSidecar(dir.File("Backup_0.7z"));
        MakeSidecar(dir.File("Backup_1.7z"));
        MakeSidecar(dir.File("Backup_2.7z"));

        Assert.Equal(3, Sidecars(dir).Length);

        // 归档数为 0 时 Enforce 会提前返回 QuotaReport.Empty，
        // 孤儿清理不体现在报告里 —— 所以断言看磁盘，不看返回值。
        mgr.Enforce(NoAgeCap, NoCountCap, NoByteCap);

        Assert.Empty(Sidecars(dir));
        Assert.True(log.Contains("没有对应归档的旁挂清单"), $"清理了却没说。日志：{log.Dump()}");
    }

    [Fact]
    public void 孤儿清扫只动孤儿_归档还在的清单一律不碰()
    {
        using TempDir dir = new();
        (ZipTempManager mgr, _, _) = Build(dir);

        string alive = MakeArchive(dir, "alive.7z", 1024);
        string aliveSidecar = MakeSidecar(alive);

        string orphanSidecar = MakeSidecar(dir.File("ghost.7z"));   // 归档从来不存在

        mgr.Enforce(NoAgeCap, NoCountCap, NoByteCap);

        Assert.True(File.Exists(aliveSidecar), "归档还在，清单却被当成孤儿删了");
        Assert.False(File.Exists(orphanSidecar), "孤儿清单没被清掉");
        Assert.True(File.Exists(alive));
    }

    // ==================================================================
    //  演练目录：单次占用磁盘最多的东西，必须有人兜底
    // ==================================================================

    [Fact]
    public void 演练目录被清扫_普通目录不动()
    {
        // 演练目录里装的是解压出来的完整副本，和源文件一样大。
        // 演练自己的 finally 会删，但进程被杀、文件被杀毒软件占用时都删不掉。
        using TempDir dir = new();
        (ZipTempManager mgr, _, _) = Build(dir);

        // 两个演练残骸，其中一个里面还有子目录和文件（递归删除要能处理）。
        string drill1 = Path.Combine(dir.Path, RestoreDrillService.DrillDirectoryPrefix + "Backup_1_1234_1");
        string drill2 = Path.Combine(dir.Path, RestoreDrillService.DrillDirectoryPrefix + "Backup_2_1234_2");

        Directory.CreateDirectory(Path.Combine(drill1, "extracted", "子目录"));
        File.WriteAllText(Path.Combine(drill1, "extracted", "子目录", "解出来的.dat"), "内容");
        File.WriteAllText(Path.Combine(drill1, "Backup_1.7z"), "复制进来的包");
        Directory.CreateDirectory(drill2);

        // 一个名字不沾边的目录，绝不能被误删。
        string innocent = Path.Combine(dir.Path, "我不是演练目录");
        Directory.CreateDirectory(innocent);
        File.WriteAllText(Path.Combine(innocent, "重要.dat"), "别删我");

        int swept = mgr.SweepIntermediates();

        Assert.Equal(2, swept);
        Assert.False(Directory.Exists(drill1), "有内容的演练目录没被递归删掉");
        Assert.False(Directory.Exists(drill2));

        Assert.True(Directory.Exists(innocent), "误删了无关目录");
        Assert.True(File.Exists(Path.Combine(innocent, "重要.dat")));
    }

    [Fact]
    public void 演练目录与中间文件一起计数()
    {
        // 清扫的返回值是给日志用的，虚报会让人以为清掉了不存在的东西。
        using TempDir dir = new();
        (ZipTempManager mgr, _, _) = Build(dir);

        dir.WriteFile("Backup_1.7z.part", 4096);
        dir.WriteFile("Backup_1.7z.list", 128);
        dir.WriteFile("scratch.tmp", 64);
        dir.WriteFile(ArchiveManifest.EntryName, 256);          // 包内清单的中转残骸
        dir.WriteFile("Backup_9.7z.manifest.json.writing", 32); // 旁挂清单的半成品
        dir.WriteFile("Backup_0.7z", 1024);                     // 正经归档，不许动

        string archive = MakeArchive(dir, "Backup_8.7z", 1024);
        string sidecar = MakeSidecar(archive);                  // 正经清单，同样不许动

        Directory.CreateDirectory(Path.Combine(
            dir.Path, RestoreDrillService.DrillDirectoryPrefix + "Backup_3_999_1"));

        int swept = mgr.SweepIntermediates();

        // 5 个中间文件 + 1 个演练目录
        Assert.Equal(6, swept);

        Assert.Equal(2, dir.Files("*.7z").Length);
        Assert.Empty(dir.Files("*.part"));
        Assert.Empty(dir.Files("*.list"));
        Assert.Empty(dir.Files("*.tmp"));
        Assert.Empty(dir.Files("*.writing"));
        Assert.Empty(dir.Files(ArchiveManifest.EntryName));
        Assert.Empty(Directory.GetDirectories(dir.Path, RestoreDrillService.DrillDirectoryPrefix + "*"));

        // 清扫绝不能碰正经归档的旁挂清单 —— 它和 .7z 一样是长期资产。
        Assert.True(File.Exists(sidecar), "清扫把正经归档的旁挂清单删了");
    }

    [Fact]
    public void 没有演练目录时清扫不虚报()
    {
        using TempDir dir = new();
        (ZipTempManager mgr, _, _) = Build(dir);

        MakeArchive(dir, "Backup_0.7z", 1024);

        Assert.Equal(0, mgr.SweepIntermediates());
    }

    [Fact]
    public void 正在进行的演练目录不会被清扫误删()
    {
        // 界面上手动触发的演练和引擎自己跑的演练在<b>同一个进程</b>里，
        // 引擎 tick 结束时的清扫正好能撞上界面那一侧跑了一半的演练。
        // 撞上的后果是：解压结果连目录一起被删，那一侧报"演练失败" ——
        // 这种假警报比不报还坏，它会让人开始怀疑所有演练结果。
        using TempDir dir = new();
        (ZipTempManager mgr, _, _) = Build(dir);

        string active = Path.Combine(
            dir.Path, RestoreDrillService.DrillDirectoryPrefix + "Backup_live_1234_1");

        Directory.CreateDirectory(active);

        // 没有登记过的目录一律视为残骸。
        Assert.False(RestoreDrillService.IsActive(active));

        Assert.Equal(1, mgr.SweepIntermediates());
        Assert.False(Directory.Exists(active));
    }
}
