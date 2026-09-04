using NewAutoZip.Core.Diagnostics;

namespace NewAutoZip.Core.Watching;

public enum TrackedFileState
{
    /// <summary>还在观察：大小/时间仍在变，或静默期未满。</summary>
    Observing,

    /// <summary>已就绪，可以打包。</summary>
    Ready,

    /// <summary>存在但读不了（写入方用了独占锁，或权限不足）。</summary>
    Unreadable,
}

public sealed class TrackedFile
{
    internal TrackedFile(string path, DateTimeOffset now)
    {
        Path = path;
        FirstSeenUtc = now;
        LastChangeUtc = now;
    }

    public string Path { get; }

    public long Length { get; internal set; }

    public DateTimeOffset LastWriteUtc { get; internal set; }

    public DateTimeOffset FirstSeenUtc { get; }

    /// <summary>最后一次观察到大小或修改时间发生变化的时刻。</summary>
    public DateTimeOffset LastChangeUtc { get; internal set; }

    /// <summary>静默期满足之后，连续多少次探测都没有变化。</summary>
    public int QuietProbes { get; internal set; }

    public int UnreadableProbes { get; internal set; }

    /// <summary>
    /// 连续读不到的<b>累计探测时长</b>，与设置里的"读不到时最多等"比较。
    ///
    /// 为什么不直接用"第一次读不到的时刻"到现在的墙上时间差：工作时段之外主循环根本不探测，
    /// 打包和上传也会占住主循环好一阵。用墙上时间差的话，关一夜机再开工，
    /// 所有锁着的文件会在恢复后的第一次探测里集体"到点"，白刷一屏告警。
    /// 这里只累加"两次探测之间真正过去的时间"，且每次累加有上限（见 <c>_probeStepCap</c>）。
    /// </summary>
    public TimeSpan UnreadableFor { get; internal set; }

    /// <summary>上一次探测这个文件的时刻，用来算 <see cref="UnreadableFor"/> 的增量。</summary>
    internal DateTimeOffset LastProbeUtc { get; set; }

    public TrackedFileState State { get; internal set; } = TrackedFileState.Observing;

    /// <summary>是否已经完成过初始化探测（用来区分"首次见到"和"后续复查"）。</summary>
    internal bool Initialized { get; set; }

    public string FileName => System.IO.Path.GetFileName(Path);

    public override string ToString() => $"{FileName} ({Length} B, {State})";
}

public sealed record RefreshResult(
    IReadOnlyList<TrackedFile> Ready,
    IReadOnlyList<string> Vanished,
    IReadOnlyList<TrackedFile> Unreadable,
    IReadOnlyList<string> GaveUp,
    int Observing)
{
    public static RefreshResult Empty { get; } = new([], [], [], [], 0);

    public int Total => Ready.Count + Unreadable.Count + Observing;
}

/// <summary>
/// 稳定性判定。
///
/// 旧版的三个致命问题都在这里修掉：
///
/// 1. <b>永久假死</b>：旧版用 <c>f.LastWriteTime &lt;= windowDeadline</c> 过滤候选文件 ——
///    一个在窗口到期时仍在写入的文件会被永久排除在候选之外，但它先前留在 _files 里的
///    条目 IsStable 永远是 false，于是 allStable 永远不成立，整个程序静默停摆。
///    新版没有 allStable 这个概念：窗口关闭时<b>取走当时已就绪的文件</b>，未就绪的留到下一轮。
///
/// 2. <b>条目永不移除</b>：旧版从不从 _files 移除任何条目，文件被删除或改名后同样导致
///    allStable 永假。新版每次 <see cref="Refresh"/> 都剔除已消失的文件。
///
/// 3. <b>就绪太慢</b>：旧版需要走完 Writing → StableRound1 → StableRound2 →
///    PackConfirmRound1 → PackConfirmRound2 五个阶段，即 5 × StableSeconds = 300 秒，
///    恰好撞上默认 5 分钟的批处理窗口。新版是"静默期 + 可配置确认轮数"，默认约 70 秒。
/// </summary>
public sealed class StabilityTracker
{
    private readonly Dictionary<string, TrackedFile> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeProvider _time;
    private readonly IFileProbe _probe;
    private readonly IAppLogger _log;

    private int _quietSeconds = 60;
    private int _confirmRounds = 2;

    /// <summary>读不到的文件最多等多久（累计探测时长）。对应设置项 UnreadableGiveUpMinutes。</summary>
    private TimeSpan _unreadableGiveUp = TimeSpan.FromMinutes(20);

    /// <summary>
    /// 单次探测最多能给 <see cref="TrackedFile.UnreadableFor"/> 记多少时间。
    ///
    /// 两个作用：主循环被打包/上传占住、或者干脆停在工作时段外时，
    /// 恢复后的第一次探测不会把整段空白都算成"等过了"；
    /// 同时它不超过放弃阈值的一半，于是"放弃"永远需要至少两次真实探测，
    /// 不可能被一次时间跳变（NTP 校时、休眠唤醒）一步凑满。
    /// </summary>
    private TimeSpan _probeStepCap = TimeSpan.FromSeconds(15);

    public StabilityTracker(TimeProvider time, IFileProbe probe, IAppLogger log)
    {
        _time = time;
        _probe = probe;
        _log = log;
    }

    public int Count => _files.Count;

    public IReadOnlyCollection<TrackedFile> Files => _files.Values;

