using NewAutoZip.Core.Notifications;
using NewAutoZip.Core.OneDrive;
using NewAutoZip.Core.Scheduling;
using NewAutoZip.Core.Storage;
using NewAutoZip.Core.Watching;

namespace NewAutoZip.Core.Configuration;

public enum IssueSeverity
{
    /// <summary>不阻止启动，但用户应当知道。</summary>
    Warning,

    /// <summary>必须先改掉，否则不允许启动。</summary>
    Error,
}

/// <param name="Severity">严重程度。</param>
/// <param name="Field">对应的配置项名（UI 用它定位到出问题的输入框）。</param>
/// <param name="Message">给人看的说明，包含"为什么"而不只是"不合法"。</param>
public sealed record ValidationIssue(IssueSeverity Severity, string Field, string Message)
{
    public override string ToString() =>
        (Severity == IssueSeverity.Error ? "【错误】" : "【提醒】") + Message;
}

public sealed record ValidationReport(IReadOnlyList<ValidationIssue> Issues)
{
    public static ValidationReport Empty { get; } = new([]);

    public bool CanStart => !Issues.Any(i => i.Severity == IssueSeverity.Error);

    public IEnumerable<ValidationIssue> Errors => Issues.Where(i => i.Severity == IssueSeverity.Error);

    public IEnumerable<ValidationIssue> Warnings => Issues.Where(i => i.Severity == IssueSeverity.Warning);

    public string ToText() => string.Join(Environment.NewLine, Issues.Select(i => i.ToString()));
}

/// <summary>
/// 启动前一次性校验，把全部问题一起列出来。
///
/// 旧版是"每发现一个问题弹一个 MessageBox 然后 return"，用户要点五六次才能把配置改对；
/// 而且根本不校验路径嵌套 —— 那是最危险的一项。
/// </summary>
public static class SettingsValidator
{
    /// <summary>
    /// 明显不该当压缩密码用的串。旧版设计器里硬编码了默认密码 <c>233</c> 并直接发布，
    /// 意味着不改配置就跑的人，"加密"备份的密码是公开的。
    /// </summary>
    private static readonly string[] WeakPasswords =
    [
        "233", "1234", "12345", "123456", "1234567", "12345678", "123456789", "1234567890",
        "password", "passwd", "admin", "admin123", "root", "qwerty", "abc123", "111111",
        "000000", "666666", "888888", "aaaaaa", "test", "test123", "backup", "zip",
    ];

    /// <param name="settings">待校验的配置。</param>
    /// <param name="sevenZipExePath">压缩程序的实际路径。</param>
    /// <param name="dataRoot">
    /// 程序数据目录。传 <c>null</c> 用 <see cref="AppPaths.DataRoot"/>；单元测试传入假目录。
    /// </param>
    public static ValidationReport Validate(AppSettings settings, string sevenZipExePath, string? dataRoot = null)
    {
        List<ValidationIssue> issues = [];

        ValidatePaths(settings, issues);
        ValidateDataRoot(settings, dataRoot ?? AppPaths.DataRoot, issues);
        ValidateOneDriveSync(settings, issues);
        ValidateSchedule(settings, issues);
        ValidatePassword(settings, issues);
        ValidateSevenZip(sevenZipExePath, issues);
        ValidateNotifications(settings, issues);
        ValidateQuotaAndDisk(settings, issues);
        ValidateCloudQuota(settings, issues);
        ValidateTiming(settings, issues);
        ValidateDrill(settings, issues);

        return new ValidationReport(issues);
    }

