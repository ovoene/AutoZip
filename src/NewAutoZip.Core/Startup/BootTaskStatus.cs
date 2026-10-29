using System.Globalization;

namespace NewAutoZip.Core.Startup;

/// <summary>
/// 从任务计划程序读回来的那条开机自启任务。只是数据，判断在 <see cref="Describe"/> 里。
/// </summary>
/// <param name="QueryError">读状态本身失败时的原因；读成功（含"没注册"）时为 null。</param>
/// <param name="Exists">任务存在。</param>
/// <param name="Enabled">任务没被停用。</param>
/// <param name="Running">任务实例正在运行 —— 也就是后台实例此刻在跑。</param>
/// <param name="Command">动作里登记的程序路径（可能带引号）。</param>
/// <param name="Arguments">动作里登记的参数。</param>
/// <param name="Account">运行账户。</param>
/// <param name="LogonType">登录类型，取值见 <see cref="BootTaskScheduler"/> 里的常量。</param>
/// <param name="LastRunLocal">上次启动的时间；从没跑过时为 null。</param>
/// <param name="LastResult">上次运行的结果码（HRESULT 或进程退出码）。</param>
public sealed record BootTaskStatus(
    string? QueryError,
    bool Exists,
    bool Enabled,
    bool Running,
    string? Command,
    string? Arguments,
    string? Account,
    int LogonType,
    DateTime? LastRunLocal,
    int LastResult)
{
    /// <summary>任务计划程序里的"保存密码"登录类型。只有它拿得到 DPAPI 主密钥。</summary>
    public const int PasswordLogon = 1;

    // ---------- LastTaskResult 里常见的几个值 ----------

    /// <summary>SCHED_S_TASK_RUNNING：正在运行。</summary>
    public const int ResultRunning = 0x00041301;

    /// <summary>SCHED_S_TASK_HAS_NOT_RUN：还没跑过（刚注册、还没开过机）。</summary>
    public const int ResultHasNotRun = 0x00041303;

    /// <summary>SCHED_S_TASK_TERMINATED：被手动结束。</summary>
    public const int ResultTerminated = 0x00041306;

    /// <summary>ERROR_LOGON_FAILURE：用户名或密码不对 —— 绝大多数是用户改过 Windows 密码。</summary>
    public const int ResultLogonFailure = unchecked((int)0x8007052E);

    /// <summary>ERROR_LOGON_TYPE_NOT_GRANTED：账户没有「作为批处理作业登录」的权限。</summary>
    public const int ResultLogonTypeNotGranted = unchecked((int)0x80070569);

    /// <summary>ERROR_ACCOUNT_RESTRICTION：常见原因是账户没设密码（Windows 不允许空密码账户这样登录）。</summary>
    public const int ResultAccountRestriction = unchecked((int)0x8007052F);

    /// <summary>任务不存在。</summary>
    public static BootTaskStatus NotRegistered { get; } =
        new(null, false, false, false, null, null, null, 0, null, 0);

    /// <summary>读状态失败。</summary>
    public static BootTaskStatus Failed(string error) =>
        new(error, false, false, false, null, null, null, 0, null, 0);

    /// <summary>任务在、并且读得到。</summary>
    public bool Registered => QueryError is null && Exists;

    /// <summary>
    /// 把状态说成人话，并判断要不要用户出手。
    /// </summary>
    /// <param name="currentExe">当前这个程序的完整路径，用来认出"程序被挪过位置"。</param>
    /// <param name="exists">文件存在性检查。测试替换它，免得依赖真实磁盘。</param>
    public BootTaskAdvice Describe(string? currentExe, Func<string, bool>? exists = null)
    {
        exists ??= File.Exists;

        if (QueryError is { } error)
        {
            return new BootTaskAdvice(
                $"读不到开机自启任务的状态：{error}",
                NeedsAttention: true);
        }

        if (!Exists)
        {
            return new BootTaskAdvice(
                "未注册。现在只有打开程序时才会备份 —— 开机后如果没人登录、没人打开它，就不会运行。",
                NeedsAttention: false);
        }

        string? registered = BootTaskDefinition.CommandPathOf(Command);

        if (registered is null || !SafeExists(exists, registered))
        {
            return new BootTaskAdvice(
                $"开机自启任务指向的程序已经不存在：{registered ?? "（空）"}。" +
                "程序多半被挪走或删掉了 —— 点「重新注册」改成当前位置。",
                NeedsAttention: true);
        }

        if (currentExe is not null && !BootTaskDefinition.SameExecutable(registered, currentExe))
        {
            return new BootTaskAdvice(
                $"开机自启任务指向的是另一个位置的程序：{registered}，" +
                $"而当前这个程序在 {currentExe}。开机时运行的会是那一份 —— " +
                "如果程序是挪过来的，点「重新注册」改成当前位置。",
                NeedsAttention: true);
        }

        if (!BootTaskDefinition.HasBackgroundArgument(Arguments))
        {
            return new BootTaskAdvice(
                $"开机自启任务的启动参数被改过（现在是「{Arguments}」），开机时会弹出界面而不是在后台运行。点「重新注册」恢复。",
                NeedsAttention: true);
        }

        if (!Enabled)
        {
            return new BootTaskAdvice(
                "开机自启任务被停用了（多半是在「任务计划程序」里手动停用的），开机不会运行。点「重新注册」恢复。",
                NeedsAttention: true);
        }

        if (LogonType != PasswordLogon)
        {
            return new BootTaskAdvice(
                "开机自启任务没有按「保存密码」方式登记，开机后解不开配置里加密保存的压缩密码。点「重新注册」。",
                NeedsAttention: true);
        }

        if (LastResult == ResultLogonFailure)
        {
            return new BootTaskAdvice(
                $"上次开机启动失败：Windows 拒绝了保存的密码（{LastRunText()}）。" +
                "多半是之后改过 Windows 密码 —— 点「重新注册」填一次新密码。",
                NeedsAttention: true);
        }

        if (LastResult == ResultLogonTypeNotGranted)
        {
            return new BootTaskAdvice(
                $"上次开机启动失败：账户 {Account} 没有「作为批处理作业登录」的权限（{LastRunText()}），" +
                "通常是被组策略收走了。请让管理员在本地安全策略里加回这项权限。",
                NeedsAttention: true);
        }

        string running = Running
            ? "后台实例正在运行。"
            : "后台实例现在没在运行（打开界面时由界面接管；退出界面后会交还给后台）。";

        return new BootTaskAdvice(
            $"已注册：开机后无需登录，以 {Account} 的身份在后台运行并自动开始备份；" +
            $"每 {BootTaskDefinition.WatchdogInterval.TotalMinutes:0} 分钟检查一次后台实例是否还在。" +
            $"{running}{LastRunSentence()}",
            NeedsAttention: false);
    }

    /// <summary>把结果码说成人话。</summary>
    public static string DescribeResult(int result) => result switch
    {
        0 => "正常",
        ResultRunning => "正在运行",
        ResultHasNotRun => "还没运行过",
        ResultTerminated => "被手动结束",
        ResultLogonFailure => "登录失败：密码不对",
        ResultLogonTypeNotGranted => "登录失败：没有批处理登录权限",
        ResultAccountRestriction => "登录失败：账户受限（可能没设密码）",
        _ => string.Create(CultureInfo.InvariantCulture, $"返回码 0x{result:X8}"),
    };

    private string LastRunText() =>
        LastRunLocal is { } at
            ? $"{at.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}，{DescribeResult(LastResult)}"
            : DescribeResult(LastResult);

    private string LastRunSentence() =>
        LastRunLocal is null
            ? "下次开机起生效。"
            : $"上次启动：{LastRunText()}。";

    private static bool SafeExists(Func<string, bool> exists, string path)
    {
        try
        {
            return exists(path);
        }
        catch (Exception)
        {
            return false;
        }
    }
}

/// <summary>设置页上显示的那一句状态，以及它要不要标成警告色。</summary>
public sealed record BootTaskAdvice(string Text, bool NeedsAttention);
