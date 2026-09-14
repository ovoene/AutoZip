using Microsoft.Extensions.Time.Testing;
using NewAutoZip.Core.Configuration;
using NewAutoZip.Core.Notifications;
using NewAutoZip.Core.OneDrive;
using NewAutoZip.Core.Packing;
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
    /// —— 回归场景 7（重启恢复）就靠它。它在 <paramref name="seedMonitor"/> <b>之后</b>执行，
    /// 因为水位线的例外名单是按路径记的，种状态时得先知道文件的真实路径。
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
        Action<TempDir>? seedMonitor = null,
        IFileProbe? probe = null)
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

        // 先放文件、再种状态、最后 Start。
        //
        // 文件必须在 Start 之前放好：那时 watcher 还没 EnableRaisingEvents，一个事件都不会产生，
        // 这些文件只能由启动时的那一次 Reconcile 一起发现 —— 于是"同一时刻进来 N 个文件"是确定的。
        // 状态排在文件之后，是因为水位线的两份例外名单是<b>按路径</b>记的，
        // 种状态的闭包得先知道临时目录里那个文件的真实路径。
        seedMonitor?.Invoke(monitor);

        if (seedState is not null)
        {
            EngineState seeded = store.Load();
            seedState(seeded);
            store.Save(seeded);
        }

        BackupEngine engine = new(log, time, probe ?? Win32FileProbe.Instance, store);

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

    // ==================================================================
    //  静默丢文件的三个洞
    //
    //  根因是同一个：水位线 Checkpoint 是<b>一个标量</b>，只能表达"这一刻之前都处理过了"，
    //  表达不了"这一刻之前都处理过了，除了 F"。三处后果各不相同：
    //    1. 7za 跳过的文件（退出码 1 = 成功但有警告）被水位线一并跨过 → 永久没备份；
    //    2. 连续 240 轮打不开被放弃跟踪的文件，之后再也没人捡它；
    //    3. 一个未来时间戳把水位线顶到 2030，此后所有文件都被挡住。
    //  三处都是静默的：不重试、不隔离、不通知，日志里也看不出所以然。
    // ==================================================================

    [Fact]
    public async Task 洞3_水位线落在未来时启动即清空且已有文件照样被发现()
    {
        Harness h = BuildHarness(
            tweak: NeverReady(),
            seedMonitor: d => d.WriteFile("normal.dat", 4096),
            seedState: s =>
            {
                s.FirstRunCompleted = true;
                s.Checkpoint = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
            });

        try
        {
            Assert.True(
                await PumpUntilAsync(h, s => s.TrackedCount == 1, maxSteps: 10, engineSecondsPerStep: 6),
                $"水位线清空之后这个文件应当被发现。状态：{h.Engine.Snapshot.StatusText}");

            // 治法是清空，不是改成"现在"：改成"现在"会把监控目录里已有的文件一并跨过去，
            // 那只是把静默丢失搬了个地方。
            Assert.Null(h.Store.Load().Checkpoint);
            Assert.Contains("水位线落在未来", h.Log.Dump());
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
    /// 水位线只超前几分钟（系统时钟被回拨）时压回当前时刻，而不是整段清空。
    /// 清空会把监控目录里的老文件全部重打一遍；原样留着则会静默挡掉接下来那几分钟里的每个文件。
    /// </summary>
    [Fact]
    public async Task 洞3_水位线只略微超前时压回当前时刻而不是清空()
    {
        DateTimeOffset before = DateTimeOffset.UtcNow;

        Harness h = BuildHarness(
            tweak: NeverReady(),
            seedMonitor: d =>
            {
                // 修改时间正好落在"被超前的水位线挡住"的那几分钟里。
                string full = d.WriteFile("inside-the-skew.dat", 4096);
                File.SetLastWriteTimeUtc(full, DateTime.UtcNow.AddMinutes(1));
            },
            seedState: s =>
            {
                s.FirstRunCompleted = true;
                s.Checkpoint = DateTimeOffset.UtcNow.AddMinutes(2);
            });

        try
        {
            Assert.True(
                await PumpUntilAsync(h, s => s.TrackedCount == 1, maxSteps: 10, engineSecondsPerStep: 6),
                $"压回水位线之后这个文件应当被发现。状态：{h.Engine.Snapshot.StatusText}");

            DateTimeOffset? after = h.Store.Load().Checkpoint;

            // 压回而不是清空：老文件仍然算处理过，不会被重打一遍。
            Assert.NotNull(after);
            Assert.InRange(after!.Value, before, DateTimeOffset.UtcNow);
            Assert.Contains("已压回当前时刻", h.Log.Dump());
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
    /// 例外名单能推翻水位线。<paramref name="forced"/> = false 的那一档是对照组：
    /// 没有名单，同一个文件就被水位线永久挡在门外 —— 这正是洞 1 与洞 2 的后果。
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task 洞12_例外名单里的文件即使被水位线盖住也会重新纳入处理(bool forced)
    {
        string? full = null;

        Harness h = BuildHarness(
            tweak: NeverReady(),
            seedMonitor: d =>
            {
                full = d.WriteFile("skipped-by-7za.dat", 4096);

                // 文件比水位线更老 —— 光看水位线，它已经"处理过了"。
                File.SetLastWriteTimeUtc(full, DateTime.UtcNow.AddHours(-2));
            },
            seedState: s =>
            {
                s.FirstRunCompleted = true;
                s.Checkpoint = DateTimeOffset.UtcNow.AddHours(-1);

                if (forced)
                {
                    s.ForcedFiles.Add(new ForcedFile
                    {
                        Path = full!,
                        Reason = "打包时被其他进程独占，7za 已跳过",
                        AddedUtc = DateTimeOffset.UtcNow.AddHours(-1),
                    });
                }
            });

        try
        {
            bool discovered = await PumpUntilAsync(
                h, s => s.TrackedCount == 1, maxSteps: 12, engineSecondsPerStep: 6);

            Assert.Equal(forced, discovered);
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
    /// 未来时间戳的账是按 (路径, 修改时间) 记的，不是按路径记的。
    ///
    /// 只按路径记会走到另一个极端：文件被真正重写之后也再不受理，又是一次静默丢失。
    /// <paramref name="sameStamp"/> = false 那一档就是在验这个自愈：时间戳一变即放行。
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task 洞3_未来时间戳按路径加修改时间记账时间戳变了就重新放行(bool sameStamp)
    {
        string? full = null;
        DateTime written = default;

        Harness h = BuildHarness(
            tweak: NeverReady(),
            seedMonitor: d =>
            {
                full = d.WriteFile("stamped-in-2030.dat", 4096);

                // 修改时间在未来：水位线推不到这里（会被截断到当前时刻），
                // 所以挡住它的只可能是这份名单，不会是水位线。
                File.SetLastWriteTimeUtc(full, DateTime.UtcNow.AddDays(30));
                written = File.GetLastWriteTimeUtc(full);
            },
            seedState: s =>
            {
                s.FirstRunCompleted = true;
                s.FutureStamped.Add(new FutureStampedFile
                {
                    Path = full!,
                    LastWriteUtc = new DateTimeOffset(
                        sameStamp ? written : written.AddSeconds(-1), TimeSpan.Zero),
                });
            });

        try
        {
            bool discovered = await PumpUntilAsync(
                h, s => s.TrackedCount == 1, maxSteps: 12, engineSecondsPerStep: 6);

            Assert.Equal(!sameStamp, discovered);
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
    /// 洞 2：连续 240 轮打不开而被放弃跟踪的文件，必须留下一笔账。
    ///
    /// 只用假探测器就能确定性复现"一直被独占"，不必真去抢一个 20 分钟的文件锁。
    /// 240 是跟踪器里写死的次数，一轮一次探测，所以这里得实打实地转 240 圈。
    /// </summary>
    [Fact]
    public async Task 洞2_持续读不到而被放弃跟踪的文件会记入待重新处理名单()
    {
        FakeFileProbe probe = new();
        string? full = null;

        Harness h = BuildHarness(
            seedMonitor: d =>
            {
                // 真实文件也要有：PassesCheckpoint 与名单清理读的是真磁盘。
                full = d.WriteFile("locked-forever.dat", 4096);
                probe.Set(full, 4096, Origin, readable: false);
            },
            probe: probe);

        try
        {
            EngineState state = h.Store.Load();

            for (int i = 0; i < 300 && state.ForcedFiles.Count == 0; i++)
            {
                h.Time.Advance(TimeSpan.FromSeconds(5));

                Assert.True(await WaitOneRoundAsync(h), "引擎在第 " + i + " 轮卡住了");

                state = h.Store.Load();
            }

            Assert.Contains(
                state.ForcedFiles,
                f => string.Equals(f.Path, full, StringComparison.OrdinalIgnoreCase));

            Assert.Contains("至今无法读取", h.Log.Dump());

            // 放弃跟踪不等于放弃这个文件：它必须还能被对账扫描重新捡起来。
            Assert.True(
                await PumpUntilAsync(h, s => s.TrackedCount == 1, maxSteps: 12, engineSecondsPerStep: 6),
                "记了账就该被重新纳入观察");
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
    /// "忽略"这个动作必须把该批次在待重新处理名单上的账一并销掉。
    ///
    /// 否则会造出一类怎么点都关不掉的文件：批次没了，名单还在，
    /// 每轮对账都把它重新捞回来 —— 用户点了"忽略"，界面上却没完。
    /// </summary>
    [Fact]
    public async Task 忽略隔离批次会同时销掉它在待重新处理名单上的账()
    {
        string? full = null;

        Harness h = BuildHarness(
            tweak: NeverReady(),
            seedMonitor: d => { full = d.WriteFile("quarantined.dat", 4096); },
            seedState: s =>
            {
                s.FirstRunCompleted = true;
                s.Quarantined.Add(new QuarantinedBatch
                {
                    Id = "B-1",
                    Files = [full!],
                    Reason = "测试用：连续打包失败",
                    Attempts = 3,
                    QuarantinedUtc = Origin,
                    TotalBytes = 4096,
                });
                s.ForcedFiles.Add(new ForcedFile
                {
                    Path = full!,
                    Reason = "打包时被其他进程独占，7za 已跳过",
                    AddedUtc = Origin,
                });
            });

        try
        {
            // Enqueue 是按界面看到的那份快照校验批次是否存在的，得先让它发布出来。
            Assert.True(
                await PumpUntilAsync(h, s => s.Quarantines.Count == 1, maxSteps: 6, engineSecondsPerStep: 6),
                "种进去的隔离批次没有出现在快照里");

            Assert.True(h.Engine.DiscardQuarantined("B-1"));

            Assert.True(
                await PumpUntilAsync(h, s => s.Quarantines.Count == 0, maxSteps: 6, engineSecondsPerStep: 6),
                "忽略命令没有被执行");

            EngineState after = h.Store.Load();
            Assert.Empty(after.Quarantined);
            Assert.Empty(after.ForcedFiles);
            Assert.Contains("已忽略隔离批次 B-1", h.Log.Dump());
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
    /// 洞 1 的完整现场，用真 7za 跑：三个文件，中间那个能读，两头的被独占锁住。
    /// 7za 跳过锁住的两个、以退出码 1（成功但有警告）收尾，归档本身完好。
    ///
    /// 修复前这一下会丢两个文件，而且是两种不同的丢法，缺一不可：
    ///   · newer 比进包的文件<b>新</b> —— 旧版把整批清单丢给水位线，水位线一步推到 -5 分，
    ///     它从此再也过不了闸门；
    ///   · older 比进包的文件<b>老</b> —— 水位线合法地盖住了它，光靠水位线怎么都救不回来，
    ///     只能靠例外名单。
    /// 所以两半都得钉住：水位线不许越过被跳过的文件，被跳过的文件必须留下账。
    /// </summary>
    [Fact]
    public async Task 洞1_7za跳过的文件不会被水位线跨过且锁一松开就补打一包()
    {
        Harness h = BuildHarness(
            TestBinaries.SevenZip(),
            seedMonitor: d =>
            {
                // 修改时间必须显式拉开：水位线是按修改时间比的，
                // 三个文件同一秒写出来就分不出"跨过去"和"没跨过去"。
                DateTime now = DateTime.UtcNow;
                File.SetLastWriteTimeUtc(d.WriteFile("older.bak", 64 * 1024), now.AddMinutes(-15));
                File.SetLastWriteTimeUtc(d.WriteFile("middle.bak", 64 * 1024), now.AddMinutes(-10));
                File.SetLastWriteTimeUtc(d.WriteFile("newer.bak", 64 * 1024), now.AddMinutes(-5));
            });

        string older = h.Monitor.File("older.bak");
        string middle = h.Monitor.File("middle.bak");
        string newer = h.Monitor.File("newer.bak");

        FileStream? holdOlder = null;
        FileStream? holdNewer = null;

        try
        {
            Assert.True(
                await PumpUntilAsync(h, s => s.BatchFileCount == 3, maxSteps: 12, engineSecondsPerStep: 6),
                $"三个文件没能一起进批次。当前状态：{h.Engine.Snapshot.StatusText}");

            // FileShare.None 连 7za 的 -ssw 都绕不过去，这是"文件被别的进程占着"的真实现场。
            holdOlder = new FileStream(older, FileMode.Open, FileAccess.Read, FileShare.None);
            holdNewer = new FileStream(newer, FileMode.Open, FileAccess.Read, FileShare.None);

            Assert.True(
                await PumpUntilAsync(h, s => s.ArchivesCreated > 0 && s.PendingUploadCount == 0),
                $"没能跑完第一轮（打包 + 上传确认）。当前状态：{h.Engine.Snapshot.StatusText}");

            EngineState mid = h.Store.Load();

            // ---- 水位线只推到真正进包的 middle，没有跨过被跳过的 newer ----
            Assert.Equal(
                new DateTimeOffset(File.GetLastWriteTimeUtc(middle), TimeSpan.Zero),
                mid.Checkpoint);

            // ---- 被跳过的两个都留下了账：一个在水位线之前，一个在水位线之后 ----
            Assert.Equal(2, mid.ForcedFiles.Count);
            Assert.Contains(
                mid.ForcedFiles,
                f => string.Equals(f.Path, older, StringComparison.OrdinalIgnoreCase));
            Assert.Contains(
                mid.ForcedFiles,
                f => string.Equals(f.Path, newer, StringComparison.OrdinalIgnoreCase));

            // 通知如实写了跳过几个，而不是拿清单条数虚报成 3 个。
            Assert.Contains(
                "本次共 1 个文件打包成功，跳过 2 个（被占用或已消失）。",
                h.Notify.First(NotifyEvent.PackCompleted).ToPlainText());

            Assert.Single(h.OneDrive.Files("*.7z"));

            // ---- 锁一松开就得补上：这是"名单自愈"那一半 ----
            holdOlder.Dispose();
            holdNewer.Dispose();

            Assert.True(
                await PumpUntilAsync(h, s => s.ArchivesCreated > 1 && s.PendingUploadCount == 0),
                $"锁松开之后没有补打第二个包。当前状态：{h.Engine.Snapshot.StatusText}");

            EngineState done = h.Store.Load();

            // 两笔账都销了 —— 名单不会一直攒着。
            Assert.Empty(done.ForcedFiles);

            // newer 这一轮真进包了，水位线这才允许推到它。
            Assert.Equal(
                new DateTimeOffset(File.GetLastWriteTimeUtc(newer), TimeSpan.Zero),
                done.Checkpoint);

            // 全程没有一个未来时间戳，那份名单就该一条都不长。
            Assert.Empty(done.FutureStamped);

            Assert.Equal(2, h.OneDrive.Files("*.7z").Length);
            AssertZipTempClean(h);
        }
        catch
        {
            DumpOnFailure(h);
            throw;
        }
        finally
        {
            holdOlder?.Dispose();
            holdNewer?.Dispose();
            await h.Engine.StopAsync();
        }
    }

    // ==================================================================
    //  云端恢复演练：到期才做，做完把计时器推进去
    //
    //  这是本地演练证明不了的那一半 —— 包传上去之后有没有在传输或存储中损坏。
    //  本地演练验的包从没离开过这台机器。
    // ==================================================================

    /// <summary>
    /// 在指定目录里造一个<b>真的能解开</b>的归档，密码用 harness 的那个。
    ///
    /// 源文件放在监控目录<b>外面</b>：放进去会被 watcher 收进批次，
    /// 于是引擎忙着打包，而云端演练只在真正空闲时才做 —— 测试就永远等不到它。
    /// </summary>
    private async Task<string> MakeCloudArchiveAsync(Harness h, string name)
    {
        TempDir source = NewDir("naz-clouddrill-src");
        TempDir manifestDir = NewDir("naz-clouddrill-mf");

        // 内容各不相同：全零内容下逐文件校验值比对等于没比。
        List<string> files =
        [
            source.WriteText("甲.dat", "第一个文件的内容 " + Guid.NewGuid()),
            source.WriteText("乙.dat", "第二个文件的内容 " + Guid.NewGuid()),
        ];

        RecordingLogger packLog = new();
        SevenZipRunner runner = new(TestBinaries.SevenZip(), packLog);

        // 带上包内清单，演练才能逐条核对而不是"解开了就算数"。
        ArchiveManifestDocument doc = await ArchiveManifest.BuildAsync(
            files, source.Path, name, Origin, h.Settings, packLog, CancellationToken.None);

        // 清单中转文件不能落在 ZipTemp：引擎启动时的清扫会按名字把它删掉。
        string manifestPath = Path.Combine(manifestDir.Path, ArchiveManifest.EntryName);
        ArchiveManifest.WriteInnerManifest(manifestPath, doc);

        string output = Path.Combine(h.OneDrive.Path, name);

        PackResult result = await runner.CreateAsync(
            new PackRequest(
                output, files, h.Settings.Password,
                CompressionLevel: 1, EncryptFileNames: true, TimeSpan.FromMinutes(3))
            {
                ExtraFiles = [manifestPath],
            },
            progress: null,
            CancellationToken.None);

        Assert.True(result.Ok, $"测试自己的准备工作失败了（造不出归档）：{result.Error}");

        return output;
    }

    [Fact]
    public async Task 云端演练没到期不做_到期才做并推进计时器()
    {
        // 计时器必须真的管用：做得太勤会把云端的包反复下载回来（按流量计费的线路上是持续开销），
        // 而根本不推进计时器则会变成"每一轮都演练一次"。
        Harness h = BuildHarness(
            TestBinaries.SevenZip(),
            tweak: s =>
            {
                s.CloudDrillEnabled = true;
                s.CloudDrillIntervalHours = 6;

                // 本轮不打包，所以本地演练无关；关掉它避免干扰日志断言。
                s.VerifyAfterPackByExtract = false;
            },
            // 种一个"刚刚演练过"的时间点，否则从没演练过时引擎会立刻做一次。
            seedState: st => st.LastCloudDrillUtc = Origin);

        try
        {
            await MakeCloudArchiveAsync(h, "Backup_cloud.7z");

            // ---- 还没到 6 小时：一次都不许做 ----
            Assert.True(await AdvanceAndSettleAsync(h, TimeSpan.FromHours(1)));

            Assert.False(
                h.Log.Contains("开始云端恢复演练"),
                $"离到期还有 5 小时就开演了。日志：{h.Log.Dump()}");
            Assert.Equal(Origin, h.Store.Load().LastCloudDrillUtc);

            // ---- 推过 6 小时：这一轮必须做 ----
            h.Time.Advance(TimeSpan.FromHours(6));

            Assert.True(
                await PumpUntilAsync(h, _ => h.Store.Load().LastCloudDrillUtc > Origin),
                $"到期了却没做云端演练。日志：{h.Log.Dump()}");

            Assert.True(h.Log.Contains("开始云端恢复演练"), h.Log.Dump());

            EngineState after = h.Store.Load();

            // 计时器推进到了"现在"，而不是停在原地 —— 停在原地就是每轮都演练一次。
            Assert.NotNull(after.LastCloudDrillUtc);
            Assert.True(
                after.LastCloudDrillUtc > Origin.AddHours(6),
                $"计时器没推进：{after.LastCloudDrillUtc}");

            // 演练结论也记下来了，而且是通过。
            Assert.True(after.LastDrillOk, $"演练没通过：{after.LastDrillMessage}");
            Assert.NotNull(after.LastDrillUtc);

            // 包是好的，不该有任何失败通知。
            Assert.Equal(0, h.Notify.CountOf(NotifyEvent.Failure));

            // 演练目录不能留下 —— 它装的是解压出来的完整副本。
            Assert.Empty(Directory.GetDirectories(
                h.ZipTemp.Path, RestoreDrillService.DrillDirectoryPrefix + "*"));

            // 云盘里那个包一动不动。
            Assert.Single(h.OneDrive.Files("*.7z"));
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
    public async Task 云端演练发现坏包时发演练通知_但不动那个包也不隔离()
    {
        // 【演练】标签必须和【失败】分开：备份失败是"这次没备份成"，
        // 演练失败是"以前备份的那些可能恢复不了" —— 后者要紧得多。
        Harness h = BuildHarness(
            TestBinaries.SevenZip(),
            tweak: s =>
            {
                s.CloudDrillEnabled = true;
                s.CloudDrillIntervalHours = 6;
                s.VerifyAfterPackByExtract = false;
            });

        try
        {
            // 一个彻头彻尾不是 7z 的文件。放在云盘目录里，等着被抽中。
            string corrupt = Path.Combine(h.OneDrive.Path, "Backup_corrupt.7z");
            File.WriteAllText(corrupt, "这根本不是一个压缩包，只是一段文字。");

            // 没种 LastCloudDrillUtc：从没演练过就立刻做一次
            //（用户是主动打开这个开关的，让他等满一个周期才知道能不能用是不合理的）。
            Assert.True(
                await PumpUntilAsync(h, _ => h.Store.Load().LastCloudDrillUtc is not null),
                $"从没演练过却没有立刻做一次。日志：{h.Log.Dump()}");

            Assert.True(h.Log.Contains("云端恢复演练未通过"), h.Log.Dump());

            // ---- 发的是【演练】，不是【失败】 ----
            NotifyMessage msg = h.Notify.First(NotifyEvent.Failure);
            string[] lines = msg.ToPlainText().Split('\n', StringSplitOptions.RemoveEmptyEntries);

            Assert.Equal($"【{NotifyTag.Drill}】", lines[1].Trim());

            string text = msg.ToPlainText();
            Assert.Contains("Backup_corrupt.7z", text);
            Assert.Contains("恢复演练", text);

            // ---- 坏包不归引擎处置 ----
            // 它是既成事实，删了等于毁掉用户手里仅有的那份（哪怕是坏的）；
            // 而隔离区是给"源文件反复打包失败"用的，与云端的包无关。
            Assert.True(File.Exists(corrupt), "引擎把云盘里的包删了");
            Assert.Equal(0, h.Engine.Snapshot.QuarantineCount);

            EngineState after = h.Store.Load();

            Assert.False(after.LastDrillOk, "坏包居然被记成演练通过");

            // 失败也要推进计时器 —— 否则一个反复失败的演练会变成每一轮都下载一次包。
            Assert.NotNull(after.LastCloudDrillUtc);

            // 失败路径同样要清干净。
            Assert.Empty(Directory.GetDirectories(
                h.ZipTemp.Path, RestoreDrillService.DrillDirectoryPrefix + "*"));
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
    public async Task 云端演练关着时一轮都不做()
    {
        // 默认就是关着的：它要把包重新下载回本地，得由用户自己决定值不值。
        Harness h = BuildHarness(
            TestBinaries.SevenZip(),
            tweak: s =>
            {
                s.CloudDrillEnabled = false;
                s.CloudDrillIntervalHours = 1;
                s.VerifyAfterPackByExtract = false;
            });

        try
        {
            await MakeCloudArchiveAsync(h, "Backup_cloud.7z");

            // 推过好几个周期。
            Assert.True(await AdvanceAndSettleAsync(h, TimeSpan.FromHours(12)));

            Assert.False(h.Log.Contains("开始云端恢复演练"), h.Log.Dump());
            Assert.Null(h.Store.Load().LastCloudDrillUtc);
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
    public async Task 云盘目录里一个包都没有时不推进计时器()
    {
        // 不推进是有意的：等第一个包送上去，下一轮就会立刻验它，
        // 正好把"打包 → 上传 → 云端还能解开"整条链路走通一遍。
        // 若在这里就把计时器推掉，那第一个包要再等一个完整周期才会被验。
        Harness h = BuildHarness(
            TestBinaries.SevenZip(),
            tweak: s =>
            {
                s.CloudDrillEnabled = true;
                s.CloudDrillIntervalHours = 6;
                s.VerifyAfterPackByExtract = false;
            });

        try
        {
            // 云盘目录是空的。
            Assert.Empty(h.OneDrive.Files("*.7z"));

            Assert.True(await AdvanceAndSettleAsync(h, TimeSpan.FromHours(12)));

            Assert.False(h.Log.Contains("开始云端恢复演练"), h.Log.Dump());
            Assert.Null(h.Store.Load().LastCloudDrillUtc);
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
    //  云盘容量预算：算出来放不下，就在打包之前拒绝
    //
    //  这道检查的位置是关键 —— 它在打包<b>之前</b>，所以拒绝的那一轮
    //  压根不产生任何文件。放在打包之后就成了"先占满再说"。
    // ==================================================================

    /// <summary>在云盘目录里放一个假装的旧包，把预算吃掉一部分。</summary>
    private static void SeedCloudUsage(Harness h, string name, int bytes) =>
        h.OneDrive.WriteFile(name, bytes);

    [Fact]
    public async Task 容量预算不足时拒绝打包且不产生任何文件()
    {
        // 云盘目录里已经躺着 8 KB 的包，预算也正好是 8 KB —— 剩余为 0。
        // 此时不管来多小的文件都放不下。
        Harness h = BuildHarness(
            maxAttempts: 2,
            tweak: s =>
            {
                s.CloudQuotaBytes = 8192;
                s.CloudQuotaWarnBytes = 0;
            });

        try
        {
            SeedCloudUsage(h, "Backup_old.7z", 8192);

            h.Monitor.WriteFile("data.dat", 4096);

            bool refused = await PumpUntilAsync(h, _ => h.Log.Contains("容量预算不足"));

            Assert.True(refused, $"容量预算不足却照常打包了。日志：{h.Log.Dump()}");

            // 核心断言：拒绝的那一轮一个中间文件都不该出现。
            AssertZipTempClean(h);

            // 云盘目录里还是只有那个旧包 —— 新的一个都没进来。
            string[] cloud = h.OneDrive.Files("*.7z");
            Assert.Single(cloud);
            Assert.EndsWith("Backup_old.7z", cloud[0]);

            Assert.Equal(0, h.Engine.Snapshot.ArchivesCreated);

            // 借位 DiskWarning + NotifyTag.Disk，不新增 NotifyEvent 成员 ——
            // 那个枚举按名字序列化，加成员要连着改 All 和设置页的勾选项。
            Assert.True(h.Notify.Any(NotifyEvent.DiskWarning), "预算不足没有发【磁盘】通知");

            // 拒绝一轮不等于引擎该停下：用户清理掉旧包之后它要能自己恢复。
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
    public async Task 没填容量预算时行为与改动前完全一致()
    {
        // 新字段遇上老 settings.json 拿到的就是 0。
        // 云盘目录里堆着 4 MB，但预算没填 —— 整道检查必须跳过，照常打包。
        // 这一条是所有老用户升级后的默认路径，挂了就是升级即故障。
        Harness h = BuildHarness(
            TestBinaries.SevenZip(),
            tweak: s =>
            {
                s.CloudQuotaBytes = 0;
                s.CloudQuotaWarnBytes = 0;
            });

        try
        {
            SeedCloudUsage(h, "Backup_old.7z", 4 * 1024 * 1024);

            h.Monitor.WriteFile("data.dat", 4096);

            Assert.True(
                await PumpUntilAsync(h, s => s.ArchivesCreated > 0),
                $"没填预算却没能跑完一轮。当前状态：{h.Engine.Snapshot.StatusText}");

            Assert.False(h.Log.Contains("容量预算不足"), $"预算没填却做了预算检查。日志：{h.Log.Dump()}");
            Assert.False(h.Log.Contains("低于警戒线"), h.Log.Dump());

            // 旧包 + 新包，新的那个确实落进来了。
            Assert.Equal(2, h.OneDrive.Files("*.7z").Length);
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
    public async Task 剩余低于警戒线只告警_照常打包投递()
    {
        // 告警和拒绝是两件事：告警是"快满了，注意一下"，拒绝是"这一包放不进去"。
        // 混在一起的后果是剩余一进入警戒区就再也不备份了 —— 那正是用户最需要备份的时候。
        Harness h = BuildHarness(
            TestBinaries.SevenZip(),
            tweak: s =>
            {
                s.CloudQuotaBytes = 1024 * 1024;            // 总共 1 MB
                s.CloudQuotaWarnBytes = 512 * 1024;         // 剩余低于 512 KB 就提醒
            });

        try
        {
            SeedCloudUsage(h, "Backup_old.7z", 900 * 1024);   // 已用 900 KB，剩 124 KB

            h.Monitor.WriteFile("data.dat", 4096);            // 预计需要 ~4.5 KB，塞得下

            Assert.True(
                await PumpUntilAsync(h, s => s.ArchivesCreated > 0),
                $"告警路径把打包也拦下了。当前状态：{h.Engine.Snapshot.StatusText}");

            Assert.True(h.Log.Contains("低于警戒线"), $"剩余已在警戒线之下却没告警。日志：{h.Log.Dump()}");
            Assert.False(h.Log.Contains("容量预算不足"), $"塞得下却被拒了。日志：{h.Log.Dump()}");

            Assert.True(h.Notify.Any(NotifyEvent.DiskWarning), "低于警戒线没有发【磁盘】通知");

            // 关键：告警之后包照样做出来、照样投递。
            Assert.Equal(2, h.OneDrive.Files("*.7z").Length);
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
    //  清空历史记录：复位成"全新的、从未运行过"的样子
    // ==================================================================

    [Fact]
    public async Task 引擎运行中拒绝清空历史记录()
    {
        // 引擎在 Start 时把状态读进内存副本，之后十几处 Save 会把那份副本写回来。
        // 运行中复位，下一次保存就把旧状态原样写回去了 —— 用户看到的是"点了没反应"。
        // 所以拦在引擎里，而不是只把按钮置灰：置灰挡得住鼠标，挡不住别的调用方。
        Harness h = BuildHarness();

        try
        {
            Assert.True(h.Engine.IsRunning);

            Assert.False(h.Engine.ResetState(out string? error), "引擎运行中竟然允许清空");
            Assert.False(string.IsNullOrWhiteSpace(error));
            Assert.Contains("运行", error!);
        }
        finally
        {
            await h.Engine.StopAsync();
        }
    }

    [Fact]
    public async Task 清空历史记录后每个字段都回到初始值()
    {
        Harness h = BuildHarness(TestBinaries.SevenZip());

        try
        {
            h.Monitor.WriteFile("data.dat", 4096);

            Assert.True(
                await PumpUntilAsync(h, s => s.ArchivesCreated > 0 && s.PendingUploadCount == 0),
                $"没能跑完一轮，等于没有历史可清。当前状态：{h.Engine.Snapshot.StatusText}");

            await h.Engine.StopAsync();

            // 先确认真的攒下了东西 —— 否则"清空后是空的"这个断言毫无意义。
            EngineState before = h.Store.Load();
            Assert.True(before.TotalArchivesCreated > 0, "跑完一轮却没记下任何累计数字");
            Assert.True(before.TotalBytesArchived > 0);
            Assert.True(before.TotalFilesArchived > 0);
            Assert.True(before.FirstRunCompleted);
            Assert.NotNull(before.Checkpoint);
            Assert.NotNull(before.LastPackSuccessUtc);

            Assert.True(h.Engine.ResetState(out string? error), $"清空失败：{error}");
            Assert.Null(error);

            // ---- 磁盘上的状态：逐个字段对照 new EngineState() ----
            EngineState after = h.Store.Load();
            EngineState fresh = new();

            Assert.Equal(fresh.TotalArchivesCreated, after.TotalArchivesCreated);
            Assert.Equal(fresh.TotalBytesArchived, after.TotalBytesArchived);
            Assert.Equal(fresh.TotalFilesArchived, after.TotalFilesArchived);
            Assert.Equal(fresh.FirstRunCompleted, after.FirstRunCompleted);
            Assert.Equal(fresh.Checkpoint, after.Checkpoint);
            Assert.Equal(fresh.LastPackSuccessUtc, after.LastPackSuccessUtc);
            Assert.Equal(fresh.LastPackFailureUtc, after.LastPackFailureUtc);
            Assert.Equal(fresh.LastPackFailureReason, after.LastPackFailureReason);
            Assert.Equal(fresh.LastDeliverSuccessUtc, after.LastDeliverSuccessUtc);
            Assert.Equal(fresh.LastDeliverFailureUtc, after.LastDeliverFailureUtc);
            Assert.Equal(fresh.LastDeliverFailureReason, after.LastDeliverFailureReason);
            Assert.Equal(fresh.LastDrillUtc, after.LastDrillUtc);
            Assert.Equal(fresh.LastDrillOk, after.LastDrillOk);
            Assert.Equal(fresh.LastDrillMessage, after.LastDrillMessage);
            Assert.Equal(fresh.LastCloudDrillUtc, after.LastCloudDrillUtc);

            Assert.Empty(after.PendingUploads);
            Assert.Empty(after.Quarantined);
            Assert.Empty(after.ForcedFiles);
            Assert.Empty(after.FutureStamped);

            // ---- 内存里那份也得跟着空 ----
            //
            // 状态存在两个地方：磁盘上的 state.json，和引擎发布给界面的快照。
            // 只清磁盘的话，总览上的累计数字会一直挂着旧值直到重启 ——
            // 用户点了"清空"却看见数字没变，只会以为没清掉，然后再点一次。
            Assert.Equal(0, h.Engine.Snapshot.ArchivesCreated);
            Assert.Equal(0, h.Engine.StateView.TotalArchivesCreated);
            Assert.Null(h.Engine.StateView.Checkpoint);

            // ---- 压缩包一个都不许少 ----
            //
            // 清的是账本，不是备份。这一条是整个功能的底线：
            // 将来谁在复位路径上顺手加了删文件的逻辑，会先在这里被挡下。
            Assert.NotEmpty(h.OneDrive.Files("*.7z"));
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
    public async Task 停止状态下的StateView读的是磁盘而不是空壳()
    {
        // 引擎从没跑过时，内存里那份 _stateView 是空的。
        // 确认框要靠它念出"有 3 条待上传记录会被清掉"——
        // 读空壳就会念成"没有"，用户点下去才发现清掉了真东西。
        Harness h = BuildHarness(
            seedState: st =>
            {
                st.TotalArchivesCreated = 7;
                st.PendingUploads.Add(new PendingUpload
                {
                    ArchivePath = "X:\\cloud\\Backup_pending.7z",
                    Bytes = 1024,
                    FileCount = 2,
                });
            });

        await h.Engine.StopAsync();

        EngineState view = h.Engine.StateView;

        Assert.Equal(7, view.TotalArchivesCreated);
        Assert.Single(view.PendingUploads);
    }

    // ==================================================================
    //  隔离区上限：内存与 state.json 都不能无限制地涨
    //
    //  隔离区曾是全状态里唯一<b>一点上限都没有</b>的集合。每个批次还拖着
    //  一整份 List<string> Files，而 state.json 是每轮整份重新序列化的 ——
    //  一个持续失败的目录（权限不对、被独占）能让它一轮一轮涨上去，
    //  内存和每轮的写盘量一起变大。
    // ==================================================================

    /// <summary>造 <paramref name="count"/> 个隔离批次，Id 依次是 Q-0、Q-1……</summary>
    private static void SeedQuarantine(EngineState state, int count)
    {
        for (int i = 0; i < count; i++)
        {
            state.Quarantined.Add(new QuarantinedBatch
            {
                Id = $"Q-{i}",
                Files = [$"X:\\src\\f{i}.dat"],
                Reason = "测试用：连续打包失败",
                Attempts = 3,
                QuarantinedUtc = Origin.AddMinutes(i),
                TotalBytes = 4096,
            });
        }
    }

    [Fact]
    public async Task 隔离区超过上限时裁掉最旧的批次()
    {
        const int Cap = 200;
        const int Seeded = 250;

        Harness h = BuildHarness(
            tweak: NeverReady(),
            seedState: s =>
            {
                s.FirstRunCompleted = true;
                SeedQuarantine(s, Seeded);
            });

        try
        {
            // 裁剪发生在 StartupAsync 里的 SanitizeState —— 那是主循环的异步部分，
            // Start() 返回时还没跑到。得等隔离区在快照里出现才能断言。
            Assert.True(
                await PumpUntilAsync(h, s => s.QuarantineCount > 0, maxSteps: 10, engineSecondsPerStep: 1),
                "种进去的隔离批次一直没出现在快照里");

            EngineState after = h.Engine.StateSnapshot();

            Assert.Equal(Cap, after.Quarantined.Count);

            // 留下的必须是最新的那一批：最旧的 Q-0 被丢掉，最新的 Q-249 还在。
            Assert.DoesNotContain(after.Quarantined, q => q.Id == "Q-0");
            Assert.DoesNotContain(after.Quarantined, q => q.Id == $"Q-{Seeded - Cap - 1}");
            Assert.Equal($"Q-{Seeded - Cap}", after.Quarantined[0].Id);
            Assert.Equal($"Q-{Seeded - 1}", after.Quarantined[^1].Id);
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
    /// 淘汰必须留下日志。
    ///
    /// 这一点跟 <c>LedgerCap</c> 那两个名单不一样：那些是程序自用的内部账本，
    /// 悄悄淘汰无妨。隔离区是<b>给用户看的待办清单</b> —— 批次从这里消失，
    /// 意味着用户再也不会知道那些文件失败过，而源文件还在磁盘上没被备份。
    /// 不出声地丢掉，等于悄悄放弃了用户的数据。
    /// </summary>
    [Fact]
    public async Task 裁剪隔离区必须留下告警()
    {
        Harness h = BuildHarness(
            tweak: NeverReady(),
            seedState: s =>
            {
                s.FirstRunCompleted = true;
                SeedQuarantine(s, 250);
            });

        try
        {
            Assert.True(
                await PumpUntilAsync(h, s => s.QuarantineCount > 0, maxSteps: 10, engineSecondsPerStep: 1),
                "种进去的隔离批次一直没出现在快照里");

            Assert.True(
                h.Log.Contains("隔离区已达上限"),
                "裁掉了 50 批隔离记录却一声不吭：" + h.Log.Dump());

            // 丢了多少批要说清楚，只说"达到上限"等于没说。
            Assert.True(h.Log.Contains("丢弃最早的 50 批"), h.Log.Dump());

            // 也要讲清后果：源文件还在，但用户从此看不到它们了。
            Assert.True(h.Log.Contains("源文件仍在磁盘上"), h.Log.Dump());
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
    public async Task 没到上限的隔离区一条都不动()
    {
        Harness h = BuildHarness(
            tweak: NeverReady(),
            seedState: s =>
            {
                s.FirstRunCompleted = true;
                SeedQuarantine(s, 5);
            });

        try
        {
            Assert.True(
                await PumpUntilAsync(h, s => s.QuarantineCount > 0, maxSteps: 10, engineSecondsPerStep: 1),
                "种进去的隔离批次一直没出现在快照里");

            EngineState after = h.Engine.StateSnapshot();

            Assert.Equal(5, after.Quarantined.Count);
            Assert.Equal("Q-0", after.Quarantined[0].Id);
            Assert.False(h.Log.Contains("隔离区已达上限"), h.Log.Dump());
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

    /// <summary>裁剪结果必须落盘 —— 否则内存里裁了，下次启动又从磁盘读回 250 条。</summary>
    [Fact]
    public async Task 裁剪结果要写回state文件()
    {
        Harness h = BuildHarness(
            tweak: NeverReady(),
            seedState: s =>
            {
                s.FirstRunCompleted = true;
                SeedQuarantine(s, 250);
            });

        try
        {
            Assert.True(
                await PumpUntilAsync(h, s => s.QuarantineCount > 0, maxSteps: 10, engineSecondsPerStep: 1),
                "种进去的隔离批次一直没出现在快照里");

            EngineState onDisk = h.Store.Load();

            Assert.Equal(200, onDisk.Quarantined.Count);
            Assert.DoesNotContain(onDisk.Quarantined, q => q.Id == "Q-0");
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
