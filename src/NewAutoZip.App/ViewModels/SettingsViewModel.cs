using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Media;
using NewAutoZip.Core.Configuration;
using NewAutoZip.Core.Diagnostics;
using NewAutoZip.Core.Notifications;
using NewAutoZip.Core.Storage;

namespace NewAutoZip.App.ViewModels;

/// <summary>下拉框用的键值对。</summary>
public sealed record ChoiceItem<T>(T Value, string Text)
{
    public override string ToString() => Text;
}

/// <summary>
/// 设置页里的一个预置皮肤色块。
///
/// <see cref="Fill"/> 是 Freeze 过的：它就是这个色块自己的颜色，
/// 不跟随主题变化（不然"选颜色"这件事就没意义了），Freeze 掉还省一份渲染开销。
/// </summary>
public sealed class AccentSwatch : ObservableObject
{
    private bool _isSelected;

    public AccentSwatch(string name, string hex)
    {
        Name = name;
        Hex = hex;

        SolidColorBrush brush = AccentColorSpec.TryParseRgb(hex, out byte r, out byte g, out byte b)
            ? new SolidColorBrush(Color.FromRgb(r, g, b))
            : new SolidColorBrush(Colors.Gray);

        brush.Freeze();
        Fill = brush;
    }

    public string Name { get; }

    /// <summary>规范化后的 <c>#RRGGBB</c>，直接写进配置。</summary>
    public string Hex { get; }

    public Brush Fill { get; }

    /// <summary>当前皮肤色正是这一个。界面据此画选中框。</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }
}

/// <summary>
/// 设置页的可绑定副本。
///
/// 界面改的是<b>副本</b>，只有点"保存"才写回 <see cref="AppSettings"/> ——
/// 所以取消改动是免费的，也不会出现"改了一半的配置被引擎读走"。
///
/// 数值项统一用 <c>double</c> 暴露，好让 <c>ui:NumberBox</c> 直接绑定，
/// 且每个都有真实的 Min/Max（见 <see cref="SettingsLimits"/>）——
/// 旧版四个 NumericUpDown 全是设计器默认的 0–100，扫描间隔填 0 会让线程 100% 忙等。
/// </summary>
public sealed class SettingsViewModel : ObservableObject
{
    private string _monitorPath = string.Empty;
    private bool _includeSubdirectories;
    private string _excludePatterns = string.Empty;
    private string _cloudPath = string.Empty;
    private string _zipTempPath = string.Empty;

    private bool _limitDates;
    private DateTime? _effectiveFrom;
    private DateTime? _effectiveTo;
    private bool _allDay = true;
    private string _dailyStartText = "00:00";
    private string _dailyEndText = "23:59";
    private double _batchWindowMinutes = 5;

    private double _quietSeconds = 60;
    private double _stableConfirmRounds = 2;
    private double _refreshIntervalSeconds = 5;
    private double _unreadableGiveUpMinutes = 20;
    private double _reconcileIntervalSeconds = 300;
    private bool _processExistingFilesOnFirstRun;

    private string _password = string.Empty;
    private double _compressionLevel = 5;
    private bool _encryptFileNames = true;
    private double _packTimeoutMinutes = 180;
    private string _archivePrefix = "Backup";
    private bool _writeArchiveManifest = true;
    private bool _verifyAfterPackByExtract = true;

    // 演练默认全关。它要解密、要占临时盘、云端包还得先下下来 ——
    // 这种会自己跑起来干重活的功能，默认开是不礼貌的。
    private bool _cloudDrillEnabled;
    private double _cloudDrillIntervalHours = 168;
    private double _drillTimeoutMinutes = 60;

    private double _zipTempKeepDays = 3;
    private double _zipTempMaxCount = 20;
    private double _zipTempMaxTotalGb = 5;
    private double _minFreeDiskGb = 10;
    private double _maxAttemptsBeforeQuarantine = 6;

    // 容量预算。默认 0 = 不限制，与 AppSettings 的默认值一致；
    // 单位默认 GB（幂次 3），用户一打开下拉框就在最常用的那一档上。
    private double _cloudQuotaValue;
    private ChoiceItem<int> _cloudQuotaUnit;
    private double _cloudQuotaWarnValue;
    private ChoiceItem<int> _cloudQuotaWarnUnit;

    private bool _releaseLocalSpace = true;
    private double _uploadTimeoutMinutes = 120;
    private double _uploadPollSeconds = 15;

    private ChoiceItem<NotifierKind> _notifyChannel;
    private string _webhookUrl = string.Empty;
    private string _telegramBotToken = string.Empty;
    private string _telegramChatId = string.Empty;

    // 12 个事件开关，顺序按一次备份的时间线排：
    // 启动 → 开工 → 开始 → 就绪 → 打包 → 上传 → 结束 → 收工 → 失败 / 隔离 / 磁盘 → 停止。
    private bool _notifyEngineStarted = true;
    private bool _notifyScheduleOpened = true;
    private bool _notifyRoundStarted = true;
    private bool _notifyFilesStable = true;
    private bool _notifyPackCompleted = true;
    private bool _notifyUploadCompleted = true;
    private bool _notifyRoundFinished = true;
    private bool _notifyScheduleClosed = true;
    private bool _notifyFailure = true;
    private bool _notifyQuarantined = true;
    private bool _notifyDiskWarning = true;
    private bool _notifyEngineStopped = true;

    private bool _startMinimized;
    private bool _autoStartEngine;
    private ChoiceItem<LogLevel> _logLevel;
    private ChoiceItem<ThemeMode> _theme;
    private ChoiceItem<CloudTarget> _cloudTarget;
    private string _accentColor = AccentColorSpec.FollowSystem;
    private string _backgroundImagePath = string.Empty;
    private double _backgroundBlur = 18;
    private double _backgroundOpacity = 30;

    /// <summary>背景图为什么没用上。由 <c>MainViewModel</c> 从 <c>ThemeService</c> 那里灌进来。</summary>
    private string _backgroundProblem = string.Empty;

    private bool _dirty;

    public SettingsViewModel()
    {
        _notifyChannel = NotifyChannels[0];
        _logLevel = LogLevels[1];
        _theme = Themes[0];
        _cloudTarget = CloudTargets[0];

        // GB 那一档。不能写 ByteUnits[1] 之外的硬下标 —— 列表以后要是加了 KB，
        // 下标会整体错位，而错位的后果是默认单位悄悄变成别的。按值找最稳。
        _cloudQuotaUnit = UnitFor(3);
        _cloudQuotaWarnUnit = UnitFor(3);

        PickAccentCommand = new RelayCommand<string>(PickAccent);
        ResetAccentCommand = new RelayCommand(() => AccentColor = AccentColorSpec.FollowSystem);
        ClearBackgroundCommand = new RelayCommand(() => BackgroundImagePath = string.Empty);

        // 账号是构造完之后由 MainViewModel 灌进来的，灌进来这一刻「本机发现的账号」那一行
        // 的显隐条件（ShowOneDriveAccounts）就变了，得主动通知界面重算一次。
        OneDriveAccounts.CollectionChanged += (_, _) => Raise(nameof(ShowOneDriveAccounts));
    }

