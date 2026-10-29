using System.Security.Principal;
using System.Xml;
using System.Xml.Linq;

namespace NewAutoZip.Core.Startup;

/// <summary>
/// 「开机自启（无需登录）」那条计划任务长什么样。只有纯函数，不碰任务计划程序本身 ——
/// 那一半在 <see cref="BootTaskScheduler"/>。拆开是为了能直接测这份定义。
///
/// <b>为什么是计划任务而不是 Windows 服务</b>
/// 服务跑在 LocalSystem 之类的服务账户下，而这个程序的秘密（压缩密码、Webhook）
/// 是按<b>当前用户</b>的 DPAPI 加密的，换成服务账户就解不开；OneDrive 目录、
/// %LOCALAPPDATA% 也都是那个用户的。计划任务可以"以这个用户的身份、不需要他登录桌面"
/// 运行（登录类型 = 保存密码），开机触发，效果和数据库服务一样，而身份、权限、
/// 数据目录与界面版完全相同 —— 两边看到的是同一份 settings.json 和 state.json。
///
/// <b>代价</b>：任务计划程序必须保存这个账户的 Windows 密码（由系统加密保管，
/// 本程序不经手存储）。改了 Windows 密码以后任务会登录失败，要重新注册一次。
/// "不保存密码"（S4U）那种登录方式拿不到 DPAPI 主密钥，秘密解不开，所以不用。
/// </summary>
public static class BootTaskDefinition
{
    /// <summary>
    /// 任务名，放在任务计划程序库的根目录下。
    ///
    /// 名字固定、不随安装位置变：程序被挪了位置，新位置上的程序照样能找到这条任务，
    /// 发现它指向旧路径，再一键改过来 —— 这正是「重新注册」按钮存在的意义。
    /// 按目录派生名字的话，挪过的程序永远找不到旧任务，旧任务就一直指着一个不存在的 exe。
    /// 纯 ASCII：用户可能要在命令行里 <c>schtasks /query /tn</c> 它。
    /// </summary>
    public const string TaskName = "AutoZip-BootStart";

    /// <summary>任务启动程序时带的参数：无界面、只跑引擎。</summary>
    public const string BackgroundArgument = "--background";