    /// <summary>
    /// 恢复演练相关。
    ///
    /// <b>全部只发提醒，一条都不拦启动。</b>演练是备份之上的一层保障，
    /// 配得不理想顶多是"保障弱一点"，而拦下启动意味着连备份本身都不做了 ——
    /// 那是拿更大的风险去换更小的风险。
    /// </summary>
    private static void ValidateDrill(AppSettings settings, List<ValidationIssue> issues)
    {
        // 两种演练都关掉 = 回到"从没验证过自己产出的包还能不能用"的状态。
        // 这是默认配置下不会出现的（VerifyAfterPackByExtract 默认开），所以说一句是有意义的。
        if (!settings.VerifyAfterPackByExtract && !settings.CloudDrillEnabled)
        {
            issues.Add(new ValidationIssue(IssueSeverity.Warning, nameof(settings.VerifyAfterPackByExtract),
                "两种恢复演练都关着。压缩包会照常产出，但程序不会再验证“存着的密码解不解得开它”——" +
                "密码写坏、改错或 DPAPI 失效，要等你真正需要恢复的那天才会发现。"));
        }

        // 关了清单，逐文件核对就没有账本可比。演练仍能验出"密码打不开 / 包坏了"，
        // 但验不出"内容变了"。
        if (!settings.WriteArchiveManifest && (settings.VerifyAfterPackByExtract || settings.CloudDrillEnabled))
        {
            issues.Add(new ValidationIssue(IssueSeverity.Warning, nameof(settings.WriteArchiveManifest),
                "开了恢复演练但关了归档清单。演练仍能验出“密码打不开”和“包损坏”，" +
                "但没有清单就无法逐文件核对内容是否与打包时一致。"));
        }

        // 云端演练比本地演练贵得多（要把包重新下载回来），周期设得太密只会持续烧流量。
        if (settings.CloudDrillEnabled && settings.CloudDrillIntervalHours < 24)
        {
            issues.Add(new ValidationIssue(IssueSeverity.Warning, nameof(settings.CloudDrillIntervalHours),
                $"云端演练间隔只有 {settings.CloudDrillIntervalHours} 小时。" +
                "云端的包多半已脱水，每次演练都要把它重新下载回本地 —— " +
                "按流量计费的线路上这会是一笔持续开销。建议至少 24 小时。"));
        }
    }

    /// <summary>
    /// 程序数据目录（settings.json / state.json / logs / 默认 ZipTemp 都在这里）与监控目录的关系。
    ///
    /// 数据目录现在默认就是 exe 所在目录，所以"把程序放进被监控的目录里"变成了一个很容易踩的坑：
    /// 日志、状态文件、临时压缩包会被监控器当成新增文件收进批次，压进包里再上传，
    /// 而写日志这个动作本身又会不断制造新的"新增文件" —— 自我喂养，停不下来。
    /// 这一条直接拦下启动。
    /// </summary>
    private static void ValidateDataRoot(AppSettings settings, string dataRoot, List<ValidationIssue> issues)
    {
        if (!string.IsNullOrWhiteSpace(AppPaths.DataRootFallbackReason))
        {
            issues.Add(new ValidationIssue(IssueSeverity.Warning, "DataRoot",
                AppPaths.DataRootFallbackReason!));
        }

        string monitor = settings.MonitorPath;

        if (string.IsNullOrWhiteSpace(monitor) || string.IsNullOrWhiteSpace(dataRoot))
        {
            return;
        }

        if (FileFilter.IsSameOrUnder(dataRoot, monitor))
        {
            issues.Add(new ValidationIssue(IssueSeverity.Error, nameof(settings.MonitorPath),
                $"程序自身的数据目录就在监控目录里面：{dataRoot}。" +
                "日志、状态文件和临时压缩包会被当成新增文件收进批次打包上传，" +
                "而写日志又会立刻制造出新的“新增文件”，循环停不下来。" +
                "请把程序移到监控目录之外，或改成监控别的目录。"));
        }
    }