    // ==================== 下拉框数据源 ====================

    public IReadOnlyList<ChoiceItem<NotifierKind>> NotifyChannels { get; } =
    [
        .. Enum.GetValues<NotifierKind>()
            .Select(k => new ChoiceItem<NotifierKind>(k, NotifierFactory.NameOf(k))),
    ];

    public IReadOnlyList<ChoiceItem<LogLevel>> LogLevels { get; } =
    [
        new(LogLevel.Debug, "调试（最详细，排查问题时用）"),
        new(LogLevel.Info, "普通"),
        new(LogLevel.Warn, "仅警告与错误"),
        new(LogLevel.Error, "仅错误"),
    ];

    public IReadOnlyList<ChoiceItem<ThemeMode>> Themes { get; } =
    [
        new(ThemeMode.System, "跟随系统"),
        new(ThemeMode.Light, "浅色"),
        new(ThemeMode.Dark, "深色"),
    ];

    /// <summary>云盘类型。选 <see cref="CloudTarget.Folder"/> 后整条 OneDrive 逻辑都不再参与。</summary>
    public IReadOnlyList<ChoiceItem<CloudTarget>> CloudTargets { get; } =
    [
        .. Enum.GetValues<CloudTarget>()
            .Select(t => new ChoiceItem<CloudTarget>(t, CloudTargetText.Describe(t))),
    ];

    /// <summary>本机发现的 OneDrive 账号目录，设置页可以一键填入。</summary>
    public ObservableCollection<OneDriveChoice> OneDriveAccounts { get; } = [];

    /// <summary>
    /// 下拉框里最小的那一档（MB）的幂次。读档换算时当下限用 ——
    /// 拆出来的单位若比它还小，下拉框里根本没有对应项。
    /// </summary>
    private const int MinByteUnitPower = 2;

    /// <summary>
    /// 容量单位下拉框。<c>Value</c> 就是 1024 的幂次（MB=2、GB=3、TB=4）。
    ///
    /// 只给 MB 以上：容量预算填到 KB 这一档没有任何现实意义，
    /// 而选项越少越不容易选错单位 —— 选错一档就是 1024 倍的偏差。
    /// </summary>
    public IReadOnlyList<ChoiceItem<int>> ByteUnits { get; } =
    [
        new(2, "MB"),
        new(3, "GB"),
        new(4, "TB"),
        new(5, "PB"),
    ];

    // ==================== 路径 ====================

    public string MonitorPath
    {
        get => _monitorPath;
        set { if (Set(ref _monitorPath, value)) { MarkDirty(); } }
    }

    public bool IncludeSubdirectories
    {
        get => _includeSubdirectories;
        set { if (Set(ref _includeSubdirectories, value)) { MarkDirty(); } }
    }

    /// <summary>一行一条通配符。比旧版写死在代码里的排除列表灵活得多。</summary>
    public string ExcludePatterns
    {
        get => _excludePatterns;
        set { if (Set(ref _excludePatterns, value)) { MarkDirty(); } }
    }

    /// <summary>压缩包最终落点。名字不叫 OneDrivePath 了 —— 它可以是任何云盘的本地同步目录。</summary>
    public string CloudPath
    {
        get => _cloudPath;
        set { if (Set(ref _cloudPath, value)) { MarkDirty(); } }
    }

    /// <summary>
    /// 云盘类型。切换它会连带影响一堆界面文案与校验项，所以改完要顺手通知那几个派生属性。
    /// </summary>
    public ChoiceItem<CloudTarget> CloudTargetChoice
    {
        get => _cloudTarget;
        set
        {
            if (Set(ref _cloudTarget, value))
            {
                MarkDirty();
                Raise(nameof(IsOneDriveTarget));
                Raise(nameof(ShowOneDriveAccounts));
                Raise(nameof(CloudPathLabel));
                Raise(nameof(CloudPathHint));
            }
        }
    }

    /// <summary>OneDrive 模式才显示"等待上传确认""释放本地空间"那一组设置。</summary>
    public bool IsOneDriveTarget => _cloudTarget.Value == CloudTarget.OneDrive;

    /// <summary>
    /// 「本机发现的账号」那一行该不该显示。
    ///
    /// 两个条件缺一不可：<b>当前是 OneDrive 模式</b>，且<b>本机确实枚举到了账号</b>。
    /// 只看有没有账号是不够的 —— 那一行的唯一作用是把某个 OneDrive 同步目录一键填进
    /// 目标路径；一旦云盘类型切成"其他云盘 / 普通目录"，填进去的还是 OneDrive 的路径，
    /// 对这条分支毫无意义，反倒会把人往错路径上带。XAML 里那句"非 OneDrive 模式下
    /// 这一行没有意义"的注释一直都在，只是旧绑定漏了这半个条件。
    /// </summary>
    public bool ShowOneDriveAccounts => IsOneDriveTarget && OneDriveAccounts.Count > 0;

    public string CloudPathLabel => CloudTargetText.PathLabel(_cloudTarget.Value);

    public string CloudPathHint => _cloudTarget.Value == CloudTarget.OneDrive
        ? "压缩包会被剪切到这里，程序随后确认云端已收到，再按设置释放本地占用。可以是同步根下的任意子目录。"
        : "压缩包会被剪切到这里，本轮到此结束。之后的同步由这个目录自己的客户端负责，程序不等待、也不释放本地空间。";

    public string ZipTempPath
    {
        get => _zipTempPath;
        set { if (Set(ref _zipTempPath, value)) { MarkDirty(); } }
    }

    // ==================== 计划 ====================

    /// <summary>
    /// 是否限定生效日期范围。
    ///
    /// 旧版把"绝对日期范围"和"每日时段"挤在同两个 DateTimePicker 里，
    /// 而 <c>MainForm_Load</c> 又偷偷把每日时段设成"当前时刻 → 23:59"，
    /// 于是程序在启动那一刻之前的时段永远不工作，用户完全无法察觉。
    /// </summary>
    public bool LimitDates
    {
        get => _limitDates;
        set { if (Set(ref _limitDates, value)) { MarkDirty(); Raise(nameof(EffectiveFromHint)); } }
    }

    public DateTime? EffectiveFrom
    {
        get => _effectiveFrom;
        set { if (Set(ref _effectiveFrom, value)) { MarkDirty(); Raise(nameof(EffectiveFromHint)); } }
    }

    public DateTime? EffectiveTo
    {
        get => _effectiveTo;
        set { if (Set(ref _effectiveTo, value)) { MarkDirty(); Raise(nameof(EffectiveFromHint)); } }
    }

