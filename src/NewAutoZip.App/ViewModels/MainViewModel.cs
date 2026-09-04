using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using NewAutoZip.App.Services;
using NewAutoZip.Core.Configuration;
using NewAutoZip.Core.Diagnostics;
using NewAutoZip.Core.Notifications;
using NewAutoZip.Core.OneDrive;
using NewAutoZip.Core.Pipeline;

namespace NewAutoZip.App.ViewModels;

/// <summary>
/// 关于窗口里的一行"标签：值"。
/// </summary>
/// <param name="Label">左侧标签，固定宽度对齐。</param>
/// <param name="Value">右侧内容，通常是一条真实路径。</param>
public sealed record AboutRow(string Label, string Value);

/// <summary>
/// 主界面的全部状态。
///
/// 刷新模型：<b>一个</b> 250 毫秒的 <see cref="DispatcherTimer"/> 拉取引擎快照，
/// 逐项与上次比较、只在真的变了时才触发绑定更新。
/// 引擎侧一个事件都不往界面推 —— 所以无论后台多忙，界面的负载都是恒定的 4 Hz。
/// 旧版是反过来的（每个文件每轮推一条日志 + 一次 <c>BeginInvoke</c>），20 个文件就冻死。
/// </summary>
public sealed class MainViewModel : ObservableObject, IDisposable
{
    /// <summary>界面上保留的日志行数。超出丢最旧的。</summary>
    private const int MaxLogLines = 2000;

    /// <summary>单轮最多取走多少条日志 —— 避免一次爆量把 UI 线程占满。</summary>
    private const int LogDrainPerTick = 200;

    /// <summary>
    /// 界面上所有"某件事发生在什么时候"的时间戳格式。<b>带年份</b>。
    ///
    /// 之前是 <c>MM-dd HH:mm:ss</c>，省掉年份在这个程序里是错的：它常驻后台跑几个月，
    /// "最近成功 08-13 18:09"到了跨年之后完全分不清是今年还是去年的 —— 而这恰好是
    /// 用户最需要判断的一件事（"上次成功备份到底是多久以前"）。
    ///
    /// 借用 <see cref="Views.StampConverter"/> 上的常量，不在这里再抄一份：
    /// XAML 里那几个 DataGrid 列走的是同一个转换器，格式全程只有一个定义。
    /// </summary>
    private const string StampFormat = Views.StampConverter.Format;

    /// <summary>同上，但不带秒 —— "下次几点开工"精确到秒没有意义。</summary>
    private const string DayStampFormat = Views.StampConverter.MinuteFormat;

    private readonly EngineHost _host;
    private readonly UiLogSink _sink;
    private readonly IAppLogger _log;
    private readonly ThemeService _theme;
    private readonly DispatcherTimer _timer;
    private readonly List<LogRecord> _drainBuffer = new(LogDrainPerTick);

    private EngineSnapshot _snapshot = EngineSnapshot.Stopped;

    /// <summary>
    /// 云盘模式的三个缓存值，全部只由 <see cref="SyncCloudTarget()"/> 维护。
    ///
    /// 属性一律读这里、不再各自去读快照和配置：那样等于同一件事有好几个来源，
    /// 迟早出现"按钮说 OneDrive、右边的步骤列表却只有 4 步"这种自相矛盾的界面。
    /// </summary>
    private CloudTarget _shownCloudTarget;

    private CloudTarget _savedCloudTarget;
    private bool _cloudTargetPending;

    private int _selectedPage;
    private bool _autoScrollLog = true;
    private bool _newestLogFirst = true;
    private bool _disposed;

    private string _infoBarTitle = string.Empty;
    private string _infoBarMessage = string.Empty;
    private bool _infoBarOpen;
    private string _infoBarSeverity = "Informational";

    private string _validationText = string.Empty;
    private string _notifyTestResult = string.Empty;

    public MainViewModel(EngineHost host, UiLogSink sink, IAppLogger log, ThemeService theme)
    {
        _host = host;
        _sink = sink;
        _log = log;
        _theme = theme;

        Settings = new SettingsViewModel();
        Settings.Load(_host.Settings);
        _sink.MinimumLevel = _host.Settings.LogLevel;

        foreach (OneDriveAccount account in SafeDiscover())
        {
            Settings.OneDriveAccounts.Add(new OneDriveChoice(
                account.Path,
                $"{account.DisplayName}（{account.Path}）"));
        }

        StartCommand = new RelayCommand(Start, () => !_snapshot.Running);
        StopCommand = new AsyncRelayCommand(StopAsync, () => _snapshot.Running);
        NudgeCommand = new RelayCommand(Nudge, () => _snapshot.Running);

        SaveSettingsCommand = new RelayCommand(SaveSettings);
        ReloadSettingsCommand = new RelayCommand(ReloadSettings);
        TestNotifyCommand = new AsyncRelayCommand(TestNotifyAsync);

        ToggleThemeCommand = new RelayCommand(ToggleTheme);

        OpenLogFileCommand = new RelayCommand(() => Reveal(App.LogFilePath));
        OpenLogFolderCommand = new RelayCommand(() => Reveal(AppPaths.LogDirectory));
        OpenZipTempCommand = new RelayCommand(() => Reveal(_host.ZipTempDirectory));
        OpenDataFolderCommand = new RelayCommand(() => Reveal(AppPaths.DataRoot));
        OpenMonitorFolderCommand = new RelayCommand(() => Reveal(Settings.MonitorPath));
        OpenCloudFolderCommand = new RelayCommand(() => Reveal(Settings.CloudPath));

        ClearLogCommand = new RelayCommand(() => LogLines.Clear());
        CopyLogCommand = new RelayCommand(CopyLog);

        RequeueQuarantineCommand = new RelayCommand<string>(RequeueQuarantine);
        DiscardQuarantineCommand = new RelayCommand<string>(DiscardQuarantine);

        DismissInfoBarCommand = new RelayCommand(() => InfoBarOpen = false);

        // 配置一改就重算校验清单，用户在填的过程中就能看到问题，而不是点了"开始"才被拦。
        Settings.Changed += (_, _) => RefreshValidation();

        // 主题与皮肤即时生效（不等"保存"）。挑颜色看不到效果的话，这个功能等于没有。
        Settings.ThemeChanged += (_, _) => ApplyTheme();

        // 系统主题变化（"跟随系统"时）也要让标题栏那个按钮的图标跟着换。
        _theme.Changed += OnThemeServiceChanged;

        RefreshValidation();

        // 步骤列表按配置里的云盘类型先搭起来（非 OneDrive 只有 4 步）。
        // 引擎此刻必然没在跑，所以直接按磁盘上那份来；下面那次 Tick 会再对齐一遍。
        _shownCloudTarget = _host.Settings.CloudTarget;
        _savedCloudTarget = _shownCloudTarget;
        RebuildPipelineSteps(_shownCloudTarget);

        if (_host.LoadWarning is { } warning)
        {
            ShowInfo("配置读取有问题", warning, "Warning");
        }
        else if (!EngineHost.SevenZipAvailable)
        {
            ShowInfo(
                "找不到压缩程序",
                $"{AppPaths.SevenZipExe} 不存在。没有它无法打包，请检查安装是否完整。",
                "Error");
        }

        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(250),
        };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();

