using System.Collections.Concurrent;
using NewAutoZip.Core.Configuration;
using NewAutoZip.Core.Diagnostics;
using NewAutoZip.Core.Notifications;
using NewAutoZip.Core.OneDrive;
using NewAutoZip.Core.Packing;
using NewAutoZip.Core.Scheduling;
using NewAutoZip.Core.Storage;
using NewAutoZip.Core.Watching;

namespace NewAutoZip.Core.Pipeline;

/// <summary>
/// 备份流水线。
///
/// 整个类围绕一条不变式设计：<b>一个批次要么完成、要么进重试队列、要么进隔离区，
/// 不存在第四种结局，也不可能停在"还赖在当前批次里"的状态。</b>
///
/// 旧版 <c>BackupWorker</c> 缺的正是这条。它的失败分支（BackupWorker.cs:538-542）
/// 只记了一行日志就 continue，<c>_files</c> / <c>_hasWindow</c> / <c>_checkpoint</c> 全都不动，
/// 于是下一个扫描周期（默认 30 秒）用同一批文件再打一个完整压缩包 —— 永不停止。
/// 这就是用户看到的"ZipTemp 塞满磁盘"。触发入口至少有五个：
/// 7-Zip 退出码 1（其实成功）、未包 try 的通知调用抛异常、源文件被删、
/// Checkpoint.Save 无写权限、以及磁盘满本身（磁盘满 → Save 抛异常 → 跳过复位 → 再打一个包）。
///
/// 这里的对策是结构性的，不是补丁：
///   * <see cref="RunBatchAsync"/> 全程 try/finally，finally 里<b>无条件</b>清空批次；
///   * 失败一律交给 <see cref="RetryQueue"/>，由它做退避与熔断，当前批次立刻腾空；
///   * 通知调用一律经 <see cref="NotifySafeAsync"/>，异常在那里就地吞掉；
///   * 打包前做磁盘预检，放不下就<b>压根不产生文件</b>；
///   * 主循环每轮都套 try/catch，单次异常不会让引擎静默死亡（旧版会，而 UI 还显示"运行中"）。
/// </summary>
public sealed class BackupEngine : IAsyncDisposable
{
    private readonly IAppLogger _log;
    private readonly TimeProvider _time;
    private readonly IFileProbe _probe;
    private readonly StateStore _stateStore;

    private readonly MonitorService _monitor;
    private readonly StabilityTracker _tracker;
    private readonly ZipTempManager _zipTemp;
    private readonly RetryQueue _retries;

    /// <summary>当前批次：路径 → 字节数。只在主循环线程上访问。</summary>
    private readonly Dictionary<string, long> _batch = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>界面线程排进来、由主循环执行的操作（隔离区重试 / 忽略）。</summary>
    private readonly ConcurrentQueue<Action> _commands = new();

    private readonly object _lifecycleGate = new();

    private AppSettings _settings = new();
    private INotificationHub _notify = NullNotificationHub.Instance;
    private IUploadMonitor _uploads = DeliveryOnlyUploadMonitor.Instance;
    private SevenZipRunner? _runner;
    private EngineState _state = new();
    private FileFilter _filter = new(null);

    private CancellationTokenSource? _cts;
    private Task? _loop;

    private DateTimeOffset? _windowOpenedUtc;
    private DateTimeOffset _lastReconcileUtc;
    private DateTimeOffset _lastUploadPollUtc;

    /// <summary>
    /// 上一轮观察到的"是否在工作时段内"。<c>null</c> = 引擎刚起来，还没观察过。
    ///
    /// 【开工】/【收工】是<b>边沿触发</b>的：只在这个值真正翻转时发一条。
    /// 拿不到"上一轮"就没法区分"刚跨进时段"和"本来就在时段里"，
    /// 而后者每 5 秒一轮，会把通知刷成刷屏。
    /// 全天模式（<c>AllDay</c>）下永远不翻转，因此一条都不发 —— 这正是应该的。
    /// </summary>
    private bool? _scheduleOpen;

    /// <summary>
    /// 本窗口是否已经发过【就绪】。用户要的是"整个窗口期内所有文件都稳定了，才发<b>一次</b>"，
    /// 所以这是个一次性闸门，跟着批次一起在 <see cref="RunBatchAsync"/> 的 finally 里复位。
    /// </summary>
    private bool _stableAnnounced;

    /// <summary>
    /// 本轮已经发出过几条【第 N 次发现新文件】。<b>数的是通知条数，不是文件数。</b>
    ///
    /// 用户的原话：同一时间新增 2 个文件、此前已经是【第 3 次】，那么这一条是【第 4 次】而不是【第 5 次】。
    /// 一条通知 = 一次"发现"，所以这里每发一条就 +1，与那一条带了几个文件无关。
    ///
    /// 正文里的"发现新文件：1. / 2. …"是另一回事：那份清单<b>每条通知各自从 1 数</b>
    /// （见 <see cref="DiscoveryLines"/>），只表示这一条带来了哪几个文件。
    /// 两个编号互不相干，只有"一次通报一个文件"时才碰巧相等。
    /// </summary>
    private int _discoveries;

    /// <summary>
    /// 本轮已经通报过的路径。<b>只管通报，不管跟踪。</b>
    ///
    /// 用户报的冗余就出在这里：同一个文件先来一条 0B、紧接着又来一条 9.85K。
    /// 原因不是"大小变了就发通知"（从来没有这条逻辑），而是文件<b>掉出过跟踪表</b>——
    /// Excel、WPS 这类程序保存时是"写临时文件 + 替换原文件"，替换的那一瞬间
    /// 路径真的不存在，<see cref="StabilityTracker.Refresh"/> 按 <c>!Exists</c>
    /// 把它剔了（这一步不能不做，否则就是旧版那个静默假死）；下一个 watcher 事件
    /// 又把它当"新文件"重新登记，于是第二条通知带着新的大小发了出去。
    ///
    /// 所以去重必须记在<b>通知这一侧</b>：文件该重新跟踪就重新跟踪，只是不再重复通报。
    /// 跟着 <see cref="_discoveries"/> 一起在批次结束时清空 —— 下一轮是新的一轮，
    /// 同名文件再出现就是真的新文件了。
    /// </summary>
    private readonly HashSet<string> _announced = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 工作时间里"生效起始日期"那天的本地零点（UTC）。文件的修改时间不得早于它。
    /// 在 <see cref="Start"/> 里算一次，避免每个候选文件都去做一次时区换算。
    /// </summary>
    private DateTimeOffset? _fileFloorUtc;
    private EnginePhase _phase = EnginePhase.Stopped;
    private int _packPercent;
    private string? _packFile;
    private EngineSnapshot _snapshot = EngineSnapshot.Stopped;
    private EngineState _stateView = new();
    private long _rounds;

    public BackupEngine(IAppLogger log, TimeProvider? time = null, IFileProbe? probe = null, StateStore? stateStore = null)
    {
        _log = log;
        _time = time ?? TimeProvider.System;
        _probe = probe ?? Win32FileProbe.Instance;
        _stateStore = stateStore ?? new StateStore(AppPaths.StateFile, log);

        _monitor = new MonitorService(log);
        _tracker = new StabilityTracker(_time, _probe, log);
        _zipTemp = new ZipTempManager(log, _time);
        _retries = new RetryQueue(log, _time);
    }

    /// <summary>UI 以约 4 Hz 轮询这个属性，不订阅细粒度事件。</summary>
    public EngineSnapshot Snapshot => Volatile.Read(ref _snapshot);

    public bool IsRunning => _loop is { IsCompleted: false };

    public string ZipTempDirectory => _zipTemp.Root;

    /// <summary>已完成的检查轮数。用于界面上的"已运行 N 轮"，也让测试能确定性地等一轮跑完。</summary>
    public long CompletedRounds => Volatile.Read(ref _rounds);

    /// <summary>
    /// 立刻唤醒正在等待的主循环，不必等到下一个 <c>RefreshIntervalSeconds</c>。
    /// 界面的"立即检查一次"按钮用它；改完设置后也可以调一次让新配置马上生效。
    /// </summary>
    public void Nudge() => _monitor.Poke();

    // ==================================================================
    //  生命周期
    // ==================================================================

    /// <summary>
    /// 启动引擎。已在运行时直接返回 false，<b>绝不会起第二个循环</b>。
    ///
    /// 旧版 <c>StopProgram</c> 把 <c>_thread</c> 和 <c>_worker</c> 都置为 null 却不 Join，
    /// 而"是否已在运行"的判断依据是 <c>_thread != null &amp;&amp; _thread.IsAlive</c>，
    /// 于是停止后立刻再点开始，旧线程还活着就已经起了新线程 ——
    /// 两个 Worker 同时往 ZipTemp 扔压缩包。
    /// </summary>
    public bool Start(
        AppSettings settings,
        INotificationHub? notifications = null,
        IUploadMonitor? uploadMonitor = null,
        string? sevenZipExePath = null)
    {
        lock (_lifecycleGate)
        {
            if (_loop is { IsCompleted: false })
            {
                _log.Warn("引擎已在运行，忽略重复的启动请求。");
                return false;
            }

            _settings = settings.Clone();
            _settings.ClampToLimits();
            _notify = notifications ?? NullNotificationHub.Instance;
            _uploads = uploadMonitor ?? DeliveryOnlyUploadMonitor.Instance;
            _runner = new SevenZipRunner(sevenZipExePath ?? AppPaths.SevenZipExe, _log);
            _fileFloorUtc = ScheduleWindow.FromSettings(_settings).EffectiveFromUtc();

            SecretRedactor.Shared.SetSecrets(
            [
                _settings.Password,
                _settings.WebhookUrl,
                _settings.TelegramBotToken,
                _settings.TelegramChatId,
            ]);

            _cts = new CancellationTokenSource();
            CancellationToken ct = _cts.Token;

            _loop = Task.Run(() => LoopAsync(ct), CancellationToken.None);
            return true;
        }
    }