    private static void ValidatePaths(AppSettings settings, List<ValidationIssue> issues)
    {
        string monitor = settings.MonitorPath;
        string cloud = settings.CloudPath;
        string zipTemp = settings.ResolveZipTemp();
        string cloudLabel = CloudTargetText.PathLabel(settings.CloudTarget);

        if (string.IsNullOrWhiteSpace(monitor))
        {
            issues.Add(new ValidationIssue(IssueSeverity.Error, nameof(settings.MonitorPath), "未设置监控目录。"));
        }
        else if (!Directory.Exists(monitor))
        {
            issues.Add(new ValidationIssue(IssueSeverity.Error, nameof(settings.MonitorPath),
                $"监控目录不存在：{monitor}"));
        }

        if (string.IsNullOrWhiteSpace(cloud))
        {
            issues.Add(new ValidationIssue(IssueSeverity.Error, nameof(settings.CloudPath), $"未设置{cloudLabel}。"));
        }
        else if (!Directory.Exists(cloud))
        {
            issues.Add(new ValidationIssue(IssueSeverity.Error, nameof(settings.CloudPath),
                $"{cloudLabel}不存在：{cloud}"));
        }

        if (!CanWriteTo(zipTemp, out string? zipTempError))
        {
            issues.Add(new ValidationIssue(IssueSeverity.Error, nameof(settings.ZipTempPath),
                $"临时目录不可写：{zipTemp}（{zipTempError}）"));
        }

        if (!string.IsNullOrWhiteSpace(cloud) && Directory.Exists(cloud)
            && !CanWriteTo(cloud, out string? cloudError))
        {
            issues.Add(new ValidationIssue(IssueSeverity.Error, nameof(settings.CloudPath),
                $"{cloudLabel}不可写：{cloud}（{cloudError}）"));
        }

        // ========== 路径嵌套：最危险的一项 ==========
        //
        // 若目标目录或 ZipTemp 位于监控目录内，生成的 .7z 会被监控器当成新增文件
        // 再次纳入下一个批次，打进新的 .7z，如此自我喂养式指数膨胀，几轮就能填满磁盘。
        // 默认排除规则里有 "*.7z" 挡了一层，但用户一旦改掉它就彻底失守，因此这里直接拦下。

        if (string.IsNullOrWhiteSpace(monitor))
        {
            return;
        }

        if (FileFilter.IsSameOrUnder(cloud, monitor))
        {
            issues.Add(new ValidationIssue(IssueSeverity.Error, nameof(settings.CloudPath),
                $"{cloudLabel}位于监控目录之内。生成的压缩包会被当作新文件再次打包，" +
                "形成自我喂养的循环，几轮就能填满磁盘。请把两者分开。"));
        }

        if (FileFilter.IsSameOrUnder(zipTemp, monitor))
        {
            issues.Add(new ValidationIssue(IssueSeverity.Error, nameof(settings.ZipTempPath),
                "临时目录位于监控目录之内。正在写入的压缩包会被自己监控到并再次打包。请把它移到监控目录之外。"));
        }

        if (FileFilter.IsSameOrUnder(monitor, cloud))
        {
            issues.Add(new ValidationIssue(IssueSeverity.Warning, nameof(settings.MonitorPath),
                settings.CloudTarget == CloudTarget.OneDrive
                    ? "监控目录位于 OneDrive 目录之内。源文件本身已经在同步，再压缩上传一份属于重复占用空间；" +
                      "另外 OneDrive 脱水后的源文件会触发回传下载。请确认这是你想要的。"
                    : $"监控目录位于{cloudLabel}之内。源文件本身已经在同步，再压缩一份放回同一棵目录树" +
                      "属于重复占用空间。请确认这是你想要的。"));
        }

        if (FileFilter.IsSameOrUnder(zipTemp, cloud))
        {
            issues.Add(new ValidationIssue(IssueSeverity.Warning, nameof(settings.ZipTempPath),
                $"临时目录位于{cloudLabel}之内。半成品 .part 文件会被同步客户端反复上传，" +
                "浪费带宽也拖慢压缩。建议改到本地非同步目录。"));
        }
    }

