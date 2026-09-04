using Microsoft.Extensions.Time.Testing;
using NewAutoZip.Core.Configuration;
using NewAutoZip.Core.Notifications;
using NewAutoZip.Core.OneDrive;
using NewAutoZip.Core.Pipeline;
using NewAutoZip.Core.Watching;
using Xunit;
using Xunit.Abstractions;

namespace NewAutoZip.Tests;

/// <summary>
/// 引擎级集成测试 —— 直接对应方案里的"关键回归场景"。
///
/// 时间模型：引擎每轮的等待用真实时钟（<c>SemaphoreSlim.WaitAsync</c>，兜底 1 秒），
/// 而<b>所有业务判断都读 TimeProvider</b>。测试因此可以：
///   1. 用 <see cref="BackupEngine.Nudge"/> 立刻唤醒主循环，不等那 1 秒；
///   2. 用 <see cref="BackupEngine.CompletedRounds"/> 确认"这一轮真的跑完了"再断言；
///   3. 用 <see cref="FakeTimeProvider"/> 让引擎眼里的时间走过几十分钟。
/// 于是 30s→1m→2m→5m 的完整退避阶梯在几百毫秒真实时间内就能跑完，且完全确定性。
/// </summary>
public sealed class BackupEngineTests(ITestOutputHelper output) : IDisposable
{
    private readonly ITestOutputHelper _output = output;
    private readonly List<IDisposable> _cleanup = [];

    /// <summary>固定的起点。所有"引擎眼里的时间"都从这里开始，与真实时钟无关。</summary>
    private static readonly DateTimeOffset Origin = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    /// <summary>单轮真实时间上限。超了说明引擎卡住了，要让测试失败而不是挂住整个测试套件。</summary>
    private static readonly TimeSpan RoundTimeout = TimeSpan.FromSeconds(20);

    private TempDir NewDir(string label)
    {
        TempDir dir = new(label);
        _cleanup.Add(dir);
        return dir;
    }

    public void Dispose()
    {
        foreach (IDisposable d in _cleanup)
        {
            d.Dispose();
        }
    }

    private sealed record Harness(
        BackupEngine Engine,
        FakeTimeProvider Time,
        RecordingLogger Log,
        RecordingNotificationHub Notify,
        FakeUploadMonitor Uploads,
        TempDir Monitor,
        TempDir OneDrive,
        TempDir ZipTemp,
        AppSettings Settings,
        StateStore Store);

    /// <summary>
    /// 搭一个引擎。<paramref name="sevenZipExePath"/> 指向一个不存在的路径就能稳定制造打包失败,
    /// 等价于回归场景 1 里"把 7za.exe 临时改名"。
    ///
    /// <paramref name="seedState"/> 在 Start 之前写一份 state.json，用来模拟"上次进程留下的状态"
    /// —— 回归场景 7（重启恢复）就靠它。
    ///
    /// <paramref name="seedMonitor"/> 在 Start <b>之前</b>往监控目录里放文件。这个时机是关键：
    /// 那时 watcher 还没 <c>EnableRaisingEvents</c>，一个事件都不会产生，
    /// 这些文件只能由启动时的那一次 Reconcile 一起发现 —— 于是"同一时刻进来 N 个文件"
    /// 是确定的，不用去赌两个 watcher 事件会不会落进同一轮。
    /// </summary>
    private Harness BuildHarness(
        string sevenZipExePath = "Z:\\这个目录不存在\\7za.exe",
        int maxAttempts = 3,
        Action<AppSettings>? tweak = null,
        Action<EngineState>? seedState = null,
        Action<TempDir>? seedMonitor = null)
    {
        TempDir monitor = NewDir("naz-watch");
        TempDir oneDrive = NewDir("naz-od");
        TempDir zipTemp = NewDir("naz-tmp");
        TempDir state = NewDir("naz-state");

        AppSettings settings = new()
        {
            MonitorPath = monitor.Path,
            CloudPath = oneDrive.Path,
            ZipTempPath = zipTemp.Path,
            Password = "Str0ng-Passw0rd!",

            // 全部压到下限，让"引擎眼里的时间"少推进一点就能跑完一整轮流程。
            RefreshIntervalSeconds = SettingsLimits.RefreshIntervalSecondsMin,
            QuietSeconds = SettingsLimits.QuietSecondsMin,
            StableConfirmRounds = 1,
            BatchWindowMinutes = SettingsLimits.BatchWindowMinutesMin,
            ReconcileIntervalSeconds = SettingsLimits.ReconcileIntervalSecondsMin,
            UploadPollSeconds = SettingsLimits.UploadPollSecondsMin,

            MinFreeDiskBytes = 0,
            MaxAttemptsBeforeQuarantine = maxAttempts,
            ProcessExistingFilesOnFirstRun = true,
            EnabledEvents = NotifyEvent.All,
            NotifyChannel = NotifierKind.None,
        };

        tweak?.Invoke(settings);

        FakeTimeProvider time = new(Origin);
        RecordingLogger log = new();
        RecordingNotificationHub notify = new();
        FakeUploadMonitor uploads = new();

        StateStore store = new(Path.Combine(state.Path, "state.json"), log);

        if (seedState is not null)
        {
            EngineState seeded = store.Load();
            seedState(seeded);
            store.Save(seeded);
        }

        BackupEngine engine = new(log, time, Win32FileProbe.Instance, store);

        // 先放文件、再 Start：这样它们必然是被同一次 Reconcile 一起发现的。
        seedMonitor?.Invoke(monitor);

        Assert.True(engine.Start(settings, notify, uploads, sevenZipExePath));

        return new Harness(engine, time, log, notify, uploads, monitor, oneDrive, zipTemp, settings, store);
    }

    /// <summary>等一轮检查真正跑完（不是"睡一会儿希望它跑完了"）。</summary>
    private static Task<bool> WaitOneRoundAsync(Harness h) => WaitRoundsAsync(h, 1);

    /// <summary>等 <paramref name="rounds"/> 轮检查跑完。</summary>
    private static async Task<bool> WaitRoundsAsync(Harness h, int rounds)
    {
        long target = h.Engine.CompletedRounds + rounds;
        DateTimeOffset deadline = DateTimeOffset.UtcNow + RoundTimeout;

        while (h.Engine.CompletedRounds < target)
        {
            if (!h.Engine.IsRunning || DateTimeOffset.UtcNow > deadline)
            {
                return false;
            }

            h.Engine.Nudge();
            await Task.Delay(2);
        }

        return true;
    }

    /// <summary>
    /// 拨快"引擎眼里的时间"，然后等到<b>确实有一轮是在拨表之后开始的</b>。
    ///
    /// 为什么不能拨完表只等一轮：<see cref="WaitOneRoundAsync"/> 只看
    /// <c>CompletedRounds</c> 有没有 +1，而拨表的那一刻可能<b>正有一轮在跑</b>
    /// —— 它是拨表之前读的时钟。那一轮跑完计数就够了，于是断言在"引擎还没看过新时间"
    /// 的时候就开始查，边沿通知一条都没有，测试报 Equal(1, 0)。
    /// 空跑时这两件事挨得极近，很难撞上；整个测试套件并行跑起来抢 CPU 时就会撞上，
    /// 表现为"单独跑一直过，全量跑偶尔挂"。
    ///
    /// 等两轮就把它堵死了：在跑的那一轮算第一轮，第二轮只能在它之后开始，
    /// 也就必然在拨表之后。边沿通知本身是跳变触发的，多跑一轮不会多发一条
    /// （测试末尾那段就是专门盯这个的）。
    /// </summary>
    private static async Task<bool> AdvanceAndSettleAsync(Harness h, TimeSpan span)
    {
        h.Time.Advance(span);
        return await WaitRoundsAsync(h, 2);
    }

