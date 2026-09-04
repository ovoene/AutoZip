using System.IO;
using NewAutoZip.Core.Configuration;
using NewAutoZip.Core.Diagnostics;
using NewAutoZip.Core.Notifications;
using NewAutoZip.Core.OneDrive;
using NewAutoZip.Core.Pipeline;

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

    public EngineHost(IAppLogger log)
    {
        _log = log;
        _store = new SettingsStore(AppPaths.SettingsFile);
        _engine = new BackupEngine(log);

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