    /// <summary>
    /// 目标目录到底在不在 OneDrive 的同步范围内。
    ///
    /// 这一条旧版完全没有：只要目录存在就照样打包、照样移进去，然后死等两小时超时，
    /// 最后仍然发出"上传完成"的通知。用户可能几个月后才发现云端一个包都没有。
    /// 判定用三条独立证据（详见 OneDriveScope）：按需文件状态位、OneDrive 同步根标记文件、
    /// 已登录账号的本地目录。任一条成立即算在范围内 —— 同步根之下任意层的子目录都会命中根目录。
    ///
    /// <see cref="CloudTarget.Folder"/> 模式下整条检查直接跳过：那时程序压根不等上传确认，
    /// 目录在不在 OneDrive 范围内与它无关，硬提一句只会让用户以为配错了。
    /// </summary>
    private static void ValidateOneDriveSync(AppSettings settings, List<ValidationIssue> issues)
    {
        if (settings.CloudTarget != CloudTarget.OneDrive)
        {
            return;
        }

        string oneDrive = settings.CloudPath;

        if (string.IsNullOrWhiteSpace(oneDrive) || !Directory.Exists(oneDrive))
        {
            return;     // 路径本身的问题已由 ValidatePaths 报过，不重复
        }

        SyncScope scope = OneDriveScope.Resolve(oneDrive);

        if (scope.InScope)
        {
            return;     // 包括同步根之下任意层的子目录
        }

        // 只给提醒不拦启动：程序仍然会正确打包并移入目录，只是无法确认上传、无法释放空间。
        // 而且有的用户就是想先攒在本地、自己另作处理。
        string extra = CloudFileState.CloudApiAvailable
            ? "向上逐层找不到同步根目录（既没有按需文件标记位，也没有 OneDrive 的同步根标记文件，" +
              "也不在已登录账号的本地目录之下）。请确认 OneDrive 已登录，且这个目录真的在它的同步范围内。"
            : "本系统不支持按需文件（Cloud Files，需要 Windows 10 1709 及以上），无法做这项判断。";

        issues.Add(new ValidationIssue(IssueSeverity.Warning, nameof(settings.CloudPath),
            $"这个目录看起来不在 OneDrive 的同步范围内：{oneDrive}。{extra} " +
            "压缩包仍会被移进去，但程序无法确认云端已收到" +
            (settings.ReleaseLocalSpace ? "，也无法释放本地空间" : string.Empty) +
            $"，每个包都要等满 {settings.UploadTimeoutMinutes} 分钟才会按“未能确认”记账。" +
            $"若这个目录本来就不是 OneDrive，请把云盘类型改成“{CloudTargetText.Describe(CloudTarget.Folder)}”，" +
            "程序移入目录后即视为完成，不再等待。"));
    }

    private static void ValidateSchedule(AppSettings settings, List<ValidationIssue> issues)
    {
        if (settings.EffectiveFrom is { } from && settings.EffectiveTo is { } to && from > to)
        {
            issues.Add(new ValidationIssue(IssueSeverity.Error, nameof(settings.EffectiveFrom),
                $"生效起始日期（{from:yyyy-MM-dd}）晚于结束日期（{to:yyyy-MM-dd}）。"));
        }

        ScheduleWindow window = ScheduleWindow.FromSettings(settings);
        DateTime now = DateTime.Now;

        if (window.IsExpired(now))
        {
            issues.Add(new ValidationIssue(IssueSeverity.Warning, nameof(settings.EffectiveTo),
                $"生效日期范围已经结束（{settings.EffectiveTo:yyyy-MM-dd}），启动后不会处理任何文件。"));
        }

        if (!settings.AllDay && settings.DailyStart == settings.DailyEnd)
        {
            issues.Add(new ValidationIssue(IssueSeverity.Warning, nameof(settings.DailyStart),
                "每日时段的开始与结束时刻相同，将按“全天”处理。"));
        }

        if (!settings.AllDay && settings.DailyEnd < settings.DailyStart)
        {
            issues.Add(new ValidationIssue(IssueSeverity.Warning, nameof(settings.DailyEnd),
                $"每日时段 {settings.DailyStart:HH\\:mm} → {settings.DailyEnd:HH\\:mm} 跨过午夜，" +
                "将按“当天开始时刻起、到次日结束时刻止”处理。"));
        }
    }