    /// <summary>
    /// 开机后等多久再启动。给磁盘挂载、网络与 OneDrive 所在卷一点时间 ——
    /// 开机第一秒就去找监控目录，外接盘或刚上线的卷还不在。
    /// </summary>
    public static readonly TimeSpan BootDelay = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 看门狗间隔：触发器每隔这么久再拉一次程序。
    ///
    /// 已经有实例在跑时，新拉起来的那个拿不到单实例锁，立刻安静退出（几十毫秒）；
    /// 没有实例在跑时它就接上。于是这三种情况都不用另外处理：
    /// 后台实例崩了、用户打开界面接管之后又注销了、开机那一刻启动失败了。
    /// </summary>
    public static readonly TimeSpan WatchdogInterval = TimeSpan.FromMinutes(5);

    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    /// <summary>
    /// 生成任务 XML（任务计划程序 1.3 架构，Win7 起都认）。
    /// </summary>
    /// <param name="exePath">程序完整路径。</param>
    /// <param name="account">以谁的身份运行，形如 <c>机器名\用户名</c>。</param>
    public static string BuildXml(string exePath, string account)
    {
        string fullPath = Path.GetFullPath(exePath);
        string workingDirectory = Path.GetDirectoryName(fullPath) ?? string.Empty;

        XElement task = new(
            Ns + "Task",
            new XAttribute("version", "1.3"),
            new XElement(
                Ns + "RegistrationInfo",
                new XElement(Ns + "Author", "AutoZip"),
                new XElement(
                    Ns + "Description",
                    "AutoZip 开机自启：开机后无需任何人登录，即以下面这个账户在后台运行备份引擎（无界面）。" +
                    "由 AutoZip 设置页的「开机自启」登记，在那里重新注册或取消。")),
            new XElement(
                Ns + "Triggers",
                new XElement(
                    Ns + "BootTrigger",
                    new XElement(Ns + "Enabled", "true"),
                    new XElement(
                        Ns + "Repetition",
                        new XElement(Ns + "Interval", XmlConvert.ToString(WatchdogInterval)),
                        new XElement(Ns + "StopAtDurationEnd", "false")),
                    new XElement(Ns + "Delay", XmlConvert.ToString(BootDelay)))),
            new XElement(
                Ns + "Principals",
                new XElement(
                    Ns + "Principal",
                    new XAttribute("id", "Author"),
                    new XElement(Ns + "UserId", account),
                    new XElement(Ns + "LogonType", "Password"),

                    // 不提权。程序本身是 asInvoker，界面版也从不提权运行；
                    // 后台版若以完整管理员令牌跑，它建的单实例锁与事件对象界面版就打不开了。
                    new XElement(Ns + "RunLevel", "LeastPrivilege"))),
            new XElement(
                Ns + "Settings",

                // 已有实例在跑（正常情况下一直在跑）时，看门狗那次触发直接跳过，不叠第二个。
                new XElement(Ns + "MultipleInstancesPolicy", "IgnoreNew"),
                new XElement(Ns + "DisallowStartIfOnBatteries", "false"),
                new XElement(Ns + "StopIfGoingOnBatteries", "false"),
                new XElement(Ns + "AllowHardTerminate", "true"),
                new XElement(Ns + "StartWhenAvailable", "true"),
                new XElement(Ns + "RunOnlyIfNetworkAvailable", "false"),
                new XElement(
                    Ns + "IdleSettings",
                    new XElement(Ns + "StopOnIdleEnd", "false"),
                    new XElement(Ns + "RestartOnIdle", "false")),
                new XElement(Ns + "AllowStartOnDemand", "true"),
                new XElement(Ns + "Enabled", "true"),
                new XElement(Ns + "Hidden", "false"),
                new XElement(Ns + "RunOnlyIfIdle", "false"),
                new XElement(Ns + "WakeToRun", "false"),

                // 默认值是 72 小时 —— 不改的话，后台实例跑满三天就会被任务计划程序杀掉。
                // PT0S = 不限时。
                new XElement(Ns + "ExecutionTimeLimit", "PT0S"),

                // 默认优先级 7 会同时把 I/O 与内存优先级压到"低"，7za 打大包会明显变慢。
                // 5 = 普通，和用户双击启动时一样。
                new XElement(Ns + "Priority", "5"),
                new XElement(
                    Ns + "RestartOnFailure",
                    new XElement(Ns + "Interval", "PT1M"),
                    new XElement(Ns + "Count", "3"))),
            new XElement(
                Ns + "Actions",
                new XAttribute("Context", "Author"),
                new XElement(
                    Ns + "Exec",

                    // 加引号：路径里有空格时，不加引号就是经典的"未加引号的服务路径"——
                    // 系统会先去试 C:\Program.exe。
                    new XElement(Ns + "Command", Quote(fullPath)),
                    new XElement(Ns + "Arguments", BackgroundArgument),
                    new XElement(Ns + "WorkingDirectory", workingDirectory))));

        // 不带 XML 声明：交给 COM 的是一段 UTF-16 字符串，声明里的编码写什么都是多余的。
        return task.ToString();
    }

    /// <summary>
    /// 任务的安全描述符：管理员与 SYSTEM 完全控制（与系统默认一致），
    /// 再加一条 —— 被登记的那个用户可以<b>读取和运行</b>它。
    ///
    /// 没有这一条，未提权的界面版连任务在不在都查不到（根目录默认只给管理员与 SYSTEM），
    /// 设置页就没法显示状态，退出界面时也没法立刻把后台实例拉起来。
    /// 只给读与运行、不给写：能改任务定义就能把它改成以最高权限运行别的程序。
    /// </summary>
    public static string SecurityDescriptorFor(string userSid)
    {
        // 解析一遍：既校验格式，也规范成标准写法，绝不把调用方给的字符串原样拼进 SDDL。
        string sid = new SecurityIdentifier(userSid).Value;

        return $"D:(A;;FA;;;BA)(A;;FA;;;SY)(A;;FRFX;;;{sid})";
    }

    /// <summary>
    /// 任务动作里登记的程序路径，去掉引号与首尾空白。空串或只有引号时返回 null。
    /// </summary>
    public static string? CommandPathOf(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        string trimmed = command.Trim().Trim('"').Trim();

        return trimmed.Length == 0 ? null : trimmed;
    }

    /// <summary>两个路径是否指向同一个程序。大小写、相对写法、多余的分隔符都不算差别。</summary>
    public static bool SameExecutable(string? a, string? b)
    {
        if (CommandPathOf(a) is not { } left || CommandPathOf(b) is not { } right)
        {
            return false;
        }

        try
        {
            return string.Equals(
                Path.GetFullPath(left),
                Path.GetFullPath(right),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>参数里有没有后台模式的开关。用户在任务计划程序里手改过参数时会对不上。</summary>
    public static bool HasBackgroundArgument(string? arguments) =>
        arguments is not null
        && arguments
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(a => string.Equals(a.Trim('"'), BackgroundArgument, StringComparison.OrdinalIgnoreCase));

    private static string Quote(string path) => $"\"{path}\"";
}