    /// <summary>
    /// 停止并<b>等待循环真正退出</b>后才返回。UI 在此期间保持按钮禁用状态，
    /// 因此不可能出现"旧循环还在跑、新循环已经起来"的重叠。
    /// </summary>
    public async Task StopAsync()
    {
        Task? loop;
        CancellationTokenSource? cts;

        lock (_lifecycleGate)
        {
            loop = _loop;
            cts = _cts;
        }

        if (loop is null)
        {
            return;
        }

        SetPhase(EnginePhase.Stopping);
        PublishSnapshot();

        try
        {
            cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 已经停过了
        }

        _monitor.Poke();

        try
        {
            await loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 正常
        }
        catch (Exception ex)
        {
            _log.Error("引擎循环退出时抛出异常。", ex);
        }

        lock (_lifecycleGate)
        {
            _loop = null;
            cts?.Dispose();
            _cts = null;
        }

        SetPhase(EnginePhase.Stopped);
        PublishSnapshot();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _monitor.Dispose();
    }

    // ==================================================================
    //  主循环
    // ==================================================================

    private async Task LoopAsync(CancellationToken ct)
    {
        try
        {
            await StartupAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await ShutdownAsync().ConfigureAwait(false);
            return;
        }
        catch (Exception ex)
        {
            _log.Error("引擎启动阶段失败，将继续尝试进入主循环。", ex);
        }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await TickAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // 主循环绝不因单次异常退出。旧版的 while 循环里任何异常都会让工作线程
                // 静默死亡，而 UI 上"● 运行中"的绿灯还亮着 —— 界面在撒谎。
                _log.Error("主循环出现未预期的异常，本轮跳过，5 秒后继续。", ex);

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
            finally
            {
                Interlocked.Increment(ref _rounds);
                PublishSnapshot();
            }
        }