    private static void ValidatePassword(AppSettings settings, List<ValidationIssue> issues)
    {
        string password = settings.Password;

        if (string.IsNullOrEmpty(password))
        {
            issues.Add(new ValidationIssue(IssueSeverity.Error, nameof(settings.Password),
                "压缩密码为空。备份将不加密上传到云端。"));
            return;
        }

        if (string.IsNullOrWhiteSpace(password))
        {
            // 7-Zip 本身接受全空白密码，但这几乎总是误操作（在输入框里按了几下空格），
            // 而且用户之后极难重现出一模一样的空白串来解压。当成空密码处理。
            issues.Add(new ValidationIssue(IssueSeverity.Error, nameof(settings.Password),
                $"压缩密码只有 {password.Length} 个空白字符。这通常是误输入，" +
                "而且日后你几乎不可能敲出完全一致的空白串来解压。请填一个真正的密码。"));
            return;
        }

        if (WeakPasswords.Contains(password, StringComparer.OrdinalIgnoreCase))
        {
            issues.Add(new ValidationIssue(IssueSeverity.Error, nameof(settings.Password),
                $"密码「{new string('*', password.Length)}」在常见弱密码名单里，等同于不加密。请换一个。"));
        }
        else if (password.Length < SettingsLimits.PasswordRecommendedLength)
        {
            issues.Add(new ValidationIssue(IssueSeverity.Warning, nameof(settings.Password),
                $"密码只有 {password.Length} 位，建议至少 {SettingsLimits.PasswordRecommendedLength} 位。"));
        }

        if (password.Any(char.IsControl))
        {
            issues.Add(new ValidationIssue(IssueSeverity.Error, nameof(settings.Password),
                "密码含控制字符，无法作为命令行参数传给 7-Zip。"));
        }

        if (password.Any(c => c > 0x7F))
        {
            issues.Add(new ValidationIssue(IssueSeverity.Warning, nameof(settings.Password),
                "密码含非 ASCII 字符。7-Zip 能正确处理，但在别的机器上用不同工具解压时可能因编码差异对不上，" +
                "建议只用 ASCII 可见字符。"));
        }

        if (password.Contains('"'))
        {
            issues.Add(new ValidationIssue(IssueSeverity.Warning, nameof(settings.Password),
                "密码含英文双引号。程序已按 Windows 规则转义，但换用其他解压工具时容易出错，建议避开。"));
        }
    }

