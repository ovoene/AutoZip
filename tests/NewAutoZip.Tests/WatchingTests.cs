using Microsoft.Extensions.Time.Testing;
using NewAutoZip.Core.Configuration;
using NewAutoZip.Core.Watching;
using Xunit;

namespace NewAutoZip.Tests;

public class StabilityTrackerTests
{
    private static readonly DateTimeOffset Origin = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    private const int Quiet = 60;
    private const int Rounds = 2;

    private static (StabilityTracker Tracker, FakeTimeProvider Time, FakeFileProbe Probe) Build(
        int quietSeconds = Quiet,
        int rounds = Rounds)
    {
        FakeTimeProvider time = new(Origin);
        FakeFileProbe probe = new();
        StabilityTracker tracker = new(time, probe, new RecordingLogger());
        tracker.Configure(quietSeconds, rounds);
        return (tracker, time, probe);
    }

    [Fact]
    public void 静默期满且连续确认后判定就绪()
    {
        (StabilityTracker t, FakeTimeProvider time, FakeFileProbe probe) = Build();
        const string path = "C:\\watch\\a.dat";

        probe.Set(path, 1000, Origin);
        t.Observe(path);

        Assert.Empty(t.Refresh().Ready);                        // 首轮只是初始化

        time.Advance(TimeSpan.FromSeconds(Quiet + 1));
        Assert.Empty(t.Refresh().Ready);                        // 静默期满，但确认轮数还差

        time.Advance(TimeSpan.FromSeconds(5));
        RefreshResult result = t.Refresh();

        Assert.Single(result.Ready);
        Assert.Equal(path, result.Ready[0].Path);
        Assert.Equal(TrackedFileState.Ready, result.Ready[0].State);
    }

    [Fact]
    public void 持续写入的文件永远不会被判定就绪()
    {
        (StabilityTracker t, FakeTimeProvider time, FakeFileProbe probe) = Build();
        const string path = "C:\\watch\\growing.dat";

        probe.Set(path, 1000, Origin);
        t.Observe(path);
        t.Refresh();

        long size = 1000;

        for (int i = 0; i < 20; i++)
        {
            time.Advance(TimeSpan.FromSeconds(30));
            size += 4096;
            probe.Set(path, size, time.GetUtcNow());        // 还在长

            Assert.Empty(t.Refresh().Ready);
        }

        // 停止写入后应当能正常就绪 —— 不能因为"曾经在长"就永久卡住。
        time.Advance(TimeSpan.FromSeconds(Quiet + 10));
        t.Refresh();
        time.Advance(TimeSpan.FromSeconds(10));

        Assert.Single(t.Refresh().Ready);
    }

    [Fact]
    public void 大小不变但修改时间在变也算未稳定()
    {
        // 定长文件被原地覆写（数据库、日志轮转）时大小不变，只有 mtime 在动。
        (StabilityTracker t, FakeTimeProvider time, FakeFileProbe probe) = Build();
        const string path = "C:\\watch\\fixed.dat";

        probe.Set(path, 4096, Origin);
        t.Observe(path);
        t.Refresh();

        for (int i = 0; i < 5; i++)
        {
            time.Advance(TimeSpan.FromSeconds(Quiet + 5));
            probe.Set(path, 4096, time.GetUtcNow());       // 大小相同，时间在变
            Assert.Empty(t.Refresh().Ready);
        }
    }

    [Fact]
    public void 文件被删除后从跟踪表里剔除()
    {
        // 旧版从不剔除消失的文件 → allStable 永假 → 整个程序静默假死，
        // UI 上还亮着"运行中"的绿灯。这是最隐蔽的一类故障。
        (StabilityTracker t, FakeTimeProvider time, FakeFileProbe probe) = Build();
        const string path = "C:\\watch\\doomed.dat";

        probe.Set(path, 1000, Origin);
        t.Observe(path);
        t.Refresh();
        Assert.Equal(1, t.Count);

        probe.Remove(path);
        RefreshResult result = t.Refresh();

        Assert.Single(result.Vanished);
        Assert.Equal(path, result.Vanished[0]);
        Assert.Equal(0, t.Count);
        Assert.False(t.IsTracked(path));
    }

    [Fact]
    public void 文件改名等价于旧的消失新的出现()
    {
        (StabilityTracker t, FakeTimeProvider time, FakeFileProbe probe) = Build();
        const string oldPath = "C:\\watch\\before.dat";
        const string newPath = "C:\\watch\\after.dat";

        probe.Set(oldPath, 1000, Origin);
        t.Observe(oldPath);
        t.Refresh();

        probe.Remove(oldPath);
        probe.Set(newPath, 1000, Origin);
        t.Observe(newPath);

        RefreshResult result = t.Refresh();

        Assert.Contains(oldPath, result.Vanished);
        Assert.True(t.IsTracked(newPath));
        Assert.False(t.IsTracked(oldPath));
        Assert.Equal(1, t.Count);
    }