        await ShutdownAsync().ConfigureAwait(false);
    }

    private async Task StartupAsync(CancellationToken ct)
    {
        SetPhase(EnginePhase.Starting);
        PublishSnapshot();

        AppPaths.EnsureCreated();

        _state = _stateStore.Load();
        _zipTemp.Configure(_settings.ResolveZipTemp());
        _zipTemp.EnsureDirectory();

        // 清扫上次异常退出留下的 .part / .list 残骸。
        _zipTemp.SweepIntermediates();
        EnforceQuota();

        _filter = new FileFilter(
            _settings.ExcludePatterns,
            [_zipTemp.Root, _settings.CloudPath, AppPaths.DataRoot]);

        _tracker.Configure(_settings.QuietSeconds, _settings.StableConfirmRounds);

        if (_runner is not null)
        {
            string? version = await _runner.TryGetVersionAsync(ct).ConfigureAwait(false);
            _log.Info(version is null
                ? $"警告：压缩程序不可用（{_runner.ExePath}）。打包会全部失败。"
                : $"压缩程序：{version}（{_runner.ExePath}）");
        }

        if (_state.PendingUploads.Count > 0)
        {
            // 重启后继续等原来的归档，而不是重新打包。旧版没有这个概念。
            _log.Info($"发现 {_state.PendingUploads.Count} 个上次未确认上传的归档，将继续跟踪。");
        }

        if (_state.Quarantined.Count > 0)
        {
            _log.Warn($"隔离区里有 {_state.Quarantined.Count} 个批次等待处理，它们不会被自动重试。");
        }

        // 首次运行的水位线。
        //
        // 监控目录里可能已经堆着几万个历史文件。三条规则，从最具体的开始：
        //
        //   1. 设了「生效起始日期」→ 那句话本身就是水位线：这一天之后的文件都要备份，
        //      包括程序启动前就已经躺在目录里的。用户在界面上写下这个日期，
        //      期待的就是这个语义；旧行为把水位线钉在"程序启动的这一刻"，
        //      于是"起始日期之后、启动之前"落下的文件被永久挡住，而界面上完全看不出原因。
        //   2. 没设起始日期、勾了「首次启动也处理已有文件」→ 全部处理，不设水位线。
        //   3. 都没有 → 把当前时刻记为水位线，只处理此后的新文件。
        if (!_state.FirstRunCompleted)
        {
            if (_fileFloorUtc is { } floor)
            {
                // 不写 _state.Checkpoint —— 准入下限由 PassesCheckpoint 里的起始日期那条线负责，
                // 这样用户以后改日期立刻生效，不必去"清除运行状态"。
                _log.Info(
                    $"首次运行：按工作时间里的生效起始日期（{floor.ToLocalTime():yyyy-MM-dd}）取文件，" +
                    "该日期之后的文件都会被备份，包括目录里已有的。");
            }
            else if (_settings.ProcessExistingFilesOnFirstRun)
            {
                _log.Info("首次运行：按设置处理监控目录中已有的文件。");
            }
            else if (_state.Checkpoint is null)
            {
                _state.Checkpoint = _time.GetUtcNow();
                _log.Info(
                    $"首次运行：已把当前时刻记为水位线（{_state.Checkpoint:yyyy-MM-dd HH:mm:ss}），" +
                    "监控目录中已有的文件不会被打包。若需要处理它们，请在设置里勾选"
                    + "「首次运行也处理已有文件」后清除运行状态，或直接设一个生效起始日期。");
            }

            _state.FirstRunCompleted = true;
            _stateStore.Save(_state);
        }

        if (!string.IsNullOrWhiteSpace(_settings.MonitorPath) && Directory.Exists(_settings.MonitorPath))
        {
            _monitor.Start(_settings.MonitorPath, _settings.IncludeSubdirectories);
        }
        else
        {
            _log.Error($"监控目录不可用：{_settings.MonitorPath}。引擎会空转，请修正配置后重启引擎。");
        }

        _lastReconcileUtc = _time.GetUtcNow();
        _lastUploadPollUtc = DateTimeOffset.MinValue;

        // 边沿触发的状态清零：这次运行的第一轮不该把"本来就在时段内"当成"刚跨进时段"。
        _scheduleOpen = null;
        _stableAnnounced = false;
        _discoveries = 0;
        _announced.Clear();

        ScheduleWindow startupSchedule = ScheduleWindow.FromSettings(_settings);
        DateTimeOffset startupLocal = _time.GetLocalNow();

        await NotifySafeAsync(
            Msg(NotifyEvent.EngineStarted, NotifyTag.EngineStarted,
                CloudStatusLine(),
                "备份程序已启动，开始监控目录变化。",
                $"监控目录：{_settings.MonitorPath}",
                $"归档目录：{_settings.CloudPath}",
                $"批处理窗口：{_settings.BatchWindowMinutes} 分钟",
                ScheduleRangeLine(),

                // 当前在不在时段内必须说清楚：【开工】只在真正跨进时段的那一刻发，
                // 启动时就已经在时段里的话不会有那一条，这里把这个信息补上，
                // 否则用户会以为"到点了却没通知"。
                startupSchedule.IsActive(startupLocal)
                    ? "当前已在工作时段内，发现新文件会立即开始处理。"
                    : NextStartLine(startupSchedule, startupLocal),
                $"重试阶梯：{RetryPolicy.DescribeLadder()}，{_settings.MaxAttemptsBeforeQuarantine} 次后隔离",
                ProgramDiskLine()),
            CancellationToken.None).ConfigureAwait(false);

        SetPhase(EnginePhase.Idle);
    }

    private async Task ShutdownAsync()
    {
        SetPhase(EnginePhase.Stopping);

        // 界面在停止前一刻排进来的操作也要执行掉，否则那次点击就白点了。
        DrainCommands();

        try
        {
            _monitor.Stop();
        }
        catch (Exception ex)
        {
            _log.Warn("停止目录监控时出错。", ex);
        }

        try
        {
            _stateStore.Save(_state);
        }
        catch (Exception ex)
        {
            _log.Warn("退出时保存状态失败。", ex);
        }

        // 停止通知用独立的超时令牌：主令牌已经取消了，不能拿它去发 HTTP 请求。
        using CancellationTokenSource shutdownCts = new(TimeSpan.FromSeconds(10));

        await NotifySafeAsync(
            Msg(NotifyEvent.EngineStopped, NotifyTag.EngineStopped,
                "备份程序已停止，不再监控目录。",
                $"累计生成归档 {_state.TotalArchivesCreated} 个，共 {ByteSize.FormatShort(_state.TotalBytesArchived)}，" +
                $"{_state.TotalFilesArchived} 个文件。",
                _state.PendingUploads.Count > 0
                    ? $"仍有 {_state.PendingUploads.Count} 个归档等待云端确认，下次启动会继续跟踪。"
                    : null,
                ProgramDiskLine()),
            shutdownCts.Token).ConfigureAwait(false);

        _tracker.Clear();
        _batch.Clear();
        _windowOpenedUtc = null;
        _scheduleOpen = null;
        _stableAnnounced = false;
        _discoveries = 0;
        _announced.Clear();

        SetPhase(EnginePhase.Stopped);
        PublishSnapshot();

        _log.Info("引擎已停止。");
    }

    private async Task TickAsync(CancellationToken ct)
    {
        DateTimeOffset nowUtc = _time.GetUtcNow();

        // 0) 先执行界面排进来的操作（隔离区重试 / 忽略），确保它们跑在这个线程上。
        DrainCommands();

        // 1) 待上传归档：每轮只查一次，且按 UploadPollSeconds 限频。
        //    绝不在这里长时间等待 —— 旧版用 Thread.Sleep(60000) 循环最长等两小时，
        //    期间"停止"按钮完全无效。
        await ProcessPendingUploadsAsync(nowUtc, ct).ConfigureAwait(false);

        // 2) 计划时段。进出时段各发一次通知（边沿触发，见 _scheduleOpen）。
        ScheduleWindow schedule = ScheduleWindow.FromSettings(_settings);
        DateTimeOffset nowLocal = _time.GetLocalNow();
        bool scheduleActive = schedule.IsActive(nowLocal);

        await AnnounceScheduleEdgeAsync(schedule, nowLocal, scheduleActive, ct).ConfigureAwait(false);

        if (!scheduleActive)
        {
            SetPhase(EnginePhase.OutsideSchedule);
            PublishSnapshot();
            await WaitForNextRoundAsync(ct).ConfigureAwait(false);
            return;
        }

        // 3) 把 watcher 攒下的候选路径纳入跟踪，新进来的立刻通报（不等它稳定）。
        List<string> found = [];
        IngestHints(found);
        MaybeReconcile(nowUtc, found);
        await AnnounceDiscoveryAsync(found, ct).ConfigureAwait(false);

        // 4) 复查跟踪中的文件；已消失的在这里被剔除（旧版从不剔除 → allStable 永假 → 静默假死）。
        RefreshResult refresh = _tracker.Refresh();
        AbsorbReady(refresh);
        await AnnounceStableAsync(refresh, nowUtc, ct).ConfigureAwait(false);

        // 5) 到点的重试批次优先，并且<b>单独</b>处理：
        //    不把新就绪的文件并进去，避免一个"毒批次"把无辜文件一起拖进隔离区。
        _retries.PruneMissingFiles(File.Exists);
        RetryEntry? due = _retries.TakeDue();

        if (due is not null)
        {
            // 重试批次自成一轮：耗时从"这次重试开始"算起，不把上次失败的等待时间算进去。
            await RunBatchAsync([.. due.Files], due.Id, nowUtc, ct).ConfigureAwait(false);
            return;
        }

        // 6) 批处理窗口。
        //    【第 N 次发现新文件】已经在发现文件那一刻发过了（见 AnnounceDiscoveryAsync），
        //    这里只是打开窗口计时，不再重复通知。
        if (_batch.Count > 0 && _windowOpenedUtc is null)
        {
            _windowOpenedUtc = nowUtc;
            _log.Info($"批处理窗口已开启，将在 {_settings.BatchWindowMinutes} 分钟后打包本窗口内就绪的文件。");
        }

        bool windowDue = _windowOpenedUtc is { } opened
            && nowUtc - opened >= TimeSpan.FromMinutes(_settings.BatchWindowMinutes);

        if (_batch.Count > 0 && windowDue)
        {
            await RunBatchAsync([.. _batch.Keys], null, _windowOpenedUtc ?? nowUtc, ct).ConfigureAwait(false);
            return;
        }

        SetPhase(_batch.Count > 0
            ? EnginePhase.Collecting
            : _retries.Count > 0 ? EnginePhase.Retrying : EnginePhase.Idle);

        PublishSnapshot();
        await WaitForNextRoundAsync(ct).ConfigureAwait(false);
    }

    // ==================================================================
    //  候选收集
    // ==================================================================

    /// <param name="found">
    /// 本轮新纳入跟踪的路径追加到这里，供【第 N 次发现新文件】通知列名字。
    /// 注意"新纳入跟踪"不等于"要通报"—— 被替换式保存踢出去又回来的路径也会出现在这里，
    /// 由 <see cref="AnnounceDiscoveryAsync"/> 负责去重。
    /// </param>
    private void IngestHints(ICollection<string> found)
    {
        string[] hints = _monitor.DrainHints();
        if (hints.Length == 0)
        {
            return;
        }

        int added = 0;
        HashSet<string> inFlight = _retries.AllFiles();

        foreach (string path in hints)
        {
            if (!_filter.Accept(path) || _batch.ContainsKey(path) || inFlight.Contains(path))
            {
                continue;
            }

            if (IsQuarantined(path) || !PassesCheckpoint(path))
            {
                continue;
            }

            if (_tracker.Observe(path))
            {
                found.Add(path);
                added++;
            }
        }

        if (added > 0)
        {
            _log.Debug($"新增 {added} 个候选文件（当前跟踪 {_tracker.Count} 个）。");
        }
    }

    /// <param name="found">对账补入的路径追加到这里，和 watcher 发现的走同一条通知。</param>
    private void MaybeReconcile(DateTimeOffset nowUtc, ICollection<string> found)
    {
        bool forced = _monitor.ConsumeReconcileRequest();
        bool due = nowUtc - _lastReconcileUtc >= TimeSpan.FromSeconds(_settings.ReconcileIntervalSeconds);

        if (!forced && !due)
        {
            return;
        }

        _lastReconcileUtc = nowUtc;

        if (string.IsNullOrWhiteSpace(_settings.MonitorPath) || !Directory.Exists(_settings.MonitorPath))
        {
            return;
        }

        HashSet<string> inFlight = _retries.AllFiles();

        int added = _tracker.Reconcile(
            _settings.MonitorPath,
            _settings.IncludeSubdirectories,
            _filter,
            path => _batch.ContainsKey(path)
                    || inFlight.Contains(path)
                    || IsQuarantined(path)
                    || !PassesCheckpoint(path),
            found);

        if (added > 0)
        {
            _log.Info($"对账扫描补入 {added} 个此前遗漏的文件。");
        }
    }

    private void AbsorbReady(RefreshResult refresh)
    {
        foreach (string path in refresh.Vanished)
        {
            _log.Debug($"文件已消失，停止跟踪：{Path.GetFileName(path)}");
        }

        if (refresh.Ready.Count == 0)
        {
            return;
        }

        foreach (TrackedFile file in refresh.Ready)
        {
            _batch[file.Path] = file.Length;
            _log.Info($"已就绪：{file.FileName}（{ByteSize.Format(file.Length)}）");
        }

        _tracker.Forget(refresh.Ready.Select(f => f.Path));
    }

    // ==================================================================
    //  通知节点
    //
    //  用户要求的那几个节点集中在这一段，一眼能看完整条时间线：
    //    【开工】跨进工作时段                      AnnounceScheduleEdgeAsync
    //    【第 N 次发现新文件】一发现就立即通报       AnnounceDiscoveryAsync
    //    【就绪】窗口内全部稳定（只发一次）          AnnounceStableAsync
    //    【收工】离开工作时段 + 下次开始时间         AnnounceScheduleEdgeAsync
    //  【打包】【上传】【结束】离不开各自的上下文，就地发在
    //  RunBatchAsync / CompleteFolderRoundAsync / ProcessPendingUploadsAsync 里。
    // ==================================================================

    /// <summary>
    /// 【开工】/【收工】。<b>边沿触发</b>：只在"是否在时段内"真正翻转的那一轮发一条。
    ///
    /// 第一轮（<c>_scheduleOpen is null</c>）一律不发 —— 那不是"到点了"，
    /// 而是"程序刚起来，正好在（或不在）时段里"，这个信息由【启动】负责说明。
    /// 全天模式下永远不翻转，因此一条都不会发，这正是应该的。
    /// </summary>
    private async Task AnnounceScheduleEdgeAsync(
        ScheduleWindow schedule,
        DateTimeOffset nowLocal,
        bool active,
        CancellationToken ct)
    {
        bool? previous = _scheduleOpen;
        _scheduleOpen = active;

        if (previous is null || previous.Value == active)
        {
            return;
        }

        if (active)
        {
            _log.Info("已进入工作时段。");

            await NotifySafeAsync(
                Msg(NotifyEvent.ScheduleOpened, NotifyTag.ScheduleOpened,
                    CloudStatusLine(),
                    "已到工作时段，开始监控目录并处理新文件。",
                    ScheduleRangeLine(),
                    $"监控目录：{_settings.MonitorPath}",
                    $"批处理窗口：{_settings.BatchWindowMinutes} 分钟",
                    _batch.Count > 0
                        ? $"上一个时段留下 {_batch.Count} 个待打包文件" +
                          $"（{ByteSize.FormatShort(_batch.Values.Sum())}），本时段继续处理。"
                        : null,
                    ProgramDiskLine()),
                ct).ConfigureAwait(false);

            return;
        }

        _log.Info("已过工作时段，暂停扫描与打包。");

        await NotifySafeAsync(
            Msg(NotifyEvent.ScheduleClosed, NotifyTag.ScheduleClosed,
                "已过工作时段，暂停扫描与打包。",
                ScheduleRangeLine(),
                NextStartLine(schedule, nowLocal),
                _batch.Count > 0
                    ? $"本轮还有 {_batch.Count} 个文件（{ByteSize.FormatShort(_batch.Values.Sum())}）" +
                      "没打包，留到下一次工作时段。"
                    : null,
                _retries.Count > 0
                    ? $"重试队列里还有 {_retries.Count} 个批次，同样留到下一次工作时段。"
                    : null,
                _state.PendingUploads.Count > 0
                    ? $"仍有 {_state.PendingUploads.Count} 个归档在等云端确认 —— " +
                      "这一项不受时段限制，会继续跟踪到底。"
                    : null,
                ProgramDiskLine()),
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 【第 N 次发现新文件】—— 一发现新文件就立刻发，<b>不等它稳定</b>。
    ///
    /// 改这一条的理由：稳定判定默认是"静默 60 秒 + 连续确认 2 轮"，一个正在复制的大文件
    /// 从落盘到判定就绪可能是十几分钟。旧行为在这十几分钟里一声不响，
    /// 用户完全不知道程序有没有看见它。现在是"看见即通报，然后持续观察"。
    ///
    /// 三件事在这一个方法里定死：
    /// <list type="number">
    /// <item><b>一个路径一轮只通报一次</b>（<see cref="_announced"/>）。文件在观察期里
    ///       被"替换式保存"踢出跟踪表再重新登记是常事，那不是新文件。</item>
    /// <item>标签带编号（<see cref="NotifyTag.Discovered"/>），N = <b>本轮第几条这种通知</b>，
    ///       一条 +1。同一时刻进来 2 个文件只发一条，所以【第 3 次】之后是【第 4 次】。
    ///       正文清单则每条各自从 1 数（<see cref="DiscoveryLines"/>），两个编号是两回事。</item>
    /// <item>大小变化<b>一条都不发</b>。观察期里文件从 0B 长到 9.85K 是正常写入过程，
    ///       不是事件；本轮全部稳定之后由【就绪】一次说清（见 <see cref="AnnounceStableAsync"/>）。</item>
    /// </list>
    /// </summary>
    private async Task AnnounceDiscoveryAsync(IReadOnlyList<string> found, CancellationToken ct)
    {
        if (found.Count == 0)
        {
            return;
        }

        // 通报过的就不再通报。注意<b>不</b>影响跟踪：它照样在观察队列里等稳定。
        List<PackedFile> files =
        [
            .. found.Where(p => _announced.Add(p))
                    .Select(p => new PackedFile(p, SafeLength(p))),
        ];

        if (files.Count == 0)
        {
            return;
        }

        // 一条通知算一次"发现"，不看这一条带了几个文件。
        int ordinal = ++_discoveries;

        await NotifySafeAsync(
            Msg(NotifyEvent.RoundStarted, NotifyTag.Discovered(ordinal),
                [
                    CloudStatusLine(),
                    "检测到有新文件，已进入自动化处理流程…",
                    .. DiscoveryLines(files),
                    $"稳定判定：静默 {_settings.QuietSeconds} 秒且连续 {_settings.StableConfirmRounds} 次大小不变。",
                    $"全部稳定后，等批处理窗口（{_settings.BatchWindowMinutes} 分钟）到期即开始打包。",
                ]),
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 【就绪】—— 窗口期内的文件<b>全部</b>稳定了才发，而且一个窗口只发一次，
    /// 正文里带那一行"准备开始打包。"。
    ///
    /// 判定用 <see cref="RefreshResult.Observing"/> 而不是 <c>_tracker.Count</c>：
    /// 被写入方独占锁住、读不出来的文件会一直留在跟踪表里等解锁，
    /// 拿总数当条件的话这一条永远发不出去。这类文件改为在正文里如实点出来。
    /// </summary>
    private async Task AnnounceStableAsync(RefreshResult refresh, DateTimeOffset nowUtc, CancellationToken ct)
    {
        if (_stableAnnounced || _batch.Count == 0 || refresh.Observing > 0)
        {
            return;
        }

        _stableAnnounced = true;

        // 窗口是本轮第 6 步才开的，这一刻可能还是 null —— 那就意味着"这一轮马上开"。
        DateTimeOffset packAt =
            (_windowOpenedUtc ?? nowUtc) + TimeSpan.FromMinutes(_settings.BatchWindowMinutes);

        List<PackedFile> ready =
        [
            .. _batch.Select(kv => new PackedFile(kv.Key, kv.Value))
                     .OrderBy(f => f.Name, StringComparer.CurrentCulture),
        ];

        _log.Info($"窗口内 {ready.Count} 个文件已全部稳定，等窗口到期即开始打包。");

        await NotifySafeAsync(
            Msg(NotifyEvent.FilesStable, NotifyTag.FilesStable,
                [
                    $"本窗口期内的 {ready.Count} 个文件已全部稳定，不再有变化。",
                    $"源文件总大小：{ByteSize.FormatShort(_batch.Values.Sum())}",
                    .. FileListLines(ready),
                    refresh.Unreadable.Count > 0
                        ? $"另有 {refresh.Unreadable.Count} 个文件仍被占用读不到，不进本轮，会继续等它解锁。"
                        : null,
                    "准备开始打包。",
                    $"打包开始时间：{packAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}（批处理窗口到期）",
                ]),
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 准入判断：文件的修改时间必须同时越过两条线。
    ///
    /// <list type="number">
    /// <item><b>水位线</b>（<c>_state.Checkpoint</c>）—— 严格晚于它，避免重复打包已处理过的文件。
    ///       为空表示不过滤。</item>
    /// <item><b>生效起始日期</b>（工作时间里的"从"）—— 不早于那天的本地零点。
    ///       用户设了这个日期，指的就是"这一天以后的文件才算"；
    ///       旧行为只把它当"哪天开始运行"，于是"起始日期之后、程序启动之前"落下的文件
    ///       被启动时刻的水位线永久挡住，界面上却看不出任何原因。</item>
    /// </list>
    /// </summary>
    private bool PassesCheckpoint(string path)
    {
        DateTimeOffset? checkpoint = _state.Checkpoint;
        DateTimeOffset? floor = _fileFloorUtc;

        if (checkpoint is null && floor is null)
        {
            return true;
        }

        try
        {
            DateTime written = File.GetLastWriteTimeUtc(path);

            if (checkpoint is { } cp && written <= cp.UtcDateTime)
            {
                return false;
            }

            // 起始日期是"从这一天开始"，所以零点整的文件算在内 —— 用 < 排除，不是 <=。
            return floor is not { } from || written >= from.UtcDateTime;
        }
        catch
        {
            return true;
        }
    }

    private bool IsQuarantined(string path) =>
        _state.Quarantined.Any(q => q.Files.Contains(path, StringComparer.OrdinalIgnoreCase));

    // ==================================================================
    //  批次执行 —— 全程 try/finally，finally 无条件复位
    // ==================================================================

    private async Task RunBatchAsync(
        IReadOnlyList<string> requested,
        string? retryId,
        DateTimeOffset roundStartedUtc,
        CancellationToken ct)
    {
        List<string> files = [];
        List<PackedFile> packed = [];
        long totalBytes = 0;

        try
        {
            SetPhase(EnginePhase.Packing);
            _packPercent = 0;
            _packFile = null;
            PublishSnapshot();

            // 只打包仍然存在的文件。源文件在窗口期内被删是常态，
            // 旧版会让整批永远凑不齐，于是永久卡住并每轮重打一次包。
            foreach (string path in requested)
            {
                FileProbeResult probe = _probe.Probe(path);

                if (!probe.Exists)
                {
                    _log.Info($"源文件已不存在，从本批次移除：{path}");
                    continue;
                }

                files.Add(path);
                packed.Add(new PackedFile(path, probe.Length));
                totalBytes += probe.Length;
            }

            if (files.Count == 0)
            {
                _log.Info("本批次的源文件已全部消失，跳过（不产生任何压缩包）。");

                if (retryId is not null)
                {
                    _retries.Remove(retryId);
                }

                // 这一支曾经是整条时间线上唯一的静默处：【就绪】已经发出去了，
                // 正文里还写着"准备开始打包 / 打包开始时间：HH:mm:ss"，
                // 而到了那个时刻用户再也收不到任何消息，只能自己去翻日志。
                //
                // 用【结束】那一位、但换成【取消】这个词：它就是本轮的收尾消息，
                // 订阅【结束】的人要的正是"这一轮怎么了"；而正文里一个压缩包都没有，
                // 标签必须一眼能和成功那条区分开。
                await NotifySafeAsync(
                    Msg(NotifyEvent.RoundFinished, NotifyTag.RoundCancelled,
                        [
                            $"⊘ 本轮取消：准备打包时，这一批的 {requested.Count} 个源文件已全部消失，未产生任何压缩包。",
                            .. MissingFileLines(requested),
                            "常见原因：文件被移走、改名或删除，也可能是写入方保存完成后自行清理了临时文件。",
                            retryId is not null ? $"批次编号：{retryId}（已从重试队列移除，不再重试）" : null,
                            $"共计耗时：{ByteSize.FormatClock(_time.GetUtcNow() - roundStartedUtc)}",
                            NextRoundStartLine(),
                        ]),
                    ct).ConfigureAwait(false);

                return;
            }

            if (_runner is null)
            {
                HandleFailure(PipelineStage.Pack, retryId, files, totalBytes, "压缩程序未初始化。");
                return;
            }

            // ---------- 磁盘预检：放不下就压根不产生文件 ----------
            if (!await PrecheckDiskAsync(files, totalBytes, retryId, ct).ConfigureAwait(false))
            {
                return;
            }

            // ---------- 压缩 ----------
            string archivePath = BuildArchivePath();

            PackRequest request = new(
                archivePath,
                files,
                _settings.Password,
                _settings.CompressionLevel,
                _settings.EncryptFileNames,
                TimeSpan.FromMinutes(_settings.PackTimeoutMinutes),
                VerifyAfterPack: true);

            Progress<PackProgress> progress = new(p =>
            {
                _packPercent = p.Percent;
                _packFile = p.CurrentFile;
            });

            PackResult result = await _runner.CreateAsync(request, progress, ct).ConfigureAwait(false);

            if (result.Outcome == PackOutcome.Cancelled)
            {
                _log.Info("打包已取消。批次状态已复位，未留下任何半成品。");
                return;
            }

            if (!result.Ok || result.ArchivePath is null)
            {
                HandleFailure(PipelineStage.Pack, retryId, files, totalBytes, result.Error ?? "未知的压缩失败。");
                return;
            }

            // 用 7za 报回来的实际数量，而不是清单条数：退出码 1 时可能有文件被跳过
            // （被独占占用、或在打包开始前已消失）。上报清单条数就是在虚报备份内容。
            int archivedCount = result.FileCount;
            int skippedCount = Math.Max(0, files.Count - archivedCount);
            string skipNote = skippedCount > 0
                ? $"，跳过 {skippedCount} 个（被占用或已消失）"
                : string.Empty;

            _log.Info(
                $"压缩完成：{Path.GetFileName(result.ArchivePath)}，" +
                $"{archivedCount} 个文件{skipNote} {ByteSize.Format(totalBytes)} → {ByteSize.Format(result.ArchiveBytes)}" +
                $"（{Ratio(totalBytes, result.ArchiveBytes)}），耗时 {ByteSize.FormatDuration(result.Elapsed)}。");

            _state.TotalArchivesCreated++;
            _state.TotalBytesArchived += result.ArchiveBytes;
            _state.TotalFilesArchived += archivedCount;
            _state.LastPackSuccessUtc = _time.GetUtcNow();

            await NotifySafeAsync(
                Msg(NotifyEvent.PackCompleted, NotifyTag.Packed,
                    [
                        $"本次共 {archivedCount} 个文件打包成功{skipNote}。",
                        $"源文件总大小：{ByteSize.FormatShort(totalBytes)}",
                        .. FileListLines(packed),
                        $"压缩包：{Path.GetFileName(result.ArchivePath)}",
                        $"压缩包大小：{ByteSize.FormatShort(result.ArchiveBytes)}（{Ratio(totalBytes, result.ArchiveBytes)}）",
                        $"打包耗时：{ByteSize.FormatClock(result.Elapsed)}",
                        result.Warnings.Count > 0 ? $"警告 {result.Warnings.Count} 条（归档已校验，可正常解压）" : null,

                        // 下一步是什么，在这一条里就说清楚。非 OneDrive 云盘不能写"上传至 OneDrive"——
                        // 那些客户端程序探测不到，只能说"移到目录"，说得比知道的多就是谎报。
                        _settings.CloudTarget == CloudTarget.OneDrive
                            ? "准备开始上传至OneDrive。"
                            : $"准备移动到{CloudTargetText.PathLabel(_settings.CloudTarget)}。",
                    ]),
                ct).ConfigureAwait(false);

            // ---------- 交付到目标目录 ----------
            SetPhase(EnginePhase.Delivering);
            PublishSnapshot();

            string pathLabel = CloudTargetText.PathLabel(_settings.CloudTarget);
            string? delivered = TryDeliver(result.ArchivePath, out string? deliverError);

            if (delivered is null)
            {
                // 归档本身是好的，只是移不过去。保留它并如实说明位置 ——
                // 绝不因为"移动失败"就重新压缩一遍（那才是磁盘被填满的老路）。
                _log.Error(
                    $"归档已生成但移入{pathLabel}失败：{deliverError}。" +
                    $"归档保留在 {result.ArchivePath}，受临时目录配额管理。");

                HandleFailure(PipelineStage.Deliver, retryId, files, totalBytes, $"移入{pathLabel}失败：{deliverError}");
                return;
            }

            AdvanceCheckpoint(files);

            if (_settings.CloudTarget == CloudTarget.OneDrive)
            {
                _state.PendingUploads.Add(new PendingUpload
                {
                    ArchivePath = delivered,
                    Bytes = result.ArchiveBytes,
                    FileCount = archivedCount,
                    SourceBytes = totalBytes,
                    RoundStartedUtc = roundStartedUtc,
                    DeliveredUtc = _time.GetUtcNow(),
                });

                // 【上传】发在这里 —— 剪切进 OneDrive 目录的那一刻，客户端从这一刻开始传。
                //
                // 旧行为是等云端确认完了才发这一条，于是"上传"和"结束"几乎同时到，
                // 中间那段真正在传的时间（几分钟到几十分钟）界面外一片空白。
                // 上传<b>完成</b>由【结束】负责宣布，两条各司其职。
                await NotifySafeAsync(
                    Msg(NotifyEvent.UploadCompleted, NotifyTag.Uploaded,
                        "正在上传到OneDrive。",
                        $"压缩包：{Path.GetFileName(delivered)}",
                        $"压缩包大小：{ByteSize.FormatShort(result.ArchiveBytes)}",
                        $"包含文件：{archivedCount} 个（源文件总大小 {ByteSize.FormatShort(totalBytes)}）",
                        $"所在目录：{_settings.CloudPath}",
                        CloudStatusLine(),
                        $"程序每 {_settings.UploadPollSeconds} 秒查一次云端是否已收到，" +
                        $"最长等 {_settings.UploadTimeoutMinutes} 分钟；确认收到后会发【结束】。",
                        _settings.ReleaseLocalSpace
                            ? "确认上传完成后会把本地副本改为「仅联机」，释放磁盘空间。"
                            : "本地副本会保留，不释放磁盘空间。"),
                    ct).ConfigureAwait(false);
            }
            else
            {
                // 非 OneDrive：剪切进目录就是本轮的终点。
                //
                // 这条分支存在的理由是<b>没有可靠的替代信号</b>：坚果云、百度网盘、Dropbox
                // 各有各的私有协议，没有一个像 Cloud Files API 那样能被"文件已脱水"证明。
                // 硬要等一个猜出来的信号，只会变成旧版那种死等两小时再谎报成功。
                // 所以边界划在这里 ——【结束】只说程序自己做完的那件事（已移入指定目录），
                // 不提"后续由客户端上传"：程序既没检测过那个客户端，也不该替它承诺。
                await CompleteFolderRoundAsync(
                    delivered,
                    result.ArchiveBytes,
                    archivedCount,
                    totalBytes,
                    roundStartedUtc,
                    ct).ConfigureAwait(false);
            }

            if (retryId is not null)
            {
                _log.Info($"批次 {retryId} 重试成功，已从重试队列移除。");
                _retries.Remove(retryId);
            }
        }
        finally
        {
            // ============================================================
            //  无条件复位。这一段是整个重写里最重要的十行代码：
            //  上面任何一条路径抛异常、返回、被取消，批次状态都会在这里被清空，
            //  因此"用同一批文件反复生成压缩包"在结构上不可能发生。
            // ============================================================
            if (retryId is null)
            {
                _batch.Clear();
                _windowOpenedUtc = null;

                // 这一窗口的【就绪】已经发过、通报也已经数到第 N 次，下一窗口重新开始：
                // 不复位的话，第二批文件永远收不到【就绪】通知，标签还会从【第 N+1 次】接着往下数。
                _stableAnnounced = false;
                _discoveries = 0;
                _announced.Clear();
            }

            _packPercent = 0;
            _packFile = null;

            _zipTemp.SweepIntermediates();
            EnforceQuota();
            _stateStore.Save(_state);

            SetPhase(EnginePhase.Idle);
            PublishSnapshot();
        }
    }

    private async Task<bool> PrecheckDiskAsync(
        IReadOnlyList<string> files,
        long totalBytes,
        string? retryId,
        CancellationToken ct)
    {
        PrecheckResult zipTempCheck = _zipTemp.Precheck(totalBytes, _settings.MinFreeDiskBytes);

        if (!zipTempCheck.Ok)
        {
            _log.Error(zipTempCheck.Reason!);

            await NotifySafeAsync(
                Msg(NotifyEvent.DiskWarning, NotifyTag.Disk,
                    "临时目录所在磁盘空间不足，本次已拒绝打包（未产生任何文件）。",
                    zipTempCheck.Reason!,
                    ProgramDiskLine()),
                ct).ConfigureAwait(false);

            HandleFailure(PipelineStage.Pack, retryId, files, totalBytes, zipTempCheck.Reason!);
            return false;
        }

        if (zipTempCheck.Reason is not null)
        {
            _log.Warn(zipTempCheck.Reason);
        }

        // 归档最终要落到云盘目录，那个卷也得放得下。
        if (!string.IsNullOrWhiteSpace(_settings.CloudPath))
        {
            string pathLabel = CloudTargetText.PathLabel(_settings.CloudTarget);

            PrecheckResult cloudCheck = ZipTempManager.PrecheckPath(
                _settings.CloudPath,
                totalBytes,
                _settings.MinFreeDiskBytes,
                pathLabel);

            if (!cloudCheck.Ok)
            {
                _log.Error(cloudCheck.Reason!);

                await NotifySafeAsync(
                    Msg(NotifyEvent.DiskWarning, NotifyTag.Disk,
                        $"{pathLabel}所在磁盘空间不足，本次已拒绝打包（未产生任何文件）。",
                        cloudCheck.Reason!,
                        ProgramDiskLine()),
                    ct).ConfigureAwait(false);

                HandleFailure(PipelineStage.Pack, retryId, files, totalBytes, cloudCheck.Reason!);
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 非 OneDrive：压缩包剪切进指定目录，本轮到此结束。
    ///
    /// 【结束】通知里<b>不出现 OneDrive 字样，也不声称"云端已收到"</b>——
    /// 程序确实不知道客户端传完了没有，说得比知道的多就是谎报。
    /// 同理也不写"云盘"：这条分支下目标就是用户指定的一个目录，
    /// 后面有没有云盘客户端、传没传、什么时候传完，程序一概不知道。
    /// </summary>
    private async Task CompleteFolderRoundAsync(
        string archivePath,
        long archiveBytes,
        int fileCount,
        long sourceBytes,
        DateTimeOffset roundStartedUtc,
        CancellationToken ct)
    {
        DateTimeOffset now = _time.GetUtcNow();

        _state.LastDeliverSuccessUtc = now;
        Interlocked.Increment(ref _rounds);

        // 目录的称呼取自 PathLabel，跟【打包】里那句"准备移动到…"同一个来源：
        // 前一条说"准备移动到 X"、后一条说"已移入 Y"而 X≠Y，用户会以为中间还有一步。
        string pathLabel = CloudTargetText.PathLabel(_settings.CloudTarget);

        _log.Info($"本轮完成：压缩包已移入{pathLabel} {archivePath}。");

        await NotifySafeAsync(
            Msg(NotifyEvent.RoundFinished, NotifyTag.RoundFinished,
                $"✔ 压缩包已移入{pathLabel}，本次自动化处理已结束。",
                $"压缩包：{Path.GetFileName(archivePath)}（{ByteSize.FormatShort(archiveBytes)}）",
                $"文件数：{fileCount}（源文件总大小 {ByteSize.FormatShort(sourceBytes)}）",
                $"所在目录：{_settings.CloudPath}",
                $"共计耗时：{ByteSize.FormatClock(now - roundStartedUtc)}",
                ProgramDiskLine(),
                NextRoundStartLine()),
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 【第 N 次发现新文件】与【上传】里那一行客户端状态。
    /// 非 OneDrive 模式下没有统一可靠的检测方式，就不写这一行 —— 空着比编一句好。
    /// </summary>
    private string? CloudStatusLine() =>
        _settings.CloudTarget == CloudTarget.OneDrive
            ? OneDriveClientStatus.DescribeNow()
            : null;

    /// <summary>登记失败：交给重试队列做退避，或达到上限后隔离。批次状态由 finally 清空。</summary>
    private void HandleFailure(
        PipelineStage stage,
        string? retryId,
        IReadOnlyList<string> files,
        long totalBytes,
        string reason)
    {
        DateTimeOffset now = _time.GetUtcNow();

        if (stage == PipelineStage.Pack)
        {
            _state.LastPackFailureUtc = now;
            _state.LastPackFailureReason = reason;
        }
        else
        {
            _state.LastDeliverFailureUtc = now;
            _state.LastDeliverFailureReason = reason;
        }

        FailureOutcome outcome = _retries.RecordFailure(
            retryId,
            files,
            totalBytes,
            reason,
            _settings.MaxAttemptsBeforeQuarantine);

        if (outcome.Quarantined is { } quarantined)
        {
            _state.Quarantined.Add(quarantined);

            // 通知在这里是 fire-and-forget，但用的是 NotifySafeAsync，异常不会外泄。
            _ = NotifySafeAsync(
                Msg(NotifyEvent.Quarantined, NotifyTag.Quarantined,
                    $"本批次连续失败 {quarantined.Attempts} 次，已进入隔离区，不再重试。",
                    $"批次编号：{quarantined.Id}",
                    $"文件数：{quarantined.Files.Count}（{ByteSize.FormatShort(quarantined.TotalBytes)}）",
                    $"失败原因：{reason}",
                    "这些文件已从后续窗口中排除，请在界面的「隔离区」里重试或忽略。"),
                CancellationToken.None);

            return;
        }

        _ = NotifySafeAsync(
            Msg(NotifyEvent.Failure, NotifyTag.Failure,
                $"本次处理失败，将自动重试（第 {outcome.Requeued?.Attempts} 次，共 {_settings.MaxAttemptsBeforeQuarantine} 次后隔离）。",
                $"批次编号：{outcome.Requeued?.Id}",
                $"文件数：{files.Count}（{ByteSize.FormatShort(totalBytes)}）",
                $"下次重试：{outcome.Requeued?.NextAttemptUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}",
                $"失败原因：{reason}"),
            CancellationToken.None);
    }

    private string? TryDeliver(string archivePath, out string? error)
    {
        error = null;

        try
        {
            Directory.CreateDirectory(_settings.CloudPath);

            string target = Path.Combine(_settings.CloudPath, Path.GetFileName(archivePath));
            target = MakeUnique(target);

            File.Move(archivePath, target);

            _log.Info($"已移入{CloudTargetText.PathLabel(_settings.CloudTarget)}：{target}");
            return target;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    private void AdvanceCheckpoint(IReadOnlyList<string> files)
    {
        DateTimeOffset newest = _state.Checkpoint ?? DateTimeOffset.MinValue;

        foreach (string path in files)
        {
            try
            {
                DateTimeOffset written = new(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
                if (written > newest)
                {
                    newest = written;
                }
            }
            catch
            {
                // 文件可能刚被删；忽略。
            }
        }

        if (newest > (_state.Checkpoint ?? DateTimeOffset.MinValue))
        {
            _state.Checkpoint = newest;
        }
    }

    /// <summary>
    /// 压缩包文件名：<c>Backup_20260813_180900.7z</c> —— 前缀 + 秒级时间戳，没有别的后缀。
    /// 秒级时间戳只在同一秒内才会重复，而一个批次至少要花掉压缩时间，
    /// 所以 <see cref="MakeUnique"/> 基本用不上；留着只防手工放进来的同名文件。
    /// </summary>
    private string BuildArchivePath()
    {
        string name = ArchiveNaming.Build(_settings.ArchivePrefix, _time.GetLocalNow());
        return MakeUnique(Path.Combine(_zipTemp.Root, name));
    }

    private static string MakeUnique(string path)
    {
        if (!File.Exists(path))
        {
            return path;
        }

        string directory = Path.GetDirectoryName(path) ?? string.Empty;
        string name = Path.GetFileNameWithoutExtension(path);
        string extension = Path.GetExtension(path);

        for (int i = 2; i < 1000; i++)
        {
            string candidate = Path.Combine(directory, $"{name}_{i}{extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine(directory, $"{name}_{Guid.NewGuid():N}{extension}");
    }

    private void EnforceQuota()
    {
        HashSet<string> locked = new(
            _state.PendingUploads.Select(u => u.ArchivePath),
            StringComparer.OrdinalIgnoreCase);

        _zipTemp.Enforce(
            _settings.ZipTempKeepDays,
            _settings.ZipTempMaxCount,
            _settings.ZipTempMaxTotalBytes,
            locked);
    }

    // ==================================================================
    //  待上传归档
    // ==================================================================

    private async Task ProcessPendingUploadsAsync(DateTimeOffset nowUtc, CancellationToken ct)
    {
        if (_state.PendingUploads.Count == 0)
        {
            return;
        }

        if (nowUtc - _lastUploadPollUtc < TimeSpan.FromSeconds(_settings.UploadPollSeconds))
        {
            return;
        }

        _lastUploadPollUtc = nowUtc;
        SetPhase(EnginePhase.WaitingUpload);

        TimeSpan timeout = TimeSpan.FromMinutes(_settings.UploadTimeoutMinutes);
        List<PendingUpload> done = [];

        foreach (PendingUpload upload in _state.PendingUploads)
        {
            ct.ThrowIfCancellationRequested();

            upload.Polls++;

            UploadCheckResult check;

            try
            {
                check = await _uploads.CheckAsync(upload, _settings.ReleaseLocalSpace, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.Warn($"检查上传状态时出错：{upload.FileName}", ex);
                continue;
            }

            if (_settings.ReleaseLocalSpace && !upload.ReleaseRequested)
            {
                upload.ReleaseRequested = true;
            }

            switch (check.Status)
            {
                case UploadStatus.Completed:
                    done.Add(upload);
                    _state.LastDeliverSuccessUtc = nowUtc;

                    _log.Info($"上传已确认：{upload.FileName}" +
                              (check.SpaceReleased ? "，本地空间已释放。" : "。") +
                              (check.Detail is null ? string.Empty : $" {check.Detail}"));

                    string oneDriveLine = check.SpaceReleased
                        ? "OneDrive：✔ 本次上传文件和释放空间成功完成（仅联机）。"
                        : "OneDrive：✔ 云端已确认收到（本地副本保留，未释放空间）。";

                    // 这里<b>只</b>发【结束】。
                    // 【上传】已经在压缩包剪切进 OneDrive 目录的那一刻发过了（见 RunBatchAsync）——
                    // 那才是"正在上传"。两条都发在这一刻的话，用户会在同一秒收到
                    // "正在上传"和"已结束"，中间真正在传的那段时间反而没有任何消息。
                    //
                    // 一轮 = 一个压缩包，所以上传确认就是这一轮的终点。
                    // RoundStartedUtc 随状态持久化，等待期间重启进程也算得出正确耗时。
                    await NotifySafeAsync(
                        Msg(NotifyEvent.RoundFinished, NotifyTag.RoundFinished,
                            oneDriveLine,
                            "本次自动化处理已结束。",
                            $"压缩包：{upload.FileName}",
                            $"压缩包大小：{ByteSize.FormatShort(upload.Bytes)}",
                            $"包含文件：{upload.FileCount} 个",
                            $"上传耗时：{ByteSize.FormatClock(nowUtc - upload.DeliveredUtc)}",
                            check.SpaceReleased ? null : check.Detail,
                            upload.RoundStartedUtc > DateTimeOffset.MinValue
                                ? $"共计耗时：{ByteSize.FormatClock(nowUtc - upload.RoundStartedUtc)}"
                                : null,
                            upload.SourceBytes > 0
                                ? $"源文件总大小：{ByteSize.FormatShort(upload.SourceBytes)} → " +
                                  $"压缩包 {ByteSize.FormatShort(upload.Bytes)}"
                                : null,
                            ProgramDiskLine(),
                            NextRoundStartLine()),
                        ct).ConfigureAwait(false);

                    break;

                case UploadStatus.Vanished:
                    done.Add(upload);
                    _log.Warn($"归档已不在 OneDrive 目录中，停止跟踪：{upload.FileName}。{check.Detail}");
                    break;

                case UploadStatus.TimedOut:
                    done.Add(upload);
                    _state.LastDeliverFailureUtc = nowUtc;
                    _state.LastDeliverFailureReason = $"等待上传超时：{upload.FileName}";
                    _log.Warn($"等待上传超时，停止跟踪：{upload.FileName}。{check.Detail}");
                    break;

                default:
                    if (nowUtc - upload.DeliveredUtc > timeout)
                    {
                        done.Add(upload);

                        // 如实报告，绝不谎报成功。旧版在中文列名匹配失败时死等两小时，
                        // 然后按"超时"处理并且从不释放空间，却仍然发出成功通知。
                        string detail = check.Detail ?? "未能确认上传状态。";

                        _state.LastDeliverFailureUtc = nowUtc;
                        _state.LastDeliverFailureReason = $"未能确认云端已收到：{upload.FileName}";

                        _log.Warn(
                            $"归档 {upload.FileName} 已在 OneDrive 目录中 " +
                            $"{ByteSize.FormatDuration(nowUtc - upload.DeliveredUtc)}，" +
                            $"但仍未能确认上传完成，停止跟踪。{detail}");

                        await NotifySafeAsync(
                            Msg(NotifyEvent.Failure, NotifyTag.Failure,
                                "OneDrive：未能确认云端已收到（不谎报成功）。",
                                $"压缩包：{upload.FileName}",
                                $"压缩包大小：{ByteSize.FormatShort(upload.Bytes)}",
                                $"文件已在 OneDrive 目录中，但 {_settings.UploadTimeoutMinutes} 分钟内未能确认云端收到。",
                                detail,
                                "压缩包没有被删除，本地空间也未释放，请手动检查 OneDrive 同步状态。",
                                ProgramDiskLine()),
                            ct).ConfigureAwait(false);
                    }

                    break;
            }
        }

        if (done.Count > 0)
        {
            foreach (PendingUpload upload in done)
            {
                _state.PendingUploads.Remove(upload);
            }

            _stateStore.Save(_state);
        }
    }

    // ==================================================================
    //  隔离区操作（供 UI 调用）
    //
    //  这两个方法是<b>唯一</b>由界面线程发起的状态变更。它们不直接改引擎内部状态，
    //  而是把操作排进命令队列，由主循环在自己的线程上执行 ——
    //  否则界面线程会与主循环并发写 _batch（普通 Dictionary）和 _state.Quarantined（List），
    //  那是能把字典结构写坏、且事后完全无法复现的一类 bug。
    //
    //  返回值依据的是界面当前看到的那份快照：用户点的按钮对应的批次存在就返回 true。
    // ==================================================================

    /// <summary>把隔离批次放回处理队列，下一轮立即重打一次。</summary>
    public bool RequeueQuarantined(string id) =>
        Enqueue(id, () => ApplyRequeue(id));

    /// <summary>永久忽略一个隔离批次。</summary>
    public bool DiscardQuarantined(string id) =>
        Enqueue(id, () => ApplyDiscard(id));

    /// <summary>
    /// 引擎状态的只读副本，由主循环每轮更新一次。
    /// 直接 <c>_state.Clone()</c> 会与主循环并发读写 List，这里改成读取已发布的副本。
    /// </summary>
    public EngineState StateSnapshot() => Volatile.Read(ref _stateView);

    private bool Enqueue(string id, Action command)
    {
        // 先按界面看到的快照判断这个批次是否存在，避免读 _state（主循环正在写它）。
        if (!Snapshot.Quarantines.Any(q => q.Id == id))
        {
            return false;
        }

        lock (_lifecycleGate)
        {
            if (_loop is { IsCompleted: false })
            {
                _commands.Enqueue(command);
                _monitor.Poke();          // 立刻唤醒，界面无需等一个刷新间隔
                return true;
            }
        }

        // 引擎没在跑，没有第二个线程会碰这些状态，直接就地执行。
        command();
        PublishSnapshot();
        return true;
    }

    /// <summary>在主循环线程上执行界面排进来的操作。每轮开头调用一次。</summary>
    private void DrainCommands()
    {
        while (_commands.TryDequeue(out Action? command))
        {
            try
            {
                command();
            }
            catch (Exception ex)
            {
                _log.Error("执行界面请求的操作时出错，已忽略这一条。", ex);
            }
        }
    }

    private void ApplyRequeue(string id)
    {
        QuarantinedBatch? batch = _state.Quarantined.FirstOrDefault(q => q.Id == id);

        if (batch is null)
        {
            return;
        }

        _state.Quarantined.Remove(batch);

        List<string> alive = batch.Files.Where(File.Exists).ToList();

        if (alive.Count == 0)
        {
            _log.Warn($"隔离批次 {id} 的源文件已全部不存在，直接丢弃。");
            _stateStore.Save(_state);
            return;
        }

        foreach (string path in alive)
        {
            _batch[path] = SafeLength(path);
        }

        _windowOpenedUtc = null;   // 立刻开新窗口，下一轮就会打包

        _log.Info(alive.Count == batch.Files.Count
            ? $"隔离批次 {id} 已放回处理队列（{alive.Count} 个文件）。"
            : $"隔离批次 {id} 已放回处理队列（{alive.Count} 个文件，另有 " +
              $"{batch.Files.Count - alive.Count} 个已不存在，已跳过）。");

        _stateStore.Save(_state);
    }

    private void ApplyDiscard(string id)
    {
        if (_state.Quarantined.RemoveAll(q => q.Id == id) == 0)
        {
            return;
        }

        _log.Info($"已忽略隔离批次 {id}。");
        _stateStore.Save(_state);
    }

    // ==================================================================
    //  辅助
    // ==================================================================

    private async Task WaitForNextRoundAsync(CancellationToken ct)
    {
        TimeSpan wait = TimeSpan.FromSeconds(_settings.RefreshIntervalSeconds);

        // 有事件就立刻醒，没事件就按刷新间隔兜底 —— 事件驱动为主、定时为辅。
        await _monitor.WaitForActivityAsync(wait, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 按用户指定的三段式格式组装一条通知：<c>yyyy-MM-dd HH:mm:ss</c> + <c>【标签】</c> + 若干行正文。
    /// 时间取引擎自己的 <see cref="TimeProvider"/>，所以测试里也是确定的。
    /// </summary>
    private NotifyMessage Msg(NotifyEvent evt, string tag, params string?[] lines) =>
        NotifyMessage.Create(evt, tag, _time.GetLocalNow(), lines);

    /// <summary>
    /// 【结束】里那行"程序所在磁盘"。用户要求带上它 —— 一轮跑完顺手报一次余量，
    /// 比等磁盘告警触发再看要早得多。取不到磁盘信息就返回 null，让这一行整行消失，
    /// 而不是印一个 "0 B" 出去骗人。
    /// </summary>
    private static string? ProgramDiskLine()
    {
        string dir = AppPaths.BaseDirectory;
        DiskSpaceInfo disk = DiskSpace.Query(dir);

        if (!disk.Known)
        {
            return null;
        }

        string label = Path.GetPathRoot(dir)?.TrimEnd(Path.DirectorySeparatorChar) ?? dir;

        return $"程序所在磁盘 {label}：总 {ByteSize.FormatShort(disk.TotalBytes)}，" +
               $"可用 {ByteSize.FormatShort(disk.FreeBytes)}";
    }

    /// <summary>
    /// 【打包】里的编号文件列表：<c>1. name（4.72G）</c>。
    /// 超过 <paramref name="max"/> 条就截断并说明还有多少 ——
    /// 企业微信正文有 4096 字节上限，一个几百文件的批次会把正文整段截掉，
    /// 连后面的"压缩包大小"都看不见。
    /// </summary>
    private static IEnumerable<string> FileListLines(IReadOnlyList<PackedFile> files, int max = 20)
    {
        yield return "文件列表：";

        int shown = Math.Min(files.Count, max);

        for (int i = 0; i < shown; i++)
        {
            yield return $"{i + 1}. {files[i].Name}（{ByteSize.FormatShort(files[i].Bytes)}）";
        }

        if (files.Count > shown)
        {
            yield return $"… 另有 {files.Count - shown} 个文件未列出";
        }
    }

    /// <summary>
    /// 【取消】里那份"已消失的文件"清单：<c>1. name</c>，<b>只有名字没有大小</b>。
    ///
    /// 文件已经不在了，<c>new FileInfo(path).Length</c> 只会抛异常、被
    /// <see cref="SafeLength"/> 吞成 0，正文里印出"（0B）"反而像是"发现了一个空文件"。
    /// 所以这一份清单不带大小 —— 这也正是它不能复用 <see cref="FileListLines"/> 的原因。
    /// </summary>
    private static IEnumerable<string> MissingFileLines(IReadOnlyList<string> paths, int max = 20)
    {
        yield return "已消失的文件：";

        int shown = Math.Min(paths.Count, max);

        for (int i = 0; i < shown; i++)
        {
            yield return $"{i + 1}. {Path.GetFileName(paths[i])}";
        }

        if (paths.Count > shown)
        {
            yield return $"… 另有 {paths.Count - shown} 个文件未列出";
        }
    }

    /// <summary>
    /// 【第 N 次发现新文件】里的"发现新文件"清单，形如
    /// <c>发现新文件：1. a.mp4（4.72G），持续观察中......</c>。
    ///
    /// <b>每条通知都从 1 数</b>，只列这一条带来的文件，和本轮此前发现过多少个无关 ——
    /// 用户的原话："正文内容的序号……和历史发现的数量没有关系，仅当前发现的文件数量"。
    /// 所以它跟标签里那个 N（第几次通报，见 <see cref="NotifyTag.Discovered"/>）不是一回事：
    /// 一次通报两个文件时，标签是【第 4 次】而正文只到 2。
    ///
    /// 条数上限与 <see cref="FileListLines"/> 一致 —— 企业微信正文有 4096 字节上限。
    /// </summary>
    private static IEnumerable<string> DiscoveryLines(IReadOnlyList<PackedFile> found, int max = 20)
    {
        int shown = Math.Min(found.Count, max);

        for (int i = 0; i < shown; i++)
        {
            yield return
                $"发现新文件：{i + 1}. {found[i].Name}" +
                $"（{ByteSize.FormatShort(found[i].Bytes)}），持续观察中......";
        }

        if (found.Count > shown)
        {
            yield return $"… 另有 {found.Count - shown} 个新文件未列出，同样在观察中";
        }
    }

    /// <summary>"工作时段：…"那一行。全天 / 每日时段 / 跨夜 / 生效日期都收在这一行里。</summary>
    private string ScheduleRangeLine()
    {
        ScheduleWindow schedule = ScheduleWindow.FromSettings(_settings);

        string daily = schedule.AllDay
            ? "全天"
            : $"每天 {Hm(schedule.DailyStart)} – {Hm(schedule.DailyEnd)}" +
              (schedule.WrapsMidnight ? "（跨夜）" : string.Empty);

        string range = (schedule.EffectiveFrom, schedule.EffectiveTo) switch
        {
            (null, null) => string.Empty,
            ({ } from, null) => $"，{from:yyyy-MM-dd} 起生效",
            (null, { } to) => $"，有效期至 {to:yyyy-MM-dd}",
            ({ } from, { } to) => $"，生效期 {from:yyyy-MM-dd} ~ {to:yyyy-MM-dd}",
        };

        return $"工作时段：{daily}{range}";
    }

    /// <summary>
    /// 【收工】与【启动】里那一行"下一次的工作开始时间"。
    ///
    /// 生效日期整体过期时<b>不编一个时间出来</b>，而是如实说明不会再有下一轮 ——
    /// 那是个真需要用户去动设置的状态，糊过去等于让程序静默停摆。
    /// </summary>
    private static string NextStartLine(ScheduleWindow schedule, DateTimeOffset nowLocal) =>
        schedule.NextActivation(nowLocal) is { } next
            ? $"下一次的工作开始时间：{next:yyyy-MM-dd HH:mm}"
            : "下一次的工作开始时间：没有了 —— 生效日期已过期，请到设置里改「生效日期」。";

    /// <summary>
    /// 【结束】最后那一行"下一次的工作开始时间"。
    ///
    /// 和【收工】那一行（<see cref="NextStartLine"/>）不能共用一个实现：一轮处理跑完的时候
    /// 程序通常<b>还在</b>工作时段里，<see cref="ScheduleWindow.NextActivation"/> 在那一刻
    /// 返回的是"现在"，印出去就是"下一次的工作开始时间：（刚刚过去的那一秒）"。
    ///
    /// 时段还开着的时候，真实答案不是某个钟点而是"等下一个文件"——
    /// 就照这么说。编一个"明天 09:00"出来更糟：程序马上就会接着处理新文件，
    /// 用户看了那一行却以为今天不会再动了。
    /// </summary>
    private string NextRoundStartLine()
    {
        ScheduleWindow schedule = ScheduleWindow.FromSettings(_settings);
        DateTimeOffset nowLocal = _time.GetLocalNow();

        if (schedule.IsActive(nowLocal))
        {
            return schedule.NeverCloses
                ? "下一次的工作开始时间：全天监控，一发现新文件就立刻开始下一轮。"
                : $"下一次的工作开始时间：仍在工作时段内（{Hm(schedule.DailyStart)} – {Hm(schedule.DailyEnd)}），" +
                  "一发现新文件就立刻开始下一轮。";
        }

        // 时段已经关了也照样会走到这里：等云端确认不受时段限制（见 ProcessPendingUploadsAsync），
        // 一个 17:59 交付的包完全可能在 18:20 才确认完成。
        return schedule.NextOpening(nowLocal) is { } next
            ? $"下一次的工作开始时间：{next:yyyy-MM-dd HH:mm}"
            : "下一次的工作开始时间：没有了 —— 生效日期已过期，请到设置里改「生效日期」。";
    }

    /// <summary>时段里的 <c>HH:mm</c>。固定 InvariantCulture，理由同 <c>NotifyMessage.TimeText</c>。</summary>
    private static string Hm(TimeOnly time) =>
        time.ToString("HH\\:mm", System.Globalization.CultureInfo.InvariantCulture);

    private async Task NotifySafeAsync(NotifyMessage message, CancellationToken ct)
    {
        try
        {
            if ((_settings.EnabledEvents & message.Event) == 0)
            {
                return;
            }

            NotifyResult result = await _notify.SendAsync(message, ct).ConfigureAwait(false);

            if (!result.Ok)
            {
                _log.Warn($"通知发送失败（{_notify.ChannelName}，尝试 {result.Attempts} 次）：{result.Error}");
            }
        }
        catch (Exception ex)
        {
            // 通知层承诺不抛异常，但这里再兜一层：
            // 旧版正是因为通知抛异常穿过了状态复位代码，才把"钉钉限流"升级成"磁盘被打满"。
            _log.Warn("发送通知时出现异常，已忽略（不影响备份流程）。", ex);
        }
    }

    private void SetPhase(EnginePhase phase) => _phase = phase;

    private void PublishSnapshot()
    {
        try
        {
            Volatile.Write(ref _snapshot, BuildSnapshot());
            Volatile.Write(ref _stateView, _state.Clone());
        }
        catch (Exception ex)
        {
            // 构造快照绝不允许影响引擎运行。
            _log.Debug($"构造状态快照失败：{ex.Message}");
        }
    }

    private EngineSnapshot BuildSnapshot()
    {
        ScheduleWindow schedule = ScheduleWindow.FromSettings(_settings);
        DateTimeOffset nowLocal = _time.GetLocalNow();
        bool active = schedule.IsActive(nowLocal);

        IReadOnlyList<ArchiveInfo> archives = _zipTemp.ListArchives();
        DiskSpaceInfo disk = DiskSpace.Query(_zipTemp.Root);

        DateTimeOffset? windowCloses = _windowOpenedUtc is { } opened
            ? (opened + TimeSpan.FromMinutes(_settings.BatchWindowMinutes)).ToLocalTime()
            : null;

        return new EngineSnapshot(
            _phase,
            EnginePhaseText.Describe(_phase),
            Running: _phase is not (EnginePhase.Stopped or EnginePhase.Stopping),
            ScheduleActive: active,
            NextActivationLocal: active ? null : schedule.NextActivation(nowLocal),
            TrackedCount: _tracker.Count,
            BatchFileCount: _batch.Count,
            BatchBytes: _batch.Values.Sum(),
            WindowClosesAtLocal: windowCloses,
            PackPercent: _packPercent,
            PackCurrentFile: _packFile,
            RetryCount: _retries.Count,
            NextRetryLocal: _retries.NextAttemptUtc?.ToLocalTime(),
            QuarantineCount: _state.Quarantined.Count,
            PendingUploadCount: _state.PendingUploads.Count,
            ArchivesCreated: _state.TotalArchivesCreated,
            BytesArchived: _state.TotalBytesArchived,
            FilesArchived: _state.TotalFilesArchived,
            ZipTempArchiveCount: archives.Count,
            ZipTempBytes: archives.Sum(a => a.Bytes),
            FreeDiskBytes: disk.FreeBytes,
            CloudTarget: _settings.CloudTarget,
            LastPackSuccessLocal: _state.LastPackSuccessUtc?.ToLocalTime(),
            LastPackFailureLocal: _state.LastPackFailureUtc?.ToLocalTime(),
            LastPackFailureReason: _state.LastPackFailureReason,
            LastDeliverSuccessLocal: _state.LastDeliverSuccessUtc?.ToLocalTime(),
            LastDeliverFailureLocal: _state.LastDeliverFailureUtc?.ToLocalTime(),
            LastDeliverFailureReason: _state.LastDeliverFailureReason,
            TrackedFiles: _tracker.Files
                .Select(f => new TrackedFileView(
                    f.Path, f.FileName, f.Length, f.State, f.FirstSeenUtc.ToLocalTime(), f.QuietProbes))
                .OrderBy(f => f.FileName, StringComparer.CurrentCulture)
                .ToList(),
            BatchFiles: _batch
                .Select(kv => new BatchFileView(kv.Key, Path.GetFileName(kv.Key), kv.Value))
                .OrderBy(f => f.FileName, StringComparer.CurrentCulture)
                .ToList(),
            PendingUploads: _state.PendingUploads
                .Select(u => new PendingUploadView(
                    u.ArchivePath, u.FileName, u.Bytes, u.FileCount,
                    u.DeliveredUtc.ToLocalTime(), u.ReleaseRequested, u.Polls))
                .ToList(),
            Retries: _retries.Entries
                .Select(e => new RetryView(
                    e.Id, e.Files.Count, e.Attempts, e.NextAttemptUtc.ToLocalTime(), e.LastError))
                .ToList(),
            Quarantines: _state.Quarantined
                .Select(q => new QuarantineView(
                    q.Id, q.Files.Count, q.TotalBytes, q.Reason, q.Attempts,
                    q.QuarantinedUtc.ToLocalTime(), q.Files))
                .ToList());
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

    private static string Ratio(long original, long compressed)
    {
        if (original <= 0)
        {
            return "—";
        }

        double percent = compressed * 100.0 / original;
        return $"{percent:0.#}%";
    }
}

/// <param name="Path">源文件完整路径。</param>
/// <param name="Bytes">压缩前的大小。</param>
/// <summary>
/// 进了本批次的一个源文件。通知里的编号列表要"文件名（大小）"，
/// 而 <c>_batch</c> 那本字典在 finally 里已经被清空了，所以列表必须在打包前就抄出来。
/// </summary>
internal sealed record PackedFile(string Path, long Bytes)
{
    public string Name => System.IO.Path.GetFileName(Path);
}
