using System.Text.Json.Serialization;

using NewAutoZip.Core.Diagnostics;
using NewAutoZip.Core.Notifications;

namespace NewAutoZip.Core.Configuration;

/// <summary>
/// 各数值项的合法区间。UI 的 NumberBox 与 <see cref="SettingsValidator"/> 共用同一份定义，
/// 避免旧版那种"设计器里 NumericUpDown 默认 0–100、校验器另有一套"的不一致。
/// 旧版四个数值控件全部没设 Minimum/Maximum：扫描间隔填 0 会让工作线程变成 100% CPU 的忙等循环。
/// </summary>
public static class SettingsLimits
{
    public const int BatchWindowMinutesMin = 1;
    public const int BatchWindowMinutesMax = 1440;

    public const int QuietSecondsMin = 5;
    public const int QuietSecondsMax = 3600;

    public const int StableConfirmRoundsMin = 1;
    public const int StableConfirmRoundsMax = 20;

    public const int RefreshIntervalSecondsMin = 1;
    public const int RefreshIntervalSecondsMax = 300;

    /// <summary>
    /// 最小 1 分钟：填 0 会让文件第一次读不到就被放弃，等于关掉了"等它解锁"这件事。
    /// 上限 1440 分钟（一整天）—— 再长就该考虑把这个目录移出监控范围了。
    /// </summary>
    public const int UnreadableGiveUpMinutesMin = 1;
    public const int UnreadableGiveUpMinutesMax = 1440;

    public const int ReconcileIntervalSecondsMin = 30;
    public const int ReconcileIntervalSecondsMax = 86400;

    public const int CompressionLevelMin = 0;
    public const int CompressionLevelMax = 9;

    public const int PackTimeoutMinutesMin = 1;
    public const int PackTimeoutMinutesMax = 1440;

    /// <summary>最小 1 天。旧版允许填 0，而 0 会让清理逻辑直接 return —— 等于永久关闭清理。</summary>
    public const int ZipTempKeepDaysMin = 1;
    public const int ZipTempKeepDaysMax = 365;

    public const int ZipTempMaxCountMin = 1;
    public const int ZipTempMaxCountMax = 10000;

    public const long ZipTempMaxTotalBytesMin = 64L * 1024 * 1024;
    public const long ZipTempMaxTotalBytesMax = 4L * 1024 * 1024 * 1024 * 1024;

    public const long MinFreeDiskBytesMin = 0;
    public const long MinFreeDiskBytesMax = 4L * 1024 * 1024 * 1024 * 1024;

    /// <summary>
    /// 云盘/目标目录的容量预算。上限放到 1 PB —— 云盘是按 TB 卖的，
    /// 沿用 4 TB 那条线会让填了 5 TB 的用户被悄悄夹回去。
    /// 下限必须是 0：0 表示"不限制"，是这个功能的关闭开关。
    /// </summary>
    public const long CloudQuotaBytesMin = 0;
    public const long CloudQuotaBytesMax = 1024L * 1024 * 1024 * 1024 * 1024;

    public const long CloudQuotaWarnBytesMin = 0;
    public const long CloudQuotaWarnBytesMax = 1024L * 1024 * 1024 * 1024 * 1024;

    public const int MaxAttemptsBeforeQuarantineMin = 1;
    public const int MaxAttemptsBeforeQuarantineMax = 100;

    public const int UploadTimeoutMinutesMin = 1;
    public const int UploadTimeoutMinutesMax = 1440;

    public const int UploadPollSecondsMin = 2;
    public const int UploadPollSecondsMax = 600;

    /// <summary>
    /// 云端恢复演练的间隔（小时）。1 小时 – 8760 小时（一年）。
    ///
    /// 不写死是有道理的：这个周期该多长完全取决于备份的价值和带宽成本 ——
    /// 每天出一个包的机器和每月出一个包的机器，合理周期差一个数量级；
    /// 而云端演练要把包重新下载回来，按流量计费的线路上一周一次都嫌频繁。
    /// 下限 1 小时而不是更小：演练要解出一份和源文件等大的副本，
    /// 比这更密集就会一直在跟备份主流程抢磁盘和 IO。
    /// </summary>
    public const int CloudDrillIntervalHoursMin = 1;
    public const int CloudDrillIntervalHoursMax = 8760;

    /// <summary>单次演练的总时限。与打包超时同量级。</summary>
    public const int DrillTimeoutMinutesMin = 1;
    public const int DrillTimeoutMinutesMax = 1440;