    /// <summary>
    /// 推进"引擎眼里的时间"，每步跑完一轮再检查条件。
    /// </summary>
    private static async Task<bool> PumpUntilAsync(
        Harness h,
        Func<EngineSnapshot, bool> condition,
        int maxSteps = 60,
        int engineSecondsPerStep = 30)
    {
        for (int i = 0; i < maxSteps; i++)
        {
            if (condition(h.Engine.Snapshot))
            {
                return true;
            }

            h.Time.Advance(TimeSpan.FromSeconds(engineSecondsPerStep));

            if (!await WaitOneRoundAsync(h))
            {
                return condition(h.Engine.Snapshot);
            }
        }

        return condition(h.Engine.Snapshot);
    }

    /// <summary>断言 ZipTemp 里一个中间产物都没有 —— 这就是用户报告的症状的反面。</summary>
    private static void AssertZipTempClean(Harness h)
    {
        Assert.Empty(h.ZipTemp.Files("*.7z"));
        Assert.Empty(h.ZipTemp.Files("*.part"));
        Assert.Empty(h.ZipTemp.Files("*.list"));
        Assert.Empty(h.ZipTemp.Files("*.tmp"));
    }

    private void DumpOnFailure(Harness h)
    {
        _output.WriteLine($"---- 快照：{h.Engine.Snapshot.StatusText}（已跑 {h.Engine.CompletedRounds} 轮）----");
        _output.WriteLine("---- 引擎日志 ----");
        _output.WriteLine(h.Log.Dump());
        _output.WriteLine("---- ZipTemp 内容 ----");

        foreach (string f in h.ZipTemp.Files())
        {
            _output.WriteLine($"  {Path.GetFileName(f)}  {new FileInfo(f).Length} B");
        }
    }

    // ==================================================================
    //  回归场景 1：打包失败绝不堆积文件（用户报告的症状）
    // ==================================================================

    [Fact]
    public async Task 场景1_打包持续失败时ZipTemp不新增任何文件并最终隔离()
    {
        // 旧版行为：失败分支不复位 _files / _hasWindow，于是每个扫描周期（默认 30 秒）
        // 重新生成一个完整压缩包，永不停止，直到磁盘被打满。
        Harness h = BuildHarness(maxAttempts: 3);

        try
        {
            h.Monitor.WriteFile("data1.dat", 4096);
            h.Monitor.WriteFile("data2.dat", 4096);

            bool reached = await PumpUntilAsync(h, s => s.QuarantineCount > 0);
            EngineSnapshot snap = h.Engine.Snapshot;

            Assert.True(reached, $"没能在时限内收敛到隔离区。当前状态：{snap.StatusText}");

            // 核心断言：一个压缩包都不该产生。
            AssertZipTempClean(h);
            Assert.Empty(h.OneDrive.Files());

            // 隔离后不再有任何自动重试。
            Assert.Equal(1, snap.QuarantineCount);
            Assert.Equal(0, snap.RetryCount);
            Assert.Equal(0, snap.ArchivesCreated);

            // 隔离批次里保留了文件清单、失败次数和原因，用户能看懂发生了什么。
            QuarantineView q = snap.Quarantines[0];
            Assert.Equal(2, q.FileCount);
            Assert.Equal(3, q.Attempts);
            Assert.False(string.IsNullOrWhiteSpace(q.Reason));

            // 通知：失败要发，隔离要发 —— 旧版打包失败时根本不通知。
            Assert.True(h.Notify.Any(NotifyEvent.Failure), "打包失败没有发通知");
            Assert.True(h.Notify.Any(NotifyEvent.Quarantined), "进入隔离区没有发通知");

            // 引擎必须还活着 —— 不能悄悄死掉却让界面继续显示"运行中"。
            Assert.True(h.Engine.IsRunning);
        }
        catch
        {
            DumpOnFailure(h);
            throw;
        }
        finally
        {
            await h.Engine.StopAsync();
        }
    }

    [Fact]
    public async Task 场景1b_隔离之后继续运行很久也不会再产出文件()
    {
        Harness h = BuildHarness(maxAttempts: 2);

        try
        {
            h.Monitor.WriteFile("data.dat", 2048);
            Assert.True(await PumpUntilAsync(h, s => s.QuarantineCount > 0));

            long roundsAtQuarantine = h.Engine.CompletedRounds;

            // 隔离之后再让引擎眼里的时间走过 12 小时（每步半小时，共 24 步）。
            // 旧版在这个阶段已经产出上千个压缩包。
            await PumpUntilAsync(h, _ => false, maxSteps: 24, engineSecondsPerStep: 1800);

            Assert.True(
                h.Engine.CompletedRounds > roundsAtQuarantine + 20,
                "引擎并没有真的继续跑，这个测试没有验证到任何东西");

            AssertZipTempClean(h);
            Assert.Equal(0, h.Engine.Snapshot.ArchivesCreated);
            Assert.Equal(0, h.Engine.Snapshot.RetryCount);
            Assert.Equal(1, h.Engine.Snapshot.QuarantineCount);
            Assert.True(h.Engine.IsRunning);
        }
        catch
        {
            DumpOnFailure(h);
            throw;
        }
        finally
        {
            await h.Engine.StopAsync();
        }
    }

    [Fact]
    public async Task 场景9_通知通道抛异常也不影响主流程且不导致重复打包()
    {
        // 旧版【打包】那次 Notify 调用没有 try/catch，钉钉限流 / Webhook 500 / TG 连不上
        // 任一情况都会让异常穿过状态复位代码 —— 然后无限重打包。
        Harness h = BuildHarness(maxAttempts: 2);
        h.Notify.ThrowOnSend = true;

        try
        {
            h.Monitor.WriteFile("data.dat", 2048);

            Assert.True(
                await PumpUntilAsync(h, s => s.QuarantineCount > 0),
                "通知抛异常把主流程带崩了 —— 批次没能走到隔离区");

            AssertZipTempClean(h);
            Assert.True(h.Engine.IsRunning);

            // 通知确实被调用过（否则这个测试什么都没验证）。
            Assert.NotEmpty(h.Notify.Sent);
        }
        catch
        {
            DumpOnFailure(h);
            throw;
        }
        finally
        {
            await h.Engine.StopAsync();
        }
    }

    // ==================================================================
    //  回归场景 3：源文件中途消失
    // ==================================================================

