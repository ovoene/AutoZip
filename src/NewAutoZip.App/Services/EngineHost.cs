using System.IO;
using NewAutoZip.Core.Configuration;
using NewAutoZip.Core.Diagnostics;
using NewAutoZip.Core.Notifications;
using NewAutoZip.Core.OneDrive;
using NewAutoZip.Core.Packing;
using NewAutoZip.Core.Pipeline;
using NewAutoZip.Core.Storage;

namespace NewAutoZip.App.Services;

/// <summary>
/// 界面与引擎之间唯一的接缝。
///
/// <b>这是整套程序里唯一装配依赖的地方</b> —— 真实的 OneDrive 上传判定
/// （<see cref="OneDriveUploader"/>）与通知出口（<see cref="NotificationHub"/>）
/// 都只在这里被塞进 <see cref="BackupEngine"/>。引擎自己的默认值是安全的空实现，
/// 所以单元测试永远不会意外碰到真实文件系统或真实网络。
///
/// 界面只调这个类的方法，从不直接碰 <see cref="BackupEngine"/> 的构造与装配。
/// </summary>
public sealed class EngineHost : IAsyncDisposable
{
    private readonly IAppLogger _log;
    private readonly SettingsStore _store;
    private readonly BackupEngine _engine;

    /// <summary>
    /// 只用来数云盘目录里的包，给 <see cref="DescribeCloudQuota"/> 用。
    ///
    /// 引擎自己也有一个，但那个是私有的、且只在引擎跑起来后才配好路径 ——
    /// 而这里要回答的恰恰是"引擎停着的时候容量卡片显示什么"。
    ///
    /// <b>只调 <c>ListArchives</c>，永远不调 <c>Enforce</c>。</b>
    /// 云盘目录里的包是备份本体，不是中转站（见 <see cref="CloudQuota"/> 的类注释）。
    /// </summary>
    private readonly ZipTempManager _cloudScanner;

    public EngineHost(IAppLogger log)
    {
        _log = log;
        _store = new SettingsStore(AppPaths.SettingsFile);
        _engine = new BackupEngine(log);
        _cloudScanner = new ZipTempManager(log, TimeProvider.System);

        Settings = _store.Load(out string? warning);
        Settings.ClampToLimits();
        LoadWarning = warning;

        if (warning is not null)
        {
            _log.Warn($"读取配置时有问题：{warning}");
        }
    }

    /// <summary>当前生效的配置。界面改动经 <see cref="SaveSettings"/> 提交。</summary>
    public AppSettings Settings { get; private set; }

    /// <summary>配置文件损坏或被手改过时的提示，界面用 InfoBar 显示。</summary>
    public string? LoadWarning { get; }

    public EngineSnapshot Snapshot => _engine.Snapshot;

    public bool IsRunning => _engine.IsRunning;

    public long CompletedRounds => _engine.CompletedRounds;

    public string ZipTempDirectory => _engine.ZipTempDirectory;

    public string SettingsFilePath => _store.Path;

    // ==================================================================
    //  校验
    // ==================================================================

    /// <summary>
    /// 启动前校验。一次列出全部问题，而不是像旧版那样弹一串 MessageBox
    /// 让用户逐个确认（改一个、再点开始、再弹下一个）。
    /// </summary>
    public ValidationReport Validate(AppSettings settings) =>
        SettingsValidator.Validate(settings, AppPaths.SevenZipExe);

    public ValidationReport Validate() => Validate(Settings);

    // ==================================================================
    //  生命周期
    // ==================================================================

    /// <summary>
    /// 启动引擎。校验不通过时<b>不启动</b>并把问题清单交给界面显示 ——
    /// 旧版在这种情况下会照样把状态灯点亮，界面在撒谎。
    /// </summary>
    public bool TryStart(out ValidationReport report)
    {
        report = Validate();

        if (!report.CanStart)
        {
            _log.Warn($"配置校验未通过，未启动：{report.ToText()}");
            return false;
        }

        foreach (ValidationIssue issue in report.Warnings)
        {
            _log.Warn($"配置提醒：{issue.Message}");
        }

        // 这三个参数就是整套程序的装配点。
        return _engine.Start(
            Settings,
            NotificationHub.Create(Settings, _log),
            new OneDriveUploader(_log),
            AppPaths.SevenZipExe);
    }