    /// <summary>背景图模糊半径。0 = 不模糊；上限 60，再大只是更慢，看上去已经是一片色块。</summary>
    public const int BackgroundBlurMin = 0;
    public const int BackgroundBlurMax = 60;

    /// <summary>
    /// 背景图不透明度（百分比）。下限取 5 而不是 0：
    /// 选了图却一点变化都没有，用户只会以为功能是坏的 —— 不想要背景就清掉路径。
    /// </summary>
    public const int BackgroundOpacityMin = 5;
    public const int BackgroundOpacityMax = 100;

    public const int PasswordRecommendedLength = 8;
}

/// <summary>
/// 全部可配置项。纯 POCO，可直接 JSON 序列化。
/// 密码与 Webhook 在落盘时由 <see cref="SettingsStore"/> 用 DPAPI 加密，内存里保持明文。
/// </summary>
public sealed class AppSettings
{
    // ==================== 路径 ====================

    /// <summary>被监控的目录。</summary>
    public string MonitorPath { get; set; } = string.Empty;

    public bool IncludeSubdirectories { get; set; }

    /// <summary>排除的文件名通配符。默认排掉常见的"写入中"临时文件与压缩包自身。</summary>
    public List<string> ExcludePatterns { get; set; } =
    [
        "*.tmp", "*.temp", "*.part", "*.partial", "*.crdownload", "*.download",
        "~$*", ".~*", "*.7z", "*.lnk",
    ];

    /// <summary>
    /// 云盘目录（压缩包最终落点）。
    ///
    /// JSON 里的键名仍是 <c>OneDrivePath</c> —— 支持非 OneDrive 云盘是后加的，
    /// 改键名会让已经配好路径的 settings.json 静默丢掉这一项，让用户重填一遍。
    /// 属性名按现在的语义取，键名保持向后兼容，两边都不将就。
    /// </summary>
    [JsonPropertyName("OneDrivePath")]
    public string CloudPath { get; set; } = string.Empty;

    /// <summary>
    /// 云盘类型。<see cref="CloudTarget.Folder"/> 时程序把压缩包剪切进目录就算完成，
    /// 不再等待上传确认、不检测客户端状态、消息里也不提 OneDrive。
    /// </summary>
    public CloudTarget CloudTarget { get; set; } = CloudTarget.OneDrive;

    /// <summary>临时压缩目录。留空表示使用 <see cref="AppPaths.DefaultZipTemp"/>。</summary>
    public string ZipTempPath { get; set; } = string.Empty;

    // ==================== 计划 ====================

    /// <summary>生效日期范围（含）。null 表示不限。</summary>
    public DateOnly? EffectiveFrom { get; set; }

    public DateOnly? EffectiveTo { get; set; }

    /// <summary>true = 全天工作，忽略 <see cref="DailyStart"/>/<see cref="DailyEnd"/>。</summary>
    public bool AllDay { get; set; } = true;

    /// <summary>每日开始时刻。允许跨夜（End &lt;= Start 视为跨过午夜）。</summary>
    public TimeOnly DailyStart { get; set; } = new(0, 0);

    public TimeOnly DailyEnd { get; set; } = new(23, 59);

    /// <summary>批处理窗口：第一个新文件出现后，再收集多久才打成一个包。</summary>
    public int BatchWindowMinutes { get; set; } = 5;

    // ==================== 监控 ====================

    /// <summary>文件大小/修改时间保持不变多少秒后，才认为写入结束。</summary>
    public int QuietSeconds { get; set; } = 60;

    /// <summary>静默期满足后，还需要连续几次探测都不变才判定就绪。</summary>
    public int StableConfirmRounds { get; set; } = 2;

    /// <summary>对已跟踪文件重新探测的间隔。</summary>
    public int RefreshIntervalSeconds { get; set; } = 5;

    /// <summary>
    /// 一个<b>连一个字节都读不到</b>的文件最多等多久（分钟），到点后本轮不再等它。
    ///
    /// 只有写入方用独占方式打开文件（连共享读都不给）才会进入这个计时。
    /// 绝大多数数据库备份、日志写入允许共享读，那些文件走的是
    /// <see cref="QuietSeconds"/> 那条"还在变就继续等"的路，与这个值无关，等多久都行。
    ///
    /// 到点之后文件<b>不会</b>被跳过：它会被记进状态里的待重新处理名单，
    /// 下一次对账扫描重新纳入观察。所以这个值只影响"多久告警一次、多久重来一遍"，
    /// 不影响"会不会丢"。
    ///
    /// 计时只累计程序<b>真正在探测</b>的时间：工作时段之外、以及打包/上传占住主循环的那段
    /// 不计入，否则关一夜机再开工，所有锁着的文件会在第一次探测时集体"到点"。
    /// </summary>
    public int UnreadableGiveUpMinutes { get; set; } = 20;