    /// <summary>
    /// 说清楚"起始日期"到底管什么 —— 它同时是<b>文件的准入下限</b>，不只是"哪天开始运行"。
    /// 这一条正是用户报告的困惑点：设了起始日期，却不见它去认那天之后已经存在的文件。
    /// </summary>
    public string EffectiveFromHint
    {
        get
        {
            if (!LimitDates || EffectiveFrom is not { } from)
            {
                return "不限定日期：只备份程序运行期间新出现的文件。" +
                       "想把目录里已有的文件也备份，就设一个起始日期，或勾选下面「文件监控」里的首次启动选项。";
            }

            string range = EffectiveTo is { } to
                ? $"{from:yyyy-MM-dd} 到 {to:yyyy-MM-dd}"
                : $"{from:yyyy-MM-dd} 起";

            return $"生效范围：{range}。起始日期同时是文件的准入下限 —— " +
                   $"修改时间在 {from:yyyy-MM-dd} 00:00 之后的文件都会被备份，" +
                   "包括程序启动前就已经在目录里的；早于这一天的一律不碰。";
        }
    }

    public bool AllDay
    {
        get => _allDay;
        set { if (Set(ref _allDay, value)) { MarkDirty(); Raise(nameof(DailyRangeHint)); } }
    }

    public string DailyStartText
    {
        get => _dailyStartText;
        set { if (Set(ref _dailyStartText, value)) { MarkDirty(); Raise(nameof(DailyRangeHint)); } }
    }

    public string DailyEndText
    {
        get => _dailyEndText;
        set { if (Set(ref _dailyEndText, value)) { MarkDirty(); Raise(nameof(DailyRangeHint)); } }
    }

    /// <summary>把时段解释成人话，特别是跨夜的情况 —— 旧版跨夜判断恒为假，永远不工作。</summary>
    public string DailyRangeHint
    {
        get
        {
            if (AllDay)
            {
                return "全天工作，不受时段限制。";
            }

            if (!TryParseTime(DailyStartText, out TimeOnly start)
                || !TryParseTime(DailyEndText, out TimeOnly end))
            {
                return "时间格式应为 HH:mm，例如 22:00。";
            }

            if (start == end)
            {
                return "开始与结束相同，等于全天不工作。请改一个。";
            }

            return end < start
                ? $"跨夜时段：每天 {start:HH\\:mm} 到次日 {end:HH\\:mm}。"
                : $"每天 {start:HH\\:mm} 到 {end:HH\\:mm}。";
        }
    }

    public double BatchWindowMinutes
    {
        get => _batchWindowMinutes;
        set { if (Set(ref _batchWindowMinutes, value)) { MarkDirty(); } }
    }

    // ==================== 监控 ====================

    public double QuietSeconds
    {
        get => _quietSeconds;
        set { if (Set(ref _quietSeconds, value)) { MarkDirty(); Raise(nameof(ReadyDelayHint)); } }
    }

    public double StableConfirmRounds
    {
        get => _stableConfirmRounds;
        set { if (Set(ref _stableConfirmRounds, value)) { MarkDirty(); Raise(nameof(ReadyDelayHint)); } }
    }

    public double RefreshIntervalSeconds
    {
        get => _refreshIntervalSeconds;
        set { if (Set(ref _refreshIntervalSeconds, value)) { MarkDirty(); Raise(nameof(ReadyDelayHint)); } }
    }

    /// <summary>
    /// 把三个参数换算成"一个文件写完后大约多久会被认为就绪"。
    /// 旧版需要 5 × StableSeconds = 300 秒，刚好撞上默认 5 分钟的批处理窗口，
    /// 而界面上完全看不出这个关系。
    /// </summary>
    public string ReadyDelayHint =>
        $"文件停止写入后，约 {QuietSeconds + ((StableConfirmRounds - 1) * RefreshIntervalSeconds):0} 秒被判定为就绪。";

    public double UnreadableGiveUpMinutes
    {
        get => _unreadableGiveUpMinutes;
        set { if (Set(ref _unreadableGiveUpMinutes, value)) { MarkDirty(); Raise(nameof(UnreadableGiveUpHint)); } }
    }

    /// <summary>
    /// 把这个值换算成用户实际会看到的现象。
    /// 光写"最多等 N 分钟"说不清两件要紧事：什么样的文件才会走进这个计时，以及到点之后会怎样。
    /// 前者决定用户该不该动这个值（数据库备份多半根本不走这条路），
    /// 后者决定用户看到告警时慌不慌（文件没丢，只是这一轮先放着）。
    /// </summary>
    public string UnreadableGiveUpHint =>
        $"一个文件连续 {UnreadableGiveUpMinutes:0} 分钟读不到，就先放下它：记一条警告，" +
        $"并把它挂进「待重新处理」名单，下次对账扫描（每 {ReconcileIntervalSeconds:0} 秒）重新纳入观察。";

    public double ReconcileIntervalSeconds
    {
        get => _reconcileIntervalSeconds;
        set { if (Set(ref _reconcileIntervalSeconds, value)) { MarkDirty(); Raise(nameof(UnreadableGiveUpHint)); } }
    }

    public bool ProcessExistingFilesOnFirstRun
    {
        get => _processExistingFilesOnFirstRun;
        set { if (Set(ref _processExistingFilesOnFirstRun, value)) { MarkDirty(); } }
    }

    // ==================== 压缩 ====================

    public string Password
    {
        get => _password;
        set { if (Set(ref _password, value)) { MarkDirty(); } }
    }

    public double CompressionLevel
    {
        get => _compressionLevel;
        set { if (Set(ref _compressionLevel, value)) { MarkDirty(); Raise(nameof(CompressionHint)); } }
    }

    public string CompressionHint => CompressionLevel switch
    {
        <= 0 => "仅打包不压缩，最快。已压缩过的文件（视频、图片）选这个最合适。",
        <= 3 => "轻度压缩，速度优先。",
        <= 6 => "平衡（推荐）。",
        <= 8 => "高压缩率，明显更慢、更吃内存。",
        _ => "极限压缩，非常慢且吃内存，收益通常有限。",
    };

    public bool EncryptFileNames
    {
        get => _encryptFileNames;
        set { if (Set(ref _encryptFileNames, value)) { MarkDirty(); } }
    }

    public double PackTimeoutMinutes
    {
        get => _packTimeoutMinutes;
        set { if (Set(ref _packTimeoutMinutes, value)) { MarkDirty(); } }
    }

    public string ArchivePrefix
    {
        get => _archivePrefix;
        set { if (Set(ref _archivePrefix, value)) { MarkDirty(); } }
    }