    [Fact]
    public void 读不了的文件被单独归类_不会伪装成已就绪()
    {
        (StabilityTracker t, FakeTimeProvider time, FakeFileProbe probe) = Build();
        const string path = "C:\\watch\\locked.dat";

        probe.Set(path, 1000, Origin, readable: false);
        t.Observe(path);
        t.Refresh();

        time.Advance(TimeSpan.FromSeconds(Quiet * 3));
        RefreshResult result = t.Refresh();

        Assert.Empty(result.Ready);
        Assert.Single(result.Unreadable);
        Assert.Equal(TrackedFileState.Unreadable, result.Unreadable[0].State);
    }

    [Fact]
    public void 长期读不了的文件最终被放弃_不会永久占着跟踪表()
    {
        (StabilityTracker t, FakeTimeProvider time, FakeFileProbe probe) = Build();
        const string path = "C:\\watch\\forever-locked.dat";

        probe.Set(path, 1000, Origin, readable: false);
        t.Observe(path);

        // 放弃阈值默认 20 分钟；这里调到 1 分钟以便快速验证收敛。
        t.Configure(Quiet, Rounds, refreshIntervalSeconds: 10, unreadableGiveUpMinutes: 1);

        List<string> gaveUp = [];

        for (int i = 0; i < 40 && gaveUp.Count == 0; i++)
        {
            time.Advance(TimeSpan.FromSeconds(10));
            gaveUp.AddRange(t.Refresh().GaveUp);
        }

        Assert.Contains(path, gaveUp);
        Assert.False(t.IsTracked(path));
    }