    /// <summary>兜底全目录对账扫描间隔（FileSystemWatcher 丢事件时的补救）。</summary>
    public int ReconcileIntervalSeconds { get; set; } = 300;

    /// <summary>首次运行时是否处理目录里已有的旧文件。默认 false，只处理新增。</summary>
    public bool ProcessExistingFilesOnFirstRun { get; set; }

    // ==================== 打包 ====================

    public string Password { get; set; } = string.Empty;

    /// <summary>7-Zip -mx 等级，0=仅存储，9=极限。</summary>
    public int CompressionLevel { get; set; } = 5;

    /// <summary>-mhe=on，同时加密文件名。</summary>
    public bool EncryptFileNames { get; set; } = true;

    public int PackTimeoutMinutes { get; set; } = 180;

    public string ArchivePrefix { get; set; } = "Backup";

    // ==================== 恢复演练与清单 ====================

    /// <summary>
    /// 为每个压缩包产出清单：明文旁挂的 <c>.manifest.json</c>（只有校验元数据，<b>不含文件名</b>）
    /// + 打进包内、随 AES-256 加密的逐文件明细。见 <see cref="Packing.ArchiveManifest"/>。
    /// </summary>
    public bool WriteArchiveManifest { get; set; } = true;

    /// <summary>
    /// 打包成功后，就地对刚产出的包做一次<b>完整恢复演练</b>（真解开、逐条核对），
    /// 通过了才投递。默认开。
    ///
    /// 与打包内部那次 <c>7za t</c> 不是一回事：那一次只解码不落盘，
    /// 而且用的是内存里刚拿来打包的密码。这一次解到磁盘上，
    /// 并且用<b>从 settings.json 重新读出来的密码</b> —— 验的就是"存下来的那个还好不好用"。
    ///
    /// 代价是要临时占用与源文件相当的磁盘空间，并让本轮多花一段时间。
    /// 空间不够时会自动跳过并告警，绝不会把磁盘写满。
    /// </summary>
    public bool VerifyAfterPackByExtract { get; set; } = true;

    /// <summary>
    /// 定期抽一个<b>云端</b>的包做恢复演练。默认<b>关</b>——
    /// OneDrive 上的包多半已经脱水，读它会触发重新下载，产生真实流量。
    /// 想验"传上去之后有没有坏"就打开它。
    /// </summary>
    public bool CloudDrillEnabled { get; set; }

    /// <summary>云端演练的间隔（小时）。默认 168 = 7 天。用户可改，见 <see cref="SettingsLimits"/>。</summary>
    public int CloudDrillIntervalHours { get; set; } = 168;

    /// <summary>单次演练的总时限（分钟）。</summary>
    public int DrillTimeoutMinutes { get; set; } = 60;

    // ==================== 配额与磁盘 ====================

    public int ZipTempKeepDays { get; set; } = 3;

    /// <summary>ZipTemp 里最多保留几个归档。旧版没有数量上限。</summary>
    public int ZipTempMaxCount { get; set; } = 20;

    /// <summary>ZipTemp 总容量上限。旧版没有容量上限。</summary>
    public long ZipTempMaxTotalBytes { get; set; } = 5L * 1024 * 1024 * 1024;

    /// <summary>打包前要求卷上至少剩余这么多空间，否则拒绝打包并告警。</summary>
    public long MinFreeDiskBytes { get; set; } = 10L * 1024 * 1024 * 1024;

    // ==================== 云盘容量预算 ====================
    //
    // OneDrive 的真实配额读不到 —— 那个数字从不落盘，OneDrive.exe 用自己的令牌
    // 问服务端并只留在内存里（注册表、settings 数据库、按字节对齐搜都落空过）。
    // 所以这里让用户自己填，程序按目录里实际存在的包动态算已用量。
    //
    // 两个默认值都是 0，这是新增字段遇上老 settings.json 的唯一安全默认：
    // 反序列化拿到 0 就等于"没启用"，老用户升级后行为完全不变。