    /// <summary>
    /// 每个归档旁边放一份明文清单（<c>.7z.manifest.json</c>）。
    ///
    /// 注意它<b>不含任何源文件名</b>：加密时 <c>-mhe=on</c> 隐藏的正是文件名表，
    /// 清单里写名字等于把这道保护从旁边捅穿。清单只放条目数、总字节数、校验值，
    /// 够用来判"包完不完整、和旁边的包对不对得上"就够了。
    /// </summary>
    public bool WriteArchiveManifest
    {
        get => _writeArchiveManifest;
        set { if (Set(ref _writeArchiveManifest, value)) { MarkDirty(); Raise(nameof(DrillHint)); } }
    }

    /// <summary>
    /// 打完包立刻把包解一遍、逐条比对，确认"它真的能还原"再交付。
    ///
    /// 比只跑 <c>7z t</c> 重得多（要真解压、要算校验值），但 <c>t</c> 只测压缩流本身，
    /// 测不出"这一包其实少压了几个文件"—— 而后者恰恰是这个程序最需要发现的事。
    /// </summary>
    public bool VerifyAfterPackByExtract
    {
        get => _verifyAfterPackByExtract;
        set { if (Set(ref _verifyAfterPackByExtract, value)) { MarkDirty(); } }
    }

    // ==================== 配额与磁盘 ====================

    public double ZipTempKeepDays
    {
        get => _zipTempKeepDays;
        set { if (Set(ref _zipTempKeepDays, value)) { MarkDirty(); } }
    }

    public double ZipTempMaxCount
    {
        get => _zipTempMaxCount;
        set { if (Set(ref _zipTempMaxCount, value)) { MarkDirty(); } }
    }

    public double ZipTempMaxTotalGb
    {
        get => _zipTempMaxTotalGb;
        set { if (Set(ref _zipTempMaxTotalGb, value)) { MarkDirty(); } }
    }

    public double MinFreeDiskGb
    {
        get => _minFreeDiskGb;
        set { if (Set(ref _minFreeDiskGb, value)) { MarkDirty(); } }
    }

    public double MaxAttemptsBeforeQuarantine
    {
        get => _maxAttemptsBeforeQuarantine;
        set { if (Set(ref _maxAttemptsBeforeQuarantine, value)) { MarkDirty(); } }
    }

    // ==================== 云盘容量预算 ====================
    //
    // 数值与单位分成两个属性，配置里仍然只存一个字节数。
    // 换算集中在 ByteSize.ToUnit / FromUnit，这里不自己乘 1024。

    /// <summary>总容量的数值部分。0 = 不限制，整套容量检查关闭。</summary>
    public double CloudQuotaValue
    {
        get => _cloudQuotaValue;
        set { if (Set(ref _cloudQuotaValue, value)) { MarkDirty(); Raise(nameof(CloudQuotaHint)); } }
    }

    public ChoiceItem<int> CloudQuotaUnit
    {
        get => _cloudQuotaUnit;
        set { if (Set(ref _cloudQuotaUnit, value)) { MarkDirty(); Raise(nameof(CloudQuotaHint)); } }
    }

    /// <summary>警戒容量的数值部分。0 = 不告警。</summary>
    public double CloudQuotaWarnValue
    {
        get => _cloudQuotaWarnValue;
        set { if (Set(ref _cloudQuotaWarnValue, value)) { MarkDirty(); Raise(nameof(CloudQuotaHint)); } }
    }

    public ChoiceItem<int> CloudQuotaWarnUnit
    {
        get => _cloudQuotaWarnUnit;
        set { if (Set(ref _cloudQuotaWarnUnit, value)) { MarkDirty(); Raise(nameof(CloudQuotaHint)); } }
    }

    /// <summary>
    /// 这两个数字当前意味着什么，实时显示在输入框下面。
    ///
    /// 值得专门算一句：填的是"1 TB"，而用户真正关心的是"那还能放多少个包"。
    /// 单位选错一档就是 1024 倍的偏差，把换算后的结果直接摆出来最容易发现填错了。
    /// </summary>
    public string CloudQuotaHint
    {
        get
        {
            long quota = ByteSize.FromUnit(CloudQuotaValue, CloudQuotaUnit.Value);

            if (quota <= 0)
            {
                return "留空或填 0 表示不限制容量，程序不会因为容量而拒绝打包。";
            }

            long warn = ByteSize.FromUnit(CloudQuotaWarnValue, CloudQuotaWarnUnit.Value);

            string head = $"上限 {ByteSize.Format(quota)}";

            return warn <= 0
                ? head + "。未设警戒线，只有真的放不下时才会拒绝打包。"
                : warn >= quota
                    ? head + $"，警戒线 {ByteSize.Format(warn)} —— 警戒线不低于上限，每一轮都会告警，请调小。"
                    : head + $"，剩余不足 {ByteSize.Format(warn)} 时告警。";
        }
    }

    /// <summary>按幂次取单位项。找不到就退到 GB。</summary>
    private ChoiceItem<int> UnitFor(int power) =>
        ByteUnits.FirstOrDefault(u => u.Value == power)
        ?? ByteUnits.FirstOrDefault(u => u.Value == 3)
        ?? ByteUnits[0];

    // ==================== 恢复演练 ====================

    /// <summary>
    /// 让引擎自己隔一阵挑一个包真解一遍。
    ///
    /// 默认关。它和"打完包立刻验"不是一回事：那个验的是刚出炉的包，
    /// 而包在云上躺三个月之后还坏没坏，只有在三个月后真去解一次才知道 ——
    /// 而用户通常正是在这三个月里把本地那份删了。
    /// </summary>
    public bool CloudDrillEnabled
    {
        get => _cloudDrillEnabled;
        set { if (Set(ref _cloudDrillEnabled, value)) { MarkDirty(); Raise(nameof(DrillHint)); } }
    }

    public double CloudDrillIntervalHours
    {
        get => _cloudDrillIntervalHours;
        set { if (Set(ref _cloudDrillIntervalHours, value)) { MarkDirty(); Raise(nameof(DrillHint)); } }
    }

    public double DrillTimeoutMinutes
    {
        get => _drillTimeoutMinutes;
        set { if (Set(ref _drillTimeoutMinutes, value)) { MarkDirty(); } }
    }

    /// <summary>把间隔小时数翻译成人话，顺带把两个已知的坑说出来。</summary>
    public string DrillHint
    {
        get
        {
            if (!CloudDrillEnabled)
            {
                return "关着的时候一份包都不会被解，临时目录也不会多占一点空间。";
            }

            int hours = (int)Math.Round(CloudDrillIntervalHours);

            string every = hours switch
            {
                < 0 => "每隔一段时间",
                < 24 => $"每 {hours} 小时",
                < 48 => "每天",
                < 168 => $"每 {hours / 24} 天",
                < 336 => "每周",
                _ => $"每 {hours / 24} 天",
            };

            string warn = hours < 24
                ? "\n间隔小于 24 小时意义不大 —— 云盘上的包不会一夜之间坏掉，只会白耗流量和临时空间。"
                : string.Empty;

            string manifest = WriteArchiveManifest
                ? string.Empty
                : "\n注意：关掉了「随包清单」，演练只能验出「解得开」，验不出「少压了几个文件」—— 建议一并打开。";

            return $"大约{every}挑一份归档解出来核对一遍。云端包会先下载到临时目录，核对完立即删除。" + warn + manifest;
        }
    }