    [Fact]
    public async Task 场景3_源文件在窗口期内被删除时程序不假死()
    {
        // 旧版：跟踪表从不剔除已消失的文件 → allStable 永远为 false → 整个程序静默假死，
        // 而 UI 上"● 运行中"的绿灯还亮着。
        Harness h = BuildHarness(maxAttempts: 3);

        try
        {
            string doomed = h.Monitor.WriteFile("doomed.dat", 2048);

            // 等它进入视野（跟踪中或已并入批次）。
            Assert.True(await PumpUntilAsync(h, s => s.TrackedCount > 0 || s.BatchFileCount > 0));

            File.Delete(doomed);

            // 引擎必须继续正常运转：后续文件照样能走完整个流程
            //（这里"走完"= 一路失败到隔离，因为 7za 是故意缺失的）。
            h.Monitor.WriteFile("healthy.dat", 2048);

            Assert.True(
                await PumpUntilAsync(h, s => s.QuarantineCount > 0),
                "文件被删后引擎卡住了 —— 后续文件再也走不完流程");

            AssertZipTempClean(h);
            Assert.True(h.Engine.IsRunning);

            // 已消失的文件不该出现在隔离批次里。
            Assert.DoesNotContain(
                doomed,
                h.Engine.Snapshot.Quarantines.SelectMany(q => q.Files),
                StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            DumpOnFailure(h);
            throw;
        }
        finally
        {
            await h.Engine.StopAsync();
        }
    }

    /// <summary>
    /// 已就绪、正在批次里等窗口到期的文件被全部删掉 —— 这一支曾经一条通知都不发。
    ///
    /// 为什么非补不可：【就绪】那一条已经发出去了，正文里写着"准备开始打包 / 打包开始时间：HH:mm:ss"，
    /// 用户等到那个时刻，然后再也收不到任何消息（只有一行 Info 日志）。
    /// 现在改为借【结束】那一位、换用【取消】这个词收尾。
    /// </summary>
    [Fact]
    public async Task 源文件在打包前全部消失时以取消收尾并通知()
    {
        Harness h = BuildHarness();

        try
        {
            string doomed = h.Monitor.WriteFile("会消失的.dat", 2048);

            // 等它稳定并进入批次。这一刻窗口刚刚打开（同一轮里绝不可能已经到期）。
            Assert.True(
                await PumpUntilAsync(h, s => s.BatchFileCount == 1),
                $"文件没能进入批次。状态：{h.Engine.Snapshot.StatusText}");

            // 【就绪】已经许诺过要打包了 —— 这正是这条通知存在的理由。
            Assert.Equal(1, h.Notify.CountOf(NotifyEvent.FilesStable));

            // 现在删掉。已就绪的文件不再被探测，所以删除当场没人知道，
            // 只有窗口到期时 RunBatchAsync 重新 Probe 才会发现。
            File.Delete(doomed);

            Assert.True(
                await PumpUntilAsync(h, _ => h.Notify.Any(NotifyEvent.RoundFinished)),
                $"窗口到期后没有任何收尾通知。状态：{h.Engine.Snapshot.StatusText}");

            Assert.Equal(1, h.Notify.CountOf(NotifyEvent.RoundFinished));

            NotifyMessage cancelled = h.Notify.First(NotifyEvent.RoundFinished);

            // 标签必须和成功那条不一样：正文里一个压缩包都没有。
            Assert.Equal("取消", cancelled.Tag);

            string text = cancelled.ToPlainText();

            Assert.Contains("【取消】", text);
            Assert.Contains("本轮取消", text);
            Assert.Contains("已消失的文件：", text);
            Assert.Contains("1. 会消失的.dat", text);
            // 文件已经不在了，取不到大小 —— 印成"（0B）"会被当成"发现了一个空文件"。
            Assert.DoesNotContain("（0B）", text);

            // 最后一行照旧是"下一次的工作开始时间"，跟【结束】一致。
            string lastLine = text.Split('\n', StringSplitOptions.RemoveEmptyEntries)[^1].Trim();
            Assert.StartsWith("下一次的工作开始时间：", lastLine);

            // 普通批次不带批次编号（那是重试批次才有的）。
            Assert.DoesNotContain("批次编号：", text);

            // 什么都没产出，也没有失败、没有重试、没有隔离 —— 这不是故障。
            Assert.Equal(0, h.Notify.CountOf(NotifyEvent.PackCompleted));
            Assert.Equal(0, h.Notify.CountOf(NotifyEvent.Failure));
            Assert.Equal(0, h.Engine.Snapshot.ArchivesCreated);
            Assert.Equal(0, h.Engine.Snapshot.RetryCount);
            Assert.Equal(0, h.Engine.Snapshot.QuarantineCount);
            Assert.Empty(h.OneDrive.Files());
            AssertZipTempClean(h);

            // 批次状态已复位，引擎照常继续跑。
            Assert.Equal(0, h.Engine.Snapshot.BatchFileCount);
            Assert.True(h.Engine.IsRunning);
        }
        catch
        {
            DumpOnFailure(h);
            throw;
        }
        finally
        {
            await h.Engine.StopAsync();
        }
    }

    // ==================================================================
    //  回归场景 5：磁盘预检
    // ==================================================================

    [Fact]
    public async Task 场景5_磁盘余量不足时拒绝打包且不产生任何文件()
    {
        Harness h = BuildHarness(
            maxAttempts: 2,
            tweak: s => s.MinFreeDiskBytes = SettingsLimits.MinFreeDiskBytesMax);   // 要求 4 TB 空闲

        try
        {
            h.Monitor.WriteFile("data.dat", 4096);

            bool refused = await PumpUntilAsync(h, _ => h.Log.Contains("已拒绝打包，未产生任何文件"));

            Assert.True(refused, "磁盘预检没有拒绝打包");

            // 关键：预检拒绝的路径上一个中间文件都不该出现。
            AssertZipTempClean(h);
            Assert.True(h.Notify.Any(NotifyEvent.DiskWarning), "磁盘不足没有发告警通知");
            Assert.True(h.Engine.IsRunning);
        }
        catch
        {
            DumpOnFailure(h);
            throw;
        }
        finally
        {
            await h.Engine.StopAsync();
        }
    }

    // ==================================================================
    //  回归场景 6：停止立即生效，且不会出现双 Worker
    // ==================================================================

    [Fact]
    public async Task 场景6_停止立即返回且不残留僵尸循环()
    {
        Harness h = BuildHarness();

        h.Monitor.WriteFile("data.dat", 2048);
        await PumpUntilAsync(h, s => s.TrackedCount > 0 || s.BatchFileCount > 0, maxSteps: 10);

        DateTimeOffset began = DateTimeOffset.UtcNow;
        await h.Engine.StopAsync();
        TimeSpan elapsed = DateTimeOffset.UtcNow - began;

        Assert.False(h.Engine.IsRunning);
        Assert.True(elapsed < TimeSpan.FromSeconds(10), $"StopAsync 花了 {elapsed.TotalSeconds:0.0} 秒才返回");
        Assert.Equal(EnginePhase.Stopped, h.Engine.Snapshot.Phase);
        Assert.False(h.Engine.Snapshot.Running);

        // 停下来之后轮数不再增长。
        long rounds = h.Engine.CompletedRounds;
        await Task.Delay(150);
        Assert.Equal(rounds, h.Engine.CompletedRounds);
    }

    [Fact]
    public async Task 场景6b_运行中重复Start被拒绝_停止后可以再启动()
    {
        // 旧版 StopProgram 把 _thread 置 null 却不 Join，"是否在运行"又靠 _thread.IsAlive 判断，
        // 于是停止后立刻再点开始会起第二个 Worker —— 两个 Worker 同时往 ZipTemp 扔包。
        Harness h = BuildHarness();

        try
        {
            Assert.False(h.Engine.Start(h.Settings), "运行中竟然允许再启动一个循环");
            Assert.True(h.Log.Contains("已在运行"));
        }
        finally
        {
            await h.Engine.StopAsync();
        }

        // 停干净之后可以再启动，且仍然只有一个循环。
        Assert.True(h.Engine.Start(h.Settings, h.Notify, h.Uploads, "Z:\\这个目录不存在\\7za.exe"));
        Assert.False(h.Engine.Start(h.Settings));
        await h.Engine.StopAsync();

        AssertZipTempClean(h);
    }

    // ==================================================================
    //  隔离区人工干预
    // ==================================================================

    [Fact]
    public async Task 隔离批次可以被人工重试或丢弃()
    {
        Harness h = BuildHarness(maxAttempts: 2);

        try
        {
            h.Monitor.WriteFile("data.dat", 2048);
            Assert.True(await PumpUntilAsync(h, s => s.QuarantineCount > 0));

            string id = h.Engine.Snapshot.Quarantines[0].Id;

            Assert.False(h.Engine.DiscardQuarantined("不存在的Id"));
            Assert.False(h.Engine.RequeueQuarantined("不存在的Id"));

            // 重试/忽略是唯一由界面线程发起的状态变更，它们只是排进命令队列，
            // 真正的修改由主循环在自己的线程上做（否则会与主循环并发写 Dictionary/List）。
            // 所以这里要等主循环跑一轮才看得到效果。
            Assert.True(h.Engine.RequeueQuarantined(id));
            Assert.True(
                await PumpUntilAsync(
                    h, s => s.Quarantines.All(q => q.Id != id), maxSteps: 10, engineSecondsPerStep: 1),
                "重试请求没有被主循环执行 —— 批次仍留在隔离区");

            Assert.True(h.Log.Contains("已放回处理队列"));

            // 重试仍然会失败，于是再次回到隔离区 —— 但依然一个压缩包都不产生。
            Assert.True(await PumpUntilAsync(h, s => s.QuarantineCount > 0));
            AssertZipTempClean(h);

            string again = h.Engine.Snapshot.Quarantines[0].Id;
            Assert.NotEqual(id, again);

            Assert.True(h.Engine.DiscardQuarantined(again));
            Assert.True(
                await PumpUntilAsync(h, s => s.QuarantineCount == 0, maxSteps: 10, engineSecondsPerStep: 1),
                "忽略请求没有被主循环执行");

            // StateSnapshot 是主循环每轮发布的只读副本，此时应当已经同步。
            Assert.Empty(h.Engine.StateSnapshot().Quarantined);
            AssertZipTempClean(h);
        }
        catch
        {
            DumpOnFailure(h);
            throw;
        }
        finally
        {
            await h.Engine.StopAsync();
        }
    }

    [Fact]
    public async Task 引擎已停止时的隔离区操作就地生效()
    {
        // 界面可能在引擎停止后仍然操作隔离区面板。此时没有第二个线程，直接执行即可，
        // 但仍然必须重新发布快照 —— 否则面板上那一条永远不消失。
        Harness h = BuildHarness(maxAttempts: 2);

        try
        {
            h.Monitor.WriteFile("data.dat", 2048);
            Assert.True(await PumpUntilAsync(h, s => s.QuarantineCount > 0));
        }
        catch
        {
            DumpOnFailure(h);
            throw;
        }

        await h.Engine.StopAsync();

        string id = h.Engine.Snapshot.Quarantines[0].Id;
        Assert.True(h.Engine.DiscardQuarantined(id));

        Assert.Equal(0, h.Engine.Snapshot.QuarantineCount);
        Assert.Empty(h.Engine.StateSnapshot().Quarantined);
    }

    // ==================================================================
    //  计划时段
    // ==================================================================

    [Fact]
    public async Task 计划时段之外不打包()
    {
        Harness h = BuildHarness(tweak: s =>
        {
            s.AllDay = false;

            // 用"当前本地时刻的一小时后 → 两小时后"这个区间，无论测试机在哪个时区，
            // 起点 Origin 都必然落在区间之外。
            TimeOnly nowLocal = TimeOnly.FromDateTime(Origin.ToLocalTime().DateTime);
            s.DailyStart = nowLocal.AddHours(1);
            s.DailyEnd = nowLocal.AddHours(2);
        });

        try
        {
            h.Monitor.WriteFile("data.dat", 2048);

            // 只推进几分钟，远不到时段开始。
            await PumpUntilAsync(h, _ => false, maxSteps: 10, engineSecondsPerStep: 10);

            Assert.Equal(EnginePhase.OutsideSchedule, h.Engine.Snapshot.Phase);
            Assert.False(h.Engine.Snapshot.ScheduleActive);
            Assert.Equal(0, h.Engine.Snapshot.BatchFileCount);
            Assert.Equal(0, h.Engine.Snapshot.QuarantineCount);
            AssertZipTempClean(h);
            Assert.True(h.Engine.IsRunning);
        }
        catch
        {
            DumpOnFailure(h);
            throw;
        }
        finally
        {
            await h.Engine.StopAsync();
        }
    }

    [Fact]
    public async Task 进出工作时段各发一条通知且收工带下一次开始时间()
    {
        Harness h = BuildHarness(tweak: s =>
        {
            s.AllDay = false;

            // 用引擎眼里的"本地时间"算区间，不能用 Origin.ToLocalTime()：
            // FakeTimeProvider.LocalTimeZone 默认就是 UTC，所以引擎看到的本地时刻
            // 是 Origin 本身（12:00），而 ToLocalTime() 给的是跑测试这台机器的时区。
            // 两者在 UTC+8 上差 8 小时，区间会整个错开，边沿一次都不会触发。
            TimeOnly engineLocal = TimeOnly.FromDateTime(Origin.UtcDateTime);
            s.DailyStart = engineLocal.AddHours(1);
            s.DailyEnd = engineLocal.AddHours(2);
        });

        try
        {
            // ---- 时段之外：一条边沿通知都不该有 ----
            // 第一轮只是"观察到当前在时段外"，不是"刚刚收工"，所以不发。
            Assert.True(await WaitOneRoundAsync(h), "引擎第一轮没跑完");
            Assert.Equal(0, h.Notify.CountOf(NotifyEvent.ScheduleOpened));
            Assert.Equal(0, h.Notify.CountOf(NotifyEvent.ScheduleClosed));

            // ---- 跨进时段：【开工】 ----
            Assert.True(await AdvanceAndSettleAsync(h, TimeSpan.FromMinutes(61)), "跨入时段后没跑完一轮");

            Assert.Equal(1, h.Notify.CountOf(NotifyEvent.ScheduleOpened));

            string open = h.Notify.First(NotifyEvent.ScheduleOpened).ToPlainText();
            Assert.Contains($"【{NotifyTag.ScheduleOpened}】", open);
            Assert.Contains("已到工作时段", open);
            Assert.Contains("工作时段：每天", open);

            // ---- 跨出时段：【收工】，正文里必须有下一次开始时间 ----
            Assert.True(await AdvanceAndSettleAsync(h, TimeSpan.FromMinutes(61)), "跨出时段后没跑完一轮");

            Assert.Equal(1, h.Notify.CountOf(NotifyEvent.ScheduleClosed));

            string closed = h.Notify.First(NotifyEvent.ScheduleClosed).ToPlainText();
            Assert.Contains($"【{NotifyTag.ScheduleClosed}】", closed);
            Assert.Contains("已过工作时段", closed);
            Assert.Matches(@"下一次的工作开始时间：\d{4}-\d{2}-\d{2} \d{2}:\d{2}", closed);

            // ---- 边沿触发：留在时段外继续跑，不会重复发【收工】 ----
            await PumpUntilAsync(h, _ => false, maxSteps: 3, engineSecondsPerStep: 60);
            Assert.Equal(1, h.Notify.CountOf(NotifyEvent.ScheduleClosed));
            Assert.Equal(1, h.Notify.CountOf(NotifyEvent.ScheduleOpened));
        }
        catch
        {
            DumpOnFailure(h);
            throw;
        }
        finally
        {
            await h.Engine.StopAsync();
        }
    }

    // ==================================================================
    //  首次运行水位线
    // ==================================================================

    [Fact]
    public async Task 默认不处理监控目录里已有的历史文件()
    {
        // 指向一个已经堆了文件的目录时，默认不该把它们全部打包 —— 那可能是几万个文件。
        // 这就是 ProcessExistingFilesOnFirstRun 默认为 false 的含义（旧版没有这个概念）。
        TempDir monitor = NewDir("naz-existing");

        // 修改时间明确早于引擎的起点，与真实时钟无关。
        foreach (string name in new[] { "history1.dat", "history2.dat" })
        {
            string path = monitor.WriteFile(name, 1024);
            File.SetLastWriteTimeUtc(path, Origin.UtcDateTime.AddDays(-30));
        }

        Harness h = BuildHarness(tweak: s =>
        {
            s.MonitorPath = monitor.Path;
            s.ProcessExistingFilesOnFirstRun = false;
        });

        try
        {
            await PumpUntilAsync(h, _ => false, maxSteps: 10);

            Assert.True(h.Log.Contains("水位线"), "启动日志里没有说明水位线");
            Assert.Equal(0, h.Engine.Snapshot.BatchFileCount);
            Assert.Equal(0, h.Engine.Snapshot.TrackedCount);
            Assert.Equal(0, h.Engine.Snapshot.QuarantineCount);
            AssertZipTempClean(h);
        }
        catch
        {
            DumpOnFailure(h);
            throw;
        }
        finally
        {
            await h.Engine.StopAsync();
        }
    }

    [Fact]
    public async Task 勾选了处理已有文件时历史文件会被纳入()
    {
        TempDir monitor = NewDir("naz-existing2");
        string old = monitor.WriteFile("history.dat", 1024);
        File.SetLastWriteTimeUtc(old, Origin.UtcDateTime.AddDays(-30));

        Harness h = BuildHarness(
            maxAttempts: 2,
            tweak: s =>
            {
                s.MonitorPath = monitor.Path;
                s.ProcessExistingFilesOnFirstRun = true;
            });

        try
        {
            Assert.True(
                await PumpUntilAsync(h, s => s.QuarantineCount > 0),
                "勾选了处理已有文件，但历史文件根本没被纳入流程");

            Assert.Contains(
                old,
                h.Engine.Snapshot.Quarantines.SelectMany(q => q.Files),
                StringComparer.OrdinalIgnoreCase);

            AssertZipTempClean(h);
        }
        catch
        {
            DumpOnFailure(h);
            throw;
        }
        finally
        {
            await h.Engine.StopAsync();
        }
    }

    // ==================================================================
    //  回归场景 7：重启后继续等待原归档，而不是重新打包
    // ==================================================================

    [Fact]
    public async Task 场景7_重启后继续等待上次的归档而不是重新打包()
    {
        // 旧版把"待上传"只记在内存里：上传等待期间进程挂掉或被关掉，那个已经生成好的
        // 压缩包就没人认领了 —— 源文件还在，于是下一轮又完整打一次包。
        // 新版把待上传清单写进 state.json，启动时接着等。
        TempDir zipTemp = NewDir("naz-resume-tmp");

        // 上一次进程留下的产物：包已经打好、已经投递，正在等 OneDrive 上传。
        string archive = zipTemp.WriteFile("Backup_20260901_120000.7z", 8192);

        Harness h = BuildHarness(
            maxAttempts: 3,
            tweak: s =>
            {
                s.ZipTempPath = zipTemp.Path;

                // 关掉新文件的来源，确保观察到的一切都只可能来自恢复出来的状态。
                s.ProcessExistingFilesOnFirstRun = false;
            },
            seedState: st => st.PendingUploads.Add(new PendingUpload
            {
                ArchivePath = archive,
                Bytes = 8192,
                FileCount = 3,
                DeliveredUtc = Origin.AddMinutes(-10),
                ReleaseRequested = true,
                Polls = 4,
            }));

        try
        {
            // 还没上传完 —— 引擎应该接手继续等。
            h.Uploads.Status = UploadStatus.Pending;

            Assert.True(await WaitOneRoundAsync(h), "引擎没能跑完第一轮");

            EngineSnapshot resumed = h.Engine.Snapshot;

            Assert.Equal(1, resumed.PendingUploadCount);
            Assert.Equal(
                Path.GetFileName(archive),
                Assert.Single(resumed.PendingUploads).FileName);

            // 关键：接手的是原来那个包，不是新打的。计数器必须还是 0。
            Assert.Equal(0, resumed.ArchivesCreated);
            Assert.Empty(zipTemp.Files("*.part"));
            Assert.Single(zipTemp.Files("*.7z"));

            // 之前已经请求过脱水，这个事实也要一起恢复，不能重复请求。
            Assert.True(Assert.Single(resumed.PendingUploads).ReleaseRequested);

            // 上传真的完成后，待上传项应该正常清掉。
            h.Uploads.Status = UploadStatus.Completed;

            Assert.True(
                await PumpUntilAsync(h, s => s.PendingUploadCount == 0),
                "上传已完成，但待上传项没有被清理");

            // 全程一个新包都不该出现。
            Assert.Equal(0, h.Engine.Snapshot.ArchivesCreated);
            Assert.Empty(zipTemp.Files("*.part"));
            Assert.Empty(zipTemp.Files("*.list"));
        }
        catch
        {
            DumpOnFailure(h);
            throw;
        }
        finally
        {
            await h.Engine.StopAsync();
        }
    }

    [Fact]
    public async Task 场景7b_待上传的归档在重启后不会被配额清掉()
    {
        // 配额淘汰按"最旧优先"，而恢复出来的待上传包恰恰是最旧的那个。
        // 如果配额不认它，就会出现"包被删了、还在等它上传"的死局。
        //
        // 注意：文件的修改时间（配额判据）与 DeliveredUtc（上传超时判据）是两件事。
        // 这里要的是"文件很旧、但投递才刚发生"，所以只把 mtime 拨到 90 天前。
        TempDir zipTemp = NewDir("naz-resume-quota");
        string archive = zipTemp.WriteFile("Backup_20260101_000000.7z", 4096);
        File.SetLastWriteTimeUtc(archive, Origin.UtcDateTime.AddDays(-90));

        Harness h = BuildHarness(
            maxAttempts: 3,
            tweak: s =>
            {
                s.ZipTempPath = zipTemp.Path;
                s.ZipTempKeepDays = 1;          // 90 天前的文件，按天数早该被删
                s.ZipTempMaxCount = 1;
                s.ProcessExistingFilesOnFirstRun = false;
            },
            seedState: st => st.PendingUploads.Add(new PendingUpload
            {
                ArchivePath = archive,
                Bytes = 4096,
                FileCount = 1,
                DeliveredUtc = Origin.AddMinutes(-5),   // 还远没到上传超时（默认 120 分钟）
            }));

        try
        {
            h.Uploads.Status = UploadStatus.Pending;

            for (int i = 0; i < 3; i++)
            {
                h.Time.Advance(TimeSpan.FromSeconds(30));
                Assert.True(await WaitOneRoundAsync(h), $"第 {i + 1} 轮没跑完");
            }

            Assert.True(
                File.Exists(archive),
                "待上传的归档被配额清理删掉了 —— 会变成永远等一个不存在的文件");

            Assert.Equal(1, h.Engine.Snapshot.PendingUploadCount);

            // 而且是"明知超期、刻意放过"，不是配额没扫到它。
            // （删不掉之后还会不会被重新考虑，是 ZipTempManager 的职责，
            //   由 ZipTempManagerTests.待上传的归档绝不被配额删除 覆盖。）
            Assert.True(
                h.Log.Contains("仍在等待上传"),
                $"配额没有识别出这是待上传归档。日志：{h.Log.Dump()}");
        }
        catch
        {
            DumpOnFailure(h);
            throw;
        }
        finally
        {
            await h.Engine.StopAsync();
        }
    }

    // ==================================================================
    //  工作时间：生效起始日期同时是"文件准入下限"
    //  用户报告：设了起始日期，那天之后<b>已经存在</b>的文件却检测不到。
    // ==================================================================

    [Fact]
    public async Task 起始日期之后已经存在的文件也会被检测到()
    {
        // 旧行为：首次运行把水位线钉在"程序启动的这一刻"，于是
        // "起始日期之后、程序启动之前"落下的文件被永久挡住 ——
        // 而用户看着界面上那个起始日期，完全想不到还要去勾"首次启动也处理已有文件"。
        TempDir monitor = NewDir("naz-watch-pre");

        string old = monitor.WriteFile("已经躺在这里的备份.bak", 4096);
        File.SetLastWriteTimeUtc(old, Origin.UtcDateTime.AddDays(-5));

        Harness h = BuildHarness(
            maxAttempts: 1,
            tweak: s =>
            {
                s.MonitorPath = monitor.Path;
                s.EffectiveFrom = DateOnly.FromDateTime(Origin.UtcDateTime.AddDays(-12));

                // 关键：这个开关是关的。起始日期本身就应该把那天之后的已有文件放进来。
                s.ProcessExistingFilesOnFirstRun = false;
            });

        try
        {
            // 7za 是故意缺失的，所以"被检测到"的确证就是它一路走到隔离区。
            Assert.True(
                await PumpUntilAsync(h, s => s.QuarantineCount > 0),
                $"起始日期之后已有的文件根本没被拾起。状态：{h.Engine.Snapshot.StatusText}");

            Assert.Contains(
                old,
                h.Engine.Snapshot.Quarantines.SelectMany(q => q.Files),
                StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            DumpOnFailure(h);
            throw;
        }
        finally
        {
            await h.Engine.StopAsync();
        }
    }

    [Fact]
    public async Task 起始日期之前的文件一律不碰()
    {
        TempDir monitor = NewDir("naz-watch-old");

        string tooOld = monitor.WriteFile("太老的备份.bak", 4096);
        File.SetLastWriteTimeUtc(tooOld, Origin.UtcDateTime.AddDays(-12));

        Harness h = BuildHarness(
            maxAttempts: 1,
            tweak: s =>
            {
                s.MonitorPath = monitor.Path;
                s.EffectiveFrom = DateOnly.FromDateTime(Origin.UtcDateTime.AddDays(-5));

                // 连"首次启动也处理已有文件"都打开：起始日期是更精确的那个，必须压住它。
                s.ProcessExistingFilesOnFirstRun = true;
            });

        try
        {
            long before = h.Engine.CompletedRounds;

            // 让引擎眼里的时间走过 4 小时，对账扫描（下限 30 秒）会跑很多遍。
            await PumpUntilAsync(h, _ => false, maxSteps: 16, engineSecondsPerStep: 900);

            Assert.True(
                h.Engine.CompletedRounds > before + 10,
                "引擎并没有真的跑起来，这个测试没有验证到任何东西");

            EngineSnapshot snap = h.Engine.Snapshot;

            Assert.Equal(0, snap.TrackedCount);
            Assert.Equal(0, snap.BatchFileCount);
            Assert.Equal(0, snap.ArchivesCreated);
            Assert.Equal(0, snap.QuarantineCount);
            Assert.Equal(0, snap.RetryCount);
            Assert.False(h.Log.Contains("已就绪"), $"太老的文件仍被判定就绪。日志：{h.Log.Dump()}");

            AssertZipTempClean(h);
            Assert.True(File.Exists(tooOld), "不该处理的文件被动过了");
        }
        catch
        {
            DumpOnFailure(h);
            throw;
        }
        finally
        {
            await h.Engine.StopAsync();
        }
    }

    // ==================================================================
    //  通知：一整轮走成功路径，五条通知必须是用户要求的那个格式
    //  （用真实 7za.exe —— 其余引擎测试全走失败分支，测不到这些正文）
    // ==================================================================

    [Fact]
    public async Task 一轮处理发出开始就绪打包上传结束五条通知且格式与用户样例一致()
    {
        Harness h = BuildHarness(TestBinaries.SevenZip());

        try
        {
            h.Monitor.WriteFile("MT20210608_20260824_180100.bak", 64 * 1024);

            Assert.True(
                await PumpUntilAsync(h, s => s.ArchivesCreated > 0 && s.PendingUploadCount == 0),
                $"没能在时限内跑完一整轮（打包 + 上传确认）。当前状态：{h.Engine.Snapshot.StatusText}");

            // ---- 顺序：第 1 次发现新文件 → 就绪 → 打包 → 上传 → 结束 ----
            int started = h.Notify.IndexOf(NotifyEvent.RoundStarted);
            int stable = h.Notify.IndexOf(NotifyEvent.FilesStable);
            int packed = h.Notify.IndexOf(NotifyEvent.PackCompleted);
            int uploaded = h.Notify.IndexOf(NotifyEvent.UploadCompleted);
            int finished = h.Notify.IndexOf(NotifyEvent.RoundFinished);

            Assert.True(started >= 0, "没有发【第 1 次发现新文件】");
            Assert.True(stable >= 0, "没有发【就绪】");
            Assert.True(started < stable, "【就绪】发在了【第 1 次发现新文件】之前");
            Assert.True(stable < packed, "【打包】发在了【就绪】之前");
            Assert.True(packed < uploaded, "【上传】发在了【打包】之前");
            Assert.True(uploaded < finished, "【结束】没有发在【上传】之后");

            // 【就绪】一个窗口只发一次 —— 用户明确要求"才发一次"。
            Assert.Equal(1, h.Notify.CountOf(NotifyEvent.FilesStable));

            // 一个文件只发现一次，所以这一轮只有一条【第 1 次发现新文件】，没有第 2 次。
            Assert.Equal(1, h.Notify.CountOf(NotifyEvent.RoundStarted));

            // ---- 每条都是"第一行时间、第二行【标签】" ----
            foreach ((NotifyEvent evt, string tag) in new[]
            {
                (NotifyEvent.RoundStarted, NotifyTag.Discovered(1)),
                (NotifyEvent.FilesStable, NotifyTag.FilesStable),
                (NotifyEvent.PackCompleted, NotifyTag.Packed),
                (NotifyEvent.UploadCompleted, NotifyTag.Uploaded),
                (NotifyEvent.RoundFinished, NotifyTag.RoundFinished),
            })
            {
                string[] lines = h.Notify.First(evt).ToPlainText()
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries);

                // 第一行必须是"年月日 + 时分秒"：通知发到手机和聊天窗口之后，
                // 就没有"这条是刚才"这个上下文了，只有 HH:mm:ss 分不清是哪一天。
                Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}$", lines[0].Trim());
                Assert.Equal($"【{tag}】", lines[1].Trim());
            }

            // ---- 【第 1 次发现新文件】：一发现就发，此时还没稳定，所以是"持续观察中" ----
            string startText = h.Notify.First(NotifyEvent.RoundStarted).ToPlainText();
            Assert.Contains("OneDrive：", startText);
            Assert.Contains("检测到有新文件，已进入自动化处理流程", startText);
            Assert.Contains(
                "发现新文件：1. MT20210608_20260824_180100.bak（64.00K），持续观察中......",
                startText);

            // ---- 【就绪】：窗口内全部稳定，正文里必须有"准备开始打包" ----
            string stableText = h.Notify.First(NotifyEvent.FilesStable).ToPlainText();
            Assert.Contains("已全部稳定，不再有变化。", stableText);
            Assert.Contains("源文件总大小：64.00K", stableText);
            Assert.Contains("1. MT20210608_20260824_180100.bak（64.00K）", stableText);
            Assert.Contains("准备开始打包。", stableText);

            // ---- 【打包】：用户样例里的编号清单与两个大小，末尾接上下一步 ----
            string packText = h.Notify.First(NotifyEvent.PackCompleted).ToPlainText();
            Assert.Contains("本次共 1 个文件打包成功", packText);
            Assert.Contains("源文件总大小：64.00K", packText);
            Assert.Contains("文件列表：", packText);
            Assert.Contains("1. MT20210608_20260824_180100.bak（64.00K）", packText);
            Assert.Matches(@"压缩包：Backup_\d{8}_\d{6}\.7z", packText);
            Assert.Contains("压缩包大小：", packText);
            Assert.Contains("准备开始上传至OneDrive。", packText);

            // ---- 【上传】：剪切进目标目录那一刻发，说的是"正在上传"而不是"已完成" ----
            string uploadText = h.Notify.First(NotifyEvent.UploadCompleted).ToPlainText();
            Assert.Contains("正在上传到OneDrive。", uploadText);
            Assert.Matches(@"压缩包：Backup_\d{8}_\d{6}\.7z", uploadText);
            Assert.DoesNotContain("✔", uploadText);   // 这一刻还没传完，不能打勾

            // ---- 【结束】：云端确认之后才发，共计耗时 + 程序所在磁盘 ----
            string endText = h.Notify.First(NotifyEvent.RoundFinished).ToPlainText();
            Assert.Contains("本次自动化处理已结束。", endText);
            Assert.Contains("OneDrive：✔", endText);
            Assert.Matches(@"共计耗时：\d+小时\d+分\d+秒", endText);

            // 窗口至少开了 1 分钟才打包，所以耗时不可能是零 ——
            // 这一条同时证明 RoundStartedUtc 真的被记下并用上了，而不是拿"现在"减"现在"。
            Assert.DoesNotContain("共计耗时：0小时0分0秒", endText);
            Assert.Contains("程序所在磁盘", endText);

            // 用户要求：【结束】的<b>最后一行</b>是"下一次的工作开始时间"。
            // 这个夹具是全天模式且此刻仍在时段内 —— 那时候印一个钟点是假的
            // （程序马上就接着处理新文件），所以说的是"一发现新文件就立刻开始下一轮"。
            string lastLine = endText
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)[^1]
                .Trim();

            Assert.StartsWith("下一次的工作开始时间：", lastLine);
            Assert.Contains("全天监控", lastLine);

            // 全天模式没有"进出工作时段"这回事，这两条一条都不该发。
            Assert.Equal(0, h.Notify.CountOf(NotifyEvent.ScheduleOpened));
            Assert.Equal(0, h.Notify.CountOf(NotifyEvent.ScheduleClosed));

            // ---- 归档真的落在 OneDrive 目录里，名字没有多余后缀 ----
            string[] delivered = h.OneDrive.Files("*.7z");
            Assert.Single(delivered);
            Assert.Matches(@"^Backup_\d{8}_\d{6}\.7z$", Path.GetFileName(delivered[0]));

            AssertZipTempClean(h);
        }
        catch
        {
            DumpOnFailure(h);
            throw;
        }
        finally
        {
            await h.Engine.StopAsync();
        }
    }

    // ==================================================================
    //  通知：发现新文件只通报一次，标签数的是"第几次通报"
    //
    //  用户报告的原文：同一个文件先来一条【开始】…（0B），紧接着又来一条
    //  【开始】…（9.85K）。两条时间戳一样，只有大小不同。
    //  成因不是重复扫描，而是 Excel / WPS 的"写临时文件 + 替换原文件"——
    //  替换那一瞬间路径真的不存在，StabilityTracker.Refresh 按 !Exists 把它剔掉，
    //  下一个 watcher 事件又把它当新文件重新登记。
    //  剔除这一步不能去掉（它正是修掉"静默假死"的那一步），所以去重记在通知一侧。
    //
    //  两个编号是两回事，别混：
    //    * 标签的 N —— 本轮<b>第几条</b>这种通知，一条 +1，与它带了几个文件无关；
    //    * 正文的 1. 2. 3. —— <b>每条通知各自从 1 数</b>，只列这一条带来的文件。
    //  用户的原话："正文内容的序号……和历史发现的数量没有关系，仅当前发现的文件数量"；
    //  "若同一时间内新增了 2 个文件，此前已经是第 3 次，那么此时应该是第 4 次而非第 5 次"。
    //
    //  这几个测试<b>故意把静默期拉满</b>：文件永远不会就绪，也就永远不会打包，
    //  于是 _announced / _discoveries 不会被"一轮结束"清掉，测的就是同一轮内的行为。
    // ==================================================================

    /// <summary>观察期里的文件被"替换式保存"踢出去又回来，不算新文件，不再发通知。</summary>
    private static Action<AppSettings> NeverReady() => s =>
    {
        s.QuietSeconds = SettingsLimits.QuietSecondsMax;    // 静默期永不满足 → 永远不就绪
        s.BatchWindowMinutes = 600;                          // 窗口也不会到期 → 永远不打包
    };
    [Fact]
    public async Task 同一个文件被替换式保存后重新出现只通报一次()
    {
        Harness h = BuildHarness(tweak: NeverReady());

        try
        {
            const string name = "新建 XLSX 工作表.xlsx";
            string path = h.Monitor.WriteFile(name, 0);

            Assert.True(
                await PumpUntilAsync(h, s => s.TrackedCount == 1, maxSteps: 10, engineSecondsPerStep: 31),
                $"引擎没发现这个文件。状态：{h.Engine.Snapshot.StatusText}");

            Assert.Equal(1, h.Notify.CountOf(NotifyEvent.RoundStarted));
            Assert.Equal("第 1 次发现新文件", h.Notify.First(NotifyEvent.RoundStarted).Tag);
            Assert.Contains(
                $"发现新文件：1. {name}（0B），持续观察中......",
                h.Notify.First(NotifyEvent.RoundStarted).ToPlainText());

            // 替换保存的那一瞬间：路径消失，跟踪表把它剔掉。
            File.Delete(path);

            Assert.True(
                await PumpUntilAsync(h, s => s.TrackedCount == 0, maxSteps: 10, engineSecondsPerStep: 31),
                $"文件删掉了却还留在跟踪表里。状态：{h.Engine.Snapshot.StatusText}");

            // 同一个路径带着新的大小回来 —— 用户报告里的 0B → 9.85K 就是这一步。
            h.Monitor.WriteFile(name, 10086);

            Assert.True(
                await PumpUntilAsync(h, s => s.TrackedCount == 1, maxSteps: 10, engineSecondsPerStep: 31),
                $"重新出现的文件没有被重新纳入跟踪。状态：{h.Engine.Snapshot.StatusText}");

            // 跟踪照旧（它必须重新被观察，否则就轮到"静默假死"了），
            // 但通知<b>一条都没有多</b>：这不是新文件，大小变化也不是事件。
            Assert.Equal(1, h.Notify.CountOf(NotifyEvent.RoundStarted));
            Assert.DoesNotContain("9.85K", h.Notify.First(NotifyEvent.RoundStarted).ToPlainText());
            Assert.Equal(0, h.Notify.CountOf(NotifyEvent.FilesStable));
        }
        catch
        {
            DumpOnFailure(h);
            throw;
        }
        finally
        {
            await h.Engine.StopAsync();
        }
    }

    [Fact]
    public async Task 陆续发现的文件标签逐条递增而正文序号每条都从1数()
    {
        Harness h = BuildHarness(tweak: NeverReady());

        try
        {
            h.Monitor.WriteFile("先来的.dat", 1024);

            Assert.True(
                await PumpUntilAsync(h, s => s.TrackedCount == 1, maxSteps: 10, engineSecondsPerStep: 31),
                $"第一个文件没被发现。状态：{h.Engine.Snapshot.StatusText}");

            Assert.Equal(1, h.Notify.CountOf(NotifyEvent.RoundStarted));

            // 第一条已经发出去了，这时候才写第二个文件 —— 两次发现必然落在不同的轮里。
            h.Monitor.WriteFile("后来的.dat", 2048);

            Assert.True(
                await PumpUntilAsync(h, s => s.TrackedCount == 2, maxSteps: 10, engineSecondsPerStep: 31),
                $"第二个文件没被发现。状态：{h.Engine.Snapshot.StatusText}");

            NotifyMessage[] discoveries =
                [.. h.Notify.Sent.Where(m => m.Event == NotifyEvent.RoundStarted)];

            Assert.Equal(2, discoveries.Length);

            // 标签：一条通知 +1。
            Assert.Equal("第 1 次发现新文件", discoveries[0].Tag);
            Assert.Equal("第 2 次发现新文件", discoveries[1].Tag);

            // 正文：每条各自从 1 数，且只列这一条带来的那个文件。
            Assert.Contains("发现新文件：1. 先来的.dat（1.00K）", discoveries[0].ToPlainText());
            Assert.Contains("发现新文件：1. 后来的.dat（2.00K）", discoveries[1].ToPlainText());

            // 第二条不该出现"2."，也不该把第一条的文件再列一遍 ——
            // 用户的要求是"序号和历史发现的数量没有关系，仅当前发现的文件数量"。
            Assert.DoesNotContain("发现新文件：2.", discoveries[1].ToPlainText());
            Assert.DoesNotContain("先来的.dat", discoveries[1].ToPlainText());
        }
        catch
        {
            DumpOnFailure(h);
            throw;
        }
        finally
        {
            await h.Engine.StopAsync();
        }
    }

    [Fact]
    public async Task 同一次发现两个文件只算一次通报且正文数到2()
    {
        // 用户的原话：若同一时间内新增了 2 个文件，此前已经是第 3 次发现新增文件，
        // 那么此时应该是第 4 次发现文件，而非第 5 次。
        //
        // 两个文件必须<b>确定地</b>落在同一次发现里，所以它们在 Start 之前就已经在目录里
        //（那时 watcher 还没启用，一个事件都不会产生），只能由启动时那一次 Reconcile 一起纳入。
        Harness h = BuildHarness(
            tweak: NeverReady(),
            seedMonitor: dir =>
            {
                dir.WriteFile("同时来的甲.dat", 1024);
                dir.WriteFile("同时来的乙.dat", 2048);
            });

        try
        {
            Assert.True(
                await PumpUntilAsync(h, s => s.TrackedCount == 2, maxSteps: 10, engineSecondsPerStep: 31),
                $"两个预置文件没被一起发现。状态：{h.Engine.Snapshot.StatusText}");

            // 关键断言：2 个文件 → 只有 1 条通知，标签停在【第 1 次】而不是【第 2 次】。
            Assert.Equal(1, h.Notify.CountOf(NotifyEvent.RoundStarted));

            NotifyMessage first = h.Notify.First(NotifyEvent.RoundStarted);
            Assert.Equal("第 1 次发现新文件", first.Tag);

            // 正文里两个都在，序号 1 和 2 —— AnnounceDiscoveryAsync 不排序，所以不假设先后。
            string text = first.ToPlainText();

            Assert.Contains("同时来的甲.dat（1.00K）", text);
            Assert.Contains("同时来的乙.dat（2.00K）", text);
            Assert.Contains("发现新文件：1. ", text);
            Assert.Contains("发现新文件：2. ", text);
            Assert.DoesNotContain("发现新文件：3. ", text);

            // 再来一个新文件：编号只 +1（第 2 次），正文又从 1 数起。
            h.Monitor.WriteFile("后来的丙.dat", 4096);

            Assert.True(
                await PumpUntilAsync(h, s => s.TrackedCount == 3, maxSteps: 10, engineSecondsPerStep: 31),
                $"第三个文件没被发现。状态：{h.Engine.Snapshot.StatusText}");

            NotifyMessage[] discoveries =
                [.. h.Notify.Sent.Where(m => m.Event == NotifyEvent.RoundStarted)];

            Assert.Equal(2, discoveries.Length);

            // 按文件数算这里会是【第 3 次】—— 那正是用户明确否掉的那个算法。
            Assert.Equal("第 2 次发现新文件", discoveries[1].Tag);
            Assert.Contains("发现新文件：1. 后来的丙.dat（4.00K）", discoveries[1].ToPlainText());
            Assert.DoesNotContain("发现新文件：2.", discoveries[1].ToPlainText());
        }
        catch
        {
            DumpOnFailure(h);
            throw;
        }
        finally
        {
            await h.Engine.StopAsync();
        }
    }

    // ==================================================================
    //  通知：非 OneDrive 模式下，流程与消息里不出现"云盘"
    //
    //  用户要求原文：非 OneDrive 云盘的时候，流程和消息不要再出现"云盘"字样，
    //  改为"准备移动到指定目录。""✔ 压缩包已移入指定目录，本次自动化处理已结束。"，
    //  并且把"后续上传由云盘客户端自行完成…"那段删掉。
    //
    //  为什么这么改：那个目录后面可能压根没有云盘（普通目录、映射的 NAS 盘），
    //  而程序对客户端做过什么一无所知 —— 说"云盘"就是在替它背书。
    // ==================================================================

    [Fact]
    public async Task 非OneDrive模式的通知只说指定目录且不提云盘()
    {
        Harness h = BuildHarness(
            TestBinaries.SevenZip(),
            tweak: s => s.CloudTarget = CloudTarget.Folder);

        try
        {
            h.Monitor.WriteFile("MT20210608_20260824_180100.bak", 64 * 1024);

            Assert.True(
                await PumpUntilAsync(h, s => s.ArchivesCreated > 0),
                $"没能在时限内跑完一整轮。当前状态：{h.Engine.Snapshot.StatusText}");

            // 剪切进目录就是终点，所以没有【上传】这一步，也没有"等云端确认"。
            Assert.Equal(0, h.Notify.CountOf(NotifyEvent.UploadCompleted));
            Assert.Equal(1, h.Notify.CountOf(NotifyEvent.RoundFinished));

            string packText = h.Notify.First(NotifyEvent.PackCompleted).ToPlainText();
            Assert.Contains("准备移动到指定目录。", packText);
            Assert.DoesNotContain("OneDrive", packText);

            string endText = h.Notify.First(NotifyEvent.RoundFinished).ToPlainText();

            Assert.Contains("✔ 压缩包已移入指定目录，本次自动化处理已结束。", endText);
            Assert.Contains("所在目录：", endText);
            Assert.Matches(@"共计耗时：\d+小时\d+分\d+秒", endText);

            // 删掉的那段话，一个字都不该留下。
            Assert.DoesNotContain("后续上传", endText);
            Assert.DoesNotContain("不再等待", endText);

            // 这一条是用户要求的核心：整条流程的消息里不出现"云盘"，也不冒出 OneDrive。
            foreach (NotifyMessage m in h.Notify.Sent)
            {
                string text = m.ToPlainText();
                Assert.DoesNotContain("云盘", text);
                Assert.DoesNotContain("OneDrive", text);
            }

            // 最后一行仍然是"下一次的工作开始时间"—— 两条【结束】分支都要有。
            string lastLine = endText
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)[^1]
                .Trim();

            Assert.StartsWith("下一次的工作开始时间：", lastLine);
            Assert.Contains("全天监控", lastLine);

            // 归档真的落在指定目录里了。
            Assert.Single(h.OneDrive.Files("*.7z"));
            AssertZipTempClean(h);
        }
        catch
        {
            DumpOnFailure(h);
            throw;
        }
        finally
        {
            await h.Engine.StopAsync();
        }
    }
}
