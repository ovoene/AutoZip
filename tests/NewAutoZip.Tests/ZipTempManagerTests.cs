using Microsoft.Extensions.Time.Testing;
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
}