        Tick();
    }

    // ==================================================================
    //  绑定集合
    // ==================================================================

    public SettingsViewModel Settings { get; }

    public ObservableCollection<TrackedFileView> TrackedFiles { get; } = [];

    public ObservableCollection<BatchFileView> BatchFiles { get; } = [];

    public ObservableCollection<PendingUploadView> PendingUploads { get; } = [];

    public ObservableCollection<RetryView> Retries { get; } = [];

    public ObservableCollection<QuarantineView> Quarantines { get; } = [];

    public ObservableCollection<LogRecord> LogLines { get; } = [];

    // ==================================================================
    //  命令
    // ==================================================================

    public RelayCommand StartCommand { get; }

    public AsyncRelayCommand StopCommand { get; }

    public RelayCommand NudgeCommand { get; }

    public RelayCommand SaveSettingsCommand { get; }

    public RelayCommand ReloadSettingsCommand { get; }

    public AsyncRelayCommand TestNotifyCommand { get; }

    public RelayCommand OpenLogFileCommand { get; }

    public RelayCommand OpenLogFolderCommand { get; }

    public RelayCommand OpenZipTempCommand { get; }

    public RelayCommand OpenDataFolderCommand { get; }

    public RelayCommand OpenMonitorFolderCommand { get; }

    public RelayCommand OpenCloudFolderCommand { get; }

    public RelayCommand ClearLogCommand { get; }

    public RelayCommand CopyLogCommand { get; }

    public RelayCommand<string> RequeueQuarantineCommand { get; }

    public RelayCommand<string> DiscardQuarantineCommand { get; }

    public RelayCommand DismissInfoBarCommand { get; }

    // ==================================================================
    //  关于
    // ==================================================================

    /// <summary>
    /// 标题栏最左边的程序名。
    ///
    /// 和版本号一样取自程序集元数据（<c>Directory.Build.props</c> 的 Product），
    /// 界面上不再抄一遍字符串 —— 改名时漏掉一处就会出现两个名字并存。
    /// </summary>
    public string AppName { get; } = App.AppName;

    /// <summary>
    /// 标题栏上那个版本号，紧跟在程序名右边。
    ///
    /// 版本号取自程序集（<c>Directory.Build.props</c> 里的 Version），不在界面上再抄一遍 ——
    /// 手抄的版本号迟早和真实版本对不上，而"用户报的问题到底出在哪个版本"这件事
    /// 全靠它，对不上就等于没有。
    /// </summary>
    public string VersionText { get; } = $"v{App.AppVersion()}";

    /// <summary>关于窗口的标题。</summary>
    public string AboutTitle { get; } = $"关于 {App.AppName}";

    /// <summary>关于窗口顶部那行大字：程序名 + 版本号。</summary>
    public string AboutHeadline { get; } = $"{App.AppName} {App.AppVersion()}";

    /// <summary>关于窗口里的一句话介绍。</summary>
    public string AboutSummary { get; } =
        "盯住一个目录，等文件确实写完，打成带密码的 7z 压缩包，移进指定目录。" +
        "目标是 OneDrive 时还会确认云端确实收到、再释放本地占用。全程发通知。";

    /// <summary>关于窗口里的托盘说明。这是最常被误解的一条行为，单独一行摆出来。</summary>
    public string AboutTrayNote { get; } =
        "关掉窗口只是收进托盘，备份继续跑；真正退出要在托盘菜单里选“退出”。";

    /// <summary>
    /// 关于窗口里那几行"标签：值"。
    ///
    /// 便携模式、装到没有写权限的目录、自带 7za 找不到时回退到系统安装版，
    /// 这些都会让真实路径和用户以为的不一样。出问题时第一件要问的就是
    /// "你的数据在哪、日志在哪、用的哪个 7za、什么系统"，摆在这里就不必再教用户去翻。
    /// </summary>
    public IReadOnlyList<AboutRow> AboutRows { get; } = BuildAboutRows();

    private static IReadOnlyList<AboutRow> BuildAboutRows()
    {
        List<AboutRow> rows =
        [
            new("数据目录", AppPaths.DataRoot),
            new("日志目录", AppPaths.LogDirectory),
            new("压缩程序", AppPaths.SevenZipExe),
            new("运行环境", $".NET {Environment.Version}"),
            new("当前系统", DescribeOperatingSystem()),
        ];

        if (AppPaths.IsPortable)
        {
            rows.Add(new("运行模式", "便携模式（程序目录下有 portable.txt），所有数据都留在程序目录里"));
        }

        return rows;
    }

    /// <summary>
    /// 托盘菜单"关于"的纯文本版。
    ///
    /// 关于窗口本身用的是上面那几个结构化属性；这一份是<b>兜底路径</b>——
    /// 独立窗口万一构造失败（样式资源缺失等），退回系统 MessageBox 时要有东西可显示。
    /// 自检也拿它核对内容齐不齐。
    /// </summary>
    public string AboutText { get; } = BuildAbout();

    private static string BuildAbout()
    {
        StringBuilder text = new();

        text.AppendLine($"{App.AppName} {App.AppVersion()}");
        text.AppendLine();
        text.AppendLine(
            "盯住一个目录，等文件确实写完，打成带密码的 7z 压缩包，移进指定目录。");
        text.AppendLine("目标是 OneDrive 时还会确认云端确实收到、再释放本地占用。全程发通知。");
        text.AppendLine();
        text.AppendLine("关掉窗口只是收进托盘，备份继续跑；真正退出要在托盘菜单里选“退出”。");
        text.AppendLine();

        foreach (AboutRow row in BuildAboutRows())
        {
            text.AppendLine($"{row.Label}：{row.Value}");
        }

        return text.ToString().TrimEnd();
    }

    /// <summary>
    /// 当前系统那一行。
    ///
    /// 不直接用 <c>Environment.OSVersion.VersionString</c>：那串东西长成
    /// "Microsoft Windows NT 10.0.19045.0"，既看不出是 Win10 还是 Win11
    /// （两者主版本号都是 10.0，只有 Build 号能区分：22000 起算 Win11），
    /// 也看不出是 64 位还是 32 位。用户报问题时这两件事恰好是最有用的。
    /// </summary>
    private static string DescribeOperatingSystem()
    {
        try
        {
            Version v = Environment.OSVersion.Version;

            string family = v.Major switch
            {
                >= 10 when v.Build >= 22000 => "Windows 11",
                >= 10 => "Windows 10",
                6 => v.Minor switch
                {
                    >= 3 => "Windows 8.1",
                    2 => "Windows 8",
                    1 => "Windows 7",
                    _ => "Windows Vista",
                },
                _ => "Windows",
            };

            string bitness = Environment.Is64BitOperatingSystem ? "64 位" : "32 位";
            string process = Environment.Is64BitProcess ? "64 位进程" : "32 位进程";

            return $"{family} {v.Major}.{v.Minor} 内部版本 {v.Build} · {bitness}系统 / {process}";
        }
        catch (Exception)
        {
            // 取不到就如实说取不到，绝不编一个像真的一样的字符串出来。
            return "未能识别";
        }
    }

    // ==================================================================
    //  外观（主题模式 / 强调色 / 背景图）
    // ==================================================================

    /// <summary>标题栏上的一键深浅切换。</summary>
    public RelayCommand ToggleThemeCommand { get; }

    /// <summary>切换按钮的图标：现在是深色就显示"太阳"（点了变亮），反之显示"月亮"。</summary>
    public string ThemeToggleIcon => _theme.IsDark ? "" : "";

    public string ThemeToggleTip => _theme.IsDark ? "切换到浅色主题" : "切换到深色主题";

    /// <summary>设置页上显示"跟随系统"当前解析成了哪个主题。</summary>
    public string EffectiveThemeText => _theme.Mode == ThemeMode.System
        ? $"当前跟随系统，正在使用{(_theme.IsDark ? "深色" : "浅色")}。"
        : $"已固定为{(_theme.IsDark ? "深色" : "浅色")}，不再跟随系统。";

    /// <summary>
    /// 背景图那一层的两个绑定源。
    ///
    /// 从 <see cref="ThemeService"/> 直接读，而不是在这里再存一份 ——
    /// 存两份必然出现"设置里改了、界面上没变"，而这类不一致是静默的。
    /// 两个属性都在 <c>OnThemeServiceChanged</c> 里一起 Raise。
    ///
    /// 这里<b>没有</b> BackgroundBlur：模糊已经烘进 <see cref="BackgroundImage"/> 那张位图里，
    /// 界面上不再挂 <c>BlurEffect</c>（原因见 MainWindow.xaml 那层的注释）。
    /// 留一个没人绑的属性，下次有人就会以为界面还在实时模糊，把效果加回去。
    /// </summary>
    public System.Windows.Media.ImageSource? BackgroundImage => _theme.Background;

    public double BackgroundOpacity => _theme.BackgroundOpacity;

    /// <summary>背景图没用上的原因（人话）。设置页显示它，不能静默失败。</summary>
    public string BackgroundProblem => _theme.BackgroundProblem ?? string.Empty;

    // ==================================================================
    //  导航
    // ==================================================================

    public int SelectedPage
    {
        get => _selectedPage;
        set => Set(ref _selectedPage, value);
    }

    /// <summary>
    /// 日志列表按「最新的在最上面」排。<b>默认就是它。</b>
    ///
    /// 出了事第一眼要看的是最后发生了什么，而不是三小时前程序刚起来时的那几行。
    /// 顺着排的话得先滚到底 —— 而日志一直在往里进，滚到底这个动作永远做不完。
    ///
    /// 改这个开关会把已经攒下来的行<b>原地翻一遍</b>，不是只影响后续新增的行，
    /// 否则切一下就得到一份「前半段顺着、后半段倒着」的列表，比两种排法都难读。
    /// </summary>
    public bool NewestLogFirst
    {
        get => _newestLogFirst;
        set
        {
            if (Set(ref _newestLogFirst, value))
            {
                ReverseLog();
            }
        }
    }

    public bool AutoScrollLog
    {
        get => _autoScrollLog;
        set => Set(ref _autoScrollLog, value);
    }

    // ==================================================================
    //  快照派生的显示属性
    // ==================================================================

    public string PhaseText => _snapshot.StatusText;

    public bool IsRunning => _snapshot.Running;

    /// <summary>状态圆点的颜色键，界面查资源。故意不在 ViewModel 里造 Brush。</summary>
    public string PhaseColorKey => _snapshot.Phase switch
    {
        EnginePhase.Stopped => "DotIdle",
        EnginePhase.OutsideSchedule => "DotIdle",
        EnginePhase.Retrying => "DotWarn",
        EnginePhase.Stopping => "DotWarn",
        _ => _snapshot.QuarantineCount > 0 ? "DotWarn" : "DotOk",
    };

    public string RunButtonText => _snapshot.Running ? "运行中" : "已停止";

    public string TrackedCountText => _snapshot.TrackedCount.ToString();

    public string BatchText => _snapshot.BatchFileCount == 0
        ? "空"
        : $"{_snapshot.BatchFileCount} 个 · {_snapshot.BatchBytesText}";

    public string WindowText => _snapshot.WindowClosesAtLocal is { } t
        ? $"{t:HH:mm:ss} 关闭"
        : "无进行中的窗口";

    public int PackPercent => _snapshot.PackPercent;

    public bool PackInProgress => _snapshot.Phase == EnginePhase.Packing;

    public string PackCurrentFile => _snapshot.PackCurrentFile ?? string.Empty;

    public string PendingUploadText => _snapshot.PendingUploadCount == 0
        ? "无"
        : $"{_snapshot.PendingUploadCount} 个归档等待确认";

    public string RetryText => _snapshot.RetryCount == 0
        ? "无"
        : _snapshot.NextRetryLocal is { } next
            ? $"{_snapshot.RetryCount} 个批次待重试，最近一次 {next:HH:mm:ss}"
            : $"{_snapshot.RetryCount} 个批次待重试";

    public int QuarantineCount => _snapshot.QuarantineCount;

    /// <summary>隔离区有东西必须显眼 —— 那意味着有文件<b>没被备份</b>且程序已停止重试。</summary>
    public bool HasQuarantine => _snapshot.QuarantineCount > 0;

    public string QuarantineBadge => _snapshot.QuarantineCount > 0
        ? _snapshot.QuarantineCount.ToString()
        : string.Empty;

    public string ArchivesCreatedText => _snapshot.ArchivesCreated.ToString();

    public string FilesArchivedText => _snapshot.FilesArchived.ToString();

    public string BytesArchivedText => _snapshot.BytesArchivedText;

    public string ZipTempText => $"{_snapshot.ZipTempArchiveCount} 个 · {_snapshot.ZipTempBytesText}";

    public string FreeDiskText => _snapshot.FreeDiskText;

    /// <summary>剩余空间已经低于设定阈值。界面用它把数字变红。</summary>
    public bool DiskLow =>
        _snapshot.FreeDiskBytes > 0 && _snapshot.FreeDiskBytes <= _host.Settings.MinFreeDiskBytes;

    // ==================================================================
    //  最近结果：打包与送达分开报
    //
    //  "包打好了但没送上去"和"压根没打出包"要采取的行动完全不同，
    //  只报一个笼统的"最近成功 / 最近失败"等于让用户自己去翻日志才知道该做什么。
    //  送达那一行的名字随云盘类型变：OneDrive 说"上传"，其他云盘说"移入"——
    //  非 OneDrive 模式下程序确实只做到移入，写"上传"就是在替云盘客户端吹牛。
    // ==================================================================

    /// <summary>送达动作的称呼：OneDrive 是"上传"，其他云盘是"移入"。</summary>
    public string DeliverNoun => CloudTargetText.DeliverNoun(_shownCloudTarget);

    /// <summary>当前总览页按哪种云盘模式在显示。见 <see cref="SyncCloudTarget()"/>。</summary>
    public bool IsOneDriveMode => _shownCloudTarget == CloudTarget.OneDrive;

    /// <summary>
    /// 「打开 OneDrive 目录」/「打开指定目录」。
    ///
    /// OneDrive 那一边多一个空格：中文动词后面紧跟拉丁词会挤成「打开OneDrive 目录」，
    /// 而标签本身「OneDrive 目录」里就带着空格，不补反而前后不一致。
    /// </summary>
    public string OpenCloudFolderText => IsOneDriveMode
        ? $"打开 {CloudTargetText.PathLabel(_shownCloudTarget)}"
        : $"打开{CloudTargetText.PathLabel(_shownCloudTarget)}";

    /// <summary>
    /// 总览页<b>应该</b>按哪种模式显示。
    ///
    /// 引擎在跑 → 跟着引擎快照，因为总览报的是"程序此刻在做什么"，
    /// 而正在跑的这一轮用的是启动时那份配置，换成新值等于报了个还没生效的未来。
    ///
    /// 引擎停着 → 跟着<b>磁盘上已保存</b>的配置。这时没有"正在做的事"可报，
    /// 该报的是"下次跑会按哪种模式"。不这么做的话，保存完得点一次"开始"
    /// 才能看到界面跟上，而引擎快照在停止后还留着上一次启动的值
    /// （<see cref="EngineSnapshot.Stopped"/> 里写死的是 OneDrive），
    /// 光等快照变化是等不到的。
    ///
    /// 写成静态纯函数是为了自检能直接喂输入。走界面上的"保存"会真的落盘，
    /// 而自检是诊断命令，不该把用户的配置改掉。
    /// </summary>
    internal static CloudTarget ShownTargetFor(EngineSnapshot snapshot, CloudTarget saved) =>
        snapshot.Running ? snapshot.CloudTarget : saved;

    /// <summary>
    /// 引擎正按旧的云盘类型跑，而磁盘上已经是新的了。
    ///
    /// 这时候界面故意<b>不</b>切 —— 但必须说出来，否则用户改完保存、
    /// 看总览一点没变，只会以为保存没生效。
    /// </summary>
    internal static bool SwitchPendingFor(EngineSnapshot snapshot, CloudTarget saved) =>
        snapshot.Running && saved != snapshot.CloudTarget;

    /// <summary>见 <see cref="SwitchPendingFor"/>。由 <see cref="SyncCloudTarget()"/> 独家维护。</summary>
    public bool CloudTargetPending => _cloudTargetPending;

    /// <summary>
    /// 待切换时的说明文字。
    ///
    /// "引擎那边"取 <c>_shownCloudTarget</c> 而不是快照：待切换成立的前提就是引擎在跑，
    /// 而引擎在跑时显示的正是快照里那种模式，两者必然相同 —— 少读一处状态就少一处能对不上的地方。
    /// </summary>
    public string CloudTargetPendingText => _cloudTargetPending
        ? $"云盘类型已保存为「{CloudTargetText.ShortName(_savedCloudTarget)}」，"
            + $"但引擎正在按「{CloudTargetText.ShortName(_shownCloudTarget)}」跑完这一轮。"
            + "停止引擎后这里会自动切换。"
        : string.Empty;

    /// <summary>
    /// 把总览页切到该显示的那种云盘模式。
    ///
    /// 触发点有四个：保存配置、重新读取配置、引擎启停、引擎快照里的类型变了。
    /// 前两个是关键 —— 「保存后要立刻切换」就靠它们，不再等"点开始"。
    /// </summary>
    internal void SyncCloudTarget() => SyncCloudTarget(_snapshot, _host.Settings.CloudTarget);

    /// <summary>
    /// 同上，但两个输入由调用方给出 —— 自检用这个重演"引擎在跑 / 已停止"两种情形，
    /// 不必真的启动引擎、也不必往用户的 settings.json 里写东西。
    /// 它<b>不</b>改 <c>_snapshot</c>：自检跑完调一次无参版就能回到真实状态。
    /// </summary>
    internal void SyncCloudTarget(EngineSnapshot snapshot, CloudTarget saved)
    {
        CloudTarget wanted = ShownTargetFor(snapshot, saved);
        bool pending = SwitchPendingFor(snapshot, saved);

        _savedCloudTarget = saved;

        if (_shownCloudTarget != wanted)
        {
            _shownCloudTarget = wanted;

            Raise(nameof(DeliverNoun));
            Raise(nameof(DeliverSuccessLabel));
            Raise(nameof(DeliverFailureLabel));
            Raise(nameof(IsOneDriveMode));
            Raise(nameof(OpenCloudFolderText));

            // 步数本身变了（6 步 ⇄ 4 步），只 Raise 不重建的话列表还是旧的。
            RebuildPipelineSteps(wanted);
        }

        // 提示要单独通知：类型没切但"待切换"的状态可能刚出现或刚消失，
        // 而"保存成了另一种类型"也会让同一句提示的内容变。
        _cloudTargetPending = pending;
        Raise(nameof(CloudTargetPending));
        Raise(nameof(CloudTargetPendingText));
    }

    public string DeliverSuccessLabel => $"最近{DeliverNoun}成功";

    public string DeliverFailureLabel => $"最近{DeliverNoun}失败";

    public string LastPackSuccessText => _snapshot.LastPackSuccessLocal is { } t
        ? t.ToString(StampFormat)
        : "还没有";

    public string LastDeliverSuccessText => _snapshot.LastDeliverSuccessLocal is { } t
        ? t.ToString(StampFormat)
        : "还没有";

    public string LastPackFailureText => _snapshot.LastPackFailureLocal is { } t
        ? $"{t.ToString(StampFormat)} · {_snapshot.LastPackFailureReason}"
        : "还没有";

    public string LastDeliverFailureText => _snapshot.LastDeliverFailureLocal is { } t
        ? $"{t.ToString(StampFormat)} · {_snapshot.LastDeliverFailureReason}"
        : "还没有";

    public bool HasPackFailure => _snapshot.LastPackFailureLocal is not null;

    public bool HasDeliverFailure => _snapshot.LastDeliverFailureLocal is not null;

    // ==================================================================
    //  执行到哪一步了
    //
    //  状态条只报"此刻在干什么"，看不出整条流水线有几步、还剩什么。
    //  这个列表把全程摊开，正在跑的那一步挂「正在进行」徽章。
    //  步数随云盘类型变（见 PipelineStepMap），所以不能写死在 XAML 里。
    // ==================================================================

    public ObservableCollection<PipelineStepView> PipelineSteps { get; } = [];

    /// <summary>没有任何一步在跑时的解释（已停止 / 等待重试 / 不在时段）。</summary>
    public string StepNote => PipelineStepMap.Note(_snapshot.Phase);

    public bool HasStepNote => StepNote.Length > 0;

    public string ScheduleText
    {
        get
        {
            if (!_snapshot.Running)
            {
                return "未启动";
            }

            return _snapshot.ScheduleActive
                ? "在工作时段内"
                : _snapshot.NextActivationLocal is { } next
                    ? $"不在工作时段，下次 {next.ToString(DayStampFormat)}"
                    : "不在工作时段";
        }
    }

    public string DroppedLogText => _sink.Dropped > 0
        ? $"（界面来不及显示，已丢弃 {_sink.Dropped} 条，完整内容在日志文件里）"
        : string.Empty;

    // ==================================================================
    //  信息条
    // ==================================================================

    public string InfoBarTitle
    {
        get => _infoBarTitle;
        private set => Set(ref _infoBarTitle, value);
    }

    public string InfoBarMessage
    {
        get => _infoBarMessage;
        private set => Set(ref _infoBarMessage, value);
    }

    public bool InfoBarOpen
    {
        get => _infoBarOpen;
        set => Set(ref _infoBarOpen, value);
    }

    /// <summary>字符串而非枚举，好让 XAML 直接写 <c>Severity="{Binding InfoBarSeverity}"</c>。</summary>
    public string InfoBarSeverity
    {
        get => _infoBarSeverity;
        private set => Set(ref _infoBarSeverity, value);
    }

    /// <summary>设置页底部的校验结果。空串表示一切正常。</summary>
    public string ValidationText
    {
        get => _validationText;
        private set => Set(ref _validationText, value);
    }

    public string NotifyTestResult
    {
        get => _notifyTestResult;
        private set => Set(ref _notifyTestResult, value);
    }

    // ==================================================================
    //  刷新
    // ==================================================================

    private void Tick()
    {
        DrainLog();

        EngineSnapshot next = _host.Snapshot;
        EngineSnapshot previous = _snapshot;
        _snapshot = next;

        // record 的相等比较是逐成员的，而集合成员比的是引用 —— 引擎每轮都造新列表，
        // 所以这里必然不等；真正省事的是下面 SyncList 里的逐元素比较。
        if (!ReferenceEquals(previous, next))
        {
            RaiseSnapshotProperties(previous, next);
        }

        SyncList(TrackedFiles, next.TrackedFiles);
        SyncList(BatchFiles, next.BatchFiles);
        SyncList(PendingUploads, next.PendingUploads);
        SyncList(Retries, next.Retries);
        SyncList(Quarantines, next.Quarantines);

        if (previous.Running != next.Running)
        {
            StartCommand.RaiseCanExecuteChanged();
            StopCommand.RaiseCanExecuteChanged();
            NudgeCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>
    /// 只在对应的快照字段真的变了时才通知。
    /// 一轮 4 次、每次几十个属性，无谓的通知会让 WPF 白跑一遍布局与绘制。
    /// </summary>
    private void RaiseSnapshotProperties(EngineSnapshot a, EngineSnapshot b)
    {
        if (a.Phase != b.Phase || a.StatusText != b.StatusText)
        {
            Raise(nameof(PhaseText));
            Raise(nameof(PhaseColorKey));
            Raise(nameof(PackInProgress));
            ApplyStepState(b.Phase);
        }

        if (a.Running != b.Running)
        {
            Raise(nameof(IsRunning));
            Raise(nameof(RunButtonText));
            Raise(nameof(ScheduleText));

            // 引擎停下来了：总览该从"引擎在用的"换成"下次会用的"。
            // 这正是"引擎停止后自动切换"那句承诺的落点。
            SyncCloudTarget();
        }

        if (a.ScheduleActive != b.ScheduleActive || a.NextActivationLocal != b.NextActivationLocal)
        {
            Raise(nameof(ScheduleText));
        }

        if (a.TrackedCount != b.TrackedCount)
        {
            Raise(nameof(TrackedCountText));
        }

        if (a.BatchFileCount != b.BatchFileCount || a.BatchBytes != b.BatchBytes)
        {
            Raise(nameof(BatchText));
        }

        if (a.WindowClosesAtLocal != b.WindowClosesAtLocal)
        {
            Raise(nameof(WindowText));
        }

        if (a.PackPercent != b.PackPercent)
        {
            Raise(nameof(PackPercent));
        }

        if (a.PackCurrentFile != b.PackCurrentFile)
        {
            Raise(nameof(PackCurrentFile));
        }

        if (a.PendingUploadCount != b.PendingUploadCount)
        {
            Raise(nameof(PendingUploadText));
        }

        if (a.RetryCount != b.RetryCount || a.NextRetryLocal != b.NextRetryLocal)
        {
            Raise(nameof(RetryText));
        }

        if (a.QuarantineCount != b.QuarantineCount)
        {
            Raise(nameof(QuarantineCount));
            Raise(nameof(HasQuarantine));
            Raise(nameof(QuarantineBadge));
            Raise(nameof(PhaseColorKey));
        }

        if (a.ArchivesCreated != b.ArchivesCreated)
        {
            Raise(nameof(ArchivesCreatedText));
        }

        if (a.FilesArchived != b.FilesArchived)
        {
            Raise(nameof(FilesArchivedText));
        }

        if (a.BytesArchived != b.BytesArchived)
        {
            Raise(nameof(BytesArchivedText));
        }

        if (a.ZipTempArchiveCount != b.ZipTempArchiveCount || a.ZipTempBytes != b.ZipTempBytes)
        {
            Raise(nameof(ZipTempText));
        }

        if (a.FreeDiskBytes != b.FreeDiskBytes)
        {
            Raise(nameof(FreeDiskText));
            Raise(nameof(DiskLow));
        }

        if (a.CloudTarget != b.CloudTarget)
        {
            // 引擎自己换了类型（重新启动过），走同一条切换路径。
            SyncCloudTarget();
        }

        if (a.LastPackSuccessLocal != b.LastPackSuccessLocal)
        {
            Raise(nameof(LastPackSuccessText));
        }

        if (a.LastDeliverSuccessLocal != b.LastDeliverSuccessLocal)
        {
            Raise(nameof(LastDeliverSuccessText));
        }

        if (a.LastPackFailureLocal != b.LastPackFailureLocal || a.LastPackFailureReason != b.LastPackFailureReason)
        {
            Raise(nameof(LastPackFailureText));
            Raise(nameof(HasPackFailure));
        }

        if (a.LastDeliverFailureLocal != b.LastDeliverFailureLocal
            || a.LastDeliverFailureReason != b.LastDeliverFailureReason)
        {
            Raise(nameof(LastDeliverFailureText));
            Raise(nameof(HasDeliverFailure));
        }
    }

    /// <summary>
    /// 重建步骤列表。只在云盘类型变了时调用 —— 那时候步数本身变了（6 步 ⇄ 4 步）。
    /// 重建后要立刻把当前阶段的状态套上去，否则新列表里一个高亮都没有。
    /// </summary>
    private void RebuildPipelineSteps(CloudTarget target)
    {
        PipelineSteps.Clear();

        foreach (PipelineStepView step in PipelineStepMap.Build(target))
        {
            PipelineSteps.Add(step);
        }

        ApplyStepState(_snapshot.Phase);
    }

    /// <summary>
    /// 谁在跑、谁已经走过了。
    ///
    /// 只改两个 bool，不动集合 —— 每次阶段变化都重建列表的话，
    /// 正在跑的那一行会连着徽章一起闪一下，而阶段变化是很频繁的事。
    /// </summary>
    private void ApplyStepState(EnginePhase phase)
    {
        int active = PipelineStepMap.ActiveIndex(phase);

        for (int i = 0; i < PipelineSteps.Count; i++)
        {
            PipelineSteps[i].IsActive = i == active;
            PipelineSteps[i].IsDone = active >= 0 && i < active;
        }

        Raise(nameof(StepNote));
        Raise(nameof(HasStepNote));
    }

    /// <summary>
    /// 把 <paramref name="source"/> 同步到 <paramref name="target"/>，逐元素比较。
    ///
    /// 不能直接 Clear + AddRange：那样每轮都会重建全部行，正在滚动或选中的行会跳回去，
    /// 大列表还会明显闪烁。record 的值相等让"没变的行"能被廉价地识别出来。
    /// </summary>
    private static void SyncList<T>(ObservableCollection<T> target, IReadOnlyList<T> source)
        where T : class
    {
        int count = source.Count;

        for (int i = 0; i < count; i++)
        {
            if (i < target.Count)
            {
                if (!Equals(target[i], source[i]))
                {
                    target[i] = source[i];
                }
            }
            else
            {
                target.Add(source[i]);
            }
        }

        while (target.Count > count)
        {
            target.RemoveAt(target.Count - 1);
        }
    }

    /// <summary>
    /// 把这一轮攒下的日志搬进列表。
    /// </summary>
    private void DrainLog()
    {
        _drainBuffer.Clear();

        if (_sink.Drain(_drainBuffer, LogDrainPerTick) == 0)
        {
            return;
        }

        // 缓冲区里是「旧 → 新」，按这个顺序逐条交给 AppendLog，方向由它负责。
        foreach (LogRecord record in _drainBuffer)
        {
            AppendLog(record);
        }

        if (_sink.Dropped > 0)
        {
            Raise(nameof(DroppedLogText));
        }
    }

    /// <summary>
    /// 往列表里放<b>一行</b>日志：按当前排序方向落到正确的那一头，顺手把超出上限的最旧一行丢掉。
    ///
    /// 插入方向只留这一份实现。<c>DrainLog</c> 和 <c>--selftest</c> 都走它 ——
    /// 自检要是自己另写一遍插入逻辑，那验的就是自检抄的那份，生产代码插错了它也看不见。
    ///
    /// 「插哪头」和「丢哪头」必须一起翻：倒序时最旧的那些在<b>尾巴上</b>，
    /// 还去 <c>RemoveAt(0)</c> 就成了「一边往上插新的、一边把刚插进去的删掉」，
    /// 列表看着永远只有那么几行，而且不报任何错。
    /// </summary>
    internal void AppendLog(LogRecord record)
    {
        if (_newestLogFirst)
        {
            LogLines.Insert(0, record);
        }
        else
        {
            LogLines.Add(record);
        }

        if (LogLines.Count > MaxLogLines)
        {
            LogLines.RemoveAt(_newestLogFirst ? LogLines.Count - 1 : 0);
        }
    }

    /// <summary>
    /// 把已有的日志行整体翻个方向。
    ///
    /// 用 <c>Clear</c> + 重新 <c>Add</c>，不用逐条 <c>Move</c>：<c>Move</c> 会为两千行
    /// 各发一条集合变更通知，虚拟化面板一条条跟着重排，界面会明显卡一下；
    /// <c>Clear</c> 只发一条 Reset，列表整块重建，反而快得多。
    /// </summary>
    private void ReverseLog()
    {
        if (LogLines.Count < 2)
        {
            return;
        }

        List<LogRecord> flipped = [.. LogLines];
        flipped.Reverse();

        LogLines.Clear();

        foreach (LogRecord record in flipped)
        {
            LogLines.Add(record);
        }
    }

    // ==================================================================
    //  动作
    // ==================================================================

    private void Start()
    {
        // 未保存的改动会让"界面上写的"和"引擎实际用的"不一致 —— 这种撒谎必须先消除。
        if (Settings.IsDirty)
        {
            SaveSettings();
        }

        if (_host.TryStart(out ValidationReport report))
        {
            InfoBarOpen = false;
            Tick();
            return;
        }

        SelectedPage = 4;
        ShowInfo("配置还不能启动", report.ToText(), "Error");
        RefreshValidation();
    }

    private async Task StopAsync()
    {
        try
        {
            await _host.StopAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // 停止本身失败是个缺陷，但绝不能让它冒到 Dispatcher 去弹崩溃框。
            _log.Error("停止引擎时出错。", ex);
        }
        finally
        {
            Tick();
        }
    }

    private void Nudge()
    {
        _host.Nudge();
        _log.Info("已手动触发一次检查。");
    }

    private void SaveSettings()
    {
        try
        {
            CloudTarget before = _shownCloudTarget;

            AppSettings settings = Settings.ToSettings();
            _host.SaveSettings(settings);

            // 「启动时最小化到托盘」同时管开机自启动，所以保存配置时要把注册表
            // 那条记录跟上。登记失败不算保存失败 —— 配置已经写进磁盘了，
            // 只是这一项没生效，如实附在提示后面告诉用户。
            string? startupProblem = StartupRegistration.Sync(_host.Settings.StartMinimized, _log);

            // 保存后回读一遍：数值被 Clamp 修正过的话，界面必须显示修正后的真实值，
            // 不能让用户以为自己填的 0 生效了。
            Settings.Load(_host.Settings);
            _sink.MinimumLevel = _host.Settings.LogLevel;

            // 云盘类型不用等"点开始"了 —— 引擎停着就当场切，在跑就先记下、停了再切。
            SyncCloudTarget();

            RefreshValidation();

            if (startupProblem is null)
            {
                ShowInfo("已保存", SavedMessage(before), SavedSeverity());
            }
            else
            {
                ShowInfo("已保存", $"{SavedMessage(before)}{Environment.NewLine}{startupProblem}", "Warning");
            }
        }
        catch (Exception ex)
        {
            _log.Error("保存配置失败。", ex);
            ShowInfo("保存失败", ex.Message, "Error");
        }
    }

    /// <summary>
    /// 保存后那条提示。
    ///
    /// 三种情况说三种话，因为用户接下来该做的事不一样：
    /// <list type="bullet">
    ///   <item>引擎停着 —— 全部生效，没有后续动作；顺带报一句总览已经切过去了，
    ///         不然用户不知道该去看右边；</item>
    ///   <item>引擎在跑、且改了云盘类型 —— 这一轮还是旧模式，总览也故意没切，
    ///         必须点明"停止后自动切换"，否则看起来像保存没生效；</item>
    ///   <item>引擎在跑、云盘类型没改 —— 老话照说：下次启动生效。</item>
    /// </list>
    /// </summary>
    private string SavedMessage(CloudTarget before)
    {
        if (!_snapshot.Running)
        {
            return before == _shownCloudTarget
                ? "配置已写入。"
                : $"配置已写入。总览已切换为「{CloudTargetText.ShortName(_shownCloudTarget)}」模式。";
        }

        return CloudTargetPending
            ? "配置已写入。" + CloudTargetPendingText
            : "配置已写入。引擎正在运行，新配置会在下次启动时生效 —— 现在就想生效请先停止再开始。";
    }

    private string SavedSeverity() => _snapshot.Running ? "Warning" : "Success";

    private void ReloadSettings()
    {
        Settings.Load(_host.ReloadSettings());
        _sink.MinimumLevel = _host.Settings.LogLevel;
        RefreshValidation();

        // 丢弃界面改动同样可能改变云盘类型（界面上改过但没保存），
        // 所以这里也要把总览拉回磁盘上那份。
        SyncCloudTarget();

        // 主题也要跟着退回磁盘上的值 —— 设置页上的主题是即时预览的，
        // "重新读取"说的是"丢弃界面改动"，那就得把预览一起丢掉，否则界面
        // 显示"深色"而实际还亮着浅色，两边不一致。
        ApplyTheme();

        ShowInfo("已重新读取", "界面上的改动已丢弃，现在显示的是磁盘上的配置。", "Informational");
    }

    // ==================================================================
    //  外观（主题模式 / 强调色 / 背景图）
    // ==================================================================

    /// <summary>
    /// 把设置页当前的外观推到界面上，并<b>立刻落盘</b>。
    ///
    /// 外观是唯一不走"保存"按钮的一类配置：挑颜色、挑背景图必须马上看到效果，
    /// 而看到效果之后又不该因为忘了点保存就白挑一次。落盘只写外观那几个字段，
    /// 不碰设置页上其他未提交的编辑（见 <see cref="EngineHost.SaveAppearance"/>）。
    /// </summary>
    private void ApplyTheme()
    {
        AppearanceSpec wanted = Settings.ToAppearance();

        _theme.Apply(wanted);

        // 已经是磁盘上的值就不用再写一遍：拖动滑块、连点色块时这个方法会被调很多次，
        // 每次都写一遍文件纯属浪费，还多了一堆无谓的失败机会。
        if (wanted.Matches(_host.Settings))
        {
            return;
        }

        try
        {
            _host.SaveAppearance(wanted);
        }
        catch (Exception ex)
        {
            // 存不下来不影响本次已经生效的观感，只是下次启动会回到旧主题。
            // 这种事值得说一声，但不该打断用户正在做的事。
            _log.Warn($"外观设置保存失败，本次有效但重启后会还原。{ex.Message}");
        }
    }

    /// <summary>
    /// 外观真的变了（可能来自设置页，也可能来自系统的深浅切换）。
    ///
    /// 只有那几个"读 <see cref="ThemeService"/> 算出来"的显示属性需要通知；
    /// 语义配色不在这里管 —— 那些画刷是被原地改色的，界面会自己跟着变。
    /// </summary>
    private void OnThemeServiceChanged(object? sender, EventArgs e)
    {
        Raise(nameof(ThemeToggleIcon));
        Raise(nameof(ThemeToggleTip));
        Raise(nameof(EffectiveThemeText));

        Raise(nameof(BackgroundImage));
        Raise(nameof(BackgroundOpacity));
        Raise(nameof(BackgroundProblem));

        // 载入失败的原因显示在设置页上，而那段文字是 SettingsViewModel 拼的 ——
        // 它自己不知道图片解没解开，只有 ThemeService 知道，所以从这里捅一下。
        Settings.NotifyBackgroundStatus(_theme.BackgroundProblem);
    }

    /// <summary>
    /// 标题栏上的一键深浅切换。
    ///
    /// 切换的结果要<b>写回设置页</b>，不然设置页还显示"跟随系统"而界面已经固定住了，
    /// 而且下一次点"保存"会把标题栏这次切换悄悄覆盖掉。
    /// 写回时走 <see cref="SettingsViewModel.ThemeChoice"/> 的 setter，
    /// 它会触发 <c>ThemeChanged</c> → <see cref="ApplyTheme"/> 完成落盘。
    /// </summary>
    private void ToggleTheme()
    {
        ThemeMode next = _theme.IsDark ? ThemeMode.Light : ThemeMode.Dark;

        ChoiceItem<ThemeMode>? choice = Settings.Themes.FirstOrDefault(c => c.Value == next);

        if (choice is null)
        {
            // Themes 是写死的三项，正常不可能走到这里；真到了也别静默失败。
            _theme.Apply(next, Settings.AccentColor);
            return;
        }

        Settings.ThemeChoice = choice;
    }


    private async Task TestNotifyAsync()
    {
        NotifyTestResult = "正在发送…";

        try
        {
            using CancellationTokenSource cts = new(TimeSpan.FromSeconds(60));
            NotifyResult result = await _host.TestNotifyAsync(Settings.ToSettings(), cts.Token)
                .ConfigureAwait(true);

            NotifyTestResult = result.Ok
                ? $"✔ 发送成功（尝试 {result.Attempts} 次）。"
                : $"✘ 发送失败：{result.Error}";
        }
        catch (Exception ex)
        {
            NotifyTestResult = $"✘ 发送失败：{ex.Message}";
        }
    }

    private void RequeueQuarantine(string id)
    {
        if (_host.RequeueQuarantined(id))
        {
            _host.Nudge();
            Tick();
            return;
        }

        ShowInfo("重试失败", "这个批次已经不在隔离区里了。", "Warning");
    }

    private void DiscardQuarantine(string id)
    {
        if (_host.DiscardQuarantined(id))
        {
            Tick();
            return;
        }

        ShowInfo("忽略失败", "这个批次已经不在隔离区里了。", "Warning");
    }

    /// <summary>
    /// 复制日志。<b>始终按时间顺序（旧 → 新）导出</b>，不跟着界面上的排法走。
    ///
    /// 界面倒序是为了「一眼看到最后发生了什么」，而复制出去的文本是要贴进反馈、
    /// 工单或者聊天窗口给人读的 —— 那种场合读的是「先后」，倒着贴过去等于让对方
    /// 从结论倒推过程。两个场景要的东西不一样，就别硬凑成一个。
    ///
    /// 时间戳<b>必须带年月日</b>，而且这里比界面上更需要：文本一旦离开这个程序，
    /// 就再也没有「屏幕上那些行都是刚才的事」这个上下文了。工单隔周才被打开、
    /// 聊天记录翻到上个月，光看 "18:09:31" 连是哪天发生的都无从判断。
    /// 格式统一走 <see cref="StampFormat"/>，和界面上那个时间戳列是同一份定义。
    /// </summary>
    private void CopyLog()
    {
        StringBuilder builder = new();

        // 倒序显示时列表本身是「新 → 旧」，反过来遍历才是时间顺序。
        IEnumerable<LogRecord> ordered = _newestLogFirst ? LogLines.Reverse() : LogLines;

        foreach (LogRecord record in ordered)
        {
            builder.Append(record.TimestampLocal.ToString(StampFormat))
                .Append("  ")
                .Append(record.Level)
                .Append("  ")
                .AppendLine(record.Message);
        }

        try
        {
            Clipboard.SetText(builder.ToString());
            ShowInfo("已复制", $"{LogLines.Count} 行日志已放进剪贴板。", "Success");
        }
        catch (Exception ex)
        {
            // 剪贴板被别的进程占用时 SetText 会抛 COMException，这不该让程序崩。
            _log.Warn($"复制到剪贴板失败：{ex.Message}");
            ShowInfo("复制失败", "剪贴板被其他程序占用，请稍后再试。", "Warning");
        }
    }

    private void RefreshValidation()
    {
        try
        {
            ValidationReport report = _host.Validate(Settings.ToSettings());
            ValidationText = report.Issues.Count == 0 ? string.Empty : report.ToText();
        }
        catch (Exception ex)
        {
            // 校验本身出错（比如路径含非法字符导致 Path API 抛异常）也要如实显示，不能静默。
            ValidationText = $"【错误】校验时出错：{ex.Message}";
        }
    }

    private void ShowInfo(string title, string message, string severity)
    {
        InfoBarTitle = title;
        InfoBarMessage = message;
        InfoBarSeverity = severity;
        InfoBarOpen = true;
    }

    /// <summary>用资源管理器打开文件或目录。文件则选中它。</summary>
    private void Reveal(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            ShowInfo("打不开", "路径是空的。", "Warning");
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
                {
                    UseShellExecute = true,
                })?.Dispose();
                return;
            }

            if (!Directory.Exists(path))
            {
                ShowInfo("打不开", $"路径不存在：{path}", "Warning");
                return;
            }

            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            _log.Warn($"打开「{path}」失败：{ex.Message}");
            ShowInfo("打不开", ex.Message, "Warning");
        }
    }

    private IReadOnlyList<OneDriveAccount> SafeDiscover()
    {
        try
        {
            return OneDriveLocator.Discover();
        }
        catch (Exception ex)
        {
            // 注册表读不到不是致命问题，用户可以手填路径。
            _log.Warn($"枚举 OneDrive 账号失败：{ex.Message}");
            return [];
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Stop();

        // ThemeService 活得比 ViewModel 长（它挂在 App 上），不退订就把已经废弃的
        // ViewModel 一直留在它的事件链上。
        _theme.Changed -= OnThemeServiceChanged;
    }
}