    public Task StopAsync() => _engine.StopAsync();

    public void Nudge() => _engine.Nudge();

    public bool RequeueQuarantined(string id) => _engine.RequeueQuarantined(id);

    public bool DiscardQuarantined(string id) => _engine.DiscardQuarantined(id);

    // ==================================================================
    //  配置
    // ==================================================================

    /// <summary>
    /// 保存配置。密码与 Webhook 由 <see cref="SettingsStore"/> 用 DPAPI 加密落盘。
    /// 引擎正在运行时新配置<b>不会</b>热切换 —— 界面负责提示"重启引擎后生效"，
    /// 免得出现"一半旧配置一半新配置"的中间状态。
    /// </summary>
    public void SaveSettings(AppSettings settings)
    {
        settings.ClampToLimits();
        _store.Save(settings);
        Settings = settings;

        _log.Info("配置已保存。");
    }

    /// <summary>
    /// 只保存外观（主题模式 + 强调色 + 背景图），其余字段一律沿用磁盘上的现值。
    ///
    /// 标题栏上那个一键切换必须<b>立刻落盘</b> —— 它周围没有"保存"按钮，
    /// 用户点完就换页/关窗，不存等于下次启动又回到原来的主题。
    ///
    /// 但绝对不能顺手调 <see cref="SaveSettings"/> 把整份界面配置写下去：
    /// 设置页上可能正躺着一堆没提交的编辑（甚至是打了一半的路径），
    /// 一次换肤把它们悄悄提交了，比不保存要坏得多。所以这里从当前生效的配置
    /// 克隆一份，只改外观那几个字段。
    ///
    /// 参数是一个 <see cref="AppearanceSpec"/> 而不是一串散参数：外观项以后还会加，
    /// 散参数每加一项都要改签名、改调用点、改比较条件，漏一处就是"选了但不生效"。
    /// </summary>
    public void SaveAppearance(AppearanceSpec appearance)
    {
        AppSettings next = Settings.Clone();
        appearance.WriteTo(next);

        next.ClampToLimits();
        _store.Save(next);
        Settings = next;

        AppearanceSpec saved = AppearanceSpec.From(next);

        _log.Debug(
            $"外观已保存：{saved.Theme} / " +
            $"{(saved.AccentColor.Length == 0 ? "系统强调色" : saved.AccentColor)} / " +
            $"背景图 {(saved.HasBackground ? $"{saved.BackgroundImagePath}（模糊 {saved.BackgroundBlur}、不透明度 {saved.BackgroundOpacity}%）" : "无")}。");
    }

    public AppSettings ReloadSettings()
    {
        Settings = _store.Load(out string? warning);
        Settings.ClampToLimits();

        if (warning is not null)
        {
            _log.Warn($"重新读取配置时有问题：{warning}");
        }

        return Settings;
    }

    /// <summary>界面上「测试发送」按钮：忽略事件勾选，直接发一条。</summary>
    public Task<NotifyResult> TestNotifyAsync(AppSettings settings, CancellationToken ct) =>
        NotificationHub.TestAsync(settings, _log, ct);

    // ==================================================================
    //  恢复
    //
    //  「恢复」页要的四件事全从这里走。界面不碰 SevenZipRunner，
    //  也不碰 RestoreDrillService —— 那两个都要知道 7za 在哪、临时目录在哪、
    //  密码从哪来，这些装配知识只应该存在于这个类里。
    // ==================================================================

    /// <summary>
    /// 现在能拿来恢复的包。临时目录与云盘目录各扫一遍，新的排前面。
    ///
    /// 两个目录都扫，是因为一个包在它的生命周期里会先后待在这两处：
    /// 刚打好时在临时目录，投递之后在云盘目录。只扫一处必然有一半的包看不见。
    /// </summary>
    public IReadOnlyList<ArchiveInfo> ListRestorableArchives()
    {
        List<ArchiveInfo> found = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        foreach (string directory in new[] { Settings.ResolveZipTemp(), Settings.CloudPath })
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                continue;
            }