    public void Configure(
        int quietSeconds,
        int confirmRounds,
        int refreshIntervalSeconds = 5,
        int unreadableGiveUpMinutes = 20)
    {
        _quietSeconds = Math.Max(1, quietSeconds);
        _confirmRounds = Math.Max(1, confirmRounds);
        _unreadableGiveUp = TimeSpan.FromMinutes(Math.Max(1, unreadableGiveUpMinutes));

        // 正常一轮的间隔给三倍余量，再兜一个 30 秒的底（间隔设成 1 秒时，
        // 一轮偶尔跑到十几秒是正常的，不该因此少记时间）；最后压到阈值的一半以内。
        double capSeconds = Math.Max(Math.Max(1, refreshIntervalSeconds) * 3, 30);
        _probeStepCap = TimeSpan.FromSeconds(Math.Min(capSeconds, _unreadableGiveUp.TotalSeconds / 2));
    }

    /// <summary>登记一个候选文件。已在跟踪中则忽略。返回 true 表示新加入。</summary>
    public bool Observe(string path)
    {
        if (_files.ContainsKey(path))
        {
            return false;
        }

        _files[path] = new TrackedFile(path, _time.GetUtcNow());
        return true;
    }

    public bool IsTracked(string path) => _files.ContainsKey(path);

    /// <summary>
    /// 复查全部跟踪中的文件，返回本轮的状态迁移结果。
    /// 已消失的文件在此被移除 —— 这一步是修掉"静默假死"的关键。
    /// </summary>
    public RefreshResult Refresh()
    {
        if (_files.Count == 0)
        {
            return RefreshResult.Empty;
        }

        DateTimeOffset now = _time.GetUtcNow();
        TimeSpan quiet = TimeSpan.FromSeconds(_quietSeconds);

        List<TrackedFile> ready = [];
        List<string> vanished = [];
        List<TrackedFile> unreadable = [];
        List<string> gaveUp = [];
        int observing = 0;

        // 复制键，因为循环内会修改字典。
        foreach (string path in _files.Keys.ToArray())
        {
            TrackedFile file = _files[path];
            FileProbeResult probe = _probe.Probe(path);

            if (!probe.Exists)
            {
                _files.Remove(path);
                vanished.Add(path);
                continue;
            }

            // 距上次探测这个文件真正过去了多久。首次探测记 0；时钟被回拨记 0；
            // 太长的空白（停在工作时段外、打包占住主循环）压到上限。
            TimeSpan since = file.LastProbeUtc == default ? TimeSpan.Zero : now - file.LastProbeUtc;

            if (since < TimeSpan.Zero)
            {
                since = TimeSpan.Zero;
            }
            else if (since > _probeStepCap)
            {
                since = _probeStepCap;
            }

            file.LastProbeUtc = now;

            if (!probe.Readable)
            {
                file.State = TrackedFileState.Unreadable;
                file.UnreadableProbes++;
                file.UnreadableFor += since;

                if (file.UnreadableFor >= _unreadableGiveUp)
                {
                    _files.Remove(path);
                    gaveUp.Add(path);
                    _log.Warn(
                        $"文件已连续 {_unreadableGiveUp.TotalMinutes:0} 分钟无法读取（探测 {file.UnreadableProbes} 次），" +
                        $"本轮放弃跟踪：{path}（被独占锁定或权限不足）");
                }
                else
                {
                    unreadable.Add(file);
                }

                continue;
            }

            file.UnreadableProbes = 0;
            file.UnreadableFor = TimeSpan.Zero;

            bool changed = !file.Initialized
                || probe.Length != file.Length
                || probe.LastWriteUtc != file.LastWriteUtc;

            if (changed)
            {
                file.Length = probe.Length;
                file.LastWriteUtc = probe.LastWriteUtc;
                file.QuietProbes = 0;
                file.State = TrackedFileState.Observing;

                // 首次探测时，用"文件自身的修改时间"作为静默起点：
                // 一个躺了两小时没人动的旧文件应当立刻可以就绪，而不是再等一个静默期。
                file.LastChangeUtc = file.Initialized
                    ? now
                    : (probe.LastWriteUtc < now ? probe.LastWriteUtc : now);

                file.Initialized = true;
                observing++;
                continue;
            }

            if (now - file.LastChangeUtc < quiet)
            {
                file.State = TrackedFileState.Observing;
                observing++;
                continue;
            }

            file.QuietProbes++;

            if (file.QuietProbes >= _confirmRounds)
            {
                file.State = TrackedFileState.Ready;
                ready.Add(file);
            }
            else
            {
                file.State = TrackedFileState.Observing;
                observing++;
            }
        }

        return new RefreshResult(ready, vanished, unreadable, gaveUp, observing);
    }

    /// <summary>
    /// 全目录对账扫描：把目录里符合条件但尚未跟踪的文件补进来。
    /// FileSystemWatcher 的缓冲区溢出会静默丢事件，这是唯一的补救手段。
    /// </summary>
    /// <param name="discovered">
    /// 可选的收集器：补进来的路径会追加到这里。
    /// 通知层要按名字列出"发现新文件"，光有个数量不够 ——
    /// 而返回值仍是数量，老调用点与测试不受影响。
    /// </param>
    public int Reconcile(
        string root,
        bool recurse,
        FileFilter filter,
        Func<string, bool>? alreadyHandled = null,
        ICollection<string>? discovered = null)
    {
        int added = 0;

        foreach (string path in _probe.Enumerate(root, recurse))
        {
            if (_files.ContainsKey(path) || !filter.Accept(path))
            {
                continue;
            }

            if (alreadyHandled is not null && alreadyHandled(path))
            {
                continue;
            }

            _files[path] = new TrackedFile(path, _time.GetUtcNow());
            discovered?.Add(path);
            added++;
        }

        return added;
    }

    public void Forget(IEnumerable<string> paths)
    {
        foreach (string path in paths)
        {
            _files.Remove(path);
        }
    }

    public void Clear() => _files.Clear();
}
