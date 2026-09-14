namespace NewAutoZip.Core.Notifications;

public enum NotifierKind
{
    None = 0,
    WeCom = 1,
    DingTalk = 2,
    Telegram = 3,
    Webhook = 4,
}

/// <summary>
/// 可订阅的事件类型。
/// 旧版只在"开始 / 打包成功 / 上传成功 / 结束"时通知 —— 打包失败恰恰不发，
/// 最需要告警的场景反而静默。这里把失败、隔离、磁盘告警都列为一等事件。
///
/// 注意区分三组：
/// <list type="bullet">
/// <item><see cref="EngineStarted"/>/<see cref="EngineStopped"/> 是<b>程序</b>的启停；</item>
/// <item><see cref="ScheduleOpened"/>/<see cref="ScheduleClosed"/> 是<b>工作时段</b>的进出
///       —— 程序常驻后台好几天，而每天真正干活只有一段时间，这两条回答的是
///       "它今天到点醒了吗"；</item>
/// <item><see cref="RoundStarted"/>/<see cref="RoundFinished"/> 是<b>一轮处理</b>的起止
///       （发现新文件 → 上传确认完成），耗时也是按这一对算的。</item>
/// </list>
///
/// <b>成员名字不能改。</b>设置里这个枚举是按<i>名字</i>序列化的
/// （<c>"EnabledEvents": "All"</c> 或 <c>"PackCompleted, Failure"</c>），
/// 改名会让已有配置反序列化失败，用户的订阅项静默清零。
/// 所以 <see cref="UploadCompleted"/> 保持原名，含义已经改成
/// "压缩包已剪切到目标目录、正在上传"（界面上的文案随之改了）。
/// 反过来，往 <see cref="All"/> 里加新位是安全的：写着 <c>"All"</c> 的老配置会自动包含新事件。
/// </summary>
[Flags]
public enum NotifyEvent
{
    None = 0,
    EngineStarted = 1 << 0,
    PackCompleted = 1 << 1,

    /// <summary>【上传】压缩包已进入目标目录、开始上传。<b>不是</b>"上传已完成"。</summary>
    UploadCompleted = 1 << 2,
    Failure = 1 << 3,
    Quarantined = 1 << 4,
    DiskWarning = 1 << 5,
    EngineStopped = 1 << 6,
    RoundStarted = 1 << 7,
    RoundFinished = 1 << 8,

    /// <summary>【开工】进入工作时段。</summary>
    ScheduleOpened = 1 << 9,

    /// <summary>【就绪】窗口期内所有文件都稳定了，一个窗口只发一次。</summary>
    FilesStable = 1 << 10,

    /// <summary>【收工】离开工作时段，正文里带下一次开始时间。</summary>
    ScheduleClosed = 1 << 11,

    All = EngineStarted | PackCompleted | UploadCompleted | Failure | Quarantined | DiskWarning
          | EngineStopped | RoundStarted | RoundFinished
          | ScheduleOpened | FilesStable | ScheduleClosed,

    /// <summary>只关心出问题的时候。</summary>
    ProblemsOnly = Failure | Quarantined | DiskWarning,
}

/// <summary>
/// 通知正文里方括号中的那个词。集中定义，免得各处手写出现不一致。
///
/// 只有"发现新文件"那一个是<b>动态</b>的（见 <see cref="Discovered"/>），其余都是固定词。
/// </summary>
public static class NotifyTag
{
    public const string EngineStarted = "启动";
    public const string ScheduleOpened = "开工";

    // 时间线上这里是【第 N 次发现新文件】—— 它带编号，所以是个方法，见下面的 Discovered。

    public const string FilesStable = "就绪";
    public const string Packed = "打包";
    public const string Uploaded = "上传";
    public const string RoundFinished = "结束";

    /// <summary>
    /// 【取消】本轮的源文件在打包前全部消失，什么都没产出。
    ///
    /// 借用 <see cref="NotifyEvent.RoundFinished"/> 那一位：它就是"本轮怎么收尾的"这条消息，
    /// 订阅了【结束】的人要的正是这个答案，而且不必为此新增一个枚举位
    /// （老配置里写死的 <c>"PackCompleted, RoundFinished"</c> 也就自动包含了它）。
    ///
    /// 但<b>用词必须和"结束"不同</b>：正文里一个压缩包都没有，
    /// 扫一眼标签就得能看出这一轮是空手收场，不能和成功那条混成一样。
    /// </summary>
    public const string RoundCancelled = "取消";

    public const string ScheduleClosed = "收工";
    public const string Failure = "失败";

    /// <summary>
    /// 【演练】恢复演练的结论 —— 主要是失败的那些。
    ///
    /// 借用 <see cref="NotifyEvent.Failure"/> 那一位，<b>不新增枚举位</b>：
    /// 一来演练失败本来就属于"出问题了"，订阅了 <see cref="NotifyEvent.ProblemsOnly"/> 的人
    /// 要的正是这条；二来这个枚举按<i>名字</i>序列化，加成员要连着改 <c>All</c>、
    /// 改设置页的勾选项，而收益只是让人多勾一个框。
    ///
    /// 但<b>用词必须和"失败"分开</b>：备份失败是"这次没备份成"，
    /// 演练失败是"以前备份的那些可能恢复不了"——后者要紧得多，也更急，
    /// 混成同一个词会让人按处理前者的习惯去忽略它。
    /// </summary>
    public const string Drill = "演练";

    public const string Quarantined = "隔离";
    public const string Disk = "磁盘";
    public const string EngineStopped = "停止";
    public const string Test = "测试";