    private static void ValidateSevenZip(string sevenZipExePath, List<ValidationIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(sevenZipExePath) || !File.Exists(sevenZipExePath))
        {
            // 旧版找不到 7z.exe 时 Run() 直接 return，但 UI 仍然显示"● 运行中" —— 界面在撒谎。
            issues.Add(new ValidationIssue(IssueSeverity.Error, "SevenZip",
                $"找不到压缩程序：{sevenZipExePath}。" +
                @"正常情况下它应随程序发布在 tools\7za.exe，请检查安装是否完整。"));
        }
    }

    private static void ValidateNotifications(AppSettings settings, List<ValidationIssue> issues)
    {
        switch (settings.NotifyChannel)
        {
            case NotifierKind.None:
                issues.Add(new ValidationIssue(IssueSeverity.Warning, nameof(settings.NotifyChannel),
                    "未选择通知渠道。打包失败或进入隔离区时你不会收到任何提醒。"));
                break;

            case NotifierKind.Telegram:
                if (string.IsNullOrWhiteSpace(settings.TelegramBotToken))
                {
                    issues.Add(new ValidationIssue(IssueSeverity.Error, nameof(settings.TelegramBotToken),
                        "选择了 Telegram 通知，但未填 Bot Token。"));
                }

                if (string.IsNullOrWhiteSpace(settings.TelegramChatId))
                {
                    issues.Add(new ValidationIssue(IssueSeverity.Error, nameof(settings.TelegramChatId),
                        "选择了 Telegram 通知，但未填 Chat ID。"));
                }

                break;

            default:
                if (string.IsNullOrWhiteSpace(settings.WebhookUrl))
                {
                    issues.Add(new ValidationIssue(IssueSeverity.Error, nameof(settings.WebhookUrl),
                        "选择了 Webhook 类通知，但未填地址。"));
                }
                else if (!Uri.TryCreate(settings.WebhookUrl, UriKind.Absolute, out Uri? uri)
                         || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                {
                    issues.Add(new ValidationIssue(IssueSeverity.Error, nameof(settings.WebhookUrl),
                        $"Webhook 地址不是合法的 http/https URL：{settings.WebhookUrl}"));
                }
                else if (uri.Scheme == Uri.UriSchemeHttp)
                {
                    issues.Add(new ValidationIssue(IssueSeverity.Warning, nameof(settings.WebhookUrl),
                        "Webhook 使用明文 http，通知内容（含文件名）会在网络上裸奔。建议改用 https。"));
                }

                break;
        }

        if (settings.NotifyChannel != NotifierKind.None && settings.EnabledEvents == NotifyEvent.None)
        {
            issues.Add(new ValidationIssue(IssueSeverity.Warning, nameof(settings.EnabledEvents),
                "已选通知渠道但没有勾选任何事件，实际不会发出任何通知。"));
        }
    }

    private static void ValidateQuotaAndDisk(AppSettings settings, List<ValidationIssue> issues)
    {
        string zipTemp = settings.ResolveZipTemp();
        DiskSpaceInfo space = DiskSpace.Query(zipTemp);

        if (!space.Known)
        {
            issues.Add(new ValidationIssue(IssueSeverity.Warning, nameof(settings.ZipTempPath),
                $"无法读取临时目录所在卷的剩余空间：{zipTemp}。打包前的磁盘预检将被跳过。"));
            return;
        }

        if (space.FreeBytes <= settings.MinFreeDiskBytes)
        {
            issues.Add(new ValidationIssue(IssueSeverity.Error, nameof(settings.MinFreeDiskBytes),
                $"临时目录所在卷现在只剩 {ByteSize.Format(space.FreeBytes)}，" +
                $"已经不高于设定的最低保留量 {ByteSize.Format(settings.MinFreeDiskBytes)}。" +
                "启动后每一次打包都会被预检拒绝。请清理磁盘或下调该阈值。"));
        }

        if (settings.ZipTempMaxTotalBytes > space.FreeBytes)
        {
            issues.Add(new ValidationIssue(IssueSeverity.Warning, nameof(settings.ZipTempMaxTotalBytes),
                $"临时目录容量上限 {ByteSize.Format(settings.ZipTempMaxTotalBytes)} " +
                $"超过卷当前可用空间 {ByteSize.Format(space.FreeBytes)}，这条配额起不到保护作用。"));
        }

        if (settings.MinFreeDiskBytes == 0)
        {
            issues.Add(new ValidationIssue(IssueSeverity.Warning, nameof(settings.MinFreeDiskBytes),
                "最低保留空间设为 0，等于关闭打包前的磁盘预检。磁盘可能被压缩包填满。"));
        }
    }

    /// <summary>
    /// 云盘/目标目录的容量预算。
    ///
    /// 独立成一个方法而不是并进 <see cref="ValidateQuotaAndDisk"/>：那里在
    /// "读不到临时目录所在卷"时会直接 return，而容量预算跟临时目录所在的卷毫无关系，
    /// 不该被那条早退顺手关掉。
    ///
    /// <b>全部以"用户填了非 0 值"为前提</b>：这两项默认都是 0（= 不启用），
    /// 默认配置下这里一条都不该触发。
    /// </summary>
    private static void ValidateCloudQuota(AppSettings settings, List<ValidationIssue> issues)
    {
        long quota = settings.CloudQuotaBytes;
        long warn = settings.CloudQuotaWarnBytes;

        if (quota <= 0)
        {
            // 只填了警戒线、没填总容量：剩余量无从算起，这条警戒线永远不会触发。
            if (warn > 0)
            {
                issues.Add(new ValidationIssue(IssueSeverity.Warning, nameof(settings.CloudQuotaWarnBytes),
                    $"填了警戒容量 {ByteSize.Format(warn)}，但没有填总容量上限。" +
                    "剩余空间无从计算，这条警戒线不会起作用。"));
            }

            return;
        }

        if (warn >= quota)
        {
            issues.Add(new ValidationIssue(IssueSeverity.Error, nameof(settings.CloudQuotaWarnBytes),
                $"警戒容量 {ByteSize.Format(warn)} 不低于总容量上限 {ByteSize.Format(quota)}。" +
                "这样一来剩余量从一开始就在警戒线之下，每一轮都会收到告警。请把警戒线调小。"));
        }

        // 普通目录才做这一条：OneDrive 的包会脱水，本地卷的大小和云端配额没有关系，
        // 拿本地磁盘去质疑用户填的云盘容量是错的。
        if (settings.CloudTarget != CloudTarget.Folder || string.IsNullOrWhiteSpace(settings.CloudPath))
        {
            return;
        }

        DiskSpaceInfo space = DiskSpace.Query(settings.CloudPath);

        if (space.Known && quota > space.TotalBytes)
        {
            issues.Add(new ValidationIssue(IssueSeverity.Warning, nameof(settings.CloudQuotaBytes),
                $"目标目录容量上限 {ByteSize.Format(quota)} " +
                $"超过该卷的总容量 {ByteSize.Format(space.TotalBytes)}，这条预算起不到保护作用。" +
                "实际能放多少仍以磁盘真实剩余空间为准。"));
        }
    }

    private static void ValidateTiming(AppSettings settings, List<ValidationIssue> issues)
    {
        // 旧版需要 5 × StableSeconds = 300 秒才判定一个文件就绪，而默认窗口正好 5 分钟，
        // 于是窗口关闭时往往一个文件都还没就绪。这里把这种"注定收不到东西"的组合直接指出来。
        int readySeconds = settings.QuietSeconds + (settings.StableConfirmRounds * settings.RefreshIntervalSeconds);
        int windowSeconds = settings.BatchWindowMinutes * 60;

        if (readySeconds >= windowSeconds)
        {
            issues.Add(new ValidationIssue(IssueSeverity.Warning, nameof(settings.QuietSeconds),
                $"判定一个文件“写入完成”约需 {readySeconds} 秒，而批处理窗口只有 {windowSeconds} 秒。" +
                "窗口关闭时可能一个文件都还没就绪，它们会被顺延到下一个窗口 —— 不会丢，但会延迟。" +
                "建议缩短静默期或加长窗口。"));
        }

        if (settings.QuietSeconds < 30)
        {
            issues.Add(new ValidationIssue(IssueSeverity.Warning, nameof(settings.QuietSeconds),
                $"静默期只有 {settings.QuietSeconds} 秒。写入速度慢或有停顿的大文件可能被误判为已完成，" +
                "从而压进一个不完整的副本。"));
        }

        if (settings.ReconcileIntervalSeconds > 3600)
        {
            issues.Add(new ValidationIssue(IssueSeverity.Warning, nameof(settings.ReconcileIntervalSeconds),
                "对账扫描间隔超过 1 小时。系统事件队列溢出时丢掉的文件，最坏情况下要等这么久才会被补回来。"));
        }
    }

    private static bool CanWriteTo(string directory, out string? error)
    {
        error = null;

        if (string.IsNullOrWhiteSpace(directory))
        {
            error = "路径为空";
            return false;
        }

        try
        {
            Directory.CreateDirectory(directory);

            string probe = Path.Combine(directory, $".newautozip-write-test-{Environment.ProcessId}.tmp");
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