            // 同一个目录被配置两次（临时目录就设在云盘目录里）时只扫一遍，
            // 否则列表里每个包都会出现两次。
            if (!seen.Add(NormalizeDirectory(directory)))
            {
                continue;
            }

            ZipTempManager scanner = new(_log, TimeProvider.System);
            scanner.Configure(directory);

            found.AddRange(scanner.ListArchives());
        }

        // ListArchives 给的是从旧到新，恢复场景要的恰好相反 ——
        // 十有八九是想拿最近那个包。
        found.Sort((a, b) => b.CreatedUtc.CompareTo(a.CreatedUtc));

        return found;
    }

    /// <summary>解之前先看看里面有什么。密码不对时 <c>WrongPassword</c> 为 true。</summary>
    public Task<ArchiveListing> ListArchiveContentsAsync(
        string archivePath,
        string password,
        CancellationToken ct) =>
        NewRunner().ListAsync(
            archivePath,
            password,
            TimeSpan.FromMinutes(Settings.DrillTimeoutMinutes),
            ct);

    /// <summary>把整个包解到指定目录。</summary>
    public Task<ExtractResult> ExtractAsync(
        string archivePath,
        string targetDirectory,
        string password,
        IProgress<PackProgress>? progress,
        CancellationToken ct) =>
        NewRunner().ExtractAsync(
            new ExtractRequest(
                archivePath,
                targetDirectory,
                password,
                TimeSpan.FromMinutes(Settings.DrillTimeoutMinutes),
                Overwrite: true),
            progress,
            ct);

    /// <summary>
    /// 手动演练一个包 —— 解出来、逐条核对、立刻删干净，不在磁盘上留东西。
    ///
    /// 密码<b>从磁盘上重新读</b>，不用内存里这份。演练要回答的问题正是
    /// "settings.json 里存着的那个密码，现在还解得开这个包吗"；
    /// 拿内存里的副本去验，等于自己考自己。
    /// </summary>
    public Task<DrillResult> RunDrillAsync(string archivePath, CancellationToken ct)
    {
        AppSettings onDisk = _store.Load(out _);
        onDisk.ClampToLimits();

        RestoreDrillService drill = new(NewRunner(), _log);

        return drill.RunAsync(
            archivePath,
            onDisk.Password,
            onDisk.ResolveZipTemp(),
            TimeSpan.FromMinutes(onDisk.DrillTimeoutMinutes),
            onDisk.MinFreeDiskBytes,
            ct);
    }

    /// <summary>
    /// 演练与解压默认用的密码 —— 磁盘上现存的那个。
    ///
    /// 界面把它填进密码框当默认值，用户可以改：换过密码的老包需要旧密码才解得开，
    /// 而那种包恰恰是最需要能被恢复出来的。
    /// </summary>
    public string CurrentPassword => Settings.Password;

    private SevenZipRunner NewRunner() => new(AppPaths.SevenZipExe, _log);

    // ==================================================================
    //  云盘容量
    // ==================================================================

    /// <summary>
    /// 按<b>磁盘上那份配置</b>算一遍容量账。引擎停着时总览的容量卡片用它。
    ///
    /// 引擎在跑的时候快照里就带着这份数字（<c>BackupEngine.BuildSnapshot</c> 每轮算），
    /// 停了之后那四个字段全是 0 —— 而"0 总容量"在界面上的含义是"没配预算，把卡片藏起来"。
    /// 用户明明填了容量却看不见卡片，就是这么来的。
    ///
    /// <b>这里会真的枚举一次目录，别放到 4 Hz 的轮询路径上。</b>
    /// 调用点只有 <c>MainViewModel.SyncCloudDisplay</c> 那几个（保存 / 重新读取 /
    /// 引擎启停 / 构造），与云盘类型的同步点是同一批。
    ///
    /// 没填总容量时<b>不扫目录</b>（那是一次白花的枚举），但仍要走一遍
    /// <see cref="CloudQuota.Evaluate"/>：本地硬盘的容量不需要谁来填，
    /// 卷的三个数照样要显示出来。远程/云盘没填预算时它会返回
    /// <see cref="CloudQuotaStatus.NotConfigured"/>，卡片自然还是藏着。
    /// </summary>
    public CloudQuotaStatus DescribeCloudQuota()
    {
        // 每次都重新 Configure：保存配置可能刚把 CloudPath 改到别处，
        // 只在构造时配一次的话，改完路径算的还是旧目录。
        _cloudScanner.Configure(Settings.CloudPath);

        long used = Settings.CloudQuotaBytes > 0 ? CloudQuota.MeasureUsed(_cloudScanner) : 0;

        return CloudQuota.Evaluate(Settings, used);
    }

    // ==================================================================
    //  清空历史记录
    // ==================================================================

    /// <summary>
    /// 清空之后会发生什么 —— 界面拿它拼确认框正文。
    ///
    /// 状态从引擎取：引擎停着时它会现读磁盘那一份，运行中则给内存里的视图。
    /// 界面自己 new 一个 <see cref="StateStore"/> 去读也能读到，但那样就有
    /// 两条通往同一个文件的路，日后改路径必定漏掉一条。
    /// </summary>
    public ClearHistoryAdvice DescribeClearHistory() =>
        ClearHistoryAdvice.For(Settings, _engine.StateView);

    /// <summary>
    /// 把运行状态复位成"从未运行过"，并按需删掉磁盘上的日志文件。
    ///
    /// <b>引擎必须先停。</b>运行中的引擎持有状态的内存副本，
    /// 下一次保存会把旧状态原样写回去 —— 用户看到的是"点了没反应"。
    /// 拦这一道的是 <see cref="BackupEngine.ResetState"/>，不指望界面记得禁用按钮。
    ///
    /// <b>压缩包一个都不删。</b>清的是账本，不是备份。
    /// </summary>
    public bool ClearHistory(bool deleteLogFiles, out string message)
    {
        if (!_engine.ResetState(out string? error))
        {
            message = _engine.IsRunning
                ? "引擎正在运行，请先停止再清空历史记录。"
                : $"清空运行状态失败：{error}";

            return false;
        }

        string tail = string.Empty;

        if (deleteLogFiles)
        {
            int deleted = DeleteLogFiles(out string? logError);

            tail = logError is null
                ? $"，并删除了 {deleted} 个日志文件"
                : $"。日志文件未能全部删除（{logError}）";
        }

        message = $"历史记录已清空{tail}。下次启动将按首次运行处理。";
        _log.Info(message);

        return true;
    }

    /// <summary>
    /// 删掉日志目录里的 <c>*.log</c>。
    ///
    /// <see cref="RollingFileLogger"/> 用 <c>File.AppendAllText</c> 写盘、不持有文件句柄，
    /// 所以当前正在写的那个也删得掉，下一条日志会自动把它重建出来。
    /// </summary>
    private static int DeleteLogFiles(out string? error)
    {
        error = null;
        int deleted = 0;

        try
        {
            if (!Directory.Exists(AppPaths.LogDirectory))
            {
                return 0;
            }

            foreach (string file in Directory.EnumerateFiles(AppPaths.LogDirectory, "*.log"))
            {
                try
                {
                    File.Delete(file);
                    deleted++;
                }
                catch (Exception ex)
                {
                    // 单个文件删不掉（被别的程序打开着）不该让整件事失败 ——
                    // 状态已经清了，这里再抛出去只会让用户以为清空没成功。
                    error ??= ex.Message;
                }
            }
        }
        catch (Exception ex)
        {
            error ??= ex.Message;
        }

        return deleted;
    }

    private static string NormalizeDirectory(string path)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch
        {
            return path;
        }
    }

    // ==================================================================
    //  辅助
    // ==================================================================

    /// <summary>7za.exe 是否真的在。缺了它整个程序做不了任何事，界面必须显眼地说出来。</summary>
    public static bool SevenZipAvailable
    {
        get
        {
            try
            {
                return File.Exists(AppPaths.SevenZipExe);
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    public async ValueTask DisposeAsync() => await _engine.DisposeAsync().ConfigureAwait(false);
}