    /// <summary>
    /// 云盘/目标目录的总容量预算。<b>0 = 不限制</b>，整套容量检查直接跳过。
    ///
    /// 存字节而不是"数值 + 单位"两个字段：单位只是界面上的显示方式，
    /// 存进来两个字段会让每个读它的地方都要先做一次换算，迟早有人漏掉。
    /// </summary>
    public long CloudQuotaBytes { get; set; }

    /// <summary>
    /// 剩余容量低于这个数就告警。<b>0 = 不告警</b>。
    ///
    /// 与 <see cref="CloudQuotaBytes"/> 分开的理由：一个是"总共有多少"，
    /// 一个是"剩多少就该管了"。后者通常远小于前者，合成一个百分比反而难填 ——
    /// 1 TB 的 5% 是多少，没人愿意在设置界面上心算。
    /// </summary>
    public long CloudQuotaWarnBytes { get; set; }

    // ==================== 失败处理 ====================

    /// <summary>同一个文件参与失败批次达到该次数后进入隔离区，不再重试。</summary>
    public int MaxAttemptsBeforeQuarantine { get; set; } = 6;

    // ==================== OneDrive ====================

    /// <summary>上传完成后请求 OneDrive 释放本地空间（脱水为"仅联机"）。</summary>
    public bool ReleaseLocalSpace { get; set; } = true;

    public int UploadTimeoutMinutes { get; set; } = 120;

    public int UploadPollSeconds { get; set; } = 15;

    // ==================== 通知 ====================

    /// <summary>显式选择渠道，不再靠 URL 里含不含 "weixin" 猜。</summary>
    public NotifierKind NotifyChannel { get; set; } = NotifierKind.None;

    public string WebhookUrl { get; set; } = string.Empty;

    public string TelegramBotToken { get; set; } = string.Empty;

    public string TelegramChatId { get; set; } = string.Empty;

    public NotifyEvent EnabledEvents { get; set; } = NotifyEvent.All;

    // ==================== 界面与运行 ====================

    public bool StartMinimized { get; set; }

    /// <summary>程序启动后自动开始监控（无人值守场景）。</summary>
    public bool AutoStartEngine { get; set; }

    public LogLevel LogLevel { get; set; } = LogLevel.Info;

    /// <summary>深色 / 浅色 / 跟随系统。默认跟随系统，这样装完不用先去设置里挑一遍。</summary>
    public ThemeMode Theme { get; set; } = ThemeMode.System;

    /// <summary>
    /// 自定义皮肤的强调色，<c>#RRGGBB</c>。空 = 用 Windows 的系统强调色。
    /// 存的是字符串而不是结构体：这个字段用户可能直接手改 settings.json，
    /// 文本形式最好认也最好改，坏值由 <see cref="AccentColorSpec"/> 兜底。
    /// </summary>
    public string AccentColor { get; set; } = AccentColorSpec.FollowSystem;

    /// <summary>
    /// 自定义背景图的完整路径。空 = 不用背景图，窗口保持 Mica 材质。
    ///
    /// 存的是路径而不是图片本身：settings.json 是纯文本配置，
    /// 往里塞几 MB 的 base64 会让它没法手看也没法手改，
    /// 而且换一张图就要重写整个文件。
    /// </summary>
    public string BackgroundImagePath { get; set; } = string.Empty;

    /// <summary>
    /// 背景图的模糊半径。默认 18 —— 不模糊的照片会和正文抢注意力，字直接看不清。
    /// </summary>
    public int BackgroundBlur { get; set; } = 18;

    /// <summary>背景图的不透明度（百分比）。默认 30，够看出是张图，又不影响读字。</summary>
    public int BackgroundOpacity { get; set; } = 30;

    public AppSettings Clone()
    {
        AppSettings copy = (AppSettings)MemberwiseClone();
        copy.ExcludePatterns = [.. ExcludePatterns];
        return copy;
    }