    [Fact]
    public void 放弃时长按设置值生效_不再是写死的探测次数()
    {
        (StabilityTracker t, FakeTimeProvider time, FakeFileProbe probe) = Build();
        const string path = "C:\\watch\\locked-timed.dat";

        probe.Set(path, 1000, Origin, readable: false);
        t.Observe(path);
        t.Configure(Quiet, Rounds, refreshIntervalSeconds: 5, unreadableGiveUpMinutes: 3);

        DateTimeOffset start = time.GetUtcNow();
        DateTimeOffset? gaveUpAt = null;

        // 按 5 秒一轮推进，直到被放弃。上限给足（3 分钟 = 36 轮，这里给 200 轮）。
        for (int i = 0; i < 200 && gaveUpAt is null; i++)
        {
            time.Advance(TimeSpan.FromSeconds(5));

            if (t.Refresh().GaveUp.Count > 0)
            {
                gaveUpAt = time.GetUtcNow();
            }
        }

        Assert.NotNull(gaveUpAt);

        // 首次探测记 0 增量，所以实际会比 3 分钟多一轮，允许一轮的误差。
        TimeSpan waited = gaveUpAt!.Value - start;
        Assert.InRange(waited, TimeSpan.FromMinutes(3), TimeSpan.FromMinutes(3) + TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void 停摆一整夜不会让读不到的文件在恢复后的第一次探测就被放弃()
    {
        (StabilityTracker t, FakeTimeProvider time, FakeFileProbe probe) = Build();
        const string path = "C:\\watch\\locked-overnight.dat";

        probe.Set(path, 1000, Origin, readable: false);
        t.Observe(path);
        t.Configure(Quiet, Rounds, refreshIntervalSeconds: 5, unreadableGiveUpMinutes: 20);

        t.Refresh();

        // 工作时段之外主循环不探测；恢复后的第一次探测不该把整段空白算成"已经等过了"。
        time.Advance(TimeSpan.FromHours(9));
        RefreshResult after = t.Refresh();

        Assert.Empty(after.GaveUp);
        Assert.True(t.IsTracked(path));
        Assert.Single(after.Unreadable);

        // 时间跳变也不能一步凑满：单次增量不超过阈值的一半，所以至少还得再来一次。
        time.Advance(TimeSpan.FromHours(9));
        Assert.Empty(t.Refresh().GaveUp);
    }

    [Fact]
    public void 中途读到过一次就重新计时()
    {
        (StabilityTracker t, FakeTimeProvider time, FakeFileProbe probe) = Build();
        const string path = "C:\\watch\\flaky-lock.dat";

        probe.Set(path, 1000, Origin, readable: false);
        t.Observe(path);
        t.Configure(Quiet, Rounds, refreshIntervalSeconds: 10, unreadableGiveUpMinutes: 1);

        // 先攒 50 秒（阈值 60 秒，还差一步）。
        for (int i = 0; i < 5; i++)
        {
            time.Advance(TimeSpan.FromSeconds(10));
            Assert.Empty(t.Refresh().GaveUp);
        }

        // 锁松开一次：累计清零。
        probe.Set(path, 1000, Origin, readable: true);
        time.Advance(TimeSpan.FromSeconds(10));
        t.Refresh();

        // 再锁上，只等 30 秒，不该被放弃。
        probe.Set(path, 1000, Origin, readable: false);

        for (int i = 0; i < 3; i++)
        {
            time.Advance(TimeSpan.FromSeconds(10));
            Assert.Empty(t.Refresh().GaveUp);
        }

        Assert.True(t.IsTracked(path));
    }

    [Fact]
    public void 已就绪的文件不会被重复上报()
    {
        (StabilityTracker t, FakeTimeProvider time, FakeFileProbe probe) = Build();
        const string path = "C:\\watch\\a.dat";

        probe.Set(path, 1000, Origin);
        t.Observe(path);
        t.Refresh();
        time.Advance(TimeSpan.FromSeconds(Quiet + 1));
        t.Refresh();
        time.Advance(TimeSpan.FromSeconds(5));

        Assert.Single(t.Refresh().Ready);

        // 已经交给批次之后要 Forget，否则会被反复打包。
        t.Forget([path]);
        time.Advance(TimeSpan.FromSeconds(5));

        Assert.Empty(t.Refresh().Ready);
        Assert.Equal(0, t.Count);
    }

    [Fact]
    public void 重复Observe同一路径不会产生重复条目()
    {
        (StabilityTracker t, _, FakeFileProbe probe) = Build();
        const string path = "C:\\watch\\a.dat";
        probe.Set(path, 1000, Origin);

        Assert.True(t.Observe(path));
        Assert.False(t.Observe(path));
        Assert.False(t.Observe(path.ToUpperInvariant()));   // 大小写不敏感

        Assert.Equal(1, t.Count);
    }

    [Fact]
    public void Reconcile_补上watcher漏掉的文件()
    {
        // FileSystemWatcher 缓冲区溢出会静默丢事件，定时对账是唯一兜底。
        (StabilityTracker t, _, FakeFileProbe probe) = Build();

        probe.Set("C:\\watch\\1.dat", 100, Origin);
        probe.Set("C:\\watch\\2.dat", 100, Origin);
        probe.Set("C:\\watch\\3.dat", 100, Origin);

        int added = t.Reconcile("C:\\watch", recurse: false, new FileFilter(null));

        Assert.Equal(3, added);
        Assert.Equal(3, t.Count);

        Assert.Equal(0, t.Reconcile("C:\\watch", false, new FileFilter(null)));   // 幂等
    }

    [Fact]
    public void Reconcile_跳过已在处理中的文件()
    {
        (StabilityTracker t, _, FakeFileProbe probe) = Build();

        probe.Set("C:\\watch\\1.dat", 100, Origin);
        probe.Set("C:\\watch\\2.dat", 100, Origin);

        int added = t.Reconcile(
            "C:\\watch",
            recurse: false,
            new FileFilter(null),
            alreadyHandled: p => p.EndsWith("1.dat", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(1, added);
        Assert.False(t.IsTracked("C:\\watch\\1.dat"));
    }
}

public class FileFilterTests
{
    [Theory]
    [InlineData("C:\\a\\b", "C:\\a", true)]
    [InlineData("C:\\a", "C:\\a", true)]
    [InlineData("C:\\a\\", "C:\\a", true)]
    [InlineData("C:\\a\\b\\c", "C:\\a", true)]
    [InlineData("C:\\ab", "C:\\a", false)]        // 前缀相同但不是子目录 —— 纯字符串比较会算错
    [InlineData("C:\\b", "C:\\a", false)]
    [InlineData("C:\\a", "C:\\a\\b", false)]
    public void 路径嵌套判断正确(string candidate, string ancestor, bool expected) =>
        Assert.Equal(expected, FileFilter.IsSameOrUnder(candidate, ancestor));

    [Fact]
    public void 排除目录下的文件被拒绝()
    {
        FileFilter filter = new(null, ["C:\\watch\\ZipTemp"]);

        Assert.False(filter.Accept("C:\\watch\\ZipTemp\\Backup_1.7z"));
        Assert.False(filter.Accept("C:\\watch\\ZipTemp\\sub\\x.dat"));
        Assert.True(filter.Accept("C:\\watch\\data.dat"));
    }

    [Theory]
    [InlineData("C:\\w\\x.tmp", false)]
    [InlineData("C:\\w\\x.crdownload", false)]
    [InlineData("C:\\w\\x.part", false)]
    [InlineData("C:\\w\\~$doc.docx", false)]
    [InlineData("C:\\w\\x.dat", true)]
    [InlineData("C:\\w\\report.pdf", true)]
    public void 默认排除写入中的临时文件(string path, bool accepted)
    {
        FileFilter filter = new(new AppSettings().ExcludePatterns);
        Assert.Equal(accepted, filter.Accept(path));
    }

    [Fact]
    public void 通配符按Windows语义匹配()
    {
        FileFilter filter = new(["*.log", "temp*"]);

        Assert.False(filter.Accept("C:\\w\\app.log"));
        Assert.False(filter.Accept("C:\\w\\APP.LOG"));       // 必须忽略大小写
        Assert.False(filter.Accept("C:\\w\\tempfile.dat"));
        Assert.True(filter.Accept("C:\\w\\app.log.bak"));
    }
}