    // ==================== OneDrive ====================

    public bool ReleaseLocalSpace
    {
        get => _releaseLocalSpace;
        set { if (Set(ref _releaseLocalSpace, value)) { MarkDirty(); } }
    }

    public double UploadTimeoutMinutes
    {
        get => _uploadTimeoutMinutes;
        set { if (Set(ref _uploadTimeoutMinutes, value)) { MarkDirty(); } }
    }

    public double UploadPollSeconds
    {
        get => _uploadPollSeconds;
        set { if (Set(ref _uploadPollSeconds, value)) { MarkDirty(); } }
    }

    // ==================== 通知 ====================

    /// <summary>
    /// 显式选择渠道。旧版靠 <c>hook.Contains("weixin")</c> 猜，
    /// 而各渠道自己的 <c>IsEnabled</c> 用的又是另一套前缀规则，两者不一致时
    /// 界面显示"✔ 企业微信群"、消息却被静默丢弃。
    /// </summary>
    public ChoiceItem<NotifierKind> NotifyChannel
    {
        get => _notifyChannel;
        set
        {
            if (Set(ref _notifyChannel, value))
            {
                MarkDirty();
                Raise(nameof(NeedsWebhookUrl));
                Raise(nameof(NeedsTelegram));
                Raise(nameof(NotifyConfigured));
            }
        }
    }

    public bool NeedsWebhookUrl =>
        NotifyChannel.Value is NotifierKind.WeCom or NotifierKind.DingTalk or NotifierKind.Webhook;

    public bool NeedsTelegram => NotifyChannel.Value == NotifierKind.Telegram;

    public bool NotifyConfigured => NotifyChannel.Value != NotifierKind.None;

    public string WebhookUrl
    {
        get => _webhookUrl;
        set { if (Set(ref _webhookUrl, value)) { MarkDirty(); } }
    }

    public string TelegramBotToken
    {
        get => _telegramBotToken;
        set { if (Set(ref _telegramBotToken, value)) { MarkDirty(); } }
    }

    public string TelegramChatId
    {
        get => _telegramChatId;
        set { if (Set(ref _telegramChatId, value)) { MarkDirty(); } }
    }

    /// <summary>【启动】—— 程序本身开始运行。</summary>
    public bool NotifyEngineStarted
    {
        get => _notifyEngineStarted;
        set { if (Set(ref _notifyEngineStarted, value)) { MarkDirty(); } }
    }

    /// <summary>【开工】—— 进入工作时段。全天模式下没有"进出"，这一条不会发。</summary>
    public bool NotifyScheduleOpened
    {
        get => _notifyScheduleOpened;
        set { if (Set(ref _notifyScheduleOpened, value)) { MarkDirty(); } }
    }

    /// <summary>
    /// 【第 N 次发现新文件】—— 一发现新文件就发，正文里逐个列出文件名。
    /// N 是本轮第几次通报（一条通知 +1，与那一条带了几个文件无关）；
    /// 正文里的序号每条通知各自从 1 数。同一个文件在一轮里只通报一次，大小变了不再重发。
    /// </summary>
    public bool NotifyRoundStarted
    {
        get => _notifyRoundStarted;
        set { if (Set(ref _notifyRoundStarted, value)) { MarkDirty(); } }
    }

    /// <summary>【就绪】—— 窗口内所有文件都不再变化，一个窗口只发一次。</summary>
    public bool NotifyFilesStable
    {
        get => _notifyFilesStable;
        set { if (Set(ref _notifyFilesStable, value)) { MarkDirty(); } }
    }

    /// <summary>【打包】—— 压缩包已生成并校验通过。</summary>
    public bool NotifyPackCompleted
    {
        get => _notifyPackCompleted;
        set { if (Set(ref _notifyPackCompleted, value)) { MarkDirty(); } }
    }

    /// <summary>【上传】—— 压缩包刚进入目标目录、<b>开始</b>上传（不是上传完成）。</summary>
    public bool NotifyUploadCompleted
    {
        get => _notifyUploadCompleted;
        set { if (Set(ref _notifyUploadCompleted, value)) { MarkDirty(); } }
    }

    /// <summary>
    /// 【结束】—— 云端已确认收到，本轮完结，带"共计耗时"。
    /// 同一个开关也管【取消】：源文件在打包前全部消失、本轮什么都没产出时发的那一条
    /// （见 <c>NotifyTag.RoundCancelled</c>，用的是同一个枚举位）。
    /// </summary>
    public bool NotifyRoundFinished
    {
        get => _notifyRoundFinished;
        set { if (Set(ref _notifyRoundFinished, value)) { MarkDirty(); } }
    }

    /// <summary>【收工】—— 离开工作时段，正文里带下一次开始时间。</summary>
    public bool NotifyScheduleClosed
    {
        get => _notifyScheduleClosed;
        set { if (Set(ref _notifyScheduleClosed, value)) { MarkDirty(); } }
    }

    public bool NotifyFailure
    {
        get => _notifyFailure;
        set { if (Set(ref _notifyFailure, value)) { MarkDirty(); } }
    }

    public bool NotifyQuarantined
    {
        get => _notifyQuarantined;
        set { if (Set(ref _notifyQuarantined, value)) { MarkDirty(); } }
    }

    public bool NotifyDiskWarning
    {
        get => _notifyDiskWarning;
        set { if (Set(ref _notifyDiskWarning, value)) { MarkDirty(); } }
    }

    /// <summary>【停止】—— 程序退出。</summary>
    public bool NotifyEngineStopped
    {
        get => _notifyEngineStopped;
        set { if (Set(ref _notifyEngineStopped, value)) { MarkDirty(); } }
    }

    // ==================== 界面与运行 ====================

    public bool StartMinimized
    {
        get => _startMinimized;
        set { if (Set(ref _startMinimized, value)) { MarkDirty(); } }
    }

    public bool AutoStartEngine
    {
        get => _autoStartEngine;
        set { if (Set(ref _autoStartEngine, value)) { MarkDirty(); } }
    }

    public ChoiceItem<LogLevel> LogLevelChoice
    {
        get => _logLevel;
        set { if (Set(ref _logLevel, value)) { MarkDirty(); } }
    }

    // ==================== 主题与皮肤 ====================