    /// <summary>把所有数值项夹进合法区间。反序列化被手改过的 settings.json 后必须调用。</summary>
    public void ClampToLimits()
    {
        BatchWindowMinutes = Math.Clamp(BatchWindowMinutes, SettingsLimits.BatchWindowMinutesMin, SettingsLimits.BatchWindowMinutesMax);
        QuietSeconds = Math.Clamp(QuietSeconds, SettingsLimits.QuietSecondsMin, SettingsLimits.QuietSecondsMax);
        StableConfirmRounds = Math.Clamp(StableConfirmRounds, SettingsLimits.StableConfirmRoundsMin, SettingsLimits.StableConfirmRoundsMax);
        RefreshIntervalSeconds = Math.Clamp(RefreshIntervalSeconds, SettingsLimits.RefreshIntervalSecondsMin, SettingsLimits.RefreshIntervalSecondsMax);
        UnreadableGiveUpMinutes = Math.Clamp(UnreadableGiveUpMinutes, SettingsLimits.UnreadableGiveUpMinutesMin, SettingsLimits.UnreadableGiveUpMinutesMax);
        ReconcileIntervalSeconds = Math.Clamp(ReconcileIntervalSeconds, SettingsLimits.ReconcileIntervalSecondsMin, SettingsLimits.ReconcileIntervalSecondsMax);
        CompressionLevel = Math.Clamp(CompressionLevel, SettingsLimits.CompressionLevelMin, SettingsLimits.CompressionLevelMax);
        PackTimeoutMinutes = Math.Clamp(PackTimeoutMinutes, SettingsLimits.PackTimeoutMinutesMin, SettingsLimits.PackTimeoutMinutesMax);
        ZipTempKeepDays = Math.Clamp(ZipTempKeepDays, SettingsLimits.ZipTempKeepDaysMin, SettingsLimits.ZipTempKeepDaysMax);
        ZipTempMaxCount = Math.Clamp(ZipTempMaxCount, SettingsLimits.ZipTempMaxCountMin, SettingsLimits.ZipTempMaxCountMax);
        ZipTempMaxTotalBytes = Math.Clamp(ZipTempMaxTotalBytes, SettingsLimits.ZipTempMaxTotalBytesMin, SettingsLimits.ZipTempMaxTotalBytesMax);
        MinFreeDiskBytes = Math.Clamp(MinFreeDiskBytes, SettingsLimits.MinFreeDiskBytesMin, SettingsLimits.MinFreeDiskBytesMax);
        CloudQuotaBytes = Math.Clamp(CloudQuotaBytes, SettingsLimits.CloudQuotaBytesMin, SettingsLimits.CloudQuotaBytesMax);
        CloudQuotaWarnBytes = Math.Clamp(CloudQuotaWarnBytes, SettingsLimits.CloudQuotaWarnBytesMin, SettingsLimits.CloudQuotaWarnBytesMax);
        MaxAttemptsBeforeQuarantine = Math.Clamp(MaxAttemptsBeforeQuarantine, SettingsLimits.MaxAttemptsBeforeQuarantineMin, SettingsLimits.MaxAttemptsBeforeQuarantineMax);
        UploadTimeoutMinutes = Math.Clamp(UploadTimeoutMinutes, SettingsLimits.UploadTimeoutMinutesMin, SettingsLimits.UploadTimeoutMinutesMax);
        UploadPollSeconds = Math.Clamp(UploadPollSeconds, SettingsLimits.UploadPollSecondsMin, SettingsLimits.UploadPollSecondsMax);
        CloudDrillIntervalHours = Math.Clamp(CloudDrillIntervalHours, SettingsLimits.CloudDrillIntervalHoursMin, SettingsLimits.CloudDrillIntervalHoursMax);
        DrillTimeoutMinutes = Math.Clamp(DrillTimeoutMinutes, SettingsLimits.DrillTimeoutMinutesMin, SettingsLimits.DrillTimeoutMinutesMax);

        if (string.IsNullOrWhiteSpace(ArchivePrefix))
        {
            ArchivePrefix = "Backup";
        }

        // 主题相关的两个字段同样要过一遍：settings.json 是可以手改的纯文本，
        // 一个拼错的颜色值不该让界面起不来，更不该让程序崩在启动路径上。
        if (!Enum.IsDefined(Theme))
        {
            Theme = ThemeMode.System;
        }

        AccentColor = AccentColorSpec.Sanitize(AccentColor);

        BackgroundImagePath = (BackgroundImagePath ?? string.Empty).Trim();
        BackgroundBlur = Math.Clamp(BackgroundBlur, SettingsLimits.BackgroundBlurMin, SettingsLimits.BackgroundBlurMax);
        BackgroundOpacity = Math.Clamp(BackgroundOpacity, SettingsLimits.BackgroundOpacityMin, SettingsLimits.BackgroundOpacityMax);
    }

    /// <summary>解析出实际使用的 ZipTemp 目录。</summary>
    public string ResolveZipTemp() =>
        string.IsNullOrWhiteSpace(ZipTempPath) ? AppPaths.DefaultZipTemp : Path.GetFullPath(ZipTempPath);
}