    /// <summary>
    /// 【第 N 次发现新文件】——<b>唯一一个带编号的标签</b>。
    ///
    /// <paramref name="ordinal"/> 是"本轮第几次通报"，即本轮已发出的同类通知条数 + 1。
    /// <b>数的是通知条数，不是文件数</b>：同一时刻进来 2 个文件也只发一条通知，编号只 +1——
    /// 上一条是【第 3 次】，这一条就是【第 4 次】，不是【第 5 次】。
    ///
    /// 正文里那份"发现新文件：1. / 2. …"清单是<b>每条通知各自从 1 数</b>的
    /// （见 BackupEngine.DiscoveryLines），只说明这一条带了几个文件，
    /// 与本轮此前发现过多少个无关。所以标签的 N 和正文最后一行的序号<b>互不相干</b>，
    /// 只有"一次通报一个文件"时才碰巧相等。
    ///
    /// 为什么不再固定写"开始"：一轮处理里文件是陆陆续续进来的，每来一批就发一条通知。
    /// 每条都写【开始】，用户手机上就是一串一模一样的"开始"——
    /// 分不清是新文件来了，还是同一个文件在反复播报（真实反馈就是这么来的：
    /// 同一个文件先报 0B 又报 9.85K，两条都写着【开始】）。
    /// 写成第几次，一眼能看出这是同一轮里的第几批。
    /// </summary>
    public static string Discovered(int ordinal) => $"第 {ordinal} 次发现新文件";
}

/// <summary>
/// 一条通知。
///
/// 正文格式是用户指定的三段式，所有事件一律照这个来：
/// <code>
/// 2026-08-24 18:10:47
/// 【打包】
/// 本次共 1 个文件打包成功。
/// 源文件总大小：4.72G
/// …
/// </code>
/// <see cref="Tag"/> 就是方括号里那个词。留空时退回旧的"标题 + 正文"渲染，
/// 单元测试里的简易构造还用得上。
/// </summary>
public sealed record NotifyMessage(NotifyEvent Event, string Title, string Body)
{
    /// <summary>
    /// 方括号里的事件标签：第 N 次发现新文件 / 就绪 / 打包 / 上传 / 结束 / 失败 / 隔离 / 磁盘。
    /// </summary>
    public string Tag { get; init; } = string.Empty;

    /// <summary>事件发生的本地时间，渲染成正文第一行的 <c>yyyy-MM-dd HH:mm:ss</c>。</summary>
    public DateTimeOffset LocalTime { get; init; } = DateTimeOffset.Now;

    /// <summary>
    /// 通知正文第一行的那个时间戳。<b>带年月日。</b>
    ///
    /// 四个渠道（企业微信 / 钉钉 / Telegram / 通用 Webhook）全部取自这里 ——
    /// 纯文本走 <see cref="ToPlainText"/>，JSON 走 <c>time</c> 字段 ——
    /// 所以格式只要在这一处定，改一次四个渠道一起跟上。
    ///
    /// 必须带年月日：通知是发到手机和聊天窗口的，那种场合没有"这条就是刚才"
    /// 这个上下文。早八点发的备份失败，晚上翻到只剩 "08:12:33"，
    /// 根本分不清是今天早上还是昨天早上；跨年、跨月之后更是无从判断。
    ///
    /// 固定用 InvariantCulture：阿拉伯语区的默认数字形状会把 18:10:47 渲染成 ١٨:١٠:٤٧，
    /// 而有些区域的时间分隔符也不是冒号。
    /// </summary>
    public string TimeText =>
        LocalTime.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// 按标签与若干行正文组装一条通知。<c>null</c> 与空白行会被丢掉，
    /// 免得某个可选信息缺失时正文里留出一片空行。
    /// </summary>
    public static NotifyMessage Create(
        NotifyEvent evt,
        string tag,
        DateTimeOffset localTime,
        params string?[] lines)
    {
        string body = string.Join(
            Environment.NewLine,
            lines.Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => l!.TrimEnd()));

        return new NotifyMessage(evt, $"【{tag}】", body)
        {
            Tag = tag,
            LocalTime = localTime,
        };
    }

    /// <summary>纯文本渠道（企业微信 / 钉钉 / Telegram）使用的合并文本。</summary>
    public string ToPlainText()
    {
        if (string.IsNullOrEmpty(Tag))
        {
            return string.IsNullOrEmpty(Body) ? Title : Title + Environment.NewLine + Body;
        }

        string head = TimeText + Environment.NewLine + $"【{Tag}】";

        return string.IsNullOrEmpty(Body) ? head : head + Environment.NewLine + Body;
    }
}

public sealed record NotifyResult(bool Ok, string? Error, int Attempts)
{
    public static NotifyResult Skipped { get; } = new(true, null, 0);

    public static NotifyResult Success(int attempts) => new(true, null, attempts);

    public static NotifyResult Failure(string error, int attempts) => new(false, error, attempts);
}

/// <summary>
/// 通知渠道。
/// 契约的关键一条：<see cref="SendAsync"/> <b>永不抛异常</b>，失败通过返回值表达。
/// 旧版三个渠道都调 EnsureSuccessStatusCode()，而调用点没有 try/catch，
/// 于是"钉钉限流"能直接演变成"每 30 秒生成一个压缩包直到磁盘满"。
/// </summary>
public interface INotifier
{
    string DisplayName { get; }

    Task<NotifyResult> SendAsync(NotifyMessage message, CancellationToken ct);
}