    /// <summary>
    /// 深色 / 浅色 / 跟随系统。
    ///
    /// 这一项和强调色都会<b>立即生效并立即落盘</b>（<see cref="ThemeChanged"/> →
    /// <c>MainViewModel.ApplyTheme</c> → <c>EngineHost.SaveAppearance</c>），不走"保存" ——
    /// 挑颜色必须看得见效果，先保存再看是反的。
    ///
    /// 所以这两个属性<b>不能 MarkDirty</b>：它们从来没有"未保存"这个状态。
    /// 标成脏的后果很实在 —— <c>Start()</c> 里有一句"有未保存改动就先保存"，
    /// 于是仅仅点一下标题栏的深浅切换，再点"开始"，就会把设置页上那些
    /// 打了一半的字段（甚至半截路径）一起悄悄提交。这正是外观单独走
    /// <c>SaveAppearance</c> 要避免的事，脏标记会把它整个绕过去。
    /// </summary>
    public ChoiceItem<ThemeMode> ThemeChoice
    {
        get => _theme;
        set
        {
            if (Set(ref _theme, value))
            {
                ThemeChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>
    /// 自定义皮肤色，<c>#RRGGBB</c>；留空 = 跟随 Windows 的系统强调色。
    ///
    /// 存的是用户敲进去的<b>原文</b>，不在 setter 里规范化：
    /// 边打字边被改写（敲到"#1"就变成"#110011"）会让输入框根本没法用。
    /// 规范化留到 <see cref="ToSettings"/> 和实际应用的时候。
    /// </summary>
    public string AccentColor
    {
        get => _accentColor;
        set
        {
            if (!Set(ref _accentColor, value))
            {
                return;
            }

            Raise(nameof(AccentColorValid));
            Raise(nameof(AccentColorHint));
            SyncSwatchSelection();

            // 同 ThemeChoice：外观自己落盘，不参与"未保存"。

            // 只在能解析出颜色时才推给界面，否则打字的中间状态会让配色乱跳。
            if (AccentColorValid)
            {
                ThemeChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public bool AccentColorValid => AccentColorSpec.TryNormalize(_accentColor, out _);

    public string AccentColorHint => AccentColorValid
        ? string.IsNullOrWhiteSpace(_accentColor)
            ? "留空表示跟随 Windows 的系统强调色。"
            : "点上面的色块可以快速选色，也可以直接填十六进制。"
        : "填不出来这个颜色。格式是 #RRGGBB 或 #RGB，例如 #0078D4。";

    /// <summary>预置强调色块。点一下就换，不用自己想十六进制。</summary>
    public IReadOnlyList<AccentSwatch> AccentSwatches { get; } =
    [
        .. AccentColorSpec.Presets.Select(p => new AccentSwatch(p.Name, p.Hex)),
    ];

    // ==================== 背景图 ====================

    /// <summary>
    /// 自定义背景图的路径，空 = 不用背景图。
    ///
    /// 和主题、强调色一样属于"外观"，所以同样<b>不 MarkDirty</b>：立刻生效、立刻落盘。
    /// </summary>
    public string BackgroundImagePath
    {
        get => _backgroundImagePath;
        set
        {
            if (!Set(ref _backgroundImagePath, value ?? string.Empty))
            {
                return;
            }

            // 换图之后旧的失败原因就过期了，先清掉；载入结果由 ThemeService 回灌。
            _backgroundProblem = string.Empty;

            Raise(nameof(HasBackground));
            Raise(nameof(BackgroundHint));
            Raise(nameof(BackgroundProblem));
            Raise(nameof(HasBackgroundProblem));

            ThemeChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>模糊半径。不模糊的照片会和正文抢注意力，所以默认给一档模糊。</summary>
    public double BackgroundBlur
    {
        get => _backgroundBlur;
        set
        {
            if (Set(ref _backgroundBlur, value))
            {
                ThemeChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>不透明度（百分比）。越低越淡，正文越好读。</summary>
    public double BackgroundOpacity
    {
        get => _backgroundOpacity;
        set
        {
            if (Set(ref _backgroundOpacity, value))
            {
                ThemeChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>选了背景图。模糊与不透明度两个滑块据此启用/禁用 —— 没图时调它们没有意义。</summary>
    public bool HasBackground => _backgroundImagePath.Length > 0;

    public string BackgroundHint => HasBackground
        ? "模糊和不透明度调完立刻能看到效果。图片只记路径不复制文件，所以别把它放在会被清理的临时目录里。"
        : "留空＝不用背景图，窗口保持 Windows 的 Mica 材质。支持 PNG、JPG、BMP、GIF、TIFF、WebP。";

    /// <summary>背景图没用上的原因。空＝没问题。</summary>
    public string BackgroundProblem => _backgroundProblem;

    public bool HasBackgroundProblem => _backgroundProblem.Length > 0;

    /// <summary>
    /// 由 <c>MainViewModel</c> 在 <c>ThemeService</c> 应用完外观之后回灌载入结果。
    ///
    /// 设置页自己看不出那张图解开了没有 —— 只有 ThemeService 知道。
    /// 不回灌的后果是：选了一张损坏的图，界面毫无反应，用户完全不知道为什么。
    /// </summary>
    public void NotifyBackgroundStatus(string? problem)
    {
        if (Set(ref _backgroundProblem, problem ?? string.Empty, nameof(BackgroundProblem)))
        {
            Raise(nameof(HasBackgroundProblem));
        }
    }

    /// <summary>清除背景图。</summary>
    public RelayCommand ClearBackgroundCommand { get; }

    /// <summary>主题、强调色或背景图变了，需要立即应用到界面。</summary>
    public event EventHandler? ThemeChanged;

    /// <summary>
    /// 打包成一个外观快照。<c>MainViewModel.ApplyTheme</c> 拿它去应用与落盘。
    ///
    /// 数值在这里取整并夹进合法区间：滑块给的是 double，
    /// 而 <c>19.999</c> 这种值写进 settings.json 只会让人怀疑程序算错了。
    /// </summary>
    public AppearanceSpec ToAppearance() => new AppearanceSpec(
        ThemeChoice.Value,
        AccentColorSpec.Sanitize(AccentColor),
        BackgroundImagePath.Trim(),
        (int)Math.Round(BackgroundBlur),
        (int)Math.Round(BackgroundOpacity))
        .Normalized();

    /// <summary>
    /// 点预置色块选皮肤色，参数是 <c>#RRGGBB</c>。
    ///
    /// 命令放在这里而不是 MainViewModel：它做的事只是改本类的一个属性，
    /// 而设置页的 DataContext 就是本类 —— 放这里 XAML 直接 <c>{Binding PickAccentCommand}</c>，
    /// 不用绕 RelativeSource 去外层找。
    /// </summary>
    public RelayCommand<string> PickAccentCommand { get; }

    /// <summary>皮肤色恢复成"跟随系统强调色"。</summary>
    public RelayCommand ResetAccentCommand { get; }

    /// <summary>点已选中的色块＝取消选择，回到系统强调色 —— 不然选了就再也回不去。</summary>
    private void PickAccent(string? hex)
    {
        string wanted = AccentColorSpec.Sanitize(hex);

        AccentColor = string.Equals(
            AccentColorSpec.Sanitize(_accentColor), wanted, StringComparison.Ordinal)
            ? AccentColorSpec.FollowSystem
            : wanted;
    }

    /// <summary>把当前颜色对应的那个色块标成选中。</summary>
    private void SyncSwatchSelection()
    {
        string current = AccentColorSpec.Sanitize(_accentColor);

        foreach (AccentSwatch swatch in AccentSwatches)
        {
            swatch.IsSelected = swatch.Hex.Equals(current, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>有未保存的改动。界面据此提示"记得保存"。</summary>
    public bool IsDirty
    {
        get => _dirty;
        private set => Set(ref _dirty, value);
    }

    public event EventHandler? Changed;

    // ==================== 载入 / 写回 ====================

    public void Load(AppSettings settings)
    {
        _monitorPath = settings.MonitorPath;
        _includeSubdirectories = settings.IncludeSubdirectories;
        _excludePatterns = string.Join(Environment.NewLine, settings.ExcludePatterns);
        _cloudPath = settings.CloudPath;
        _cloudTarget = CloudTargets.FirstOrDefault(c => c.Value == settings.CloudTarget) ?? CloudTargets[0];
        _zipTempPath = settings.ZipTempPath;

        _limitDates = settings.EffectiveFrom is not null || settings.EffectiveTo is not null;
        _effectiveFrom = settings.EffectiveFrom?.ToDateTime(TimeOnly.MinValue);
        _effectiveTo = settings.EffectiveTo?.ToDateTime(TimeOnly.MinValue);
        _allDay = settings.AllDay;
        _dailyStartText = settings.DailyStart.ToString("HH\\:mm", CultureInfo.InvariantCulture);
        _dailyEndText = settings.DailyEnd.ToString("HH\\:mm", CultureInfo.InvariantCulture);
        _batchWindowMinutes = settings.BatchWindowMinutes;

        _quietSeconds = settings.QuietSeconds;
        _stableConfirmRounds = settings.StableConfirmRounds;
        _refreshIntervalSeconds = settings.RefreshIntervalSeconds;
        _unreadableGiveUpMinutes = settings.UnreadableGiveUpMinutes;
        _reconcileIntervalSeconds = settings.ReconcileIntervalSeconds;
        _processExistingFilesOnFirstRun = settings.ProcessExistingFilesOnFirstRun;

        _password = settings.Password;
        _compressionLevel = settings.CompressionLevel;
        _encryptFileNames = settings.EncryptFileNames;
        _packTimeoutMinutes = settings.PackTimeoutMinutes;
        _archivePrefix = settings.ArchivePrefix;
        _writeArchiveManifest = settings.WriteArchiveManifest;
        _verifyAfterPackByExtract = settings.VerifyAfterPackByExtract;

        _cloudDrillEnabled = settings.CloudDrillEnabled;
        _cloudDrillIntervalHours = settings.CloudDrillIntervalHours;
        _drillTimeoutMinutes = settings.DrillTimeoutMinutes;

        _zipTempKeepDays = settings.ZipTempKeepDays;
        _zipTempMaxCount = settings.ZipTempMaxCount;
        _zipTempMaxTotalGb = ToGb(settings.ZipTempMaxTotalBytes);
        _minFreeDiskGb = ToGb(settings.MinFreeDiskBytes);
        _maxAttemptsBeforeQuarantine = settings.MaxAttemptsBeforeQuarantine;

        // 挑能整除的最大单位显示：1 TB 要显示成"1 TB"而不是"1024 GB"。
        // 否则每存一次读一次，单位就往下掉一档，用户会以为自己填错了。
        (_cloudQuotaValue, int quotaPower) = ByteSize.ToUnit(settings.CloudQuotaBytes, MinByteUnitPower);
        (_cloudQuotaWarnValue, int warnPower) = ByteSize.ToUnit(settings.CloudQuotaWarnBytes, MinByteUnitPower);
        _cloudQuotaUnit = UnitFor(quotaPower);
        _cloudQuotaWarnUnit = UnitFor(warnPower);

        _releaseLocalSpace = settings.ReleaseLocalSpace;
        _uploadTimeoutMinutes = settings.UploadTimeoutMinutes;
        _uploadPollSeconds = settings.UploadPollSeconds;

        _notifyChannel = NotifyChannels.FirstOrDefault(c => c.Value == settings.NotifyChannel)
                         ?? NotifyChannels[0];
        _webhookUrl = settings.WebhookUrl;
        _telegramBotToken = settings.TelegramBotToken;
        _telegramChatId = settings.TelegramChatId;

        NotifyEvent events = settings.EnabledEvents;
        _notifyEngineStarted = events.HasFlag(NotifyEvent.EngineStarted);
        _notifyScheduleOpened = events.HasFlag(NotifyEvent.ScheduleOpened);
        _notifyRoundStarted = events.HasFlag(NotifyEvent.RoundStarted);
        _notifyFilesStable = events.HasFlag(NotifyEvent.FilesStable);
        _notifyPackCompleted = events.HasFlag(NotifyEvent.PackCompleted);
        _notifyUploadCompleted = events.HasFlag(NotifyEvent.UploadCompleted);
        _notifyRoundFinished = events.HasFlag(NotifyEvent.RoundFinished);
        _notifyScheduleClosed = events.HasFlag(NotifyEvent.ScheduleClosed);
        _notifyFailure = events.HasFlag(NotifyEvent.Failure);
        _notifyQuarantined = events.HasFlag(NotifyEvent.Quarantined);
        _notifyDiskWarning = events.HasFlag(NotifyEvent.DiskWarning);
        _notifyEngineStopped = events.HasFlag(NotifyEvent.EngineStopped);

        _startMinimized = settings.StartMinimized;
        _autoStartEngine = settings.AutoStartEngine;
        _logLevel = LogLevels.FirstOrDefault(c => c.Value == settings.LogLevel) ?? LogLevels[1];
        _theme = Themes.FirstOrDefault(c => c.Value == settings.Theme) ?? Themes[0];
        _accentColor = AccentColorSpec.Sanitize(settings.AccentColor);
        _backgroundImagePath = settings.BackgroundImagePath;
        _backgroundBlur = settings.BackgroundBlur;
        _backgroundOpacity = settings.BackgroundOpacity;
        _backgroundProblem = string.Empty;
        SyncSwatchSelection();

        // 一次性通知全部属性变更，比逐个 Raise 简单也更不容易漏。
        Raise(null);
        IsDirty = false;
    }

    /// <summary>写回一个新的 <see cref="AppSettings"/>。数值越界由 <c>ClampToLimits</c> 兜底。</summary>
    public AppSettings ToSettings()
    {
        AppSettings s = new()
        {
            MonitorPath = MonitorPath.Trim(),
            IncludeSubdirectories = IncludeSubdirectories,
            ExcludePatterns = ParsePatterns(ExcludePatterns),
            CloudPath = CloudPath.Trim(),
            CloudTarget = CloudTargetChoice.Value,
            ZipTempPath = ZipTempPath.Trim(),

            EffectiveFrom = LimitDates && EffectiveFrom is { } from ? DateOnly.FromDateTime(from) : null,
            EffectiveTo = LimitDates && EffectiveTo is { } to ? DateOnly.FromDateTime(to) : null,
            AllDay = AllDay,
            DailyStart = TryParseTime(DailyStartText, out TimeOnly start) ? start : new TimeOnly(0, 0),
            DailyEnd = TryParseTime(DailyEndText, out TimeOnly end) ? end : new TimeOnly(23, 59),
            BatchWindowMinutes = (int)Math.Round(BatchWindowMinutes),

            QuietSeconds = (int)Math.Round(QuietSeconds),
            StableConfirmRounds = (int)Math.Round(StableConfirmRounds),
            RefreshIntervalSeconds = (int)Math.Round(RefreshIntervalSeconds),
            UnreadableGiveUpMinutes = (int)Math.Round(UnreadableGiveUpMinutes),
            ReconcileIntervalSeconds = (int)Math.Round(ReconcileIntervalSeconds),
            ProcessExistingFilesOnFirstRun = ProcessExistingFilesOnFirstRun,

            Password = Password,
            CompressionLevel = (int)Math.Round(CompressionLevel),
            EncryptFileNames = EncryptFileNames,
            PackTimeoutMinutes = (int)Math.Round(PackTimeoutMinutes),
            ArchivePrefix = ArchivePrefix.Trim(),
            WriteArchiveManifest = WriteArchiveManifest,
            VerifyAfterPackByExtract = VerifyAfterPackByExtract,

            CloudDrillEnabled = CloudDrillEnabled,
            CloudDrillIntervalHours = (int)Math.Round(CloudDrillIntervalHours),
            DrillTimeoutMinutes = (int)Math.Round(DrillTimeoutMinutes),

            ZipTempKeepDays = (int)Math.Round(ZipTempKeepDays),
            ZipTempMaxCount = (int)Math.Round(ZipTempMaxCount),
            ZipTempMaxTotalBytes = FromGb(ZipTempMaxTotalGb),
            MinFreeDiskBytes = FromGb(MinFreeDiskGb),
            MaxAttemptsBeforeQuarantine = (int)Math.Round(MaxAttemptsBeforeQuarantine),

            CloudQuotaBytes = ByteSize.FromUnit(CloudQuotaValue, CloudQuotaUnit.Value),
            CloudQuotaWarnBytes = ByteSize.FromUnit(CloudQuotaWarnValue, CloudQuotaWarnUnit.Value),

            ReleaseLocalSpace = ReleaseLocalSpace,
            UploadTimeoutMinutes = (int)Math.Round(UploadTimeoutMinutes),
            UploadPollSeconds = (int)Math.Round(UploadPollSeconds),

            NotifyChannel = NotifyChannel.Value,
            WebhookUrl = WebhookUrl.Trim(),
            TelegramBotToken = TelegramBotToken.Trim(),
            TelegramChatId = TelegramChatId.Trim(),
            EnabledEvents = CollectEvents(),

            StartMinimized = StartMinimized,
            AutoStartEngine = AutoStartEngine,
            LogLevel = LogLevelChoice.Value,

            Theme = ThemeChoice.Value,

            // 存规范化后的值：用户可能敲的是"0078d4"或"#07d"，落盘统一成 #RRGGBB。
            // 解析不出来时 Sanitize 会退回"跟随系统"，不会把坏值写进 settings.json。
            AccentColor = AccentColorSpec.Sanitize(AccentColor),

            // 背景图这三项也要写回来。外观虽然走自己那条"立刻落盘"的路，
            // 但"保存"按钮是整份覆盖写 —— 这里漏一项，点一次保存就把用户选的背景图清空了。
            BackgroundImagePath = BackgroundImagePath.Trim(),
            BackgroundBlur = (int)Math.Round(BackgroundBlur),
            BackgroundOpacity = (int)Math.Round(BackgroundOpacity),
        };

        s.ClampToLimits();
        return s;
    }

    public void MarkSaved() => IsDirty = false;

    private void MarkDirty()
    {
        IsDirty = true;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private NotifyEvent CollectEvents()
    {
        NotifyEvent events = NotifyEvent.None;

        if (NotifyEngineStarted) { events |= NotifyEvent.EngineStarted; }
        if (NotifyScheduleOpened) { events |= NotifyEvent.ScheduleOpened; }
        if (NotifyRoundStarted) { events |= NotifyEvent.RoundStarted; }
        if (NotifyFilesStable) { events |= NotifyEvent.FilesStable; }
        if (NotifyPackCompleted) { events |= NotifyEvent.PackCompleted; }
        if (NotifyUploadCompleted) { events |= NotifyEvent.UploadCompleted; }
        if (NotifyRoundFinished) { events |= NotifyEvent.RoundFinished; }
        if (NotifyScheduleClosed) { events |= NotifyEvent.ScheduleClosed; }
        if (NotifyFailure) { events |= NotifyEvent.Failure; }
        if (NotifyQuarantined) { events |= NotifyEvent.Quarantined; }
        if (NotifyDiskWarning) { events |= NotifyEvent.DiskWarning; }
        if (NotifyEngineStopped) { events |= NotifyEvent.EngineStopped; }

        return events;
    }

    private static List<string> ParsePatterns(string text) =>
    [
        .. text.Split(['\r', '\n', ';', ','], StringSplitOptions.RemoveEmptyEntries
                                              | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase),
    ];

    /// <summary>只认 <c>HH:mm</c> 与 <c>HH:mm:ss</c>，且固定用 InvariantCulture —— 换区域设置也不会解析失败。</summary>
    private static bool TryParseTime(string text, out TimeOnly time) =>
        TimeOnly.TryParseExact(
            text?.Trim() ?? string.Empty,
            ["HH\\:mm", "H\\:mm", "HH\\:mm\\:ss"],
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out time);

    private static double ToGb(long bytes) =>
        Math.Round(bytes / (1024.0 * 1024 * 1024), 2);

    private static long FromGb(double gb) =>
        (long)Math.Round(Math.Max(0, gb) * 1024 * 1024 * 1024);
}

/// <summary>设置页里可一键填入的 OneDrive 目录。</summary>
public sealed record OneDriveChoice(string Path, string Text)
{
    public override string ToString() => Text;
}
